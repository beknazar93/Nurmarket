using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Core.Contracts;

/// <summary>Cross-platform catalog cache (local SQLite + remote sync).</summary>
public interface ICatalogCacheService
{
    Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default);

    /// <summary>2026-10-04, отчёт о производительности (п. 5): фоновая синхронизация (SyncService, раз в
    /// 2 мин) — каталог качается целиком, только если изменилась его «версия» на сервере или давно не было
    /// полной загрузки (остатки). По умолчанию — как раньше, полная загрузка.</summary>
    Task<CatalogSyncResult> SyncCatalogIfChangedAsync(CancellationToken cancellationToken = default) =>
        SyncCatalogFullAsync(cancellationToken);

    bool TryLoadFromDatabase();

    IReadOnlyList<CatalogProductTileVm> GetProducts();
}
