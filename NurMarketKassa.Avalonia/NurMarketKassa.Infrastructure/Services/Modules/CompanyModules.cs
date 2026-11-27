using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (ответ Умара 20.11, В.16 / В.21 / В.23): коды модулей Маркетплейса на сервере (GET users/company/modules/). Только те, что касса уже умеет показывать или скрывать.</summary>
public static class ModuleIds
{
    public const string Estimate = "estimate", Rental = "rental", Repair = "repair", TradeIn = "trade_in", Variants = "variants",
        Packages = "packages", ClientPrice = "client_price", FractionalQty = "fractional_qty", PriceLevels = "price_levels", MaterialCalc = "material_calc",
        Serials = "serials", Warranty = "warranty", PartNumbers = "part_numbers", CrossAnalogs = "cross_analogs", CustomerOrders = "customer_orders",
        ScalesLabels = "scales_labels", ExpiryBatches = "expiry_batches", QuickItems = "quick_items", Consultant = "consultant", Exchange = "exchange";

    /// <summary>2026-11-26 («Выход мяса при приходе», просьба владельца для мясных магазинов): сервер этот модуль пока не знает — касса включает его локально (UserPreferences.CarcassYieldEnabled), см. CarcassYieldFeature.</summary>
    public const string CarcassYield = "carcass_yield";

    /// <summary>2026-11-26: «Статус ремонта на витрине» — клиент проверяет ремонт по коду квитанции на онлайн-витрине. Отдельного модуля у сервера нет (Умар: «привязано к Ремонту»): признак repair_status_public
    /// приходит в capabilities.features при включённом модуле «Ремонт»; карточка Маркетплейса — «Включено» по признаку, «Нужен Ремонт» при выключенном «Ремонте», «Скоро» — пока сервер признак не прислал.</summary>
    public const string RepairStatusSite = "repair_status_site";

    /// <summary>Модули, у которых в кассе своя карточка Маркетплейса и своё окно (остальные получают общую карточку «Модуль»).</summary>
    public static readonly string[] WithOwnCard = { Estimate, Rental, Repair, TradeIn };
}

/// <summary>Одна строка items[] ответа: включён ли модуль и откуда (default — из профиля, added — добавил владелец, removed — владелец убрал из профиля, off — не входит и не включён).</summary>
/// 2026-11-21 (ответ Умара 21.11): source "trial" — пробный период 7 дней, TrialUntil — trial_until (на пробе модуль в capabilities.modules.enabled, но в абонплату не входит).
public sealed record ModuleItem(string Id, bool Enabled, string Source, DateTimeOffset? TrialUntil = null)
{
    public bool IsTrial => string.Equals(Source, "trial", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Модуль из catalog[]: название на языке интерфейса, группа и профили, где он включён по умолчанию.</summary>
public sealed record ModuleInfo(string Id, string Name, string Group, IReadOnlyList<string> DefaultIn);

/// <summary>Профиль (сфера) из profiles[]: id, база (grocery / clothing / services…) и его модули по умолчанию.</summary>
public sealed record ModuleProfileInfo(string Id, string Name, string? Base, IReadOnlyList<string> Modules);

/// <summary>Разобранный ответ GET/PUT users/company/modules/ (и DELETE users/company/tariff/modules/{code}/). Версия — для If-Match.</summary>
public sealed class CompanyModulesSnapshot
{
    public string Profile { get; init; } = "";
    public string? ProfileBase { get; init; }
    public string? Mode { get; init; }
    /// <summary>2026-11-21 (ответ Умара 21.11): profile_source — "owner" (профиль выбрал владелец) или "auto" (вывел сервер, например hozmag → stroymag). Нет поля (старый сервер) — как "owner".</summary>
    public string? ProfileSource { get; init; }
    /// <summary>Профиль вывел сервер сам: касса не считает его выбором клиента и свою сферу не перезаписывает.</summary>
    public bool ProfileIsAuto => string.Equals(ProfileSource, "auto", StringComparison.OrdinalIgnoreCase);
    public string Version { get; init; } = "";
    public IReadOnlyList<ModuleItem> Items { get; init; } = Array.Empty<ModuleItem>();
    public IReadOnlyList<ModuleInfo> Catalog { get; init; } = Array.Empty<ModuleInfo>();
    public IReadOnlyList<ModuleProfileInfo> Profiles { get; init; } = Array.Empty<ModuleProfileInfo>();
    public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();
    /// <summary>Включённые модули (modules[] ответа; нет — по items[]).</summary>
    public IReadOnlySet<string> Enabled { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ModuleItem? Item(string id) => Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    public ModuleInfo? Info(string id) => Catalog.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Сервер знает такой модуль (есть в items[] или catalog[]).</summary>
    public bool Knows(string id) => Item(id) is not null || Info(id) is not null;

    public bool IsEnabled(string id) => Enabled.Contains(id);

    /// <summary>Разбор ответа; null — это не ответ модулей (нет ни modules[], ни items[]). Лишние и неизвестные поля пропускаются.</summary>
    public static CompanyModulesSnapshot? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        // Иногда ответ завёрнут в {"data": {...}}.
        if (root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object
            && !root.TryGetProperty("modules", out _) && !root.TryGetProperty("items", out _))
            root = inner;
        var hasModules = root.TryGetProperty("modules", out var mods) && mods.ValueKind == JsonValueKind.Array;
        var hasItems = root.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array;
        if (!hasModules && !hasItems)
            return null;

        var items = new List<ModuleItem>();
        if (hasItems)
            foreach (var el in its.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var id = Str(el, "id");
                if (id is null)
                    continue;
                var enabled = el.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                items.Add(new ModuleItem(id, enabled, (Str(el, "source") ?? "").ToLowerInvariant(),
                    Str(el, "trial_until") is { } tu && DateTimeOffset.TryParse(tu, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var tuDate) ? tuDate : null));
            }

        var enabledSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hasModules)
            foreach (var m in mods.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())))
                enabledSet.Add(m.GetString()!.Trim());
        else
            foreach (var i in items.Where(i => i.Enabled))
                enabledSet.Add(i.Id);

        var catalog = new List<ModuleInfo>();
        if (root.TryGetProperty("catalog", out var cat) && cat.ValueKind == JsonValueKind.Array)
            foreach (var el in cat.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var id = Str(el, "id");
                if (id is null)
                    continue;
                catalog.Add(new ModuleInfo(id, TariffApi.LocalName(el, "names") ?? TariffApi.LocalName(el, "name") ?? id, Str(el, "group") ?? "", Strings(el, "default_in")));
            }

        var profiles = new List<ModuleProfileInfo>();
        if (root.TryGetProperty("profiles", out var prof) && prof.ValueKind == JsonValueKind.Array)
            foreach (var el in prof.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            {
                var id = Str(el, "id");
                if (id is null)
                    continue;
                profiles.Add(new ModuleProfileInfo(id, TariffApi.LocalName(el, "names") ?? TariffApi.LocalName(el, "name") ?? id, Str(el, "base"), Strings(el, "modules")));
            }

        return new CompanyModulesSnapshot
        {
            Profile = Str(root, "profile") ?? "",
            ProfileBase = Str(root, "profile_base"),
            Mode = Str(root, "mode"),
            ProfileSource = Str(root, "profile_source")?.ToLowerInvariant(),
            Version = Str(root, "version") ?? "",
            Items = items,
            Catalog = catalog,
            Profiles = profiles,
            Flags = Strings(root, "flags"),
            Enabled = enabledSet,
        };
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static List<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())).Select(x => x.GetString()!.Trim()).ToList()
            : new List<string>();
}

/// <summary>TrialUsed / AlreadyActive — 409 trial_used / already_active у POST users/company/features/trial/ (2026-11-21).</summary>
public enum ModuleChangeStatus { Ok, Unsupported, NoConnection, Forbidden, UnknownModule, Conflict, Failed, TrialUsed, AlreadyActive }

/// <summary>Итог «Включить» / «Выключить» модуль. Detail — текст сервера, если он есть.</summary>
public sealed record ModuleChangeResult(ModuleChangeStatus Status, string? Detail = null);

/// <summary>
/// 2026-11-21 (ответ Умара 20.11, В.16 / В.21 / В.23): реестр модулей компании с сервера — GET/PUT users/company/modules/, DELETE users/company/tariff/modules/{code}/.
/// Профиль (сфера) задаёт модули по умолчанию; владелец добавляет модули других сфер и убирает ненужные. Модуль включён = items[].enabled.
///
/// Касса и программа владельца спрашивают одно место — <see cref="IsOn"/>; без ответа сервера (нет связи, старый сервер без адреса, чужая компания) результат прежний:
/// <c>profileDefault || explicitLocal</c> (профиль сферы кассы и признак, включённый в Маркетплейсе раньше). Кэш — в памяти и на диске по компании (company-modules.json), условный запрос (ETag, если сервер его отдаёт).
/// Изменение набора модулей поднимает <see cref="Changed"/> и <see cref="TariffGate.RaisePacksChanged"/> — меню кассы, программа владельца и витрина Маркетплейса пересобираются без перезапуска.
///
/// Профиль, выбранный на сайте (market.nurcrm.kg → настройки → сфера), берётся кассой, если он изменился с тех пор, как касса видела его в прошлый раз (или на новой установке) — как вид магазина
/// (<see cref="MarketSphereSync"/>). Сфера, выбранная в кассе, уходит на сервер <see cref="PushProfileAsync"/> (владелец; кассиру сервер ответит 403 — тогда останется только в кассе).
/// </summary>
public static class CompanyModules
{
    /// <summary>Признак сервера features.company_modules (ответ capabilities).</summary>
    public const string FeatureName = "company_modules";

    private const string Path = "api/users/company/modules/";
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnsupportedFor = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public string Account = "";
        public CompanyModulesSnapshot Snap = null!;
        public string? Etag;
        public string? SeenProfile; // профиль сервера, который касса видела в прошлый раз (для «на сайте сменили»)
    }

    private static volatile Entry? _cur;
    private static readonly object DiskLock = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _diskTriedFor;
    private static DateTime _lastAttemptUtc = DateTime.MinValue;
    private static DateTime _unsupportedUntilUtc = DateTime.MinValue;
    private static string _lastIssue = "";

    /// <summary>Набор модулей, профиль или версия изменились (или получены впервые). Приходит из любого потока.</summary>
    public static event Action? Changed;

    /// <summary>Клиент API; не задан — тот же, что у платных функций и бонусов.</summary>
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    /// <summary>Только для тестов: папка кэша вместо AppPaths.KassaLocal.</summary>
    internal static string? DirOverride { get; set; }

    private static NurMarketApiClient? Api =>
        (ApiProvider ?? ServerAddons.ApiProvider ?? ServerLoyalty.ApiProvider)?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    private static string AccountKey() => ServerCapabilities.AccountKey();

    private static bool HasAccount(string account) => account.Trim('|').Length > 0;

    private static string DiskKey(string account) => AppPaths.ServerKeyPrefix + account;

    private static string FilePath => System.IO.Path.Combine(DirOverride ?? AppPaths.KassaLocal, "company-modules.json");

    // ───────────────────────────── чтение состояния ─────────────────────────────

    private static Entry? Current()
    {
        var account = AccountKey();
        if (!HasAccount(account))
            return null;
        TryLoadFromDisk(account);
        var cur = _cur;
        return cur is not null && cur.Account == account ? cur : null;
    }

    /// <summary>Последний известный ответ сервера для этой компании (из памяти или с диска); null — ещё не получали, старый сервер или другая компания.</summary>
    public static CompanyModulesSnapshot? Snapshot => Current()?.Snap;

    /// <summary>Реестр модулей этой компании известен (ответ сервера получен хотя бы раз).</summary>
    public static bool IsKnown => Current() is not null;

    /// <summary>Модуль включён в компании. Единственное место решения для кассы и программы владельца.
    /// <paramref name="profileDefault"/> — включён ли он профилем сферы кассы (прежнее поведение); <paramref name="explicitLocal"/> — признак, включённый в Маркетплейсе раньше (EstimateEnabled…).
    /// Ответа сервера нет или он такого модуля не знает — <c>profileDefault || explicitLocal</c>. Ответ есть: «убрал владелец» (removed) — выключен всегда; профиль сервера совпадает с профилем кассы —
    /// решает один сервер (<c>enabled</c>; 2026-11-22: местный признак <paramref name="explicitLocal"/> при ответе сервера по модулю не учитывается); не совпадает (например, сервер вывел профиль сам) — прежний профиль кассы, а от сервера берётся только то, что владелец добавил (added / trial).</summary>
    public static bool IsOn(string id, bool profileDefault, bool explicitLocal = false)
    {
        var entry = Current();
        if (entry is null)
            return profileDefault || explicitLocal;
        return Decide(entry.Snap, id, profileDefault, explicitLocal, ProfilesMatch(entry.Snap.Profile, MarketSpheres.Profile.Id));
    }

    /// <summary>Чистое правило <see cref="IsOn"/> (для тестов).</summary>
    internal static bool Decide(CompanyModulesSnapshot snap, string id, bool profileDefault, bool explicitLocal, bool profileInSync)
    {
        var item = snap.Item(id);
        if (item is null)
            return snap.IsEnabled(id) || profileDefault || explicitLocal; // сервер модуль в items не прислал — только modules[] или прежнее
        if (item.Source == "removed")
            return false;
        // 2026-11-22: сервер ответил про модуль (есть запись в items) — он единственный источник; старый местный признак (EstimateEnabled / RentalEnabled в user-settings.json)
        // больше не удерживает функцию включённой, когда сервер говорит «выкл» (у не-Строймага оставались «Смета» и «Прокат»). Местный признак — только когда ответа по модулю нет (выше и в IsOn).
        if (profileInSync)
            return item.Enabled || snap.IsEnabled(id);
        return item.Source is "added" or "trial" || profileDefault; // 2026-11-21: проба (trial) — как добавленный владельцем
    }

    /// <summary>Владелец убрал модуль (source = removed): функция выключена, даже если у кассы остался старый признак или есть данные.</summary>
    public static bool IsRemoved(string id) => Current()?.Snap.Item(id) is { Source: "removed" };

    /// <summary>Модуль входит в профиль сферы бесплатно («входит в вашу сферу»): по catalog[].default_in и profiles[].modules ответа, запасной путь — GET public/modules/.</summary>
    public static bool IncludedInProfile(string id)
    {
        var snap = Current()?.Snap;
        if (snap is null || snap.Profile.Length == 0)
            return false;
        var p = CanonProfile(snap.Profile);
        if (snap.Info(id)?.DefaultIn.Any(d => CanonProfile(d) == p) == true)
            return true;
        if (snap.Profiles.FirstOrDefault(x => CanonProfile(x.Id) == p)?.Modules.Any(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        return PublicModules.IncludedInProfile(id, snap.Profile);
    }

    /// <summary>id профиля сервера → единое имя: hardware → stroymag, bazaar_retail / bazaar_wholesale → bazaar.</summary>
    internal static string CanonProfile(string? id)
    {
        var v = (id ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "hardware" => HardwareProfile.StroymagId,
            "bazaar_retail" or "bazaar_wholesale" => BazaarProfile.Id,
            _ => v,
        };
    }

    internal static bool ProfilesMatch(string? serverProfile, string? kassaProfile) =>
        string.Equals(CanonProfile(serverProfile), CanonProfile(kassaProfile), StringComparison.Ordinal);

    // ───────────────────────────── кэш на диске ─────────────────────────────

    private static void TryLoadFromDisk(string account)
    {
        if (_diskTriedFor == account)
            return;
        lock (DiskLock)
        {
            if (_diskTriedFor == account)
                return;
            _diskTriedFor = account;
            if (_cur is { } now && now.Account == account)
                return;
            try
            {
                var path = FilePath;
                if (!File.Exists(path))
                    return;
                var text = AtomicFile.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text))
                    return;
                if (JsonNode.Parse(text) is not JsonObject root || root["accounts"] is not JsonObject accounts || accounts[DiskKey(account)] is not JsonObject entry
                    || entry["data"] is not JsonNode data)
                    return;
                using var doc = JsonDocument.Parse(data.ToJsonString());
                if (CompanyModulesSnapshot.Parse(doc.RootElement) is not { } snap)
                    return;
                _cur = new Entry { Account = account, Snap = snap, Etag = entry["etag"]?.GetValue<string>(), SeenProfile = entry["seen_profile"]?.GetValue<string>() };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or System.Security.SecurityException)
            {
                PosLogger.Log($"Модули компании: кэш не прочитан ({ex.GetType().Name}).", "WARNING");
            }
        }
    }

    private static void SaveToDisk(Entry entry, JsonElement raw)
    {
        if (!HasAccount(entry.Account))
            return;
        lock (DiskLock)
        {
            try
            {
                var path = FilePath;
                var accounts = new JsonObject();
                try
                {
                    if (File.Exists(path) && AtomicFile.ReadAllText(path) is { Length: > 0 } old && JsonNode.Parse(old) is JsonObject oldRoot && oldRoot["accounts"] is JsonObject oldAccounts)
                        foreach (var kv in oldAccounts.ToArray())
                            if (accounts.Count < 7 && kv.Key != DiskKey(entry.Account))
                                accounts[kv.Key] = kv.Value?.DeepClone();
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { /* битый файл — перепишем с нуля */ }
                accounts[DiskKey(entry.Account)] = new JsonObject
                {
                    ["saved_at"] = DateTime.UtcNow.ToString("O"),
                    ["etag"] = entry.Etag,
                    ["seen_profile"] = entry.SeenProfile,
                    ["data"] = JsonNode.Parse(raw.GetRawText()),
                };
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, new JsonObject { ["accounts"] = accounts }.ToJsonString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
            {
                PosLogger.Log($"Модули компании: кэш не записан ({ex.GetType().Name}).", "WARNING");
            }
        }
    }

    /// <summary>Блок capabilities.modules ({profile, version, enabled}) и признак features.company_modules: версия отличается от известной (или модулей мы ещё не знаем, а сервер их умеет) — читаем модули заново.
    /// Старый сервер без блока — ничего не происходит.</summary>
    internal static void NoteCapabilities(JsonElement capabilities)
    {
        try
        {
            if (capabilities.ValueKind != JsonValueKind.Object)
                return;
            var cur = Current()?.Snap;
            if (capabilities.TryGetProperty("modules", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                var version = m.TryGetProperty("version", out var v) ? Str(v) : null;
                if (version is not null && (cur is null || cur.Version != version))
                    RefreshInBackground(force: true);
                return;
            }
            if (cur is null && ServerHasFeature(capabilities, FeatureName))
                RefreshInBackground();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Модули компании: capabilities не разобраны ({ex.Message}).", "WARNING");
        }
    }

    private static string? Str(JsonElement v) => v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };

    private static bool ServerHasFeature(JsonElement capabilities, string name)
    {
        foreach (var key in new[] { "features", "capabilities" })
            if (capabilities.TryGetProperty(key, out var part))
            {
                if (part.ValueKind == JsonValueKind.Array && part.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == name))
                    return true;
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.True)
                    return true;
            }
        return false;
    }

    /// <summary>Забыть всё в памяти (смена аккаунта, выход); файл остаётся — он по компании.</summary>
    public static void Reset()
    {
        _cur = null;
        _diskTriedFor = null;
        _lastAttemptUtc = DateTime.MinValue;
        _unsupportedUntilUtc = DateTime.MinValue;
    }

    // ───────────────────────────── загрузка с сервера ─────────────────────────────

    /// <summary>Запросить модули в фоне (после входа, по такту AccessSync и по событию features_changed). Без входа и без связи — тихо.</summary>
    public static void RefreshInBackground(bool force = false)
    {
        if (Api is not { } api)
            return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(api, force).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Модули компании: фоновое обновление: {ex.Message}", "WARNING"); }
        });
    }

    public static Task<bool> RefreshAsync(bool force = false, CancellationToken ct = default) =>
        Api is { } api ? RefreshAsync(api, force, ct) : Task.FromResult(false);

    /// <summary>GET users/company/modules/. true — ответ получен и разобран (или 304). Ошибка запроса — false, кэш остаётся.</summary>
    internal static async Task<bool> RefreshAsync(NurMarketApiClient api, bool force = false, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (now < _unsupportedUntilUtc)
            return false;
        if (!force && now - _lastAttemptUtc < MinGap)
            return Current() is not null;
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
            return false;
        try
        {
            _lastAttemptUtc = DateTime.UtcNow;
            var account = AccountKey();
            if (!HasAccount(account))
                return false;
            var cur = Current();
            try
            {
                var (body, status, etag) = await api.GetConditionalAsync(Path, null, cur?.Etag, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                if (status == 304 && cur is not null)
                    return true;
                var snap = CompanyModulesSnapshot.Parse(body);
                if (snap is null)
                {
                    NoteIssue("bad-format", "Модули компании: неожиданный ответ — работаем по профилю кассы.");
                    return false;
                }
                _lastIssue = "";
                Apply(account, snap, body, etag);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405 or 501)
            {
                _unsupportedUntilUtc = DateTime.UtcNow + UnsupportedFor; // сервер без реестра модулей — прежнее поведение
                NoteIssue("unsupported", "Модули компании: на сервере нет адреса users/company/modules/ — работаем по профилю кассы.");
                return false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                NoteIssue(ex is ApiException a ? "HTTP " + a.StatusCode : ex.GetType().Name, $"Модули компании не загружены ({(ex is ApiException a2 ? "HTTP " + a2.StatusCode : ex.GetType().Name)}) — кэш или профиль кассы.");
                return false;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void NoteIssue(string issue, string text)
    {
        if (_lastIssue == issue)
            return;
        _lastIssue = issue;
        PosLogger.Log(text, "WARNING");
    }

    /// <summary>Принять ответ сервера (GET, PUT, DELETE): запомнить, записать на диск, поднять Changed при изменении, потом — профиль.</summary>
    private static void Apply(string account, CompanyModulesSnapshot snap, JsonElement raw, string? etag)
    {
        var prev = _cur is { } p && p.Account == account ? p : null;
        if (prev is null)
        {
            TryLoadFromDisk(account);
            prev = _cur is { } q && q.Account == account ? q : null;
        }
        var entry = new Entry { Account = account, Snap = snap, Etag = string.IsNullOrWhiteSpace(etag) ? null : etag, SeenProfile = prev?.SeenProfile };
        var changed = prev is null || prev.Snap.Version != snap.Version || !prev.Snap.Enabled.SetEquals(snap.Enabled)
                      || !string.Equals(CanonProfile(prev.Snap.Profile), CanonProfile(snap.Profile), StringComparison.Ordinal)
                      || prev.Snap.Items.Count != snap.Items.Count || prev.Snap.Items.Zip(snap.Items).Any(z => z.First.Enabled != z.Second.Enabled || z.First.Source != z.Second.Source);
        _cur = entry;
        Volatile.Write(ref _diskTriedFor, account);
        var adopted = ReconcileProfile(entry);
        SaveToDisk(entry, raw);
        if (changed || adopted)
        {
            PosLogger.Log($"Модули компании: профиль «{snap.Profile}», включено {snap.Enabled.Count} ({string.Join(", ", snap.Enabled.OrderBy(s => s, StringComparer.Ordinal))}).", "INFO");
            RaiseChanged();
        }
    }

    internal static void RaiseChanged()
    {
        try
        {
            TariffGate.RaisePacksChanged(); // меню кассы и программы владельца пересобираются
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Модули компании: обработчик изменения упал ({ex.Message}).", "WARNING");
        }
    }

    // ───────────────────────────── профиль с сайта ─────────────────────────────

    /// <summary>Профиль с сервера → касса: на новой установке или когда он изменился с тех пор, как касса видела его в прошлый раз. true — сфера кассы сменилась.</summary>
    private static bool ReconcileProfile(Entry entry)
    {
        var server = CanonProfile(entry.Snap.Profile);
        if (server.Length == 0)
            return false;
        var seen = entry.SeenProfile;
        var local = CanonProfile(MarketSpheres.Profile.Id);
        // 2026-11-21 (ответ Умара 21.11): profile_source "auto" — профиль вывел сервер, а не выбрал владелец: выбор сферы в кассе не перезаписываем (и «виденный» профиль не запоминаем,
        // чтобы последующий выбор владельца на сайте — "owner" — подхватился). "owner" или нет поля — как раньше.
        if (entry.Snap.ProfileIsAuto)
            return false;
        entry.SeenProfile = server;
        if (server == local)
            return false;
        var siteChanged = seen is not null && !string.Equals(seen, server, StringComparison.Ordinal);
        var freshInstall = seen is null && !UserPreferences.Instance.MarketSphereWasInFile;
        if (!siteChanged && !freshInstall)
            return false; // выбор клиента на уже настроенной кассе не затираем; пока профили разные, модули профиля берутся по кассе (см. Decide)
        if (SphereCatalog.TryGet(server) is not { } profile)
            return false; // профиль новее кассы — остаётся прежняя сфера
        PosLogger.Log($"Профиль магазина взят с сервера: {local} → {profile.Id} ({(freshInstall ? "новая установка" : "на сайте сменили")}).", "API");
        MarketSpheres.Set(profile.Id); // на сервер из Set ничего не уходит: отправляет только явный выбор владельца (PushProfileAsync)
        if (CanonProfile(entry.Snap.Profile) == BazaarProfile.Id)
            SphereSettings.SetBazaarMode(string.Equals(entry.Snap.Mode, "wholesale", StringComparison.OrdinalIgnoreCase) || entry.Snap.Profile.EndsWith("wholesale", StringComparison.OrdinalIgnoreCase) ? BazaarMode.Wholesale : BazaarMode.Retail);
        return true;
    }

    /// <summary>Владелец выбрал сферу в кассе (Настройки → Операции или первый запуск) — профиль сервера становится таким же: PUT users/company/modules/ {profile, mode?}. Менять может владелец / администратор;
    /// 403 и ошибки — тихо (сфера остаётся только в этой кассе). Вызывается ТОЛЬКО из места явного выбора: сфера, пришедшая с сервера или из общих настроек, на сервер не возвращается.</summary>
    public static async Task<ModuleChangeResult> PushProfileAsync(CancellationToken ct = default)
    {
        if (Api is not { } api)
            return new ModuleChangeResult(ModuleChangeStatus.NoConnection);
        var local = CanonProfile(MarketSpheres.Profile.Id);
        if (SphereCatalog.TryGet(local) is null)
            return new ModuleChangeResult(ModuleChangeStatus.Unsupported);
        if (Current() is { } cur && ProfilesMatch(cur.Snap.Profile, local)
            && (local != BazaarProfile.Id || string.Equals(cur.Snap.Mode, SphereSettings.GetBazaarMode() == BazaarMode.Wholesale ? "wholesale" : "retail", StringComparison.OrdinalIgnoreCase)))
            return new ModuleChangeResult(ModuleChangeStatus.Ok); // сервер уже на этом профиле
        var body = new Dictionary<string, object?> { ["profile"] = local };
        if (local == BazaarProfile.Id)
            body["mode"] = SphereSettings.GetBazaarMode() == BazaarMode.Wholesale ? "wholesale" : "retail";
        return await SendChangeAsync(api, HttpMethod.Put, Path, body, enable: true, ct).ConfigureAwait(false);
    }

    // ───────────────────────────── включить / выключить ─────────────────────────────

    public static Task<ModuleChangeResult> EnableAsync(string id, CancellationToken ct = default) => ChangeAsync(id, true, ct);

    public static Task<ModuleChangeResult> DisableAsync(string id, CancellationToken ct = default) => ChangeAsync(id, false, ct);

    // ───────────────────────────── пробный период модуля ─────────────────────────────

    private static readonly HashSet<string> TrialUsedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Пробный период этого модуля уже использован (сервер ответил 409 trial_used — иного признака в ответе модулей нет). Живёт до перезапуска.</summary>
    public static bool IsTrialUsed(string id)
    {
        lock (TrialUsedKeys)
            return TrialUsedKeys.Contains(AccountKey() + "#" + id);
    }

    /// <summary>Модуль сейчас на пробе: source "trial" (trial_until — до какого числа).</summary>
    public static bool IsOnTrial(string id, out DateTimeOffset? until)
    {
        var item = Current()?.Snap.Item(id);
        until = item?.TrialUntil;
        return item is { IsTrial: true, Enabled: true };
    }

    /// <summary>POST users/company/features/trial/ {code}: 7 дней пробного периода модуля (200 {code, kind:"module", expires_at, modules_version}). 409 trial_used — проба уже была, 409 already_active — модуль уже включён.
    /// После успеха модули перечитываются (source "trial", trial_until).</summary>
    public static async Task<ModuleChangeResult> StartTrialAsync(string id, CancellationToken ct = default)
    {
        if (Api is not { } api)
            return new ModuleChangeResult(ModuleChangeStatus.NoConnection);
        return await StartTrialAsync(api, id, ct).ConfigureAwait(false);
    }

    internal static async Task<ModuleChangeResult> StartTrialAsync(NurMarketApiClient api, string id, CancellationToken ct)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0)
            return new ModuleChangeResult(ModuleChangeStatus.UnknownModule);
        try
        {
            await api.RequestAsync(HttpMethod.Post, "api/users/company/features/trial/", new Dictionary<string, object?> { ["code"] = id }, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await RefreshAsync(api, force: true, CancellationToken.None).ConfigureAwait(false);
            return new ModuleChangeResult(ModuleChangeStatus.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex)
        {
            var raw = (ex.Payload?.GetRawText() ?? "") + " " + ex.Message;
            if (ex.StatusCode == 409 && raw.Contains("trial_used", StringComparison.OrdinalIgnoreCase))
            {
                lock (TrialUsedKeys)
                    TrialUsedKeys.Add(AccountKey() + "#" + id);
                RaiseChanged(); // карточка перерисуется без кнопки «Попробовать»
                return new ModuleChangeResult(ModuleChangeStatus.TrialUsed);
            }
            if (ex.StatusCode == 409 && raw.Contains("already_active", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshAsync(api, force: true, CancellationToken.None).ConfigureAwait(false);
                return new ModuleChangeResult(ModuleChangeStatus.AlreadyActive);
            }
            if (ex.StatusCode == 403 || raw.Contains("permission_denied", StringComparison.OrdinalIgnoreCase))
                return new ModuleChangeResult(ModuleChangeStatus.Forbidden);
            if (raw.Contains("module_unknown", StringComparison.OrdinalIgnoreCase))
                return new ModuleChangeResult(ModuleChangeStatus.UnknownModule, TariffCustomRules.ServerText(ex));
            if (ex.StatusCode is 404 or 405 or 501)
                return new ModuleChangeResult(ModuleChangeStatus.Unsupported);
            if (ex.StatusCode is >= 500 or 408 or 429)
                return new ModuleChangeResult(ModuleChangeStatus.NoConnection, TariffCustomRules.ServerText(ex));
            PosLogger.Log($"Модули компании: пробный период «{id}» не принят (HTTP {ex.StatusCode}).", "WARNING");
            return new ModuleChangeResult(ModuleChangeStatus.Failed, TariffCustomRules.ServerText(ex));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            return new ModuleChangeResult(ModuleChangeStatus.NoConnection);
        }
    }

    private static async Task<ModuleChangeResult> ChangeAsync(string id, bool enable, CancellationToken ct)
    {
        if (Api is not { } api)
            return new ModuleChangeResult(ModuleChangeStatus.NoConnection);
        return await ChangeAsync(api, id, enable, ct).ConfigureAwait(false);
    }

    /// <summary>Включить: PUT users/company/modules/ {enable:[id]}. Выключить: DELETE users/company/tariff/modules/{id}/ (если такого адреса нет — PUT {disable:[id]}).
    /// If-Match — версия из последнего ответа; 409 version_conflict — перечитать и повторить один раз.</summary>
    internal static async Task<ModuleChangeResult> ChangeAsync(NurMarketApiClient api, string id, bool enable, CancellationToken ct)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0)
            return new ModuleChangeResult(ModuleChangeStatus.UnknownModule);
        var result = enable
            ? await SendChangeAsync(api, HttpMethod.Put, Path, new Dictionary<string, object?> { ["enable"] = new[] { id } }, true, ct).ConfigureAwait(false)
            : await SendChangeAsync(api, HttpMethod.Delete, "api/users/company/tariff/modules/" + Uri.EscapeDataString(id) + "/", null, false, ct, id).ConfigureAwait(false);
        if (!enable && result.Status == ModuleChangeStatus.Unsupported)
            result = await SendChangeAsync(api, HttpMethod.Put, Path, new Dictionary<string, object?> { ["disable"] = new[] { id } }, false, ct).ConfigureAwait(false);
        return result;
    }

    private static async Task<ModuleChangeResult> SendChangeAsync(NurMarketApiClient api, HttpMethod method, string path, object? body, bool enable, CancellationToken ct, string? moduleId = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var account = AccountKey();
            var version = Current()?.Snap.Version;
            var headers = string.IsNullOrEmpty(version) ? null : new Dictionary<string, string> { ["If-Match"] = version };
            try
            {
                var data = await api.RequestAsync(method, path, body, null, ct, TimeSpan.FromSeconds(20), headers).ConfigureAwait(false);
                if (CompanyModulesSnapshot.Parse(data) is { } snap && HasAccount(account))
                    Apply(account, snap, data, null);
                else
                    _ = RefreshAsync(api, force: true, CancellationToken.None); // ответ без модулей — перечитаем
                return new ModuleChangeResult(ModuleChangeStatus.Ok);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiException ex)
            {
                var raw = (ex.Payload?.GetRawText() ?? "") + " " + ex.Message;
                if (ex.StatusCode == 409 && raw.Contains("version_conflict", StringComparison.OrdinalIgnoreCase))
                {
                    await RefreshAsync(api, force: true, ct).ConfigureAwait(false); // версия устарела — перечитать и повторить один раз
                    if (attempt == 0)
                        continue;
                    return new ModuleChangeResult(ModuleChangeStatus.Conflict);
                }
                if (ex.StatusCode == 409 && raw.Contains("module_not_enabled", StringComparison.OrdinalIgnoreCase) && !enable)
                {
                    _ = RefreshAsync(api, force: true, CancellationToken.None); // уже выключен — цель достигнута
                    return new ModuleChangeResult(ModuleChangeStatus.Ok);
                }
                if (ex.StatusCode == 403 || raw.Contains("permission_denied", StringComparison.OrdinalIgnoreCase))
                    return new ModuleChangeResult(ModuleChangeStatus.Forbidden);
                if (raw.Contains("module_unknown", StringComparison.OrdinalIgnoreCase) || raw.Contains("profile_unknown", StringComparison.OrdinalIgnoreCase))
                    return new ModuleChangeResult(ModuleChangeStatus.UnknownModule, TariffCustomRules.ServerText(ex));
                if (ex.StatusCode is 404 or 405 or 501)
                    return new ModuleChangeResult(ModuleChangeStatus.Unsupported);
                if (ex.StatusCode is >= 500 or 408 or 429)
                    return new ModuleChangeResult(ModuleChangeStatus.NoConnection, TariffCustomRules.ServerText(ex));
                PosLogger.Log($"Модули компании: {method} {path} не принят (HTTP {ex.StatusCode}).", "WARNING");
                return new ModuleChangeResult(ModuleChangeStatus.Failed, TariffCustomRules.ServerText(ex));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
            {
                return new ModuleChangeResult(ModuleChangeStatus.NoConnection);
            }
        }
        return new ModuleChangeResult(ModuleChangeStatus.Conflict);
    }

    /// <summary>Понятный текст итога на языке интерфейса; null — всё хорошо.</summary>
    public static string? Describe(ModuleChangeResult r) => r.Status switch
    {
        ModuleChangeStatus.Ok => null,
        ModuleChangeStatus.Forbidden => Tr.T("Модули меняет только владелец или администратор компании.", "Модулдарды компаниянын ээси же администратору гана өзгөртөт.",
            "Only the company owner or an administrator can change modules.", "Modülleri yalnızca şirket sahibi veya yönetici değiştirebilir.", "Modullarni faqat kompaniya egasi yoki administrator o'zgartira oladi."),
        ModuleChangeStatus.NoConnection => Tr.T("Нет связи с сервером — повторите, когда появится интернет.", "Сервер менен байланыш жок — интернет пайда болгондо кайра аракет кылыңыз.",
            "No connection to the server — try again when you are online.", "Sunucuyla bağlantı yok — internet geldiğinde tekrar deneyin.", "Server bilan aloqa yo'q — internet paydo bo'lganda qayta urinib ko'ring."),
        ModuleChangeStatus.UnknownModule => Tr.T("Сервер не знает этот модуль — обновите программу или обратитесь в поддержку.", "Сервер бул модулду билбейт — программаны жаңыртыңыз же колдоо кызматына кайрылыңыз.",
            "The server does not know this module — update the program or contact support.", "Sunucu bu modülü tanımıyor — programı güncelleyin veya destekle iletişime geçin.", "Server bu modulni bilmaydi — dasturni yangilang yoki qo'llab-quvvatlashga murojaat qiling."),
        ModuleChangeStatus.Conflict => Tr.T("Модули только что изменили в другом месте — список обновлён, повторите действие.", "Модулдар дагы бир жерде эми өзгөртүлдү — тизме жаңыртылды, аракетти кайталаңыз.",
            "The modules were just changed elsewhere — the list is refreshed, repeat the action.", "Modüller az önce başka yerde değiştirildi — liste yenilendi, işlemi tekrarlayın.", "Modullar hozirgina boshqa joyda o'zgartirildi — ro'yxat yangilandi, amalni takrorlang."),
        ModuleChangeStatus.TrialUsed => Tr.T("Пробный период этого модуля уже использован. Модуль можно включить обычным порядком — цена в карточке.", "Бул модулдун сыноо мөөнөтү мурда колдонулган. Модулду кадимки тартипте күйгүзсө болот — баасы карточкада.",
            "The trial period of this module has already been used. You can turn the module on in the usual way — the price is on the card.", "Bu modülün deneme süresi daha önce kullanıldı. Modül olağan şekilde açılabilir — fiyat kartta.", "Bu modulning sinov muddati allaqachon ishlatilgan. Modulni odatdagidek yoqish mumkin — narxi kartochkada."),
        ModuleChangeStatus.AlreadyActive => Tr.T("Модуль уже включён — пробный период не нужен.", "Модуль мурунтан эле күйгүзүлгөн — сыноо мөөнөтү керек эмес.",
            "The module is already on — no trial is needed.", "Modül zaten açık — deneme gerekmez.", "Modul allaqachon yoqilgan — sinov kerak emas."),
        ModuleChangeStatus.Unsupported => Tr.T("Этот сервер пока не умеет менять модули.", "Бул сервер модулдарды азырынча өзгөртө албайт.",
            "This server cannot change modules yet.", "Bu sunucu henüz modülleri değiştiremiyor.", "Bu server modullarni hali o'zgartira olmaydi."),
        _ => r.Detail ?? Tr.T("Не удалось изменить модуль. Повторите позже.", "Модулду өзгөртүү мүмкүн болбоду. Кийинчерээк аракет кылыңыз.",
            "Could not change the module. Try again later.", "Modül değiştirilemedi. Daha sonra tekrar deneyin.", "Modulni o'zgartirib bo'lmadi. Keyinroq urinib ko'ring."),
    };

    // ───────────────────────────── для тестов ─────────────────────────────

    /// <summary>Только для тестов: подставить ответ сервера для текущей компании (без запроса и записи на диск).</summary>
    internal static void SetForTests(CompanyModulesSnapshot? snap, string? seenProfile = null)
    {
        if (snap is null)
        {
            _cur = null;
            _diskTriedFor = AccountKey();
            return;
        }
        _cur = new Entry { Account = AccountKey(), Snap = snap, SeenProfile = seenProfile };
        _diskTriedFor = AccountKey();
    }

    internal static string? SeenProfileForTests => _cur?.SeenProfile;

    internal static void ResetForTests()
    {
        Reset();
        _unsupportedUntilUtc = DateTime.MinValue;
        _lastIssue = "";
    }
}
