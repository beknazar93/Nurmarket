using BenchmarkDotNet.Attributes;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Bench;

/// <summary>Поиск по каталогу из 15 000 товаров: штрихкод (словарь) и поиск по тексту (страница из 30). То, что кассир ощущает при вводе в строку поиска.</summary>
[MemoryDiagnoser]
public class CatalogSearchBench
{
    private const int Count = 15_000;
    private readonly LocalProductRepository _repository = LocalProductRepository.Instance;

    [GlobalSetup]
    public void Setup()
    {
        _repository.EnsureSchema();
        var tiles = new List<CatalogProductTileVm>(Count);
        for (var i = 0; i < Count; i++)
        {
            var tile = new CatalogProductTileVm($"bench-{i:D6}", $"Молоко Простоквашино {i % 50} Товар {i:D6}", $"{10 + i % 500:0.00} сом", false)
            {
                Barcode = $"46{i:D11}",
                Unit = "шт",
                Quantity = 10,
                Category = $"Категория {i % 40}",
                Brand = $"Бренд {i % 25}",
            };
            ProductUnitNormalizer.ApplyToTile(tile);
            tiles.Add(tile);
        }
        _repository.SyncReplaceAllWithDiff(tiles);
    }

    [GlobalCleanup]
    public void Cleanup() => _repository.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());

    [Benchmark]
    public object? ByBarcode() => _repository.TryGetTileByBarcode("46" + 7_777.ToString("D11"));

    [Benchmark]
    public int TextSearch_Page30() => _repository.SearchFullCatalogText("простоква", offset: 0, limit: 30).Count;

    [Benchmark]
    public int TextSearch_RareWord() => _repository.SearchFullCatalogText("007777", offset: 0, limit: 30).Count;
}
