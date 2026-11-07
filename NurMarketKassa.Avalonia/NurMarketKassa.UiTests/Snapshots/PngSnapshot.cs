using System.Runtime.CompilerServices;
using SkiaSharp;

namespace NurMarketKassa.UiTests.Snapshots;

/// <summary>2026-11-06 (В14): сравнение снимка окна с эталоном из репозитория (Snapshots/Baselines/*.png) с допуском.
/// Эталон хранится уменьшенным вдвое (≤ 150 КБ на файл, общий размер под контролем); сравнение — по каналам с порогом и долей отличий.
/// При расхождении «actual» и карта отличий («diff») сохраняются в папку отчёта (по умолчанию %TEMP%\nurmarket-tests\ui-actual),
/// эталон не трогается. Обновить эталоны: переменная NURMARKET_UI_UPDATE=1 (test-safe.ps1 -UpdateSnapshots).</summary>
internal static class PngSnapshot
{
    /// <summary>Разница канала (0–255), меньше которой пиксели считаются равными (сглаживание шрифтов, округления Skia).</summary>
    public const int ChannelTolerance = 24;

    /// <summary>Доля отличающихся пикселей, выше которой снимок считается изменившимся.</summary>
    public const double MaxDiffFraction = 0.004;

    /// <summary>Предел размера одного эталона, байт.</summary>
    public const int MaxBaselineBytes = 150 * 1024;

    public static bool UpdateMode => Environment.GetEnvironmentVariable("NURMARKET_UI_UPDATE") == "1";

    public static string BaselineDir { get; } = FindBaselineDir();

    public static string ReportDir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("NURMARKET_UI_REPORT_DIR") is { Length: > 0 } d ? d : Path.Combine(Path.GetTempPath(), "nurmarket-tests"), "ui-actual");

    private static string FindBaselineDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "Baselines");

    /// <summary>Результат сравнения: пусто — совпало; иначе текст причины для сообщения теста.</summary>
    public static string? Compare(string name, SKBitmap actualFull)
    {
        using var actual = Downscale(actualFull);
        var baselinePath = Path.Combine(BaselineDir, name + ".png");
        if (UpdateMode)
        {
            Directory.CreateDirectory(BaselineDir);
            Save(actual, baselinePath);
            var size = new FileInfo(baselinePath).Length;
            return size > MaxBaselineBytes ? $"эталон {name}.png {size / 1024} КБ — больше {MaxBaselineBytes / 1024} КБ" : null;
        }

        if (!File.Exists(baselinePath))
        {
            var saved = SaveActual(name, actual);
            return $"нет эталона {name}.png; снимок сохранён: {saved}. Создать эталоны: test-safe.ps1 -UpdateSnapshots";
        }

        using var expected = SKBitmap.Decode(baselinePath);
        if (expected is null)
            return $"эталон {name}.png не читается";
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return $"размер {actual.Width}x{actual.Height} вместо {expected.Width}x{expected.Height}; снимок: {SaveActual(name, actual)}";

        var diff = Diff(expected, actual, out var diffMap);
        using (diffMap)
        {
            if (diff <= MaxDiffFraction)
                return null;
            var actualPath = SaveActual(name, actual);
            var diffPath = Path.Combine(ReportDir, name + ".diff.png");
            Save(diffMap, diffPath);
            return $"{diff:P2} пикселей отличается (допуск {MaxDiffFraction:P1}); actual: {actualPath}; diff: {diffPath}";
        }
    }

    /// <summary>Доля пикселей, у которых любой канал отличается больше допуска; diffMap — красные точки на затемнённом фоне.</summary>
    public static double Diff(SKBitmap a, SKBitmap b, out SKBitmap diffMap)
    {
        diffMap = new SKBitmap(a.Width, a.Height);
        long bad = 0;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var p = a.GetPixel(x, y);
            var q = b.GetPixel(x, y);
            var different = Math.Abs(p.Red - q.Red) > ChannelTolerance || Math.Abs(p.Green - q.Green) > ChannelTolerance
                            || Math.Abs(p.Blue - q.Blue) > ChannelTolerance;
            if (different)
                bad++;
            diffMap.SetPixel(x, y, different ? new SKColor(255, 0, 0) : new SKColor((byte)(q.Red / 3), (byte)(q.Green / 3), (byte)(q.Blue / 3)));
        }
        return a.Width * a.Height == 0 ? 0 : (double)bad / (a.Width * a.Height);
    }

    private static SKBitmap Downscale(SKBitmap src)
    {
        var w = Math.Max(1, src.Width / 2);
        var h = Math.Max(1, src.Height / 2);
        var dst = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        using var image = SKImage.FromBitmap(src);
        canvas.DrawImage(image, new SKRect(0, 0, w, h), paint);
        return dst;
    }

    private static string SaveActual(string name, SKBitmap bmp)
    {
        var path = Path.Combine(ReportDir, name + ".actual.png");
        Save(bmp, path);
        return path;
    }

    private static void Save(SKBitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}
