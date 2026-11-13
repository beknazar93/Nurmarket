using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Ui.Shared;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>
/// Platform-agnostic in-memory catalog cache backed by SQLite persistence.
/// UI hosts subscribe to <see cref="CacheUpdated"/> and <see cref="ToastRequested"/>.
/// </summary>
public static class CatalogCacheService
{
    private static readonly LocalProductRepository Repository = LocalProductRepository.Instance;

    public static List<CatalogProductTileVm> Products { get; } = [];

    // 2026-11-06 (шаг 17, ядро): поиск плитки по id был FirstOrDefault по всему списку — на ревизии и продаже 15 000 × число строк. Индекс id → позиция строится лениво
    // и проверяется при каждом обращении (плитка на своём месте и с тем же id), поэтому правки списка в любом месте (Add, замена по индексу, Clear + AddRange) его не ломают:
    // устаревший индекс перестраивается, а промах без явных признаков устаревания проверяется прежним линейным поиском — результат всегда тот же, что у FirstOrDefault.
    private static readonly object IdIndexGate = new();
    private static Dictionary<string, int>? _idIndex;
    private static int _idIndexCount = -1;

    /// <summary>Плитка каталога по id (без учёта регистра) — то же, что <c>Products.FirstOrDefault(p => p.Id == id)</c>, но без прохода по всему списку.</summary>
    public static CatalogProductTileVm? FindProductById(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        lock (IdIndexGate)
        {
            var list = Products;
            var stale = _idIndex is null || _idIndexCount != list.Count;
            if (!stale && _idIndex!.TryGetValue(id, out var at))
            {
                if (at < list.Count && string.Equals(list[at].Id, id, StringComparison.OrdinalIgnoreCase))
                    return list[at];
                stale = true; // плитку заменили или убрали — индекс устарел
            }

            if (stale)
            {
                RebuildIdIndex(list);
                return _idIndex!.TryGetValue(id, out var fresh) ? list[fresh] : null;
            }

            // Индекс свежий по числу плиток и id в нём нет: на всякий случай (замена плитки на плитку с другим id при том же числе) — прежний линейный поиск.
            var found = list.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                RebuildIdIndex(list);
            return found;
        }
    }

    private static void RebuildIdIndex(List<CatalogProductTileVm> list)
    {
        var map = new Dictionary<string, int>(list.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
            if (!string.IsNullOrEmpty(list[i].Id))
                map.TryAdd(list[i].Id, i); // при одинаковых id — первая, как у FirstOrDefault
        _idIndex = map;
        _idIndexCount = list.Count;
    }

    public static DateTime? LastSyncTime { get; private set; }

    public static string? LocalCatalogVersionToken => Repository.GetCatalogVersionToken();

    public static event Action? CacheUpdated;

    /// <summary>Каталог реально изменился после синхронизации с сервером (added/changed/deleted > 0),
    /// 2026-09-07. В отличие от <see cref="CacheUpdated"/> (любая замена in-memory списка, включая
    /// очистку при выходе) это сигнал именно «на сервере что-то поменялось — перечитай SQLite и
    /// перепубликуй плитки». Поднимают оба пути полного sync'а: статический (Склад после правки/
    /// удаления товара) и AvaloniaCatalogCacheService (фоновый SyncService). Слушает
    /// CatalogPanelViewModel — раньше фоновый sync до экрана кассира вообще не доходил.</summary>
    public static event Action? CatalogChanged;

    public static void NotifyCatalogChanged() =>
        UiDispatcherHolder.InvokeAsync(() => CatalogChanged?.Invoke());

    public static event Action<string, bool>? ToastRequested;

    /// <summary>Позволяет внешним вызывающим (диалогам вне этого класса) показать тот же тост,
    /// что и внутренние сбои синхронизации каталога — событие можно поднять только изнутри
    /// объявляющего класса.</summary>
    public static void RaiseToast(string message, bool isError) => ToastRequested?.Invoke(message, isError);

    public static void EnsureLocalDatabase() => Repository.EnsureSchema();

    // 2026-10-30 (оптимизация кассы, Б7): ProductCatalogMapper.TryTile спрашивал FavoriteIds на КАЖДЫЙ товар — запрос к SQLite × 15 000
    // (45 с одного ядра на полной загрузке, если сервер не присылает is_favorite). Теперь набор кэшируется на 5 с (хватает на всю загрузку)
    // и сбрасывается при SetFavorite. Возвращаемый набор только читают (Contains).
    private static readonly object FavoriteGate = new();
    private static HashSet<string>? _favoriteCache;
    private static long _favoriteCacheAt;

    public static HashSet<string> FavoriteIds
    {
        get
        {
            lock (FavoriteGate)
            {
                var now = Environment.TickCount64;
                if (_favoriteCache is null || now - _favoriteCacheAt > 5000)
                {
                    _favoriteCache = Repository.GetFavoriteIds();
                    _favoriteCacheAt = now;
                }

                return _favoriteCache;
            }
        }
    }

    public static void SetFavorite(string productId, bool isFavorite)
    {
        Repository.SetFavorite(productId, isFavorite);
        lock (FavoriteGate)
            _favoriteCache = null;
    }

    public static bool LoadFromDatabase()
    {
        try
        {
            var tiles = Repository.LoadAllTiles();
            SetProducts(tiles);
            LastSyncTime = Repository.GetLastSyncTime();
            return tiles.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetProducts(IEnumerable<CatalogProductTileVm> products)
    {
        UiDispatcherHolder.InvokeAsync(() =>
        {
            Products.Clear();
            foreach (var vm in products)
                Products.Add(vm);
            CacheUpdated?.Invoke();
        });
    }

    /// <summary>Очищает in-memory коллекцию каталога (без обращения к SQLite).</summary>
    public static void ClearInMemory()
    {
        UiDispatcherHolder.InvokeAsync(() =>
        {
            Products.Clear();
            CacheUpdated?.Invoke();
        });
        LastSyncTime = null;
    }

    public static IReadOnlyList<CatalogProductTileVm> ApplySqlFilter(FilterCriteria criteria)
    {
        var tiles = Repository.QueryFiltered(criteria);
        SetProducts(tiles);
        return tiles;
    }

    public static async Task<CatalogVersionInfo?> FetchRemoteVersionAsync(CancellationToken cancellationToken = default) =>
        await PosApp.CatalogApi.ProductsCatalogVersionAsync(cancellationToken).ConfigureAwait(false);

    public static void SaveLocalVersionToken(string token) =>
        Repository.SetCatalogVersionToken(token);

    public static bool IsSameVersion(CatalogVersionInfo remote)
    {
        var local = LocalCatalogVersionToken;
        if (string.IsNullOrWhiteSpace(local))
            return false;

        return string.Equals(local, remote.Token, StringComparison.Ordinal);
    }

    /// <summary>2026-10-30 (15 тыс. товаров): общий путь обновления каталога для окон (Склад, массовая загрузка, ревизия, правка товара).
    /// Ставит AvaloniaCatalogCacheService: лента изменений, если жива (секунды), иначе ОДНА полная загрузка на всю программу — раньше этот
    /// статический путь качал весь каталог сам, параллельно фоновой синхронизации (в журнале владельца 30.10 — до трёх полных загрузок
    /// по 155 страниц одновременно). null (стенды, старые хосты) — прежняя полная загрузка ниже.</summary>
    public static Func<CancellationToken, Task<CatalogSyncResult>>? SharedRefresh { get; set; }

    /// <summary>2026-10-30: «Обновить» на Складе — полная сверка всего каталога в фоне (одна на программу, не во время массовой загрузки).
    /// Ставит AvaloniaCatalogCacheService; возвращает задачу загрузки или null, если полная загрузка сейчас не нужна или запрещена.</summary>
    public static Func<Task<CatalogSyncResult>?>? RequestBackgroundFullReload { get; set; }

    /// <summary>2026-11-07: точечное обновление (см. <see cref="CatalogPointRefresh"/>) через хост программы: читает карточки товаров по id и применяет под замком ленты, обновляет
    /// плитки и сообщает экранам. Ставит AvaloniaCatalogCacheService. null (стенды, старые хосты) — <see cref="RefreshProductsAsync"/> делает то же сам.</summary>
    public static Func<IReadOnlyList<string>, CancellationToken, Task<CatalogSyncResult>>? SharedRefreshProducts { get; set; }

    /// <summary>2026-11-07, жалоба владельца «при каждом изменении товара весь склад загружается заново»: обновить в каталоге кассы только эти товары (правка, создание, цена, остаток, возврат из архива).
    /// Берётся карточка товара с сервера и применяется к базе и памяти; ПОЛНАЯ ЗАГРУЗКА КАТАЛОГА ЗДЕСЬ НЕ ЗАПУСКАЕТСЯ НИКОГДА, ни при нездоровой ленте, ни при паузе 429.
    /// Карточку не прочитали (нет связи, сервер медлит) — тихо оставляем как есть: правка уже на сервере, лента догонит. Не бросает (кроме отмены).</summary>
    public static async Task<CatalogSyncResult> RefreshProductsAsync(IEnumerable<string?> ids, CancellationToken cancellationToken = default)
    {
        var list = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            return CatalogSyncResult.Ok(0, 0, 0);
        // Без связи (или в аварии сервера) читать нечего: локальные правки уже в базе.
        if (OfflineModeHelper.SellLocally)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");

        try
        {
            if (SharedRefreshProducts is { } shared)
                return await shared(list, cancellationToken).ConfigureAwait(false);

            var outcome = await CatalogPointRefresh.ApplyByIdsAsync(PosApp.CatalogApi, list, PosApp.Settings?.ApiBaseUrl ?? "", FeedStockGuard.Create(), cancellationToken).ConfigureAwait(false);
            if (outcome.Applied)
            {
                LoadFromDatabase();
                NotifyCatalogChanged();
            }

            return outcome.Fetched == 0
                ? CatalogSyncResult.Failed("Карточка товара не получена — останется как была, обновит лента изменений.")
                : CatalogSyncResult.Ok(outcome.Added, outcome.Changed, outcome.Deleted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: точечное обновление товаров не удалось ({ex.GetType().Name}: {ex.Message}) — каталог не перекачиваем, донесёт лента.", "CATALOG");
            return CatalogSyncResult.Failed(ex.Message);
        }
    }

    /// <summary>2026-10-30: каталог сверен с сервером (лентой или полной загрузкой) — для «каталог старше 10 минут» массовой загрузки.</summary>
    public static void NoteSynced(DateTime utc)
    {
        LastSyncTime = utc;
        // 2026-11-07 (Б-B1): сервер только что ответил (лента, сверка, полная загрузка) — подписчикам (плашка «автономный режим» каталога).
        try { ServerAnswered?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"CATALOG: подписчик ServerAnswered упал: {ex.Message}", "WARNING"); }
    }

    /// <summary>2026-11-07: сервер ответил на запрос каталога (см. <see cref="NoteSynced"/>). Вызывается из любого потока.</summary>
    public static event Action? ServerAnswered;

    public static async Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default)
    {
        // 2026-09-29: и в аварии сервера — каталог из локальной базы, сервер не дёргаем.
        if (OfflineModeHelper.SellLocally)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");

        if (SharedRefresh is { } shared)
            return await shared(cancellationToken).ConfigureAwait(false);

        try
        {
            var snapshotStartedUtc = DateTime.UtcNow;
            var remoteVersion = await FetchRemoteVersionAsync(cancellationToken).ConfigureAwait(false);

            var rawItems = await PosApp.CatalogApi.ProductsCatalogAsync(
                PosApp.Settings.Catalog.QuickCatalogLimit,
                PosApp.Settings.Catalog.CatalogMaxPages,
                cancellationToken).ConfigureAwait(false);

            var apiBaseUrl = PosApp.Settings.ApiBaseUrl;
            var newList = new List<CatalogProductTileVm>();

            foreach (JsonElement el in rawItems)
            {
                var vm = ProductCatalogMapper.TryTile(el, apiBaseUrl);
                if (vm != null)
                    newList.Add(vm);
            }

            // 2026-09-28: акции товаров (запомнены в TryTile) — на диск, для запуска без интернета.
            PromotionRules.SaveIfDirty();
            ProductSerialRules.SaveIfDirty(); // 2026-11-13 (сервер sn1311): track_serials / warranty_months
            ProductSaleUnits.SaveIfDirty(); // 2026-11-13: sale_units

            await StockSyncService.OverlayAgentStockAsync(newList, cancellationToken).ConfigureAwait(false);
            foreach (var vm in newList)
            {
                if (string.IsNullOrWhiteSpace(vm.StockInfo))
                    StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
            }

            // 2026-11-13: запасной путь (нет хостовой службы каталога) тоже держит остаток товаров, менявшихся на этом ПК, и «приход в пути» — тот же общий метод, что у AvaloniaCatalogCacheService.
            StockSyncService.KeepLocalStockForTouched(newList, snapshotStartedUtc);

            // Продажи соседних касс, которых сервер ещё не знает, вычитаются из снимка до записи.
            Lan.LanStockAdjuster.AdjustSnapshot(newList, snapshotStartedUtc);
            var (added, changed, deleted) = Repository.SyncReplaceAllWithDiff(newList);

            if (remoteVersion != null && !remoteVersion.IsEmpty)
                Repository.SetCatalogVersionToken(remoteVersion.Token);

            LastSyncTime = DateTime.UtcNow;
            Repository.SetLastSyncTime(LastSyncTime.Value);
            PosApp.AuditDb.LogEvent("catalog", "refresh", new { count = newList.Count, added, changed, deleted });

            PosLogger.Log(
                $"CATALOG sync: added={added}, changed={changed}, deleted={deleted}, total={newList.Count}, version={remoteVersion?.Token}",
                "CATALOG");

            await UiDispatcherHolder.InvokeAsync(() =>
            {
                Products.Clear();
                foreach (var vm in newList)
                    Products.Add(vm);
                CacheUpdated?.Invoke();
            }).ConfigureAwait(false);

            if (added + changed + deleted > 0)
                NotifyCatalogChanged();

            return CatalogSyncResult.Ok(added, changed, deleted);
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
            return CatalogSyncResult.Failed(ex.Message);
        }
    }

    /// <summary>Обратная совместимость — полная синхронизация.</summary>
    public static async Task RefreshFromApiAsync(CancellationToken cancellationToken = default)
    {
        var result = await SyncCatalogFullAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorMessage))
            ToastRequested?.Invoke(result.ErrorMessage, true);
    }

    public static void PersistProductStock(string productId, double quantity, bool mustWeigh)
    {
        // 2026-10-29: остаток менялся здесь (продажа, ревизия, приход) — лента каталога ещё ~90 с его не перезаписывает (FeedStockGuard).
        StockSyncService.MarkLocallyTouched(productId);
        Repository.UpdateStock(productId, quantity, mustWeigh);
    }
}
