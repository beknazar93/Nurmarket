using System.Text.Json;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Этот файл предназначен для работы с локальным каталогом товаров в Avalonia-кассе:
/// загрузка из SQLite, полная синхронизация с REST API сайта и кэширование плиток каталога в памяти.
/// </summary>
public sealed class AvaloniaCatalogCacheService : ICatalogCacheService
{
    private readonly ICatalogApiService _catalogApi;
    private readonly AppSettings _settings;
    private readonly MySqlAuditService _auditDb;
    private List<CatalogProductTileVm> _products = [];

    public AvaloniaCatalogCacheService(
        ICatalogApiService catalogApi,
        AppSettings settings,
        MySqlAuditService auditDb)
    {
        _catalogApi = catalogApi;
        _settings = settings;
        _auditDb = auditDb;
        // 2026-10-29: «Обновить» (SyncCenter.ForceSyncAllAsync) и сигнал «в чеках компании что-то изменилось» (RemoteSalesPulse) — тоже сюда.
        SyncCenter.CatalogRefresher = ForceRefreshAsync;
        RemoteSalesPulse.Detected += OnRemoteSalesDetected;
        // 2026-10-30 (15 тыс. товаров): окна (Склад, массовая загрузка, ревизия, правка товара) обновляют каталог через этот объект — одна
        // полная загрузка на программу и лента вместо полной, когда она жива (см. SharedRefreshAsync).
        CatalogCacheService.SharedRefresh = SharedRefreshAsync;
        CatalogCacheService.RequestBackgroundFullReload = RequestBackgroundFullReload;
    }

    /// <summary>2026-10-29: принудительное обновление каталога и остатков: лента изменений (если есть), затем полная загрузка.
    /// Полная загрузка идёт в фоне и UI не блокирует (запись плиток — через UiDispatcherHolder).</summary>
    private async Task ForceRefreshAsync(CancellationToken ct)
    {
        EnsureFeedLoop();
        if (_feed is { } feed && !OfflineModeHelper.SellLocally && !IsFullSyncRunning())
        {
            var tick = await TickFeedAsync(feed, ct).ConfigureAwait(false);
            // 2026-10-30 (15 тыс. товаров): пока идёт массовая загрузка карточек, «Обновить» не качает весь каталог (155 страниц под 429) —
            // изменения приносит лента.
            if (BulkLoadActive && FeedHealthy && HasLocalCatalog && !tick.NeedFullSync)
            {
                SyncCenter.RecordSuccess("catalog");
                return;
            }
        }

        var result = await SyncCatalogFullAsync(ct).ConfigureAwait(false);
        if (result.Success)
            SyncCenter.RecordSuccess("catalog");
    }

    // ---------------------------------------------------------------- 2026-10-30 (15 тыс. товаров, журнал NBS 30.10)
    // Владелец загрузил 15 104 карточки; каталог вырос до 15 452 товаров (155 страниц по 100). Полная загрузка шла ~3–4 мин и падала на первом 429
    // (сервер отбивал массовую загрузку: 1 828 ответов «слишком часто» за час), следующая начиналась со страницы 1. Статический путь окон
    // (Склад «Обновить» — «Загружаю товары с сервера… 178 с», массовая загрузка перед «Проверить» и после, правка товара) качал каталог мимо
    // общего «одна полная загрузка за раз» — до трёх одновременно. Теперь: (1) окна идут сюда; (2) лента жива и каталог в базе есть — лента
    // (1–2 запроса), а не 155 страниц; (3) пока идёт массовая загрузка карточек — полной загрузки нет вовсе, только лента; (4) неудачная полная
    // загрузка не повторяется раньше чем через 10 минут (раньше — каждые 2 минуты фонового цикла, снова с нуля).

    private readonly SemaphoreSlim _feedGate = new(1, 1);
    private DateTime _lastFullFailedUtc = DateTime.MinValue;
    private static readonly TimeSpan FailedFullRetry = TimeSpan.FromMinutes(10);

    private static bool BulkLoadActive => NurMarketKassa.Services.BulkCardsLoader.IsActive;

    private bool HasLocalCatalog => _products.Count > 0 || CatalogCacheService.Products.Count > 0;

    /// <summary>Один такт ленты за раз: цикл ленты и «Обновить» окон не читают одну страницу дважды и не двигают курсор наперегонки.</summary>
    /// <param name="insideFullSync">2026-10-30: вызов из самой «полной» загрузки (тёплый старт) — её собственный статус «идёт» применению ленты не мешает.</param>
    private async Task<CatalogFeedSync.TickResult> TickFeedAsync(CatalogFeedSync feed, CancellationToken ct, bool insideFullSync = false)
    {
        await _feedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref _accountGeneration);
            var tick = await feed.TickAsync(() => generation == Volatile.Read(ref _accountGeneration) && (insideFullSync || !IsFullSyncRunning()), ct).ConfigureAwait(false);
            if (tick.Applied || tick.Note == "304" || tick.Ok)
            {
                Interlocked.Exchange(ref _lastFeedOkTicks, DateTime.UtcNow.Ticks);
                SyncCenter.RecordSuccess("feed");
                CatalogCacheService.NoteSynced(DateTime.UtcNow);
            }

            if (tick.Applied && generation == Volatile.Read(ref _accountGeneration))
            {
                // Плитки, поиск и «Склад» берут товары из базы: перечитать и сказать экранам.
                TryLoadFromDatabase();
                CatalogCacheService.NotifyCatalogChanged();
                // 2026-10-29: телефонам в магазине — «в каталоге есть изменения» (они сами прочитают ленту с NurCRM).
                NurMarketKassa.Services.PhoneLink.PhoneLinkEvents.Publish("product");
            }

            return tick;
        }
        finally
        {
            _feedGate.Release();
        }
    }

    /// <summary>2026-10-30: «обновить каталог» из окон. Лента жива и каталог в базе есть — только лента; иначе одна общая полная загрузка.</summary>
    private async Task<CatalogSyncResult> SharedRefreshAsync(CancellationToken ct)
    {
        var result = await SharedRefreshCoreAsync(ct).ConfigureAwait(false);
        // Прежний статический путь возвращался, когда CatalogCacheService.Products уже обновлён (InvokeAsync); здесь список
        // обновляется через Post — дожидаемся его, чтобы окно сразу перестроило таблицу по новым данным.
        await UiDispatcherHolder.InvokeAsync(() => { }).ConfigureAwait(false);
        return result;
    }

    private async Task<CatalogSyncResult> SharedRefreshCoreAsync(CancellationToken ct)
    {
        if (OfflineModeHelper.SellLocally)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");
        EnsureFeedLoop();
        if (_feed is { } feed && FeedHealthy && HasLocalCatalog && !IsFullSyncRunning())
        {
            var tick = await TickFeedAsync(feed, ct).ConfigureAwait(false);
            if (!tick.NeedFullSync)
                return CatalogSyncResult.Ok(tick.Added, tick.Changed, tick.Deleted);
            if (BulkLoadActive)
            {
                PosLogger.Log("CATALOG: лента принесла неизвестный товар во время массовой загрузки — полная загрузка после её окончания.", "CATALOG");
                return CatalogSyncResult.Ok(tick.Added, tick.Changed, tick.Deleted);
            }
        }
        else if (HasLocalCatalog && (BulkLoadActive || IsFullSyncRunning()))
        {
            // Лента сейчас не отвечает (пауза 429), но каталог в базе есть: во время массовой загрузки полную загрузку не начинаем;
            // полная уже идёт в фоне — окно её 3 минуты не ждёт (показывает базу; изменения после её снимка принесёт лента).
            return CatalogSyncResult.Ok(0, 0, 0);
        }

        return await StartFullSync(ct, knownVersion: null, ignoreFresh: true).ConfigureAwait(false);
    }

    /// <summary>2026-10-30 (холодный старт: чистая установка и вход в аккаунт с 16 тыс. товаров). Раньше касса ~3 минуты (155 страниц) показывала
    /// пустой каталог: товары попадали в базу только после последней страницы, а при обрыве — ни одного. Теперь каждые ~500 товаров (или 3 с)
    /// загруженная часть пишется в базу (без удаления — удаления сверит итоговая загрузка) и появляется на плитках, в поиске и скане; скан
    /// ещё не загруженного товара находит его на сервере (BasketPanelViewModel → поиск по штрихкоду). Обрыв на середине — загруженное остаётся.</summary>
    private Action<List<JsonElement>> ColdStartPageHandler(int generation)
    {
        var apiBaseUrl = _settings.ApiBaseUrl;
        var pending = new List<CatalogProductTileVm>();
        var lastPublish = Environment.TickCount64;
        var written = 0;
        return batch =>
        {
            try
            {
                if (generation != Volatile.Read(ref _accountGeneration))
                    return;
                foreach (var el in batch)
                {
                    if (ProductCatalogMapper.TryTile(el, apiBaseUrl) is not { } vm)
                        continue;
                    if (string.IsNullOrWhiteSpace(vm.StockInfo))
                        StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
                    pending.Add(vm);
                }

                if (pending.Count < 500 && Environment.TickCount64 - lastPublish < 3000)
                    return;
                LocalProductRepository.Instance.UpsertFromTiles(pending);
                written += pending.Count;
                pending.Clear();
                lastPublish = Environment.TickCount64;
                TryLoadFromDatabase();
                CatalogCacheService.NotifyCatalogChanged();
                PosLogger.Log($"CATALOG: первая загрузка — в базе уже {written} товаров (можно искать и продавать по скану).", "CATALOG");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"CATALOG: первая загрузка — часть не записана ({ex.GetType().Name}: {ex.Message}), запишется в конце.", "CATALOG");
            }
        };
    }

    /// <summary>2026-10-30 (холодный старт на 16 тыс.): первая загрузка каталога лентой изменений с начала (since пусто, limit=500) —
    /// 32 запроса вместо 160 страниц по 100. 429 — пауза сервера и повтор с ТОГО ЖЕ курсора (не с нуля). Лента не поддерживается, ошибка
    /// или отдала заметно меньше товаров, чем «версия каталога» (count), — null: тогда прежняя загрузка по страницам (загруженное уже в базе).
    /// Карточки ленты могут быть сокращёнными (без упаковок, доп. штрихкодов) — их допишет страховочная полная загрузка (раз в час).</summary>
    private async Task<(List<JsonElement>? Items, string? Cursor)> TryFeedSnapshotAsync(CatalogVersionInfo? remoteVersion, int generation, CancellationToken ct)
    {
        if (_catalogApi is not ICatalogFeedApi feedApi || !ServerPriceContract.CatalogFeedEnabled)
            return (null, null);
        int? expected = remoteVersion?.CatalogVersion is { } cv and > 0 and < int.MaxValue ? (int)cv
            : remoteVersion?.Token is { } tok && int.TryParse(tok.Split('|')[0], out var tc) && tc > 0 ? tc : null;
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new List<JsonElement?>();
        string? cursor = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (var page = 1; page <= 200; page++)
            {
                if (generation != Volatile.Read(ref _accountGeneration))
                    return (null, null);
                CatalogFeedPage? p = null;
                for (var attempt = 1; p is null; attempt++)
                {
                    try
                    {
                        p = await feedApi.ProductsChangesAsync(cursor, CatalogFeedSync.PageLimit, null, ct).ConfigureAwait(false);
                    }
                    catch (ApiException e) when (e.StatusCode == 429 && attempt < 12 && !ct.IsCancellationRequested)
                    {
                        var pause = ApiThrottle.RemainingBlock;
                        if (pause < TimeSpan.FromSeconds(Math.Min(attempt, 10)))
                            pause = TimeSpan.FromSeconds(Math.Min(attempt, 10));
                        await Task.Delay(pause, ct).ConfigureAwait(false);
                    }
                }

                if (p.Status != CatalogFeedStatus.Ok)
                {
                    PosLogger.Log($"CATALOG: первая загрузка лентой не вышла ({p.Status} {p.Note}) — загрузка по страницам.", "CATALOG");
                    return (null, null);
                }

                var batch = new List<JsonElement>(p.Items.Count);
                foreach (var item in p.Items)
                {
                    var id = ProductCatalogMapper.TryId(item);
                    if (string.IsNullOrEmpty(id))
                        continue;
                    batch.Add(item);
                    if (byId.TryGetValue(id, out var at))
                        items[at] = item; // тот же товар менялся ещё раз, пока шла загрузка — берём позднюю карточку
                    else
                    {
                        byId[id] = items.Count;
                        items.Add(item);
                    }
                }

                foreach (var del in p.DeletedIds)
                    if (byId.Remove(del, out var at))
                        items[at] = null;

                CatalogApiService.ReportFullLoad(byId.Count, expected, batch);
                if (!string.IsNullOrWhiteSpace(p.NextCursor))
                    cursor = p.NextCursor;
                if (!p.HasMore || string.IsNullOrWhiteSpace(p.NextCursor))
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
        {
            PosLogger.Log($"CATALOG: первая загрузка лентой прервалась ({ex.GetType().Name}: {ex.Message}) — загрузка по страницам (загруженное уже в базе).", "CATALOG");
            return (null, null);
        }

        var result = items.Where(i => i is not null).Select(i => i!.Value).ToList();
        if (expected is { } want && result.Count < want * 0.98)
        {
            PosLogger.Log($"CATALOG: лента отдала {result.Count} из {want} товаров — загрузка по страницам.", "CATALOG");
            return (null, null);
        }

        PosLogger.Log($"CATALOG: первая загрузка лентой — {result.Count} товаров за {watch.Elapsed.TotalSeconds:0} с (страниц по {CatalogFeedSync.PageLimit}).", "CATALOG");
        return (result, cursor);
    }

    /// <summary>2026-10-30: «Обновить» на Складе — полная сверка в фоне, если её давно не было (10 мин) и не идёт массовая загрузка.</summary>
    private Task<CatalogSyncResult>? RequestBackgroundFullReload()
    {
        if (OfflineModeHelper.SellLocally || BulkLoadActive)
            return null;
        DateTime last;
        lock (_syncGate)
        {
            if (_inFlightSync is { IsCompleted: false } running)
                return running;
            last = _lastSuccessfulSyncUtc;
        }

        if (DateTime.UtcNow - last < TimeSpan.FromMinutes(10))
            return null;
        return StartFullSync(CancellationToken.None, knownVersion: null);
    }

    /// <summary>2026-10-29: в чеках компании появилось изменение (чужая продажа, возврат, отмена) — остатки перечитываются полной загрузкой, но
    /// не чаще <see cref="SyncCenter.StockReloadMinSpacing"/> и не для «своей» продажи (остаток здесь уже уменьшен сразу).</summary>
    private void OnRemoteSalesDetected(bool probablyOwn)
    {
        if (probablyOwn || OfflineModeHelper.SellLocally || IsFullSyncRunning())
            return;
        // 2026-10-29 (скорость синхронизации): остаток чужой продажи приходит лентой (сервер меняет updated_at при продаже) — читаем только ленту,
        // а не весь каталог (4+ тяжёлых страницы, до ~15 с работы сервера на большом каталоге, раз в минуту на каждую кассу и программу владельца).
        // Второй такт через 2,5 с — сервер мог дописать продажу чуть позже чека. Лента недоступна — прежний путь (полная загрузка).
        if (FeedHealthy)
        {
            SyncCenter.FeedKick.Kick();
            _ = Task.Delay(TimeSpan.FromMilliseconds(2500)).ContinueWith(_ => SyncCenter.FeedKick.Kick(), TaskScheduler.Default);
            return;
        }

        if (DateTime.UtcNow - _lastSuccessfulSyncUtc < SyncCenter.StockReloadMinSpacing)
            return;
        PosLogger.Log("SYNC: чужая продажа/возврат — перечитываю остатки каталога.", "SYNC");
        _ = StartFullSync(CancellationToken.None, knownVersion: null);
    }

    public bool TryLoadFromDatabase()
    {
        try
        {
            LocalProductRepository.Instance.EnsureSchema();
            var tiles = LocalProductRepository.Instance.LoadAllTiles();
            // 2026-10-30 (оптимизация кассы, Б-дельта): после каждой оплаты кэш репозитория отдаёт ТЕ ЖЕ объекты плиток (остаток правится на месте,
            // PatchStockInCache) — копировать 15 000 ссылок и перезаливать CatalogCacheService.Products на UI-потоке (Clear + AddRange) незачем.
            // Состав изменился (новые объекты после полной загрузки/ленты/правки Склада) — публикуем, как раньше.
            var shared = CatalogCacheService.Products;
            if (!SameTiles(_products, tiles) || shared.Count != tiles.Count)
            {
                _products = tiles.ToList();
                SyncInMemoryCatalog();
            }

            return tiles.Count > 0;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG load from SQLite failed: {ex}", "CATALOG");
            return false;
        }
    }

    public IReadOnlyList<CatalogProductTileVm> GetProducts() => _products;

    /// <summary>2026-10-30: те же объекты плиток в том же порядке (сравнение ссылок, микросекунды даже на 15 000).</summary>
    internal static bool SameTiles(IReadOnlyList<CatalogProductTileVm> a, IReadOnlyList<CatalogProductTileVm> b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]))
                return false;
        }

        return true;
    }

    // Один полный sync за раз + окно свежести (2026-09-07, оптимизация): при старте кассы
    // SyncService (первый проход цикла) и CatalogPanelViewModel.RefreshCatalogAsync запускали
    // ДВЕ полные загрузки каталога подряд с разницей в 2–3 секунды (в логе два "CATALOG fetch")
    // — вторая просто повторяла первую: сеть, маппинг, diff в SQLite, чекпоинт WAL. Теперь
    // (1) параллельный вызов дожидается уже идущего sync'а, (2) вызов в течение 15 с после
    // успешного получает его результат без повторной загрузки. Явные обновления после
    // редактирования/удаления товара в Складе идут через статический CatalogCacheService и
    // этим окном не ограничены.
    private static readonly TimeSpan FreshSyncWindow = TimeSpan.FromSeconds(15);
    private readonly object _syncGate = new();
    private Task<CatalogSyncResult>? _inFlightSync;
    private DateTime _lastSuccessfulSyncUtc = DateTime.MinValue;
    private CatalogSyncResult? _lastSuccessfulResult;
    private int _accountGeneration;

    /// <summary>Смена аккаунта (2026-09-26, «очищай старую базу товаров другого аккаунта»): база
    /// очищается в AccountCatalogIsolation, но этот список в памяти жил дальше — касса показывала и
    /// находила по штрихкоду товары прежнего аккаунта, пока не пройдёт синхронизация. Заодно
    /// забываем «свежую» синхронизацию прежнего аккаунта и отбрасываем ту, что ещё идёт.</summary>
    public void ResetForAccountChange()
    {
        lock (_syncGate)
        {
            _accountGeneration++;
            _warmStartDone = false; // 2026-10-30: новый аккаунт/магазин — тёплого старта по чужому курсору нет
            _warmFailures = 0;
            _inFlightSync = null;
            _lastSuccessfulResult = null;
            _lastSuccessfulSyncUtc = DateTime.MinValue;
        }

        _products = [];
        // 2026-10-27 (CATALOG_FEED): курсор ленты изменений принадлежал прежнему аккаунту.
        _feed?.Reset();
        SyncInMemoryCatalog();
        CatalogCacheService.NotifyCatalogChanged();
    }

    // ---------------------------------------------------------------- 2026-10-27 (Б-46 сервера, CATALOG_FEED): лента изменений каталога
    // Жалоба «на складе 195, а касса продаёт 205»: «версия» каталога смену цены не видит, другие кассы до 15–30 мин продают по старой цене.
    // Раз в ~8 секунд касса читает ленту products/changes/ (ETag/304), применяет изменённые товары к базе и сразу обновляет плитки.
    // Нет ленты на сервере / ошибка — тихо прежняя логика: проверка «версии» и полная загрузка по таймеру (страховка раз в 15–30 мин).
    private CatalogFeedSync? _feed;
    private int _feedStarted;
    private long _lastFeedFullSyncTicks;
    private long _lastFeedOkTicks;

    /// <summary>2026-10-29: лента отвечала (304 или изменения) в последние 90 с — «версия каталога» и полная загрузка по изменению версии не нужны.
    /// 2026-10-30 (15 тыс. товаров): 90 с → 5 мин. Под потоком 429 (массовая загрузка) лента стоит на паузе 30 с и ждёт щит — 90 с не хватало,
    /// лента считалась «мёртвой», и смена версии (её меняет каждая записанная карточка) запускала полную загрузку 155 страниц (журнал 15:57, 16:03).
    /// 429 — не поломка ленты: изменения, пропущенные за паузу, отдаст следующий такт по тому же курсору.</summary>
    private bool FeedHealthy => Interlocked.Read(ref _lastFeedOkTicks) is var t && t != 0 && DateTime.UtcNow.Ticks - t < TimeSpan.FromMinutes(5).Ticks;

    private void EnsureFeedLoop()
    {
        if (_catalogApi is not ICatalogFeedApi feedApi || !ServerPriceContract.CatalogFeedEnabled)
            return;
        if (Interlocked.Exchange(ref _feedStarted, 1) != 0)
            return;

        _feed = new CatalogFeedSync(
            feedApi,
            () => _settings.ApiBaseUrl,
            () => UserPreferences.Instance.LastCatalogUserKey ?? "",
            // 2026-10-29: остаток из ленты решается по каждому товару (FeedStockGuard: нет неотправленных чеков/продаж соседей/недавних
            // правок остатка на этом ПК), а не одним выключателем на весь каталог.
            () => true,
            FeedStockGuard.Create);
        _ = Task.Run(FeedLoopAsync);
    }

    private bool IsFullSyncRunning()
    {
        lock (_syncGate)
            return _inFlightSync is { IsCompleted: false };
    }

    private async Task FeedLoopAsync()
    {
        while (true)
        {
            try
            {
                // 2026-10-29: разброс ±15 % — несколько касс магазина не бьют в сервер в одну секунду.
                // 2026-10-29 (скорость синхронизации): адаптивный такт — 3 с при открытом чеке и после действия/подсказки, обычный (8 с), 30–60 с в простое
                // и при свёрнутом окне; подсказка соседа/телефона (SyncCenter.FeedKick) будит цикл сразу.
                await SyncCenter.WaitAdaptiveAsync(
                    () => SyncCenter.FeedInterval(TimeSpan.FromSeconds(ServerPriceContract.FeedIntervalSeconds)),
                    SyncCenter.FeedKick, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                var feed = _feed;
                if (feed is null || !ServerPriceContract.CatalogFeedEnabled
                    || OfflineModeHelper.SellLocally || ServerOutageMonitor.IsOutage || IsFullSyncRunning())
                    continue;

                // 2026-10-30: такт — через общий замок (TickFeedAsync): «Обновить» окон тоже читает ленту.
                var result = await TickFeedAsync(feed, CancellationToken.None).ConfigureAwait(false);

                // 2026-10-30 (15 тыс. товаров): во время массовой загрузки карточек полной загрузки нет — неизвестный товар принесёт лента
                // (карточка с названием добавляется в базу сразу), а полная сверка — после окончания загрузки.
                if (result.NeedFullSync && !BulkLoadActive && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastFeedFullSyncTicks) > TimeSpan.FromMinutes(1).Ticks)
                {
                    Interlocked.Exchange(ref _lastFeedFullSyncTicks, DateTime.UtcNow.Ticks);
                    PosLogger.Log("CATALOG_FEED: лента принесла товар, которого нет в базе кассы, — полная загрузка.", "CATALOG_FEED");
                    _ = StartFullSync(CancellationToken.None, knownVersion: null);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Лента — помощь каталогу: цикл не должен умереть.
                PosLogger.Log($"CATALOG_FEED: сбой цикла: {ex.GetType().Name}: {ex.Message}", "CATALOG_FEED");
            }
        }
    }

    public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) =>
        StartFullSync(cancellationToken, knownVersion: null);

    /// <param name="ignoreFresh">2026-10-30: окно после правки товара (лента не отвечает) — без «свежей загрузки 15 с»: прежний статический путь
    /// окон этим окном не ограничивался.</param>
    private Task<CatalogSyncResult> StartFullSync(CancellationToken cancellationToken, CatalogVersionInfo? knownVersion, bool ignoreFresh = false)
    {
        lock (_syncGate)
        {
            if (_inFlightSync is { IsCompleted: false } running)
                return running;

            if (!ignoreFresh && _lastSuccessfulResult is { } fresh && DateTime.UtcNow - _lastSuccessfulSyncUtc < FreshSyncWindow)
            {
                PosLogger.Log("CATALOG sync skipped: previous successful sync is fresh (<15s)", "CATALOG");
                return Task.FromResult(fresh);
            }

            // 2026-10-30 (приёмка 1.17.66, «Важно» 8): первая загрузка запуска при наличии базы и курсора ленты — дельта, а не весь каталог. Явные обновления окон
            // после правки товара (ignoreFresh) и повторные загрузки идут как раньше.
            var task = !ignoreFresh && !_warmStartDone
                ? WarmOrFullSyncAsync(cancellationToken, knownVersion)
                : SyncCatalogFullCoreAsync(cancellationToken, knownVersion);
            _inFlightSync = task;
            // 2026-10-30: сорванная полная загрузка — следующая по таймеру не раньше чем через 10 мин (см. SyncCatalogIfChangedAsync).
            _ = task.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Success)
                    _lastFullFailedUtc = DateTime.MinValue;
                else
                    _lastFullFailedUtc = DateTime.UtcNow;
            }, TaskScheduler.Default);
            return task;
        }
    }

    // ---------------------------------------------------------------- 2026-10-30 (приёмка 1.17.66, «Важно» 8): тёплый старт
    // Каждый перезапуск кассы качал весь каталог страницами по 100 (журнал NBS 19:00–19:07: 155 запросов, 6,5 мин, ≈51 запрос/мин — и под 429 при нескольких
    // кассах): «полной загрузки в этом запуске ещё не было» (lastFull = MinValue) считалось поводом качать всё, хотя товары уже в pos_local.db, а курсор ленты —
    // в catalog_feed_cursor.json. Теперь: база показана сразу (CatalogPanelViewModel → TryLoadFromDatabase), а дальше один-два запроса ленты с сохранённого курсора.
    // Полная загрузка при запуске — ТОЛЬКО если базы нет, курсора нет (первый запуск, смена аккаунта/магазина/сервера), лента не поддерживается или курсор забыт (410).

    private volatile bool _warmStartDone;

    // 2026-10-30 (приёмка 1.17.67, «Важно» 3): неудачи тёплого старта подряд (не 429).
    private int _warmFailures;
    private DateTime _warmFirstFailUtc = DateTime.MinValue;
    private const int WarmFailuresBeforeFull = 3;
    private static readonly TimeSpan WarmFailuresWindow = TimeSpan.FromMinutes(10);

    private async Task<CatalogSyncResult> WarmOrFullSyncAsync(CancellationToken ct, CatalogVersionInfo? knownVersion)
    {
        var generation = Volatile.Read(ref _accountGeneration);
        var warm = await TryWarmStartAsync(ct).ConfigureAwait(false);
        if (warm is not null)
            return warm;
        if (generation == Volatile.Read(ref _accountGeneration))
            _warmStartDone = true; // решение принято окончательно (полная загрузка) — дальше как обычно
        return await SyncCatalogFullCoreAsync(ct, knownVersion).ConfigureAwait(false);
    }

    /// <summary>null — тёплый старт невозможен (идём полной загрузкой). Иначе итог: Ok — каталог из базы дочитан лентой (или лента занята/недоступна —
    /// база остаётся, повтор при следующей синхронизации); Failed — нет связи (база показана как есть).</summary>
    private async Task<CatalogSyncResult?> TryWarmStartAsync(CancellationToken ct)
    {
        if (OfflineModeHelper.SellLocally || _catalogApi is not ICatalogFeedApi || !ServerPriceContract.CatalogFeedEnabled)
            return null;
        EnsureFeedLoop();
        if (_feed is not { } feed)
            return null;
        if (!HasLocalCatalog && !await Task.Run(TryLoadFromDatabase, ct).ConfigureAwait(false))
            return null;
        if (!feed.HasSavedCursor())
        {
            PosLogger.Log("CATALOG: тёплый старт невозможен — курсора ленты нет (первый запуск, смена аккаунта/магазина) — полная загрузка.", "CATALOG");
            return null;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tick = await TickFeedAsync(feed, ct, insideFullSync: true).ConfigureAwait(false);
        if (tick.NeedFullSync)
        {
            PosLogger.Log("CATALOG: тёплый старт — лента принесла неизвестный товар или курсор забыт (410) — полная загрузка.", "CATALOG");
            return null;
        }

        if (tick.Ok)
        {
            var okResult = CatalogSyncResult.Ok(tick.Added, tick.Changed, tick.Deleted);
            lock (_syncGate)
            {
                _lastSuccessfulSyncUtc = DateTime.UtcNow;
                _lastSuccessfulResult = okResult;
            }

            _warmStartDone = true;
            _warmFailures = 0;
            SyncCenter.RecordSuccess("catalog");
            PosLogger.Log($"CATALOG: тёплый старт — {_products.Count} товаров из базы, лента с сохранённого курсора за {watch.ElapsedMilliseconds} мс (добавлено {tick.Added}, изменено {tick.Changed}, удалено {tick.Deleted}); весь каталог не качаем.", "CATALOG");
            return okResult;
        }

        if (feed.Unsupported)
        {
            PosLogger.Log("CATALOG: тёплый старт невозможен — сервер не поддерживает ленту — полная загрузка.", "CATALOG");
            return null;
        }

        // 2026-10-30 (приёмка 1.17.67, «Важно» 3): лента стабильно отвечает 5xx/таймаутом/битым JSON — тёплый старт «10 мин пауза → снова тёплый → Failed» не кончался.
        // После 3 неудач подряд или 10 минут с первой — один раз полная загрузка. 429 («занят») не неудача; пока идёт массовая загрузка карточек — не качаем.
        if (!ApiThrottle.IsBusy && !BulkLoadActive && !ServerOutageMonitor.IsOutage)
        {
            var now = DateTime.UtcNow;
            if (_warmFailures == 0)
                _warmFirstFailUtc = now;
            _warmFailures++;
            if (_warmFailures >= WarmFailuresBeforeFull || now - _warmFirstFailUtc >= WarmFailuresWindow)
            {
                PosLogger.Log($"CATALOG: тёплый старт не удался {_warmFailures} раз подряд (с {_warmFirstFailUtc:HH:mm:ss} UTC) — полная загрузка.", "CATALOG");
                _warmFailures = 0;
                return null;
            }
        }

        // Сервер занят (429), нет связи или сбой: база уже показана, полную загрузку под 429 не начинаем — повтор при следующей синхронизации.
        PosLogger.Log($"CATALOG: тёплый старт — лента сейчас не ответила ({(string.IsNullOrWhiteSpace(tick.Note) ? "пауза/сбой" : tick.Note)}); каталог из базы, повтор позже.", "CATALOG");
        return ApiThrottle.IsBusy ? CatalogSyncResult.Ok(0, 0, 0) : CatalogSyncResult.Failed(Tr.T("Лента изменений не ответила — показан каталог из локальной базы.",
            "Өзгөрүүлөр тасмасы жооп берген жок — локалдык базадагы каталог көрсөтүлдү.", "The change feed did not answer — showing the catalog from the local database.",
            "Değişiklik akışı yanıt vermedi — yerel veritabanındaki katalog gösteriliyor.", "O'zgarishlar lentasi javob bermadi — mahalliy bazadagi katalog ko'rsatilmoqda."));
    }

    /// <summary>2026-10-04, отчёт о производительности (п. 5): как часто фоновая синхронизация всё-таки
    /// качает каталог целиком, даже если «версия» не менялась. Продажа на другой кассе или на сайте не
    /// меняет updated_at товара (проверено на NBS), и остатки по версии не видны — их приносит эта
    /// полная загрузка (свои продажи вычитаются сразу, соседние кассы — по локальной сети). Большой каталог
    /// (больше 3000 товаров — 30+ страниц на каждую полную загрузку) и слабый ПК — раз в 30 минут,
    /// остальные — раз в 15.</summary>
    private TimeSpan FullReloadInterval =>
        // 2026-10-30 (15 тыс. товаров): лента жива и каталог больше 3000 — страховочная полная загрузка раз в час (155 страниц ≈ 3 мин работы сервера).
        FeedHealthy && _products.Count > 3000
            ? TimeSpan.FromMinutes(60)
            : UserPreferences.Instance.LowPerformanceMode || _products.Count > 3000
            ? TimeSpan.FromMinutes(30)
            : TimeSpan.FromMinutes(15);

    /// <summary>2026-10-04, п. 5: фоновая синхронизация (SyncService, раз в 2 мин). Раньше каждый раз
    /// качался весь каталог: 145 товаров — 2 страницы и 8 служебных запросов, 15 000 товаров — 150
    /// страниц, 27 МБ и ~90 с из каждых 120 с. Теперь сначала один лёгкий запрос «версии» (число товаров
    /// + последний изменённый, ordering=-updated_at&amp;page_size=1, ~1 КБ); совпала с сохранённой после
    /// прошлой полной загрузки — каталог не качается. Полная загрузка — при изменении версии и не реже
    /// <see cref="FullReloadInterval"/>. Явные обновления (кнопка «Обновить», Склад, весы) идут через
    /// <see cref="SyncCatalogFullAsync"/> и всегда качают всё.</summary>
    public async Task<CatalogSyncResult> SyncCatalogIfChangedAsync(CancellationToken cancellationToken = default)
    {
        if (OfflineModeHelper.SellLocally)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");

        EnsureFeedLoop(); // 2026-10-27: лента изменений стартует вместе с первой фоновой синхронизацией
        Task<CatalogSyncResult>? running;
        DateTime lastFull;
        lock (_syncGate)
        {
            running = _inFlightSync is { IsCompleted: false } inFlight ? inFlight : null;
            lastFull = _lastSuccessfulSyncUtc;
        }

        if (running != null)
            return await running.ConfigureAwait(false);

        // 2026-10-30 (15 тыс. товаров): каталог в базе есть, а массовая загрузка карточек идёт или прошлая полная загрузка сорвалась
        // меньше 10 минут назад — по таймеру весь каталог не качаем (изменения приносит лента). Раньше сорванная загрузка повторялась
        // каждые 2 минуты и снова со страницы 1 (журнал кассы 15:47–16:37: 10 попыток, дошли до страниц 1–21 из 155).
        if (HasLocalCatalog && (BulkLoadActive || DateTime.UtcNow - _lastFullFailedUtc < FailedFullRetry))
            return CatalogSyncResult.Ok(0, 0, 0);

        // Полной загрузки в этом запуске ещё не было (старт, смена аккаунта) или она была давно — всё.
        if (DateTime.UtcNow - lastFull >= FullReloadInterval)
            return await StartFullSync(cancellationToken, knownVersion: null).ConfigureAwait(false);

        // 2026-10-29: лента изменений отвечает — изменения товаров уже в базе; запрос «версии» (раз в 2 мин) и полная загрузка по смене версии
        // (каждая правка цены/товара меняла версию и гоняла весь каталог) не нужны. Страховка — полная загрузка раз в 15–30 мин (выше).
        if (FeedHealthy)
            return CatalogSyncResult.Ok(0, 0, 0);

        CatalogVersionInfo? remote;
        try
        {
            remote = await _catalogApi.ProductsCatalogVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ApiException ex)
        {
            return CatalogSyncResult.Failed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CatalogSyncResult.Failed(string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message);
        }

        var local = LocalProductRepository.Instance.GetCatalogVersionToken();
        if (remote is { IsEmpty: false }
            && !string.IsNullOrEmpty(local)
            && string.Equals(remote.Token, local, StringComparison.Ordinal))
        {
            PosLogger.Log(
                $"CATALOG: версия не изменилась ({remote.Token}) — полная загрузка пропущена; остатки — не позже чем через {(lastFull + FullReloadInterval - DateTime.UtcNow).TotalMinutes:0} мин.",
                "CATALOG");
            return CatalogSyncResult.Ok(0, 0, 0);
        }

        PosLogger.Log($"CATALOG: версия изменилась ({local ?? "—"} → {remote?.Token ?? "нет ответа"}) — полная загрузка.", "CATALOG");
        // Версия уже получена — повторно её не спрашиваем (см. SyncCatalogFullCoreAsync).
        return await StartFullSync(cancellationToken, remote).ConfigureAwait(false);
    }

    private async Task<CatalogSyncResult> SyncCatalogFullCoreAsync(CancellationToken cancellationToken = default,
        CatalogVersionInfo? knownVersion = null)
    {
        // 2026-09-29: и в аварии сервера — каталог из локальной базы, сервер не дёргаем.
        if (OfflineModeHelper.SellLocally)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");

        int generation;
        lock (_syncGate)
            generation = _accountGeneration;

        try
        {
            var snapshotStartedUtc = DateTime.UtcNow;
            // 2026-10-04: версию только что получила SyncCatalogIfChangedAsync — второй запрос не нужен.
            var remoteVersion = knownVersion ?? await _catalogApi
                .ProductsCatalogVersionAsync(cancellationToken)
                .ConfigureAwait(false);

            // 2026-10-30 (холодный старт на 16 тыс.): база кассы пуста — страницы пишутся в базу и показываются по мере загрузки.
            var onPage = HasLocalCatalog ? null : ColdStartPageHandler(generation);
            if (onPage is not null)
                CatalogApiService.FullLoadPage += onPage;
            List<JsonElement>? rawItems = null;
            string? feedCursor = null;
            try
            {
                // 2026-10-30 (холодный старт): сначала лента по 500 (32 запроса на 16 тыс. вместо 160 страниц по 100); не вышло или отдала не всё — по страницам.
                if (onPage is not null)
                    (rawItems, feedCursor) = await TryFeedSnapshotAsync(remoteVersion, generation, cancellationToken).ConfigureAwait(false);
                rawItems ??= await _catalogApi.ProductsCatalogAsync(
                    _settings.Catalog.QuickCatalogLimit,
                    _settings.Catalog.CatalogMaxPages,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (onPage is not null)
                    CatalogApiService.FullLoadPage -= onPage;
            }

            var apiBaseUrl = _settings.ApiBaseUrl;
            var newList = new List<CatalogProductTileVm>();

            foreach (JsonElement el in rawItems)
            {
                var vm = ProductCatalogMapper.TryTile(el, apiBaseUrl);
                if (vm != null)
                    newList.Add(vm);
            }

            // 2026-09-28: акции товаров (запомнены в TryTile) — на диск, для запуска без интернета.
            PromotionRules.SaveIfDirty();

            await StockSyncService.OverlayAgentStockAsync(newList, cancellationToken).ConfigureAwait(false);

            // Гарантируем StockInfo перед записью в SQLite (на случай отсутствия overlay).
            foreach (var vm in newList)
            {
                if (string.IsNullOrWhiteSpace(vm.StockInfo))
                    StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
            }

            // Пока товары качались, сменился аккаунт — это каталог прежнего, в базу нового его не пишем.
            if (generation != Volatile.Read(ref _accountGeneration))
            {
                PosLogger.Log("CATALOG sync: аккаунт сменился во время загрузки — каталог прежнего аккаунта отброшен.", "CATALOG");
                return CatalogSyncResult.Failed("Аккаунт сменился во время загрузки каталога.");
            }

            // 2026-10-29: товар, остаток которого менялся на этом ПК за последние 90 с (своя продажа/возврат/ревизия), — остаток в снимке мог быть
            // взят до того, как сервер учёл операцию: оставляем локальный (иначе «остаток откатывается» после продажи).
            foreach (var vm in newList)
            {
                if (vm.Id is null || !StockSyncService.WasLocallyTouched(vm.Id, FeedStockGuard.LocalTouchWindow))
                    continue;
                if (LocalProductRepository.Instance.LoadTileCopy(vm.Id) is { } keep)
                    StockSyncService.ApplyQuantityToTile(vm, keep.Quantity, vm.MustWeigh);
            }

            // Продажи соседних касс, которых сервер ещё не знает, вычитаются из снимка до записи.
            NurMarketKassa.Services.Lan.LanStockAdjuster.AdjustSnapshot(newList, snapshotStartedUtc);
            var (added, changed, deleted) = LocalProductRepository.Instance.SyncReplaceAllWithDiff(newList);

            if (remoteVersion != null && !remoteVersion.IsEmpty)
                LocalProductRepository.Instance.SetCatalogVersionToken(remoteVersion.Token);

            // 2026-10-30: каталог получен лентой — её опрос продолжает с этого курсора (не читает 16 тыс. второй раз).
            if (feedCursor is not null)
            {
                EnsureFeedLoop();
                if (_feed is { } adopt)
                {
                    await _feedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { adopt.AdoptCursor(feedCursor); }
                    finally { _feedGate.Release(); }
                    Interlocked.Exchange(ref _lastFeedOkTicks, DateTime.UtcNow.Ticks);
                }
            }

            var syncTime = DateTime.UtcNow;
            LocalProductRepository.Instance.SetLastSyncTime(syncTime);
            CatalogCacheService.NoteSynced(syncTime); // 2026-10-30: «каталог свежий» и для окон (массовая загрузка)

            _auditDb.LogEvent("catalog", "refresh", new { count = newList.Count, added, changed, deleted });
            PosLogger.Log(
                $"CATALOG sync: added={added}, changed={changed}, deleted={deleted}, total={newList.Count}",
                "CATALOG");

            _products = newList;
            SyncInMemoryCatalog();

            // Экран кассира узнаёт об изменениях каталога (2026-09-07) — раньше результат фонового
            // sync'а оседал только в SQLite и до плиток не доходил до перезапуска.
            if (added + changed + deleted > 0)
                CatalogCacheService.NotifyCatalogChanged();

            // Сливает WAL в основной файл БД на самой частой в приложении операции записи
            // (каждые ~45с) — не единственная защита (см. App.axaml.cs, штатное закрытие), но
            // держит "риск-окно" для резкого завершения процесса (taskkill /F и т.п.) коротким,
            // а не на всю сессию (см. DatabaseService.CheckpointWal, 2026-09-04).
            DatabaseService.CheckpointWal();

            var okResult = CatalogSyncResult.Ok(added, changed, deleted);
            lock (_syncGate)
            {
                _lastSuccessfulSyncUtc = DateTime.UtcNow;
                _lastSuccessfulResult = okResult;
            }

            SyncCenter.RecordSuccess("catalog");

            return okResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ApiException ex)
        {
            return CatalogSyncResult.Failed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CatalogSyncResult.Failed(string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG sync error: {ex}", "CATALOG");
            return CatalogSyncResult.Failed(ex.Message);
        }
    }

    /// <summary>Общий список <see cref="CatalogCacheService.Products"/> читают экраны кассы и склада
    /// из UI-потока (WarehouseViewModel.ApplyProductFilter копирует его целиком в
    /// ObservableCollection), а полная синхронизация каталога идёт в фоне каждые ~45 с. Пока запись
    /// шла прямо из фонового потока, Clear()+AddRange успевали пересоздать внутренний буфер List'а
    /// между чтением его длины и самим копированием — копирование падало с "Source array was not
    /// long enough … (Parameter 'sourceArray')", и склад показывал "Не удалось загрузить каталог"
    /// при том, что товары на месте (живой баг 2026-09-21, каталог 784 позиции). Теперь запись
    /// всегда идёт в UI-потоке — том же, где читают, — поэтому гонки нет. Dispatcher выполняет
    /// действие inline, если поток уже UI-шный, так что порядок на старте не меняется.
    /// Ср. <see cref="CatalogCacheService.SetProducts"/> — там перевод в UI-поток был с самого начала.</summary>
    private void SyncInMemoryCatalog()
    {
        // _products присваивается целиком (новый список), а не мутируется, поэтому копия ссылки
        // безопасна: фоновый поток не изменит его во время AddRange.
        var snapshot = _products;
        UiDispatcherHolder.Post(() =>
        {
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(snapshot);
        });
    }
}
