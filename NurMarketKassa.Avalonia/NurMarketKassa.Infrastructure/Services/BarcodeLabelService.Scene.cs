namespace NurMarketKassa.Services;

// 2026-10-29: Android/Linux — System.Drawing там не работает (PlatformNotSupportedException), поэтому предпросмотр штрихкода,
// этикетки и ценника был пустым. Здесь раскладка этикетки собирается как «сцена» из простых фигур (без System.Drawing),
// а рисует её Avalonia (Views.axaml/Dialogs/LabelSceneRenderer.cs). Правила раскладки те же, что в GenerateLabelBitmap
// (203 dpi, целый модуль штрих-кода, цифры под кодом, автоподбор шрифта); печать по-прежнему идёт старым путём.

/// <summary>Элемент сцены этикетки. Координаты — точки 203 dpi от левого верхнего угла.</summary>
public abstract record LabelSceneItem;

/// <summary>Залитый прямоугольник (фон акционного ценника, плашка скидки). Цвет — ARGB.</summary>
public sealed record LabelSceneRect(float X, float Y, float W, float H, uint Argb) : LabelSceneItem;

/// <summary>Текст в рамке. <paramref name="SizePt"/> &gt; 0 — ручной размер (пункты, как у System.Drawing.Font): перенос по ширине,
/// обрезка по рамке; 0 — автоподбор от <paramref name="MaxAutoPt"/> вниз до 6 пт в одну строку по центру.</summary>
public sealed record LabelSceneText(float X, float Y, float W, float H, string Text, string Family, bool Bold, bool Italic, bool Strike,
    uint Argb, float SizePt, float MaxAutoPt) : LabelSceneItem;

/// <summary>Штрихи или модули QR: <paramref name="Cells"/>[x, y]; у линейного кода одна строка, растянутая на <paramref name="HeightPx"/>.</summary>
public sealed record LabelSceneBars(int X, int Y, int Module, int HeightPx, bool[,] Cells) : LabelSceneItem;

/// <summary>Готовая сцена этикетки/ценника в точках 203 dpi.</summary>
public sealed record LabelScene(int WidthPx, int HeightPx, IReadOnlyList<LabelSceneItem> Items);

public static partial class BarcodeLabelService
{
    private const uint Black = 0xFF000000;

    /// <summary>Сцена этикетки по шаблону (аналог <c>GenerateLabelBitmap</c> без System.Drawing).</summary>
    public static LabelScene BuildLabelScene(
        string productName, string barcode, string? priceText, LabelTemplate template,
        string? sku = null, string? unit = null, string? storeName = null, string? variantText = null)
    {
        var variantElement = template.Variant;
        if (!string.IsNullOrWhiteSpace(variantText) && variantElement is not { Enabled: true })
            productName = productName + " — " + variantText!.Trim();

        var widthPx = Math.Max((int)(template.WidthMm / 25.4 * Dpi), 10);
        var heightPx = Math.Max((int)(template.HeightMm / 25.4 * Dpi), 10);
        var items = new List<LabelSceneItem>();

        // Штрих-код
        var box = template.Barcode;
        var rect = (X: MmToPx(box.XMm), Y: MmToPx(box.YMm), W: MmToPx(box.WidthMm), H: MmToPx(box.HeightMm));
        if (box.Enabled && rect.W >= 4 && rect.H >= 4 && !string.IsNullOrWhiteSpace(barcode))
        {
            if (template.BarcodeFormat == LabelBarcodeFormat.QrCode)
                AddQrScene(items, rect.X, rect.Y, rect.W, rect.H, barcode, template.BarcodeMargin);
            else
            {
                var digitsHeight = template.BarcodeShowDigits ? Math.Min(rect.H * 0.22f, 16f) : 0f;
                AddLinearScene(items, rect.X, rect.Y, rect.W, rect.H, MmToPx(template.WidthMm), barcode, template.BarcodeFormat,
                    template.BarcodeMargin, digitsHeight, Math.Clamp(digitsHeight * 0.72f, 6f, 14f), template.BarcodeModuleDots);
            }
        }

        AddTextScene(items, template.ProductName, productName, false, template);
        if (!string.IsNullOrWhiteSpace(priceText))
            AddTextScene(items, template.Price, FormatPriceText(priceText!, template), true, template);
        var skuText = string.IsNullOrWhiteSpace(template.SkuCustomText) ? sku : template.SkuCustomText;
        if (!string.IsNullOrWhiteSpace(skuText))
            AddTextScene(items, template.Sku, skuText!, false, template);
        if (!string.IsNullOrWhiteSpace(unit))
            AddTextScene(items, template.Unit, unit!, false, template);
        if (!string.IsNullOrWhiteSpace(storeName))
            AddTextScene(items, template.StoreName, storeName!, false, template);
        if (!string.IsNullOrWhiteSpace(variantText) && variantElement is { Enabled: true })
            AddTextScene(items, variantElement, variantText!.Trim(), true, template);
        return new LabelScene(widthPx, heightPx, items);
    }

    private static void AddTextScene(List<LabelSceneItem> items, LabelElementLayout box, string text, bool bold, LabelTemplate template)
    {
        var rect = (X: MmToPx(box.XMm), Y: MmToPx(box.YMm), W: MmToPx(box.WidthMm), H: MmToPx(box.HeightMm));
        if (!box.Enabled || rect.W < 4 || rect.H < 4 || string.IsNullOrWhiteSpace(text))
            return;
        var size = ResolveFontSizePx(box, template);
        items.Add(new LabelSceneText(rect.X, rect.Y, rect.W, rect.H, text, ResolveFontFamily(box, template), bold, false, false, Black,
            size > 0 ? Math.Max((float)size, 1f) : 0f, Math.Clamp(rect.H * 0.72f, 6f, 40f)));
    }

    /// <summary>Линейный штрих-код + цифры (порт DrawLinearBlock при масштабе 1: целый модуль, центр блока).</summary>
    internal static void AddLinearScene(List<LabelSceneItem> items, float rx, float ry, float rw, float rh, float labelW, string? rawCode,
        LabelBarcodeFormat format, int quietModules, float digitsHeight, float digitsFontSize, int maxModuleDots = 0)
    {
        var plan = PlanBarcode(rawCode, format);
        if (plan.Content.Length == 0)
            return;
        var barsHeight = digitsHeight > 0 ? Math.Max(rh - digitsHeight - 2, 8f) : rh;
        var matrix = EncodeModules(plan);
        if (matrix is not null)
        {
            var m = FitLinearModule(matrix.Width, quietModules, rw, labelW);
            if (m > 0 && maxModuleDots > 0)
                m = Math.Max(1, Math.Min(m, maxModuleDots));
            if (m > 0)
            {
                var widthPx = matrix.Width * m;
                var x0 = (int)Math.Round(rx + (rw - widthPx) / 2f);
                x0 = Math.Clamp(x0, 1, Math.Max(1, (int)labelW - 1 - widthPx));
                var cells = new bool[matrix.Width, 1];
                for (var x = 0; x < matrix.Width; x++)
                    cells[x, 0] = matrix[x, 0];
                items.Add(new LabelSceneBars(x0, (int)Math.Round(ry), m, Math.Max((int)Math.Round(barsHeight), 1), cells));
            }
        }
        if (digitsHeight > 0)
            items.Add(new LabelSceneText(rx, ry + barsHeight + 1, rw, digitsHeight + 2, plan.Content, "Consolas", false, false, false, Black,
                digitsFontSize, 0));
    }

    /// <summary>QR-код по центру блока (порт DrawQrBlock при масштабе 1).</summary>
    internal static void AddQrScene(List<LabelSceneItem> items, float rx, float ry, float rw, float rh, string? rawCode, int quietModules)
    {
        var matrix = EncodeModules(PlanBarcode(rawCode, LabelBarcodeFormat.QrCode));
        if (matrix is null)
            return;
        var cellsCount = matrix.Width + 2 * Math.Max(quietModules, 0);
        var m = (int)(Math.Min(rw, rh) / cellsCount);
        if (m < 1 || Math.Min(rw, rh) < cellsCount)
            return;
        var size = matrix.Width * m;
        var cells = new bool[matrix.Width, matrix.Height];
        for (var y = 0; y < matrix.Height; y++)
            for (var x = 0; x < matrix.Width; x++)
                cells[x, y] = matrix[x, y];
        items.Add(new LabelSceneBars((int)Math.Round(rx + (rw - size) / 2f), (int)Math.Round(ry + (rh - size) / 2f), m, size, cells));
    }
}
