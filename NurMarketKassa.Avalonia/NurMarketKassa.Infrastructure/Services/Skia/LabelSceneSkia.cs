using SkiaSharp;

namespace NurMarketKassa.Services;

// 2026-11-06 (В18, А4-08): растр этикетки/ценника на SkiaSharp. Раскладку не дублируем: рисуется готовая LabelScene (точки 203 dpi) — та же, что
// показывает предпросмотр на Linux/Android (Avalonia, LabelSceneRenderer). Штрихи и модули QR — целые прямоугольники без сглаживания (пиксель-в-пиксель,
// как BlitModules в GDI+), текст — SkiaText (шрифты с запасными для ө ү ң ў қ ғ ҳ). На Windows печать через драйвер по-прежнему идёт GDI+; Skia-растр
// там используется в проверках (сравнение со старым растром) и для тестов.

public static class LabelSceneSkia
{
    /// <summary>Растр сцены: белый фон, размер <see cref="LabelScene.WidthPx"/> × <see cref="LabelScene.HeightPx"/> точек.</summary>
    public static SKBitmap Render(LabelScene scene)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Math.Max(scene.WidthPx, 1), Math.Max(scene.HeightPx, 1), SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        Draw(canvas, scene);
        return bitmap;
    }

    /// <summary>Рисует сцену на canvas в её точках (203 dpi) от текущего начала координат — для картинки и для страницы PDF (лист ценников).</summary>
    public static void Draw(SKCanvas canvas, LabelScene scene)
    {
        foreach (var item in scene.Items)
        {
            switch (item)
            {
                case LabelSceneRect r:
                    using (var paint = new SKPaint { Color = new SKColor(r.Argb), IsAntialias = false })
                        canvas.DrawRect(new SKRect(r.X, r.Y, r.X + r.W, r.Y + r.H), paint);
                    break;
                case LabelSceneBars bars:
                    DrawBars(canvas, bars);
                    break;
                case LabelSceneText text:
                    DrawText(canvas, text);
                    break;
            }
        }
    }

    private static void DrawBars(SKCanvas canvas, LabelSceneBars b)
    {
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        var width = b.Cells.GetLength(0);
        var rows = b.Cells.GetLength(1);
        var oneRow = rows == 1;
        for (var y = 0; y < rows; y++)
        {
            var x = 0;
            while (x < width)
            {
                if (!b.Cells[x, y])
                {
                    x++;
                    continue;
                }
                var start = x;
                while (x < width && b.Cells[x, y])
                    x++;
                var top = oneRow ? b.Y : b.Y + y * b.Module;
                var height = oneRow ? b.HeightPx : b.Module;
                var left = b.X + start * b.Module;
                canvas.DrawRect(new SKRect(left, top, left + (x - start) * b.Module, top + height), paint);
            }
        }
    }

    private static SkText Style(LabelSceneText t, float sizePt) =>
        new(t.Family, sizePt * SkiaText.PtToPx, t.Bold, t.Italic, t.Strike, false, t.Argb);

    private static void DrawText(SKCanvas canvas, LabelSceneText t)
    {
        if (t.SizePt > 0)
        {
            // Ручной размер: перенос по ширине блока, по центру, обрезка по рамке (как DrawTextElement с manualFontSizePx).
            var style = Style(t, t.SizePt);
            var lines = SkiaText.Wrap(t.Text, t.W, style);
            var lineH = SkiaText.LineHeight(style);
            var blockH = lineH * lines.Count;
            var y = t.Y + (t.H - blockH) / 2f;
            canvas.Save();
            canvas.ClipRect(new SKRect(t.X, t.Y, t.X + t.W, t.Y + t.H));
            foreach (var line in lines)
            {
                var w = SkiaText.Measure(line, style);
                SkiaText.Draw(canvas, line, t.X + (t.W - w) / 2f, y, style);
                y += lineH;
            }
            canvas.Restore();
            return;
        }

        // Автоподбор: от максимума вниз до 6 пт, пока строка не влезет по ширине; не влезла — с многоточием.
        var size = t.MaxAutoPt;
        var st = Style(t, size);
        while (size > 6 && SkiaText.Measure(t.Text, st) > t.W)
        {
            size -= 1;
            st = Style(t, size);
        }
        var display = SkiaText.Truncate(t.Text, t.W, st);
        var tw = SkiaText.Measure(display, st);
        var th = SkiaText.LineHeight(st);
        SkiaText.Draw(canvas, display, t.X + Math.Max(0, (t.W - tw) / 2f), t.Y + Math.Max(0, (t.H - th) / 2f), st);
    }
}

/// <summary>Кодирование SKBitmap для принтера и для файла: PNG, растровая команда ESC/POS «GS v 0» (как <c>BitmapToEscPosRaster</c>).</summary>
public static class SkiaRaster
{
    public static byte[] ToPng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Яркость пикселя по тем же весам, что в GDI-версии (0,299 R + 0,587 G + 0,114 B).</summary>
    public static double Luminance(SKColor c) => c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114;
}
