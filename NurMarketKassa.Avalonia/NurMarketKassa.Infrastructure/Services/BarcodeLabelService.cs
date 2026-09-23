using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;
using ZXing.Rendering;

namespace NurMarketKassa.Services;

public enum LabelPrintResult { Success, PrinterNotFound, Failed }

/// <summary>Параметры печати одной этикетки со штрих-кодом по заданному шаблону раскладки.</summary>
public sealed record LabelPrintRequest(
    string ProductName,
    string Barcode,
    string? PriceText,
    int Copies,
    string PrinterName,
    LabelTemplate Template,
    string? Sku = null,
    string? Unit = null,
    string? StoreName = null);

/// <summary>
/// Генерирует изображение этикетки (штрих-код + название товара + цена) по шаблону
/// <see cref="LabelTemplate"/> и печатает его на выбранном Windows-принтере.
/// </summary>
public static class BarcodeLabelService
{
    internal const int Dpi = 203; // стандартное разрешение термопринтеров этикеток.

    public static Bitmap GenerateLabelBitmap(
        string productName, string barcode, string? priceText, LabelTemplate template,
        string? sku = null, string? unit = null, string? storeName = null)
    {
        var widthPx = Math.Max((int)MmToPx(template.WidthMm), 10);
        var heightPx = Math.Max((int)MmToPx(template.HeightMm), 10);

        var label = new Bitmap(widthPx, heightPx);
        using var g = Graphics.FromImage(label);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        DrawBarcodeElement(g, template, barcode);
        DrawTextElement(g, template.ProductName, productName, FontStyle.Regular, ResolveFontFamily(template.ProductName, template), ResolveFontSizePx(template.ProductName, template));
        if (!string.IsNullOrWhiteSpace(priceText))
            DrawTextElement(g, template.Price, FormatPriceText(priceText!, template), FontStyle.Bold, ResolveFontFamily(template.Price, template), ResolveFontSizePx(template.Price, template));
        var skuText = string.IsNullOrWhiteSpace(template.SkuCustomText) ? sku : template.SkuCustomText;
        if (!string.IsNullOrWhiteSpace(skuText))
            DrawTextElement(g, template.Sku, skuText!, FontStyle.Regular, ResolveFontFamily(template.Sku, template), ResolveFontSizePx(template.Sku, template));
        if (!string.IsNullOrWhiteSpace(unit))
            DrawTextElement(g, template.Unit, unit!, FontStyle.Regular, ResolveFontFamily(template.Unit, template), ResolveFontSizePx(template.Unit, template));
        if (!string.IsNullOrWhiteSpace(storeName))
            DrawTextElement(g, template.StoreName, storeName!, FontStyle.Regular, ResolveFontFamily(template.StoreName, template), ResolveFontSizePx(template.StoreName, template));

        return label;
    }

    private static string ResolveFontFamily(LabelElementLayout element, LabelTemplate template) =>
        string.IsNullOrWhiteSpace(element.FontFamily)
            ? (string.IsNullOrWhiteSpace(template.FontFamily) ? "Arial" : template.FontFamily)
            : element.FontFamily;

    private static double ResolveFontSizePx(LabelElementLayout element, LabelTemplate template) =>
        element.FontSizePx ?? template.FontSizePx;

    /// <summary>Вычленяет ведущую числовую часть входящей строки цены (уже отформатированной
    /// вызывающим кодом, напр. "285 сом"), опционально обрезает копейки/тыйын и подставляет
    /// валюту, выбранную в шаблоне этикетки. Если строка не начинается с числа (неожиданный
    /// формат) — возвращается как есть, без исключений.</summary>
    internal static string FormatPriceText(string priceText, LabelTemplate template)
    {
        var match = Regex.Match(priceText, @"^[\d\s.,]+");
        if (!match.Success)
            return priceText;

        var numeric = match.Value.Trim();
        if (template.PriceHideDecimals)
            numeric = Regex.Replace(numeric, @"[.,]0+$", "");

        var currency = string.IsNullOrWhiteSpace(template.PriceCurrencyText) ? "" : " " + template.PriceCurrencyText;
        return numeric + currency;
    }

    private static void DrawBarcodeElement(Graphics g, LabelTemplate template, string code)
    {
        var box = template.Barcode;
        var rect = ToPxRect(box);
        if (!box.Enabled || rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(code))
            return;

        var isQr = template.BarcodeFormat == LabelBarcodeFormat.QrCode;
        var showDigits = template.BarcodeShowDigits && !isQr;

        if (isQr)
        {
            var qrSize = Math.Min(rect.Width, rect.Height);
            using var qrBitmap = RenderBarcode(code, (int)qrSize, (int)qrSize, template.BarcodeFormat, template.BarcodeMargin);
            var qrX = rect.X + (rect.Width - qrSize) / 2f;
            var qrY = rect.Y + (rect.Height - qrSize) / 2f;
            g.DrawImage(qrBitmap, qrX, qrY, qrSize, qrSize);
            return;
        }

        var codeTextHeight = showDigits ? Math.Min(rect.Height * 0.22f, 16f) : 0f;
        var barcodeHeight = showDigits ? Math.Max(rect.Height - codeTextHeight - 2, 8f) : rect.Height;

        using var barcodeBitmap = RenderBarcode(code, (int)rect.Width, (int)barcodeHeight, template.BarcodeFormat, template.BarcodeMargin);
        var destWidth = Math.Min(barcodeBitmap.Width, rect.Width);
        var barcodeX = rect.X + (rect.Width - destWidth) / 2f;
        g.DrawImage(barcodeBitmap, barcodeX, rect.Y, destWidth, barcodeHeight);

        if (!showDigits)
            return;

        using var codeFont = new Font("Consolas", Math.Clamp(codeTextHeight * 0.72f, 6f, 14f), FontStyle.Regular);
        var codeSize = g.MeasureString(code, codeFont);
        var codeText = codeSize.Width <= rect.Width ? code : TruncateToWidth(g, code, codeFont, rect.Width);
        codeSize = g.MeasureString(codeText, codeFont);
        g.DrawString(codeText, codeFont, Brushes.Black,
            rect.X + Math.Max(0, (rect.Width - codeSize.Width) / 2f), rect.Y + barcodeHeight + 1);
    }

    private static void DrawTextElement(Graphics g, LabelElementLayout box, string text, FontStyle style, string fontFamily, double manualFontSizePx = 0)
    {
        var rect = ToPxRect(box);
        if (!box.Enabled || rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(text))
            return;

        // manualFontSizePx > 0 — пользователь задал точный размер в редакторе этикетки вместо
        // старого автоподбора по высоте блока; FitFontToWidth ниже всё равно подрежет его, если
        // текст не помещается по ширине — переполнения/обрезки текста быть не должно.
        var maxFontSize = manualFontSizePx > 0
            ? Math.Clamp((float)manualFontSizePx, 6f, 96f)
            : Math.Clamp(rect.Height * 0.72f, 6f, 40f);
        using var font = FitFontToWidth(g, text, style, maxFontSize, rect.Width, fontFamily);
        var displayText = text;
        var size = g.MeasureString(displayText, font);
        if (size.Width > rect.Width)
        {
            displayText = TruncateToWidth(g, displayText, font, rect.Width);
            size = g.MeasureString(displayText, font);
        }
        g.DrawString(displayText, font, Brushes.Black,
            rect.X + Math.Max(0, (rect.Width - size.Width) / 2f),
            rect.Y + Math.Max(0, (rect.Height - size.Height) / 2f));
    }

    /// <summary>
    /// Уменьшает кегль шрифта (от <paramref name="startSize"/> до минимума 6pt), пока строка
    /// не впишется по ширине бокса — иначе широкая, но невысокая цена/название всегда
    /// обрезались бы многоточием даже когда достаточно уменьшить шрифт.
    /// </summary>
    internal static Font FitFontToWidth(Graphics g, string text, FontStyle style, float startSize, float maxWidthPx, string fontFamily = "Arial")
    {
        const float minFontSize = 6f;
        for (var size = startSize; size > minFontSize; size -= 1f)
        {
            var font = new Font(fontFamily, size, style);
            if (g.MeasureString(text, font).Width <= maxWidthPx)
                return font;
            font.Dispose();
        }
        return new Font(fontFamily, minFontSize, style);
    }

    private static RectangleF ToPxRect(LabelElementLayout box) => new(
        MmToPx(box.XMm), MmToPx(box.YMm), MmToPx(box.WidthMm), MmToPx(box.HeightMm));

    internal static string TruncateToWidth(Graphics g, string text, Font font, float maxWidthPx)
    {
        if (g.MeasureString(text, font).Width <= maxWidthPx)
            return text;

        var truncated = text;
        while (truncated.Length > 1 && g.MeasureString(truncated + "…", font).Width > maxWidthPx)
            truncated = truncated[..^1];
        return truncated + "…";
    }

    internal static Bitmap RenderBarcode(string code, int widthPx, int heightPx) =>
        RenderBarcode(code, widthPx, heightPx, LabelBarcodeFormat.Auto, margin: 2);

    internal static Bitmap RenderBarcode(string code, int widthPx, int heightPx, LabelBarcodeFormat format, int margin)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = ResolveFormat(code, format),
            Options = new EncodingOptions
            {
                Width = Math.Max(widthPx, 50),
                Height = Math.Max(heightPx, 30),
                Margin = margin,
                PureBarcode = true,
            }
        };
        var pixelData = writer.Write(code);
        return ToBitmap(pixelData);
    }

    private static BarcodeFormat ResolveFormat(string code, LabelBarcodeFormat format) => format switch
    {
        LabelBarcodeFormat.Ean13 => BarcodeFormat.EAN_13,
        LabelBarcodeFormat.Code128 => BarcodeFormat.CODE_128,
        LabelBarcodeFormat.QrCode => BarcodeFormat.QR_CODE,
        _ => DetectFormat(code),
    };

    private static BarcodeFormat DetectFormat(string code)
    {
        var digitsOnly = code.Length > 0 && code.All(char.IsDigit);
        return (digitsOnly, code.Length) switch
        {
            (true, 13) => BarcodeFormat.EAN_13,
            (true, 8) => BarcodeFormat.EAN_8,
            (true, 12) => BarcodeFormat.UPC_A,
            _ => BarcodeFormat.CODE_128,
        };
    }

    internal static float MmToPx(double mm) => (float)(mm / 25.4 * Dpi);

    internal static Bitmap ToBitmap(PixelData pixelData)
    {
        var bitmap = new Bitmap(pixelData.Width, pixelData.Height, PixelFormat.Format32bppRgb);
        var bitmapData = bitmap.LockBits(
            new Rectangle(0, 0, pixelData.Width, pixelData.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
        return bitmap;
    }

    /// <summary>
    /// Все варианты подключения принтера этикеток: спулер Windows, WinUSB-устройства
    /// (Zadig), "сырые" USB-порты без драйвера, LPT и COM — тот же полный список, что и
    /// у чекового принтера (<see cref="PrinterDiscoveryService"/>), вместо прежнего
    /// урезанного набора «только спулер + driverless USB».
    /// </summary>
    public static IReadOnlyList<DiscoveredPrinter> GetAvailablePrinters() =>
        PrinterDiscoveryService.Discover();

    /// <summary>Путь устройства не является именем очереди спулера Windows — печать
    /// должна идти в обход GDI, напрямую через PrinterPortService (WinUSB/LPT/COM/raw-USB).</summary>
    public static bool IsRawDevicePath(string printerName) =>
        UsbRawPrinterPort.IsRawPortLabel(printerName) ||
        WinUsbPrinterPort.IsWinUsbDevicePath(printerName) ||
        HardwarePortHelper.LooksLikeComPort(printerName) ||
        HardwarePortHelper.LooksLikeLptPort(printerName) ||
        printerName.StartsWith(@"\\.\", StringComparison.Ordinal);

    public static LabelPrintResult Print(LabelPrintRequest request)
    {
        if (IsRawDevicePath(request.PrinterName))
            return PrintToRawDevice(request);

        try
        {
            if (!RawPrinterHelper.GetInstalledPrinterNames()
                    .Any(p => string.Equals(p, request.PrinterName, StringComparison.OrdinalIgnoreCase)))
            {
                PosLogger.Log($"Label print: printer not found '{request.PrinterName}'", "WARNING");
                return LabelPrintResult.PrinterNotFound;
            }

            using var label = GenerateLabelBitmap(
                request.ProductName, request.Barcode, request.PriceText, request.Template,
                request.Sku, request.Unit, request.StoreName);
            var copiesRemaining = Math.Clamp(request.Copies, 1, 99);

            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = request.PrinterName;
            doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
            doc.PrintPage += (_, e) =>
            {
                e.Graphics!.DrawImage(label, 0, 0, label.Width, label.Height);
                copiesRemaining--;
                e.HasMorePages = copiesRemaining > 0;
            };
            doc.Print();
            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
    }

    /// <summary>
    /// No Windows print queue means no GDI print pipeline — the label bitmap is rasterized
    /// into an ESC/POS "GS v 0" bit-image command instead and written straight to the device
    /// via PrinterPortService, which already knows how to route a WinUSB address, a raw
    /// \\.\USBxxx port, LPT or COM (same retrying raw-device write receipts use).
    /// </summary>
    private static LabelPrintResult PrintToRawDevice(LabelPrintRequest request)
    {
        try
        {
            using var label = GenerateLabelBitmap(
                request.ProductName, request.Barcode, request.PriceText, request.Template,
                request.Sku, request.Unit, request.StoreName);
            var raster = BitmapToEscPosRaster(label);
            var devicePath = UsbRawPrinterPort.IsRawPortLabel(request.PrinterName)
                ? UsbRawPrinterPort.ToDevicePath(request.PrinterName)
                : request.PrinterName;
            var copies = Math.Clamp(request.Copies, 1, 99);

            for (var i = 0; i < copies; i++)
                PrinterPortService.SendRawBytes(devicePath, raster);

            return LabelPrintResult.Success;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Raw device label print failed: {ex}", "ERROR");
            return LabelPrintResult.Failed;
        }
    }

    /// <summary>Encodes a monochrome ESC/POS "GS v 0" raster bit-image command (m=0, no scaling).</summary>
    internal static byte[] BitmapToEscPosRaster(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bytesPerRow = (width + 7) / 8;
        var imageData = new byte[bytesPerRow * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var luminance = pixel.R * 0.299 + pixel.G * 0.587 + pixel.B * 0.114;
                if (luminance >= 128)
                    continue;

                var byteIndex = y * bytesPerRow + x / 8;
                var bitIndex = 7 - x % 8;
                imageData[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        using var ms = new MemoryStream();
        ms.WriteByte(0x1D); // GS
        ms.WriteByte(0x76); // v
        ms.WriteByte(0x30); // 0
        ms.WriteByte(0x00); // m = 0 (normal size)
        ms.WriteByte((byte)(bytesPerRow & 0xFF));
        ms.WriteByte((byte)((bytesPerRow >> 8) & 0xFF));
        ms.WriteByte((byte)(height & 0xFF));
        ms.WriteByte((byte)((height >> 8) & 0xFF));
        ms.Write(imageData, 0, imageData.Length);
        return ms.ToArray();
    }
}
