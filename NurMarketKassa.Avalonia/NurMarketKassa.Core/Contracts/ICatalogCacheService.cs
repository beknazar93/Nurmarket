using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Core.Contracts;

/// <summary>Cross-platform catalog cache (local SQLite + remote sync).</summary>
public interface ICatalogCacheService
{
    Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default);

    bool TryLoadFromDatabase();

    IReadOnlyList<CatalogProductTileVm> GetProducts();
}
