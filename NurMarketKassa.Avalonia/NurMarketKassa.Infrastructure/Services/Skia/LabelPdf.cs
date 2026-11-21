using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>2026-11-21 («Редактор ценников», «PDF»): этикетки в PDF — страница размером с этикетку (мм), по странице на копию. Рисуется та же
/// сцена (<see cref="BarcodeLabelService.BuildLabelScene"/>), что у печати на Linux/Android, а не снимок экрана: штрихи — целые прямоугольники,
/// текст — векторный (Skia). Работает на всех системах (без System.Drawing).</summary>
public static class LabelPdf
{
    private const float PtPerMm = 72f / 25.4f;

    /// <summary>PDF с <paramref name="copies"/> страницами (1..99) этикетки по шаблону. Данные товара для своих элементов — <see cref="LabelTemplate.Item"/>.</summary>
    public static byte[] Create(string productName, string barcode, string? priceText, LabelTemplate template, int copies,
        string? sku = null, string? unit = null, string? storeName = null, string? variantText = null)
    {
        var scene = BarcodeLabelService.BuildLabelScene(productName, barcode, priceText, template, sku, unit, storeName, variantText);
        return Create(new[] { scene }, template.WidthMm, template.HeightMm, copies);
    }

    /// <summary>PDF из готовых сцен (по сцене — страница; каждая сцена повторяется <paramref name="copies"/> раз подряд).</summary>
    public static byte[] Create(IReadOnlyList<LabelScene> scenes, double widthMm, double heightMm, int copies)
    {
        copies = Math.Clamp(copies, 1, 99);
        var wPt = (float)(widthMm * PtPerMm);
        var hPt = (float)(heightMm * PtPerMm);
        using var stream = new SKDynamicMemoryWStream();
        using (var doc = SKDocument.CreatePdf(stream))
        {
            foreach (var scene in scenes)
            {
                for (var i = 0; i < copies; i++)
                {
                    var canvas = doc.BeginPage(wPt, hPt);
                    canvas.Clear(SKColors.White);
                    // Сцена — в точках 203 dpi: масштаб в пункты PDF (1/72 дюйма) вписывает её в страницу размером с этикетку.
                    canvas.Scale(wPt / Math.Max(scene.WidthPx, 1), hPt / Math.Max(scene.HeightPx, 1));
                    LabelSceneSkia.Draw(canvas, scene);
                    doc.EndPage();
                }
            }
            doc.Close();
        }
        using var data = stream.DetachAsData();
        return data.ToArray();
    }
}
