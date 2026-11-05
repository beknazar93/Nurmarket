using BenchmarkDotNet.Attributes;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.Bench;

/// <summary>Сопоставление накладной с каталогом в 15 000 товаров: построение индекса и сверка строки (точное имя, сокращение с опечаткой, нет в каталоге).</summary>
[MemoryDiagnoser]
public class InvoiceMatcherBench
{
    private const int Count = 15_000;
    private List<CatalogProductTileVm> _tiles = [];
    private InvoiceCatalogIndex _index = null!;
    private InvoiceItem _exact = null!, _abbrev = null!, _missing = null!;

    private static readonly string[] Words = ["Молоко", "Шоколад", "Печенье", "Кока-кола", "Колбаса", "Йогурт", "Шампунь", "Мука", "Сахар", "Масло"];
    private static readonly string[] Brands = ["Простоквашино", "Аленка", "Юбилейное", "Докторская", "Савушкин", "Нивея", "Алтай", "Бишкек"];

    [GlobalSetup]
    public void Setup()
    {
        _tiles = new List<CatalogProductTileVm>(Count);
        for (var i = 0; i < Count; i++)
        {
            var title = $"{Words[i % Words.Length]} {Brands[(i / 10) % Brands.Length]} {100 + i % 900} г №{i}";
            _tiles.Add(new CatalogProductTileVm($"inv-{i:D6}", title, "10.00 сом", false) { Barcode = $"47{i:D11}", Unit = "шт", Quantity = 5 });
        }
        _index = new InvoiceCatalogIndex(_tiles);
        _exact = new InvoiceItem { Name = _tiles[7_000].Title };
        _abbrev = new InvoiceItem { Name = "Мол. Простоквашино 105г" };
        _missing = new InvoiceItem { Name = "Совсем другой товар 3000 мл" };
    }

    [Benchmark]
    public InvoiceCatalogIndex BuildIndex() => new(_tiles);

    [Benchmark]
    public InvoiceMatch Match_Exact() => _index.Match(_exact);

    [Benchmark]
    public InvoiceMatch Match_AbbreviationAndTypo() => _index.Match(_abbrev);

    [Benchmark]
    public InvoiceMatch Match_NotInCatalog() => _index.Match(_missing);

    [Benchmark]
    public double NameScore_Pair() => InvoiceCatalogIndex.NameScore("Мол. Простоквашино 1л", "Молоко Простоквашино 1 литр");
}
