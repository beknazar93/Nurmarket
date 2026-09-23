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

    public static event Action<string, bool>? ToastRequested;

    public static void EnsureLocalDatabase() => Repository.EnsureSchema();

    public static HashSet<string> FavoriteIds => Repository.GetFavoriteIds();

    public static void SetFavorite(string productId, bool isFavorite) =>
        Repository.SetFavorite(productId, isFavorite);

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

    public static async Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default)
    {
        if (OfflineModeHelper.UseLocalOperations)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");

        try
        {
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

            await StockSyncService.OverlayAgentStockAsync(newList, cancellationToken).ConfigureAwait(false);
            foreach (var vm in newList)
            {
                if (string.IsNullOrWhiteSpace(vm.StockInfo))
                    StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
            }

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

    public static void PersistProductStock(string productId, double quantity, bool mustWeigh) =>
        Repository.UpdateStock(productId, quantity, mustWeigh);
}
