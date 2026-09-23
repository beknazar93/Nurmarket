using System.Text.Json;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

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
    }

    public bool TryLoadFromDatabase()
    {
        try
        {
            LocalProductRepository.Instance.EnsureSchema();
            var tiles = LocalProductRepository.Instance.LoadAllTiles();
            _products = tiles.ToList();
            SyncInMemoryCatalog();
            return tiles.Count > 0;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG load from SQLite failed: {ex}", "CATALOG");
            return false;
        }
    }

    public IReadOnlyList<CatalogProductTileVm> GetProducts() => _products;

    public async Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default)
    {
        if (OfflineModeHelper.UseLocalOperations)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");

        try
        {
            var remoteVersion = await _catalogApi
                .ProductsCatalogVersionAsync(cancellationToken)
                .ConfigureAwait(false);

            var rawItems = await _catalogApi.ProductsCatalogAsync(
                _settings.Catalog.QuickCatalogLimit,
                _settings.Catalog.CatalogMaxPages,
                cancellationToken).ConfigureAwait(false);

            var apiBaseUrl = _settings.ApiBaseUrl;
            var newList = new List<CatalogProductTileVm>();

            foreach (JsonElement el in rawItems)
            {
                var vm = ProductCatalogMapper.TryTile(el, apiBaseUrl);
                if (vm != null)
                    newList.Add(vm);
            }

            await StockSyncService.OverlayAgentStockAsync(newList, cancellationToken).ConfigureAwait(false);

            // Гарантируем StockInfo перед записью в SQLite (на случай отсутствия overlay).
            foreach (var vm in newList)
            {
                if (string.IsNullOrWhiteSpace(vm.StockInfo))
                    StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
            }

            var (added, changed, deleted) = LocalProductRepository.Instance.SyncReplaceAllWithDiff(newList);

            if (remoteVersion != null && !remoteVersion.IsEmpty)
                LocalProductRepository.Instance.SetCatalogVersionToken(remoteVersion.Token);

            var syncTime = DateTime.UtcNow;
            LocalProductRepository.Instance.SetLastSyncTime(syncTime);

            _auditDb.LogEvent("catalog", "refresh", new { count = newList.Count, added, changed, deleted });
            PosLogger.Log(
                $"CATALOG sync: added={added}, changed={changed}, deleted={deleted}, total={newList.Count}",
                "CATALOG");

            _products = newList;
            SyncInMemoryCatalog();
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
            PosLogger.Log($"CATALOG sync error: {ex}", "CATALOG");
            return CatalogSyncResult.Failed(ex.Message);
        }
    }

    private void SyncInMemoryCatalog()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_products);
    }
}
