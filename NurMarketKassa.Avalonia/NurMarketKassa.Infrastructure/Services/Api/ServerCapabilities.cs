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
    private static (DateTime At, string Account, HashSet<string> Features, bool Ok)? _cache;
    private static bool _shapeWarned;
    private static Exception? _lastError;

    private static string AccountKey() => (UserPreferences.Instance.LastDataAccountKey ?? "") + "|" + (CompanyInfoService.LastCompany?.Id ?? "");

    /// <summary>Забыть признаки (смена аккаунта, выход).</summary>
    public static void Reset() => _cache = null;

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
    internal static void SetFeaturesForTests(params string[] names) =>
        _cache = (DateTime.UtcNow, AccountKey(), new HashSet<string>(names, StringComparer.OrdinalIgnoreCase), true);

    private static bool Fresh(out (DateTime At, string Account, HashSet<string> Features, bool Ok) c)
    {
        if (_cache is { } cur && cur.Account == AccountKey() && DateTime.UtcNow - cur.At < (cur.Ok ? Ttl : FailTtl))
        {
            c = cur;
            return true;
        }
        c = default;
        return false;
    }

    /// <summary>Без запроса: true/false, если список уже получен для ЭТОГО аккаунта; null — ещё не спрашивали или запрос не удался.</summary>
    public static bool? Cached(string name) =>
        _cache is { Ok: true } c && c.Account == AccountKey() ? c.Features.Contains(name) : null;

    public static async Task<bool> HasAsync(NurMarketApiClient api, string name, CancellationToken ct = default) =>
        (await LoadAsync(api, ct).ConfigureAwait(false)).Contains(name);

    /// <summary>2026-11-01 (контрольный аудит, редактор сайта): как <see cref="LoadAsync"/>, но сбой запроса (таймаут, 5xx, нет сети) возвращается ошибкой,
    /// а не пустым набором: «признака нет» (ответ получен) и «сервер недоступен» экран показывает по-разному. Сбой не выдаётся за «нет возможности»:
    /// при <paramref name="force"/> («Повторить») запрос уходит заново.</summary>
    public static async Task<(IReadOnlySet<string> Features, Exception? Error)> TryLoadAsync(NurMarketApiClient api, CancellationToken ct = default, bool force = false)
    {
        var set = await LoadAsync(api, ct, force).ConfigureAwait(false);
        return (set, _cache is { Ok: false } ? _lastError : null);
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
                _cache = (DateTime.UtcNow, account, set, true);
                _lastError = null;
                return set;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Признаки сервера (capabilities) не получены: {ex.Message}", "WARNING");
                var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _cache = (DateTime.UtcNow, account, empty, false);
                _lastError = ex;
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
