using SkiaSharp;

namespace NurMarketKassa.Services;

// 2026-11-06 (В18, АЛ-3): графический чек на SkiaSharp — работает на Windows, Linux и Android (раньше только Windows: GDI+). Раскладка та же, что в
// MonospaceReceiptRenderer + GraphicReceiptGenerator: моноширинный шрифт настроек (по умолчанию Courier New 10 пт), чёткий текст без сглаживания, «ИТОГО» жирным
// Arial 14 пт, шаг строк и поля из GraphicReceiptLayout/TestReceiptLineBuilder, QR внизу; в принтер — полосы ESC * по 24 точки. На Windows печать по-прежнему
// идёт через GDI+ (GraphicReceiptGenerator); Skia-версия там используется в проверках.

public static class GraphicReceiptSkia
{
    private const int BitImageBandHeightPx = 24;

    public static byte[] GenerateReceiptImage(string receiptText, GraphicReceiptSettings settings) =>
        ConvertToEscPosRaster(GenerateReceiptBitmap(receiptText, settings));

    public static byte[] GenerateTestReceiptImage(GraphicReceiptSettings settings, string storeName) =>
        ConvertToEscPosRaster(RenderLines(TestReceiptLineBuilder.GetTestTextReceiptLines(settings, storeName), settings));

    public static SKBitmap GenerateReceiptBitmap(string receiptText, GraphicReceiptSettings settings)
    {
        var width = ReceiptPaperProfile.GetCharWidth(
            settings.PaperWidthPixels >= ReceiptPaperProfile.GetRasterWidthPixels(ReceiptPaperProfile.Paper80mm)
                ? ReceiptPaperProfile.Paper80mm
                : ReceiptPaperProfile.Paper58mm);
        var formatted = ReceiptTextFormatter.FormatForPrinter(receiptText ?? string.Empty, width, rightMarginChars: 2).TrimEnd();
        var lines = formatted.Split('\n').ToList();
        if (lines.Count == 0)
            lines.Add("Чек");
        return RenderLines(lines, settings);
    }

    internal static SKBitmap RenderLines(IReadOnlyList<string> lines, GraphicReceiptSettings settings)
    {
        var paperWidth = settings.PaperWidthPixels > 0 ? settings.PaperWidthPixels : TestReceiptLineBuilder.PaperWidthPixels;
        var fontFamily = string.IsNullOrWhiteSpace(settings.FontFamily) ? TestReceiptLineBuilder.FontFamily : settings.FontFamily;
        var fontSize = TestReceiptLineBuilder.ResolveFontSize(settings.FontSize);
        var style = new SkText(fontFamily, fontSize * SkiaText.PtToPx, Alias: true);
        var boldTotal = new SkText("Arial", 14f * SkiaText.PtToPx, Bold: true, Alias: true);

        var lineHeightPx = Math.Max(SkiaText.LineHeight(style) + 2f, TestReceiptLineBuilder.LineStepPx);
        var haveQr = settings.ShowQrCode && !string.IsNullOrEmpty(settings.QrCodePath) && File.Exists(settings.QrCodePath);
        var qrBlockHeight = haveQr ? 96 : 0;
        var totalHeight = (int)(GraphicReceiptLayout.ComputeContentHeightPx(lines, lineHeightPx) + qrBlockHeight);

        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(paperWidth, 1), Math.Max(totalHeight, 64), SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        var x = TestReceiptLineBuilder.LeftMarginPx;
        var y = TestReceiptLineBuilder.StartOffsetPx;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                SkiaText.Draw(canvas, line, x, y, GraphicReceiptLayout.IsBoldTotalLine(line) ? boldTotal : style);
            y += GraphicReceiptLayout.GetLineStepPx(line, lineHeightPx);
        }

        if (haveQr)
        {
            const int qrSize = 88;
            using var qr = SKBitmap.Decode(settings.QrCodePath);
            if (qr is not null)
            {
                using var scaled = qr.Resize(new SKImageInfo(qrSize, qrSize), SKFilterQuality.High);
                if (scaled is not null)
                    canvas.DrawBitmap(scaled, (paperWidth - qrSize) / 2, y + 4);
            }
        }
        return bitmap;
    }

    public static byte[] ConvertToEscPosRaster(SKBitmap bitmap) =>
        EncodeEscStarBands(bitmap.Width, bitmap.Height, (x, y) =>
        {
            var p = bitmap.GetPixel(x, y);
            return p.Red + p.Green + p.Blue < 384;
        });

    /// <summary>Растр чека полосами «ESC *» по 24 точки (режим 33), как в GDI-версии: интервал строк равен высоте полосы, в конце подача и отрезка. <paramref name="isBlack"/> — точка чёрная.</summary>
    internal static byte[] EncodeEscStarBands(int width, int height, Func<int, int, bool> isBlack)
    {
        using var ms = new MemoryStream();
        EscPosCommands.WriteInitialize(ms);
        EscPosCommands.WriteLineSpacing(ms, BitImageBandHeightPx);

        for (var bandStart = 0; bandStart < height; bandStart += BitImageBandHeightPx)
        {
            ms.WriteByte(0x1B); // ESC
            ms.WriteByte(0x2A); // *
            ms.WriteByte(33);   // m = 24-точечная двойная плотность (3 байта на столбец)
            ms.WriteByte((byte)(width % 256));
            ms.WriteByte((byte)(width / 256));

            for (var x = 0; x < width; x++)
            {
                for (var chunk = 0; chunk < 3; chunk++)
                {
                    byte col = 0;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        var y = bandStart + chunk * 8 + bit;
                        if (y >= height)
                            continue;
                        if (isBlack(x, y))
                            col |= (byte)(1 << (7 - bit));
                    }
                    ms.WriteByte(col);
                }
            }
            ms.WriteByte(0x0A); // LF — следующая полоса
        }

        EscPosCommands.WriteDefaultLineSpacing(ms);
        EscPosCommands.WriteFeedLines(ms, 2);
        EscPosCommands.WriteFeedAndCut(ms);
        return ms.ToArray();
    }
}
