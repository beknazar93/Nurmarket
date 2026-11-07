using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-27 (Б-46 сервера, CATALOG_FEED): каталог по ленте изменений. Жалоба «на складе 195, а касса продаёт 205»: «версия каталога»
/// (ordering=-updated_at) цену не замечает, и другие кассы до 15–30 минут продают по старой цене. Лента
/// GET api/main/products/changes/?since=&lt;курсор&gt;&amp;limit=500 отдаёт только изменённые (и удалённые) товары; опрос раз в ~8 секунд
/// (ETag / 304 — ничего нового). Изменённые товары пишутся в локальную базу (схема pos_local.db не менялась), плитки и поиск
/// обновляются сразу (NotifyCatalogChanged) — цена на плитке меняется за секунды.
///
/// Безопасность:
/// • сервер ленту ещё может не выложить — 404/405/501/непонятный ответ = «не поддерживается»: касса молчит и живёт по-старому (полная
///   загрузка по таймеру 15–30 мин остаётся страховкой), через 10 минут проверяет ещё раз;
/// • 429 — пауза не меньше 30 секунд (ApiThrottle); сеть/5xx — пауза и тихий повтор;
/// • курсор хранится в своём JSON (catalog_feed_cursor.json в папке данных программы: у кассы и у программы владельца он свой) вместе
///   с «ключом области» (аккаунт|филиал|адрес сервера): сменился аккаунт или филиал — начинаем с начала;
/// • остаток из ленты НЕ применяется к товарам, у которых на этом ПК есть незавершённые операции (неотправленный офлайн-чек, продажа соседней
///   кассы по LAN, правка остатка менее 90 с назад) — сервер ещё не знает этих продаж, и остаток «откатился» бы. 2026-10-29: решение по каждому
///   товару (FeedStockGuard), а не одним выключателем на весь каталог, — остальные товары получают серверный остаток за период ленты.
/// </summary>
public sealed class CatalogFeedSync
{
    /// <param name="Ok">2026-10-29: сервер ответил на опрос (304, пустая страница или изменения) — лента жива. Раньше «жива» считалась только при 304 или
    /// изменениях, а пустая страница (первый опрос после изменений: ETag ещё не привязан к новому курсору) её не подтверждала.</param>
    public sealed record TickResult(int Added, int Changed, int Deleted, bool NeedFullSync, string Note, bool Ok = false)
    {
        public bool Applied => Added + Changed + Deleted > 0;
        public static TickResult None(string note) => new(0, 0, 0, false, note);
    }

    private sealed class StoreDto
    {
        public string? Scope { get; set; }
        public string? Cursor { get; set; }
        public string? ETag { get; set; }
        public string? ETagCursor { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    /// <summary>Файл курсора. Стенд подменяет, чтобы не трогать настоящий профиль.</summary>
    public static string StorePath { get; set; } = Path.Combine(
        NurMarketKassa.Services.AppPaths.Roaming,
        AppMode.DataFolderName,
        "catalog_feed_cursor.json");

    /// <summary>Не больше стольких страниц (по 500 товаров) за один опрос: первый проход по большому каталогу не должен держать поток минутами.</summary>
    public const int MaxPagesPerTick = 40;
    public const int PageLimit = 500;
    public static readonly TimeSpan UnsupportedRetry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ThrottlePause = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(30);

    private readonly ICatalogFeedApi _api;
    private readonly Func<string> _apiBaseUrl;
    private readonly Func<string> _accountKey;
    private readonly Func<bool> _allowStock;
    private readonly Func<Func<string, bool>>? _stockGuardFactory;

    private bool _loaded;
    private string? _scope;
    private string? _cursor;
    private string? _etag;
    private string? _etagCursor;
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private string? _lastLoggedProblem;
    private DateTime _cursorAdvancedUtc = DateTime.UtcNow;

    /// <summary>Сколько держим курсор неподвижным на пустых страницах (чтобы работал 304 по ETag).</summary>
    public static readonly TimeSpan CursorFreeze = TimeSpan.FromMinutes(10);

    /// <param name="accountKey">Ключ аккаунта (LastCatalogUserKey): другой аккаунт — другой курсор.</param>
    /// <param name="allowStock">Можно ли применять остаток из ленты (см. описание класса).</param>
    /// <param name="stockGuardFactory">2026-10-29: на каждый проход ленты даёт предикат «остаток ЭТОГО товара из ленты применять можно»
    /// (см. <see cref="FeedStockGuard"/>). null — прежнее поведение: решает только общий <paramref name="allowStock"/>.</param>
    public CatalogFeedSync(ICatalogFeedApi api, Func<string> apiBaseUrl, Func<string> accountKey, Func<bool> allowStock,
        Func<Func<string, bool>>? stockGuardFactory = null)
    {
        _stockGuardFactory = stockGuardFactory;
        _api = api;
        _apiBaseUrl = apiBaseUrl;
        _accountKey = accountKey;
        _allowStock = allowStock;
    }

    public string? Cursor => _cursor;

    /// <summary>2026-10-30 (приёмка 1.17.66, «Важно» 8): сохранённый курсор этого аккаунта/филиала/сервера есть (catalog_feed_cursor.json) — каталог при запуске можно
    /// дочитать лентой (дельта), а не качать целиком.</summary>
    public bool HasSavedCursor()
    {
        EnsureLoaded();
        return !string.IsNullOrWhiteSpace(_cursor);
    }

    /// <summary>2026-10-30: последний опрос показал, что сервер ленту не поддерживает (404/405/501…) — тогда каталог при запуске качается по-старому.</summary>
    public bool Unsupported { get; private set; }

    public bool IsPaused => DateTime.UtcNow < _pausedUntilUtc;

    /// <summary>Смена аккаунта: курсор и ETag забываются (и на диске тоже).</summary>
    public void Reset()
    {
        _cursor = null;
        _etag = null;
        _etagCursor = null;
        _scope = null;
        _loaded = true;
        _cursorAdvancedUtc = DateTime.UtcNow;
        _pausedUntilUtc = DateTime.MinValue;
        try { if (File.Exists(StorePath)) File.Delete(StorePath); }
        catch (Exception ex) { PosLogger.Log($"CATALOG_FEED: файл курсора не удалён: {ex.Message}", "CATALOG_FEED"); }
    }

    /// <summary>2026-10-30 (первая загрузка лентой): каталог уже получен лентой до этого курсора — опрос продолжает с него, а не читает
    /// всю ленту (16 тыс. товаров) второй раз.</summary>
    public void AdoptCursor(string cursor)
    {
        EnsureLoaded();
        _cursor = cursor;
        _etag = null;
        _etagCursor = null;
        _cursorAdvancedUtc = DateTime.UtcNow;
        Save();
    }

    /// <summary>Один опрос: читает страницы ленты, пока сервер говорит has_more, применяет к локальному каталогу и сдвигает курсор.
    /// Не бросает (кроме отмены): любая ошибка = тихая пауза и прежняя логика полной загрузки.</summary>
    /// <param name="stillValid">Спрашивается прямо перед записью в базу: false (сменился аккаунт, пошла полная загрузка) — страница не применяется
    /// и курсор не двигается, её отдаст следующий опрос.</param>
    public async Task<TickResult> TickAsync(Func<bool>? stillValid = null, CancellationToken ct = default)
    {
        if (!ServerPriceContract.CatalogFeedEnabled)
            return TickResult.None("лента выключена настройкой");
        if (IsPaused)
            return TickResult.None("пауза");
        var block = ApiThrottle.RemainingBlock;
        if (block > TimeSpan.Zero)
            return TickResult.None($"сервер просит паузу {block.TotalSeconds:0} с");

        EnsureLoaded();
        int added = 0, changed = 0, deleted = 0;
        var needFull = false;
        var pages = 0;
        var notes = "";
        var ok = false;
        try
        {
            var pageCursor = _cursor; // 2026-11-07: с какого места читаем очередную страницу (курсор хранения при этом никогда не идёт назад)
            while (pages < MaxPagesPerTick)
            {
                ct.ThrowIfCancellationRequested();
                pages++;
                var requested = pageCursor;
                // 2026-11-07 (живой тест, Б-П2): первая страница такта читается «с курсора минус запас» (по умолчанию 8 с): сервер отдаёт курсор = своё время ответа, а запись изменения
                // товара (приход) могла зафиксироваться на ~11 мс позже — товар не попадал в ответ, а курсор уходил дальше, и касса продавала по старой цене до полной загрузки.
                // Вторая и следующие страницы идут с настоящим курсором страницы (иначе при 500+ изменений за секунды чтение не двигалось бы). Повторно присланный товар безвреден:
                // запись в базу только при отличии (ApplyFeedChanges), остаток по-прежнему под защитой FeedStockGuard.
                var lookback = pages == 1 ? Lookback : TimeSpan.Zero;
                var sent = ShiftBack(requested, lookback);
                var etag = string.Equals(_etagCursor, sent, StringComparison.Ordinal) ? _etag : null;
                var page = await _api.ProductsChangesAsync(sent, PageLimit, etag, ct).ConfigureAwait(false);

                if (page.Status == CatalogFeedStatus.Unsupported)
                {
                    Unsupported = true;
                    Pause(UnsupportedRetry, $"лента не поддерживается сервером ({page.Note}) — прежняя логика, повтор через {UnsupportedRetry.TotalMinutes:0} мин");
                    break;
                }

                if (page.Status == CatalogFeedStatus.NotModified)
                {
                    notes = "304";
                    ok = true;
                    Unsupported = false;
                    Interlocked.Increment(ref _stat304);
                    ClearProblem();
                    break;
                }

                ok = true;
                Unsupported = false;
                if (page.Items.Count == 0 && page.DeletedIds.Count == 0)
                    Interlocked.Increment(ref _statEmpty);
                ClearProblem();
                if ((page.Items.Count > 0 || page.DeletedIds.Count > 0) && string.IsNullOrWhiteSpace(page.NextCursor))
                {
                    // Без курсора не продвинуться: применили бы одно и то же каждые 8 секунд.
                    Pause(UnsupportedRetry, "ответ ленты без курсора — прежняя логика");
                    break;
                }

                if ((page.Items.Count > 0 || page.DeletedIds.Count > 0) && stillValid is not null && !stillValid())
                {
                    notes = "применение отложено (идёт полная загрузка или смена аккаунта)";
                    break;
                }

                if (page.Items.Count > 0 || page.DeletedIds.Count > 0)
                {
                    var r = Apply(page, _apiBaseUrl(), _allowStock(), _stockGuardFactory?.Invoke());
                    added += r.Added;
                    changed += r.Changed;
                    deleted += r.Deleted;
                    needFull |= r.NeedFullSync;
                    // 2026-11-07: повторы из запаса курсора (ничего не изменилось в базе) в журнал не пишем — иначе после каждого изменения шли бы лишние строки.
                    if (lookback <= TimeSpan.Zero || r.Added + r.Changed + r.Deleted > 0 || r.NeedFullSync)
                        PosLogger.Log(
                            $"CATALOG_FEED: страница {pages}: {page.Note}; в базе добавлено {r.Added}, изменено {r.Changed}, удалено {r.Deleted}"
                            + (r.NeedFullSync ? "; есть товары, которых нет локально — нужна полная загрузка" : ""),
                            "CATALOG_FEED");
                }

                var moved = !string.IsNullOrWhiteSpace(page.NextCursor) && !string.Equals(page.NextCursor, requested, StringComparison.Ordinal);
                // 2026-10-29 (замер скорости): на пустой странице сервер возвращает курсор = своё текущее время, поэтому каждый опрос шёл с новым
                // since и ETag никогда не совпадал (304 не бывало вовсе). Пустая страница курсор НЕ двигает (ничего не пропущено: после requested
                // изменений нет, а следующий опрос по-прежнему спрашивает всё после requested), но не дольше CursorFreeze — старый курсор сервер мог забыть.
                var emptyPage = page.Items.Count == 0 && page.DeletedIds.Count == 0 && !page.HasMore;
                if (moved && emptyPage && requested is not null && DateTime.UtcNow - _cursorAdvancedUtc < CursorFreeze)
                    moved = false;
                if (moved)
                {
                    _cursor = Newer(_cursor, page.NextCursor);
                    pageCursor = page.NextCursor;
                    _cursorAdvancedUtc = DateTime.UtcNow;
                }
                // ETag относится к ответу на запрос с курсором sent (с запасом); уйдёт вместе с ним, пока курсор не изменился.
                _etag = page.ETag;
                _etagCursor = sent;
                Save();

                if (!page.HasMore || !moved)
                    break;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode == 429)
        {
            var wait = ApiThrottle.RemainingBlock > ThrottlePause ? ApiThrottle.RemainingBlock : ThrottlePause;
            Pause(wait, $"сервер ответил 429 — пауза {wait.TotalSeconds:0} с", failure: false);
        }
        catch (ApiException ex) when (ex.StatusCode == 410)
        {
            // 2026-10-30 (15 тыс. товаров): 410 cursor_expired — сервер забыл курсор: изменения за этот период могли пропасть. Курсор сбрасываем
            // (следующий такт — с начала ленты) и просим одну полную сверку каталога; раньше 410 шёл в «ошибку» и повторялся с тем же курсором вечно.
            Reset();
            needFull = true;
            Pause(ErrorPause, "сервер забыл курсор ленты (410 cursor_expired): курсор сброшен, нужна полная сверка");
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 403)
        {
            // Сервер отказал в самом запросе (например, курсор не принят): пробуем с начала, но не чаще раза в 10 минут.
            Reset();
            Pause(UnsupportedRetry, $"сервер отказал ({ex.StatusCode}): курсор сброшен, повтор через {UnsupportedRetry.TotalMinutes:0} мин");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException or JsonException)
        {
            Pause(ErrorPause, $"ошибка {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Лента — помощь каталогу, а не условие работы кассы: любой сбой = пауза.
            Pause(ErrorPause, $"сбой {ex.GetType().Name}: {ex.Message}");
        }

        LogStatsIfDue();
        if (ok)
            ProductCodeBackfill.ScheduleFromStorage(); // 2026-11-06 (класс Б, Б-7): медленное самолечение товаров без кодов на уровне хранилища (раз в 30 с, ≤ 20 карточек)
        return new TickResult(added, changed, deleted, needFull, notes, ok);
    }

    /// <summary>2026-11-07 (Б-П2): запас назад для курсора ленты (настройка server_prices.json, feed_lookback_sec; 0 — выключен).</summary>
    public static TimeSpan Lookback => TimeSpan.FromSeconds(ServerPriceContract.FeedLookbackSeconds);

    /// <summary>Курсор сдвинут назад на <paramref name="back"/>, если он — время в эпохе (13 цифр — миллисекунды, как у сервера NurCRM; 10 — секунды). Непрозрачный курсор не трогаем.</summary>
    internal static string? ShiftBack(string? cursor, TimeSpan back)
    {
        if (back <= TimeSpan.Zero || string.IsNullOrWhiteSpace(cursor) || !long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return cursor;
        var shifted = cursor.Length switch
        {
            13 => value - (long)back.TotalMilliseconds,
            10 => value - (long)Math.Ceiling(back.TotalSeconds),
            _ => -1L,
        };
        return shifted < 0 ? cursor : shifted.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Курсор хранения не идёт назад: новый числовой курсор, не больше прежнего, прежний не заменяет. Непрозрачные курсоры — как раньше (новый).</summary>
    internal static string? Newer(string? current, string? next)
    {
        if (string.IsNullOrWhiteSpace(next))
            return current;
        if (long.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var a) && long.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b <= a)
            return current;
        return next;
    }

    // ---------------------------------------------------------------- 2026-11-07 (живой тест, Б-П2): страховочная сверка «недавно изменённых»

    /// <summary>Сколько последних изменённых товаров сверяем (сервер отдаёт страницу не больше 100).</summary>
    public const int RecentLimit = 100;

    /// <param name="Ok">Сервер ответил и сверка выполнена (в т. ч. «исправлять нечего»).</param>
    /// <param name="Checked">Сколько карточек пришло от сервера.</param>
    /// <param name="Fixed">Сколько товаров в базе кассы было неверно (добавлено + изменено + удалено) и исправлено.</param>
    public sealed record RecentResult(bool Ok, int Checked, int Fixed, string Note)
    {
        public static RecentResult Skipped(string note) => new(false, 0, 0, note);
    }

    /// <summary>Когда последняя сверка недавних прошла успешно (UTC); MinValue — не было.</summary>
    public DateTime LastRecentUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Страховка к ленте: берём у сервера последние <see cref="RecentLimit"/> изменённых товаров (products/list/?ordering=-updated_at) и применяем к базе теми же правилами, что страницу ленты
    /// (защита остатка FeedStockGuard, локальные поля сокращённой карточки). Запись в базу — только при отличии, поэтому сверка идемпотентна. Не бросает (кроме отмены).
    /// <paramref name="manual"/> — по просьбе кассира: итог пишется в журнал всегда, иначе — только если что-то исправлено.</summary>
    public async Task<RecentResult> ReconcileRecentAsync(Func<bool>? stillValid = null, bool manual = false, CancellationToken ct = default)
    {
        if (!ServerPriceContract.CatalogFeedEnabled)
            return RecentResult.Skipped("лента выключена настройкой");
        if (ApiThrottle.RemainingBlock > TimeSpan.Zero)
            return RecentResult.Skipped("сервер просит паузу");
        try
        {
            var rows = await _api.ProductsRecentAsync(RecentLimit, ct).ConfigureAwait(false);
            if (rows is null)
                return RecentResult.Skipped("сервер не отдаёт последние изменённые");
            if (rows.Count > 0 && stillValid is not null && !stillValid())
                return RecentResult.Skipped("применение отложено (идёт полная загрузка или смена аккаунта)");
            var fixedCount = 0;
            if (rows.Count > 0)
            {
                var page = new CatalogFeedPage { Status = CatalogFeedStatus.Ok, Items = rows, Note = $"недавних {rows.Count}" };
                var r = Apply(page, _apiBaseUrl(), _allowStock(), _stockGuardFactory?.Invoke());
                fixedCount = r.Added + r.Changed + r.Deleted;
            }

            LastRecentUtc = DateTime.UtcNow;
            if (manual || fixedCount > 0)
                PosLogger.Log($"CATALOG: сверка недавних — исправлено {fixedCount} (проверено {rows.Count}).", "CATALOG");
            return new RecentResult(true, rows.Count, fixedCount, "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Сверка — помощь ленте, а не условие работы кассы: сбой = тихий пропуск (не чаще одной записи в 10 минут на причину).
            PosLogger.LogThrottled("recent-sync-" + ex.GetType().Name, $"CATALOG: сверка недавних не удалась: {ex.GetType().Name}: {ex.Message}", "CATALOG");
            return RecentResult.Skipped(ex.GetType().Name);
        }
    }

    // 2026-10-29 (скорость синхронизации): раз в 5 минут одна строка — сколько опросов закончились 304 (сервер не собирал ответ) и сколько пустым 200.
    // Видно, что If-None-Match без W/ работает (304 появляется со второго опроса без изменений).
    private static int _stat304, _statEmpty;
    private static long _statLoggedTicks = DateTime.UtcNow.Ticks;

    private static void LogStatsIfDue()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _statLoggedTicks) < TimeSpan.FromMinutes(5).Ticks)
            return;
        Interlocked.Exchange(ref _statLoggedTicks, now);
        var n304 = Interlocked.Exchange(ref _stat304, 0);
        var empty = Interlocked.Exchange(ref _statEmpty, 0);
        // 2026-10-30: рядом — число всех запросов к NurCRM за период и в минуту (до этого частоту считали только по коду).
        var requests = SyncCenter.TakeRequestCount();
        if (n304 + empty > 0 || requests > 0)
            PosLogger.Log($"CATALOG_FEED: за 5 минут опросов без изменений: 304 — {n304}, пустых 200 — {empty}; запросов к серверу — {requests} (≈{requests / 5.0:0.#} в минуту).", "CATALOG_FEED");
    }

    /// <summary>2026-11-02 (Н3-01): когда лента в последний раз сорвалась по-настоящему (ошибка, 410, отказ, «не поддерживается»), — 429 («сервер занят») не в счёт.
    /// Страховочная полная загрузка каталога (AvaloniaCatalogCacheService.FullReloadInterval) делается редко, только пока лента давно без ошибок.</summary>
    public DateTime LastFailureUtc => _lastFailureUtc;

    private DateTime _lastFailureUtc = DateTime.MinValue;

    private void Pause(TimeSpan span, string why, bool failure = true)
    {
        _pausedUntilUtc = DateTime.UtcNow + span;
        if (failure)
            _lastFailureUtc = DateTime.UtcNow;
        // В журнал — только когда причина новая: опрос идёт каждые 8 секунд, одинаковые строки не нужны.
        if (!string.Equals(_lastLoggedProblem, why, StringComparison.Ordinal))
        {
            _lastLoggedProblem = why;
            PosLogger.Log($"CATALOG_FEED: {why}.", "CATALOG_FEED");
        }
    }

    private void ClearProblem()
    {
        if (_lastLoggedProblem != null)
            PosLogger.Log("CATALOG_FEED: лента снова отвечает.", "CATALOG_FEED");
        _lastLoggedProblem = null;
    }

    private string CurrentScope() => $"{_accountKey()}|{_api.ScopeKey}|{_apiBaseUrl()}";

    private void EnsureLoaded()
    {
        var scope = CurrentScope();
        if (_loaded && string.Equals(_scope, scope, StringComparison.Ordinal))
            return;

        _loaded = true;
        _scope = scope;
        _cursorAdvancedUtc = DateTime.UtcNow;
        _cursor = null;
        _etag = null;
        _etagCursor = null;
        try
        {
            if (!File.Exists(StorePath))
                return;
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(StorePath));
            if (dto is not null && string.Equals(dto.Scope, scope, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(dto.Cursor))
            {
                _cursor = dto.Cursor;
                _etag = dto.ETag;
                _etagCursor = dto.ETagCursor;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG_FEED: файл курсора не прочитан ({ex.Message}) — начинаем сначала.", "CATALOG_FEED");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new StoreDto
            {
                Scope = _scope,
                Cursor = _cursor,
                ETag = _etag,
                ETagCursor = _etagCursor,
                SavedUtc = DateTime.UtcNow,
            });
            AtomicFile.WriteAllText(StorePath, json); // 2026-11-01 (Н2-01/Н-07): сброс на диск + копия .bak
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG_FEED: курсор не сохранён: {ex.Message}", "CATALOG_FEED");
        }
    }

    // ---------------------------------------------------------------- применение страницы

    public sealed record ApplyOutcome(int Added, int Changed, int Deleted, bool NeedFullSync);

    private static readonly string[] NameKeys = ["name", "title", "display_name", "label"];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> StockSkipLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-11-01 (расследование «остаток не меняется», п. 6): остаток товара из ленты не применён (защита: очередь неотправленных чеков / недавняя продажа) —
    /// строка в журнале, не чаще одного раза в минуту на товар.</summary>
    private static void NoteStockSkipped(string id)
    {
        StockSyncService.ScheduleStockReread(id, FeedStockGuard.LocalTouchWindow + TimeSpan.FromSeconds(5)); // 2026-11-01 (приёмка С-4/С-5): пропущенное изменение остатка перечитаем, когда защита снимется
        var now = Environment.TickCount64;
        if (StockSkipLogged.TryGetValue(id, out var last) && now - last < 60_000)
            return;
        StockSkipLogged[id] = now;
        if (StockSkipLogged.Count > 2000)
            StockSkipLogged.Clear();
        PosLogger.Log($"STOCK: лента: остаток товара {id} не применён (защита: очередь/недавняя продажа).", "STOCK");
    }

    /// <summary>Применить страницу ленты к локальной базе. Карточка товара (есть название) — как при полной загрузке; без названия — только
    /// присланные поля (цена, закупка, опт, остаток) поверх записи из базы. Товар, которого нет локально и карточки нет, — просьба о полной загрузке.</summary>
    /// <param name="allowStockFor">2026-10-29: решение по товару (id → можно ли применить остаток из ленты); null — решает только allowStock.</param>
    public static ApplyOutcome Apply(CatalogFeedPage page, string apiBaseUrl, bool allowStock, Func<string, bool>? allowStockFor = null)
    {
        var repo = LocalProductRepository.Instance;
        var tiles = new List<CatalogProductTileVm>();
        var patches = new List<LocalProductRepository.FeedFieldPatch>();
        List<string>? archivedIds = null;
        List<CatalogProductTileVm>? newWithoutCodes = null;
        List<string>? newShortIds = null;
        foreach (var item in page.Items)
        {
            var id = ProductCatalogMapper.TryId(item);
            if (string.IsNullOrEmpty(id))
                continue;
            // 2026-11-01 (контрольный аудит, С2-11): товар отправлен в архив на сервере — убираем его из каталога кассы (не показываем и не продаём).
            if (ProductCatalogMapper.IsArchived(item))
            {
                (archivedIds ??= new List<string>()).Add(id);
                continue;
            }
            var stockOk = allowStock && (allowStockFor?.Invoke(id) ?? true);
            if (!stockOk)
                NoteStockSkipped(id); // 2026-11-01 (расследование «остаток не меняется», п. 6): диагностика
            if (HasName(item))
            {
                var tile = ProductCatalogMapper.TryTile(item, apiBaseUrl);
                if (tile is null)
                    continue;
                var local = repo.LoadTileCopy(id);
                // 2026-10-27 (ревью, К-11): карточка ленты может быть сокращённой (без packages / alternate_barcodes / plu / kind) — тогда эти поля
                // остаются локальными, а не затираются пустыми до следующей полной загрузки (пропадали поштучная продажа, доп. штрихкоды, PLU весов).
                if (local is not null)
                    KeepLocalForAbsentFields(item, tile, local);
                else if (NewProductCardBackfill.IsShortFeedCard(item))
                    (newShortIds ??= new List<string>()).Add(tile.Id); // 2026-11-06 (класс Б, Б-1): новый товар в сокращённой карточке — полную (единица, вид, фасовка, категория, PLU) дочитаем с сервера
                else if (ProductCodeBackfill.NeedsCodes(tile))
                    (newWithoutCodes ??= new List<CatalogProductTileVm>()).Add(tile); // 2026-11-06: новый товар из сокращённой ленты — штрихкод/код дочитаем с сервера
                // Остаток из ленты нельзя — оставляем локальный (его уже уменьшили свои/соседние продажи).
                if (!stockOk && local is not null)
                    StockSyncService.ApplyQuantityToTile(tile, local.Quantity, tile.MustWeigh);
                tiles.Add(tile);
                continue;
            }

            double? price = ProductCatalogMapper.TryPrice(item) is { } p && double.IsFinite(p) && p > 0 ? p : null;
            patches.Add(new LocalProductRepository.FeedFieldPatch(
                id,
                price,
                ReadMoney(item, "purchase_price"),
                ReadMoney(item, "wholesale_price"),
                stockOk ? ReadStock(item) : null));
        }

        PromotionRules.SaveIfDirty();
        var result = repo.ApplyFeedChanges(tiles, patches, archivedIds is null ? page.DeletedIds : page.DeletedIds.Concat(archivedIds).ToList());
        // 2026-11-06 (жалоба «Код / штрихкод пустой»): товар, которого в базе не было, лента принесла без штрихкода/артикула/кода — полную карточку читаем в фоне и дописываем пустое.
        if (newWithoutCodes is { Count: > 0 })
            ProductCodeBackfill.ScheduleForRows(newWithoutCodes);
        if (newShortIds is { Count: > 0 } && result.Added > 0)
            NewProductCardBackfill.Enqueue(newShortIds);
        return new ApplyOutcome(result.Added, result.Changed, result.Deleted, result.MissingIds.Count > 0);
    }

    /// <summary>2026-10-27 (ревью, К-11): поля, которых в присланной карточке нет вовсе (ключа нет — не «пустой список»), берём из локальной записи.
    /// Ключ есть и пуст (сервер сознательно очистил) — применяется как пришло.</summary>
    internal static void KeepLocalForAbsentFields(JsonElement item, CatalogProductTileVm tile, CatalogProductTileVm local)
    {
        // 2026-11-01 (живая проверка 1.17.72, Б-2): сокращённая карточка ленты после продажи без единицы и признака «весовой» превращала весовой товар
        // в штучный («шт.», пропадало окно взвешивания). Нет НИ ОДНОГО ключа единицы/веса — берём единицу и признак из локальной записи
        // (ключ пришёл — применяется как пришло, сервер мог сознательно сменить единицу).
        // 2026-11-01 (приёмка Ф-4): ключи type/product_type/sale_kind в список не входят — они бывают в сокращённой карточке и без единицы; учитываем их, только если значение говорит о весе.
        if (!HasAnyKey(item, UnitAndWeightKeys) && !CartDisplayHelper.ProductMustWeigh(item))
        {
            tile.MustWeigh = local.MustWeigh;
            tile.Unit = local.Unit;
            StockSyncService.ApplyQuantityToTile(tile, tile.Quantity, tile.MustWeigh); // подпись остатка («кг»/«шт.») по восстановленному признаку
        }

        if (!item.TryGetProperty("kind", out _))
        {
            tile.Kind = local.Kind;
            tile.IsBundle = local.IsBundle;
            tile.BundleItems = local.BundleItems;
        }

        if (!item.TryGetProperty("packages", out _) && !tile.IsBundle)
            tile.PieceOption = local.PieceOption;
        if (!item.TryGetProperty("alternate_barcodes", out _))
        {
            tile.AlternateBarcodesRaw = local.AlternateBarcodesRaw;
            tile.AlternateBarcodeVariants = local.AlternateBarcodeVariants;
        }

        if (!item.TryGetProperty("plu", out _))
            tile.Plu = local.Plu;

        // 2026-10-29 (приёмка 1.17.64, Б-1): сокращённая карточка ленты без штрихкода/артикула/кода затирала их пустыми —
        // скан после прихода или правки цены не находил товар («Товар не найден» → дубль). Ключа нет — берём локальное значение;
        // ключ пришёл (даже пустой) — применяется как пришло. Штрихкод у маппера берётся из barcode/sku/article/code/ean,
        // поэтому «нет ключа штрихкода» = нет barcode, sku и ean (article/code могли подставить чужое значение).
        if (!item.TryGetProperty("barcode", out _) && !item.TryGetProperty("sku", out _) && !item.TryGetProperty("ean", out _)
            && !string.IsNullOrWhiteSpace(local.Barcode))
            tile.Barcode = local.Barcode;
        if (!item.TryGetProperty("article", out _) && !item.TryGetProperty("code", out _))
            tile.Article = local.Article;
        if (!item.TryGetProperty("code", out _))
            tile.ProductCode = local.ProductCode;

        // Остальные поля карточки, которых в сокращённой карточке нет (иначе форма редактирования стирала бы их при сохранении).
        if (!item.TryGetProperty("purchase_price", out _))
            tile.PurchasePrice = local.PurchasePrice;
        if (!item.TryGetProperty("markup_percent", out _))
            tile.MarkupPercent = local.MarkupPercent;
        if (!item.TryGetProperty("description", out _))
            tile.Description = local.Description;
        if (!item.TryGetProperty("is_weight", out _))
            tile.ServerIsWeight = local.ServerIsWeight; // 2026-11-01 (is_weight): сокращённая карточка признак не затирает
        if (!item.TryGetProperty("wholesale_price", out _))
            tile.WholesalePrice = local.WholesalePrice;
        if (!item.TryGetProperty("discount_percent", out _))
            tile.DiscountPercent = local.DiscountPercent;
        if (!item.TryGetProperty("country", out _))
            tile.Country = local.Country;
        if (!item.TryGetProperty("weight_kg", out _))
            tile.WeightKg = local.WeightKg;
        if (!item.TryGetProperty("minimum_quantity", out _))
            tile.MinQuantity = local.MinQuantity; // 2026-11-06 (В12, Б-8)
        if (!item.TryGetProperty("category", out _))
            tile.Category = local.Category;
        if (!item.TryGetProperty("brand", out _))
            tile.Brand = local.Brand;

        // 2026-11-06 (Б-1, И-3): в сокращённой карточке ленты нет ни картинки, ни горячей клавиши — фото и клавиша товара стирались при любой правке цены/остатка
        // (отсюда жалоба «фото не попадают в кассу»). Ключа нет — берём локальное значение; ключ пришёл (даже пустой) — применяется как пришло.
        if (!HasAnyKey(item, ImageKeys) && string.IsNullOrWhiteSpace(tile.ImageUrl) && !string.IsNullOrWhiteSpace(local.ImageUrl))
            tile.ImageUrl = local.ImageUrl;
        if (!item.TryGetProperty("hotkey_group", out _))
            tile.HotkeyGroup = local.HotkeyGroup;
    }

    /// <summary>2026-11-06 (И-3): ключи, из которых ProductImageUrl собирает адрес фото.</summary>
    private static readonly string[] ImageKeys =
    [
        "primary_image_url", "image_url", "thumbnail_url", "photo_url", "picture_url", "main_image_url", "cover_url", "preview_url", "image_path", "media_url",
        "image", "photo", "thumbnail", "picture", "cover", "img", "primary_image", "main_image", "cover_image", "image_data", "photo_data",
        "images", "photos", "gallery", "media", "attachments",
    ];

    /// <summary>2026-11-06 (Б-1, И-1): в карточке есть хоть один ключ единицы/«весовой» (у сокращённой карточки ленты их нет).</summary>
    public static bool HasUnitOrWeightKey(JsonElement item) => HasAnyKey(item, UnitAndWeightKeys);

    /// <summary>2026-11-01 (Б-2): ключи, по которым карточка определяет единицу и «весовой» (см. CartDisplayHelper.ProductMustWeigh / ProductCatalogMapper).</summary>
    private static readonly string[] UnitAndWeightKeys =
    [
        "is_wait", "is_weigh", "is_weight", "is_weight_product", "sale_as_weight", "sells_by_weight",
        "unit", "unit_display", "measure_unit", "sale_unit", "uom", "unit_code", "uom_code", "sale_unit_code",
        "measurement_unit", "primary_unit", "default_unit", "base_unit", "pricing_unit", "stock_unit", "weight_unit",
    ];

    private static bool HasAnyKey(JsonElement item, string[] keys)
    {
        foreach (var key in keys)
            if (item.TryGetProperty(key, out _))
                return true;
        return false;
    }

    private static bool HasName(JsonElement item)
    {
        foreach (var key in NameKeys)
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return true;
        return false;
    }

    private static double? ReadMoney(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0)
            return d;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) && x >= 0)
            return x;
        return null;
    }

    private static double? ReadStock(JsonElement item)
    {
        foreach (var key in new[] { "quantity", "stock", "stock_quantity" })
            if (item.TryGetProperty(key, out _))
                return ReadMoney(item, key);
        return null;
    }
}
