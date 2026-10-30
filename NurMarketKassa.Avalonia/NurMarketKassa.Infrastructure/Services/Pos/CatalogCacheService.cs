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

    /// <summary>2026-10-30: каталог сверен с сервером (лентой или полной загрузкой) — для «каталог старше 10 минут» массовой загрузки.</summary>
    public static void NoteSynced(DateTime utc) => LastSyncTime = utc;

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

            await StockSyncService.OverlayAgentStockAsync(newList, cancellationToken).ConfigureAwait(false);
            foreach (var vm in newList)
            {
                if (string.IsNullOrWhiteSpace(vm.StockInfo))
                    StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
            }

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
