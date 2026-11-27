using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using MiniExcelLibs;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Export;

/// <summary>2026-11-26 (выгрузка в 1С, OneCExport): точность сумм (файл = отчёт до копейки), кодировка и кириллица CSV, книга Excel с шапкой в первой строке, CommerceML import.xml/offers.xml.
/// Файлы — только во временной папке тестов (TestDataRoot).</summary>
public sealed class OneCExportTests
{
    private static readonly DateTime Day = new(2026, 11, 20, 14, 5, 9);

    private static string TempFile(string name) => Path.Combine(TestDataRoot.Path, "1c-" + Guid.NewGuid().ToString("N")[..6] + "-" + name);

    // Суммы с «неудобными» копейками: 0,1 + 0,2 в double не равно 0,3, 2,675 — граница округления.
    private static SalesExportService.Receipt Receipt(string number, double total, double discount = 0, string? client = null, params SalesExportService.Line[] lines) =>
        new("id-" + number, Day, number, total, discount, "cash", "paid", "Айгуль", client, lines.FirstOrDefault()?.Name, lines);

    private static OneCExport.Data Data(IReadOnlyList<SalesExportService.Receipt>? sales = null, IReadOnlyList<OneCExport.ProductRow>? products = null) =>
        new(new DateTime(2026, 11, 1), new DateTime(2026, 11, 26), "Магазин «Нур»", products ?? [], sales ?? [], [], [], []);

    private static readonly IReadOnlyList<SalesExportService.Receipt> SomeSales =
    [
        Receipt("№1", 1234.56, 0, "Бекназар «VIP»", new SalesExportService.Line("Молоко «Нур»; 1 л", 2, "шт", 617.28, 1234.56)),
        Receipt("№2", 0.1, 0, null, new SalesExportService.Line("Спичка", 1, "шт", 0.1, 0.1)),
        Receipt("№3", 0.2, 0, null, new SalesExportService.Line("Соль", 0.5, "кг", 0.4, 0.2)),
        Receipt("№4", 2.675, 0.5, "Жылдыз", new SalesExportService.Line("Мука", 1.25, "кг", 2.14, 2.675)),
        Receipt("№5", 99.995, 0, null, new SalesExportService.Line("Сахар", 3, "кг", 33.33, 99.995)),
    ];

    private static decimal ParseRu(string text) => decimal.Parse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static List<string[]> ParseCsv(byte[] bytes)
    {
        var text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ';') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else cell.Append(ch);
        }
        return rows;
    }

    private static byte[] Entry(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var s = zip.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Money_KeepsKopecks_HalfUp()
    {
        OneCExport.Money(0.1 + 0.2).Should().Be(0.30m);
        OneCExport.Money(2.675).Should().Be(2.68m);
        OneCExport.Money(99.995).Should().Be(100.00m);
        OneCExport.Money(1234.565).Should().Be(1234.57m);
        OneCExport.Money(-0.004).Should().Be(0m);
        OneCExport.Money(double.NaN).Should().Be(0m);
        OneCExport.Qty(6.9999996).Should().Be(7m, "количество — до трёх знаков (как «точное количество в приёмке»)");
    }

    [Fact]
    public void Csv_SalesTotal_EqualsReportTotal_ToTheKopeck()
    {
        var data = Data(SomeSales);
        var path = TempFile("sales.zip");
        var result = OneCExport.Export(path, OneCExport.Format.Csv, data, new[] { OneCExport.Part.Sales });
        result.Files.Should().BeEquivalentTo(["Prodazhi.csv", "Prodazhi-tovary.csv"]);

        var rows = ParseCsv(Entry(path, "Prodazhi.csv"));
        rows.Should().HaveCount(1 + SomeSales.Count, "шапка и по строке на чек");
        var amountColumn = rows[0].Length - 1; // «Сумма» — последняя колонка таблицы чеков
        var sum = rows.Skip(1).Sum(r => ParseRu(r[amountColumn]));

        // «отчёт»: те же чеки, сумма каждого — в копейках, как колонка «Сумма» таблицы «Чеки» (SalesExportService)
        var report = SomeSales.Sum(r => Math.Round((decimal)r.Total, 2, MidpointRounding.AwayFromZero));
        sum.Should().Be(report).And.Be(OneCExport.SalesTotal(data));
        sum.Should().Be(1234.56m + 0.10m + 0.20m + 2.68m + 100.00m);
        rows[1][amountColumn].Should().Be("1234,56", "десятичная запятая, без разделителей разрядов и без валюты");

        // строки товаров: сумма строк = сумма чеков (скидок в этих чеках на строках нет)
        var lines = ParseCsv(Entry(path, "Prodazhi-tovary.csv"));
        lines.Skip(1).Sum(r => ParseRu(r[^1])).Should().Be(sum);
    }

    [Fact]
    public void Csv_Utf8WithBom_CyrillicSemicolonAndQuotesSurvive()
    {
        var path = TempFile("clients.csv");
        var data = Data() with
        {
            Parties = [new OneCExport.PartyRow(false, "1", "ОсОО «Бекназар; и сыновья»", "+996 700 123 456", "a@b.kg", "", "", "", "", "г. Бишкек, ул. Ак-Орго \"4\"")],
        };
        var result = OneCExport.Export(path, OneCExport.Format.Csv, data, new[] { OneCExport.Part.Counterparties });
        result.Files.Should().Equal(Path.GetFileName(path));

        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = new UTF8Encoding(false).GetString(bytes);
        text.Should().Contain("\"ОсОО «Бекназар; и сыновья»\"").And.Contain("\"г. Бишкек, ул. Ак-Орго \"\"4\"\"\"").And.Contain("\r\n");
        var rows = ParseCsv(bytes);
        rows.Should().HaveCount(2);
        rows[1][1].Should().Be("ОсОО «Бекназар; и сыновья»");
        rows[1][^1].Should().Be("г. Бишкек, ул. Ак-Орго \"4\"");
        rows.Should().OnlyContain(r => r.Length == rows[0].Length, "в каждой строке столько же ячеек, сколько в шапке");
    }

    [Fact]
    public void Csv_QuantityAndStock_Formats()
    {
        var product = new OneCExport.ProductRow("p1", "A-1", "4600000000017", "Мука «Нур» 1 кг", "Бакалея", "кг", 41.05m, 30.10m, 12.375m);
        var path = TempFile("stock.csv");
        OneCExport.Export(path, OneCExport.Format.Csv, Data(products: [product]), new[] { OneCExport.Part.Stock });
        var rows = ParseCsv(File.ReadAllBytes(path));
        rows[1].Should().Contain("12,375").And.Contain("30,10").And.Contain("372,49", "сумма закупки = количество × цена, округлено до копеек (12,375 × 30,10 = 372,4875)")
            .And.Contain("4600000000017").And.Contain("Мука «Нур» 1 кг");
    }

    [Fact]
    public void Excel_SheetsHeaderInFirstRow_AmountsAreNumbers_AndEqualReport()
    {
        var path = TempFile("book.xlsx");
        var data = Data(SomeSales, [new OneCExport.ProductRow("p1", "A-1", "4600000000017", "Хлеб «Нур»", "Выпечка", "шт", 35m, 20m, 10m)]);
        var result = OneCExport.Export(path, OneCExport.Format.Excel, data, new[] { OneCExport.Part.Sales, OneCExport.Part.Products });
        result.Rows.Should().Be(SomeSales.Count + SomeSales.Count + 1, "чеки + строки товаров + номенклатура");

        var sheets = MiniExcel.GetSheetNames(path);
        sheets.Should().HaveCount(3).And.OnlyHaveUniqueItems();

        var receiptsSheet = sheets[1]; // порядок листов: номенклатура, чеки, товары чеков (названия зависят от языка)
        var rows = MiniExcel.Query(path, useHeaderRow: true, sheetName: receiptsSheet).Cast<IDictionary<string, object?>>().ToList();
        rows.Should().HaveCount(SomeSales.Count, "шапка в первой строке: данные начинаются со второй");
        var amountKey = rows[0].Keys.Last();
        var sum = rows.Sum(r => Convert.ToDecimal(r[amountKey], CultureInfo.InvariantCulture));
        sum.Should().Be(OneCExport.SalesTotal(data), "сумма в книге = сумма отчёта, копейка в копейку");
        rows.Select(r => (string?)r[rows[0].Keys.First()]).Should().Contain("№1");
        rows.SelectMany(r => r.Values.OfType<string>()).Should().Contain("Бекназар «VIP»");

        var products = sheets[0];
        MiniExcel.Query(path, useHeaderRow: true, sheetName: products).Cast<IDictionary<string, object?>>().Single().Values.OfType<string>().Should().Contain("Хлеб «Нур»");
    }

    [Fact]
    public void Excel_WithoutParts_Throws()
    {
        var act = () => OneCExport.Export(TempFile("empty.xlsx"), OneCExport.Format.Excel, Data(), Array.Empty<OneCExport.Part>());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CommerceMl_ImportAndOffers_AreWellFormed_WithCyrillicPricesAndStock()
    {
        var products = new[]
        {
            new OneCExport.ProductRow("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "A-1", "4600000000017", "Мука «Нур»\u0001 2 кг", "Бакалея", "кг", 41.05m, 30.10m, 12.375m),
            new OneCExport.ProductRow("not-a-guid-7", "", "", "Спички", "", "шт", 3m, 1m, 100m),
        };
        var path = TempFile("catalog.zip");
        var result = OneCExport.Export(path, OneCExport.Format.CommerceMl, Data(products: products), new[] { OneCExport.Part.Products, OneCExport.Part.Stock, OneCExport.Part.Counterparties });
        result.Files.Should().Contain(["import.xml", "offers.xml", "Kontragenty.csv"]).And.NotContain("Nomenklatura.csv", "каталог уже в XML");

        var import = XDocument.Parse(new UTF8Encoding(false).GetString(Entry(path, "import.xml")));
        import.Root!.Name.LocalName.Should().Be("КоммерческаяИнформация");
        import.Root.Attribute("ВерсияСхемы")!.Value.Should().Be("2.08");
        var goods = import.Descendants("Товар").ToList();
        goods.Should().HaveCount(2);
        goods[0].Element("Ид")!.Value.Should().Be("3f2504e0-4f89-11d3-9a0c-0305e82c3301");
        goods[0].Element("Наименование")!.Value.Should().Be("Мука «Нур» 2 кг", "управляющие символы, запрещённые в XML, убраны");
        goods[0].Element("Артикул")!.Value.Should().Be("A-1");
        goods[0].Element("БазоваяЕдиница")!.Attribute("Код")!.Value.Should().Be("166");
        goods[1].Element("БазоваяЕдиница")!.Attribute("Код")!.Value.Should().Be("796");
        Guid.TryParse(goods[1].Element("Ид")!.Value, out _).Should().BeTrue("не-GUID идентификатор превращён в стабильный GUID");
        import.Descendants("Группа").Select(g => g.Element("Наименование")!.Value).Should().Equal("Бакалея");

        var offers = XDocument.Parse(new UTF8Encoding(false).GetString(Entry(path, "offers.xml")));
        offers.Root!.Element("ПакетПредложений")!.Element("ИдКаталога")!.Value.Should().Be(import.Descendants("Каталог").Single().Element("Ид")!.Value);
        var offer = offers.Descendants("Предложение").First();
        offer.Element("Ид")!.Value.Should().Be(goods[0].Element("Ид")!.Value, "предложение привязано к товару по Ид");
        offer.Descendants("ЦенаЗаЕдиницу").Single().Value.Should().Be("41.05", "в XML десятичная точка");
        offer.Element("Количество")!.Value.Should().Be("12.375");
        offer.Descendants("Валюта").First().Value.Should().Be("KGS");
    }

    [Fact]
    public void SuggestFileName_ByFormat()
    {
        OneCExport.SuggestFileName(Day, Day, "Нур", OneCExport.Format.Excel, 3).Should().EndWith(".xlsx");
        OneCExport.SuggestFileName(Day, Day, "Нур", OneCExport.Format.Csv, 1).Should().EndWith(".csv");
        OneCExport.SuggestFileName(Day, Day, "Нур", OneCExport.Format.Csv, 2).Should().EndWith(".zip");
        OneCExport.SuggestFileName(Day, Day, "Нур", OneCExport.Format.CommerceMl, 2).Should().EndWith(".zip");
    }

    // ================================================================== 2026-11-26: стресс-тест выгрузки в 1С

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public OneCExportTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static readonly OneCExport.Part[] AllParts =
        [OneCExport.Part.Products, OneCExport.Part.Stock, OneCExport.Part.Sales, OneCExport.Part.Returns, OneCExport.Part.Receipts, OneCExport.Part.Counterparties];

    /// <summary>Большой магазин: 15 000 товаров, 20 000 чеков × 5 строк = 100 000 строк, 5 000 клиентов, 2 000 приходов × 5 строк, 1 000 возвратов.
    /// Суммы с копейками, дробные/нулевые/отрицательные количества, кавычки, «;», перевод строки и эмодзи в названиях, чеки в долг.</summary>
    private static OneCExport.Data BigData()
    {
        var rnd = new Random(20261126);
        var units = new[] { "шт", "кг", "л", "м", "упак" };
        var products = Enumerable.Range(0, 15000).Select(i => new OneCExport.ProductRow("p" + i, "A-" + i, (4600000000000L + i).ToString(CultureInfo.InvariantCulture),
            i % 997 == 0 ? $"Товар «{i}»; \"особый\"\nвторая строка 🍎" : "Товар " + i, i % 50 == 0 ? "" : "Группа " + (i % 40), units[i % units.Length],
            Math.Round((decimal)(rnd.Next(100, 500000) / 100.0), 2), Math.Round((decimal)(rnd.Next(50, 300000) / 100.0), 2),
            i % 101 == 0 ? -3.5m : i % 103 == 0 ? 0m : Math.Round((decimal)(rnd.Next(0, 100000) / 1000.0), 3))).ToList();
        var start = new DateTime(2026, 11, 1);
        var sales = Enumerable.Range(0, 20000).Select(i =>
        {
            var lines = Enumerable.Range(0, 5).Select(k =>
            {
                var qty = k == 4 && i % 9 == 0 ? 0.375 : rnd.Next(1, 5);
                var price = rnd.Next(1, 200000) / 100.0;
                return new SalesExportService.Line("Товар " + rnd.Next(15000), qty, k == 4 ? "кг" : "шт", price, Math.Round(qty * price, 2));
            }).ToArray();
            var total = Math.Round(lines.Sum(l => l.Sum), 2);
            return new SalesExportService.Receipt("s" + i, start.AddMinutes(i * 1.9), "№" + (i + 1), total, i % 13 == 0 ? 10.5 : 0, i % 3 == 0 ? "card" : "cash",
                i % 17 == 0 ? "debt" : "paid", "Кассир " + (i % 4), i % 5 == 0 ? "Клиент «" + i + "»" : null, lines[0].Name, lines);
        }).ToList();
        var returns = Enumerable.Range(0, 1000).Select(i => new OneCExport.ReturnRow("r" + i, "В-" + i, start.AddHours(i), "№" + (i * 7 + 1),
            Math.Round(rnd.Next(100, 90000) / 100m, 2), i % 2 == 0 ? "брак" : "", "Товар " + i + " × 1")).ToList();
        var receipts = Enumerable.Range(0, 2000).Select(i =>
        {
            var lines = Enumerable.Range(0, 5).Select(k =>
            {
                var q = k == 0 ? 2.5m : rnd.Next(1, 100);
                var p = Math.Round(rnd.Next(100, 50000) / 100m, 2);
                return new OneCExport.ReceiptLineRow("Товар " + (i * 5 + k), "A-" + (i * 5 + k), "", "шт", q, p, Math.Round(q * p, 2, MidpointRounding.AwayFromZero));
            }).ToList();
            return new OneCExport.ReceiptRow("rc" + i, "ПР-2026-" + (i + 1).ToString("000000", CultureInfo.InvariantCulture), start.AddHours(i * 0.3), "Поставщик " + (i % 60),
                i % 4 == 0 ? "" : "Н-" + i, lines.Sum(l => l.Sum), lines, i % 3 == 0 ? "ЭСФ-" + i : "");
        }).ToList();
        var parties = Enumerable.Range(0, 5000).Select(i => new OneCExport.PartyRow(i % 10 == 0, "c" + i, i % 333 == 0 ? "ОсОО «Ак; Жол»\n" + i : "Клиент " + i,
            "+996 700 " + (i % 1000).ToString("000", CultureInfo.InvariantCulture) + " 000", "", i % 10 == 0 ? "01234567890123" : "", "", "", "", "г. Бишкек")).ToList();
        return new OneCExport.Data(start, new DateTime(2026, 11, 30), "Магазин «Нур»", products, sales, returns, receipts, parties);
    }

    private (TimeSpan Time, long PeakDeltaMb) Measure(Action act)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var baseline = GC.GetTotalMemory(true);
        long peak = baseline;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(20).ConfigureAwait(false);
            }
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        act();
        sw.Stop();
        stop.Cancel();
        sampler.Wait();
        return (sw.Elapsed, Math.Max(0, peak - baseline) / (1024 * 1024));
    }

    [Theory]
    [InlineData(OneCExport.Format.Excel)]
    [InlineData(OneCExport.Format.Csv)]
    [InlineData(OneCExport.Format.CommerceMl)]
    public void Stress_BigShop_AllParts_FastLowMemory_AndSalesSumToTheKopeck(OneCExport.Format format)
    {
        var data = BigData();
        var path = TempFile(format == OneCExport.Format.Excel ? "big.xlsx" : "big.zip");
        OneCExport.Result result = null!;
        var (time, peakMb) = Measure(() => result = OneCExport.Export(path, format, data, AllParts));
        var size = new FileInfo(path).Length;
        var line = $"{DateTime.Now:dd.MM HH:mm} {format}: {time.TotalSeconds:0.0} с, пик управляемой памяти +{peakMb} МБ, файл {size / 1024} КБ, строк {result.Rows}";
        _out.WriteLine(line);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "nurmarket-tests", "onec-stress.txt"), line + Environment.NewLine); } catch (IOException) { /* цифры для отчёта — не обязательны */ }

        time.Should().BeLessThan(TimeSpan.FromSeconds(90), $"{format}: 160 тыс. строк выгружаются за разумное время");
        peakMb.Should().BeLessThan(500, $"{format}: память при выгрузке не больше ~500 МБ");
        result.Rows.Should().BeGreaterThan(150_000);

        // «отчёт»: выручка как в «Продажи → Excel» (сумма double по чекам выручки) и сумма всех чеков — до копейки
        var reportRevenue = Math.Round((decimal)data.Sales.Where(r => SalesExportService.CountsAsRevenue(r.Status)).Sum(r => r.Total), 2, MidpointRounding.AwayFromZero);
        OneCExport.Revenue(data).Should().Be(reportRevenue, "выручка в выгрузке = выручка отчёта, копейка в копейку, на 20 000 чеков");

        decimal fileSales;
        if (format == OneCExport.Format.Excel)
        {
            var sheets = MiniExcel.GetSheetNames(path);
            sheets.Should().HaveCount(8, "номенклатура, остатки, чеки, строки чеков, возвраты, приходы, строки приходов, контрагенты");
            var receiptsSheet = MiniExcel.Query(path, useHeaderRow: true, sheetName: sheets[2]).Cast<IDictionary<string, object?>>().ToList();
            receiptsSheet.Should().HaveCount(20000);
            var key = receiptsSheet[0].Keys.Last();
            fileSales = receiptsSheet.Sum(r => Math.Round(Convert.ToDecimal(r[key], CultureInfo.InvariantCulture), 2));
            MiniExcel.Query(path, useHeaderRow: true, sheetName: sheets[3]).Count().Should().Be(100000, "100 000 строк чеков — на одном листе (до предела Excel далеко)");
        }
        else
        {
            using var zip = ZipFile.OpenRead(path);
            if (format == OneCExport.Format.CommerceMl)
            {
                zip.Entries.Select(e => e.Name).Should().Contain(["import.xml", "offers.xml"]).And.NotContain("Nomenklatura.csv");
                XDocument.Parse(new UTF8Encoding(false).GetString(Entry(path, "offers.xml"))).Descendants("Предложение").Should().HaveCount(15000);
            }
            var rows = ParseCsv(Entry(path, "Prodazhi.csv"));
            rows.Should().HaveCount(20001);
            rows.Should().OnlyContain(r => r.Length == rows[0].Length, "кавычки, «;» и переводы строк не ломают столбцы");
            fileSales = rows.Skip(1).Sum(r => ParseRu(r[^1]));
            var lines = ParseCsv(Entry(path, "Prodazhi-tovary.csv"));
            lines.Should().HaveCount(100001);
            lines.Should().Contain(r => r[4] == "0,375", "дробное количество — с десятичной запятой");
            var parties = ParseCsv(Entry(path, "Kontragenty.csv"));
            parties.Should().HaveCount(5001, "название с переводом строки внутри кавычек — одна запись");
            parties.Should().Contain(r => r[1] == "ОсОО «Ак; Жол»\n333");
        }
        fileSales.Should().Be(OneCExport.SalesTotal(data), $"{format}: сумма чеков в файле = сумма отчёта до копейки");
        Directory.GetFiles(Path.GetDirectoryName(path)!, ".nurcrm-*.tmp").Should().BeEmpty("временный файл после выгрузки не остаётся");
    }

    [Fact]
    public void Excel_LongTable_IsSplitIntoSheets_WithHeaderOnEach()
    {
        var path = TempFile("split.xlsx");
        IReadOnlyList<object?[]> rows = Enumerable.Range(1, 25).Select(i => new object?[] { "№" + i, (decimal)i }).ToList();
        IReadOnlyList<XlColumn> columns = [new XlColumn("Номер"), new XlColumn("Сумма", XlKind.Money)];
        var sheet = (Name: "Продажи (чеки) — товары", Columns: columns, Rows: (IEnumerable<object?[]>)rows);
        LargeExcelWriter.WriteSheets(path, [sheet], maxRowsPerSheet: 10);
        var sheets = MiniExcel.GetSheetNames(path);
        sheets.Should().Equal("Продажи (чеки) — товары", "Продажи (чеки) — товары (2)", "Продажи (чеки) — товары (3)");
        sheets.Should().OnlyContain(s => s.Length <= 31, "Excel не принимает имя листа длиннее 31 знака");
        var counts = sheets.Select(s => MiniExcel.Query(path, useHeaderRow: true, sheetName: s).Count()).ToList();
        counts.Should().Equal(10, 10, 5);
        MiniExcel.Query(path, useHeaderRow: true, sheetName: sheets[2]).Cast<IDictionary<string, object?>>().First()["Номер"].Should().Be("№21", "шапка повторена, данные продолжаются");
        LargeExcelWriter.MaxSheetDataRows.Should().Be(1_048_575);
    }

    [Fact]
    public void Export_Cancelled_KeepsPreviousFile_AndLeavesNoTemp()
    {
        var path = TempFile("keep.csv");
        File.WriteAllText(path, "прежняя выгрузка");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => OneCExport.Export(path, OneCExport.Format.Csv, Data(SomeSales), new[] { OneCExport.Part.Counterparties }, cts.Token);
        act.Should().Throw<OperationCanceledException>();
        File.ReadAllText(path).Should().Be("прежняя выгрузка", "отмена не портит файл с тем же именем");
        Directory.GetFiles(Path.GetDirectoryName(path)!, ".nurcrm-*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void Export_OverwritesExistingFile_WhenFree()
    {
        var path = TempFile("again.zip");
        File.WriteAllText(path, "старое");
        OneCExport.Export(path, OneCExport.Format.Csv, Data(SomeSales), new[] { OneCExport.Part.Sales });
        using var zip = ZipFile.OpenRead(path);
        zip.Entries.Select(e => e.Name).Should().Contain("Prodazhi.csv", "повторная выгрузка за тот же период заменяет файл");
    }

    [Fact]
    public void Export_FileOpenInExcel_GivesClearMessage_AndKeepsTheFile()
    {
        if (!OperatingSystem.IsWindows())
            return; // блокировка файла другой программой — поведение Windows
        var path = TempFile("busy.xlsx");
        File.WriteAllText(path, "открыт в Excel");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // так держит файл Excel
        {
            OneCExport.CheckWritable(path).Should().NotBeNull().And.Contain("Excel", "проверка до загрузки данных — сразу понятная причина");
            var act = () => OneCExport.Export(path, OneCExport.Format.Excel, Data(SomeSales), new[] { OneCExport.Part.Sales });
            act.Should().Throw<IOException>().Which.Message.Should().Contain("Excel");
        }
        File.ReadAllText(path).Should().Be("открыт в Excel");
        OneCExport.CheckWritable(path).Should().BeNull("файл закрыли — можно писать");
        Directory.GetFiles(Path.GetDirectoryName(path)!, ".nurcrm-*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void FriendlyIoError_DiskFullMissingFolderLongPath()
    {
        OneCExport.FriendlyIoError(new IOException("disk", unchecked((int)0x80070070))).Should().NotBeNull();
        OneCExport.FriendlyIoError(new DirectoryNotFoundException("x")).Should().NotBeNull();
        OneCExport.FriendlyIoError(new PathTooLongException("x")).Should().NotBeNull();
        OneCExport.FriendlyIoError(new InvalidOperationException("x")).Should().BeNull("не файловая ошибка — как есть");
        OneCExport.CheckWritable(Path.Combine(TestDataRoot.Path, "нет-такой-папки-" + Guid.NewGuid().ToString("N")[..6], "a.xlsx")).Should().NotBeNull();
    }

    [Fact]
    public void EmptyPeriod_WritesHeadersOnly_AndCountsZeroRows()
    {
        var path = TempFile("empty.zip");
        var result = OneCExport.Export(path, OneCExport.Format.Csv, Data(), new[] { OneCExport.Part.Sales });
        result.Rows.Should().Be(0);
        ParseCsv(Entry(path, "Prodazhi.csv")).Should().ContainSingle("только шапка");
    }

    [Fact]
    public void ReceiptsTable_CarriesEsfNumber()
    {
        var line = new OneCExport.ReceiptLineRow("Мука", "A-1", "", "кг", 2.5m, 40m, 100m);
        var data = Data() with { Receipts = [new OneCExport.ReceiptRow("r1", "ПР-2026-000001", Day, "ОсОО «Дан»", "45", 100m, [line], "0123456789")] };
        var table = OneCExport.BuildTables(data, new[] { OneCExport.Part.Receipts })[0];
        table.Rows[0].Should().Contain("0123456789");
        table.Columns.Single(c => c.Kind == XlKind.Code).Title.Should().NotBeNullOrWhiteSpace("№ ЭСФ — текстом (ведущие нули не теряются)");
    }

    [Fact]
    public void TotalsLines_ExplainRevenueAndServerMismatch()
    {
        var paid = Receipt("№1", 100.10);
        var debt = Receipt("№2", 50.05) with { Status = "debt" };
        OneCExport.TotalsLines(Data([paid])).Should().HaveCount(1, "всё оплачено — сумма чеков = выручка, одна строка");
        var withDebt = Data([paid, debt]);
        OneCExport.Revenue(withDebt).Should().Be(100.10m);
        OneCExport.SalesTotal(withDebt).Should().Be(150.15m);
        OneCExport.TotalsLines(withDebt).Should().HaveCount(2, "чек в долг: отдельная строка «из них выручка»");
        OneCExport.TotalsLines(withDebt with { ServerRevenue = 100.10m }).Should().HaveCount(2, "сводка сервера совпала — без предупреждения");
        OneCExport.TotalsLines(withDebt with { ServerRevenue = 130.10m }).Should().HaveCount(3, "сводка сервера разошлась — предупреждение");
    }

    [Fact]
    public void QuickPeriods_ThisAndLastMonth_IncludingJanuary()
    {
        OneCExport.ThisMonth(new DateTime(2026, 11, 26, 15, 0, 0)).Should().Be((new DateTime(2026, 11, 1), new DateTime(2026, 11, 26)));
        OneCExport.LastMonth(new DateTime(2026, 11, 26)).Should().Be((new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)));
        OneCExport.LastMonth(new DateTime(2027, 1, 3)).Should().Be((new DateTime(2026, 12, 1), new DateTime(2026, 12, 31)));
        OneCExport.LastMonth(new DateTime(2028, 3, 1)).Should().Be((new DateTime(2028, 2, 1), new DateTime(2028, 2, 29)), "високосный февраль");
    }

    [Fact]
    public void Prefs_RememberedAndBrokenFileIgnored()
    {
        var path = TempFile("onec-export.json");
        OneCExport.LoadPrefs(path).Should().BeNull("первый раз — значения по умолчанию");
        OneCExport.SavePrefs(path, [OneCExport.Part.Receipts, OneCExport.Part.Counterparties], OneCExport.Format.Csv);
        var loaded = OneCExport.LoadPrefs(path);
        loaded!.Value.Parts.Should().BeEquivalentTo([OneCExport.Part.Receipts, OneCExport.Part.Counterparties]);
        loaded.Value.Format.Should().Be(OneCExport.Format.Csv);
        File.WriteAllText(path, "{ испорчен");
        OneCExport.LoadPrefs(path).Should().BeNull();
        File.WriteAllText(path, "");
        OneCExport.LoadPrefs(path).Should().BeNull();
    }

    [Fact]
    public void Csv_NegativeZeroFractionalAndMultilineCells()
    {
        var products = new[]
        {
            new OneCExport.ProductRow("p1", "A-1", "0012345", "Пакет\r\nбольшой", "", "шт", 5m, 2m, -3.5m),
            new OneCExport.ProductRow("p2", "A-2", "", "=СУММ(A1)", "", "кг", 0m, 0m, 0m),
        };
        var rows = ParseCsv(OneCExport.CsvBytes(OneCExport.BuildTables(Data(products: products), new[] { OneCExport.Part.Stock })[0]));
        rows.Should().HaveCount(3);
        rows[1][2].Should().Be("Пакет\r\nбольшой", "перевод строки внутри кавычек — одна ячейка");
        rows[1].Should().Contain("-3,5").And.Contain("-7,00", "отрицательный остаток × цена");
        rows[1][1].Should().Be("0012345", "штрихкод с ведущими нулями — как есть");
        rows[2].Should().Contain("0").And.Contain("0,00");
    }
}
