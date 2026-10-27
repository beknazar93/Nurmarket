using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Стиль текста на листе A4: размер в пунктах и Ж / К / П (жирный, курсив, подчёркнутый).</summary>
public sealed class A4TextStyle
{
    public double SizePt { get; set; } = 12;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool Underline { get; set; }
}

/// <summary>2026-10-26: настройки листа A4 окна «Печать штрихкодов и ценников» — как боковая панель «Печать на A4» страницы
/// «Печать штрих-кодов» на сайте NurCRM. Сохраняются в BarcodePrintSettingsStore.</summary>
public sealed class A4SheetOptions
{
    /// <summary>Заголовок листа (адрес и т. п.) — одна строка сверху каждого листа. Пусто — полосы нет.</summary>
    public string Title { get; set; } = "";
    public bool Landscape { get; set; }
    public int Columns { get; set; } = 3;
    public int Rows { get; set; } = 8;
    /// <summary>Высота штрихкода в пикселях экрана (96 на дюйм), вместе с цифрами под ним.</summary>
    public int BarcodeHeightPx { get; set; } = 48;
    public bool ShowBarcode { get; set; } = true;
    public bool ShowName { get; set; } = true;
    public bool ShowPrice { get; set; } = true;
    /// <summary>«Скидка (зачеркнуть старую цену)».</summary>
    public bool ShowDiscount { get; set; }
    public bool ShowPlu { get; set; }
    public bool ShowDescription { get; set; }
    public A4TextStyle Name { get; set; } = new() { SizePt = 12, Bold = true };
    public A4TextStyle Price { get; set; } = new() { SizePt = 16, Bold = true };
    /// <summary>Тонкая пунктирная рамка вокруг каждой этикетки (по ней режут лист).</summary>
    public bool CutLines { get; set; } = true;

    public A4SheetOptions Clone() => new()
    {
        Title = Title, Landscape = Landscape, Columns = Columns, Rows = Rows, BarcodeHeightPx = BarcodeHeightPx,
        ShowBarcode = ShowBarcode, ShowName = ShowName, ShowPrice = ShowPrice, ShowDiscount = ShowDiscount, ShowPlu = ShowPlu,
        ShowDescription = ShowDescription, CutLines = CutLines,
        Name = new A4TextStyle { SizePt = Name.SizePt, Bold = Name.Bold, Italic = Name.Italic, Underline = Name.Underline },
        Price = new A4TextStyle { SizePt = Price.SizePt, Bold = Price.Bold, Italic = Price.Italic, Underline = Price.Underline },
    };
}

/// <summary>Одна позиция листа A4: данные товара (в PriceTagData цена уже готова — «новая», старая в OldPriceText), копии, ПЛУ, описание.</summary>
public sealed record A4SheetItem(PriceTagData Data, int Copies, string? Plu = null, string? Description = null);

/// <summary>Раскладка листа: размер листа, размер ячейки и верх сетки (мм).</summary>
public readonly record struct SheetGeometry(double PageWmm, double PageHmm, double CellWmm, double CellHmm, double GridTopMm,
    int Columns, int Rows, bool HasHeader, double HeaderHeightMm)
{
    public int PerPage => Columns * Rows;
}

/// <summary>2026-10-26: лист A4 с сеткой этикеток «как на сайте» — N в ряд, M рядов, книжная/альбомная, заголовок листа, размеры шрифтов
/// названия и цены, зачёркнутая старая цена, ПЛУ, описание. Штрихкод рисует тот же BarcodeLabelService.DrawLinearBlock, что и этикетки
/// и ценники (EAN/Code 128, целые точки принтера). Один и тот же <see cref="DrawCell"/> рисует предпросмотр и печать.
/// Прежние PriceTagService.PrintBatchA4 / PrintBatchA4CustomTemplate не менялись.</summary>
public static class A4SheetService
{
    public const double MarginMm = 8;
    public const double GapMm = 1.5;
    public const double HeaderMm = 7;
    private const float WorldDpi = 203f; // раскладка считается в точках 203 dpi, как у всех этикеток (BarcodeLabelService.Dpi)

    public static SheetGeometry GetGeometry(A4SheetOptions o)
    {
        var landscape = o.Landscape;
        var pageW = landscape ? 297.0 : 210.0;
        var pageH = landscape ? 210.0 : 297.0;
        var cols = Math.Clamp(o.Columns, 1, 10);
        var rows = Math.Clamp(o.Rows, 1, 30);
        var hasHeader = !string.IsNullOrWhiteSpace(o.Title);
        var headerH = hasHeader ? HeaderMm : 0;
        var usableW = pageW - 2 * MarginMm;
        var usableH = pageH - 2 * MarginMm - headerH;
        var cellW = Math.Max((usableW - GapMm * (cols - 1)) / cols, 5);
        var cellH = Math.Max((usableH - GapMm * (rows - 1)) / rows, 5);
        return new SheetGeometry(pageW, pageH, cellW, cellH, MarginMm + headerH, cols, rows, hasHeader, headerH);
    }

    /// <summary>Каждая копия — отдельная этикетка в сетке.</summary>
    public static List<A4SheetItem> Expand(IReadOnlyList<A4SheetItem> items)
    {
        var list = new List<A4SheetItem>();
        foreach (var it in items)
            for (var i = 0; i < Math.Clamp(it.Copies, 1, 99); i++)
                list.Add(it);
        return list;
    }

    public static int LabelCount(IReadOnlyList<A4SheetItem> items) => items.Sum(i => Math.Clamp(i.Copies, 1, 99));

    public static int PageCount(int labels, A4SheetOptions o) => labels == 0 ? 0 : (labels + GetGeometry(o).PerPage - 1) / GetGeometry(o).PerPage;

    private static float Px(double mm) => (float)(mm / 25.4 * WorldDpi);

    private static Font MakeFont(A4TextStyle s, double scale = 1, bool strike = false)
    {
        var style = FontStyle.Regular;
        if (s.Bold) style |= FontStyle.Bold;
        if (s.Italic) style |= FontStyle.Italic;
        if (s.Underline) style |= FontStyle.Underline;
        if (strike) style |= FontStyle.Strikeout;
        var px = (float)Math.Max(Math.Clamp(s.SizePt, 4, 96) * scale * WorldDpi / 72.0, 4);
        return new Font("Arial", px, style, GraphicsUnit.Pixel);
    }

    /// <summary>Рисует одну этикетку в мировых координатах g (точки 203 dpi, начало — левый верхний угол ячейки).</summary>
    public static void DrawCell(Graphics g, A4SheetItem it, A4SheetOptions o, double cellWmm, double cellHmm)
    {
        var W = Px(cellWmm);
        var H = Px(cellHmm);
        var pad = Px(1.2);
        var innerW = Math.Max(W - 2 * pad, 4);
        var innerH = Math.Max(H - 2 * pad, 4);
        var gap = Px(0.8);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        // Размеры текста считаются на «чистой» поверхности без масштаба: MeasureString на g учитывает ScaleTransform (на 600 dpi принтера высота цены
        // выходила втрое больше, и цена уезжала за край этикетки).
        using var scratch = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(scratch);
        mg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        if (o.CutLines)
        {
            using var pen = new Pen(Color.FromArgb(170, 170, 170), 1f) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, 0.5f, 0.5f, W - 1, H - 1);
        }

        using var center = new StringFormat(StringFormatFlags.LineLimit) { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
        // Одна строка (цена, ПЛУ): без LineLimit — иначе при высоте ровно в строку GDI+ на некоторых разрешениях (600 dpi принтера) ничего не рисует.
        using var single = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        var data = it.Data;

        // Блоки сверху вниз: название, штрихкод, ПЛУ, описание, цена (старая зачёркнутая — над новой).
        using var nameFont = MakeFont(o.Name);
        using var priceFont = MakeFont(o.Price);
        using var oldFont = MakeFont(o.Price, 0.6, strike: true);
        using var smallBold = MakeFont(new A4TextStyle { SizePt = 8, Bold = true });
        using var descFont = MakeFont(new A4TextStyle { SizePt = 7, Bold = false });

        var showName = o.ShowName && !string.IsNullOrWhiteSpace(data.ProductName);
        var code = data.Barcode?.Trim();
        var showBar = o.ShowBarcode && !string.IsNullOrWhiteSpace(code);
        var pluText = o.ShowPlu && !string.IsNullOrWhiteSpace(it.Plu) ? Tr.T("ПЛУ", "ПЛУ", "PLU", "PLU", "PLU") + " " + it.Plu!.Trim() : null;
        var desc = o.ShowDescription && !string.IsNullOrWhiteSpace(it.Description) ? it.Description!.Trim() : null;
        var showPrice = o.ShowPrice && !string.IsNullOrWhiteSpace(data.PriceText);
        var oldText = showPrice && o.ShowDiscount && !string.IsNullOrWhiteSpace(data.OldPriceText) ? data.OldPriceText : null;

        var hName = 0f; var hPlu = 0f; var hDesc = 0f; var hPrice = 0f; var hOld = 0f;
        if (showName)
        {
            var line = nameFont.GetHeight(mg);
            var sz = mg.MeasureString(data.ProductName, nameFont, (int)innerW, center);
            hName = Math.Min(sz.Height, line * 2.05f) + 1;
        }
        if (pluText != null) hPlu = mg.MeasureString(pluText, smallBold, (int)innerW, single).Height;
        if (desc != null)
        {
            var line = descFont.GetHeight(mg);
            hDesc = Math.Min(mg.MeasureString(desc, descFont, (int)innerW, center).Height, line * 2.05f) + 1;
        }
        if (showPrice)
        {
            hPrice = mg.MeasureString(data.PriceText, priceFont, (int)innerW, single).Height;
            if (oldText != null) hOld = mg.MeasureString(oldText, oldFont, (int)innerW, single).Height;
        }

        var blocks = (showName ? 1 : 0) + (showBar ? 1 : 0) + (pluText != null ? 1 : 0) + (desc != null ? 1 : 0) + (showPrice ? 1 : 0);
        var gaps = gap * Math.Max(blocks - 1, 0);
        var others = hName + hPlu + hDesc + hPrice + hOld;
        var hBar = 0f;
        if (showBar)
        {
            var wanted = Px(LabelAutoLayout.PxToMm(Math.Clamp(o.BarcodeHeightPx, 8, 300)));
            var room = innerH - others - gaps;
            hBar = Math.Min(wanted, Math.Max(room, Px(5)));
        }
        var total = others + hBar + gaps;
        var y = pad + Math.Max(0, (innerH - total) / 2f);

        if (showName)
        {
            g.DrawString(data.ProductName, nameFont, Brushes.Black, new RectangleF(pad, y, innerW, hName), center);
            y += hName + gap;
        }
        if (showBar)
        {
            var rect = new RectangleF(pad, y, innerW, hBar);
            var digitsH = Math.Min(rect.Height * 0.2f, 14f);
            BarcodeLabelService.DrawLinearBlock(g, rect, new RectangleF(0, 0, W, H), code, LabelBarcodeFormat.Auto, 2,
                digitsH, Math.Clamp(digitsH * 0.7f, 6f, 12f));
            y += hBar + gap;
        }
        if (pluText != null)
        {
            g.DrawString(pluText, smallBold, Brushes.Black, new RectangleF(pad, y, innerW, hPlu + 1), single);
            y += hPlu + gap;
        }
        if (desc != null)
        {
            using var gray = new SolidBrush(Color.FromArgb(90, 90, 90));
            g.DrawString(desc, descFont, gray, new RectangleF(pad, y, innerW, hDesc), center);
            y += hDesc + gap;
        }
        if (showPrice)
        {
            if (oldText != null)
            {
                using var gray = new SolidBrush(Color.FromArgb(90, 90, 90));
                g.DrawString(oldText, oldFont, gray, new RectangleF(pad, y, innerW, hOld + 1), single);
                y += hOld;
            }
            g.DrawString(data.PriceText, priceFont, Brushes.Black, new RectangleF(pad, y, innerW, hPrice + 1), single);
        }
    }

    /// <summary>Картинка одной этикетки в разрешении <paramref name="dpi"/> (для печати на принтере).</summary>
    internal static Bitmap RenderCell(A4SheetItem it, A4SheetOptions o, SheetGeometry geo, int dpi)
    {
        var bmp = new Bitmap(Math.Max((int)Math.Round(geo.CellWmm / 25.4 * dpi), 10), Math.Max((int)Math.Round(geo.CellHmm / 25.4 * dpi), 10));
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.ScaleTransform(dpi / WorldDpi, dpi / WorldDpi);
        DrawCell(g, it, o, geo.CellWmm, geo.CellHmm);
        return bmp;
    }

    private static void DrawHeader(Graphics g, string title, double widthMm)
    {
        using var font = MakeFont(new A4TextStyle { SizePt = 11, Bold = true });
        using var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.DrawString(title.Trim(), font, Brushes.Black, new RectangleF(0, 0, Px(widthMm), Px(HeaderMm)), f);
    }

    /// <summary>Предпросмотр одного листа (номер с нуля) — целая страница в разрешении <paramref name="dpi"/>.</summary>
    public static Bitmap RenderPage(IReadOnlyList<A4SheetItem> expanded, int pageIndex, A4SheetOptions o, int dpi = 110)
    {
        var geo = GetGeometry(o);
        var bmp = new Bitmap((int)Math.Round(geo.PageWmm / 25.4 * dpi), (int)Math.Round(geo.PageHmm / 25.4 * dpi));
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        var scale = dpi / WorldDpi;
        if (geo.HasHeader)
        {
            var state = g.Save();
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(Px(MarginMm), Px(MarginMm));
            DrawHeader(g, o.Title, geo.PageWmm - 2 * MarginMm);
            g.Restore(state);
        }
        var start = Math.Max(pageIndex, 0) * geo.PerPage;
        for (var i = 0; i < geo.PerPage && start + i < expanded.Count; i++)
        {
            var col = i % geo.Columns;
            var row = i / geo.Columns;
            var state = g.Save();
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(Px(MarginMm + col * (geo.CellWmm + GapMm)), Px(geo.GridTopMm + row * (geo.CellHmm + GapMm)));
            DrawCell(g, expanded[start + i], o, geo.CellWmm, geo.CellHmm);
            g.Restore(state);
        }
        return bmp;
    }

    /// <summary>Документ печати листов A4 (спулер Windows). Каждая этикетка рисуется картинкой в разрешении принтера и кладётся точка-в-точку
    /// (как у PriceTagService.CreateA4GridDocument). Вынесен отдельно, чтобы стенд прогнал ровно этот путь в картинку без настоящей печати.
    /// <paramref name="printerName"/> пустое — принтер по умолчанию (только для проверки).</summary>
    public static PrintDocument? CreateDocument(IReadOnlyList<A4SheetItem> items, A4SheetOptions o, string? printerName)
    {
        var expanded = Expand(items);
        if (expanded.Count == 0)
            return null;
        var geo = GetGeometry(o);
        var pageIndex = 0;
        var doc = new PrintDocument();
        if (!string.IsNullOrWhiteSpace(printerName))
            doc.PrinterSettings.PrinterName = printerName;
        doc.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169);
        doc.DefaultPageSettings.Landscape = o.Landscape;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            var dpi = BarcodeLabelService.RenderDpiFor(g.DpiX);
            if (geo.HasHeader)
            {
                var hw = geo.PageWmm - 2 * MarginMm;
                using var hb = new Bitmap(Math.Max((int)Math.Round(hw / 25.4 * dpi), 10), Math.Max((int)Math.Round(HeaderMm / 25.4 * dpi), 10));
                using (var hg = Graphics.FromImage(hb))
                {
                    hg.Clear(Color.White);
                    hg.ScaleTransform(dpi / WorldDpi, dpi / WorldDpi);
                    DrawHeader(hg, o.Title, hw);
                }
                BarcodeLabelService.DrawOnPrinterPage(g, hb, MarginMm, MarginMm, dpi);
            }
            var start = pageIndex * geo.PerPage;
            for (var i = 0; i < geo.PerPage && start + i < expanded.Count; i++)
            {
                var col = i % geo.Columns;
                var row = i / geo.Columns;
                using var cell = RenderCell(expanded[start + i], o, geo, dpi);
                BarcodeLabelService.DrawOnPrinterPage(g, cell, MarginMm + col * (geo.CellWmm + GapMm), geo.GridTopMm + row * (geo.CellHmm + GapMm), dpi);
            }
            pageIndex++;
            e.HasMorePages = pageIndex * geo.PerPage < expanded.Count;
        };
        return doc;
    }

    /// <summary>Печать листов на выбранный принтер Windows.</summary>
    public static LabelPrintResult Print(IReadOnlyList<A4SheetItem> items, A4SheetOptions o, string printerName)
    {
        try
        {
            if (!RawPrinterHelper.GetInstalledPrinterNames().Any(p => string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase)))
            {
                PosLogger.Log($"A4 sheet print: printer not found '{printerName}'", "WARNING");
                return LabelPrintResult.PrinterNotFound;
            }
            using var doc = CreateDocument(items, o, printerName);
            if (doc is null)
                return LabelPrintResult.Failed;
            doc.Print();
            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"A4 sheet print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
    }
}
