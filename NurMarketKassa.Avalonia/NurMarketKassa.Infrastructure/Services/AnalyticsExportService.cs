using System.Globalization;
using System.Runtime.Versioning;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using XDR = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace NurMarketKassa.Services;

/// <summary>
/// Выгрузка аналитики продаж и склада в Excel (.xlsx) и Word (.docx) с графиками.
///
/// Формат пишется напрямую через DocumentFormat.OpenXml (лицензия MIT, библиотека Microsoft) —
/// без Excel и Word на компьютере: на кассе офиса обычно нет, а файл нужен.
///
/// Графики вставляются картинками (см. AnalyticsChartRenderer, там объяснено почему), а данные
/// лежат рядом обычными листами и таблицами — их можно пересортировать и посчитать по-своему.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AnalyticsExportService
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string Money(double value) => value.ToString("N2", Ru);

    private static string Period(AnalyticsReportData d) =>
        d.FromLocal.Date == d.ToLocal.Date
            ? d.FromLocal.ToString("dd.MM.yyyy")
            : $"{d.FromLocal:dd.MM.yyyy} — {d.ToLocal:dd.MM.yyyy}";

    // ------------------------------------------------------------------ Excel

    public static void ExportToExcel(string path, AnalyticsReportData data, string? shopName)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        ExcelWorkbookBuilder.AddStylesheet(workbookPart);
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        uint sheetId = 1;
        AddSummarySheet(workbookPart, sheets, ref sheetId, data, shopName);

        // Числа на этих листах хранятся ЧИСЛАМИ — только по ним Excel умеет строить диаграммы.
        var byDay = ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId, "По дням",
            new[]
            {
                new ExcelWorkbookBuilder.Column("Дата", 14),
                new ExcelWorkbookBuilder.Column("Выручка", 16, IsMoney: true),
            },
            data.ByDay.Select(d => new[]
            {
                d.Day.ToString("dd.MM.yyyy"),
                d.Revenue.ToString("0.####", CultureInfo.InvariantCulture),
            }).ToList());

        var top = ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId, "Топ товаров",
            new[]
            {
                new ExcelWorkbookBuilder.Column("Товар", 42),
                new ExcelWorkbookBuilder.Column("Количество", 14, IsNumber: true),
                new ExcelWorkbookBuilder.Column("Сумма", 16, IsMoney: true),
            },
            data.TopProducts.Select(x => new[]
            {
                x.Name,
                x.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
                x.Sum.ToString("0.####", CultureInfo.InvariantCulture),
            }).ToList());

        // По листу на срез ABC. Имя листа в Excel ограничено 31 символом и не терпит части
        // знаков, поэтому берём заголовок среза и подрезаем его.
        foreach (var slice in data.AbcSlices)
        {
            if (slice.Rows.Count == 0)
                continue;

            var money = slice.Unit != "шт.";
            ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId,
                SheetName("ABC " + slice.Title),
                new[]
                {
                    new ExcelWorkbookBuilder.Column("Группа", 10),
                    new ExcelWorkbookBuilder.Column("Название", 42),
                    new ExcelWorkbookBuilder.Column("Количество", 14, IsNumber: true),
                    new ExcelWorkbookBuilder.Column(money ? "Сумма" : "Штук", 16, IsMoney: money, IsNumber: !money),
                    new ExcelWorkbookBuilder.Column("Доля, %", 12, IsNumber: true),
                    new ExcelWorkbookBuilder.Column("Накопительно, %", 18, IsNumber: true),
                },
                slice.Rows.Select(r => new[]
                {
                    r.Group, r.Name,
                    r.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
                    r.Sum.ToString("0.####", CultureInfo.InvariantCulture),
                    r.Share.ToString("0.##", CultureInfo.InvariantCulture),
                    r.Cumulative.ToString("0.##", CultureInfo.InvariantCulture),
                }).ToList());
        }

        ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId, "Склад",
            new[]
            {
                new ExcelWorkbookBuilder.Column("Товар", 42),
                new ExcelWorkbookBuilder.Column("Остаток", 14, IsNumber: true),
                new ExcelWorkbookBuilder.Column("Продаж в день", 16, IsNumber: true),
                new ExcelWorkbookBuilder.Column("Хватит на, дн.", 16, IsNumber: true),
            },
            data.Restock.Select(r => new[]
            {
                r.Name,
                r.Stock.ToString("0.####", CultureInfo.InvariantCulture),
                r.DailyRate.ToString("0.####", CultureInfo.InvariantCulture),
                r.DaysLeft.ToString("0.##", CultureInfo.InvariantCulture),
            }).ToList());

        // Диаграммы кладём на те же листы, где лежат их данные: видно, из чего построено, и
        // Excel пересчитает диаграмму, если поправить число в таблице.
        if (data.ByDay.Count > 0)
        {
            ExcelWorkbookBuilder.AddChart(byDay, "По дням", "Выручка по дням",
                "$A$2:$A$" + (data.ByDay.Count + 1).ToString(CultureInfo.InvariantCulture),
                "$B$2:$B$" + (data.ByDay.Count + 1).ToString(CultureInfo.InvariantCulture),
                data.ByDay.Count, pie: false, fromColumn: 3, fromRow: 1);
        }

        if (data.TopProducts.Count > 0)
        {
            var count = Math.Min(data.TopProducts.Count, 10);
            ExcelWorkbookBuilder.AddChart(top, "Топ товаров", "Топ товаров по сумме",
                "$A$2:$A$" + (count + 1).ToString(CultureInfo.InvariantCulture),
                "$C$2:$C$" + (count + 1).ToString(CultureInfo.InvariantCulture),
                count, pie: false, fromColumn: 4, fromRow: 1);
        }

        if (data.AbcSummary.Count > 0)
            AddAbcSummaryWithChart(workbookPart, sheets, ref sheetId, data);

        workbookPart.Workbook.Save();
    }

    /// <summary>Имя листа Excel: не длиннее 31 символа и без знаков, которые Excel в именах
    /// листов не принимает.</summary>
    private static string SheetName(string title)
    {
        var cleaned = new string(title.Where(c => c is not (':' or '\\' or '/' or '?' or '*' or '[' or ']')).ToArray());
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    /// <summary>Лист «Сводка»: заголовок, период и показатели. Суммы — числами, чтобы их можно
    /// было складывать и сравнивать прямо в Excel.</summary>
    private static void AddSummarySheet(
        WorkbookPart workbookPart, Sheets sheets, ref uint sheetId, AnalyticsReportData d, string? shopName)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();

        var columns = new Columns(
            new DocumentFormat.OpenXml.Spreadsheet.Column { Min = 1, Max = 1, Width = 34, CustomWidth = true },
            new DocumentFormat.OpenXml.Spreadsheet.Column { Min = 2, Max = 2, Width = 22, CustomWidth = true });

        void TextRow(string a, string b = "")
        {
            var row = new Row();
            row.AppendChild(ExcelWorkbookBuilder.TextCell(a, 0));
            row.AppendChild(ExcelWorkbookBuilder.TextCell(b, 0));
            sheetData.AppendChild(row);
        }

        void MoneyRow(string label, double value)
        {
            var row = new Row();
            row.AppendChild(ExcelWorkbookBuilder.LabelCell(label));
            row.AppendChild(ExcelWorkbookBuilder.MoneyCell(value));
            sheetData.AppendChild(row);
        }

        sheetData.AppendChild(new Row(ExcelWorkbookBuilder.TitleCell("Аналитика продаж и склада")));
        TextRow("Магазин", shopName ?? "—");
        TextRow("Период", Period(d));
        TextRow("Сформирован", DateTime.Now.ToString("dd.MM.yyyy HH:mm"));
        TextRow("");

        var header = new Row();
        header.AppendChild(ExcelWorkbookBuilder.TextCell("Показатель", 1));
        header.AppendChild(ExcelWorkbookBuilder.TextCell("Значение", 1));
        sheetData.AppendChild(header);

        MoneyRow("Выручка", d.Revenue);
        MoneyRow("Чеков", d.ReceiptCount);
        MoneyRow("Средний чек", d.AverageReceipt);
        MoneyRow("Скидки", d.Discounts);
        MoneyRow("   из них бонусами", d.PointsRedeemed);
        MoneyRow("Возвраты", d.Returns);
        MoneyRow("Списания", d.WriteOffs);
        MoneyRow("Расход", d.Expenses);
        MoneyRow("Оплата долгов", d.DebtPayments);
        TextRow("");
        MoneyRow("Позиций в каталоге", d.StockPositions);
        MoneyRow("Склад по ценам продажи", d.StockValue);

        part.Worksheet = new Worksheet(columns, sheetData);
        part.Worksheet.Save();
        sheets.AppendChild(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheetId++,
            Name = "Сводка",
        });
    }

    /// <summary>Отдельный лист со сводкой ABC и круговой диаграммой: на листе с полным списком
    /// товаров круговая по трём группам потерялась бы среди сотен строк.</summary>
    private static void AddAbcSummaryWithChart(
        WorkbookPart workbookPart, Sheets sheets, ref uint sheetId, AnalyticsReportData data)
    {
        var part = ExcelWorkbookBuilder.AddTableSheet(workbookPart, sheets, ref sheetId, "ABC-сводка",
            new[]
            {
                new ExcelWorkbookBuilder.Column("Группа", 12),
                new ExcelWorkbookBuilder.Column("Позиций", 12, IsNumber: true),
                new ExcelWorkbookBuilder.Column("Сумма", 18, IsMoney: true),
                new ExcelWorkbookBuilder.Column("Доля, %", 12, IsNumber: true),
            },
            data.AbcSummary.Select(g => new[]
            {
                "Группа " + g.Group,
                g.Count.ToString(CultureInfo.InvariantCulture),
                g.Sum.ToString("0.####", CultureInfo.InvariantCulture),
                g.Share.ToString("0.##", CultureInfo.InvariantCulture),
            }).ToList());

        ExcelWorkbookBuilder.AddChart(part, "ABC-сводка", "Доля групп в выручке",
            "$A$2:$A$" + (data.AbcSummary.Count + 1).ToString(CultureInfo.InvariantCulture),
            "$C$2:$C$" + (data.AbcSummary.Count + 1).ToString(CultureInfo.InvariantCulture),
            data.AbcSummary.Count, pie: true, fromColumn: 5, fromRow: 1);
    }

    // ------------------------------------------------------------------- Word

    public static void ExportToWord(string path, AnalyticsReportData data, string? shopName)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document(new W.Body());
        var body = mainPart.Document.Body!;

        body.AppendChild(Heading("Аналитика продаж и склада", 28));
        body.AppendChild(TextLine($"Магазин: {shopName ?? "—"}"));
        body.AppendChild(TextLine($"Период: {Period(data)}"));
        body.AppendChild(TextLine($"Сформирован: {DateTime.Now:dd.MM.yyyy HH:mm}"));
        body.AppendChild(TextLine(""));

        body.AppendChild(Heading("Итоги", 20));
        body.AppendChild(BuildTable(
            new[] { "Показатель", "Значение" },
            new List<string[]>
            {
                new[] { "Выручка", Money(data.Revenue) },
                new[] { "Чеков", data.ReceiptCount.ToString(Ru) },
                new[] { "Средний чек", Money(data.AverageReceipt) },
                new[] { "Скидки", Money(data.Discounts) },
                new[] { "  из них бонусами", Money(data.PointsRedeemed) },
                new[] { "Возвраты", Money(data.Returns) },
                new[] { "Списания", Money(data.WriteOffs) },
                new[] { "Расход", Money(data.Expenses) },
                new[] { "Оплата долгов", Money(data.DebtPayments) },
                new[] { "Позиций в каталоге", data.StockPositions.ToString(Ru) },
                new[] { "Склад по ценам продажи", Money(data.StockValue) },
            }));

        foreach (var (title, png) in BuildCharts(data))
        {
            body.AppendChild(TextLine(""));
            body.AppendChild(Heading(title, 20));
            body.AppendChild(BuildImageParagraph(mainPart, png));
        }

        body.AppendChild(TextLine(""));
        body.AppendChild(Heading("Топ товаров", 20));
        body.AppendChild(BuildTable(
            new[] { "Товар", "Количество", "Сумма" },
            data.TopProducts.Select(p => new[] { p.Name, p.Quantity.ToString("0.###", Ru), Money(p.Sum) }).ToList()));

        body.AppendChild(TextLine(""));
        body.AppendChild(Heading("ABC-анализ", 20));
        body.AppendChild(TextLine(
            "Группа A даёт первые 80 % результата, B — следующие 15 %, C — оставшиеся 5 %. "
            + "Срезы считаются независимо: товар из группы A по выручке легко оказывается в C по прибыли."));

        foreach (var slice in data.AbcSlices)
        {
            if (slice.Rows.Count == 0)
                continue;

            var money = slice.Unit != "шт.";
            string Value(double v) => money ? Money(v) : v.ToString("0.###", Ru) + " шт.";

            body.AppendChild(TextLine(""));
            body.AppendChild(Heading(slice.Title, 16));
            body.AppendChild(TextLine(slice.Hint));
            body.AppendChild(BuildTable(
                new[] { "Группа", "Позиций", money ? "Сумма" : "Штук", "Доля, %" },
                slice.Summary.Select(g => new[]
                {
                    g.Group, g.Count.ToString(Ru), Value(g.Sum), g.Share.ToString("0.#", Ru),
                }).ToList()));

            body.AppendChild(TextLine(""));
            // Сорок строк на срез: дальше таблица перестаёт читаться на бумаге, а полный
            // список всегда есть в Excel-выгрузке.
            body.AppendChild(BuildTable(
                new[] { "Группа", "Название", money ? "Сумма" : "Штук", "Доля, %", "Накопительно, %" },
                slice.Rows.Take(40).Select(r => new[]
                {
                    r.Group, r.Name, Value(r.Sum),
                    r.Share.ToString("0.##", Ru), r.Cumulative.ToString("0.##", Ru),
                }).ToList()));
        }

        body.AppendChild(TextLine(""));
        body.AppendChild(Heading("Что пора заказать", 20));
        body.AppendChild(BuildTable(
            new[] { "Товар", "Остаток", "Продаж в день", "Хватит на, дн." },
            data.Restock.Select(r => new[]
            {
                r.Name, r.Stock.ToString("0.###", Ru), r.DailyRate.ToString("0.##", Ru), r.DaysLeft.ToString("0.#", Ru),
            }).ToList()));

        mainPart.Document.Save();
    }

    private static List<(string Title, byte[] Png)> BuildCharts(AnalyticsReportData data)
    {
        var charts = new List<(string, byte[])>();

        charts.Add(("Выручка по дням", AnalyticsChartRenderer.RenderBars(
            "Выручка по дням",
            data.ByDay.Select(d => (d.Day.ToString("dd.MM"), d.Revenue)).ToList())));

        charts.Add(("Топ товаров по сумме", AnalyticsChartRenderer.RenderBars(
            "Топ товаров по сумме",
            data.TopProducts.Take(10).Select(p => (p.Name, p.Sum)).ToList())));

        // Круговая строится только по тому, что реально было: нули в легенде только мешают.
        var structure = new List<(string, double)>
        {
            ("Выручка", data.Revenue),
            ("Скидки", data.Discounts),
            ("Возвраты", data.Returns),
            ("Списания", data.WriteOffs),
            ("Расход", data.Expenses),
        }.Where(x => x.Item2 > 0.005).ToList();

        charts.Add(("Структура за период", AnalyticsChartRenderer.RenderPie("Структура за период", structure)));

        if (data.AbcSummary.Count > 0)
        {
            charts.Add(("ABC-анализ по выручке", AnalyticsChartRenderer.RenderPie(
                "ABC-анализ: доля групп в выручке",
                data.AbcSummary.Select(g => ($"Группа {g.Group} ({g.Count} поз.)", g.Sum)).ToList())));
        }

        // Каждый срез ABC — своей парой картинок: доля групп и диаграмма Парето. Раньше в
        // отчёт попадала только сводка по выручке, хотя на экране срезов пять, и решения по
        // закупке принимают как раз по разным срезам.
        foreach (var slice in data.AbcSlices)
        {
            if (slice.Summary.Count > 0)
            {
                charts.Add(($"ABC: {slice.Title} — доля групп", AnalyticsChartRenderer.RenderPie(
                    $"ABC: {slice.Title} — доля групп",
                    slice.Summary.Select(g => ($"Группа {g.Group} ({g.Count} поз.)", g.Sum)).ToList())));
            }

            if (slice.Rows.Count > 0)
            {
                var top = slice.Rows.Take(20).ToList();

                charts.Add(($"Парето: {slice.Title}", AnalyticsChartRenderer.RenderParetoClassic(
                    $"Диаграмма Парето: {slice.Title}",
                    top.Select(r => (r.Name, r.Share, r.Cumulative, r.Group)).ToList())));

                charts.Add(($"Парето столбцами: {slice.Title}", AnalyticsChartRenderer.RenderPareto(
                    $"Парето столбцами: {slice.Title}",
                    top.Select(r => (r.Name, r.Sum, r.Cumulative)).ToList(),
                    slice.Title)));
            }
        }

        return charts;
    }

    private static W.Paragraph Heading(string text, int halfPoints) =>
        new(new W.Run(
            new W.RunProperties(new W.Bold(), new W.FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) }),
            new W.Text(text)));

    private static W.Paragraph TextLine(string text) =>
        new(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));

    private static W.Table BuildTable(string[] header, List<string[]> rows)
    {
        var table = new W.Table(new W.TableProperties(
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));

        table.AppendChild(BuildRow(header, bold: true));
        foreach (var row in rows)
            table.AppendChild(BuildRow(row, bold: false));

        return table;
    }

    private static W.TableRow BuildRow(string[] values, bool bold)
    {
        var row = new W.TableRow();
        foreach (var value in values)
        {
            var run = bold
                ? new W.Run(new W.RunProperties(new W.Bold()), new W.Text(value ?? ""))
                : new W.Run(new W.Text(value ?? ""));
            row.AppendChild(new W.TableCell(new W.Paragraph(run)));
        }

        return row;
    }

    private static W.Paragraph BuildImageParagraph(MainDocumentPart mainPart, byte[] png)
    {
        var imagePart = mainPart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(png))
            imagePart.FeedData(stream);

        var id = mainPart.GetIdOfPart(imagePart);
        const long emuPerPixel = 9525;
        // 620 точек ширины — вписывается в страницу A4 с обычными полями.
        var width = 620L * emuPerPixel;
        var height = 289L * emuPerPixel;

        var element = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.DocProperties { Id = (UInt32Value)(uint)Math.Abs(id.GetHashCode() % 100000 + 1), Name = "Chart" },
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = "chart.png" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = id },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = width, Cy = height }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                    )
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });

        return new W.Paragraph(new W.Run(element));
    }
}
