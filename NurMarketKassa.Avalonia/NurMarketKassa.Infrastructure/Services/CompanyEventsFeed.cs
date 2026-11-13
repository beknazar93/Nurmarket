using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (ответ Умара: общая лента компании GET api/main/changes/?since=&lt;курсор&gt;&amp;types=a,b&amp;limit=500): «что-то изменилось» — вместо того чтобы ждать такта.
/// Минимально и безопасно: один лёгкий опрос (ETag/304; свой такт <see cref="Interval"/>, в тихом режиме — свёрнуто/простой/Android в фоне — ×4), в событиях берётся только ТИП
/// из списка <see cref="Types"/> (данные событий не читаются и не применяются), а реакция — «толкнуть» уже существующие механизмы, не дублируя их логику:
/// • features_changed — тариф/функции: AccessSync (профиль, платные функции, компания и тариф);
/// • staff_changed — доступы сотрудников: AccessSync (профиль и платные функции);
/// • app_settings_changed — общие настройки компании: SettingsCloudSync.KickCompanyPoll;
/// • product_changed, stock_changed — лента товаров products/changes/ (SyncCenter.FeedKick), своя логика ленты товаров не повторяется.
/// Остальные типы не запрашиваются. Без since сервер отдаёт текущий курсор и пустой список — отсчёт с этого момента. Курсор хранится по компании и филиалу
/// (company-events-cursor.json, принадлежит компании). Курсор старше 7 дней (400 cursor_expired) или испорченный (400 invalid_cursor) — начинаем заново без since,
/// а затронутое перечитываем целиком (все четыре реакции сразу). Старый сервер (404/405) — повтор через 10 минут. Любая ошибка ленты — пауза, работе кассы не мешает.</summary>
public static class CompanyEventsFeed
{
    /// <summary>Какие типы событий просит касса (остальное ей не нужно).</summary>
    public static readonly string[] Types = ["features_changed", "staff_changed", "app_settings_changed", "product_changed", "stock_changed"];

    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);
    public static int PageLimit { get; set; } = 500;
    public const int MaxPagesPerTick = 4;
    private const string FileName = "company-events-cursor.json";

    /// <summary>Клиент API; не задан — тот же, что у бонусов (ServerLoyalty.ApiProvider, ставит App при запуске).</summary>
    public static Func<NurMarketApiClient?>? ApiProvider { get; set; }

    /// <summary>Только для тестов: другая папка данных вместо %APPDATA%.</summary>
    public static string? DirOverride { get; set; }

    /// <summary>Только для тестов: «сейчас» (UTC) для пауз.</summary>
    public static Func<DateTime>? NowUtcOverride { get; set; }

    /// <summary>Только для тестов: подмена реакций (по умолчанию — Kick-и механизмов).</summary>
    internal static Action<string>? ReactOverride { get; set; }

    private static DateTime NowUtc => NowUtcOverride?.Invoke() ?? DateTime.UtcNow;

    private sealed class Store
    {
        public string Scope { get; set; } = "";
        public string? Cursor { get; set; }
        public string? ETag { get; set; }
        public string? ETagCursor { get; set; }
        public DateTime AdvancedUtc { get; set; }
    }

    /// <summary>Сколько держим курсор на месте при пустых страницах: ETag привязан к курсору запроса, и только неподвижный курсор даёт 304 (как у ленты товаров).
    /// Не двигать курсор безопасно: новые события придут с номерами позже него; не дольше этого срока — сервер хранит события 7 дней.</summary>
    public static readonly TimeSpan CursorFreeze = TimeSpan.FromMinutes(30);

    private static readonly object Gate = new();
    private static Store? _store;
    private static DateTime _pausedUntilUtc = DateTime.MinValue;
    private static int _started;
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private static string StorePath => Path.Combine(DirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName), FileName);

    private static NurMarketApiClient? Api =>
        (ApiProvider ?? ServerLoyalty.ApiProvider)?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    /// <summary>Итог одного опроса (для журнала и тестов).</summary>
    public sealed record TickResult(int Requests, int Events, bool Reset, string Note, IReadOnlyCollection<string> Reacted);

    /// <summary>Запустить фоновый опрос (один раз за запуск; вызывает AccessSync.Start после входа в компанию).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await SyncCenter.WaitAdaptiveAsync(() => SyncCenter.QuietInterval(Interval), new SyncCenter.KickSignal(), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    if (!SyncCenter.ShouldPause() && !ServerOutageMonitor.IsOutage)
                        await TickAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.LogThrottled("company-events-loop-" + ex.GetType().Name, $"EVENTS: сбой цикла ленты событий: {ex.GetType().Name}: {ex.Message}", "EVENTS");
                }
            }
        });
    }

    /// <summary>Один опрос: читает страницы ленты, пока сервер говорит has_more, и вызывает реакции. Не бросает (кроме отмены): ошибка = пауза.</summary>
    public static async Task<TickResult> TickAsync(CancellationToken ct = default)
    {
        var reacted = new SortedSet<string>(StringComparer.Ordinal);
        var requests = 0;
        var events = 0;
        if (NowUtc < _pausedUntilUtc)
            return new TickResult(0, 0, false, "пауза", reacted);
        if (Api is not { } api || string.IsNullOrWhiteSpace(CompanyInfoService.LastCompany?.Id))
            return new TickResult(0, 0, false, "нет входа", reacted);
        if (!await OneAtATime.WaitAsync(0, ct).ConfigureAwait(false))
            return new TickResult(0, 0, false, "уже идёт", reacted);
        try
        {
            var scope = AppPaths.CompanyKey(CompanyInfoService.LastCompany!.Id) + "|" + (api.ActiveBranchId ?? "");
            var store = Load(scope);
            for (var page = 0; page < MaxPagesPerTick; page++)
            {
                ct.ThrowIfCancellationRequested();
                var query = new Dictionary<string, string> { ["types"] = string.Join(",", Types), ["limit"] = PageLimit.ToString(CultureInfo.InvariantCulture) };
                var since = store.Cursor;
                if (!string.IsNullOrEmpty(since))
                    query["since"] = since;
                var tag = !string.IsNullOrEmpty(since) && string.Equals(store.ETagCursor, since, StringComparison.Ordinal) ? store.ETag : null;
                requests++;
                (JsonElement body, int status, string? etag) response;
                try
                {
                    response = await api.GetConditionalAsync("api/main/changes/", query, tag, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.StatusCode == 400 && IsCursorProblem(ex))
                {
                    // Курсор забыт (7 дней) или испорчен: начинаем заново (следующий опрос — без since), а затронутое перечитываем целиком.
                    PosLogger.Log("EVENTS: курсор ленты событий устарел — начинаем заново, затронутое перечитывается целиком.", "EVENTS");
                    Save(store, scope, cursor: null, etag: null, etagCursor: null, advanced: NowUtc);
                    foreach (var t in Types)
                        reacted.Add(t);
                    React(reacted);
                    return new TickResult(requests, events, true, "cursor_expired", reacted);
                }

                if (response.status == 304)
                    return new TickResult(requests, events, false, "304", reacted);
                var root = response.body;
                var next = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(next))
                    throw new InvalidOperationException("ответ ленты событий без курсора");
                var hasMore = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
                // Без since сервер отвечает текущим курсором и пустым списком — только запоминаем курсор. Типы берём лишь из нашего списка (сервер уже отфильтровал).
                var pageEvents = 0;
                if (!string.IsNullOrEmpty(since) && root.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in list.EnumerateArray())
                    {
                        events++;
                        pageEvents++;
                        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
                            && ty.GetString() is { } type && Array.IndexOf(Types, type) >= 0)
                            reacted.Add(type);
                    }
                }

                var keepCursor = pageEvents == 0 && !hasMore && !string.IsNullOrEmpty(since) && NowUtc - store.AdvancedUtc < CursorFreeze;
                Save(store, scope, keepCursor ? since : next, response.etag, since, keepCursor ? store.AdvancedUtc : NowUtc);
                if (!hasMore || string.Equals(next, since, StringComparison.Ordinal))
                    break;
            }

            if (reacted.Count > 0)
                React(reacted);
            return new TickResult(requests, events, false, events > 0 ? "события" : "", reacted);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 405 or 501)
        {
            _pausedUntilUtc = NowUtc.AddMinutes(10); // сервер без ленты — как раньше (опрос профиля и функций по такту)
            PosLogger.LogThrottled("company-events-unsupported", $"EVENTS: лента событий компании недоступна ({ex.StatusCode}) — повтор через 10 минут.", "EVENTS");
            return new TickResult(requests, events, false, "не поддерживается", reacted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _pausedUntilUtc = NowUtc.AddSeconds(60);
            PosLogger.LogThrottled("company-events-" + ex.GetType().Name, $"EVENTS: лента событий не прочитана ({ex.GetType().Name}: {ex.Message}) — пауза 60 с.", "EVENTS");
            return new TickResult(requests, events, false, "ошибка", reacted);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static bool IsCursorProblem(ApiException ex)
    {
        var text = (ex.Payload?.ToString() ?? "") + " " + ex.Message;
        return text.Contains("cursor_expired", StringComparison.OrdinalIgnoreCase) || text.Contains("invalid_cursor", StringComparison.OrdinalIgnoreCase);
    }

    private static void React(IReadOnlyCollection<string> kinds)
    {
        if (ReactOverride is { } over)
        {
            foreach (var k in kinds)
                over(k);
            return;
        }
        try
        {
            if (kinds.Contains("features_changed"))
                AccessSync.KickCompany("events");
            else if (kinds.Contains("staff_changed"))
                AccessSync.Kick("events"); // только профиль и платные функции
            if (kinds.Contains("app_settings_changed"))
                SettingsCloudSync.KickCompanyPoll();
            if (kinds.Contains("product_changed") || kinds.Contains("stock_changed"))
            {
                SyncCenter.BoostFeed();
                SyncCenter.FeedKick.Kick(); // лента товаров products/changes/ читает изменения сама
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"EVENTS: реакция на событие не сработала ({ex.Message}).", "WARNING");
        }
    }

    // ---------------------------------------------------------------- курсор на диске

    private static Store Load(string scope)
    {
        lock (Gate)
        {
            if (_store is null)
            {
                try
                {
                    var text = File.Exists(StorePath) ? AtomicFile.ReadAllText(StorePath) : null;
                    _store = string.IsNullOrWhiteSpace(text) ? new Store() : JsonSerializer.Deserialize<Store>(text) ?? new Store();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _store = new Store();
                }
            }
            if (!string.Equals(_store.Scope, scope, StringComparison.Ordinal))
                _store = new Store { Scope = scope }; // другая компания/филиал — курсор чужой
            return _store;
        }
    }

    private static void Save(Store store, string scope, string? cursor, string? etag, string? etagCursor, DateTime advanced)
    {
        lock (Gate)
        {
            store.AdvancedUtc = advanced;
            store.Scope = scope;
            store.Cursor = cursor;
            store.ETag = etag;
            store.ETagCursor = etagCursor;
            try
            {
                AtomicFile.WriteAllText(StorePath, JsonSerializer.Serialize(store));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"EVENTS: курсор ленты событий не сохранён ({ex.Message}).", "WARNING");
            }
        }
    }

    /// <summary>Текущий курсор (для тестов и журнала).</summary>
    internal static string? CursorForTests { get { lock (Gate) return _store?.Cursor; } }

    /// <summary>Только для тестов: забыть состояние в памяти.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
            _store = null;
        _pausedUntilUtc = DateTime.MinValue;
        ReactOverride = null;
        ApiProvider = null;
    }
}
