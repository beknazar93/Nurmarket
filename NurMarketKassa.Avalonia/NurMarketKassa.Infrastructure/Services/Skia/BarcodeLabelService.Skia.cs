using SkiaSharp;
using ZXing.Common;

namespace NurMarketKassa.Services;

// 2026-11-06 (В18): линейный штрих-код на SKCanvas — те же правила, что DrawLinearBlock на GDI+ (целый модуль в точках устройства, поля, потолок «толщины
// штриха», цифры под кодом), но без System.Drawing: для листов A4 на Linux/Android и PDF. Масштаб «мир → устройство» передаётся числом (точек устройства на
// точку 203 dpi) и началом ячейки в устройстве — вместо g.TransformPoints: так модуль остаётся целым и в картинке, и в PDF (там поверхность — пункты, а сетка
// считается в точках принтера 600 dpi).

public static partial class BarcodeLabelService
{
    private static SKRect ToDevice(SKRect r, float scale, float originX, float originY) =>
        new(originX + r.Left * scale, originY + r.Top * scale, originX + r.Right * scale, originY + r.Bottom * scale);

    /// <summary>Линейный штрих-код и цифры под ним. <paramref name="rect"/> и <paramref name="labelRect"/> — в мировых координатах ячейки (точки 203 dpi);
    /// <paramref name="scale"/> — точек устройства на мировую точку; <paramref name="originX"/>/<paramref name="originY"/> — левый верхний угол ячейки в точках
    /// устройства (для целой сетки модулей на всей странице).</summary>
    internal static void DrawLinearBlockSkia(SKCanvas canvas, float scale, float originX, float originY, SKRect rect, SKRect labelRect, string? rawCode,
        LabelBarcodeFormat format, int quietModules, float digitsHeight, float digitsFontSize, int maxModuleDots = 0)
    {
        var plan = PlanBarcode(rawCode, format);
        if (plan.Content.Length == 0)
            return;

        var barsHeight = digitsHeight > 0 ? Math.Max(rect.Height - digitsHeight - 2, 8f) : rect.Height;
        var matrix = EncodeModules(plan);
        if (matrix is not null)
        {
            var dev = ToDevice(new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + barsHeight), scale, originX, originY);
            var devLabel = ToDevice(labelRect, scale, originX, originY);
            var m = FitLinearModule(matrix.Width, quietModules, dev.Width, devLabel.Width);
            var sc = rect.Width > 0 ? dev.Width / rect.Width : 1f;
            if (m > 0 && sc > 1.01f)
            {
                var m203 = FitLinearModule(matrix.Width, quietModules, rect.Width, labelRect.Width);
                var target = (int)Math.Round(m203 * sc);
                if (m203 == 0)
                    m = 0;
                else if (target > m && matrix.Width * target + 2 <= devLabel.Width)
                    m = target;
            }
            if (m > 0 && maxModuleDots > 0)
                m = Math.Max(1, Math.Min(m, (int)Math.Round(maxModuleDots * Math.Max(sc, 1f))));
            if (m > 0)
            {
                var widthPx = matrix.Width * m;
                var x0 = (int)Math.Round(dev.Left + (dev.Width - widthPx) / 2f);
                x0 = Math.Clamp(x0, (int)Math.Ceiling(devLabel.Left) + 1, Math.Max((int)Math.Ceiling(devLabel.Left) + 1, (int)devLabel.Right - 1 - widthPx));
                BlitModulesSkia(canvas, scale, originX, originY, matrix, x0, (int)Math.Round(dev.Top), m, Math.Max((int)Math.Round(dev.Height), 1));
            }
        }

        if (digitsHeight <= 0)
            return;
        var style = new SkText("Consolas", digitsFontSize * SkiaText.PtToPx);
        var code = plan.Content;
        var codeText = SkiaText.Measure(code, style) <= rect.Width ? code : SkiaText.Truncate(code, rect.Width, style);
        var w = SkiaText.Measure(codeText, style);
        SkiaText.Draw(canvas, codeText, rect.Left + Math.Max(0, (rect.Width - w) / 2f), rect.Top + barsHeight + 1, style);
    }

    /// <summary>2026-11-06 (В18): «текст с ручным размером шрифта не помещается в свой блок» без System.Drawing — по размерам Skia (как HasTextOverflow на GDI+).</summary>
    private static bool HasTextOverflowSkia(string productName, string? priceText, LabelTemplate template,
        string? sku, string? unit, string? storeName, string? variantText)
    {
        bool Over(LabelElementLayout box, string? text, bool bold)
        {
            var size = ResolveFontSizePx(box, template);
            var rect = ToPxRect(box);
            if (size <= 0 || !box.Enabled || rect.Width < 4 || rect.Height < 4 || string.IsNullOrWhiteSpace(text))
                return false;
            var style = new SkText(ResolveFontFamily(box, template), (float)Math.Max(size, 1) * SkiaText.PtToPx, bold);
            var lines = SkiaText.Wrap(text, rect.Width, style);
            var height = lines.Count * SkiaText.LineHeight(style);
            var width = lines.Count == 0 ? 0 : lines.Max(l => SkiaText.Measure(l, style));
            return height > rect.Height + 1f || width > rect.Width + 1f;
        }

        var variantElement = template.Variant;
        if (!string.IsNullOrWhiteSpace(variantText) && variantElement is not { Enabled: true })
            productName = productName + " — " + variantText!.Trim();
        var skuText = string.IsNullOrWhiteSpace(template.SkuCustomText) ? sku : template.SkuCustomText;
        return Over(template.ProductName, productName, false)
               || (!string.IsNullOrWhiteSpace(priceText) && Over(template.Price, FormatPriceText(priceText!, template), true))
               || Over(template.Sku, skuText, false)
               || Over(template.Unit, unit, false)
               || Over(template.StoreName, storeName, false)
               || (!string.IsNullOrWhiteSpace(variantText) && variantElement is { Enabled: true } && Over(variantElement, variantText!.Trim(), true));
    }

    /// <summary>Модули пиксель-в-пиксель в точках устройства: каждая серия чёрных модулей — один прямоугольник без сглаживания.</summary>
    private static void BlitModulesSkia(SKCanvas canvas, float scale, float originX, float originY, BitMatrix matrix, int x0, int y0, int m, int heightPx)
    {
        var oneRow = matrix.Height == 1;
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        for (var row = 0; row < matrix.Height; row++)
        {
            var x = 0;
            while (x < matrix.Width)
            {
                if (!matrix[x, row])
                {
                    x++;
                    continue;
                }
                var start = x;
                while (x < matrix.Width && matrix[x, row])
                    x++;
                var left = x0 + start * m;
                var top = oneRow ? y0 : y0 + row * m;
                var h = oneRow ? heightPx : m;
                canvas.DrawRect(new SKRect((left - originX) / scale, (top - originY) / scale,
                    (left + (x - start) * m - originX) / scale, (top + h - originY) / scale), paint);
            }
        }
    }
}
