using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// Тесты in-memory индекса каталога (<see cref="LocalProductRepository"/>).
/// После рефакторинга логика индекса выделена в кэш с O(1) lookup по штрихкоду и текстовым поиском.
/// </summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class CatalogMemoryIndexTests : IDisposable
{
    private readonly LocalProductRepository _repository = LocalProductRepository.Instance;

    public CatalogMemoryIndexTests()
    {
        _repository.EnsureSchema();
    }

    public void Dispose()
    {
        _repository.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());
    }

    [Fact]
    public void TryGetTileByBarcode_ReturnsProductInstantly_ByExactBarcode()
    {
        var milk = CatalogTestHelper.CreateTile("milk-1", "Молоко 3.2%", "4601234567890", 120);
        var bread = CatalogTestHelper.CreateTile("bread-1", "Хлеб белый", "4609876543210", 50);
        _repository.SyncReplaceAllWithDiff([milk, bread]);

        var found = _repository.TryGetTileByBarcode("4601234567890");

        found.Should().NotBeNull();
        found!.Id.Should().Be("milk-1");
        found.Title.Should().Be("Молоко 3.2%");
        _repository.TryGetTileByBarcode("unknown-barcode").Should().BeNull();
    }

    [Fact]
    public void SearchFullCatalogText_FindsProduct_ByNameSubstring()
    {
        var milk = CatalogTestHelper.CreateTile("milk-1", "Молоко 3.2%", "4601234567890", 120);
        var bread = CatalogTestHelper.CreateTile("bread-1", "Хлеб белый", "4609876543210", 50);
        _repository.SyncReplaceAllWithDiff([milk, bread]);

        var results = _repository.SearchFullCatalogText("молок");

        results.Should().ContainSingle()
            .Which.Title.Should().Be("Молоко 3.2%");
    }

    [Fact]
    public void InvalidateMemoryIndex_RebuildsCache_AfterCatalogSync()
    {
        var milk = CatalogTestHelper.CreateTile("milk-1", "Молоко 3.2%", "4601234567890", 120);
        _repository.SyncReplaceAllWithDiff([milk]);

        _repository.TryGetTileByBarcode("4601234567890").Should().NotBeNull();
        _repository.LoadAllTiles().Should().HaveCount(1);

        var juice = CatalogTestHelper.CreateTile("juice-1", "Сок яблочный", "4601111222333", 90);
        _repository.SyncReplaceAllWithDiff([juice]);

        _repository.IsCacheReady.Should().BeTrue();
        _repository.TryGetTileByBarcode("4601234567890").Should().BeNull();
        _repository.TryGetTileByBarcode("4601111222333").Should().NotBeNull();
        _repository.LoadAllTiles().Should().ContainSingle()
            .Which.Title.Should().Be("Сок яблочный");

        _repository.InvalidateMemoryIndex();

        _repository.IsCacheReady.Should().BeTrue();
        _repository.TryGetTileByBarcode("4601111222333").Should().NotBeNull();
        _repository.SearchFullCatalogText("яблоч").Should().ContainSingle()
            .Which.Id.Should().Be("juice-1");
    }
}
