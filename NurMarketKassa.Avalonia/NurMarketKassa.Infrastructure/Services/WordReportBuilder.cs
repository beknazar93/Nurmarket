using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace NurMarketKassa.Services;

/// <summary>Колонка таблицы Word: заголовок, относительная ширина и выравнивание.</summary>
/// <param name="Weight">Доля ширины страницы: колонки делят ширину пропорционально весам.</param>
/// <param name="Numeric">Числа — по правому краю, чтобы разряды стояли друг под другом.</param>
public sealed record WColumn(string Title, double Weight, bool Numeric = false, bool Center = false);

/// <summary>
/// Сборка отчёта Word в «деловом» виде: титульный блок, оглавление со ссылками, заголовки стилями
/// «Заголовок 1/2» (по ним работает область навигации Word), таблицы с повторяющейся шапкой,
/// чередованием строк и итогами, графики во всю ширину страницы, колонтитулы с названием отчёта
/// и номерами страниц, альбомные разделы для широких таблиц.
///
/// Пишется напрямую через DocumentFormat.OpenXml — Word на кассе не нужен.
/// </summary>
public sealed class WordReportBuilder : IDisposable
{
    // A4 в твипах (1/20 пункта) и поля 1,8 см по бокам, 2 см сверху и снизу.
    private const int PageWidth = 11906;
    private const int PageHeight = 16838;
    private const int MarginSide = 1021;
    private const int MarginTop = 1134;
    private const int MarginBottom = 1134;
    private const long EmuPerTwip = 635;

    private const string BodyFont = "Calibri";
    private const int TableFontSize = 18; // полупункты: 9 pt

    private readonly WordprocessingDocument _document;
    private readonly MainDocumentPart _main;
    private readonly W.Body _body;
    private readonly string _headerId;
    private readonly string _footerId;
    private readonly List<(string Text, string Anchor, int Level)> _contents = new();
    private W.Paragraph? _contentsAnchor;
    private bool _landscape;
    private int _bookmarkId = 1;
    private uint _drawingId = 1;
    private bool _saved;

    public ReportMeta Meta { get; }

    public WordReportBuilder(string path, ReportMeta meta, bool landscape = false)
    {
        Meta = meta;
        _landscape = landscape;
        _document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        _main = _document.AddMainDocumentPart();
        _main.Document = new W.Document(new W.Body());
        _body = _main.Document.Body!;

        AddStyles();
        AddSettings();
        _headerId = AddHeader();
        _footerId = AddFooter();
    }

    /// <summary>Ширина текста на странице текущего раздела, в твипах.</summary>
    private int ContentWidth => (_landscape ? PageHeight : PageWidth) - 2 * MarginSide;

    // ------------------------------------------------------------------ титульный блок

    /// <summary>Титульный блок: тёмная плашка с названием отчёта и магазином, под ней — реквизиты
    /// (период, кто и когда сформировал) в две пары колонок.</summary>
    public void TitleBlock(IReadOnlyList<(string Label, string Value)>? extraDetails = null)
    {
        var width = ContentWidth;
        var band = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = Twips(width), Type = W.TableWidthUnitValues.Dxa },
                NoBorders(),
                new W.TableLayout { Type = W.TableLayoutValues.Fixed },
                CellMargins(170, 220)),
            new W.TableGrid(new W.GridColumn { Width = Twips(width) }),
            new W.TableRow(new W.TableCell(
                new W.TableCellProperties(
                    new W.TableCellWidth { Width = Twips(width), Type = W.TableWidthUnitValues.Dxa },
                    new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ReportPalette.Accent }),
                TextParagraph(Meta.Title, size: 40, bold: true, color: ReportPalette.HeaderText, after: 40),
                TextParagraph(Meta.Company ?? "", size: 22, color: "DCE6F2", after: 0))));
        _body.AppendChild(band);

        // Сначала то, о чём документ (период, реквизиты накладной), потом — кто и когда его сформировал.
        var details = new List<(string Label, string Value)>();
        if (Meta.Period is not null)
            details.Add((ReportLabels.Period, Meta.Period));
        if (extraDetails is not null)
            details.AddRange(extraDetails);
        details.Add((ReportLabels.Generated, ReportFormat.DateAndTime(Meta.GeneratedAt)));
        if (Meta.GeneratedBy is not null)
            details.Add((ReportLabels.Cashier, Meta.GeneratedBy));
        if (Meta.Cashbox is not null)
            details.Add((ReportLabels.Cashbox, Meta.Cashbox));

        Spacer(100);
        DetailsTable(details);
        Spacer(120);
    }

    /// <summary>Реквизиты парами «подпись — значение», по две пары в строке.</summary>
    public void DetailsTable(IReadOnlyList<(string Label, string Value)> items)
    {
        if (items.Count == 0)
            return;

        var width = ContentWidth;
        var widths = Distribute(width, [0.18, 0.32, 0.18, 0.32]);
        var line = new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4U, Color = "E1E6EE" };
        var table = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = Twips(width), Type = W.TableWidthUnitValues.Dxa },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Color = "E1E6EE" },
                    new W.LeftBorder { Val = W.BorderValues.Nil },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Color = "E1E6EE" },
                    new W.RightBorder { Val = W.BorderValues.Nil },
                    line,
                    new W.InsideVerticalBorder { Val = W.BorderValues.Nil }),
                new W.TableLayout { Type = W.TableLayoutValues.Fixed },
                CellMargins(50, 85)),
            new W.TableGrid(widths.Select(w => new W.GridColumn { Width = Twips(w) })));

        for (var i = 0; i < items.Count; i += 2)
        {
            var row = new W.TableRow(new W.TableRowProperties(new W.CantSplit()));
            for (var pair = 0; pair < 2; pair++)
            {
                var item = i + pair < items.Count ? items[i + pair] : ("", "");
                row.AppendChild(Cell(widths[pair * 2], item.Item1, bold: true, color: ReportPalette.SubtitleText, size: 18));
                row.AppendChild(Cell(widths[pair * 2 + 1], item.Item2, size: 20));
            }

            table.AppendChild(row);
        }

        _body.AppendChild(table);
    }

    // ------------------------------------------------------------------ оглавление и заголовки

    /// <summary>Место под оглавление: пункты вставляются при сохранении, когда известны все
    /// заголовки. Пункты — ссылки: щелчок с Ctrl переводит к разделу.</summary>
    public void ContentsPlaceholder()
    {
        _body.AppendChild(TextParagraph(ReportLabels.Contents, size: 24, bold: true, color: ReportPalette.Accent, before: 120, after: 80));
        _contentsAnchor = new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "0" }));
        _body.AppendChild(_contentsAnchor);
    }

    public void Heading1(string text) => Heading(text, 1);

    public void Heading2(string text) => Heading(text, 2);

    private void Heading(string text, int level)
    {
        var id = (_bookmarkId++).ToString(CultureInfo.InvariantCulture);
        var anchor = "_NmSection" + id;
        _body.AppendChild(new W.Paragraph(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = level == 1 ? "Heading1" : "Heading2" }),
            new W.BookmarkStart { Id = id, Name = anchor },
            new W.Run(new W.Text(Clean(text)) { Space = SpaceProcessingModeValues.Preserve }),
            new W.BookmarkEnd { Id = id }));
        _contents.Add((text, anchor, level));
    }

    // ------------------------------------------------------------------ текст

    public void Paragraph(string text) => _body.AppendChild(TextParagraph(text, after: 120));

    /// <summary>Пояснение мелким серым курсивом.</summary>
    public void Note(string text) =>
        _body.AppendChild(TextParagraph(text, size: 18, italic: true, color: ReportPalette.Muted, after: 120));

    public void PageBreak() =>
        _body.AppendChild(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));

    /// <summary>Новый раздел с другой ориентацией страницы — для широких таблиц.</summary>
    public void NewSection(bool landscape)
    {
        // Свойства раздела Word хранит в последнем абзаце этого раздела.
        _body.AppendChild(new W.Paragraph(new W.ParagraphProperties(SectionProperties(_landscape))));
        _landscape = landscape;
    }

    private void Spacer(int after) =>
        _body.AppendChild(new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "0", After = Twips(after), Line = "120", LineRule = W.LineSpacingRuleValues.Exact })));

    // ------------------------------------------------------------------ таблицы

    /// <summary>Таблица: тёмная шапка (повторяется на каждой странице), чередование строк, числа по
    /// правому краю, строка «Итого» с жирной чертой сверху.</summary>
    /// <param name="tone">Подсветка отдельных ячеек (строка, колонка) — группы ABC, статусы.</param>
    public void Table(IReadOnlyList<WColumn> columns, IReadOnlyList<string[]> rows, string[]? totals = null,
        Func<int, int, ReportTone>? tone = null)
    {
        if (rows.Count == 0)
        {
            Note(ReportLabels.NoData);
            return;
        }

        var width = ContentWidth;
        var widths = Distribute(width, columns.Select(c => c.Weight).ToArray());
        var grid = new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4U, Color = ReportPalette.GridLine };
        var table = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = Twips(width), Type = W.TableWidthUnitValues.Dxa },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Color = ReportPalette.GridLine },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4U, Color = ReportPalette.GridLine },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Color = ReportPalette.GridLine },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4U, Color = ReportPalette.GridLine },
                    grid,
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4U, Color = ReportPalette.GridLine }),
                new W.TableLayout { Type = W.TableLayoutValues.Fixed },
                CellMargins(35, 85)),
            new W.TableGrid(widths.Select(w => new W.GridColumn { Width = Twips(w) })));

        var header = new W.TableRow(new W.TableRowProperties(new W.CantSplit(), new W.TableHeader()));
        for (var c = 0; c < columns.Count; c++)
        {
            header.AppendChild(Cell(widths[c], columns[c].Title, bold: true, color: ReportPalette.HeaderText,
                fill: ReportPalette.Accent, align: Justify(columns[c]), size: TableFontSize));
        }
        table.AppendChild(header);

        for (var r = 0; r < rows.Count; r++)
        {
            var row = new W.TableRow(new W.TableRowProperties(new W.CantSplit()));
            var zebra = r % 2 == 1 ? ReportPalette.Zebra : null;
            for (var c = 0; c < columns.Count; c++)
            {
                var value = c < rows[r].Length ? rows[r][c] : "";
                var cellTone = tone?.Invoke(r, c) ?? ReportTone.None;
                var (fill, color) = ToneColors(cellTone);
                row.AppendChild(Cell(widths[c], value, bold: cellTone is not (ReportTone.None or ReportTone.Muted) && columns[c].Center,
                    color: color, fill: fill ?? zebra, align: Justify(columns[c]), size: TableFontSize));
            }

            table.AppendChild(row);
        }

        if (totals is not null)
        {
            var row = new W.TableRow(new W.TableRowProperties(new W.CantSplit()));
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = Cell(widths[c], c < totals.Length ? totals[c] : "", bold: true, fill: ReportPalette.TotalFill,
                    align: Justify(columns[c]), size: TableFontSize);
                cell.TableCellProperties!.InsertAfter(
                    new W.TableCellBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 12U, Color = ReportPalette.Accent }),
                    cell.TableCellProperties.GetFirstChild<W.TableCellWidth>());
                row.AppendChild(cell);
            }

            table.AppendChild(row);
        }

        _body.AppendChild(table);
        Spacer(160);
    }

    /// <summary>Строки для подписей: «Отгрузил», «Принял» и т. п. — без них накладная не документ.</summary>
    public void Signatures(IReadOnlyList<string> roles)
    {
        var width = ContentWidth;
        var widths = Distribute(width, roles.Select(_ => 1.0).ToArray());
        var table = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = Twips(width), Type = W.TableWidthUnitValues.Dxa },
                NoBorders(),
                new W.TableLayout { Type = W.TableLayoutValues.Fixed },
                CellMargins(40, 85)),
            new W.TableGrid(widths.Select(w => new W.GridColumn { Width = Twips(w) })));

        var row = new W.TableRow(new W.TableRowProperties(new W.CantSplit()));
        for (var i = 0; i < roles.Count; i++)
        {
            var cell = new W.TableCell(
                new W.TableCellProperties(new W.TableCellWidth { Width = Twips(widths[i]), Type = W.TableWidthUnitValues.Dxa }),
                TextParagraph(roles[i], size: 20, bold: true, after: 360),
                TextParagraph("_____________ / _____________", size: 20, after: 0),
                TextParagraph(Tr.T("подпись / Ф.И.О.", "колу / аты-жөнү", "signature / full name", "imza / ad soyad", "imzo / F.I.Sh."),
                    size: 16, color: ReportPalette.Muted, after: 200),
                TextParagraph(Tr.T("Дата", "Күнү", "Date", "Tarih", "Sana") + ": ______________", size: 20, after: 0));
            row.AppendChild(cell);
        }

        table.AppendChild(row);
        _body.AppendChild(table);
    }

    // ------------------------------------------------------------------ графики

    /// <summary>График картинкой во всю ширину текста страницы (с сохранением пропорций).</summary>
    public void Image(byte[] png, string description)
    {
        var (pixelWidth, pixelHeight) = PngSize(png);
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return;

        var imagePart = _main.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(png))
            imagePart.FeedData(stream);
        var relationshipId = _main.GetIdOfPart(imagePart);

        var width = ContentWidth * EmuPerTwip;
        var height = width * pixelHeight / pixelWidth;
        // Высокая картинка не должна занимать больше половины страницы — иначе заголовок раздела
        // остаётся на одной странице, а график уезжает на следующую.
        var maxHeight = (long)((_landscape ? PageWidth : PageHeight) - MarginTop - MarginBottom) * EmuPerTwip / 2;
        if (height > maxHeight)
        {
            width = width * maxHeight / height;
            height = maxHeight;
        }

        var id = _drawingId++;
        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = "Chart " + id, Description = Clean(description) },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = id, Name = "chart" + id + ".png" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relationshipId },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = width, Cy = height }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
            });

        _body.AppendChild(new W.Paragraph(
            new W.ParagraphProperties(
                new W.KeepLines(),
                new W.SpacingBetweenLines { Before = "60", After = "200" },
                new W.Justification { Val = W.JustificationValues.Center }),
            new W.Run(drawing)));
    }

    /// <summary>Ширина и высота PNG из заголовка IHDR — без загрузки картинки целиком.</summary>
    private static (long Width, long Height) PngSize(byte[] png)
    {
        if (png.Length < 24)
            return (0, 0);

        static long ReadInt(byte[] b, int offset) => (b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3];
        return (ReadInt(png, 16), ReadInt(png, 20));
    }

    // ------------------------------------------------------------------ сохранение

    public void Save()
    {
        if (_saved)
            return;
        _saved = true;

        if (_contentsAnchor is not null)
        {
            foreach (var (text, anchor, level) in _contents)
            {
                var link = new W.Hyperlink(new W.Run(
                    new W.RunProperties(
                        new W.Bold { Val = level == 1 },
                        new W.Color { Val = level == 1 ? ReportPalette.Accent : ReportPalette.Link },
                        new W.FontSize { Val = level == 1 ? "21" : "19" }),
                    new W.Text(Clean(text)) { Space = SpaceProcessingModeValues.Preserve }))
                {
                    Anchor = anchor,
                    History = true,
                };

                _contentsAnchor.InsertBeforeSelf(new W.Paragraph(
                    new W.ParagraphProperties(
                        new W.SpacingBetweenLines { Before = level == 1 ? "60" : "0", After = "20" },
                        new W.Indentation { Left = level == 1 ? "0" : "360" }),
                    link));
            }
        }

        _body.AppendChild(SectionProperties(_landscape));
        _main.Document.Save();

        _document.PackageProperties.Title = Meta.Title;
        _document.PackageProperties.Subject = Meta.Subtitle;
        _document.PackageProperties.Creator = Meta.GeneratedBy ?? "NurMarket";
        _document.PackageProperties.Created = Meta.GeneratedAt.ToUniversalTime();
    }

    /// <summary>Закрывает файл; сохранение — только явным <see cref="Save"/>.</summary>
    public void Dispose() => _document.Dispose();

    // ------------------------------------------------------------------ служебное

    private W.SectionProperties SectionProperties(bool landscape) =>
        new(
            new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = _headerId },
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = _footerId },
            new W.PageSize
            {
                Width = (uint)(landscape ? PageHeight : PageWidth),
                Height = (uint)(landscape ? PageWidth : PageHeight),
                Orient = landscape ? W.PageOrientationValues.Landscape : W.PageOrientationValues.Portrait,
            },
            new W.PageMargin
            {
                Top = MarginTop,
                Right = (uint)MarginSide,
                Bottom = MarginBottom,
                Left = (uint)MarginSide,
                Header = 567U,
                Footer = 567U,
                Gutter = 0U,
            });

    private static string Twips(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int[] Distribute(int total, double[] weights)
    {
        var sum = weights.Sum();
        var result = weights.Select(w => (int)Math.Floor(total * w / sum)).ToArray();
        result[^1] += total - result.Sum();
        return result;
    }

    private static W.JustificationValues Justify(WColumn column) =>
        column.Numeric ? W.JustificationValues.Right : column.Center ? W.JustificationValues.Center : W.JustificationValues.Left;

    private static (string? Fill, string? Color) ToneColors(ReportTone tone) => tone switch
    {
        ReportTone.Good => (ReportPalette.GoodFill, ReportPalette.GoodText),
        ReportTone.Warn => (ReportPalette.WarnFill, ReportPalette.WarnText),
        ReportTone.Info => (ReportPalette.InfoFill, ReportPalette.InfoText),
        ReportTone.Bad => (ReportPalette.BadFill, ReportPalette.BadText),
        ReportTone.Muted => (ReportPalette.MutedFill, ReportPalette.MutedText),
        _ => (null, null),
    };

    private static W.TableBorders NoBorders() =>
        new(
            new W.TopBorder { Val = W.BorderValues.Nil },
            new W.LeftBorder { Val = W.BorderValues.Nil },
            new W.BottomBorder { Val = W.BorderValues.Nil },
            new W.RightBorder { Val = W.BorderValues.Nil },
            new W.InsideHorizontalBorder { Val = W.BorderValues.Nil },
            new W.InsideVerticalBorder { Val = W.BorderValues.Nil });

    private static W.TableCellMarginDefault CellMargins(int vertical, int horizontal) =>
        new(
            new W.TopMargin { Width = Twips(vertical), Type = W.TableWidthUnitValues.Dxa },
            new W.TableCellLeftMargin { Width = (short)horizontal, Type = W.TableWidthValues.Dxa },
            new W.BottomMargin { Width = Twips(vertical), Type = W.TableWidthUnitValues.Dxa },
            new W.TableCellRightMargin { Width = (short)horizontal, Type = W.TableWidthValues.Dxa });

    private static W.TableCell Cell(int width, string text, bool bold = false, string? color = null, string? fill = null,
        W.JustificationValues? align = null, int size = 20)
    {
        var properties = new W.TableCellProperties(new W.TableCellWidth { Width = Twips(width), Type = W.TableWidthUnitValues.Dxa });
        if (fill is not null)
            properties.AppendChild(new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        properties.AppendChild(new W.TableCellVerticalAlignment { Val = W.TableVerticalAlignmentValues.Center });

        var paragraphProperties = new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto });
        if (align is { } a)
            paragraphProperties.AppendChild(new W.Justification { Val = a });

        return new W.TableCell(properties, new W.Paragraph(paragraphProperties, TextRun(text, size, bold, italic: false, color)));
    }

    private static W.Paragraph TextParagraph(string text, int size = 20, bool bold = false, bool italic = false,
        string? color = null, int before = 0, int after = 80)
    {
        return new W.Paragraph(
            new W.ParagraphProperties(new W.SpacingBetweenLines { Before = Twips(before), After = Twips(after) }),
            TextRun(text, size, bold, italic, color));
    }

    private static W.Run TextRun(string text, int size, bool bold, bool italic, string? color)
    {
        var properties = new W.RunProperties();
        if (bold)
            properties.AppendChild(new W.Bold());
        if (italic)
            properties.AppendChild(new W.Italic());
        if (color is not null)
            properties.AppendChild(new W.Color { Val = color });
        properties.AppendChild(new W.FontSize { Val = size.ToString(CultureInfo.InvariantCulture) });
        properties.AppendChild(new W.FontSizeComplexScript { Val = size.ToString(CultureInfo.InvariantCulture) });
        return new W.Run(properties, new W.Text(Clean(text)) { Space = SpaceProcessingModeValues.Preserve });
    }

    /// <summary>Управляющие символы запрещены в XML — одно такое название товара сломало бы файл.</summary>
    private static string Clean(string? text) => XlSharedStrings.Sanitize(text ?? "");

    private string AddHeader()
    {
        var part = _main.AddNewPart<HeaderPart>();
        part.Header = new W.Header(new W.Paragraph(
            new W.ParagraphProperties(
                new W.ParagraphStyleId { Val = "Header" },
                new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Space = 4U, Color = "BFBFBF" })),
            TextRun(Meta.Company ?? "", 16, bold: true, italic: false, ReportPalette.Muted),
            new W.Run(new W.PositionalTab
            {
                Alignment = W.AbsolutePositionTabAlignmentValues.Right,
                RelativeTo = W.AbsolutePositionTabPositioningBaseValues.Margin,
                Leader = W.AbsolutePositionTabLeaderCharValues.None,
            }),
            TextRun(Meta.Title, 16, bold: false, italic: false, ReportPalette.Muted)));
        part.Header.Save();
        return _main.GetIdOfPart(part);
    }

    private string AddFooter()
    {
        var part = _main.AddNewPart<FooterPart>();
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(
                new W.ParagraphStyleId { Val = "Footer" },
                new W.ParagraphBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Space = 4U, Color = "BFBFBF" })),
            TextRun(ReportLabels.Generated + ": " + ReportFormat.DateAndTime(Meta.GeneratedAt), 16, bold: false, italic: false, ReportPalette.Muted),
            new W.Run(new W.PositionalTab
            {
                Alignment = W.AbsolutePositionTabAlignmentValues.Right,
                RelativeTo = W.AbsolutePositionTabPositioningBaseValues.Margin,
                Leader = W.AbsolutePositionTabLeaderCharValues.None,
            }));

        // «Страница {P} из {N}» — номера подставляет сам Word (поля PAGE и NUMPAGES).
        var template = ReportLabels.WordPageOf;
        var position = 0;
        while (position < template.Length)
        {
            var next = template.IndexOf('{', position);
            if (next < 0)
            {
                paragraph.AppendChild(TextRun(template[position..], 16, false, false, ReportPalette.Muted));
                break;
            }

            if (next > position)
                paragraph.AppendChild(TextRun(template[position..next], 16, false, false, ReportPalette.Muted));

            var close = template.IndexOf('}', next);
            var token = template[(next + 1)..close];
            paragraph.AppendChild(new W.SimpleField(TextRun("1", 16, true, false, ReportPalette.SubtitleText))
            {
                Instruction = token == "P" ? " PAGE " : " NUMPAGES ",
            });
            position = close + 1;
        }

        part.Footer = new W.Footer(paragraph);
        part.Footer.Save();
        return _main.GetIdOfPart(part);
    }

    private void AddSettings()
    {
        var part = _main.AddNewPart<DocumentSettingsPart>();
        // Режим совместимости 15 (Word 2013+): без него Word открывает файл «в режиме
        // ограниченной функциональности» и пишет это в заголовке окна.
        part.Settings = new W.Settings(
            new W.Zoom { Percent = "100" },
            new W.DefaultTabStop { Val = 708 },
            new W.CharacterSpacingControl { Val = W.CharacterSpacingValues.DoNotCompress },
            new W.Compatibility(new W.CompatibilitySetting
            {
                Name = W.CompatSettingNameValues.CompatibilityMode,
                Uri = "http://schemas.microsoft.com/office/word",
                Val = "15",
            }));
        part.Settings.Save();
    }

    private void AddStyles()
    {
        var language = ReportFormat.LanguageTag;
        var part = _main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new W.Styles(
            new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                    new W.RunFonts { Ascii = BodyFont, HighAnsi = BodyFont, ComplexScript = BodyFont, EastAsia = BodyFont },
                    new W.FontSize { Val = "20" },
                    new W.FontSizeComplexScript { Val = "20" },
                    new W.Languages { Val = language, EastAsia = language })),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                    new W.SpacingBetweenLines { After = "80", Line = "264", LineRule = W.LineSpacingRuleValues.Auto }))),
            ParagraphStyle("Normal", "Normal", basedOn: null, isDefault: true, uiPriority: null, primary: true, null, null),
            ParagraphStyle("Heading1", "heading 1", "Normal", false, 9, true,
                new W.StyleParagraphProperties(
                    new W.KeepNext(),
                    new W.KeepLines(),
                    new W.ParagraphBorders(new W.BottomBorder { Val = W.BorderValues.Single, Size = 6U, Space = 2U, Color = ReportPalette.Accent }),
                    new W.SpacingBetweenLines { Before = "360", After = "160" },
                    new W.OutlineLevel { Val = 0 }),
                new W.StyleRunProperties(
                    new W.Bold(),
                    new W.Color { Val = ReportPalette.Accent },
                    new W.FontSize { Val = "30" },
                    new W.FontSizeComplexScript { Val = "30" })),
            ParagraphStyle("Heading2", "heading 2", "Normal", false, 9, true,
                new W.StyleParagraphProperties(
                    new W.KeepNext(),
                    new W.KeepLines(),
                    new W.SpacingBetweenLines { Before = "280", After = "100" },
                    new W.OutlineLevel { Val = 1 }),
                new W.StyleRunProperties(
                    new W.Bold(),
                    new W.Color { Val = ReportPalette.AccentSoft },
                    new W.FontSize { Val = "24" },
                    new W.FontSizeComplexScript { Val = "24" })),
            ParagraphStyle("Header", "header", "Normal", false, 99, false,
                new W.StyleParagraphProperties(new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto }),
                new W.StyleRunProperties(new W.FontSize { Val = "16" }, new W.FontSizeComplexScript { Val = "16" })),
            ParagraphStyle("Footer", "footer", "Normal", false, 99, false,
                new W.StyleParagraphProperties(new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto }),
                new W.StyleRunProperties(new W.FontSize { Val = "16" }, new W.FontSizeComplexScript { Val = "16" })));
        part.Styles.Save();
    }

    private static W.Style ParagraphStyle(string id, string name, string? basedOn, bool isDefault, int? uiPriority, bool primary,
        W.StyleParagraphProperties? paragraph, W.StyleRunProperties? run)
    {
        var style = new W.Style { Type = W.StyleValues.Paragraph, StyleId = id };
        if (isDefault)
            style.Default = true;
        style.AppendChild(new W.StyleName { Val = name });
        if (basedOn is not null)
            style.AppendChild(new W.BasedOn { Val = basedOn });
        style.AppendChild(new W.NextParagraphStyle { Val = "Normal" });
        if (uiPriority is { } priority)
            style.AppendChild(new W.UIPriority { Val = priority });
        if (primary)
            style.AppendChild(new W.PrimaryStyle());
        if (paragraph is not null)
            style.AppendChild(paragraph);
        if (run is not null)
            style.AppendChild(run);
        return style;
    }
}
