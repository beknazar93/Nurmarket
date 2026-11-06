using System.IO;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using SkiaSharp;

namespace NurMarketKassa.Tests.Perf;

/// <summary>2026-11-06 (аудит №5, А5-13): чтение картинки QR банка на Android/Linux — предел 40 Мп по заголовку, серые картинки, прозрачность.</summary>
public sealed class PortableImageReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path, "pir-" + Guid.NewGuid().ToString("N")[..8]);

    public PortableImageReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    [Theory]
    [InlineData(0, 10, true)]
    [InlineData(10, -1, true)]
    [InlineData(6324, 6324, false)]   // 39,99 Мп
    [InlineData(6400, 6400, true)]    // 40,96 Мп
    public void IsTooLarge_ByPixels(int w, int h, bool expected) =>
        PortableImageReader.IsTooLarge(w, h).Should().Be(expected);

    [Fact]
    public void Read_GrayPng_ComesAsGrayBgra()
    {
        var path = Path.Combine(_dir, "gray.png");
        using (var bmp = new SKBitmap(new SKImageInfo(40, 30, SKColorType.Gray8, SKAlphaType.Opaque)))
        {
            bmp.Erase(new SKColor(90, 90, 90));
            using var img = SKImage.FromBitmap(bmp);
            File.WriteAllBytes(path, img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }

        var res = PortableImageReader.Read(path);

        res.Should().NotBeNull();
        res!.Value.Width.Should().Be(40);
        res.Value.Height.Should().Be(30);
        res.Value.Bgra.Length.Should().Be(40 * 30 * 4);
        res.Value.Bgra[0].Should().Be(90);   // B
        res.Value.Bgra[1].Should().Be(90);   // G
        res.Value.Bgra[2].Should().Be(90);   // R
        res.Value.Bgra[3].Should().Be(255);  // A
    }

    [Fact]
    public void Read_TransparentPixel_BecomesWhite()
    {
        var path = Path.Combine(_dir, "alpha.png");
        using (var bmp = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            bmp.Erase(SKColors.Transparent);
            using var img = SKImage.FromBitmap(bmp);
            File.WriteAllBytes(path, img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }

        var res = PortableImageReader.Read(path);

        res.Should().NotBeNull();
        res!.Value.Bgra[0].Should().Be(255);
        res.Value.Bgra[1].Should().Be(255);
        res.Value.Bgra[2].Should().Be(255);
    }

    [Fact]
    public void Read_NotAnImage_ReturnsNull()
    {
        var path = Path.Combine(_dir, "x.png");
        File.WriteAllText(path, "not an image at all, just text");
        PortableImageReader.Read(path).Should().BeNull();
        PortableImageReader.Read(Path.Combine(_dir, "missing.png")).Should().BeNull();
    }
}
