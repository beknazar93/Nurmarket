using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (В18, АЛ-5): лист A4 с сеткой ценников/этикеток в PDF (SKDocument) — для печати через CUPS на Linux/macOS. Сетка та же, что у
/// PriceTagService.CreateA4GridDocument на Windows: поля 8 мм, промежуток 3 мм, сколько влезло в ряд и по высоте. Каждая сцена рисуется векторно в точках 203 dpi.</summary>
internal static class PriceTagSheetSkia
{
    private const double MarginMm = 8, GapMm = 3, PageWmm = 210, PageHmm = 297;

    public static (int Columns, int Rows) Grid(double widthMm, double heightMm) =>
        (Math.Max(1, (int)((PageWmm - 2 * MarginMm + GapMm) / (widthMm + GapMm))), Math.Max(1, (int)((PageHmm - 2 * MarginMm + GapMm) / (heightMm + GapMm))));

    public static byte[] CreatePdf(IReadOnlyList<Func<LabelScene>> scenes, double widthMm, double heightMm)
    {
        var (columns, rows) = Grid(widthMm, heightMm);
        var perPage = columns * rows;
        using var stream = new SKDynamicMemoryWStream();
        using (var doc = SKDocument.CreatePdf(stream))
        {
            var pageW = (float)(PageWmm / 25.4 * 72);
            var pageH = (float)(PageHmm / 25.4 * 72);
            for (var index = 0; index < scenes.Count; index += perPage)
            {
                var canvas = doc.BeginPage(pageW, pageH);
                var onThisPage = Math.Min(scenes.Count - index, perPage);
                for (var i = 0; i < onThisPage; i++)
                {
                    var col = i % columns;
                    var row = i / columns;
                    canvas.Save();
                    canvas.Translate((float)((MarginMm + col * (widthMm + GapMm)) / 25.4 * 72), (float)((MarginMm + row * (heightMm + GapMm)) / 25.4 * 72));
                    canvas.Scale(72f / 203f);
                    LabelSceneSkia.Draw(canvas, scenes[index + i]());
                    canvas.Restore();
                }
                doc.EndPage();
            }
            doc.Close();
        }
        using var data = stream.DetachAsData();
        return data.ToArray();
    }
}
