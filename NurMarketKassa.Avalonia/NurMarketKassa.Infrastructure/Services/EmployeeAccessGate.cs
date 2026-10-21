using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>2026-09-08: гейт для 4 защищённых действий кассы (удаление позиции из корзины,
/// удаление товара со склада, редактирование товара, добавление товара) — по просьбе владельца.
/// Владелец/админ (есть право ViewSettings — тот же признак, что открывает раздел Настройки, где
/// живёт сам редактор кодов) действует без кода; у сотрудника без ViewSettings запрашивается его
/// личный код для конкретного действия. Если для действия НИ У ОДНОГО сотрудника код не задан —
/// гейт не активен (иначе включение этой функции сразу заблокировало бы всех, у кого владелец ещё
/// не успел вписать коды в Настройки → Сотрудники).
/// 2026-10-18, аудит безопасности: (1) подходит код ТОЛЬКО текущего кассира (по id/email вошедшего
/// пользователя), а не любого сотрудника; нет своего кода, а коды у кого-то заданы — действие
/// заблокировано (как и написано на странице «Сотрудники»); код с сайта для корзины — как раньше.
/// (2) Коды хранятся и уходят в облако только хэшем (PBKDF2-SHA256, случайная соль на каждый код):
/// общие настройки компании в облаке читает любой сотрудник.</summary>
public static class EmployeeAccessGate
{
    public const string CartDelete = "cartDelete";
    public const string WarehouseDelete = "warehouseDelete";
    public const string ProductEdit = "productEdit";
    public const string ProductAdd = "productAdd";
    /// <summary>2026-10-18, владелец: приём оплаты долга (касса и программа владельца). В отличие от остальных действий
    /// включается не наличием кодов, а переключателем «нужен код сотрудника» в общих настройках «Оплата долга»
    /// (DebtPaymentSettings) — см. <see cref="IsActiveFor"/>.</summary>
    public const string DebtPayment = "debtPayment";

    private static readonly string[] AllActions = { CartDelete, WarehouseDelete, ProductEdit, ProductAdd, DebtPayment };

    /// <summary>2026-10-18: сколько всего действий с кодом (для «Кодов заполнено: N/…» на странице «Сотрудники»).</summary>
    public static int ActionCount => AllActions.Length;

    /// <summary>2026-10-18: профиль вошедшего пользователя (NurMarketApiClient.UserPayload) — чей код подходит.</summary>
    public static Func<JsonElement>? CurrentUserProvider { get; set; }

    /// <summary>Только что введённый (ещё не захэшированный) код — до ближайшего сохранения настроек.</summary>
    private static string? PlainFor(string action, EmployeeAccessCode e) => action switch
    {
        CartDelete => e.CartDeleteCode,
        WarehouseDelete => e.WarehouseDeleteCode,
        ProductEdit => e.ProductEditCode,
        ProductAdd => e.ProductAddCode,
        DebtPayment => e.DebtPaymentCode,
        _ => null,
    };

    private static void SetPlain(string action, EmployeeAccessCode e, string? value)
    {
        switch (action)
        {
            case CartDelete: e.CartDeleteCode = value; break;
            case WarehouseDelete: e.WarehouseDeleteCode = value; break;
            case ProductEdit: e.ProductEditCode = value; break;
            case ProductAdd: e.ProductAddCode = value; break;
            case DebtPayment: e.DebtPaymentCode = value; break;
        }
    }

    private static string? HashFor(string action, EmployeeAccessCode e) => action switch
    {
        CartDelete => e.CartDeleteCodeHash,
        WarehouseDelete => e.WarehouseDeleteCodeHash,
        ProductEdit => e.ProductEditCodeHash,
        ProductAdd => e.ProductAddCodeHash,
        DebtPayment => e.DebtPaymentCodeHash,
        _ => null,
    };

    /// <summary>2026-10-18, решение владельца: новый личный код — ровно 6 цифр (проверка ввода кода на кассе длину
    /// не ограничивает — прежние короткие коды продолжают подходить).</summary>
    public static bool IsSixDigits(string? code) =>
        code?.Trim() is { Length: 6 } c && c.All(char.IsAsciiDigit);

    /// <summary>2026-10-18: код действия задан до правила «6 цифр» (короче или не из цифр). Набран новый (ещё не
    /// захэшированный) код — пометки уже нет.</summary>
    public static bool IsShortCode(string action, EmployeeAccessCode e) =>
        e.ShortCodes?.Contains(action) == true && string.IsNullOrWhiteSpace(PlainFor(action, e));

    public static bool HasShortCode(EmployeeAccessCode e) => AllActions.Any(a => IsShortCode(a, e) && HasCode(a, e));

    private static void MarkShort(string action, EmployeeAccessCode e, bool isShort)
    {
        var list = e.ShortCodes?.Where(a => a != action).ToList() ?? new List<string>();
        if (isShort)
            list.Add(action);
        e.ShortCodes = list.Count > 0 ? list : null;
    }

    private static void SetHashFromPlain(string action, EmployeeAccessCode e, string plain)
    {
        SetHash(action, e, HashCode(plain));
        MarkShort(action, e, !IsSixDigits(plain));
    }

    private static void SetHash(string action, EmployeeAccessCode e, string? value)
    {
        if (value is null)
            MarkShort(action, e, false);
        switch (action)
        {
            case CartDelete: e.CartDeleteCodeHash = value; break;
            case WarehouseDelete: e.WarehouseDeleteCodeHash = value; break;
            case ProductEdit: e.ProductEditCodeHash = value; break;
            case ProductAdd: e.ProductAddCodeHash = value; break;
            case DebtPayment: e.DebtPaymentCodeHash = value; break;
        }
    }

    /// <summary>2026-10-18: задан ли у сотрудника код на действие (хэш или только что введённый).</summary>
    public static bool HasCode(string action, EmployeeAccessCode e) =>
        !string.IsNullOrWhiteSpace(HashFor(action, e)) || !string.IsNullOrWhiteSpace(PlainFor(action, e));

    /// <summary>2026-10-18: сколько из кодов (<see cref="ActionCount"/>) задано (для списка на странице «Сотрудники»).</summary>
    public static int CountCodes(EmployeeAccessCode e) => AllActions.Count(a => HasCode(a, e));

    /// <summary>2026-10-18: «Убрать код» в карточке сотрудника.</summary>
    public static void ClearCode(string action, EmployeeAccessCode e)
    {
        SetPlain(action, e, null);
        SetHash(action, e, null);
    }

    /// <summary>2026-10-18: новый код из карточки сотрудника (пустой — оставить прежний).</summary>
    public static void SetPendingCode(string action, EmployeeAccessCode e, string? code) =>
        SetPlain(action, e, string.IsNullOrWhiteSpace(code) ? null : code.Trim());

    /// <summary>2026-09-08: для CartDelete на сайте app.nurcrm.kg (Моя компания → Касса) уже
    /// есть настоящее серверное поле "Код на удаление из корзины" (CompanyDto.CartDeleteCode) —
    /// владелец явно попросил, чтобы касса его видела и использовала, а не только локальные
    /// коды из Настройки → Сотрудники. Оба источника работают одновременно: подходит любой —
    /// код с сайта или личный код сотрудника.</summary>
    private static string? ServerCodeFor(string action) =>
        action == CartDelete ? CompanyInfoService.LastCompany?.CartDeleteCode : null;

    /// <summary>true, если для этого действия нужно спрашивать код у текущего аккаунта.</summary>
    public static bool IsActiveFor(string action, IPermissionService? permissions)
    {
        // 2026-10-18, третий круг ревью: для оплаты долга — закрыто при неизвестности (нет службы прав = не владелец/админ);
        // у остальных действий — как было.
        var isOwnerOrAdmin = permissions?.HasPermission(PosPermissions.ViewSettings) ?? action != DebtPayment;
        if (isOwnerOrAdmin)
            return false;

        // 2026-10-18: оплата долга — код нужен, только если владелец включил это в «Оплате долга»; тогда сотрудник без
        // своего кода оплату не примет (как и у остальных действий, когда коды заданы).
        if (action == DebtPayment)
            return DebtPaymentSettings.Current.RequireEmployeeCode;

        if (!string.IsNullOrWhiteSpace(ServerCodeFor(action)))
            return true;

        foreach (var e in ListForCurrentCompany())
            if (HasCode(action, e))
                return true;
        return false;
    }

    /// <summary>Введённый код совпадает либо с кодом с сайта (если он для этого действия есть),
    /// либо (2026-10-18) с личным кодом ТЕКУЩЕГО кассира — чужой код больше не подходит.</summary>
    public static bool TryValidate(string action, string? enteredCode) => TryValidate(action, enteredCode, out _);

    /// <summary>2026-10-18, ревью безопасности: проверка и «чей код подошёл» за один расчёт PBKDF2
    /// (раньше TryValidate и DescribeMatch считали хэш дважды). matchedBy — имя сотрудника или
    /// <see cref="CartRemovalLogStore.ConfirmedBySiteCode"/>.</summary>
    public static bool TryValidate(string action, string? enteredCode, out string? matchedBy)
    {
        matchedBy = null;
        if (string.IsNullOrWhiteSpace(enteredCode))
            return false;

        var trimmed = enteredCode.Trim();

        var me = FindCurrentEmployee();
        if (me != null && Matches(action, me, trimmed))
        {
            matchedBy = string.IsNullOrWhiteSpace(me.Name) ? "?" : me.Name.Trim();
            return true;
        }

        var serverCode = ServerCodeFor(action);
        if (!string.IsNullOrWhiteSpace(serverCode) && string.Equals(serverCode!.Trim(), trimmed, StringComparison.Ordinal))
        {
            matchedBy = CartRemovalLogStore.ConfirmedBySiteCode;
            return true;
        }
        return false;
    }

    /// <summary>2026-10-17, этап 4 (журнал «Удалено из чека»): чей код подошёл — имя сотрудника из
    /// Настройки → Сотрудники или <see cref="CartRemovalLogStore.ConfirmedBySiteCode"/> для кода с сайта.
    /// Сам код в журнал не пишется.</summary>
    public static string? DescribeMatch(string action, string? enteredCode) =>
        TryValidate(action, enteredCode, out var matchedBy) ? matchedBy : null;

    private static bool Matches(string action, EmployeeAccessCode e, string trimmed)
    {
        var plain = PlainFor(action, e);
        if (!string.IsNullOrWhiteSpace(plain))
            return string.Equals(plain.Trim(), trimmed, StringComparison.Ordinal);
        return VerifyHash(HashFor(action, e), trimmed);
    }

    /// <summary>2026-10-18, четвёртый круг ревью (оплата долга): у вошедшего сотрудника задан код на это действие.</summary>
    public static bool CurrentEmployeeHasCode(string action) => FindCurrentEmployee() is { } me && HasCode(action, me);

    /// <summary>2026-10-18: карточка вошедшего сотрудника — по id пользователя на сервере, затем по email.</summary>
    private static EmployeeAccessCode? FindCurrentEmployee()
    {
        JsonElement payload;
        try
        {
            payload = CurrentUserProvider?.Invoke() ?? default;
        }
        catch
        {
            return null;
        }

        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        if (user.ValueKind != JsonValueKind.Object)
            return null;

        var id = OfflineAuthSessionStore.TryExtractUserId(payload);
        var email = user.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString()?.Trim() : null;

        var list = ListForCurrentCompany();
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = list.FirstOrDefault(e => string.Equals(e.ServerId?.Trim(), id, StringComparison.OrdinalIgnoreCase));
            if (byId != null)
                return byId;
        }

        return string.IsNullOrWhiteSpace(email)
            ? null
            : list.FirstOrDefault(e => string.Equals(e.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>2026-10-18, 5-й круг (К1): список сотрудников этого ПК привязан к компании. Пока компания известна и не
    /// совпадает с той, для которой список получен (вход в другой магазин на том же ПК), — кодов «нет»: чужие коды не
    /// проверяются. Список без отметки (касса до обновления) работает, пока не придёт облако этой компании.</summary>
    private static List<EmployeeAccessCode> ListForCurrentCompany()
    {
        var prefs = UserPreferences.Instance;
        var listCompany = prefs.EmployeeAccessCodesCompanyId;
        var current = CompanyInfoService.LastCompany?.Id;
        return string.IsNullOrWhiteSpace(listCompany) || string.IsNullOrWhiteSpace(current) || listCompany == current
            ? prefs.EmployeeAccessCodes.ToList()
            : new List<EmployeeAccessCode>();
    }

    /// <summary>2026-10-18, 5-й круг (К1): облако компании получено — если список на ПК от другой компании (или без
    /// отметки, касса до обновления), он чужой: очищается, версия 0, дальше берётся из облака этой компании и
    /// никогда не отправляется в её облако. Вызывать на UI-потоке. true — список очищен.</summary>
    /// <param name="cloudHasCodes">2026-10-18, 6-й круг (К-1): в облаке компании есть поле кодов (новое или старое).
    /// Список БЕЗ отметки (касса до обновления) при пустом облаке не стирается — он этой кассы (коды мог вписать кассир
    /// без права записи общих настроек или отправка не прошла): получает отметку компании и остаётся.</param>
    /// <param name="companyEmployees">2026-10-18, 7-й круг: сотрудники компании с сайта (id, email) или null, если список
    /// получить не удалось. Карточки с id/email, которых в компании нет, — чужие и убираются.</param>
    /// <param name="keptLocalOnly">7-й круг: список без отметки оставлен — пользоваться им можно, но в облако он сам не
    /// уходит (только после правки на странице «Сотрудники»).</param>
    public static bool AdoptCompany(string? companyId, bool cloudHasCodes,
        IReadOnlyCollection<(string? Id, string? Email)>? companyEmployees, out bool keptLocalOnly)
    {
        keptLocalOnly = false;
        if (string.IsNullOrWhiteSpace(companyId))
            return false;
        var prefs = UserPreferences.Instance;
        if (prefs.EmployeeAccessCodesCompanyId == companyId)
            return false;
        var unmarked = string.IsNullOrWhiteSpace(prefs.EmployeeAccessCodesCompanyId);
        var count = prefs.EmployeeAccessCodes.Count;
        if (unmarked && !cloudHasCodes)
        {
            if (companyEmployees is not null)
            {
                var ids = companyEmployees.Select(e => e.Id).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var emails = companyEmployees.Select(e => e.Email).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool Foreign(EmployeeAccessCode e) =>
                    (!string.IsNullOrWhiteSpace(e.ServerId) || !string.IsNullOrWhiteSpace(e.Email))
                    && !(e.ServerId is { } sid && ids.Contains(sid))
                    && !(e.Email is { } em && emails.Contains(em));
                var foreign = prefs.EmployeeAccessCodes.Count(Foreign);
                if (foreign > 0)
                {
                    prefs.EmployeeAccessCodes = prefs.EmployeeAccessCodes.Where(e => !Foreign(e)).ToList();
                    PosLogger.Log($"Коды сотрудников: в списке без отметки {foreign} карточек не из этой компании (нет среди сотрудников сайта) — убраны.", "WARNING");
                }
            }
            prefs.EmployeeAccessCodesCompanyId = companyId;
            keptLocalOnly = true;
            PosLogger.Log($"Коды сотрудников: список без отметки компании ({prefs.EmployeeAccessCodes.Count} из {count}) оставлен на этой кассе — в облаке этой компании кодов нет; в облако уйдёт после правки в «Сотрудниках».", "INFO");
            return true;
        }
        prefs.EmployeeAccessCodes = new List<EmployeeAccessCode>();
        prefs.EmployeeAccessCodesVersion = 0;
        prefs.EmployeeAccessCodesCompanyId = companyId;
        PosLogger.Log(unmarked
            ? $"Коды сотрудников: список без отметки компании ({count}) заменяется облаком этой компании."
            : $"Коды сотрудников: список на этом ПК от другой компании ({count}) очищен — берётся из облака этой компании.", "INFO");
        return true;
    }

    /// <summary>2026-10-18, 6-й круг (П.3): список на ПК относится к текущей компании (или она ещё неизвестна,
    /// или список без отметки) — его можно показывать на странице «Сотрудники».</summary>
    public static bool ListBelongsToCurrentCompany()
    {
        var listCompany = UserPreferences.Instance.EmployeeAccessCodesCompanyId;
        var current = CompanyInfoService.LastCompany?.Id;
        return string.IsNullOrWhiteSpace(listCompany) || string.IsNullOrWhiteSpace(current) || listCompany == current;
    }

    // ---- 2026-10-18: хэш кодов ----

    private const string HashPrefix = "pbkdf2-sha256";
    private const int HashIterations = 50_000;

    private static string HashCode(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code.Trim()), salt, HashIterations, HashAlgorithmName.SHA256, 32);
        return $"{HashPrefix}${HashIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyHash(string? stored, string code)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != HashPrefix || !int.TryParse(parts[1], out var iterations) || iterations is < 1000 or > 2_000_000)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            // 2026-10-18, ревью: хэш нашего формата — 32 байта, соль не короче 16; иначе недействителен
            // (иначе подложенный в облако «хэш» длиной 1 байт подбирался бы мгновенно).
            if (expected.Length != 32 || salt.Length < 16)
                return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code.Trim()), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>2026-10-18: открытые коды → хэш, пароли сотрудников → стереть. true — что-то изменилось.</summary>
    public static bool SecureStoredCodes(List<EmployeeAccessCode>? list)
    {
        if (list is null)
            return false;
        var changed = false;
        foreach (var e in list.ToList())
        {
            foreach (var action in AllActions)
            {
                var plain = PlainFor(action, e);
                if (plain is null)
                    continue;
                if (!string.IsNullOrWhiteSpace(plain))
                    SetHashFromPlain(action, e, plain);
                SetPlain(action, e, null);
                changed = true;
            }
            if (e.LoginPassword != null)
            {
                e.LoginPassword = null;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>2026-10-18, ревью безопасности: копия списка для записи на диск — набираемый (ещё не
    /// захэшированный) код в копии сразу хэшем, пароли не пишутся; живые объекты не трогаем (их правит карточка
    /// сотрудника на UI-потоке). Открытый код никогда не сериализуется.</summary>
    public static List<EmployeeAccessCode> ToDiskCopy(List<EmployeeAccessCode>? list)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var source = list?.ToList() ?? new List<EmployeeAccessCode>();
                var copy = ToCloudSecure(source);
                for (var i = 0; i < copy.Count; i++)
                    foreach (var action in AllActions)
                        if (PlainFor(action, source[i]) is { } plain && !string.IsNullOrWhiteSpace(plain))
                            SetHashFromPlain(action, copy[i], plain);
                return copy;
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
                // список меняется прямо сейчас (UI-поток) — повторяем
            }
        }
    }

    /// <summary>2026-10-18, ревью безопасности: файл настроек другой программы на этом ПК (старая папка NurCrmKassa,
    /// касса ↔ программа владельца) — открытые коды сотрудников превратить в хэш, пароли стереть. Остальное содержимое
    /// файла не трогается; нечего чистить — файл не переписывается.</summary>
    /// <param name="hashCodes">2026-10-18, повторное ревью: false — файл старой программы (NurCrmKassa), которая хэши не
    /// понимает: стираются только пароли, коды остаются как есть.</param>
    public static void ScrubSettingsFile(string path, bool hashCodes)
    {
        try
        {
            if (!File.Exists(path))
                return;
            // 2026-10-18, повторное ревью: время изменения файла возвращается прежним — иначе обмен ботом и ключами ИИ
            // между кассой и программой владельца (AdoptTelegramBotFromOtherApp) принял бы файл за свежий.
            var writtenUtc = File.GetLastWriteTimeUtc(path);
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject;
            var listNode = root?.FirstOrDefault(kv => string.Equals(kv.Key, "EmployeeAccessCodes", StringComparison.OrdinalIgnoreCase)).Value
                as System.Text.Json.Nodes.JsonArray;
            if (listNode is null)
                return;
            var changed = false;
            foreach (var item in listNode.OfType<System.Text.Json.Nodes.JsonObject>())
            {
                foreach (var (plainKey, hashKey) in new[]
                         {
                             ("CartDeleteCode", "CartDeleteCodeHash"), ("WarehouseDeleteCode", "WarehouseDeleteCodeHash"),
                             ("ProductEditCode", "ProductEditCodeHash"), ("ProductAddCode", "ProductAddCodeHash"),
                             ("DebtPaymentCode", "DebtPaymentCodeHash"),
                         })
                {
                    var key = item.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, plainKey, StringComparison.OrdinalIgnoreCase));
                    if (key is null || !hashCodes)
                        continue;
                    var plain = item[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
                    item.Remove(key);
                    changed = true;
                    var hasHash = item.Any(kv => string.Equals(kv.Key, hashKey, StringComparison.OrdinalIgnoreCase) && kv.Value is not null);
                    if (!string.IsNullOrWhiteSpace(plain) && !hasHash)
                    {
                        item[hashKey] = HashCode(plain);
                        if (!IsSixDigits(plain))
                        {
                            // 2026-10-18: пометка «короткий код» — как у SecureStoredCodes
                            var shortList = item["ShortCodes"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
                            if (item["ShortCodes"] is null)
                                item["ShortCodes"] = shortList;
                            shortList.Add(plainKey switch
                            {
                                "CartDeleteCode" => CartDelete,
                                "WarehouseDeleteCode" => WarehouseDelete,
                                "ProductEditCode" => ProductEdit,
                                "DebtPaymentCode" => DebtPayment,
                                _ => ProductAdd,
                            });
                        }
                    }
                }
                var pwKey = item.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, "LoginPassword", StringComparison.OrdinalIgnoreCase));
                if (pwKey is not null)
                {
                    item.Remove(pwKey);
                    changed = true;
                }
            }
            if (!changed)
                return;
            File.WriteAllText(path, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.SetLastWriteTimeUtc(path, writtenUtc);
            PosLogger.Log($"Настройки другой программы ({Path.GetFileName(Path.GetDirectoryName(path))}): {(hashCodes ? "коды сотрудников переведены в хэш, " : "")}пароли сотрудников стёрты.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Очистка кодов в чужом файле настроек не удалась: {ex.GetType().Name}", "WARNING");
        }
    }

    // ---- 2026-10-18: облако (SettingsCloudSync, общие настройки компании) ----

    /// <summary>2026-10-18, ревью: карточка для записи из облака — по id, по email; по имени — только если имя
    /// встречается ровно один раз с обеих сторон и карточка не привязана к другому сотруднику сайта.</summary>
    private static EmployeeAccessCode? FindMatch(IReadOnlyList<EmployeeAccessCode> local, EmployeeAccessCode r, IReadOnlyList<EmployeeAccessCode> remoteAll)
    {
        if (!string.IsNullOrWhiteSpace(r.ServerId)
            && local.FirstOrDefault(l => string.Equals(l.ServerId, r.ServerId, StringComparison.OrdinalIgnoreCase)) is { } byId)
            return byId;
        if (!string.IsNullOrWhiteSpace(r.Email)
            && local.FirstOrDefault(l => string.Equals(l.Email, r.Email, StringComparison.OrdinalIgnoreCase)) is { } byEmail)
            return byEmail;
        var name = (r.Name ?? "").Trim();
        if (name.Length == 0)
            return null;
        var localSame = local.Where(l => SameName(l, name)).ToList();
        if (localSame.Count != 1 || remoteAll.Count(x => SameName(x, name)) != 1)
            return null;
        var candidate = localSame[0];
        return string.IsNullOrWhiteSpace(candidate.ServerId) || string.IsNullOrWhiteSpace(r.ServerId)
               || string.Equals(candidate.ServerId, r.ServerId, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static bool SameName(EmployeeAccessCode e, string name) =>
        string.Equals((e.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Старое поле «EmployeeAccessCodes» (его читают кассы до 1.17.59) — только имена, без кодов и паролей.</summary>
    public static List<EmployeeAccessCode> ToCloudPublic(IEnumerable<EmployeeAccessCode> list) =>
        list.Select(e => new EmployeeAccessCode { Name = e.Name, ServerId = e.ServerId, Email = e.Email }).ToList();

    /// <summary>Новое поле — имена и хэши кодов (без открытых кодов и паролей).</summary>
    public static List<EmployeeAccessCode> ToCloudSecure(IEnumerable<EmployeeAccessCode> list) =>
        list.Select(e => new EmployeeAccessCode
        {
            Name = e.Name,
            ServerId = e.ServerId,
            Email = e.Email,
            CartDeleteCodeHash = e.CartDeleteCodeHash,
            WarehouseDeleteCodeHash = e.WarehouseDeleteCodeHash,
            ProductEditCodeHash = e.ProductEditCodeHash,
            ProductAddCodeHash = e.ProductAddCodeHash,
            DebtPaymentCodeHash = e.DebtPaymentCodeHash,
            ShortCodes = e.ShortCodes?.ToList(),
        }).ToList();

    /// <summary>2026-10-18, повторное ревью: локальная правка списка сотрудников/кодов — версия списка растёт
    /// (облачный список с меньшей версией его больше не перезапишет).</summary>
    public static void MarkCodesChanged()
    {
        // 2026-10-18, 4-й круг (П1): пока общие настройки компании не получены — версию не повышаем, иначе касса без
        // облака (новая, офлайн) затёрла бы своим списком коды других касс. 5-й круг: и у кассира без права записи
        // общих настроек (403) — его версия в облако всё равно не уйдёт.
        if (!SettingsCloudSync.CompanySettingsWritable)
            return;
        var prefs = UserPreferences.Instance;
        prefs.EmployeeAccessCodesVersion = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), prefs.EmployeeAccessCodesVersion + 1);
    }

    /// <summary>Новое поле из облака заменяет список этой кассы (сервер главнее). true — список изменился.
    /// 2026-10-18, ревью: вызывать на UI-потоке; карточки обновляются на месте (открытая карточка сотрудника не
    /// теряет набираемый код). Повторное ревью: версия облачного списка новее локальной — список применяется
    /// целиком, даже пустой (удаление на другой кассе); старее — не применяется, localNewer = true (отправить свой);
    /// версии равны (в т. ч. обе 0 — кассы до версий) — пустой облачный список не затирает непустой локальный.</summary>
    public static bool ApplyCloudSecure(List<EmployeeAccessCode>? remote, long remoteVersion, out bool localNewer)
    {
        localNewer = false;
        if (remote is null)
            return false;
        var prefs = UserPreferences.Instance;
        var localVersion = prefs.EmployeeAccessCodesVersion;
        if (remoteVersion < localVersion)
        {
            localNewer = true;
            return false;
        }
        var local = prefs.EmployeeAccessCodes.ToList();
        if (remoteVersion == localVersion && remote.Count == 0 && local.Count > 0)
            return false;
        prefs.EmployeeAccessCodesVersion = remoteVersion;
        var incoming = ToCloudSecure(remote);
        if (JsonSerializer.Serialize(incoming) == JsonSerializer.Serialize(ToCloudSecure(local)))
            return remoteVersion != localVersion;

        var result = new List<EmployeeAccessCode>();
        foreach (var r in incoming)
        {
            var l = FindMatch(local.Where(x => !result.Contains(x)).ToList(), r, incoming);
            if (l is null)
            {
                result.Add(r);
                continue;
            }
            l.Name = r.Name;
            // 2026-10-18, повторное ревью: пустые id/email из облака не стирают уже известные кассе.
            l.ServerId = r.ServerId ?? l.ServerId;
            l.Email = r.Email ?? l.Email;
            foreach (var action in AllActions)
                if (string.IsNullOrWhiteSpace(PlainFor(action, l)))
                {
                    SetHash(action, l, HashFor(action, r));
                    MarkShort(action, l, IsShortCode(action, r));
                }
            result.Add(l);
        }
        prefs.EmployeeAccessCodes = result;
        return true;
    }

    /// <summary>Старое поле из облака: если в нём открытые коды (записала касса до обновления) — принять их,
    /// сразу захэшировать. hadSecrets — в облаке лежат открытые коды или пароли, поле надо перезаписать.</summary>
    /// <param name="secureMissing">2026-10-18, 7-й круг: нового поля в облаке нет (кассы только 1.17.58) — старое поле
    /// переносится и без кодов, чтобы сотрудники «только имена» не пропали.</param>
    public static bool MergeCloudLegacy(List<EmployeeAccessCode>? remote, bool secureMissing, out bool hadSecrets)
    {
        hadSecrets = false;
        if (remote is null)
            return false;
        hadSecrets = remote.Any(r => !string.IsNullOrWhiteSpace(r.LoginPassword) || AllActions.Any(a => !string.IsNullOrWhiteSpace(PlainFor(a, r))));
        if (!hadSecrets && !secureMissing)
            return false;

        // 2026-10-18, ревью: вызывать на UI-потоке; список меняется копией (страница «Сотрудники» может его обходить).
        // 6-й круг (К-2): переносятся ВСЕ записи старого поля (и без кодов — привязка к сайту), никто не теряется при его
        // перезаписи: нет совпадения по id/email/единственному имени — новая карточка (однофамильцы — разные карточки).
        var list = UserPreferences.Instance.EmployeeAccessCodes.ToList();
        var changed = false;
        foreach (var r in remote)
        {
            var actions = AllActions.Where(a => !string.IsNullOrWhiteSpace(PlainFor(a, r))).ToList();
            var local = FindMatch(list, r, remote);
            if (local == null)
            {
                var name = (r.Name ?? "").Trim();
                if (name.Length > 0 && string.IsNullOrWhiteSpace(r.ServerId) && string.IsNullOrWhiteSpace(r.Email) && list.Any(l => SameName(l, name)))
                    PosLogger.Log($"Коды из старого поля облака: «{name}» — имя не единственное, заведена отдельная карточка.", "WARNING");
                local = new EmployeeAccessCode { Name = r.Name ?? "", ServerId = r.ServerId, Email = r.Email };
                list.Add(local);
                changed = true;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(local.ServerId) && !string.IsNullOrWhiteSpace(r.ServerId))
                {
                    local.ServerId = r.ServerId;
                    changed = true;
                }
                if (string.IsNullOrWhiteSpace(local.Email) && !string.IsNullOrWhiteSpace(r.Email))
                {
                    local.Email = r.Email;
                    changed = true;
                }
            }
            // 2026-10-18, ревью: у карточки уже есть хэш — старый открытый код из облака его не перезаписывает.
            foreach (var action in actions)
            {
                if (!string.IsNullOrWhiteSpace(HashFor(action, local)))
                    continue;
                SetHashFromPlain(action, local, PlainFor(action, r)!);
                changed = true;
            }
        }
        if (changed)
        {
            UserPreferences.Instance.EmployeeAccessCodes = list;
            MarkCodesChanged();
        }
        return changed;
    }
}
