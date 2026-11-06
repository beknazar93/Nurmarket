using System.IO;
using FluentAssertions;
using NurMarketKassa.Services;
using SkiaSharp;

namespace NurMarketKassa.Tests.Perf;

/// <summary>2026-11-05 (аудит скорости, п. 15 а): ужатие миниатюр товаров до 320 px — размер сторон, пропорции, прозрачность, повреждённые файлы.
/// Файл ThumbImageCompressor.cs подключён в тесты ссылкой (проект кассы с интерфейсом тестами не подключается).</summary>
public sealed class ThumbImageCompressorTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestDataRoot_Dir(), "thumb-" + Guid.NewGuid().ToString("N")[..8]);

    public ThumbImageCompressorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private static string TestDataRoot_Dir() => NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path;

    private static byte[] MakeImage(int w, int h, SKEncodedImageFormat format, bool transparentCorner = false)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(transparentCorner ? SKColors.Transparent : SKColors.White);
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, h),
                new[] { SKColors.OrangeRed, SKColors.RoyalBlue }, null, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            if (transparentCorner)
                canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) / 3f, paint);
            else
                canvas.DrawRect(0, 0, w, h, paint);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(format, 90);
        return data.ToArray();
    }

    private static (int W, int H, SKAlphaType Alpha) Probe(byte[] bytes)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes));
        codec.Should().NotBeNull("результат должен расшифровываться");
        return (codec!.Info.Width, codec.Info.Height, codec.Info.AlphaType);
    }

    [Theory]
    [InlineData(1200, 600)]
    [InlineData(600, 1200)]
    [InlineData(900, 900)]
    [InlineData(4000, 3000)]   // большой JPEG — путь уменьшенной расшифровки
    public void Compress_LongSideAtMost320_KeepsAspect(int w, int h)
    {
        var src = MakeImage(w, h, SKEncodedImageFormat.Jpeg);

        var packed = ThumbImageCompressor.Compress(src, ThumbImageCompressor.ThumbMaxSide, 80, out var ext);

        packed.Should().NotBeNull();
        ext.Should().BeOneOf(".webp", ".jpg");
        var (rw, rh, _) = Probe(packed!);
        Math.Max(rw, rh).Should().Be(320);
        ((double)rw / rh).Should().BeApproximately((double)w / h, 0.02);
        packed!.Length.Should().BeLessThan(src.Length);
    }

    [Fact]
    public void Compress_SmallImage_IsNotEnlarged()
    {
        var src = MakeImage(100, 50, SKEncodedImageFormat.Png);
        var packed = ThumbImageCompressor.Compress(src, 320, 80, out _);
        packed.Should().NotBeNull();
        var (rw, rh, _) = Probe(packed!);
        (rw, rh).Should().Be((100, 50));
    }

    [Fact]
    public void Compress_KeepsTransparency()
    {
        var src = MakeImage(800, 800, SKEncodedImageFormat.Png, transparentCorner: true);

        var packed = ThumbImageCompressor.Compress(src, 320, 80, out var ext);

        packed.Should().NotBeNull();
        ext.Should().BeOneOf(".webp", ".png"); // JPEG для прозрачной картинки не допускается
        using var bmp = SKBitmap.Decode(packed!);
        bmp.Should().NotBeNull();
        bmp!.GetPixel(1, 1).Alpha.Should().BeLessThan(40, "угол был прозрачным");
        bmp.GetPixel(bmp.Width / 2, bmp.Height / 2).Alpha.Should().BeGreaterThan(200, "центр — непрозрачный круг");
    }

    [Fact]
    public void Compress_NotAnImage_ReturnsNull_NoException()
    {
        var junk = new byte[4096];
        new Random(1).NextBytes(junk);
        ThumbImageCompressor.Compress(junk, 320, 80, out _).Should().BeNull();
        ThumbImageCompressor.Compress(Array.Empty<byte>(), 320, 80, out _).Should().BeNull();
        ThumbImageCompressor.Compress(System.Text.Encoding.UTF8.GetBytes("<html>404 Not Found</html> padding padding"), 320, 80, out _).Should().BeNull();
    }

    [Fact]
    public void Compress_TruncatedFile_DoesNotThrow()
    {
        var src = MakeImage(1200, 800, SKEncodedImageFormat.Jpeg);
        var cut = src.AsSpan(0, src.Length / 3).ToArray();

        var act = () => ThumbImageCompressor.Compress(cut, 320, 80, out _);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryRecompressFile_WritesNewFile_KeepsOriginal()
    {
        var source = Path.Combine(_dir, "orig.jpg");
        var dest = Path.Combine(_dir, "orig.t320.webp");
        File.WriteAllBytes(source, MakeImage(2000, 1500, SKEncodedImageFormat.Jpeg));
        var before = new FileInfo(source).Length;

        ThumbImageCompressor.TryRecompressFile(source, dest, 320, 80).Should().BeTrue();

        File.Exists(source).Should().BeTrue("оригинал не теряется — его удаляет вызывающий после успеха");
        new FileInfo(source).Length.Should().Be(before);
        File.Exists(dest).Should().BeTrue();
        new FileInfo(dest).Length.Should().BeLessThan(before);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void TryRecompressFile_CorruptSource_ReturnsFalse_NoNewFile_NoTempLeft()
    {
        var source = Path.Combine(_dir, "bad.jpg");
        var dest = Path.Combine(_dir, "bad.t320.webp");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18 });

        ThumbImageCompressor.TryRecompressFile(source, dest, 320, 80).Should().BeFalse();

        File.Exists(dest).Should().BeFalse();
        File.Exists(source).Should().BeTrue();
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        ThumbImageCompressor.TryRecompressFile(Path.Combine(_dir, "no-such.jpg"), dest, 320, 80).Should().BeFalse();
    }

    [Fact]
    public void TryWriteAtomic_ReplacesExistingFile()
    {
        var path = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(path, new byte[] { 1, 1, 1 });
        ThumbImageCompressor.TryWriteAtomic(path, new byte[] { 9, 8, 7, 6 }).Should().BeTrue();
        File.ReadAllBytes(path).Should().Equal(9, 8, 7, 6);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }
    // 2026-11-06 (Л5-23 / А5-12): PNG/GIF/BMP не умеют уменьшенную расшифровку — огромные не раскрываем вовсе; JPEG и WebP предела не имеют.
    [Fact]
    public void IsOversized_LargePng_True_SmallPng_False_Jpeg_False_Garbage_False()
    {
        var png = MakeImage(1200, 1000, SKEncodedImageFormat.Png);
        var jpg = MakeImage(1200, 1000, SKEncodedImageFormat.Jpeg);
        var webp = MakeImage(1200, 1000, SKEncodedImageFormat.Webp);

        ThumbImageCompressor.IsOversized(png, 1_000_000).Should().BeTrue();    // 1,2 Мп > 1 Мп
        ThumbImageCompressor.IsOversized(png, 2_000_000).Should().BeFalse();
        ThumbImageCompressor.IsOversized(jpg, 1_000_000).Should().BeFalse();   // читается в уменьшенном виде
        ThumbImageCompressor.IsOversized(webp, 1_000_000).Should().BeFalse();
        ThumbImageCompressor.IsOversized(new byte[64], 1).Should().BeFalse();
        ThumbImageCompressor.IsOversized(Array.Empty<byte>(), 1).Should().BeFalse();
    }

    [Fact]
    public void MaxNonScalablePixels_IsSane() =>
        ThumbImageCompressor.MaxNonScalablePixels.Should().BeInRange(16_000_000L, 50_000_000L);

    [Fact]
    public void Compress_LargePng_ProducesThumb()
    {
        var src = MakeImage(3000, 2000, SKEncodedImageFormat.Png);
        var packed = ThumbImageCompressor.Compress(src, ThumbImageCompressor.ThumbMaxSide, 80, out _);
        packed.Should().NotBeNull();
        var (w, h, _) = Probe(packed!);
        Math.Max(w, h).Should().Be(320);
    }

    [Fact]
    public void Compress_ManyAtOnce_AllSucceed()
    {
        var src = MakeImage(1500, 1000, SKEncodedImageFormat.Png);
        var results = Enumerable.Range(0, 8).AsParallel().Select(i => ThumbImageCompressor.Compress(src, 320, 80, out _)).ToList();
        results.Should().OnlyContain(r => r != null);
    }
}
