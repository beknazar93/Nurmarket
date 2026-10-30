namespace NurMarketKassa.Services;

// 2026-10-29: сцена готового ценника для Android/Linux (System.Drawing там не работает) — см. BarcodeLabelService.Scene.cs.
// Доли блоков те же, что в DrawSimple/DrawWithBarcode/DrawPromotional/DrawDetailed/DrawWithQr.
public static partial class PriceTagService
{
    private const uint SceneBlack = 0xFF000000;
    private const uint SceneWhite = 0xFFFFFFFF;
    private const uint SceneAccent = 0xFFD93F3D; // AccentColor (217, 63, 61)
    private const uint SceneDimGray = 0xFF696969;

    public static LabelScene BuildScene(PriceTagKind kind, PriceTagData data, double widthMm, double heightMm)
    {
        var widthPx = Math.Max((int)(widthMm / 25.4 * BarcodeLabelService.Dpi), 10);
        var heightPx = Math.Max((int)(heightMm / 25.4 * BarcodeLabelService.Dpi), 10);
        var items = new List<LabelSceneItem>();
        var wPx = BarcodeLabelService.MmToPx(widthMm);
        var hPx = BarcodeLabelService.MmToPx(heightMm);

        void Text(double x, double y, double w, double h, string? text, bool bold, uint color, bool italic = false, bool strike = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            var r = (X: (float)(x * wPx), Y: (float)(y * hPx), W: (float)(w * wPx), H: (float)(h * hPx));
            if (r.W < 4 || r.H < 4)
                return;
            items.Add(new LabelSceneText(r.X, r.Y, r.W, r.H, text!, "Arial", bold, italic, strike, color, 0, Math.Clamp(r.H * 0.72f, 6f, 48f)));
        }

        void Linear((double X, double Y, double W, double H) b, string? code)
        {
            var r = (X: (float)(b.X * wPx), Y: (float)(b.Y * hPx), W: (float)(b.W * wPx), H: (float)(b.H * hPx));
            if (r.W < 4 || r.H < 4 || string.IsNullOrWhiteSpace(code))
                return;
            var digits = Math.Min(r.H * 0.2f, 14f);
            BarcodeLabelService.AddLinearScene(items, r.X, r.Y, r.W, r.H, wPx, code, LabelBarcodeFormat.Auto, PresetQuietModules,
                digits, Math.Clamp(digits * 0.7f, 6f, 12f));
        }

        switch (kind)
        {
            case PriceTagKind.Simple:
                Text(0.05, 0.06, 0.9, 0.36, data.ProductName, false, SceneBlack);
                Text(0.05, 0.46, 0.9, 0.48, data.PriceText, true, SceneBlack);
                break;
            case PriceTagKind.WithBarcode:
                Linear(WithBarcodeBox, data.Barcode);
                Text(0.05, 0.60, 0.9, 0.18, data.ProductName, false, SceneBlack);
                Text(0.05, 0.80, 0.9, 0.18, data.PriceText, true, SceneBlack);
                break;
            case PriceTagKind.Promotional:
            case PriceTagKind.PromotionalColored:
            {
                var colored = kind == PriceTagKind.PromotionalColored;
                if (colored)
                    items.Add(new LabelSceneRect(0, 0, wPx, hPx, SceneAccent));
                var textColor = colored ? SceneWhite : SceneBlack;
                Text(0.05, 0.04, 0.9, 0.20, data.ProductName, false, textColor);
                if (!string.IsNullOrWhiteSpace(data.OldPriceText))
                    Text(0.05, 0.28, 0.55, 0.18, data.OldPriceText, false, colored ? 0xFFFFDCDC : SceneDimGray, strike: true);
                if (!string.IsNullOrWhiteSpace(data.DiscountText))
                {
                    items.Add(new LabelSceneRect((float)(0.62 * wPx), (float)(0.26 * hPx), (float)(0.33 * wPx), (float)(0.22 * hPx), colored ? SceneWhite : SceneAccent));
                    Text(0.62, 0.26, 0.33, 0.22, data.DiscountText, true, colored ? SceneAccent : SceneWhite);
                }
                Text(0.05, 0.52, 0.9, 0.42, data.PriceText, true, textColor);
                break;
            }
            case PriceTagKind.Detailed:
                Text(0.05, 0.01, 0.9, 0.10, data.StoreName, false, SceneDimGray);
                Text(0.05, 0.12, 0.9, 0.20, data.ProductName, true, SceneBlack);
                Text(0.05, 0.33, 0.9, 0.12, data.Category, false, SceneDimGray, italic: true);
                Linear(DetailedBox, data.Barcode);
                Text(0.05, 0.80, 0.35, 0.18, data.Unit, false, SceneDimGray);
                Text(0.42, 0.78, 0.53, 0.20, data.PriceText, true, SceneBlack);
                break;
            case PriceTagKind.WithQr:
                if (!string.IsNullOrWhiteSpace(data.Barcode))
                    BarcodeLabelService.AddQrScene(items, (float)(QrBox.X * wPx), (float)(QrBox.Y * hPx), (float)(QrBox.W * wPx), (float)(QrBox.H * hPx),
                        data.Barcode, PresetQrQuietModules);
                Text(0.58, 0.08, 0.37, 0.50, data.ProductName, false, SceneBlack);
                Text(0.05, 0.70, 0.9, 0.26, data.PriceText, true, SceneBlack);
                break;
        }
        return new LabelScene(widthPx, heightPx, items);
    }
}
