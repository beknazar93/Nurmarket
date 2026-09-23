using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace NurMarketKassa.Services;

/// <summary>Один из готовых шаблонов ценника — аналог набора шаблонов на service-online.su/forms/cenniki.</summary>
public enum PriceTagKind
{
    Simple,
    WithBarcode,
    Promotional,
    PromotionalColored,
    Detailed,
    WithQr,

    /// <summary>Свободно настраиваемый шаблон (2026-09-06) — рисуется НЕ через GenerateBitmap
    /// в этом файле, а через BarcodeLabelService.GenerateLabelBitmap по LabelTemplate,
    /// сохранённому в PriceTagTemplateStore. Значение существует только как пункт выбора в UI —
    /// вызывающий код обязан ветвиться на Custom ДО обращения к GenerateBitmap/GetDefaultSize.</summary>
    Custom,
}

/// <summary>Целевое устройство печати ценника.</summary>
public enum PriceTagPrintTarget { Thermal, A4 }

/// <summary>Данные товара для отрисовки ценника — независимо от источника (карточка каталога).</summary>
public sealed record PriceTagData(
    string ProductName,
    string? Barcode,
    string PriceText,
    string? OldPriceText = null,
    string? DiscountText = null,
    string? Sku = null,
    string? Unit = null,
    string? Category = null,
    string? StoreName = null);

public sealed record PriceTagPrintRequest(
    PriceTagKind Kind,
    PriceTagData Data,
    double WidthMm,
    double HeightMm,
    int Copies,
    string PrinterName,
    PriceTagPrintTarget Target);

/// <summary>
/// Рендерит и печатает ценники по одному из 6 готовых шаблонов (без drag/resize-редактора —
/// в отличие от <see cref="LabelTemplate"/>, пользователь просто выбирает готовый вид).
/// Печать — либо на термопринтер этикеток (через тот же путь, что и BarcodeLabelService:
/// WinUSB/LPT/COM/raw-USB/спулер), либо на обычный офисный принтер A4 сеткой ценников на листе.
/// </summary>
public static class PriceTagService
{
    private static readonly Color AccentColor = Color.FromArgb(217, 63, 61);

    public static (double WidthMm, double HeightMm) GetDefaultSize(PriceTagKind kind) => kind switch
    {
        PriceTagKind.Simple => (40, 20),
        PriceTagKind.WithBarcode => (40, 30),
        PriceTagKind.Promotional => (50, 30),
        PriceTagKind.PromotionalColored => (50, 30),
        PriceTagKind.Detailed => (60, 40),
        PriceTagKind.WithQr => (40, 30),
        _ => (40, 30),
    };

    public static Bitmap GenerateBitmap(PriceTagKind kind, PriceTagData data, double widthMm, double heightMm)
    {
        var widthPx = Math.Max((int)BarcodeLabelService.MmToPx(widthMm), 10);
        var heightPx = Math.Max((int)BarcodeLabelService.MmToPx(heightMm), 10);

        var bmp = new Bitmap(widthPx, heightPx);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);

        switch (kind)
        {
            case PriceTagKind.Simple: DrawSimple(g, data, widthMm, heightMm); break;
            case PriceTagKind.WithBarcode: DrawWithBarcode(g, data, widthMm, heightMm); break;
            case PriceTagKind.Promotional: DrawPromotional(g, data, widthMm, heightMm, colored: false); break;
            case PriceTagKind.PromotionalColored: DrawPromotional(g, data, widthMm, heightMm, colored: true); break;
            case PriceTagKind.Detailed: DrawDetailed(g, data, widthMm, heightMm); break;
            case PriceTagKind.WithQr: DrawWithQr(g, data, widthMm, heightMm); break;
        }

        return bmp;
    }

    private static void DrawSimple(Graphics g, PriceTagData data, double w, double h)
    {
        DrawText(g, Frac(w, h, 0.05, 0.06, 0.9, 0.36), data.ProductName, "Arial", FontStyle.Regular, Color.Black);
        DrawText(g, Frac(w, h, 0.05, 0.46, 0.9, 0.48), data.PriceText, "Arial", FontStyle.Bold, Color.Black);
    }

    private static void DrawWithBarcode(Graphics g, PriceTagData data, double w, double h)
    {
        DrawBarcode(g, Frac(w, h, 0.05, 0.03, 0.9, 0.55), data.Barcode);
        DrawText(g, Frac(w, h, 0.05, 0.60, 0.9, 0.18), data.ProductName, "Arial", FontStyle.Regular, Color.Black);
        DrawText(g, Frac(w, h, 0.05, 0.80, 0.9, 0.18), data.PriceText, "Arial", FontStyle.Bold, Color.Black);
    }

    private static void DrawPromotional(Graphics g, PriceTagData data, double w, double h, bool colored)
    {
        if (colored)
        {
            using var bg = new SolidBrush(AccentColor);
            g.FillRectangle(bg, 0, 0, BarcodeLabelService.MmToPx(w), BarcodeLabelService.MmToPx(h));
        }

        var textColor = colored ? Color.White : Color.Black;
        var mutedColor = colored ? Color.FromArgb(255, 255, 220, 220) : Color.DimGray;

        DrawText(g, Frac(w, h, 0.05, 0.04, 0.9, 0.20), data.ProductName, "Arial", FontStyle.Regular, textColor);

        if (!string.IsNullOrWhiteSpace(data.OldPriceText))
            DrawText(g, Frac(w, h, 0.05, 0.28, 0.55, 0.18), data.OldPriceText!, "Arial", FontStyle.Strikeout, mutedColor);

        if (!string.IsNullOrWhiteSpace(data.DiscountText))
        {
            var badgeRect = Frac(w, h, 0.62, 0.26, 0.33, 0.22);
            if (!colored)
            {
                using var badgeBrush = new SolidBrush(AccentColor);
                g.FillRectangle(badgeBrush, badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height);
                DrawText(g, badgeRect, data.DiscountText!, "Arial", FontStyle.Bold, Color.White);
            }
            else
            {
                using var badgeBrush = new SolidBrush(Color.White);
                g.FillRectangle(badgeBrush, badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height);
                DrawText(g, badgeRect, data.DiscountText!, "Arial", FontStyle.Bold, AccentColor);
            }
        }

        DrawText(g, Frac(w, h, 0.05, 0.52, 0.9, 0.42), data.PriceText, "Arial", FontStyle.Bold, textColor);
    }

    private static void DrawDetailed(Graphics g, PriceTagData data, double w, double h)
    {
        if (!string.IsNullOrWhiteSpace(data.StoreName))
            DrawText(g, Frac(w, h, 0.05, 0.01, 0.9, 0.10), data.StoreName!, "Arial", FontStyle.Regular, Color.DimGray);

        DrawText(g, Frac(w, h, 0.05, 0.12, 0.9, 0.20), data.ProductName, "Arial", FontStyle.Bold, Color.Black);

        if (!string.IsNullOrWhiteSpace(data.Category))
            DrawText(g, Frac(w, h, 0.05, 0.33, 0.9, 0.12), data.Category!, "Arial", FontStyle.Italic, Color.DimGray);

        DrawBarcode(g, Frac(w, h, 0.05, 0.46, 0.9, 0.32), data.Barcode);

        if (!string.IsNullOrWhiteSpace(data.Unit))
            DrawText(g, Frac(w, h, 0.05, 0.80, 0.35, 0.18), data.Unit!, "Arial", FontStyle.Regular, Color.DimGray);

        DrawText(g, Frac(w, h, 0.42, 0.78, 0.53, 0.20), data.PriceText, "Arial", FontStyle.Bold, Color.Black);
    }

    private static void DrawWithQr(Graphics g, PriceTagData data, double w, double h)
    {
        DrawQr(g, Frac(w, h, 0.05, 0.05, 0.48, 0.62), data.Barcode);
        DrawText(g, Frac(w, h, 0.58, 0.08, 0.37, 0.50), data.ProductName, "Arial", FontStyle.Regular, Color.Black);
        DrawText(g, Frac(w, h, 0.05, 0.70, 0.9, 0.26), data.PriceText, "Arial", FontStyle.Bold, Color.Black);
    }

    private static RectangleF Frac(double widthMm, double heightMm, double x, double y, double wFrac, double hFrac)
    {
        var wPx = BarcodeLabelService.MmToPx(widthMm);
        var hPx = BarcodeLabelService.MmToPx(heightMm);
        return new RectangleF((float)(x * wPx), (float)(y * hPx), (float)(wFrac * wPx), (float)(hFrac * hPx));
    }

    private static void DrawText(Graphics g, RectangleF rect, string text, string family, FontStyle style, Color color)
    {
        if (rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(text))
            return;

        var maxFontSize = Math.Clamp(rect.Height * 0.72f, 6f, 48f);
        using var font = BarcodeLabelService.FitFontToWidth(g, text, style, maxFontSize, rect.Width);
        var displayText = text;
        var size = g.MeasureString(displayText, font);
        if (size.Width > rect.Width)
        {
            displayText = BarcodeLabelService.TruncateToWidth(g, displayText, font, rect.Width);
            size = g.MeasureString(displayText, font);
        }
        using var brush = new SolidBrush(color);
        g.DrawString(displayText, font, brush,
            rect.X + Math.Max(0, (rect.Width - size.Width) / 2f),
            rect.Y + Math.Max(0, (rect.Height - size.Height) / 2f));
    }

    private static void DrawBarcode(Graphics g, RectangleF rect, string? code)
    {
        if (rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(code))
            return;

        var codeTextHeight = Math.Min(rect.Height * 0.2f, 14f);
        var barcodeHeight = Math.Max(rect.Height - codeTextHeight - 2, 8f);

        using var barcodeBitmap = BarcodeLabelService.RenderBarcode(code!, (int)rect.Width, (int)barcodeHeight);
        var destWidth = Math.Min(barcodeBitmap.Width, rect.Width);
        var barcodeX = rect.X + (rect.Width - destWidth) / 2f;
        g.DrawImage(barcodeBitmap, barcodeX, rect.Y, destWidth, barcodeHeight);

        using var codeFont = new Font("Consolas", Math.Clamp(codeTextHeight * 0.7f, 6f, 12f), FontStyle.Regular);
        var codeSize = g.MeasureString(code, codeFont);
        var codeText = codeSize.Width <= rect.Width ? code! : BarcodeLabelService.TruncateToWidth(g, code!, codeFont, rect.Width);
        codeSize = g.MeasureString(codeText, codeFont);
        g.DrawString(codeText, codeFont, Brushes.Black,
            rect.X + Math.Max(0, (rect.Width - codeSize.Width) / 2f), rect.Y + barcodeHeight + 1);
    }

    private static void DrawQr(Graphics g, RectangleF rect, string? code)
    {
        var payload = string.IsNullOrWhiteSpace(code) ? null : code;
        if (rect.Width < 4 || rect.Height < 4 || payload is null)
            return;

        var sizePx = (int)Math.Min(rect.Width, rect.Height);
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = sizePx, Height = sizePx, Margin = 1 },
        };
        using var qrBitmap = BarcodeLabelService.ToBitmap(writer.Write(payload));
        var x = rect.X + (rect.Width - sizePx) / 2f;
        var y = rect.Y + (rect.Height - sizePx) / 2f;
        g.DrawImage(qrBitmap, x, y, sizePx, sizePx);
    }

    /// <summary>Список принтеров — тот же полный список (спулер + WinUSB + raw-USB + LPT + COM),
    /// что и у этикеток; для печати на A4 имеет смысл выбирать только спулерные принтеры,
    /// но сузить список тут не пытаемся — обычный офисный принтер почти всегда в спулере.</summary>
    public static IReadOnlyList<DiscoveredPrinter> GetAvailablePrinters() => PrinterDiscoveryService.Discover();

    public static LabelPrintResult Print(PriceTagPrintRequest request) => request.Target switch
    {
        PriceTagPrintTarget.Thermal => PrintThermal(request),
        PriceTagPrintTarget.A4 => PrintA4(request),
        _ => LabelPrintResult.Failed,
    };

    private static LabelPrintResult PrintThermal(PriceTagPrintRequest request)
    {
        if (BarcodeLabelService.IsRawDevicePath(request.PrinterName))
        {
            try
            {
                using var tag = GenerateBitmap(request.Kind, request.Data, request.WidthMm, request.HeightMm);
                var raster = BarcodeLabelService.BitmapToEscPosRaster(tag);
                var copies = Math.Clamp(request.Copies, 1, 99);
                for (var i = 0; i < copies; i++)
                    PrinterPortService.SendRawBytes(request.PrinterName, raster);
                return LabelPrintResult.Success;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Price tag raw device print failed: {ex}", "ERROR");
                return LabelPrintResult.Failed;
            }
        }

        try
        {
            if (!RawPrinterHelper.GetInstalledPrinterNames()
                    .Any(p => string.Equals(p, request.PrinterName, StringComparison.OrdinalIgnoreCase)))
            {
                PosLogger.Log($"Price tag print: printer not found '{request.PrinterName}'", "WARNING");
                return LabelPrintResult.PrinterNotFound;
            }

            using var tag = GenerateBitmap(request.Kind, request.Data, request.WidthMm, request.HeightMm);
            var copiesRemaining = Math.Clamp(request.Copies, 1, 99);

            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = request.PrinterName;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.PrintPage += (_, e) =>
            {
                e.Graphics!.DrawImage(tag, 0, 0, tag.Width, tag.Height);
                copiesRemaining--;
                e.HasMorePages = copiesRemaining > 0;
            };
            doc.Print();
            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
    }

    /// <summary>Печать сеткой ценников на обычном листе A4 через спулер Windows — сводится к
    /// пакетной печати одного элемента (см. PrintBatchA4, которую использует и массовая печать
    /// для склада — Warehouse → "🏷 Массовая печать ценников").</summary>
    private static LabelPrintResult PrintA4(PriceTagPrintRequest request) =>
        PrintBatchA4(new[] { (request.Kind, request.Data, request.Copies) }, request.WidthMm, request.HeightMm, request.PrinterName);

    /// <summary>Печать ценников для НЕСКОЛЬКИХ разных товаров за один проход — по одному шаблону
    /// и размеру на всех, но с собственными данными (название/цена/штрих-код) у каждого. На
    /// термопринтере — просто последовательность отдельных этикеток; на A4 — общая сетка на
    /// листах, чтобы не тратить страницу на каждый товар отдельно.</summary>
    public static LabelPrintResult PrintBatch(
        IReadOnlyList<(PriceTagKind Kind, PriceTagData Data, int Copies)> items,
        double widthMm, double heightMm, string printerName, PriceTagPrintTarget target)
    {
        if (items.Count == 0)
            return LabelPrintResult.Failed;

        if (target == PriceTagPrintTarget.A4)
            return PrintBatchA4(items, widthMm, heightMm, printerName);

        foreach (var item in items)
        {
            var result = Print(new PriceTagPrintRequest(item.Kind, item.Data, widthMm, heightMm, item.Copies, printerName, target));
            if (result != LabelPrintResult.Success)
                return result; // печать уже частично прошла — сообщаем причину остановки вызывающему коду
        }
        return LabelPrintResult.Success;
    }

    private static LabelPrintResult PrintBatchA4(
        IReadOnlyList<(PriceTagKind Kind, PriceTagData Data, int Copies)> items,
        double widthMm, double heightMm, string printerName)
    {
        if (!RawPrinterHelper.GetInstalledPrinterNames()
                .Any(p => string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase)))
        {
            PosLogger.Log($"Price tag batch A4 print: printer not found '{printerName}'", "WARNING");
            return LabelPrintResult.PrinterNotFound;
        }

        // Материализуем КАЖДУЮ печатаемую копию как отдельный битмап — тиражи и данные у разных
        // товаров различаются, поэтому нельзя просто повторить один и тот же кадр N раз, как в
        // одиночной печати.
        var tags = new List<Bitmap>();
        try
        {
            foreach (var item in items)
            {
                var copies = Math.Clamp(item.Copies, 1, 99);
                for (var i = 0; i < copies; i++)
                    tags.Add(GenerateBitmap(item.Kind, item.Data, widthMm, heightMm));
            }

            if (tags.Count == 0)
                return LabelPrintResult.Failed;

            const double marginMm = 8;
            const double gapMm = 3;
            const double pageWidthMm = 210;
            const double pageHeightMm = 297;

            var usableWidthMm = pageWidthMm - 2 * marginMm;
            var usableHeightMm = pageHeightMm - 2 * marginMm;
            var columns = Math.Max(1, (int)((usableWidthMm + gapMm) / (widthMm + gapMm)));
            var rows = Math.Max(1, (int)((usableHeightMm + gapMm) / (heightMm + gapMm)));
            var perPage = columns * rows;

            var index = 0;
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            doc.DefaultPageSettings.PaperSize = new PaperSize("A4", (int)(pageWidthMm / 25.4 * 100), (int)(pageHeightMm / 25.4 * 100));
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            doc.PrintPage += (_, e) =>
            {
                var g = e.Graphics!;
                var dpiX = g.DpiX;
                var dpiY = g.DpiY;
                var tagWPx = (float)(widthMm / 25.4 * dpiX);
                var tagHPx = (float)(heightMm / 25.4 * dpiY);
                var marginXPx = (float)(marginMm / 25.4 * dpiX);
                var marginYPx = (float)(marginMm / 25.4 * dpiY);
                var gapXPx = (float)(gapMm / 25.4 * dpiX);
                var gapYPx = (float)(gapMm / 25.4 * dpiY);

                var onThisPage = Math.Min(tags.Count - index, perPage);
                for (var i = 0; i < onThisPage; i++)
                {
                    var col = i % columns;
                    var row = i / columns;
                    var x = marginXPx + col * (tagWPx + gapXPx);
                    var y = marginYPx + row * (tagHPx + gapYPx);
                    g.DrawImage(tags[index + i], x, y, tagWPx, tagHPx);
                }

                index += onThisPage;
                e.HasMorePages = index < tags.Count;
            };
            doc.Print();
            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag batch A4 print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
        finally
        {
            foreach (var tag in tags)
                tag.Dispose();
        }
    }

    /// <summary>Печать пачки ценников по ОДНОМУ пользовательскому шаблону этикетки (LabelTemplate)
    /// вместо готового PriceTagKind — используется, когда в диалоге печати ценника выбран пункт
    /// "Пользовательский шаблон" (2026-09-06). Category/OldPriceText/DiscountText из PriceTagData
    /// просто не используются — как и остальные поля вне полей самого LabelTemplate.</summary>
    public static LabelPrintResult PrintBatchCustomTemplate(
        IReadOnlyList<(PriceTagData Data, int Copies)> items,
        LabelTemplate template, string printerName, PriceTagPrintTarget target)
    {
        if (items.Count == 0)
            return LabelPrintResult.Failed;

        if (target == PriceTagPrintTarget.A4)
            return PrintBatchA4CustomTemplate(items, template, printerName);

        foreach (var item in items)
        {
            var request = new LabelPrintRequest(
                item.Data.ProductName, item.Data.Barcode ?? "", item.Data.PriceText, item.Copies,
                printerName, template, item.Data.Sku, item.Data.Unit, item.Data.StoreName);
            var result = BarcodeLabelService.Print(request);
            if (result != LabelPrintResult.Success)
                return result; // печать уже частично прошла — сообщаем причину остановки вызывающему коду
        }
        return LabelPrintResult.Success;
    }

    /// <summary>Копия <see cref="PrintBatchA4"/> — та же сетка/поля/зазоры/спулерная печать,
    /// только источник битмапа — BarcodeLabelService.GenerateLabelBitmap по шаблону вместо
    /// GenerateBitmap по PriceTagKind. Осознанное дублирование вместо обобщения существующего
    /// стабильного кинд-пути через делегат/enum.</summary>
    private static LabelPrintResult PrintBatchA4CustomTemplate(
        IReadOnlyList<(PriceTagData Data, int Copies)> items, LabelTemplate template, string printerName)
    {
        if (!RawPrinterHelper.GetInstalledPrinterNames()
                .Any(p => string.Equals(p, printerName, StringComparison.OrdinalIgnoreCase)))
        {
            PosLogger.Log($"Price tag batch A4 (custom template) print: printer not found '{printerName}'", "WARNING");
            return LabelPrintResult.PrinterNotFound;
        }

        var widthMm = template.WidthMm;
        var heightMm = template.HeightMm;

        var tags = new List<Bitmap>();
        try
        {
            foreach (var item in items)
            {
                var copies = Math.Clamp(item.Copies, 1, 99);
                for (var i = 0; i < copies; i++)
                    tags.Add(BarcodeLabelService.GenerateLabelBitmap(
                        item.Data.ProductName, item.Data.Barcode ?? "", item.Data.PriceText, template,
                        item.Data.Sku, item.Data.Unit, item.Data.StoreName));
            }

            if (tags.Count == 0)
                return LabelPrintResult.Failed;

            const double marginMm = 8;
            const double gapMm = 3;
            const double pageWidthMm = 210;
            const double pageHeightMm = 297;

            var usableWidthMm = pageWidthMm - 2 * marginMm;
            var usableHeightMm = pageHeightMm - 2 * marginMm;
            var columns = Math.Max(1, (int)((usableWidthMm + gapMm) / (widthMm + gapMm)));
            var rows = Math.Max(1, (int)((usableHeightMm + gapMm) / (heightMm + gapMm)));
            var perPage = columns * rows;

            var index = 0;
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            doc.DefaultPageSettings.PaperSize = new PaperSize("A4", (int)(pageWidthMm / 25.4 * 100), (int)(pageHeightMm / 25.4 * 100));
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

            doc.PrintPage += (_, e) =>
            {
                var g = e.Graphics!;
                var dpiX = g.DpiX;
                var dpiY = g.DpiY;
                var tagWPx = (float)(widthMm / 25.4 * dpiX);
                var tagHPx = (float)(heightMm / 25.4 * dpiY);
                var marginXPx = (float)(marginMm / 25.4 * dpiX);
                var marginYPx = (float)(marginMm / 25.4 * dpiY);
                var gapXPx = (float)(gapMm / 25.4 * dpiX);
                var gapYPx = (float)(gapMm / 25.4 * dpiY);

                var onThisPage = Math.Min(tags.Count - index, perPage);
                for (var i = 0; i < onThisPage; i++)
                {
                    var col = i % columns;
                    var row = i / columns;
                    var x = marginXPx + col * (tagWPx + gapXPx);
                    var y = marginYPx + row * (tagHPx + gapYPx);
                    g.DrawImage(tags[index + i], x, y, tagWPx, tagHPx);
                }

                index += onThisPage;
                e.HasMorePages = index < tags.Count;
            };
            doc.Print();
            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Price tag batch A4 (custom template) print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
        finally
        {
            foreach (var tag in tags)
                tag.Dispose();
        }
    }
}
