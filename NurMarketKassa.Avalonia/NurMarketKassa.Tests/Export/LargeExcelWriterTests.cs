using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NurMarketKassa.Tests.Export;

/// <summary>2026-11-05: большие выгрузки Excel через MiniExcel (LargeExcelWriter): строки, форматы, второй лист, память против OpenXml-построителя.
/// Файлы — только во временной папке тестов (TestDataRoot).</summary>
public sealed class LargeExcelWriterTests
{
    private readonly ITestOutputHelper _output;
    private string _outputLine = "";

    public LargeExcelWriterTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<XlColumn> Columns =
    [
        new("№", XlKind.Index),
        new("Название", XlKind.Text, MaxWidth: 50),
        new("Штрихкод", XlKind.Code),
        new("Остаток", XlKind.Quantity, XlTotal.Sum),
        new("Цена, сом", XlKind.Money),
        new("Доля", XlKind.Percent),
        new("Время", XlKind.DateTime),
    ];

    private static IEnumerable<object?[]> Rows(int count)
    {
        var when = new DateTime(2026, 11, 5, 14, 30, 0);
        for (var i = 1; i <= count; i++)
            yield return [i, "Товар № " + i + " Мол. 1л", "46012345" + i.ToString("00000"), i % 7 == 0 ? 2.5 : i, 99.999m + i, 12.5, new XlCell(when.AddMinutes(i), ReportTone.None)];
    }

    private static string TempFile(string name) => Path.Combine(TestDataRoot.Path, name);

    private static List<Row> ReadRows(string path, int sheetIndex, out string[] sheetNames)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        sheetNames = wb.Workbook.Descendants<Sheet>().Select(s => s.Name!.Value!).ToArray();
        var part = (WorksheetPart)wb.GetPartById(wb.Workbook.Descendants<Sheet>().ElementAt(sheetIndex).Id!);
        return part.Worksheet.Descendants<Row>().ToList();
    }

    [Fact]
    public void Write_6000Rows_HasHeaderAllRowsAndSummarySheet()
    {
        var path = TempFile("large-6000.xlsx");
        var summary = new List<XlKeyValue> { new("Строк в таблице", 6000, XlKind.Integer), new("Магазин", "NBS") };

        LargeExcelWriter.Write(path, "Ревизия", Columns, Rows(6000), "Итоги", summary);

        var rows = ReadRows(path, 0, out var names);
        names.Should().Equal("Ревизия", "Итоги");
        rows.Should().HaveCount(6001); // шапка + 6000
        var second = ReadRows(path, 1, out _);
        second.Should().HaveCountGreaterThanOrEqualTo(3); // шапка «Показатель/Значение» + 2 строки
    }

    [Fact]
    public void Write_AppliesColumnNamesAndNumberFormats()
    {
        var path = TempFile("large-formats.xlsx");
        LargeExcelWriter.Write(path, "Лист", Columns, Rows(10));

        using var zip = ZipFile.OpenRead(path);
        string Read(string name)
        {
            using var r = new StreamReader(zip.GetEntry(name)!.Open());
            return r.ReadToEnd();
        }
        var all = string.Join("\n", zip.Entries.Where(e => e.FullName.EndsWith(".xml")).Select(e => Read(e.FullName)));
        all.Should().Contain("Название").And.Contain("Цена, сом");
        all.Should().Contain("#,##0.00");
        all.Should().Contain("dd.mm.yyyy hh:mm");
    }

    [Fact]
    public void Write_MoneyQuantityPercentAndDateCellsStayNumbers()
    {
        // 2026-11-05 (ревью волн 0–3, С12): формат ячейки не должен превращать число в текст (иначе в Excel не считается «Сумма», а дата превращается в мусор).
        var path = TempFile("large-types.xlsx");
        LargeExcelWriter.Write(path, "Лист", Columns, Rows(3));

        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart!;
        var part = (WorksheetPart)wb.GetPartById(wb.Workbook.Descendants<Sheet>().First().Id!);
        var row = part.Worksheet.Descendants<Row>().ElementAt(1);
        foreach (var column in new[] { "D", "E", "F", "G" }) // Остаток, Цена, Доля, Время
        {
            var cell = row.Elements<Cell>().First(c => c.CellReference!.Value!.StartsWith(column, StringComparison.Ordinal));
            (cell.DataType is null || cell.DataType.Value == CellValues.Number).Should().BeTrue($"колонка {column}: число, а не текст (тип {cell.DataType?.Value})");
            double.TryParse(cell.CellValue?.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _).Should().BeTrue($"колонка {column}: значение «{cell.CellValue?.Text}» читается как число");
        }
    }

    [Fact]
    public void Convert_CodeStaysText_PercentBecomesFraction_DateBecomesSerial()
    {
        LargeExcelWriter.Convert(4601234500001L, XlKind.Code).Should().Be("4601234500001");
        LargeExcelWriter.Convert(12.5, XlKind.Percent).Should().Be(0.125);
        LargeExcelWriter.Convert(new DateTime(2026, 11, 5), XlKind.Date).Should().Be(new DateTime(2026, 11, 5).ToOADate());
        LargeExcelWriter.Convert(new XlCell(3.14159m, ReportTone.Bad), XlKind.Money).Should().Be(3.14);
        LargeExcelWriter.Convert(null, XlKind.Money).Should().BeNull();
        LargeExcelWriter.Convert(double.NaN, XlKind.Money).Should().Be("");
    }

    [Fact]
    public void SectionTableExport_BelowAndAboveThreshold_BothWriteReadableFiles()
    {
        var small = new SectionTable("Тест", null, Columns, Rows(20).ToList());
        var big = new SectionTable("Тест большой", null, Columns, Rows(LargeExcelWriter.Threshold + 1).ToList());
        var pathSmall = TempFile("section-small.xlsx");
        var pathBig = TempFile("section-big.xlsx");

        SectionTableExport.ExportToExcel(pathSmall, small, "NBS");
        SectionTableExport.ExportToExcel(pathBig, big, "NBS");

        ReadRows(pathSmall, 0, out _).Count.Should().BeGreaterThan(20);
        ReadRows(pathBig, 0, out _).Should().HaveCount(LargeExcelWriter.Threshold + 2);
    }

    [Fact]
    public void Write_15000Rows_AllocatesFarLessThanOpenXmlBuilder()
    {
        const int count = 15000;
        var pathMini = TempFile("mem-mini.xlsx");
        var pathOpenXml = TempFile("mem-openxml.xlsx");

        GC.Collect(); // выделения считаются только этим потоком: другие тесты рядом цифры не искажают
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LargeExcelWriter.Write(pathMini, "Товары", Columns, Rows(count));
        sw.Stop();
        var mini = GC.GetAllocatedBytesForCurrentThread() - before;
        var miniMs = sw.ElapsedMilliseconds;

        GC.Collect(); // выделения считаются только этим потоком: другие тесты рядом цифры не искажают
        before = GC.GetAllocatedBytesForCurrentThread();
        sw.Restart();
        using (var book = new ExcelWorkbookBuilder(pathOpenXml, ReportMeta.Create("Товары", "NBS", null)))
        {
            book.AddSheet("Товары", "Товары").AddTable(Columns, Rows(count));
            book.Save();
        }
        sw.Stop();
        var openXml = GC.GetAllocatedBytesForCurrentThread() - before;

        _outputLine = $"15000 строк: MiniExcel выделил {mini / 1048576.0:0.0} МБ за {miniMs} мс, файл {new FileInfo(pathMini).Length / 1024} КБ; " +
                          $"OpenXml выделил {openXml / 1048576.0:0.0} МБ за {sw.ElapsedMilliseconds} мс, файл {new FileInfo(pathOpenXml).Length / 1024} КБ";
        _output.WriteLine(_outputLine);
        // Цифры для отчёта (xunit 2 не показывает вывод прошедших тестов): рядом с итогом test-safe.ps1, во временной папке.
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "nurmarket-tests", "excel-memory-last.txt"), _outputLine); } catch { /* только справка */ }
        mini.Should().BeLessThan(openXml);
        ReadRows(pathMini, 0, out _).Should().HaveCount(count + 1);
    }
}
