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
    // 2026-10-26: TelegramAiKeysExtra — дополнительные ключи Gemini (строка JSON, GeminiKeyPool); основной остаётся в TelegramAiKey.
    private static readonly string[] SecretKeys = { "TelegramAiKey", "TelegramAiKeysExtra", "GroqApiKey", "OpenRouterApiKey" };

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
        // 2026-10-26 (ТЗ хозмаг ХМ1): точная сфера магазина (hardware…). На сервер в market_sphere уходит только базовая —
        // точное значение другие кассы и мобилка берут отсюда. null не отправляется (см. фильтр _lastCompany).
        "MarketSphereDetail",
        // 2026-10-27, аудит владельца Б-1: «контроль удалений» и «слепой пересчёт» — правила магазина, а не привычка кассира:
        // владелец меняет их один раз, и они действуют на всех кассах (раньше жили только в профиле того ПК, где поменяли).
        "AskCartRemovalReason", "CartRemovalReasonThreshold", "BlindCashCount",
    };

    /// <summary>2026-10-27, Б-1: новые общие ключи правил (удаления, слепой пересчёт) не «заполняют пустое облако» значением по умолчанию
    /// с этого ПК — на кассах они были местными, и первый вход не должен затереть чужое включённое значение. Уходят, только когда
    /// владелец их изменил.</summary>
    private static readonly HashSet<string> NoAutoFillKeys = new() { "AskCartRemovalReason", "CartRemovalReasonThreshold", "BlindCashCount" };

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

    /// <summary>Личные настройки пользователя (язык, тема, вид меню, ключи ИИ): у кассы и у программы владельца свои.</summary>
    private static string AppName => AppMode.IsOwner ? "nurmarket-owner" : "nurmarket-kassa";

    /// <summary>2026-10-27, аудит владельца Б-1: ОБЩИЕ настройки компании (реквизиты чека, телефон владельца, штраф проката, коды
    /// сотрудников, правила удалений…) — один документ nurmarket-kassa для кассы, программы владельца и старых версий кассы.
    /// Раньше программа владельца писала и читала свой nurmarket-owner, и касса её настроек не видела (а старые кассы читают только
    /// nurmarket-kassa — поэтому общий документ именно этот, а не новый). Менять может только владелец/администратор (403 у кассира).</summary>
    private const string CompanyApp = "nurmarket-kassa";

    /// <summary>Старый документ общих настроек программы владельца — читается один раз для переноса (см. MergeLegacyOwnerCompany).</summary>
    private const string LegacyOwnerApp = "nurmarket-owner";

    /// <summary>Метка в общем документе: перенос из nurmarket-owner уже выполнен (не повторять на других ПК владельца).</summary>
    private const string OwnerMergedMarkerKey = "OwnerSettingsMergedAt";

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
            // 2026-10-29: опрос общих настроек начинается заново для нового пользователя.
            _companyFingerprint = null;
            _companyEtag = null;
            _applyPending = false;
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
        // 2026-10-27, Б-1: перенос общих настроек владельца из старого документа nurmarket-owner в общий nurmarket-kassa.
        var migratedKeys = new HashSet<string>();
        var migrationPending = false;
        // 2026-10-27, ревью А-2: первый запуск владельца с общим документом — его старый локальный список кодов (зеркало старого документа)
        // не должен целиком перезаписать список, который кассы вели в общем документе (см. ApplyEmployeeCodesAsync).
        var ownerFirstRun = false;
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
            var (companyDoc, companyAt) = await GetDocAsync(api, $"api/main/app-settings/{CompanyApp}/").ConfigureAwait(false);
            company = companyDoc;
            companyOk = true;
            // 2026-10-29: отпечаток документа на момент получения — опрос на лету применяет только то, что изменилось после.
            if (generation == _generation)
            {
                _companyFingerprint = Fingerprint(companyDoc.ToJsonString());
                _companyEtag = null;
            }
            if (AppMode.IsOwner && !company.ContainsKey(OwnerMergedMarkerKey))
            {
                ownerFirstRun = true;
                migrationPending = true;
                try
                {
                    var (legacyDoc, legacyAt) = await GetDocAsync(api, $"api/main/app-settings/{LegacyOwnerApp}/").ConfigureAwait(false);
                    // 2026-10-27, ревью А-1/А-2: сначала резервная копия ОБОИХ документов в локальный JSON; не записалась — ничего не
                    // переносим и на сервер ничего не пишем (метки нет, перенос повторится при следующем запуске).
                    var backupPath = TryBackupDocuments(companyId, company, companyAt, legacyDoc, legacyAt);
                    if (backupPath is null)
                    {
                        migrationPending = false;
                        PosLogger.Log("Настройки владельца: резервная копия документов не записана — перенос отложен, на сервер ничего не пишется.", "WARNING");
                    }
                    else
                    {
                        // Сотрудники сайта нужны, чтобы не воскрешать удалённых (см. MergeLegacyEmployeeCodes); не получены — коды не переносятся.
                        List<(string? Id, string? Email)>? siteEmployees = null;
                        try
                        {
                            siteEmployees = (await api.GetEmployeesAsync().ConfigureAwait(false))?.Select(e => (e.Id, e.Email)).ToList();
                        }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"Настройки владельца: список сотрудников сайта не получен ({ex.Message}).", "WARNING");
                        }
                        // Слияние идёт на копии: сбой посреди слияния не оставляет наполовину перенесённый документ.
                        var work = (JsonObject)company.DeepClone();
                        var merged = MergeLegacyOwnerCompany(work, legacyDoc, siteEmployees);
                        company = work;
                        migratedKeys = merged.Keys;
                        // Коды не удалось сверить с сайтом — остальное перенесено, но метку не ставим: коды разберём при следующем запуске.
                        if (merged.CodesInconclusive)
                            migrationPending = false;
                        PosLogger.Log($"Настройки владельца: резервная копия — {Path.GetFileName(backupPath)}.", "INFO");
                    }
                }
                catch (Exception ex)
                {
                    // Старый документ не прочитан — метку не ставим: перенос повторится при следующем запуске.
                    migrationPending = false;
                    PosLogger.Log($"Настройки с сервера: старые общие настройки владельца не получены ({ex.Message}) — перенос позже.", "WARNING");
                }
            }
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
                    if (await ApplyEmployeeCodesAsync(api, company, companyId, generation, ownerFirstRun).ConfigureAwait(false))
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
                        .Where(kv => company is not null && (company.ContainsKey(kv.Key) || kv.Value == "null" || NoAutoFillKeys.Contains(kv.Key)
                                                              // 2026-10-27, ревью А-3: программа владельца не «заполняет пустое облако» своими значениями по умолчанию
                                                              // (кроме кодов сотрудников — у них свои правила): уходит только то, что владелец изменил.
                                                              || (AppMode.IsOwner && kv.Key is not (SecureCodesKey or CodesVersionKey or LegacyCodesKey)))
                                     && !dirty.Contains(kv.Key)
                                     && !migratedKeys.Contains(kv.Key)) // Б-1: перенесённое из старого документа ещё не отправлено в общий
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
            if (migrationPending && generation == _generation && !_companyForbidden)
                await WriteOwnerMergedMarkerAsync(api, migratedKeys).ConfigureAwait(false);
            if (appliedKeys.Count > 0)
            {
                // Меню (скрытые разделы) и купленные функции — пересобрать сразу, остальное — при следующем открытии окон.
                if (appliedKeys.Contains("HiddenSections"))
                    SectionVisibility.RaiseChanged();
                if (appliedKeys.Contains("UnlockedPacks"))
                    TariffGate.RaisePacksChanged();
                if (appliedKeys.Contains("MarketSphereDetail"))
                    MarketSpheres.RaiseChanged(); // 2026-10-26: ХМ1 — точная сфера пришла из облака
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

    // ---------------------------------------------------------------- 2026-10-29: общие настройки компании «на лету»
    // Жалоба «синхронизация кассы с админкой очень медленная», повторная приёмка Н-1: название магазина, реквизиты чека, правила скидок
    // и удалений, коды и права сотрудников, купленные темы и функции доходили до работающей кассы только после перезапуска (облако
    // читалось один раз за вход — PullOnceAsync). Теперь общий документ опрашивается раз в SyncCenter.CompanySettingsInterval (30 с ±15 %):
    // запрос с If-None-Match (304 — ничего не менялось; сервер ETag ещё не отдаёт — сравниваем отпечаток документа и ничего не применяем).
    // Правила безопасности те же, что у первого получения: грязные ключи (изменённые на этой кассе и ещё не отправленные) не затираются;
    // ответ прежнего пользователя отбрасывается (поколение); версия кодов сотрудников — по ApplyEmployeeCodesAsync. Новое: пока на экране окно настроек, применение откладывается (поля страниц читаются при открытии: «Сохранить» с устаревшим
    // полем вернул бы на сервер старое значение поверх правки владельца, а список сотрудников перерисовался бы под руками) — применим в течение
    // 15 с после закрытия окна. Журнал — категория SETTINGS_SYNC.

    /// <summary>Задаёт приложение: true — на экране окно настроек (применение настроек с сервера ждёт его закрытия).</summary>
    public static Func<bool>? EditingProbe { get; set; }

    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static string? _companyEtag;
    private static string? _companyFingerprint;
    private static bool _applyPending;
    private static int _pollStarted;
    private static int _refreshFailures;

    /// <summary>Запустить фоновый опрос общих настроек (один раз за запуск; вызывается после первого получения облака).</summary>
    public static void StartCompanyPolling()
    {
        if (Interlocked.Exchange(ref _pollStarted, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var delay = _applyPending
                        ? TimeSpan.FromSeconds(15)
                        : SyncCenter.Jitter(TimeSpan.FromTicks(SyncCenter.CompanySettingsInterval.Ticks * (1L << Math.Min(_refreshFailures, 3))));
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (SyncCenter.ShouldPause())
                        continue;
                    await RefreshCompanyAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"SETTINGS_SYNC: сбой цикла: {ex.GetType().Name}: {ex.Message}", "SETTINGS_SYNC");
                }
            }
        });
    }

    /// <summary>Крючок для <see cref="SyncCenter.ForceSyncAllAsync"/>: проверить общие настройки сейчас (ETag не используется — «Обновить» перечитывает).</summary>
    public static Task RefreshCompanyNowAsync(CancellationToken ct) => RefreshCompanyAsync(ct, force: true);

    private static string Fingerprint(string raw)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToBase64String(bytes);
    }

    private static async Task RefreshCompanyAsync(CancellationToken ct, bool force = false)
    {
        // Первое получение (PullCoreAsync) ещё не прошло или идёт — это его работа.
        if (!_companyLoaded || _pulling || _codesMergeInProgress || _lastCompanyId is not { } companyId || Api is not { } api)
            return;
        if (!await RefreshGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;
        try
        {
            var generation = _generation;
            var owner = _pulledUser;
            var useEtag = !force && !_applyPending ? _companyEtag : null;
            var (data, status, etag) = await api.GetConditionalAsync($"api/main/app-settings/{CompanyApp}/", null, useEtag, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            _refreshFailures = 0;
            SyncCenter.RecordSuccess("settings");
            if (status == 304)
                return;
            var (company, _) = ParseDoc(data);
            // Отпечаток — по смыслу документа (а не по тексту ответа): сервер может переставлять поля.
            var fingerprint = Fingerprint(company.ToJsonString());
            if (!_applyPending && fingerprint == _companyFingerprint)
            {
                _companyEtag = etag ?? _companyEtag;
                return;
            }
            if (generation != _generation || (owner is not null && CurrentUserId() != owner))
                return;

            // Окно настроек на экране: не перерисовываем под руками и не даём «Сохранить» вернуть старые значения, применим позже.
            if (EditingProbe is { } probe && SafeProbe(probe))
            {
                if (!_applyPending)
                    PosLogger.Log("SETTINGS_SYNC: на сервере изменились общие настройки, но на экране окно настроек — применю после его закрытия.", "SETTINGS_SYNC");
                _applyPending = true;
                return;
            }

            var applied = new List<string>();
            var codesChanged = false;
            lock (Sync)
            {
                if (generation != _generation)
                    return;
                var dirtyNow = new HashSet<string>(_dirty);
                _applying = true;
                try
                {
                    applied.AddRange(Apply(company, CompanyKeys, dirtyNow));
                    if (applied.Count > 0)
                        UserPreferences.Instance.SaveToDisk();
                }
                finally
                {
                    _applying = false;
                }
                // Применённое — «уже отправленное»: иначе следующая отправка вернёт его на сервер как «изменение этой кассы».
                if (applied.Count > 0)
                    foreach (var (k, v) in Snapshot(applied))
                        _lastCompany[k] = v;
                _baseline = Snapshot(AllKeys);
            }

            // Коды сотрудников — по версии списка (на UI-потоке), те же правила, что при входе.
            codesChanged = await ApplyEmployeeCodesAsync(api, company, companyId, generation).ConfigureAwait(false);
            if (generation != _generation)
                return;
            lock (Sync)
            {
                if (codesChanged)
                {
                    foreach (var (k, v) in Snapshot(new[] { SecureCodesKey, CodesVersionKey, LegacyCodesKey }))
                        _lastCompany[k] = v;
                    applied.Add(LegacyCodesKey);
                }
                // Список этой кассы новее облачного — пусть уйдёт обычной отправкой (как при входе).
                if (_forcePushCodes)
                {
                    _forcePushCodes = false;
                    _lastCompany.Remove(LegacyCodesKey);
                    _lastCompany.Remove(SecureCodesKey);
                    _lastCompany.Remove(CodesVersionKey);
                }
                _codesKeptLocalOnly = false;
                _baseline = Snapshot(AllKeys);
            }

            _companyFingerprint = fingerprint;
            _companyEtag = etag;
            _applyPending = false;
            PosLogger.Log($"SETTINGS_SYNC: общие настройки обновлены без перезапуска, применено {applied.Count}" +
                          $"{(applied.Count > 0 ? " (" + string.Join(", ", applied) + ")" : "")}.", "SETTINGS_SYNC");
            if (applied.Count > 0)
            {
                if (applied.Contains("UnlockedPacks"))
                    TariffGate.RaisePacksChanged();
                if (applied.Contains("MarketSphereDetail"))
                    MarketSpheres.RaiseChanged();
                Applied?.Invoke();
            }
            await PushAsync().ConfigureAwait(false); // накопившиеся правки этой кассы, если были
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TimeoutException or InvalidDataException or JsonException)
        {
            // Сервер не ответил / 404 / 429: тихо, следующий такт позже (чем дольше сбой, тем реже — до 8×).
            _refreshFailures = Math.Min(_refreshFailures + 1, 3);
            PosLogger.Log($"SETTINGS_SYNC: опрос не удался ({ex.Message}) — повтор позже.", "SETTINGS_SYNC");
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static bool SafeProbe(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; }
    }

    /// <summary>2026-10-18, 6-й/7-й круг (К-3): «записи нет» (200 с settings: null или {}) — пустое облако, его можно
    /// заполнять; сбой (сеть, 404, 5xx, таймаут, ответ без поля settings) — исключение. 1.17.58 не отличала: любая ошибка общих
    /// настроек считалась пустым облаком, и касса заливала туда свои значения.</summary>
    private static async Task<JsonObject> GetAsync(NurMarketApiClient api, string path) => (await GetDocAsync(api, path).ConfigureAwait(false)).Settings;

    /// <summary>2026-10-27, Б-1: то же, что GetAsync, но с меткой времени документа (updated_at) — нужна для переноса настроек владельца.</summary>
    private static async Task<(JsonObject Settings, DateTimeOffset? UpdatedAt)> GetDocAsync(NurMarketApiClient api, string path)
    {
        JsonElement data;
        // 2026-10-18, 7-й круг: 404 — тоже сбой (возможно, не тот адрес/сервер), пустое облако — только 200 с settings: null или {}.
        data = await api.RequestAsync(HttpMethod.Get, path, null, null, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return ParseDoc(data);
    }

    /// <summary>2026-10-29: разбор ответа документа настроек (выделено из GetDocAsync — то же нужно опросу с ETag).</summary>
    private static (JsonObject Settings, DateTimeOffset? UpdatedAt) ParseDoc(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("settings", out var s))
        {
            DateTimeOffset? at = null;
            if (data.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(u.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                at = parsed;
            if (s.ValueKind == JsonValueKind.Object)
                return (JsonNode.Parse(s.GetRawText()) as JsonObject ?? new JsonObject(), at);
            if (s.ValueKind == JsonValueKind.Null)
                return (new JsonObject(), at);
        }
        throw new InvalidDataException("ответ без поля settings");
    }

    /// <summary>2026-10-27, Б-1; переделано по ревью А-1/А-2: разовый перенос общих настроек из старого документа программы владельца
    /// (nurmarket-owner) в общий (nurmarket-kassa) — слияние ПО КЛЮЧАМ, а не «чей документ новее». Общего снимка «как было при
    /// последней синхронизации» у двух документов нет (владелец и кассы писали в разные документы), поэтому базой служат значения
    /// по умолчанию программы: «изменённым» считается значение, которое отличается от умолчания. Для каждого ключа старого документа:
    /// (1) в общем ключа нет — дописываем; (2) значения равны — ничего; (3) отличаются, а в общем стоит умолчание (касса ключ не
    /// меняла) — берём значение владельца; (4) отличаются, и в общем значение изменено — остаётся значение кассы (имя ключа — в журнал),
    /// старое значение лежит в резервной копии; (5) списки купленного (UnlockedPacks, UnlockedThemeIds) — объединение. Ничего не удаляется,
    /// ключи, которых нет в старом документе, не трогаются. Коды сотрудников — <see cref="MergeLegacyEmployeeCodes"/>. Изменяет переданный
    /// company, возвращает ключи, которые нужно отправить в общий документ. Значения в журнал не пишутся (там коды).</summary>
    private static MigrationResult MergeLegacyOwnerCompany(JsonObject company, JsonObject legacy, List<(string? Id, string? Email)>? siteEmployees)
    {
        var migrated = new HashSet<string>();
        var kept = new List<string>();
        var codeKeys = new[] { SecureCodesKey, CodesVersionKey, LegacyCodesKey };
        var defaults = new UserPreferences();
        foreach (var key in CompanyKeys)
        {
            if (codeKeys.Contains(key) || !legacy.TryGetPropertyValue(key, out var legacyValue) || legacyValue is null)
                continue;
            if (!company.TryGetPropertyValue(key, out var current) || current is null)
            {
                company[key] = legacyValue.DeepClone();
                migrated.Add(key);
                continue;
            }
            if (JsonNode.DeepEquals(current, legacyValue))
                continue;
            if (key is "UnlockedPacks" or "UnlockedThemeIds" && current is JsonArray currentList && legacyValue is JsonArray legacyList)
            {
                var union = new JsonArray();
                foreach (var item in currentList.Concat(legacyList))
                    if (item is not null && !union.Any(u => JsonNode.DeepEquals(u, item)))
                        union.Add(item.DeepClone());
                if (!JsonNode.DeepEquals(union, current))
                {
                    company[key] = union;
                    migrated.Add(key);
                }
                continue;
            }
            if (IsDefault(defaults, key, current))
            {
                company[key] = legacyValue.DeepClone();
                migrated.Add(key);
            }
            else
            {
                kept.Add(key);
            }
        }

        var codesInconclusive = MergeLegacyEmployeeCodes(company, legacy, siteEmployees, migrated);
        PosLogger.Log($"Настройки владельца: перенос из {LegacyOwnerApp} в {CompanyApp} — по ключам, перенесено {migrated.Count}" +
                      $"{(migrated.Count > 0 ? " (" + string.Join(", ", migrated) + ")" : "")}" +
                      $"{(kept.Count > 0 ? "; оставлено значение кассы (разные значения) — " + string.Join(", ", kept) : "")}.", "INFO");
        return new MigrationResult(migrated, codesInconclusive);
    }

    /// <summary>Результат слияния: ключи для отправки в общий документ; CodesInconclusive — коды не удалось сверить с сайтом (метку не ставить).</summary>
    private sealed record MigrationResult(HashSet<string> Keys, bool CodesInconclusive);

    /// <summary>Значение ключа в общем документе равно умолчанию программы (касса его не меняла).</summary>
    private static bool IsDefault(UserPreferences defaults, string key, JsonNode current)
    {
        try
        {
            if (Prop(key) is not { CanRead: true } p)
                return false;
            var def = JsonNode.Parse(JsonSerializer.Serialize(p.GetValue(defaults), p.PropertyType));
            return JsonNode.DeepEquals(def, current);
        }
        catch
        {
            return false; // не смогли сравнить — считаем «изменено»: значение кассы остаётся
        }
    }

    /// <summary>2026-10-27, ревью А-2: слияние списка кодов сотрудников ПО СОТРУДНИКУ (раньше список владельца заменял общий целиком по номеру
    /// версии: кассиры, заведённые на кассах, пропадали, удалённые получали доступ обратно). Правила:
    /// — в общем документе нет списка с хэшами (пусто, или только старое поле кассы 1.17.58, где могут быть открытые коды) — ничего не
    ///   переносим (список владельца остаётся на его ПК и в резервной копии; раздать его можно правкой в «Сотрудниках»);
    /// — версия списка владельца не новее общей — общий список кассы не трогаем (отсутствие сотрудника в нём — либо удаление на кассе,
    ///   либо он кассе не известен: воскрешать нельзя);
    /// — владелец новее: общий список остаётся целиком (сотрудники кассы не пропадают); у совпавшего по ServerId/Email/единственному
    ///   имени сотрудника пустые места хэшей заполняются из списка владельца (хэши кассы не перезаписываются); сотрудник, которого
    ///   нет в общем списке, добавляется только если он подтверждён сайтом (есть в списке сотрудников компании) — удалённые на
    ///   сайте не воскресают; карточки без id и email (введены вручную, проверить нельзя) не добавляются; сайт не ответил — такие не
    ///   добавляются и метка переноса не ставится;
    /// — после изменений версия списка = максимум(время, обе версии + 1). Возвращает true — «часть не удалось сверить с сайтом».</summary>
    private static bool MergeLegacyEmployeeCodes(JsonObject company, JsonObject legacy, List<(string? Id, string? Email)>? siteEmployees, HashSet<string> migrated)
    {
        long VersionOf(JsonObject doc) =>
            doc.TryGetPropertyValue(CodesVersionKey, out var v) && v is JsonValue jv && jv.TryGetValue<long>(out var n) ? n : 0;
        List<EmployeeAccessCode>? ReadList(JsonObject doc, string key)
        {
            if (!doc.TryGetPropertyValue(key, out var node) || node is null)
                return null;
            try
            {
                return JsonSerializer.Deserialize<List<EmployeeAccessCode>>(node.ToJsonString());
            }
            catch
            {
                return null;
            }
        }

        var legacyList = ReadList(legacy, SecureCodesKey);
        if (legacyList is null || legacyList.Count == 0)
            return false;
        var companyList = ReadList(company, SecureCodesKey);
        if (companyList is null)
        {
            PosLogger.Log("Коды сотрудников: в общем документе нет списка с хэшами — перенос из старого документа владельца не выполняется.", "INFO");
            return false;
        }
        var companyVersion = VersionOf(company);
        var legacyVersion = VersionOf(legacy);
        if (legacyVersion <= companyVersion)
        {
            PosLogger.Log("Коды сотрудников: список в общем документе не старее списка владельца — общий список не меняется.", "INFO");
            return false;
        }

        var added = 0;
        var filled = 0;
        var skippedUnknown = 0;
        var inconclusive = false;
        foreach (var l in legacyList)
        {
            var match = FindEmployee(companyList, l, legacyList);
            if (match is not null)
            {
                foreach (var action in EmployeeActions)
                {
                    if (!string.IsNullOrWhiteSpace(GetHash(match, action)) || string.IsNullOrWhiteSpace(GetHash(l, action)))
                        continue;
                    SetHash(match, action, GetHash(l, action));
                    if (l.ShortCodes?.Contains(action) == true)
                        (match.ShortCodes ??= new List<string>()).Add(action);
                    filled++;
                }
                continue;
            }
            // Карточка без id и email (введена вручную) проверить нельзя — её удаление на кассе не отличить от «кассе не известна»: не добавляем.
            if (string.IsNullOrWhiteSpace(l.ServerId) && string.IsNullOrWhiteSpace(l.Email))
            {
                skippedUnknown++;
                continue;
            }
            if (siteEmployees is null)
            {
                inconclusive = true; // сайт не ответил — не добавляем и повторим при следующем запуске
                continue;
            }
            var alive = siteEmployees.Any(e =>
                (!string.IsNullOrWhiteSpace(l.ServerId) && string.Equals(e.Id, l.ServerId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(l.Email) && string.Equals(e.Email?.Trim(), l.Email.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (!alive)
            {
                skippedUnknown++;
                continue;
            }
            companyList.Add(new EmployeeAccessCode
            {
                Name = l.Name, ServerId = l.ServerId, Email = l.Email,
                CartDeleteCodeHash = l.CartDeleteCodeHash, WarehouseDeleteCodeHash = l.WarehouseDeleteCodeHash,
                ProductEditCodeHash = l.ProductEditCodeHash, ProductAddCodeHash = l.ProductAddCodeHash,
                DebtPaymentCodeHash = l.DebtPaymentCodeHash, ShortCodes = l.ShortCodes?.ToList(),
            });
            added++;
        }

        if (added > 0 || filled > 0)
        {
            company[SecureCodesKey] = JsonSerializer.SerializeToNode(EmployeeAccessGate.ToCloudSecure(companyList));
            company[LegacyCodesKey] = JsonSerializer.SerializeToNode(EmployeeAccessGate.ToCloudPublic(companyList));
            company[CodesVersionKey] = JsonValue.Create(Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Math.Max(companyVersion, legacyVersion) + 1));
            migrated.Add(SecureCodesKey);
            migrated.Add(LegacyCodesKey);
            migrated.Add(CodesVersionKey);
        }
        PosLogger.Log($"Коды сотрудников: перенос по сотруднику — добавлено {added}, заполнено кодов {filled}, не добавлено (нет на сайте или не проверить) {skippedUnknown}" +
                      $"{(inconclusive ? ", часть не сверена с сайтом (повтор при следующем запуске)" : "")}.", "INFO");
        return inconclusive;
    }

    private static readonly string[] EmployeeActions =
        { EmployeeAccessGate.CartDelete, EmployeeAccessGate.WarehouseDelete, EmployeeAccessGate.ProductEdit, EmployeeAccessGate.ProductAdd, EmployeeAccessGate.DebtPayment };

    private static string? GetHash(EmployeeAccessCode e, string action) => action switch
    {
        EmployeeAccessGate.CartDelete => e.CartDeleteCodeHash,
        EmployeeAccessGate.WarehouseDelete => e.WarehouseDeleteCodeHash,
        EmployeeAccessGate.ProductEdit => e.ProductEditCodeHash,
        EmployeeAccessGate.ProductAdd => e.ProductAddCodeHash,
        EmployeeAccessGate.DebtPayment => e.DebtPaymentCodeHash,
        _ => null,
    };

    private static void SetHash(EmployeeAccessCode e, string action, string? hash)
    {
        switch (action)
        {
            case EmployeeAccessGate.CartDelete: e.CartDeleteCodeHash = hash; break;
            case EmployeeAccessGate.WarehouseDelete: e.WarehouseDeleteCodeHash = hash; break;
            case EmployeeAccessGate.ProductEdit: e.ProductEditCodeHash = hash; break;
            case EmployeeAccessGate.ProductAdd: e.ProductAddCodeHash = hash; break;
            case EmployeeAccessGate.DebtPayment: e.DebtPaymentCodeHash = hash; break;
        }
    }

    /// <summary>Тот же сотрудник в другом списке: по ServerId, по email, по имени — только если имя единственное с обеих сторон
    /// и карточки не привязаны к разным сотрудникам сайта.</summary>
    private static EmployeeAccessCode? FindEmployee(List<EmployeeAccessCode> list, EmployeeAccessCode target, List<EmployeeAccessCode> targetSide)
    {
        if (!string.IsNullOrWhiteSpace(target.ServerId)
            && list.FirstOrDefault(x => string.Equals(x.ServerId, target.ServerId, StringComparison.OrdinalIgnoreCase)) is { } byId)
            return byId;
        if (!string.IsNullOrWhiteSpace(target.Email)
            && list.FirstOrDefault(x => string.Equals(x.Email, target.Email, StringComparison.OrdinalIgnoreCase)) is { } byEmail)
            return byEmail;
        var name = (target.Name ?? "").Trim();
        if (name.Length == 0)
            return null;
        bool Same(EmployeeAccessCode x) => string.Equals((x.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase);
        var same = list.Where(Same).ToList();
        if (same.Count != 1 || targetSide.Count(Same) != 1)
            return null;
        var candidate = same[0];
        return string.IsNullOrWhiteSpace(candidate.ServerId) || string.IsNullOrWhiteSpace(target.ServerId)
               || string.Equals(candidate.ServerId, target.ServerId, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    /// <summary>2026-10-27, ревью А-1/А-2: папка резервных копий документов перед переносом (для стенда можно подменить, чтобы не писать
    /// в настоящий профиль). По умолчанию — папка данных программы, backups/settings-migration.</summary>
    public static string? BackupDirectoryOverride { get; set; }

    /// <summary>2026-10-27, ревью А-1/А-2: полная копия общего и старого документа (как пришли с сервера) в локальный JSON ДО любого слияния.
    /// Возвращает путь файла; null — записать не удалось (тогда перенос не выполняется). Хранятся последние 10 копий.</summary>
    private static string? TryBackupDocuments(string companyId, JsonObject company, DateTimeOffset? companyAt, JsonObject legacy, DateTimeOffset? legacyAt)
    {
        try
        {
            var dir = BackupDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "backups", "settings-migration");
            Directory.CreateDirectory(dir);
            var safeCompany = new string(companyId.Where(char.IsLetterOrDigit).ToArray());
            var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{(safeCompany.Length > 0 ? safeCompany : "company")}.json");
            var root = new JsonObject
            {
                ["created_utc"] = DateTime.UtcNow.ToString("o"),
                ["company_id"] = companyId,
                ["shared_nurmarket_kassa"] = new JsonObject { ["updated_at"] = companyAt?.ToString("o"), ["settings"] = company.DeepClone() },
                ["legacy_nurmarket_owner"] = new JsonObject { ["updated_at"] = legacyAt?.ToString("o"), ["settings"] = legacy.DeepClone() },
            };
            var text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, path);
            // Читаем обратно: копия должна быть целой, иначе переносить нельзя.
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject check || check["legacy_nurmarket_owner"] is null || check["shared_nurmarket_kassa"] is null)
                return null;
            foreach (var old in Directory.GetFiles(dir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(10))
            {
                try { File.Delete(old); } catch { /* не удалилась — не страшно */ }
            }
            return path;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки владельца: резервная копия не записана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>2026-10-27, Б-1: после успешной отправки перенесённых ключей в общем документе ставится метка — перенос не повторяется
    /// (в т. ч. на других ПК владельца). Не отправились — метки нет, при следующем запуске перенос повторится.</summary>
    private static async Task WriteOwnerMergedMarkerAsync(NurMarketApiClient api, HashSet<string> migrated)
    {
        // Код сотрудников другой компании в облако не уходит (см. PushCoreAsync) — такие ключи метку не задерживают.
        bool allSent;
        lock (Sync)
            allSent = migrated.All(k => _lastCompany.ContainsKey(k) || k is SecureCodesKey or CodesVersionKey or LegacyCodesKey);
        if (!allSent)
            return;
        await PatchAsync(api, $"api/main/app-settings/{CompanyApp}/",
            new List<KeyValuePair<string, string>> { new(OwnerMergedMarkerKey, JsonSerializer.Serialize(DateTime.UtcNow.ToString("o"))) }).ConfigureAwait(false);
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
    private static async Task<bool> ApplyEmployeeCodesAsync(NurMarketApiClient api, JsonObject company, string companyId, int generation, bool ownerFirstRun = false)
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
            // 2026-10-27, ревью А-2: первый запуск владельца с общим документом — локальный список кодов был зеркалом старого документа
            // владельца, его «большая версия» ничего не говорит о том, что он новее списка касс. Берём версию облака: список общего
            // документа (в нём уже слиты коды владельца, см. MergeLegacyEmployeeCodes) применяется, а список владельца целиком в облако
            // не уходит и кассиров касс не затирает.
            if (ownerFirstRun && secure is not null && UserPreferences.Instance.EmployeeAccessCodesVersion > remoteVersion)
            {
                UserPreferences.Instance.EmployeeAccessCodesVersion = remoteVersion;
                PosLogger.Log("Коды сотрудников: первый запуск программы владельца с общим документом — берётся список общего документа.", "INFO");
            }
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
            if (companyChanged.Count > 0 && await PatchAsync(api, $"api/main/app-settings/{CompanyApp}/", companyChanged).ConfigureAwait(false) && Current())
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
