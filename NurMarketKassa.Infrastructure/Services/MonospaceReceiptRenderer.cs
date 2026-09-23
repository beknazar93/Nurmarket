using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.Versioning;

namespace NurMarketKassa.Services;

[SupportedOSPlatform("windows")]
internal static class MonospaceReceiptRenderer
{
    public static Bitmap RenderLines(IReadOnlyList<string> lines, GraphicReceiptSettings settings)
    {
        var paperWidth = settings.PaperWidthPixels > 0
            ? settings.PaperWidthPixels
            : TestReceiptLineBuilder.PaperWidthPixels;

        var fontFamily = string.IsNullOrWhiteSpace(settings.FontFamily)
            ? TestReceiptLineBuilder.FontFamily
            : settings.FontFamily;
        var fontSize = TestReceiptLineBuilder.ResolveFontSize(settings.FontSize);

        using var measureBitmap = new Bitmap(1, 1);
        using var measureGraphics = Graphics.FromImage(measureBitmap);
        measureGraphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        using var measureFont = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        var lineHeightPx = Math.Max(
            measureFont.GetHeight(measureGraphics) + 2f,
            TestReceiptLineBuilder.LineStepPx);

        var qrBlockHeight = settings.ShowQrCode
                            && !string.IsNullOrEmpty(settings.QrCodePath)
                            && File.Exists(settings.QrCodePath)
            ? 96
            : 0;

        var totalHeight = (int)(GraphicReceiptLayout.ComputeContentHeightPx(lines, lineHeightPx) + qrBlockHeight);

        var bitmap = new Bitmap(paperWidth, Math.Max(totalHeight, 64));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

        using var font = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Point);
        using var boldTotalFont = new Font("Arial", 14f, FontStyle.Bold, GraphicsUnit.Point);

        var x = TestReceiptLineBuilder.LeftMarginPx;
        var y = TestReceiptLineBuilder.StartOffsetPx;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                if (GraphicReceiptLayout.IsBoldTotalLine(line))
                    graphics.DrawString(line, boldTotalFont, Brushes.Black, x, y);
                else
                    graphics.DrawString(line, font, Brushes.Black, x, y);
            }

            y += GraphicReceiptLayout.GetLineStepPx(line, lineHeightPx);
        }

        if (qrBlockHeight > 0)
        {
            using var qrImage = Image.FromFile(settings.QrCodePath);
            const int qrSize = 88;
            using var scaledQr = new Bitmap(qrImage, qrSize, qrSize);
            graphics.DrawImage(scaledQr, (paperWidth - qrSize) / 2, y + 4, qrSize, qrSize);
        }

        return bitmap;
    }
}
