using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (В18, АЛ-5): страницы-картинки (PNG, A4 целиком — как в предпросмотре документов) в один PDF через SKDocument; для печати через CUPS на Linux/macOS.
/// Размер страницы — A4 по ориентации картинки; картинка кладётся на всю страницу (поля уже внутри неё).</summary>
internal static class PngPagesPdf
{
    public static byte[] Create(IReadOnlyList<byte[]> pagePngs)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var doc = SKDocument.CreatePdf(stream))
        {
            foreach (var png in pagePngs)
            {
                using var image = SKImage.FromEncodedData(png);
                if (image is null)
                    continue;
                var landscape = image.Width > image.Height;
                var w = landscape ? 841.89f : 595.28f;
                var h = landscape ? 595.28f : 841.89f;
                var canvas = doc.BeginPage(w, h);
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawImage(image, new SKRect(0, 0, w, h), paint);
                doc.EndPage();
            }
            doc.Close();
        }
        using var data = stream.DetachAsData();
        return data.ToArray();
    }
}
