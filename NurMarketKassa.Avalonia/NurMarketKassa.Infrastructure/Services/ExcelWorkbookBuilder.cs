using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using A = DocumentFormat.OpenXml.Drawing;
using XDR = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace NurMarketKassa.Services;

/// <summary>
/// Сборка книги Excel: оформленные листы и НАСТОЯЩИЕ диаграммы Excel.
///
/// Почему числа пишутся числами, а не текстом: диаграмму Excel можно построить только по
/// числовым ячейкам, по тексту — нельзя. Ради этого числа хранятся в инвариантном виде (точка
/// как разделитель), а как их показать — решает формат ячейки: «# ##0,00» отобразит их
/// по настройкам Windows у клиента. Так и суммы считаются, и вид правильный в любой локали.
/// </summary>
public static class ExcelWorkbookBuilder
{
    /// <summary>Описание колонки: заголовок, ширина и числовая ли она.</summary>
    public sealed record Column(string Title, double Width, bool IsNumber = false, bool IsMoney = false);

    // Индексы стилей в таблице ниже — порядок важен, менять только вместе с BuildStylesheet.
    private const uint StyleDefault = 0;
    private const uint StyleHeader = 1;
    private const uint StyleMoney = 2;
    private const uint StyleNumber = 3;
    private const uint StyleTitle = 4;
    private const uint StyleLabel = 5;

    public static void AddStylesheet(WorkbookPart workbookPart)
    {
        var part = workbookPart.AddNewPart<WorkbookStylesPart>();
        part.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" },
                    new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFFFFFFF" }),
                new Font(new Bold(), new FontSize { Val = 16 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" }))
            { Count = 4 },
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Rgb = "FF1F3864" },
                    new BackgroundColor { Indexed = 64 })
                { PatternType = PatternValues.Solid }))
            { Count = 3 },
            new Borders(
                new Border(),
                new Border(
                    new LeftBorder(new DocumentFormat.OpenXml.Spreadsheet.Color { Auto = true }) { Style = BorderStyleValues.Thin },
                    new RightBorder(new DocumentFormat.OpenXml.Spreadsheet.Color { Auto = true }) { Style = BorderStyleValues.Thin },
                    new TopBorder(new DocumentFormat.OpenXml.Spreadsheet.Color { Auto = true }) { Style = BorderStyleValues.Thin },
                    new BottomBorder(new DocumentFormat.OpenXml.Spreadsheet.Color { Auto = true }) { Style = BorderStyleValues.Thin },
                    new DiagonalBorder()))
            { Count = 2 },
            new CellFormats(
                // 0 — обычная ячейка
                new CellFormat(),
                // 1 — шапка таблицы: белым по тёмно-синему, с рамкой
                new CellFormat { FontId = 1, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true },
                // 2 — деньги: «# ##0,00», формат 4 — встроенный #,##0.00
                new CellFormat { NumberFormatId = 4, BorderId = 1, ApplyNumberFormat = true, ApplyBorder = true },
                // 3 — обычное число
                new CellFormat { NumberFormatId = 2, BorderId = 1, ApplyNumberFormat = true, ApplyBorder = true },
                // 4 — заголовок отчёта
                new CellFormat { FontId = 2, ApplyFont = true },
                // 5 — подпись показателя (полужирная, с рамкой)
                new CellFormat { FontId = 3, BorderId = 1, ApplyFont = true, ApplyBorder = true })
            { Count = 6 });
        part.Stylesheet.Save();
    }

    /// <summary>Лист-таблица: шапка с заливкой, ширины колонок, закреплённая первая строка,
    /// автофильтр. Значения — массив строк; числовые колонки превращаются в числа.</summary>
    public static WorksheetPart AddTableSheet(
        WorkbookPart workbookPart, Sheets sheets, ref uint sheetId, string name,
        IReadOnlyList<Column> columns, IReadOnlyList<string[]> rows)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();

        var columnsElement = new Columns();
        for (var i = 0; i < columns.Count; i++)
        {
            columnsElement.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Column
            {
                Min = (uint)(i + 1),
                Max = (uint)(i + 1),
                Width = columns[i].Width,
                CustomWidth = true,
            });
        }

        var header = new Row();
        foreach (var column in columns)
            header.AppendChild(TextCell(column.Title, StyleHeader));
        sheetData.AppendChild(header);

        foreach (var values in rows)
        {
            var row = new Row();
            for (var i = 0; i < columns.Count; i++)
            {
                var raw = i < values.Length ? values[i] : "";
                var column = columns[i];

                if ((column.IsNumber || column.IsMoney)
                    && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                {
                    row.AppendChild(NumberCell(number, column.IsMoney ? StyleMoney : StyleNumber));
                }
                else
                {
                    row.AppendChild(TextCell(raw, StyleDefault));
                }
            }

            sheetData.AppendChild(row);
        }

        // Закрепляем шапку: таблицы длинные, без этого при прокрутке не видно, что за колонка.
        var sheetViews = new SheetViews(new SheetView(
            new Pane { VerticalSplit = 1, TopLeftCell = "A2", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen })
        { WorkbookViewId = 0 });

        part.Worksheet = new Worksheet(sheetViews, columnsElement, sheetData);
        part.Worksheet.Save();

        sheets.AppendChild(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheetId++,
            Name = name,
        });

        return part;
    }

    public static Cell TextCell(string? value, uint style) => new()
    {
        DataType = CellValues.String,
        CellValue = new CellValue(value ?? ""),
        StyleIndex = style,
    };

    public static Cell NumberCell(double value, uint style) => new()
    {
        DataType = CellValues.Number,
        CellValue = new CellValue(value.ToString("0.####", CultureInfo.InvariantCulture)),
        StyleIndex = style,
    };

    public static Cell TitleCell(string value) => TextCell(value, StyleTitle);

    public static Cell LabelCell(string value) => TextCell(value, StyleLabel);

    public static Cell MoneyCell(double value) => NumberCell(value, StyleMoney);

    // --------------------------------------------------------------- диаграммы

    /// <summary>Добавляет НАСТОЯЩУЮ диаграмму Excel на лист: она пересчитывается вместе с
    /// данными и её можно настроить средствами Excel — в отличие от картинки.</summary>
    public static void AddChart(
        WorksheetPart target, string sheetName, string title,
        string categoryRange, string valueRange, int pointCount, bool pie,
        int fromColumn, int fromRow, int widthColumns = 9, int heightRows = 18)
    {
        var drawingsPart = target.GetPartsOfType<DrawingsPart>().FirstOrDefault();
        if (drawingsPart == null)
        {
            drawingsPart = target.AddNewPart<DrawingsPart>();
            drawingsPart.WorksheetDrawing = new XDR.WorksheetDrawing();
            target.Worksheet.AppendChild(new Drawing { Id = target.GetIdOfPart(drawingsPart) });
        }

        var chartPart = drawingsPart.AddNewPart<ChartPart>();
        chartPart.ChartSpace = BuildChartSpace(sheetName, title, categoryRange, valueRange, pointCount, pie);
        chartPart.ChartSpace.Save();

        var anchor = new XDR.TwoCellAnchor(
            new XDR.FromMarker
            {
                ColumnId = new XDR.ColumnId(fromColumn.ToString(CultureInfo.InvariantCulture)),
                ColumnOffset = new XDR.ColumnOffset("0"),
                RowId = new XDR.RowId(fromRow.ToString(CultureInfo.InvariantCulture)),
                RowOffset = new XDR.RowOffset("0"),
            },
            new XDR.ToMarker
            {
                ColumnId = new XDR.ColumnId((fromColumn + widthColumns).ToString(CultureInfo.InvariantCulture)),
                ColumnOffset = new XDR.ColumnOffset("0"),
                RowId = new XDR.RowId((fromRow + heightRows).ToString(CultureInfo.InvariantCulture)),
                RowOffset = new XDR.RowOffset("0"),
            },
            new XDR.GraphicFrame(
                new XDR.NonVisualGraphicFrameProperties(
                    new XDR.NonVisualDrawingProperties { Id = (uint)(fromRow * 100 + fromColumn + 2), Name = title },
                    new XDR.NonVisualGraphicFrameDrawingProperties()),
                new XDR.Transform(
                    new A.Offset { X = 0, Y = 0 },
                    new A.Extents { Cx = 0, Cy = 0 }),
                new A.Graphic(new A.GraphicData(
                    new C.ChartReference { Id = drawingsPart.GetIdOfPart(chartPart) })
                { Uri = "http://schemas.openxmlformats.org/drawingml/chart" })),
            new XDR.ClientData());

        drawingsPart.WorksheetDrawing.AppendChild(anchor);
        drawingsPart.WorksheetDrawing.Save();
    }

    private static C.ChartSpace BuildChartSpace(
        string sheetName, string title, string categoryRange, string valueRange, int pointCount, bool pie)
    {
        var categories = new C.CategoryAxisData(
            new C.StringReference(
                new C.Formula($"'{sheetName}'!{categoryRange}"),
                new C.StringCache(new C.PointCount { Val = (uint)pointCount })));

        var values = new C.Values(
            new C.NumberReference(
                new C.Formula($"'{sheetName}'!{valueRange}"),
                new C.NumberingCache(new C.PointCount { Val = (uint)pointCount })));

        OpenXmlCompositeElement plot;
        if (pie)
        {
            plot = new C.PieChart(
                new C.VaryColors { Val = true },
                new C.PieChartSeries(
                    new C.Index { Val = 0 },
                    new C.Order { Val = 0 },
                    new C.SeriesText(new C.NumericValue(title)),
                    categories,
                    values),
                new C.DataLabels(
                    new C.ShowLegendKey { Val = false },
                    new C.ShowValue { Val = true },
                    new C.ShowCategoryName { Val = false },
                    new C.ShowSeriesName { Val = false },
                    new C.ShowPercent { Val = true },
                    new C.ShowBubbleSize { Val = false }));
        }
        else
        {
            plot = new C.BarChart(
                new C.BarDirection { Val = C.BarDirectionValues.Column },
                new C.BarGrouping { Val = C.BarGroupingValues.Clustered },
                new C.BarChartSeries(
                    new C.Index { Val = 0 },
                    new C.Order { Val = 0 },
                    new C.SeriesText(new C.NumericValue(title)),
                    categories,
                    values),
                new C.AxisId { Val = 111111111U },
                new C.AxisId { Val = 222222222U });
        }

        var plotArea = new C.PlotArea(new C.Layout(), plot);

        if (!pie)
        {
            plotArea.AppendChild(new C.CategoryAxis(
                new C.AxisId { Val = 111111111U },
                new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                new C.Delete { Val = false },
                new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
                new C.CrossingAxis { Val = 222222222U }));

            plotArea.AppendChild(new C.ValueAxis(
                new C.AxisId { Val = 222222222U },
                new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                new C.Delete { Val = false },
                new C.AxisPosition { Val = C.AxisPositionValues.Left },
                new C.CrossingAxis { Val = 111111111U }));
        }

        return new C.ChartSpace(
            new C.EditingLanguage { Val = "ru-RU" },
            new C.Chart(
                new C.Title(
                    new C.ChartText(new C.RichText(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text(title))))),
                    new C.Overlay { Val = false }),
                new C.AutoTitleDeleted { Val = false },
                plotArea,
                pie
                    ? new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Right }, new C.Overlay { Val = false })
                    : new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Bottom }, new C.Overlay { Val = false }),
                new C.PlotVisibleOnly { Val = true }));
    }
}
