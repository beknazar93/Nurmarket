using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-05, владелец: «при открытии на другом устройстве все установки слетают!!! сделай так, чтобы при открытии на другом
/// устройстве все настройки аккаунта подтягивались». Сервер добавил по ТЗ ч.13 (05.10, 23:41):
/// • личные настройки пользователя — GET/PATCH api/users/app-settings/{app}/;
/// • общие настройки компании — GET/PATCH api/main/app-settings/{app}/ (меняют владелец и администратор, читают все).
/// {app} — nurmarket-kassa или nurmarket-owner. После входа настройки берутся с сервера (сервер главнее); если на сервере ещё
/// пусто — туда уходят настройки этого компьютера (первое устройство заполняет). Каждое сохранение настроек отправляется на
/// сервер через 3 с (только то, что изменилось). Переносится только то, что не зависит от компьютера: порты весов и принтеров,
/// IP весов, масштаб экрана, полноэкранный режим, фильтры поиска остаются на каждом компьютере свои.
///
/// 2026-10-18, ревью безопасности (4-й круг — упрощено до простых правил):
/// П1. Пока настройки области (личные / общие компании) не получены успешно в этом запуске для текущего пользователя —
///     эта область не отправляется вообще (и версия списка кодов сотрудников не повышается, см. EmployeeAccessGate).
/// П2. «Грязные» ключи — изменённые на этой кассе с последнего успешного получения. После получения облако применяется ко
///     всем ключам, кроме грязных; грязные отправляются. Коды сотрудников сливаются только по версии списка.
/// П3. Не получилось — повтор при следующем обновлении компании или сохранении настроек, не чаще раза в 60 с.
/// П4. Смена пользователя (OnUserChanged) — отложенная отправка прежнего отменяется, всё «отправленное» и грязное
///     забывается, ответ сервера для прежнего пользователя отбрасывается. Личные настройки и ключи ИИ уходят в облако
///     только изменёнными на кассе (никогда — «заполнением» пустого облака пользователя настройками этого компьютера).
/// </summary>
public static class SettingsCloudSync
{
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    /// <summary>Личное: язык, тема, вид каталога, привычки кассира, скрытые разделы, ключи ИИ.</summary>
    private static readonly string[] PersonalKeys =
    {
        "Language", "DarkTheme", "AccentTheme", "MainLayoutMode", "CatalogViewMode", "ShowCatalogPhotos", "CatalogTileScalePercent",
        "SingleClickToCart", "ResetManualAddQtyAfterAdd", "DiscountModePercent", "ShowQuickProducts", "QuickCheckoutEnabled",
        "AutoShowTouchKeyboard", "HiddenSections", "OwnerSidebarCollapsed",
        // 2026-10-18: вид меню программы владельца (новое / бургер ☰ / старое) — личная настройка, как скрытые разделы.
        "OwnerMenuStyle",
        // 2026-10-19: вид меню ☰ кассы (старое / новое) — личная настройка.
        "KassaMenuStyle",
        // 2026-10-19: касса, из которой владелец выплачивает зарплату («Зарплата» → «Выплатить»).
        "SalaryPayoutCashboxId",
        // 2026-10-19: id категории расходов «Зарплата» по компаниям (не создавать дубль на другом ПК/после перезапуска).
        "SalaryCategoryIds",
    };

    /// <summary>Ключи ИИ — в личных настройках, но в отдельном поле "secrets" (бэкенд 05.10: «секреты передавать в secrets» —
    /// хранятся отдельно и отдаются только самому пользователю).</summary>
    private static readonly string[] SecretKeys = { "TelegramAiKey", "GroqApiKey", "OpenRouterApiKey" };

    // 2026-10-18: облачные поля кодов сотрудников (см. EmployeeAccessGate): новое — имена и хэши, версия списка,
    // старое (его читают кассы до обновления) — только имена. Открытые коды и пароли в облако не уходят.
    private const string LegacyCodesKey = "EmployeeAccessCodes";
    private const string SecureCodesKey = "EmployeeAccessCodesSecure";
    private const string CodesVersionKey = "EmployeeAccessCodesVersion";

    /// <summary>Общее для магазина: вид чека, бонусы, допродажа, купленные функции, коды доступа, банки, телефон владельца.</summary>
    private static readonly string[] CompanyKeys =
    {
        "ShowStoreName", "ShowAddress", "ShowInn", "ShowReceiptNumber", "ShowDate", "ShowItems", "ShowTotal", "ShowQrCode",
        "StoreName", "StoreAddress", "StoreInn", "LoyaltyEnabled", "UpsellEnabled", "UnlockedPacks", "UnlockedThemeIds",
        "VoiceControlUnlocked", "LanguagePackUnlocked", "WarehouseAnalyticsUnlocked", "PriceTagEditorUnlocked", "LabelEditorUnlocked",
        "BulkPriceTagUnlocked", "AnalyticsExportUnlocked", "TelegramBotUnlocked", "ShiftAnalyticsUnlocked", "StaffTimesheetUnlocked",
        "ScalesUnlocked", SecureCodesKey, CodesVersionKey, LegacyCodesKey, "DynamicPaymentQrEnabled", "VisibleBankNames", "CustomBankNames",
        "DebtPrepaymentChooseMethod", "OwnerPhone",
        // 2026-10-06: штрафы за просрочку проката и долга — одинаковые в чеках всех касс.
        "RentalLatePenaltyPerDay", "DebtLatePenaltyText",
        // 2026-10-06: срок обмена и категории без обмена (магазин одежды) — одинаковые на всех кассах.
        "ExchangeDaysLimit", "NonExchangeableCategories",
    };

    private static readonly string[] AllKeys = PersonalKeys.Concat(SecretKeys).Concat(CompanyKeys).ToArray();

    private static readonly object Sync = new();
    private static Dictionary<string, string> _lastPersonal = new();
    private static Dictionary<string, string> _lastCompany = new();
    private static Dictionary<string, string> _lastSecrets = new();
    private static bool _ready;
    private static bool _applying;
    private static bool _companyForbidden;
    private static CancellationTokenSource? _debounce;

    // 2026-10-18, 4-й круг: состояние упрощённой синхронизации.
    private static bool _personalLoaded;
    private static bool _companyLoaded;
    /// <summary>Номер «пользователя» в этом запуске — растёт при смене; ответы и отправки для прежнего отбрасываются.</summary>
    private static int _generation;
    /// <summary>Грязные ключи (П2) и значения, с которыми их сравнивают.</summary>
    private static readonly HashSet<string> _dirty = new();
    private static Dictionary<string, string>? _baseline;
    private static string? _pulledFor;
    private static string? _pulledUser;
    private static bool _pulledAny;
    private static string? _lastCompanyId;
    /// <summary>2026-10-18, 5-й круг: время последнего сбоя (тики UTC; Interlocked — читается из разных потоков).</summary>
    private static long _lastFailureTicks;
    /// <summary>2026-10-18, 5-й круг: идёт получение — второе одновременное не запускается.</summary>
    private static bool _pulling;
    /// <summary>2026-10-18, 5-й круг: отправки по одной (отложенная и после получения не пересекаются).</summary>
    private static readonly SemaphoreSlim PushGate = new(1, 1);
    private static bool _forcePushCodes;
    /// <summary>2026-10-18, 7-й круг: список без отметки оставлен на кассе — в облако сам не уходит.</summary>
    private static bool _codesKeptLocalOnly;
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(60);

    private static string AppName => AppMode.IsOwner ? "nurmarket-owner" : "nurmarket-kassa";

    private static NurMarketApiClient? Api => ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    private static PropertyInfo? Prop(string name) => typeof(UserPreferences).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    /// <summary>2026-10-18: общие настройки компании получены в этом запуске для текущего пользователя (П1).</summary>
    public static bool CompanySettingsLoaded => _companyLoaded || _codesMergeInProgress;

    /// <summary>2026-10-18, 5-й круг: общие настройки получены и их можно менять (не кассир с запретом 403).</summary>
    public static bool CompanySettingsWritable => CompanySettingsLoaded && !_companyForbidden;

    private static bool RetryPaused =>
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFailureTicks) < RetryPause.Ticks;

    private static void MarkFailure()
    {
        _pulledFor = null;
        Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Идёт слияние кодов из только что полученных общих настроек (версию можно повышать, отправлять — ещё нет).</summary>
    private static volatile bool _codesMergeInProgress;

    private static Dictionary<string, string> Snapshot(IEnumerable<string> keys)
    {
        var prefs = UserPreferences.Instance;
        var map = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            try
            {
                switch (key)
                {
                    case CodesVersionKey:
                        map[key] = JsonSerializer.Serialize(prefs.EmployeeAccessCodesVersion);
                        continue;
                    case SecureCodesKey:
                        map[key] = JsonSerializer.Serialize(EmployeeAccessGate.ToCloudSecure(prefs.EmployeeAccessCodes.ToList()));
                        continue;
                    case LegacyCodesKey:
                        map[key] = JsonSerializer.Serialize(EmployeeAccessGate.ToCloudPublic(prefs.EmployeeAccessCodes.ToList()));
                        continue;
                }
                if (Prop(key) is { CanRead: true } p)
                    map[key] = JsonSerializer.Serialize(p.GetValue(prefs), p.PropertyType);
            }
            catch
            {
                // значение не сериализуется (или список меняется прямо сейчас) — пропускаем
            }
        }
        return map;
    }

    private static string? CurrentUserId()
    {
        try
        {
            return Api is { } api ? OfflineAuthSessionStore.TryExtractUserId(api.UserPayload) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>2026-10-18, П4: вошёл другой пользователь (смена кассира — до переключения на его вход; выход из кассы).
    /// newUserId — id нового пользователя, если известен.</summary>
    public static void OnUserChanged(string? newUserId)
    {
        lock (Sync)
        {
            _generation++;
            // Отложенная отправка прежнего пользователя отменяется: под новым входом она отправила бы его личные
            // настройки новому пользователю. Правки за последние 3 с до смены в облако не попадут.
            _debounce?.Cancel();
            _ready = false;
            _personalLoaded = false;
            _companyLoaded = false;
            _companyForbidden = false;
            _lastPersonal = new();
            _lastCompany = new();
            // Ключи ИИ этого компьютера — «уже отправлены»: новому пользователю они не уходят, пока он их не поменяет.
            _lastSecrets = Snapshot(SecretKeys);
            _dirty.Clear();
            _baseline = Snapshot(AllKeys);
            _pulledFor = null;
            _pulledUser = newUserId;
            _pulledAny = false;
            _lastCompanyId = null;
            _pulling = false;
            Interlocked.Exchange(ref _lastFailureTicks, 0);
            _forcePushCodes = false;
        }
    }

    /// <summary>Один раз на вход в компанию (обновление компании бывает часто — повторно не тянем, чтобы не затереть
    /// только что сделанные и ещё не отправленные правки).</summary>
    public static Task PullOnceAsync(string? companyId)
    {
        // 2026-10-18, П4 (подстраховка): id пользователя появился или изменился — это смена пользователя.
        var userId = CurrentUserId();
        if (!string.IsNullOrWhiteSpace(userId) && _pulledAny && userId != _pulledUser)
        {
            PosLogger.Log("Настройки с сервера: вошёл другой пользователь — получаю заново.", "INFO");
            OnUserChanged(userId);
        }
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(userId))
                _pulledUser = userId;
            if (!string.IsNullOrWhiteSpace(companyId))
                _lastCompanyId = companyId;
            var key = (companyId ?? "") + "|" + AppName;
            // П3: после сбоя — не чаще раза в 60 с; 5-й круг: одно получение за раз.
            if (string.IsNullOrWhiteSpace(companyId) || _pulledFor == key || _pulling || RetryPaused)
                return Task.CompletedTask;
            _pulledFor = key;
            _pulledAny = true;
            _pulling = true;
        }
        return PullAsync(companyId!);
    }

    /// <summary>После входа: настройки с сервера — в эту программу; пусто на сервере — туда уходят настройки этого компьютера.</summary>
    private static async Task PullAsync(string companyId)
    {
        var generation = _generation;
        try
        {
            await PullCoreAsync(companyId, generation).ConfigureAwait(false);
        }
        finally
        {
            lock (Sync)
                if (generation == _generation)
                    _pulling = false;
        }
    }

    private static async Task PullCoreAsync(string companyId, int generation)
    {
        if (Api is not { } api)
        {
            // 2026-10-18, 5-й круг: нет входа — попробовать снова при следующем обновлении компании.
            lock (Sync)
                _pulledFor = null;
            return;
        }
        lock (Sync)
            _baseline ??= Snapshot(AllKeys);

        JsonObject? personal = null;
        JsonObject? company = null;
        var personalOk = false;
        var companyOk = false;
        try
        {
            personal = await GetAsync(api, $"api/users/app-settings/{AppName}/").ConfigureAwait(false);
            personalOk = true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки с сервера: личные не получены ({ex.Message}).", "WARNING");
        }
        try
        {
            company = await GetAsync(api, $"api/main/app-settings/{AppName}/").ConfigureAwait(false);
            companyOk = true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки с сервера: общие настройки компании не получены ({ex.Message}).", "WARNING");
        }

        // П4: пока шёл запрос, сменился пользователь — ответ относится к прежнему, отбрасываем.
        if (generation != _generation)
            return;

        try
        {
            var prefs = UserPreferences.Instance;
            var appliedKeys = new List<string>();
            // 2026-10-18, 5-й круг (С2): при повторе — только ещё не полученная область.
            bool doPersonal, doCompany;
            lock (Sync)
            {
                if (generation != _generation)
                    return;
                doPersonal = personalOk && !_personalLoaded;
                doCompany = companyOk && !_companyLoaded;
                var dirtyAtApply = new HashSet<string>(_dirty);
                _applying = true;
                try
                {
                    if (doPersonal)
                    {
                        appliedKeys.AddRange(Apply(personal, PersonalKeys, dirtyAtApply));
                        appliedKeys.AddRange(Apply(personal?["secrets"] as JsonObject, SecretKeys, dirtyAtApply));
                    }
                    if (doCompany)
                        appliedKeys.AddRange(Apply(company, CompanyKeys, dirtyAtApply));
                    if (appliedKeys.Count > 0)
                        prefs.SaveToDisk();
                }
                finally
                {
                    _applying = false;
                }
                // С1: дальше грязным считается всё, что поменяют после применения облака (в т. ч. пока сливаются коды).
                _baseline = Snapshot(AllKeys);
            }

            // Коды сотрудников — по версии списка, на UI-потоке (П1/П2: без исключений «грязных»).
            if (doCompany && company is not null)
            {
                _codesMergeInProgress = true;
                try
                {
                    if (await ApplyEmployeeCodesAsync(api, company, companyId, generation).ConfigureAwait(false))
                        appliedKeys.Add(LegacyCodesKey);
                }
                finally
                {
                    _codesMergeInProgress = false;
                }
            }

            int dirtyCount;
            lock (Sync)
            {
                if (generation != _generation)
                    return;
                var dirty = _dirty; // С1: текущие грязные, а не копия до ожидания
                dirtyCount = dirty.Count;
                if (doPersonal)
                {
                    var localPersonal = Snapshot(PersonalKeys);
                    // 2026-10-18, П4: личные настройки и ключи ИИ уходят в облако только изменёнными на этой кассе
                    // (грязными) — «заполнить пустое облако пользователя» ими больше нельзя: на общем компьютере это
                    // настройки и ключи другого пользователя.
                    _lastPersonal = localPersonal.Where(kv => !dirty.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                    _lastSecrets = Snapshot(SecretKeys).Where(kv => !dirty.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
                    _personalLoaded = true;
                }
                if (doCompany)
                {
                    _companyLoaded = true;
                    // 2026-10-21: ключа нет на сервере, а здесь он пустой (null, «ещё не настраивали») — это одно и то же, отправлять
                    // нечего. Раньше такой ключ уходил «заполнять пустое облако» значением null, а PATCH с null сервер понимает как
                    // «удалить ключ» — и при каждом запуске владелец снова писал на сервер «main — NonExchangeableCategories», хотя
                    // никто ничего не менял. Изменённый здесь ключ (грязный) по-прежнему уходит (пакет 3, п. 79).
                    _lastCompany = Snapshot(CompanyKeys)
                        .Where(kv => company is not null && (company.ContainsKey(kv.Key) || kv.Value == "null") && !dirty.Contains(kv.Key))
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    if (_forcePushCodes)
                    {
                        _forcePushCodes = false;
                        _lastCompany.Remove(LegacyCodesKey);
                        _lastCompany.Remove(SecureCodesKey);
                        _lastCompany.Remove(CodesVersionKey);
                        PosLogger.Log("Настройки на сервере: список сотрудников этой кассы новее или в облаке открытые коды/пароли — отправляю свой.", "INFO");
                    }
                    // 2026-10-18, 7-й круг (К-1): список кассы до обновления, оставленный при пустом облаке, — «уже
                    // отправлен»: в облако уйдёт только после правки в «Сотрудниках» (версия списка изменится).
                    if (_codesKeptLocalOnly)
                    {
                        _codesKeptLocalOnly = false;
                        var codes = Snapshot(new[] { SecureCodesKey, CodesVersionKey, LegacyCodesKey });
                        foreach (var (k, v) in codes)
                            _lastCompany[k] = v;
                    }
                }
                // Грязные ключи полученных областей теперь уйдут обычной отправкой (их нет в «отправленных»).
                _dirty.RemoveWhere(k => (doPersonal && (PersonalKeys.Contains(k) || SecretKeys.Contains(k))) || (doCompany && CompanyKeys.Contains(k)));
                _ready = _personalLoaded || _companyLoaded;
                if (!_personalLoaded || !_companyLoaded)
                    MarkFailure(); // П3: повтор позже, не чаще раза в 60 с
            }

            // 2026-10-05: ключи ИИ, отправленные раньше открытыми полями, — стереть (теперь они в "secrets").
            // 5-й круг (С2): свой try — сбой этой очистки не отменяет уже применённое.
            var plain = SecretKeys.Where(k => doPersonal && personal is not null && personal.ContainsKey(k)).ToList();
            if (plain.Count > 0)
            {
                try
                {
                    var wipe = new JsonObject();
                    foreach (var k in plain)
                        wipe[k] = null;
                    await api.RequestAsync(new HttpMethod("PATCH"), $"api/users/app-settings/{AppName}/", wipe, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    PosLogger.Log($"Настройки на сервере: ключи ИИ перенесены в secrets ({plain.Count}).", "INFO");
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Настройки на сервере: открытые ключи ИИ не стёрты ({ex.Message}).", "WARNING");
                }
            }
            PosLogger.Log($"Настройки с сервера ({AppName}): личные {(personalOk ? "получены" : "НЕТ")}, общие {(companyOk ? "получены" : "НЕТ")}, " +
                          $"применено {appliedKeys.Count}{(appliedKeys.Count > 0 ? " (" + string.Join(", ", appliedKeys) + ")" : "")}, грязных {dirtyCount}.", "INFO");
            await PushAsync().ConfigureAwait(false);
            if (appliedKeys.Count > 0)
            {
                // Меню (скрытые разделы) и купленные функции — пересобрать сразу, остальное — при следующем открытии окон.
                if (appliedKeys.Contains("HiddenSections"))
                    SectionVisibility.RaiseChanged();
                if (appliedKeys.Contains("UnlockedPacks"))
                    TariffGate.RaisePacksChanged();
                Applied?.Invoke();
            }
        }
        catch (Exception ex)
        {
            lock (Sync)
                if (generation == _generation)
                    MarkFailure();
            PosLogger.Log($"Настройки с сервера: не применены ({ex.Message}) — работают настройки этого компьютера.", "WARNING");
        }
    }

    /// <summary>Настройки пришли с сервера и применены (окна могут обновить вид).</summary>
    public static event Action? Applied;

    /// <summary>2026-10-18, 6-й/7-й круг (К-3): «записи нет» (200 с settings: null или {}) — пустое облако, его можно
    /// заполнять; сбой (сеть, 404, 5xx, таймаут, ответ без поля settings) — исключение. 1.17.58 не отличала: любая ошибка общих
    /// настроек считалась пустым облаком, и касса заливала туда свои значения.</summary>
    private static async Task<JsonObject> GetAsync(NurMarketApiClient api, string path)
    {
        JsonElement data;
        // 2026-10-18, 7-й круг: 404 — тоже сбой (возможно, не тот адрес/сервер), пустое облако — только 200 с settings: null или {}.
        data = await api.RequestAsync(HttpMethod.Get, path, null, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("settings", out var s))
        {
            if (s.ValueKind == JsonValueKind.Object)
                return JsonNode.Parse(s.GetRawText()) as JsonObject ?? new JsonObject();
            if (s.ValueKind == JsonValueKind.Null)
                return new JsonObject();
        }
        throw new InvalidDataException("ответ без поля settings");
    }

    private static List<string> Apply(JsonObject? server, string[] keys, ISet<string> dirty)
    {
        var applied = new List<string>();
        if (server is null)
            return applied;
        var prefs = UserPreferences.Instance;
        foreach (var key in keys)
        {
            // 2026-10-18: коды сотрудников — отдельно, по версии (ApplyEmployeeCodesAsync); грязные ключи — с этой кассы (П2).
            if (key is LegacyCodesKey or SecureCodesKey or CodesVersionKey || dirty.Contains(key))
                continue;
            if (!server.TryGetPropertyValue(key, out var node) || node is null || Prop(key) is not { CanWrite: true } p)
                continue;
            try
            {
                var local = JsonSerializer.Serialize(p.GetValue(prefs), p.PropertyType);
                var remote = node.ToJsonString();
                // Сервер может отдать секрет скрытым («****») — такое значение не применяем.
                if (SecretKeys.Contains(key) && (remote.Contains('*') || remote.Contains('•')))
                    continue;
                // Сервер (JSONB) переставляет поля внутри объектов — сравниваем по смыслу, а не текстом.
                if (local == remote || JsonNode.DeepEquals(JsonNode.Parse(local), node))
                    continue;
                p.SetValue(prefs, JsonSerializer.Deserialize(remote, p.PropertyType));
                applied.Add(key);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Настройки с сервера: «{key}» не применена ({ex.Message}).", "WARNING");
            }
        }
        return applied;
    }

    /// <summary>2026-10-18: коды сотрудников из облака — новое поле по версии списка, затем старое (открытые коды старых
    /// касс сразу в хэш). Выполняется на UI-потоке. true — список этой кассы изменился.</summary>
    private static async Task<bool> ApplyEmployeeCodesAsync(NurMarketApiClient api, JsonObject company, string companyId, int generation)
    {
        List<EmployeeAccessCode>? Read(string key)
        {
            if (!company.TryGetPropertyValue(key, out var node) || node is null)
                return null;
            try
            {
                return JsonSerializer.Deserialize<List<EmployeeAccessCode>>(node.ToJsonString());
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Настройки с сервера: «{key}» не применена ({ex.Message}).", "WARNING");
                return null;
            }
        }

        var secure = Read(SecureCodesKey);
        var legacy = Read(LegacyCodesKey);
        long remoteVersion = 0;
        if (company.TryGetPropertyValue(CodesVersionKey, out var versionNode) && versionNode is JsonValue versionValue)
            versionValue.TryGetValue(out remoteVersion);
        var changed = false;
        var hadSecrets = false;
        var localNewer = false;
        var cloudHasCodes = secure is not null || legacy is not null;
        // 2026-10-18, 7-й круг: список кассы без отметки при пустом облаке — сверить карточки с сотрудниками компании
        // на сайте (чужие id/email убрать). Не получилось — список остаётся как есть (только на этой кассе).
        List<(string? Id, string? Email)>? companyEmployees = null;
        var prefsNow = UserPreferences.Instance;
        if (!cloudHasCodes && string.IsNullOrWhiteSpace(prefsNow.EmployeeAccessCodesCompanyId)
            && prefsNow.EmployeeAccessCodes.Any(e => !string.IsNullOrWhiteSpace(e.ServerId) || !string.IsNullOrWhiteSpace(e.Email)))
        {
            try
            {
                companyEmployees = (await api.GetEmployeesAsync().ConfigureAwait(false))?.Select(e => (e.Id, e.Email)).ToList();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Коды сотрудников: список сотрудников компании для сверки не получен ({ex.Message}).", "WARNING");
            }
        }
        var keptLocalOnly = false;
        await NurMarketKassa.Ui.Shared.UiDispatcherHolder.InvokeAsync(() =>
        {
            // 2026-10-18, 6-й круг (П.2): пока ждали UI-поток, сменился пользователь — ничего не применяем.
            if (generation != _generation)
                return;
            // 2026-10-18, 5-й круг (К1): список на ПК от другой компании — чужой: очищается и никогда не отправляется в
            // облако этой компании. 6-й круг (К-1): список без отметки при пустом облаке остаётся (отмечается компанией).
            changed |= EmployeeAccessGate.AdoptCompany(companyId, cloudHasCodes, companyEmployees, out keptLocalOnly);
            changed |= EmployeeAccessGate.ApplyCloudSecure(secure, remoteVersion, out localNewer);
            changed |= EmployeeAccessGate.MergeCloudLegacy(legacy, secure is null, out hadSecrets);
            if (changed)
            {
                _applying = true;
                try
                {
                    UserPreferences.Instance.SaveToDisk();
                }
                finally
                {
                    _applying = false;
                }
            }
        }).ConfigureAwait(false);
        _forcePushCodes = hadSecrets || localNewer;
        _codesKeptLocalOnly = keptLocalOnly;
        return changed;
    }

    /// <summary>Настройки сохранены на этом компьютере — через 3 с изменения уходят на сервер.</summary>
    public static void OnSaved()
    {
        if (_applying)
            return;
        bool ready;
        lock (Sync)
        {
            // 2026-10-18, П2: какие ключи реально поменялись на этой кассе с последнего получения.
            var now = Snapshot(AllKeys);
            if (_baseline is not null)
                foreach (var (key, json) in now)
                    if (key is not (SecureCodesKey or CodesVersionKey or LegacyCodesKey) // коды — только по версии (П1)
                        && (!_baseline.TryGetValue(key, out var before) || before != json))
                        _dirty.Add(key);
            _baseline = now;
            ready = _ready;
        }

        // 2026-10-18, П3: настройки ещё не получены (сбой) — повторить, не чаще раза в 60 с.
        if ((!_personalLoaded || !_companyLoaded) && _pulledFor is null && _lastCompanyId is { } companyId)
            _ = PullOnceAsync(companyId);

        if (!ready)
            return;
        CancellationTokenSource cts;
        int generation;
        lock (Sync)
        {
            _debounce?.Cancel();
            cts = _debounce = new CancellationTokenSource();
            generation = _generation;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, cts.Token).ConfigureAwait(false);
                if (generation == _generation)
                    await PushAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static async Task PushAsync()
    {
        await PushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await PushCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            PushGate.Release();
        }
    }

    private static async Task PushCoreAsync()
    {
        if (Api is not { } api || !_ready)
            return;
        var generation = _generation;
        // 2026-10-18, П4: и вход сейчас того же пользователя, для которого получены настройки (во время входа нового
        // кассира токен уже его, а смена ещё не объявлена) — иначе отправка пропускается.
        var owner = _pulledUser;
        bool Current() => generation == _generation && (owner is null || CurrentUserId() == owner);

        // П1: каждая область — только если получена в этом запуске для текущего пользователя.
        if (_personalLoaded && Current())
        {
            var personal = Snapshot(PersonalKeys);
            var personalChanged = personal.Where(kv => !_lastPersonal.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
            if (personalChanged.Count > 0 && await PatchAsync(api, $"api/users/app-settings/{AppName}/", personalChanged).ConfigureAwait(false) && Current())
                foreach (var (k, v) in personalChanged)
                    _lastPersonal[k] = v;

            var secrets = Snapshot(SecretKeys);
            var secretsChanged = secrets.Where(kv => !_lastSecrets.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
            if (secretsChanged.Count > 0 && Current())
            {
                var inner = new JsonObject();
                foreach (var (key, json) in secretsChanged)
                    inner[key] = JsonNode.Parse(json);
                try
                {
                    await api.RequestAsync(new HttpMethod("PATCH"), $"api/users/app-settings/{AppName}/", new JsonObject { ["secrets"] = inner }, null,
                        CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                    if (Current())
                        foreach (var (k, v) in secretsChanged)
                            _lastSecrets[k] = v;
                    PosLogger.Log($"Настройки на сервере: ключи ИИ (secrets) — {secretsChanged.Count}.", "INFO");
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Настройки на сервере: ключи ИИ не отправлены ({ex.Message}).", "WARNING");
                }
            }
        }

        if (_companyLoaded && !_companyForbidden && Current())
        {
            var company = Snapshot(CompanyKeys);
            // 2026-10-18, 6-й круг (П.2): список сотрудников другой компании в облако этой компании не уходит.
            if (UserPreferences.Instance.EmployeeAccessCodesCompanyId != _lastCompanyId)
            {
                company.Remove(SecureCodesKey);
                company.Remove(CodesVersionKey);
                company.Remove(LegacyCodesKey);
            }
            var companyChanged = company.Where(kv => !_lastCompany.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
            if (companyChanged.Count > 0 && await PatchAsync(api, $"api/main/app-settings/{AppName}/", companyChanged).ConfigureAwait(false) && Current())
                foreach (var (k, v) in companyChanged)
                    _lastCompany[k] = v;
        }
    }

    private static async Task<bool> PatchAsync(NurMarketApiClient api, string path, List<KeyValuePair<string, string>> changed)
    {
        var settings = new JsonObject();
        foreach (var (key, json) in changed)
            settings[key] = JsonNode.Parse(json);
        try
        {
            // Тело — сами настройки (проверено 05.10: сервер сливает верхний уровень, null удаляет ключ).
            await api.RequestAsync(new HttpMethod("PATCH"), path, settings, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            PosLogger.Log($"Настройки на сервере: {path.Split('/')[1]} — {string.Join(", ", changed.Select(c => c.Key))}.", "INFO");
            return true;
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            // Кассир: общие настройки компании меняют только владелец и администратор.
            // 2026-10-18: запрет на личные не снимает запрет на общие (раньше присваивание сбрасывало флаг).
            if (path.Contains("/main/", StringComparison.Ordinal))
                _companyForbidden = true;
            PosLogger.Log($"Настройки на сервере: нет прав менять {path} — остаются на этом компьютере.", "INFO");
            return false;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки на сервере: не отправлены ({ex.Message}) — отправятся при следующем сохранении.", "WARNING");
            return false;
        }
    }
}
