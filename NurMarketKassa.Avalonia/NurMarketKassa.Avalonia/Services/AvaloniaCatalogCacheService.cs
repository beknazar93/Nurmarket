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
        // 2026-11-06 (Б-9): после возврата связи каталог не качается заново целиком (155 страниц, ~100 с) — достаточно ленты products/changes; см. CatalogFullLoadMarks.IsRecoveryFeedOnly.
        ServerOutageMonitor.Recovered += OnServerRecoveredForCatalog;
        // 2026-10-30 (15 тыс. товаров): окна (Склад, массовая загрузка, ревизия, правка товара) обновляют каталог через этот объект — одна
        // полная загрузка на программу и лента вместо полной, когда она жива (см. SharedRefreshAsync).
        CatalogCacheService.SharedRefresh = SharedRefreshAsync;
        CatalogCacheService.RequestBackgroundFullReload = RequestBackgroundFullReload;
    }

    private long _recoveredTicks;

    /// <summary>2026-11-06 (Б-9): последний момент, когда связи не было (касса офлайн / авария сервера) или она только что вернулась. От него считается окно «только лента»
    /// (<see cref="CatalogFullLoadMarks.RecoveryFeedOnlyWindow"/>): ни одна полная загрузка не стартует сразу после возврата связи.</summary>
    private void NoteConnectionTrouble() => Interlocked.Exchange(ref _recoveredTicks, DateTime.UtcNow.Ticks);

    private void OnServerRecoveredForCatalog()
    {
        NoteConnectionTrouble();
        PosLogger.Log("CATALOG: связь с сервером вернулась — каталог догоняет лента изменений, полную загрузку не запускаем (если база и лента есть).", "CATALOG");
        SyncCenter.FeedKick.Kick();
    }

    private DateTime? RecoveredUtc => Interlocked.Read(ref _recoveredTicks) is var t && t != 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    /// <summary>true — связь недавно вернулась, каталог в базе есть и лента поддерживается: полную загрузку не начинаем, достаточно ленты.</summary>
    private bool RecoveryFeedOnly() =>
        HasLocalCatalog && _feed is { Unsupported: false } && CatalogFullLoadMarks.IsRecoveryFeedOnly(RecoveredUtc, DateTime.UtcNow);

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
                Interlocked.Increment(ref _feedOkStreak); // 2026-11-02 (Н3-01): считаем удачные опросы ленты — см. FullReloadInterval
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

    private long _lastRecentSyncTicks;

    /// <summary>2026-11-07 (Б-П2): сверка «недавно изменённых» под тем же замком, что такт ленты (два применения в базу не наперегонки). Исправленное — на плитки, в поиск, телефонам.</summary>
    private async Task<CatalogFeedSync.RecentResult> RecentSyncAsync(CatalogFeedSync feed, bool manual, CancellationToken ct)
    {
        await _feedGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref _accountGeneration);
            var result = await feed.ReconcileRecentAsync(() => generation == Volatile.Read(ref _accountGeneration) && !IsFullSyncRunning(), manual, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                CatalogCacheService.NoteSynced(DateTime.UtcNow);
                if (result.Fixed > 0 && generation == Volatile.Read(ref _accountGeneration))
                {
                    TryLoadFromDatabase();
                    CatalogCacheService.NotifyCatalogChanged();
                    NurMarketKassa.Services.PhoneLink.PhoneLinkEvents.Publish("product");
                }
            }

            return result;
        }
        finally
        {
            _feedGate.Release();
        }
    }

    /// <summary>2026-11-07 (Б-П2): «Обновить каталог сейчас» — быстро (секунды), без полной загрузки каталога: лента изменений + сверка последних изменённых. null — быстрый путь невозможен
    /// (нет связи, ленты нет на сервере, идёт полная загрузка) — вызывающий делает обычное обновление. Fixed — сколько товаров исправлено, Total — сколько проверено.</summary>
    public async Task<CatalogSyncResult?> QuickRefreshAsync(CancellationToken ct = default)
    {
        if (OfflineModeHelper.SellLocally || ServerOutageMonitor.IsOutage || IsFullSyncRunning() || !HasLocalCatalog)
            return null;
        EnsureFeedLoop();
        if (_feed is not { } feed || feed.Unsupported)
            return null;
        var tick = await TickFeedAsync(feed, ct).ConfigureAwait(false);
        if (!tick.Ok || tick.NeedFullSync)
            return null;
        var recent = await RecentSyncAsync(feed, manual: true, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastRecentSyncTicks, DateTime.UtcNow.Ticks);
        if (!recent.Ok && recent.Note != "сервер не отдаёт последние изменённые")
            return null; // сверка не удалась (429, сбой) — пусть вызывающий обновит обычным путём
        await UiDispatcherHolder.InvokeAsync(() => { }).ConfigureAwait(false);
        return CatalogSyncResult.Ok(tick.Added, tick.Changed + recent.Fixed, tick.Deleted);
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
        {
            NoteConnectionTrouble();
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");
        }
        EnsureFeedLoop();
        // 2026-11-06 (Б-9): связь только что вернулась — лента (она одна-две страницы), а не 155 страниц полного каталога; лента отвечает — «здорова» для этого окна и без 5-минутного ожидания.
        if (_feed is { } recFeed && RecoveryFeedOnly() && !IsFullSyncRunning())
        {
            var tick = await TickFeedAsync(recFeed, ct).ConfigureAwait(false);
            if (!tick.NeedFullSync)
            {
                PosLogger.Log("CATALOG: обновление окна после возврата связи — прочитана лента изменений, полная загрузка не нужна.", "CATALOG");
                return CatalogSyncResult.Ok(tick.Added, tick.Changed, tick.Deleted);
            }
        }
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

        return await StartFullSync(ct, knownVersion: null, ignoreFresh: true, reason: "обновление по просьбе окна, лента сейчас не помогает").ConfigureAwait(false);
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
    /// 2026-11-06 (Б-1): карточка ленты у сервера СОКРАЩЁННАЯ (9 полей: нет единицы, «весовой», PLU, штрихкода, вида, фото) — такой лентой пустую базу
    /// заполнять нельзя (весовые становились штучными, штрихкоды не находились). Если в первой странице нет ни единиц/веса, ни штрихкодов — null:
    /// первая загрузка идёт по страницам (полные карточки), лента остаётся для дельты поверх полной базы. Сервер добавит поля в ленту — снова быстрая загрузка лентой.</summary>
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

                // 2026-11-06 (Б-1, И-1): проверка ДО ReportFullLoad — иначе сокращённые карточки первой страницы попали бы в базу через ColdStartPageHandler.
                if (page == 1 && CatalogFullLoadMarks.IsShortFeedPage(p.Items))
                {
                    PosLogger.Log("CATALOG: лента без единиц/PLU/штрихкодов (сокращённая карточка) — первая загрузка по страницам с полными карточками.", "CATALOG");
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

        if (AgeOf(last) < TimeSpan.FromMinutes(10))
            return null;
        return StartFullSync(CancellationToken.None, knownVersion: null, reason: "сверка после «Обновить» на Складе, прошлая загрузка старше 10 минут");
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

        if (AgeOf(_lastSuccessfulSyncUtc) < SyncCenter.StockReloadMinSpacing)
            return;
        PosLogger.Log("SYNC: чужая продажа/возврат — перечитываю остатки каталога.", "SYNC");
        _ = StartFullSync(CancellationToken.None, knownVersion: null, reason: "чужая продажа или возврат, лента сейчас не отвечает");
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
    // 2026-11-06 (Б-1): время последней ПОЛНОЙ загрузки полными карточками — для страховочного интервала. Тёплый старт раньше записывал в _lastSuccessfulSyncUtc «сейчас»,
    // и каждый перезапуск обнулял отсчёт (каталог больше 3000 не дожидался 8 часов). Теперь время берётся из catalog_meta (full_load_utc) и переживает перезапуск.
    private DateTime _lastFullLoadUtc = DateTime.MinValue;
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
            _lastFullLoadUtc = DateTime.MinValue; // 2026-11-06 (Б-1): у нового аккаунта полной загрузки не было (метка в catalog_meta стирается вместе с базой)
        }

        _products = [];
        Interlocked.Exchange(ref _feedOkStreak, 0); // 2026-11-02 (Н3-01): у нового аккаунта лента ещё не проверена
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
    private int _feedOkStreak;

    /// <summary>2026-10-29: лента отвечала (304 или изменения) в последние 90 с — «версия каталога» и полная загрузка по изменению версии не нужны.
    /// 2026-10-30 (15 тыс. товаров): 90 с → 5 мин. Под потоком 429 (массовая загрузка) лента стоит на паузе 30 с и ждёт щит — 90 с не хватало,
    /// лента считалась «мёртвой», и смена версии (её меняет каждая записанная карточка) запускала полную загрузку 155 страниц (журнал 15:57, 16:03).
    /// 429 — не поломка ленты: изменения, пропущенные за паузу, отдаст следующий такт по тому же курсору.</summary>
    /// <summary>2026-11-05 (аудит №3, Н-06): сколько прошло с момента <paramref name="utc"/>. Часы переведены назад — метка «в будущем» (больше 5 с) считается ДАВНЕЙ, а не отрицательной:
    /// иначе «только что синхронизировались» держалось бы часами, а лента считалась живой, пока часы не догонят метку. DateTime.MinValue/default тоже «давно».</summary>
    private static TimeSpan AgeOf(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age < TimeSpan.FromSeconds(-5) ? TimeSpan.MaxValue : age;
    }

    private bool FeedHealthy =>Interlocked.Read(ref _lastFeedOkTicks) is var t && t != 0 && AgeOf(new DateTime(t, DateTimeKind.Utc)) < TimeSpan.FromMinutes(5);

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
                if (OfflineModeHelper.SellLocally || ServerOutageMonitor.IsOutage)
                    NoteConnectionTrouble(); // 2026-11-06 (Б-9): окно «только лента» после возврата связи отсчитывается от последнего момента без связи
                if (feed is null || !ServerPriceContract.CatalogFeedEnabled
                    || OfflineModeHelper.SellLocally || ServerOutageMonitor.IsOutage || IsFullSyncRunning())
                    continue;

                // 2026-10-30: такт — через общий замок (TickFeedAsync): «Обновить» окон тоже читает ленту.
                var result = await TickFeedAsync(feed, CancellationToken.None).ConfigureAwait(false);

                // 2026-10-30 (15 тыс. товаров): во время массовой загрузки карточек полной загрузки нет — неизвестный товар принесёт лента
                // (карточка с названием добавляется в базу сразу), а полная сверка — после окончания загрузки.
                if (result.NeedFullSync && !BulkLoadActive && AgeOf(new DateTime(Interlocked.Read(ref _lastFeedFullSyncTicks), DateTimeKind.Utc)) > TimeSpan.FromMinutes(1))
                {
                    Interlocked.Exchange(ref _lastFeedFullSyncTicks, DateTime.UtcNow.Ticks);
                    PosLogger.Log("CATALOG_FEED: лента принесла товар, которого нет в базе кассы, — полная загрузка.", "CATALOG_FEED");
                    _ = StartFullSync(CancellationToken.None, knownVersion: null, reason: "лента принесла товар, которого нет в базе кассы");
                }

                // 2026-11-07 (живой тест, Б-П2): страховочная сверка «недавно изменённых» раз в 2–3 минуты (ServerPriceContract.RecentSyncIntervalSeconds): лента однажды потеряла
                // изменение товара после прихода (курсор ушёл на 11 мс дальше записи), касса 12 минут продавала по старой цене. Один лёгкий запрос (последние 100 изменённых).
                if (!BulkLoadActive && !IsFullSyncRunning()
                    && AgeOf(new DateTime(Interlocked.Read(ref _lastRecentSyncTicks), DateTimeKind.Utc)) > TimeSpan.FromSeconds(ServerPriceContract.RecentSyncIntervalSeconds))
                {
                    Interlocked.Exchange(ref _lastRecentSyncTicks, DateTime.UtcNow.Ticks);
                    await RecentSyncAsync(feed, manual: false, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Лента — помощь каталогу: цикл не должен умереть.
                // 2026-11-06 (Б-13): повторяющийся сбой цикла (опрос раз в несколько секунд) — одна запись в 10 минут на причину.
                PosLogger.LogThrottled("feed-loop-" + ex.GetType().Name, $"CATALOG_FEED: сбой цикла: {ex.GetType().Name}: {ex.Message}", "CATALOG_FEED");
            }
        }
    }

    public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) =>
        StartFullSync(cancellationToken, knownVersion: null, reason: "явная просьба экрана (обновить каталог)");

    /// <param name="ignoreFresh">2026-10-30: окно после правки товара (лента не отвечает) — без «свежей загрузки 15 с»: прежний статический путь
    /// окон этим окном не ограничивался.</param>
    /// <param name="reason">2026-11-06 (Б-9): причина запуска — одной строкой в журнал (раньше «CATALOG full» через 2 минуты после старта шёл без объяснения).</param>
    private Task<CatalogSyncResult> StartFullSync(CancellationToken cancellationToken, CatalogVersionInfo? knownVersion, bool ignoreFresh = false, string? reason = null)
    {
        lock (_syncGate)
        {
            if (_inFlightSync is { IsCompleted: false } running)
                return running;

            if (!ignoreFresh && _lastSuccessfulResult is { } fresh && AgeOf(_lastSuccessfulSyncUtc) < FreshSyncWindow)
            {
                PosLogger.Log("CATALOG sync skipped: previous successful sync is fresh (<15s)", "CATALOG");
                return Task.FromResult(fresh);
            }

            // 2026-10-30 (приёмка 1.17.66, «Важно» 8): первая загрузка запуска при наличии базы и курсора ленты — дельта, а не весь каталог. Явные обновления окон
            // после правки товара (ignoreFresh) и повторные загрузки идут как раньше.
            // 2026-11-06 (Б-9): строка причины есть ВСЕГДА — полная загрузка без объяснения в журнале недопустима.
            PosLogger.Log($"CATALOG: запуск загрузки каталога — {reason ?? "причина не указана вызывающим кодом"}.", "CATALOG");
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
        // 2026-11-06 (Б-1, И-2): самолечение. База, заполненная сокращённой лентой (is_weight нет почти ни у кого, метки полной загрузки нет), — один раз полная
        // постраничная загрузка с полными карточками. Идёт в фоне: плитки и продажа работают с базой, остатки и неотправленное не трогаются (SyncReplaceAllWithDiff).
        if (CatalogFullLoadMarks.NeedsSelfHeal(LocalProductRepository.Instance, out var healWhy))
        {
            PosLogger.Log($"CATALOG: самолечение каталога — {healWhy}; полная загрузка полными карточками (фоном, продажа не блокируется; не чаще раза в сутки, оборванная продолжится).", "CATALOG");
            CatalogFullLoadMarks.MarkSelfHealAttempt(LocalProductRepository.Instance, DateTime.UtcNow);
            return null;
        }

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
            // 2026-11-06 (Б-1): отсчёт страховочной полной загрузки — от ПОСЛЕДНЕЙ настоящей (из базы), а не от «сейчас»; нет метки и нет last_sync_utc — MinValue (нужна полная).
            var persistedFull = CatalogFullLoadMarks.LastFullLoadOrLegacy(LocalProductRepository.Instance) ?? DateTime.MinValue;
            lock (_syncGate)
            {
                _lastSuccessfulSyncUtc = DateTime.UtcNow;
                _lastSuccessfulResult = okResult;
                _lastFullLoadUtc = persistedFull;
            }

            // Тёплый старт показал, что лента отвечает: большому каталогу не нужно ждать 10 опросов, чтобы страховочный интервал стал 8 часами (иначе после
            // перезапуска с давней меткой сразу пошли бы 155 страниц).
            if (Volatile.Read(ref _feedOkStreak) < FeedOkStreakForSafetyReload)
                Interlocked.Exchange(ref _feedOkStreak, FeedOkStreakForSafetyReload);
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
        // 2026-11-02 (аудит №2, Н3-01): лента жива, каталог больше 3000 и лента без ошибок последние 30 мин (не меньше 10 удачных опросов) — страховочная
        // полная загрузка раз в 8 часов (было раз в час: 155 страниц, всплеск ~35 запросов/мин и рост памяти кассы с ~300 до 600–700 МБ; каждый раз
        // «added=0, changed=0, deleted=0» — остатки и цены приносит лента). Лента сбоила — прежние 30 мин, чтобы расхождение не копилось.
        FeedHealthy && _products.Count > 3000 && FeedStableForSafetyReload
            ? SafetyFullReloadInterval
            // 2026-10-30 (15 тыс. товаров): лента жива и каталог больше 3000 — страховочная полная загрузка раз в час (155 страниц ≈ 3 мин работы сервера).
            : FeedHealthy && _products.Count > 3000
            ? TimeSpan.FromMinutes(60)
            : UserPreferences.Instance.LowPerformanceMode || _products.Count > 3000
            ? TimeSpan.FromMinutes(30)
            : TimeSpan.FromMinutes(15);

    private DateTime _lastVersionSkipLoggedUtc = DateTime.MinValue;
    private DateTime _lastRecoverySkipLoggedUtc = DateTime.MinValue;
    // 2026-11-06 (Б-9): 8 часов → сутки (CatalogFullLoadMarks.SafetyFullReloadInterval): после ночи каждый первый запуск качал все 155 страниц, лента и так ведёт дельту.
    private static readonly TimeSpan SafetyFullReloadInterval = CatalogFullLoadMarks.SafetyFullReloadInterval;
    private const int FeedOkStreakForSafetyReload = 10;
    private static readonly TimeSpan FeedQuietForSafetyReload = TimeSpan.FromMinutes(30);

    /// <summary>2026-11-02 (Н3-01): «последние N проверок без ошибок» — не меньше 10 удачных опросов ленты с запуска и ни одной настоящей ошибки ленты
    /// (429 «занят» не в счёт) за последние 30 минут.</summary>
    private bool FeedStableForSafetyReload =>
        Volatile.Read(ref _feedOkStreak) >= FeedOkStreakForSafetyReload
        && _feed is { } f && AgeOf(f.LastFailureUtc) >= FeedQuietForSafetyReload;

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
        {
            NoteConnectionTrouble(); // 2026-11-06 (Б-9)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");
        }

        EnsureFeedLoop(); // 2026-10-27: лента изменений стартует вместе с первой фоновой синхронизацией
        Task<CatalogSyncResult>? running;
        DateTime lastFull;
        lock (_syncGate)
        {
            running = _inFlightSync is { IsCompleted: false } inFlight ? inFlight : null;
            lastFull = _lastFullLoadUtc; // 2026-11-06 (Б-1): время настоящей полной загрузки (из базы), а не тёплого старта
        }

        if (running != null)
            return await running.ConfigureAwait(false);

        // 2026-10-30 (15 тыс. товаров): каталог в базе есть, а массовая загрузка карточек идёт или прошлая полная загрузка сорвалась
        // меньше 10 минут назад — по таймеру весь каталог не качаем (изменения приносит лента). Раньше сорванная загрузка повторялась
        // каждые 2 минуты и снова со страницы 1 (журнал кассы 15:47–16:37: 10 попыток, дошли до страниц 1–21 из 155).
        if (HasLocalCatalog && (BulkLoadActive || AgeOf(_lastFullFailedUtc) < FailedFullRetry))
            return CatalogSyncResult.Ok(0, 0, 0);

        // 2026-11-02 (Н3-01): идёт оплата или открытие/закрытие смены — полную загрузку по таймеру не начинаем (следующая проверка через 2 мин).
        if (HasLocalCatalog && ApiThrottle.CriticalInFlight)
            return CatalogSyncResult.Ok(0, 0, 0);

        // 2026-11-06 (Б-9): связь только что вернулась (журнал 16:00: 155 страниц, 100 с, без строки причины) — каталог догоняет лента изменений; полная загрузка
        // (в том числе «плановая» и по смене версии) подождёт окончания окна. Нет базы или ленты (первый запуск, лента не поддерживается) — прежний путь.
        if (RecoveryFeedOnly())
        {
            if (AgeOf(_lastRecoverySkipLoggedUtc) >= TimeSpan.FromMinutes(5))
            {
                _lastRecoverySkipLoggedUtc = DateTime.UtcNow;
                PosLogger.Log("CATALOG: связь недавно вернулась — полную загрузку каталога откладываем, изменения принесёт лента (products/changes).", "CATALOG");
            }

            SyncCenter.FeedKick.Kick();
            return CatalogSyncResult.Ok(0, 0, 0);
        }

        // Полной загрузки в этом запуске ещё не было (старт, смена аккаунта) или она была давно — всё.
        // 2026-11-06 (Б-9): причина пишется в журнал; кассир за открытым чеком — откладываем (до 6 ч сверх срока); отсчёт — от метки в базе (переживает перезапуск).
        if (CatalogFullLoadMarks.IsSafetyReloadDue(lastFull, DateTime.UtcNow, FullReloadInterval, HasLocalCatalog && SyncCenter.OpenCheck, out var dueWhy))
            return await StartFullSync(cancellationToken, knownVersion: null, reason: $"плановая полная сверка: {dueWhy}").ConfigureAwait(false);

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
            // 2026-11-02 (Н3-02): без ленты эта проверка идёт каждые 2 мин (в журнале 30.10 — 2 617 одинаковых строк) — пишем раз в 30 минут.
            if (AgeOf(_lastVersionSkipLoggedUtc) >= TimeSpan.FromMinutes(30))
            {
                _lastVersionSkipLoggedUtc = DateTime.UtcNow;
                PosLogger.Log(
                    $"CATALOG: версия не изменилась ({remote.Token}) — полная загрузка пропущена; остатки — не позже чем через {(lastFull + FullReloadInterval - DateTime.UtcNow).TotalMinutes:0} мин.",
                    "CATALOG");
            }

            return CatalogSyncResult.Ok(0, 0, 0);
        }

        PosLogger.Log($"CATALOG: версия изменилась ({local ?? "—"} → {remote?.Token ?? "нет ответа"}) — полная загрузка.", "CATALOG");
        // Версия уже получена — повторно её не спрашиваем (см. SyncCatalogFullCoreAsync).
        return await StartFullSync(cancellationToken, remote, reason: "версия каталога на сервере изменилась, лента не отвечает").ConfigureAwait(false);
    }

    /// <summary>2026-11-02 (аудит №2, Н3-01): после УСПЕШНОЙ полной загрузки — уплотнение кучи. Состояние самой загрузки (списки, плитки) к этому
    /// моменту уже освобождено (метод-тело завершён), поэтому сборщик может вернуть ОС страницы JSON, лежавшие в куче больших объектов.</summary>
    private async Task<CatalogSyncResult> SyncCatalogFullCoreAsync(CancellationToken cancellationToken = default,
        CatalogVersionInfo? knownVersion = null)
    {
        var result = await SyncCatalogFullBodyAsync(cancellationToken, knownVersion).ConfigureAwait(false);
        if (result.Success)
            CompactMemoryAfterFullLoad();
        return result;
    }

    /// <summary>Уплотнение кучи один раз после полной загрузки (на Android так уже сделано в MainActivity). Кассир за чеком — только неблокирующий сбор
    /// (без паузы экрана); иначе — один блокирующий уплотняющий сбор (десятки–сотни мс), раз в 8 часов.</summary>
    private static void CompactMemoryAfterFullLoad()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            if (SyncCenter.OpenCheck)
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            else
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            // Одна строка для живого замера: рабочий набор процесса и управляемая куча после уплотнения.
            PosLogger.Log($"CATALOG full: после загрузки память процесса {Environment.WorkingSet / (1024 * 1024)} МБ, управляемая куча {GC.GetTotalMemory(false) / (1024 * 1024)} МБ{(SyncCenter.OpenCheck ? " (сбор без паузы, кассир за чеком)" : " (уплотнено)")}.", "CATALOG");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG full: уплотнение кучи не удалось: {ex.Message}", "CATALOG");
        }
    }

    private async Task<CatalogSyncResult> SyncCatalogFullBodyAsync(CancellationToken cancellationToken = default,
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
            var apiBaseUrl = _settings.ApiBaseUrl;
            // 2026-11-02 (Н3-01): сразу нужного размера (known каталог) — без удвоений массива в куче больших объектов.
            var newList = new List<CatalogProductTileVm>(Math.Max(16, _products.Count + 64));

            // 2026-11-06 (Б-9): база уже есть — загрузка идёт пачками по ~1000 товаров: каждая пачка сразу пишется в базу (короткая запись, замок SQLite не держится
            // минуту), а после неё сохраняется «следующая страница». Обрыв (журнал 13:34, страница 151 из 155) не возвращает к первой странице: следующая загрузка
            // продолжает с сохранённой. Пустая база (холодный старт) идёт прежним путём выше.
            var chunked = onPage is null;
            var repo = LocalProductRepository.Instance;
            var chunk = new List<CatalogProductTileVm>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CatalogFullResume? resume = null;
            int cAdded = 0, cChanged = 0, cDeleted = 0, cWeightFlag = 0, cStored = 0, resumedBase = 0;
            async Task FlushChunkAsync()
            {
                if (chunk.Count == 0)
                    return;
                if (generation != Volatile.Read(ref _accountGeneration))
                    throw new AccountChangedDuringLoadException();
                cWeightFlag += chunk.Count(t => t.ServerIsWeight.HasValue);
                await PrepareTilesForStoreAsync(chunk, snapshotStartedUtc, cancellationToken).ConfigureAwait(false);
                var (a, c, _) = await Task.Run(() => repo.SyncReplaceAllWithDiff(chunk, deleteMissing: false), cancellationToken).ConfigureAwait(false);
                cAdded += a;
                cChanged += c;
                cStored += chunk.Count;
                chunk.Clear();
            }

            if (chunked)
            {
                var progress = CatalogFullLoadMarks.GetProgress(repo, DateTime.UtcNow);
                var startPage = progress is { } pr ? CatalogFullLoadMarks.ResumePage(pr) : 1;
                resumedBase = startPage > 1 && progress is { } pr2 ? pr2.Accepted : 0;
                resume = new CatalogFullResume
                {
                    StartPage = startPage,
                    PageSize = progress?.PageSize ?? 0,
                    PageDone = async (nextPage, size, _) =>
                    {
                        if (chunk.Count < FullLoadChunkTiles)
                            return;
                        await FlushChunkAsync().ConfigureAwait(false);
                        CatalogFullLoadMarks.SaveProgress(repo, new CatalogFullLoadMarks.Progress(nextPage, size, DateTime.UtcNow, resumedBase + seenIds.Count));
                    },
                };
            }

            try
            {
                // 2026-10-30 (холодный старт): сначала лента по 500 (32 запроса на 16 тыс. вместо 160 страниц по 100); не вышло или отдала не всё — по страницам.
                if (onPage is not null)
                    (rawItems, feedCursor) = await TryFeedSnapshotAsync(remoteVersion, generation, cancellationToken).ConfigureAwait(false);
                // 2026-11-02 (Н3-01, память): страницы разбираются в плитки по одной и сразу забываются — 15 тыс. JsonElement (документы страниц в куче больших
                // объектов, десятки МБ) больше не копятся до конца загрузки и не живут вместе со списком плиток, словарями разницы и записью в базу.
                if (rawItems is null)
                    await _catalogApi.ProductsCatalogStreamAsync(
                        _settings.Catalog.QuickCatalogLimit,
                        _settings.Catalog.CatalogMaxPages,
                        batch =>
                        {
                            foreach (var el in batch)
                            {
                                var tile = ProductCatalogMapper.TryTile(el, apiBaseUrl);
                                if (tile == null)
                                    continue;
                                if (!chunked)
                                    newList.Add(tile);
                                else
                                {
                                    chunk.Add(tile);
                                    if (!string.IsNullOrEmpty(tile.Id))
                                        seenIds.Add(tile.Id);
                                }
                            }
                        },
                        cancellationToken, resume).ConfigureAwait(false);
            }
            finally
            {
                if (onPage is not null)
                    CatalogApiService.FullLoadPage -= onPage;
            }

            if (rawItems is not null)
            {
                // Первая загрузка лентой (холодный старт): список уже целиком в памяти — разбираем и отпускаем его до записи в базу.
                foreach (JsonElement el in rawItems)
                {
                    var vm = ProductCatalogMapper.TryTile(el, apiBaseUrl);
                    if (vm != null)
                        newList.Add(vm);
                }

                rawItems = null;
            }

            int added, changed, deleted, totalCount;
            // 2026-09-28: акции товаров (запомнены в TryTile) — на диск, для запуска без интернета.
            PromotionRules.SaveIfDirty();

            if (chunked)
            {
                // 2026-11-06 (Б-9): последняя пачка; лишнее из базы убираем только если загрузка прошла ВСЕ страницы с первой (после докачки список не полный —
                // удаления тогда принесёт лента, которая и так ведёт дельту).
                await FlushChunkAsync().ConfigureAwait(false);
                if (resume!.FromStart)
                    cDeleted = repo.DeleteProductsNotIn(seenIds);
                else
                    PosLogger.Log($"CATALOG full: докачка закончена (принято {seenIds.Count} с докачанной части) — удаления не сверяю, их приносит лента.", "CATALOG");
                CatalogFullLoadMarks.ClearProgress(repo);
                (added, changed, deleted) = (cAdded, cChanged, cDeleted);
                totalCount = resume.FromStart ? seenIds.Count : repo.CountProducts();
                PosLogger.Log($"CATALOG sync: is_weight прислан у {cWeightFlag} из {cStored} товаров.", "CATALOG");
            }
            else
            {
                // 2026-11-01 (приёмка Ф-5): диагностика — у скольких товаров полная загрузка принесла is_weight (NULL остаётся только у тех, где сервер поле не прислал).
                PosLogger.Log($"CATALOG sync: is_weight прислан у {newList.Count(t => t.ServerIsWeight.HasValue)} из {newList.Count} товаров.", "CATALOG");

                // Пока товары качались, сменился аккаунт — это каталог прежнего, в базу нового его не пишем.
                if (generation != Volatile.Read(ref _accountGeneration))
                {
                    PosLogger.Log("CATALOG sync: аккаунт сменился во время загрузки — каталог прежнего аккаунта отброшен.", "CATALOG");
                    return CatalogSyncResult.Failed("Аккаунт сменился во время загрузки каталога.");
                }

                await PrepareTilesForStoreAsync(newList, snapshotStartedUtc, cancellationToken).ConfigureAwait(false);
                (added, changed, deleted) = LocalProductRepository.Instance.SyncReplaceAllWithDiff(newList);
                totalCount = newList.Count;
            }

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
            // 2026-11-06 (Б-1): метка «полная загрузка полными карточками выполнена» — по ней самолечение не повторяется, а перезапуск не обнуляет интервал.
            CatalogFullLoadMarks.MarkFullLoad(LocalProductRepository.Instance, syncTime);
            CatalogCacheService.NoteSynced(syncTime); // 2026-10-30: «каталог свежий» и для окон (массовая загрузка)

            _auditDb.LogEvent("catalog", "refresh", new { count = totalCount, added, changed, deleted });
            PosLogger.Log(
                $"CATALOG sync: added={added}, changed={changed}, deleted={deleted}, total={totalCount}",
                "CATALOG");

            // 2026-11-02 (Н3-01, память): каталог на экране — те же объекты плиток, что держит кэш базы (как после ленты), а не вторая копия 15 тыс. плиток
            // из newList (она жила рядом с кэшем до следующего изменения ленты). Базу не прочитать (пусто/сбой) — как раньше, список этой загрузки.
            if (!TryLoadFromDatabase() && !chunked)
            {
                _products = newList;
                SyncInMemoryCatalog();
            }

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
                _lastFullLoadUtc = syncTime;
            }

            SyncCenter.RecordSuccess("catalog");

            return okResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AccountChangedDuringLoadException)
        {
            PosLogger.Log("CATALOG sync: аккаунт сменился во время загрузки — каталог прежнего аккаунта отброшен.", "CATALOG");
            return CatalogSyncResult.Failed("Аккаунт сменился во время загрузки каталога.");
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

    /// <summary>2026-11-06 (Б-9): сколько товаров копится до записи пачки в базу при полной загрузке (около 10 страниц по 100).</summary>
    private const int FullLoadChunkTiles = 1000;

    private sealed class AccountChangedDuringLoadException : Exception;

    /// <summary>Подготовка загруженных карточек к записи в базу (вынесено из SyncCatalogFullBodyAsync, чтобы то же самое делала каждая пачка докачки): остатки
    /// агента, подпись остатка, защита остатков, изменённых на этом ПК, и вычет продаж соседних касс.</summary>
    private static async Task PrepareTilesForStoreAsync(List<CatalogProductTileVm> list, DateTime snapshotStartedUtc, CancellationToken ct)
    {
        await StockSyncService.OverlayAgentStockAsync(list, ct).ConfigureAwait(false);

        // Гарантируем StockInfo перед записью в SQLite (на случай отсутствия overlay).
        foreach (var vm in list)
        {
            if (string.IsNullOrWhiteSpace(vm.StockInfo))
                StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
        }

        // 2026-10-29: товар, остаток которого менялся на этом ПК за последние 90 с (своя продажа/возврат/ревизия), — остаток в снимке мог быть
        // взят до того, как сервер учёл операцию: оставляем локальный (иначе «остаток откатывается» после продажи).
        foreach (var vm in list)
        {
            // 2026-11-01 (П-4): и всё, что менялось ПОСЛЕ начала загрузки (долгая загрузка 2–6 мин): страницы до продажи несут остаток без неё.
            if (vm.Id is null || !(StockSyncService.WasLocallyTouched(vm.Id, FeedStockGuard.LocalTouchWindow) || StockSyncService.TouchedSince(vm.Id, snapshotStartedUtc)))
                continue;
            if (LocalProductRepository.Instance.LoadTileCopy(vm.Id) is { } keep)
                StockSyncService.ApplyQuantityToTile(vm, keep.Quantity, vm.MustWeigh);
            StockSyncService.ScheduleStockReread(vm.Id, FeedStockGuard.LocalTouchWindow + TimeSpan.FromSeconds(5)); // 2026-11-01 (С-4): оставили локальный — потом перечитаем с сервера
        }

        // Продажи соседних касс, которых сервер ещё не знает, вычитаются из снимка до записи.
        NurMarketKassa.Services.Lan.LanStockAdjuster.AdjustSnapshot(list, snapshotStartedUtc);
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
