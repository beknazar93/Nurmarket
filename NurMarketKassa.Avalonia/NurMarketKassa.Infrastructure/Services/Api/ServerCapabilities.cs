using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-01 (контрольное ревью): ОДИН запрос GET api/main/capabilities/, один разбор и один кэш для всех признаков сервера — receipt_delete, receipt_edit(+_discount, _sale_price),
/// showcase_* и т. д. (раньше SupplierApiService и ShowcaseCapabilities спрашивали отдельно и по-разному разбирали). Кэш 10 минут (ошибка запроса — 1 минута) и ПРИВЯЗАН К АККАУНТУ:
/// ключ = LastDataAccountKey + id компании; после смены аккаунта/выхода (<see cref="Reset"/>, AccountDataIsolation) чужие признаки не показываются. Разбор понимает массив строк,
/// объект «имя → true», вложенное features / capabilities; неожиданная форма ответа — один раз WARNING в журнал.</summary>
public static class ServerCapabilities
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(1);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    // 2026-11-06 (аудит №5, Л5-01): Stale = список остался с прошлого раза (последний известный): последнее обновление не удалось или список взят с диска.
    // Ok = список пригоден для Has()/Cached(). Сбой обновления НЕ заменяет хороший список пустым.
    private static (DateTime At, string Account, HashSet<string> Features, bool Ok, bool Stale)? _cache;
    private static string? _diskTriedFor;
    private static bool _shapeWarned;
    private static Exception? _lastError;

    private static string AccountKey() => (UserPreferences.Instance.LastDataAccountKey ?? "") + "|" + (CompanyInfoService.LastCompany?.Id ?? "");

    /// <summary>Забыть признаки (смена аккаунта, выход).</summary>
    public static void Reset()
    {
        _cache = null;
        _diskTriedFor = null; // 2026-11-06: после смены аккаунта список нового аккаунта снова ищется на диске
    }

    // 2026-11-05 (сервер ceba67c): имена новых признаков — в одном месте.
    public const string DebtReturnDrawerCash = "debt_return_drawer_cash";
    public const string CheckoutBranch = "checkout_branch";
    public const string CheckoutLinesMerged = "checkout_lines_merged";
    public const string CheckoutBelowCostWarning = "checkout_below_cost_warning";
    public const string WeightLineTotalFloor = "weight_line_total_floor";
    public const string ReceiptDelete = "receipt_delete";
    public const string ReceiptVersionRequired = "receipt_version_required";
    public const string RentalIdempotency = "rental_idempotency";
    public const string ProductsArchivedFilter = "products_archived_filter";
    public const string IdempotencyLookupBonusTransfers = "idempotency_lookup_bonus_transfers";
    /// <summary>2026-11-06: сервер отдаёт каталог тем и видов кассы Маркетплейса (api/main/marketplace/themes/, пакеты по праву «куплено»). Без признака касса показывает только свои темы.</summary>
    public const string MarketplaceThemes = "marketplace_themes";
    /// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): сервер умеет править/аннулировать возврат поставщику по PIN руководителя
    /// (поля can_edit/editable_until, PATCH, void, history). Контракт предложен нами (docs/Отчеты/«ТЗ Умару — правка и аннулирование возвратных накладных 24 ч.md»), сервером не подтверждён.</summary>
    public const string SupplierReturnEdit24h = "supplier_return_edit_24h";
    /// <summary>2026-11-10: то же для возврата от клиента (pos/returns).</summary>
    public const string PosReturnEdit24h = "pos_return_edit_24h";

    /// <summary>2026-11-05: синхронная проверка признака для мест, где ждать сеть нельзя (расчёт «Касса:», тело чека). true — признак есть у ЭТОГО аккаунта; нет списка, он устарел
    /// или запрос не удался — false (поведение «как раньше»). Если список не получен или старше срока, в фоне просит его заново (ServerAddons.Api — тот же клиент, что у бонусов).</summary>
    public static bool Has(string name)
    {
        var known = Cached(name);
        if (!Fresh(out _)) // список ещё не получен, устарел или прошлый запрос не удался (раз в минуту) — в фоне запросить заново; сама проверка не ждёт
            RefreshInBackground(force: false);
        return known == true;
    }

    private static int _bgRunning;

    private static void RefreshInBackground(bool force)
    {
        if (ServerLoyalty.ApiProvider?.Invoke() is not { } api || string.IsNullOrEmpty(api.AccessToken))
            return;
        if (Interlocked.Exchange(ref _bgRunning, 1) == 1)
            return;
        _ = Task.Run(async () =>
        {
            try { await LoadAsync(api, force: force).ConfigureAwait(false); }
            catch { /* LoadAsync сам пишет в журнал */ }
            finally { Volatile.Write(ref _bgRunning, 0); }
        });
    }

    /// <summary>Загрузить список заранее — после входа и при смене аккаунта (сервер ceba67c отдаёт признаки только с токеном: до входа — 401, пустой список после 401 не считается «признаков нет»,
    /// а повторяется), чтобы Has() сразу знал ответ. Запрос всегда заново, а не из кэша неудачи.</summary>
    public static void WarmUp() => RefreshInBackground(force: true);

    /// <summary>Только для тестов: подставить список признаков для текущего аккаунта.</summary>
    internal static void SetFeaturesForTests(params string[] names)
    {
        _cache = (DateTime.UtcNow, AccountKey(), new HashSet<string>(names, StringComparer.OrdinalIgnoreCase), true, false);
        _diskTriedFor = AccountKey(); // тест ставит список сам — с диска ничего не подмешиваем
    }

    /// <summary>Только для тестов: сбросить память (файл на диске остаётся) — как перезапуск кассы.</summary>
    internal static void ResetMemoryForTests()
    {
        _cache = null;
        _diskTriedFor = null;
        _lastError = null;
    }

    /// <summary>Только для тестов: папка файла признаков (по умолчанию AppPaths.Local\NurMarketKassa — общая для кассы и программы владельца).</summary>
    internal static string? DirOverride { get; set; }

    /// <summary>Только для тестов: список в памяти — «последний известный» (обновление не удалось или взят с диска).</summary>
    internal static bool IsStaleForTests => _cache is { Stale: true };

    private static bool Fresh(out (DateTime At, string Account, HashSet<string> Features, bool Ok, bool Stale) c)
    {
        TryLoadFromDisk();
        if (_cache is { } cur && cur.Account == AccountKey() && DateTime.UtcNow - cur.At < (cur.Ok && !cur.Stale ? Ttl : FailTtl))
        {
            c = cur;
            return true;
        }
        c = default;
        return false;
    }

    /// <summary>Без запроса: true/false, если список уже получен для ЭТОГО аккаунта; null — ещё не спрашивали или запрос не удался.</summary>
    public static bool? Cached(string name)
    {
        TryLoadFromDisk();
        return _cache is { Ok: true } c && c.Account == AccountKey() ? c.Features.Contains(name) : null;
    }

    // ───────────── 2026-11-06 (аудит №5, Л5-01): последний известный список — на диске, по аккаунту ─────────────

    private static string FilePath => Path.Combine(DirOverride ?? AppPaths.KassaLocal, "server-capabilities.json");

    private static bool HasAccount(string account) => account.Trim('|').Length > 0;

    private static string DiskKey(string account) => AppPaths.ServerKeyPrefix + account;

    /// <summary>Если в памяти списка этого аккаунта нет — берёт последний известный с диска (помечает устаревшим: в фоне всё равно запросится заново). Так сбой при запуске или нет связи
    /// не выключают денежные признаки (weight_line_total_floor, checkout_branch, debt_return_drawer_cash). Ключ записи = сервер (для стенда) + аккаунт и компания.</summary>
    private static void TryLoadFromDisk()
    {
        var account = AccountKey();
        if (_diskTriedFor == account || !HasAccount(account))
            return;
        _diskTriedFor = account;
        if (_cache is { } cur && cur.Account == account && cur.Ok)
            return;
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return;
            var text = AtomicFile.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
                return;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Object
                || !accounts.TryGetProperty(DiskKey(account), out var entry) || entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("features", out var list) || list.ValueKind != JsonValueKind.Array)
                return;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(list, set);
            _cache = (DateTime.MinValue, account, set, true, true);
            PosLogger.Log($"Признаки сервера (capabilities): взят последний известный список с диска ({set.Count} шт.), обновление запрошено заново.", "INFO");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            PosLogger.Log($"Признаки сервера (capabilities): файл с последним списком не прочитан ({ex.GetType().Name}).", "WARNING");
        }
    }

    private static void SaveToDisk(string account, HashSet<string> set)
    {
        if (!HasAccount(account))
            return;
        try
        {
            var path = FilePath;
            var accounts = new System.Text.Json.Nodes.JsonObject();
            try
            {
                if (File.Exists(path) && AtomicFile.ReadAllText(path) is { Length: > 0 } old
                    && System.Text.Json.Nodes.JsonNode.Parse(old) is System.Text.Json.Nodes.JsonObject oldRoot
                    && oldRoot["accounts"] is System.Text.Json.Nodes.JsonObject oldAccounts)
                {
                    foreach (var kv in oldAccounts.ToArray()) // не больше 8 записей: новая поверх, лишние старые выбрасываются
                        if (accounts.Count < 7 && kv.Key != DiskKey(account))
                            accounts[kv.Key] = kv.Value?.DeepClone();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { /* битый файл — перепишем с нуля */ }
            var features = new System.Text.Json.Nodes.JsonArray();
            foreach (var f in set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                features.Add(f);
            accounts[DiskKey(account)] = new System.Text.Json.Nodes.JsonObject { ["saved_at"] = DateTime.UtcNow.ToString("O"), ["features"] = features };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, new System.Text.Json.Nodes.JsonObject { ["accounts"] = accounts }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            PosLogger.Log($"Признаки сервера (capabilities): список не записан на диск ({ex.GetType().Name}).", "WARNING");
        }
    }

    public static async Task<bool> HasAsync(NurMarketApiClient api, string name, CancellationToken ct = default) =>
        (await LoadAsync(api, ct).ConfigureAwait(false)).Contains(name);

    /// <summary>2026-11-01 (контрольный аудит, редактор сайта): как <see cref="LoadAsync"/>, но сбой запроса (таймаут, 5xx, нет сети) возвращается ошибкой,
    /// а не пустым набором: «признака нет» (ответ получен) и «сервер недоступен» экран показывает по-разному. Сбой не выдаётся за «нет возможности»:
    /// при <paramref name="force"/> («Повторить») запрос уходит заново.</summary>
    public static async Task<(IReadOnlySet<string> Features, Exception? Error)> TryLoadAsync(NurMarketApiClient api, CancellationToken ct = default, bool force = false)
    {
        var set = await LoadAsync(api, ct, force).ConfigureAwait(false);
        return (set, _cache is { Ok: false } or { Stale: true } ? _lastError : null);
    }

    public static async Task<IReadOnlySet<string>> LoadAsync(NurMarketApiClient api, CancellationToken ct = default, bool force = false)
    {
        if (!force && Fresh(out var fresh))
            return fresh.Features;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && Fresh(out var again))
                return again.Features;
            var account = AccountKey();
            try
            {
                var d = await api.RequestAsync(HttpMethod.Get, "api/main/capabilities/", null, null, ct).ConfigureAwait(false);
                var set = Parse(d);
                _cache = (DateTime.UtcNow, account, set, true, false);
                _lastError = null;
                SaveToDisk(account, set); // 2026-11-06 (Л5-01): ответ получен — это новый «последний известный» список (в том числе пустой: сервер реально ответил без признаков)
                return set;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Признаки сервера (capabilities) не получены: {ex.Message}", "WARNING");
                _lastError = ex;
                // 2026-11-06 (аудит №5, Л5-01): сбой обновления НЕ заменяет хороший список пустым. Раньше «мигнувший» checkout_branch менял тело оплаты (и ключ повтора),
                // а weight_line_total_floor — правило округления. Держим последний известный список этого аккаунта (память или диск), помечаем устаревшим — повтор через минуту.
                TryLoadFromDisk();
                if (_cache is { Ok: true } good && good.Account == account)
                {
                    _cache = (DateTime.UtcNow, account, good.Features, true, true);
                    return good.Features;
                }

                var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _cache = (DateTime.UtcNow, account, empty, false, false);
                return empty;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Разбор ответа: массив строк, объект «имя → true», вложенные features / capabilities (массив или объект). Пустой результат или чужая форма — WARNING один раз.</summary>
    internal static HashSet<string> Parse(JsonElement d)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recognized = false;
        if (d.ValueKind == JsonValueKind.Array)
        {
            Collect(d, set);
            recognized = true;
        }
        else if (d.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "features", "capabilities" })
                if (d.TryGetProperty(key, out var part) && part.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    Collect(part, set);
                    recognized = true;
                }
            Collect(d, set); // и признаки прямо в объекте: {"receipt_edit": true}
            recognized |= set.Count > 0;
        }
        if ((!recognized || set.Count == 0) && !_shapeWarned)
        {
            _shapeWarned = true;
            var raw = d.ValueKind == JsonValueKind.Undefined ? "" : d.GetRawText();
            PosLogger.Log($"Признаки сервера (capabilities): неожиданная форма ответа или пустой список ({d.ValueKind}): {(raw.Length > 300 ? raw[..300] + "…" : raw)}", "WARNING");
        }
        return set;
    }

    private static void Collect(JsonElement part, HashSet<string> set)
    {
        if (part.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in part.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s)
                    set.Add(s);
        }
        else if (part.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in part.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.True)
                    set.Add(p.Name);
        }
    }
}
