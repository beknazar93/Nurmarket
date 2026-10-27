namespace NurMarketKassa.Services;

/// <summary>2026-10-26, окно «Печать штрихкодов и ценников»: автоматическая расстановка элементов этикетки по размеру,
/// высоте штрихкода и набору включённых элементов — как «Размер этикетки» и «Высота штрих-кода» на странице печати
/// штрихкодов сайта. Сверху вниз: магазин, штрихкод, название, артикул и единица (в одну строку), цена. Выключенные
/// элементы места не занимают и не двигаются. Шрифт и размер шрифта элементов не трогаются. Точную расстановку руками
/// по-прежнему делает редактор этикетки.</summary>
public static class LabelAutoLayout
{
    /// <summary>Пикселей CSS на дюйм: «Высота штрих-кода: 44» в окне — это пиксели экрана, как на сайте.</summary>
    private const double CssPxPerInch = 96.0;

    public static double PxToMm(double px) => px * 25.4 / CssPxPerInch;

    public static int MmToPx(double mm) => (int)Math.Round(mm * CssPxPerInch / 25.4);

    /// <summary>Расставляет элементы шаблона. Возвращает высоту штрихкода в пикселях, которая реально поместилась
    /// (меньше запрошенной, если этикетка низкая и остальные элементы заняли место).</summary>
    public static int Apply(LabelTemplate t, double barcodeHeightPx)
    {
        const double margin = 1.2;
        const double gap = 0.6;
        var innerW = Math.Max(5, t.WidthMm - 2 * margin);
        var innerH = Math.Max(5, t.HeightMm - 2 * margin);
        var h = t.HeightMm;

        var hasStore = t.StoreName.Enabled;
        var hasBar = t.Barcode.Enabled;
        var hasName = t.ProductName.Enabled;
        var hasSkuRow = t.Sku.Enabled || t.Unit.Enabled;
        var hasPrice = t.Price.Enabled;

        var hStore = hasStore ? Math.Clamp(h * 0.11, 2.2, 4) : 0;
        var hSku = hasSkuRow ? Math.Clamp(h * 0.11, 2.2, 4) : 0;
        var hName = hasName ? Math.Clamp(h * 0.20, 3.2, 8) : 0;
        var hPrice = hasPrice ? Math.Clamp(h * 0.25, 3.6, 11) : 0;
        var hBar = hasBar ? Math.Max(PxToMm(Math.Clamp(barcodeHeightPx, 8, 400)), 4) : 0;

        var blocks = (hasStore ? 1 : 0) + (hasBar ? 1 : 0) + (hasName ? 1 : 0) + (hasSkuRow ? 1 : 0) + (hasPrice ? 1 : 0);
        if (blocks == 0)
            return MmToPx(hBar);
        var gaps = gap * (blocks - 1);

        // Штрихкод уступает место остальным элементам, но не меньше 5 мм; если и так не помещается — всё уменьшается пропорционально.
        var others = hStore + hSku + hName + hPrice;
        if (hasBar)
        {
            var room = innerH - others - gaps;
            hBar = Math.Min(hBar, Math.Max(room, 5));
        }
        var sum = others + hBar + gaps;
        if (sum > innerH)
        {
            var k = (innerH - gaps) / Math.Max(sum - gaps, 0.1);
            hStore *= k; hSku *= k; hName *= k; hPrice *= k; hBar *= k;
            sum = innerH;
        }

        // Лишняя высота делится поровну: сверху, между блоками и снизу — набор стоит по центру этикетки.
        var spare = Math.Max(0, innerH - sum);
        var space = spare / (blocks + 1);
        var y = margin + space;

        void Place(LabelElementLayout e, double height, double x, double width)
        {
            e.XMm = Math.Round(x, 2);
            e.YMm = Math.Round(y, 2);
            e.WidthMm = Math.Round(Math.Max(width, 1), 2);
            e.HeightMm = Math.Round(Math.Max(height, 1), 2);
        }

        if (hasStore) { Place(t.StoreName, hStore, margin, innerW); y += hStore + gap + space; }
        if (hasBar) { Place(t.Barcode, hBar, margin, innerW); y += hBar + gap + space; }
        if (hasName) { Place(t.ProductName, hName, margin, innerW); y += hName + gap + space; }
        if (hasSkuRow)
        {
            if (t.Sku.Enabled && t.Unit.Enabled)
            {
                var half = (innerW - gap) / 2;
                Place(t.Sku, hSku, margin, half);
                Place(t.Unit, hSku, margin + half + gap, half);
            }
            else if (t.Sku.Enabled)
                Place(t.Sku, hSku, margin, innerW);
            else
                Place(t.Unit, hSku, margin, innerW);
            y += hSku + gap + space;
        }
        if (hasPrice) Place(t.Price, hPrice, margin, innerW);

        return MmToPx(hBar);
    }

    /// <summary>Высота штрихкода шаблона в пикселях окна (для поля «Высота штрих-кода»).</summary>
    public static int BarcodeHeightPx(LabelTemplate t) => Math.Max(8, MmToPx(t.Barcode.HeightMm));
}
