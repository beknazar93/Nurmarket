using System.Diagnostics;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Load;

/// <summary>
/// НАГРУЗКА: in-memory индекс каталога (<see cref="LocalProductRepository"/>) на объёме
/// ~30 000 товаров — верхняя граница реального каталога сетевого магазина.
/// <para>
/// Проверяется, что после построения индекса поиск по подстроке названия и точный lookup
/// по штрихкоду остаются интерактивно быстрыми (кассир печатает в строке поиска и ждёт
/// отклика) и возвращают корректный результат для заведомо внедрённого маркерного товара.
/// </para>
/// <para>
/// Репозиторий — синглтон поверх общего SQLite-файла внутри NURMARKET_DATA_ROOT (2026-11-05: раньше комментарий ошибочно говорил «в каталоге сборки»;
/// в тестах это уникальная временная папка, см. TestDataRoot),
/// поэтому класс включён в ту же xUnit-коллекцию, что и <c>CatalogMemoryIndexTests</c>,
/// чтобы тесты не топтали общий индекс параллельно.
/// </para>
/// </summary>
[Collection("CatalogMemoryIndexTests")]
public sealed class CatalogMemoryIndexLoadTests : IDisposable
{
    private const int ProductCount = 30_000;
    private const string MarkerId = "test-load-catalog-marker";
    private const string MarkerTitle = "Маркерный Уникальнейший Товар Нагрузки";
    private const string MarkerBarcode = "4600000999999";

    private readonly LocalProductRepository _repository = LocalProductRepository.Instance;

    public CatalogMemoryIndexLoadTests() => _repository.EnsureSchema();

    public void Dispose() => _repository.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());

    [Fact]
    public void BarcodeLookupAndTextSearch_StayFast_OnThirtyThousandProducts()
    {
        SeedLargeCatalog();

        _repository.IsCacheReady.Should().BeTrue();
        _repository.LoadAllTiles().Should().HaveCount(ProductCount + 1);

        // Прогрев JIT/веток поиска, чтобы измерять устоявшуюся стоимость запроса.
        _repository.TryGetTileByBarcode("4600000000001");
        _repository.SearchFullCatalogText("нагруз");

        var barcodeWatch = Stopwatch.StartNew();
        var byBarcode = _repository.TryGetTileByBarcode(MarkerBarcode);
        barcodeWatch.Stop();

        byBarcode.Should().NotBeNull();
        byBarcode!.Id.Should().Be(MarkerId);
        byBarcode.Title.Should().Be(MarkerTitle);
        barcodeWatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromMilliseconds(300),
            "lookup по штрихкоду обязан быть O(1) по словарю, а занял {0} мс на {1} товарах",
            barcodeWatch.ElapsedMilliseconds,
            ProductCount);

        // 2026-11-05: замер устойчивый — медиана из 5 прогонов и предел 600 мс (на загруженном ПК один прогон давал 375–499 мс против 300);
        // смысл проверки прежний: результат верный, поиск не «подвешивает» строку.
        var byText = _repository.SearchFullCatalogText("уникальнейший");
        byText.Should().ContainSingle()
            .Which.Id.Should().Be(MarkerId);
        var searchMedian = MedianMs(5, () => _repository.SearchFullCatalogText("уникальнейший"));
        searchMedian.Should().BeLessThan(
            600,
            "полнотекстовый поиск по {0} товарам занял {1} мс (медиана из 5) — кассир увидит подвисание строки поиска",
            ProductCount,
            searchMedian);
    }

    [Fact]
    public void SearchFullCatalogText_ReturnsPagedSlice_WhenQueryMatchesEveryProduct()
    {
        SeedLargeCatalog();

        var page = _repository.SearchFullCatalogText("нагрузочный", offset: 0, limit: 30);

        page.Should().HaveCount(30, "результат обязан быть страницей, а не всей выборкой в UI");
        page.Should().OnlyContain(x => x.Title.Contains("Нагрузочный", StringComparison.OrdinalIgnoreCase));
        // 2026-11-05: медиана из 5 прогонов и предел 600 мс (см. выше).
        var median = MedianMs(5, () => _repository.SearchFullCatalogText("нагрузочный", offset: 0, limit: 30));
        median.Should().BeLessThan(
            600,
            "поиск с максимально широким совпадением занял {0} мс (медиана из 5)",
            median);
    }

    /// <summary>Медиана времени (мс) из <paramref name="runs"/> прогонов — один выброс на загруженном ПК не роняет тест.</summary>
    private static long MedianMs(int runs, Action action)
    {
        var times = new List<long>(runs);
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            action();
            watch.Stop();
            times.Add(watch.ElapsedMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    private void SeedLargeCatalog()
    {
        var tiles = new List<CatalogProductTileVm>(ProductCount + 1);
        for (var i = 0; i < ProductCount; i++)
        {
            var tile = CatalogTestHelper.CreateTile(
                $"test-load-cat-{i:D6}",
                $"Нагрузочный товар {i:D6}",
                $"46{i:D11}",
                price: 10 + (i % 500),
                mustWeigh: i % 7 == 0);
            tile.Category = $"Категория {i % 40}";
            tile.Brand = $"Бренд {i % 25}";
            tiles.Add(tile);
        }

        tiles.Add(CatalogTestHelper.CreateTile(MarkerId, MarkerTitle, MarkerBarcode, price: 777));

        _repository.SyncReplaceAllWithDiff(tiles);
    }
}
