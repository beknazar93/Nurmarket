using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

public sealed class ProductSearchResult
{
    public IReadOnlyList<CatalogProductTileVm> Items { get; init; } = Array.Empty<CatalogProductTileVm>();
    public bool HasMore { get; init; }
}

public sealed class ProductSearchService
{
    public ProductSearchService(PostgreSqlSettings postgres) { }

    public Task WarmUpLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ProductSearchResult> SearchAsync(string? query, int skip = 0, int take = 50, CancellationToken ct = default) =>
        Task.FromResult(new ProductSearchResult());
}
