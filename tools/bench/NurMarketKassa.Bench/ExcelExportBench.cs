using BenchmarkDotNet.Attributes;
using NurMarketKassa.Services;

namespace NurMarketKassa.Bench;

/// <summary>Выгрузка 15 000 строк в Excel: отчётный построитель OpenXml (стили, «Итого») против потоковой записи MiniExcel (LargeExcelWriter). Смотреть колонки Allocated и Mean.</summary>
[MemoryDiagnoser]
public class ExcelExportBench
{
    private const int Count = 15_000;
    private string _dir = "";

    private static readonly IReadOnlyList<XlColumn> Columns =
    [
        new("№", XlKind.Index),
        new("Название", XlKind.Text, MaxWidth: 50),
        new("Штрихкод", XlKind.Code),
        new("Остаток", XlKind.Quantity, XlTotal.Sum),
        new("Цена, сом", XlKind.Money),
        new("Время", XlKind.DateTime),
    ];

    private static IEnumerable<object?[]> Rows()
    {
        var when = new DateTime(2026, 11, 5, 14, 30, 0);
        for (var i = 1; i <= Count; i++)
            yield return [i, "Товар № " + i, "46012345" + i.ToString("00000"), i, 99.5m + i, when.AddMinutes(i)];
    }

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nurmarket-bench", "excel-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_dir);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    [Benchmark(Baseline = true)]
    public void OpenXmlBuilder()
    {
        using var book = new ExcelWorkbookBuilder(Path.Combine(_dir, "openxml.xlsx"), ReportMeta.Create("Товары", "NBS", null));
        book.AddSheet("Товары", "Товары").AddTable(Columns, Rows());
        book.Save();
    }

    [Benchmark]
    public void MiniExcelStream() => LargeExcelWriter.Write(Path.Combine(_dir, "mini.xlsx"), "Товары", Columns, Rows());
}
