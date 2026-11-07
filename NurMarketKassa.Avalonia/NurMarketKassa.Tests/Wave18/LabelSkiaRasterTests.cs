using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using FluentAssertions;
using NurMarketKassa.Services;
using SkiaSharp;
using Xunit.Abstractions;

namespace NurMarketKassa.Tests.Wave18;

/// <summary>2026-11-06 (В18, А4-08): растр этикетки и ценника на SkiaSharp — для Linux и Android, где System.Drawing не работает. Проверки без устройства:
/// размеры, углы, наличие текста и штрихов (не однородная картинка), совпадение штрихов со старым растром GDI+ пиксель-в-пиксель (только Windows),
/// запасные шрифты для ө ү ң ў қ ғ ҳ, одинаковые байты ESC/POS и TSPL для Skia и GDI+.</summary>
public sealed class LabelSkiaRasterTests
{
    private readonly ITestOutputHelper _out;

    public LabelSkiaRasterTests(ITestOutputHelper output) => _out = output;

    /// <summary>Подробности сравнения — в вывод теста и в файл (при обычном запуске вывод теста не виден).</summary>
    internal static void Diag(ITestOutputHelper o, string line)
    {
        o.WriteLine(line);
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "nurmarket-wave18-diag.txt"), line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }

    private static LabelTemplate Template() => LabelTemplate.CreateDefault();

    private static SKBitmap Skia(LabelTemplate t, string name = "Молоко Простоквашино 1 л", string code = "4600682000130", string? price = "95.50 сом",
        string? sku = null, string? unit = null, string? store = null, string? variant = null) =>
        LabelSceneSkia.Render(BarcodeLabelService.BuildLabelScene(name, code, price, t, sku, unit, store, variant));

    [SupportedOSPlatform("windows")]
    private static Bitmap Gdi(LabelTemplate t, string name = "Молоко Простоквашино 1 л", string code = "4600682000130", string? price = "95.50 сом") =>
        BarcodeLabelService.GenerateLabelBitmap(name, code, price, t);

    private static int Dark(SKBitmap b, int x0, int y0, int x1, int y1)
    {
        var n = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                if (SkiaRaster.Luminance(b.GetPixel(x, y)) < 128)
                    n++;
        return n;
    }

    [Fact]
    public void LabelRaster_HasTemplateSize_WhiteCorners_AndContent()
    {
        var t = Template(); // 40×30 мм
        using var bmp = Skia(t);
        bmp.Width.Should().Be((int)(40 / 25.4 * 203));
        bmp.Height.Should().Be((int)(30 / 25.4 * 203));
        foreach (var (x, y) in new[] { (0, 0), (bmp.Width - 1, 0), (0, bmp.Height - 1), (bmp.Width - 1, bmp.Height - 1) })
            bmp.GetPixel(x, y).Should().Be(SKColors.White, $"угол ({x},{y}) белый");
        var dark = Dark(bmp, 0, 0, bmp.Width, bmp.Height);
        dark.Should().BeGreaterThan(1500, "штрихи, цифры, название и цена — не однородная картинка");
        dark.Should().BeLessThan(bmp.Width * bmp.Height / 2);
        // Текст названия (6 мм от 18 мм) и цены (24,5 мм) — чёрные точки именно в их полосах.
        var nameBand = Dark(bmp, 0, (int)BarcodeLabelService.MmToPx(18), bmp.Width, (int)BarcodeLabelService.MmToPx(24));
        var priceBand = Dark(bmp, 0, (int)BarcodeLabelService.MmToPx(24.5), bmp.Width, bmp.Height);
        nameBand.Should().BeGreaterThan(100);
        priceBand.Should().BeGreaterThan(100);
    }

    [Fact]
    public void LabelRaster_Qr_ModulesAreExactSquares()
    {
        var t = Template();
        t.BarcodeFormat = LabelBarcodeFormat.QrCode;
        t.Barcode = new LabelElementLayout { XMm = 2, YMm = 1, WidthMm = 16, HeightMm = 16 };
        using var bmp = Skia(t, "QR", "https://nurcrm.kg/p/1", "10 сом");
        // Верхний левый угол QR — это «искатель» 7×7 модулей: его внешняя рамка чёрная, начиная с первой точки блока.
        var scene = BarcodeLabelService.BuildLabelScene("QR", "https://nurcrm.kg/p/1", "10 сом", t);
        var bars = scene.Items.OfType<LabelSceneBars>().Single();
        bars.Module.Should().BeGreaterThanOrEqualTo(2);
        for (var i = 0; i < 7 * bars.Module; i++)
            bmp.GetPixel(bars.X + i, bars.Y).Should().Be(SKColors.Black, "верхняя строка искателя сплошная, без сглаживания");
        bmp.GetPixel(bars.X - 1, bars.Y).Should().Be(SKColors.White);
    }

    [Fact]
    public void PriceTagRaster_AllKinds_AreNotBlank()
    {
        var data = new PriceTagData("Платье «Өтүк» летнее", "4600682000130", "1 250 сом", "1 500 сом", "-17%", "A-1", "шт", "Одежда", "Нур Маркет");
        foreach (var kind in new[] { PriceTagKind.Simple, PriceTagKind.WithBarcode, PriceTagKind.Promotional, PriceTagKind.PromotionalColored, PriceTagKind.Detailed, PriceTagKind.WithQr })
        {
            var (w, h) = PriceTagService.GetDefaultSize(kind);
            using var bmp = LabelSceneSkia.Render(PriceTagService.BuildScene(kind, data, w, h));
            bmp.Width.Should().Be((int)(w / 25.4 * 203), kind.ToString());
            bmp.Height.Should().Be((int)(h / 25.4 * 203), kind.ToString());
            var dark = Dark(bmp, 0, 0, bmp.Width, bmp.Height);
            dark.Should().BeGreaterThan(300, kind + ": есть текст и штрихи");
            if (kind != PriceTagKind.PromotionalColored)
                bmp.GetPixel(0, 0).Should().Be(SKColors.White);
            else
                bmp.GetPixel(0, 0).Red.Should().Be(0xD9, "акцентный фон");
        }
    }

    [Theory]
    [InlineData("Өтүк")]
    [InlineData("Ңың")]
    [InlineData("Ўзбек")]
    [InlineData("Қўй")]
    [InlineData("Ғалла")]
    [InlineData("Ҳаво")]
    [InlineData("Ñandú Çay ğüşöç")]
    public void SpecialLetters_HaveGlyphs_InPrimaryOrFallbackFont(string text)
    {
        var face = SkiaFonts.Get("Arial", false, false);
        foreach (var rune in text.EnumerateRunes())
        {
            if (face.ContainsGlyph(rune.Value))
                continue;
            SkiaFonts.ForCodepoint(face, rune.Value, false, false).Should().NotBeNull($"для «{rune}» нужен запасной шрифт");
        }
        // И реально рисуется: строка непустая по ширине и даёт чёрные точки.
        var style = new SkText("Arial", 24);
        SkiaText.Measure(text, style).Should().BeGreaterThan(10);
        using var bmp = new SKBitmap(new SKImageInfo(300, 50, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            SkiaText.Draw(canvas, text, 4, 4, style);
        }
        Dark(bmp, 0, 0, 300, 50).Should().BeGreaterThan(40);
    }

    [Fact]
    public void FontFallback_UnknownFamily_StillRenders()
    {
        var face = SkiaFonts.Get("Какой-то-несуществующий-шрифт", true, false);
        face.Should().NotBeNull();
        SkiaText.Measure("Test 123", new SkText("Consolas", 20)).Should().BeGreaterThan(20);
    }

    [Fact]
    public void EscPosRaster_FromSkia_EqualsFromGdi_ForSamePixels()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare();

        [SupportedOSPlatform("windows")]
        static void Compare()
        {
            const int w = 37, h = 11; // ширина не кратна 8: проверяет добивку строки
            using var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using var gdi = new Bitmap(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var black = (x * 7 + y * 3) % 5 == 0 || x == y;
                    sk.SetPixel(x, y, black ? SKColors.Black : SKColors.White);
                    gdi.SetPixel(x, y, black ? Color.Black : Color.White);
                }
            BarcodeLabelService.BitmapToEscPosRaster(sk).Should().Equal(BarcodeLabelService.BitmapToEscPosRaster(gdi));
            BarcodeLabelService.BuildTsplJob(sk, 40, 30, 2, 1, 1, 3, new TsplExtras(8, 4, false, "gap"))
                .Should().Equal(BarcodeLabelService.BuildTsplJob(gdi, 40, 30, 2, 1, 1, 3, new TsplExtras(8, 4, false, "gap")));
        }
    }

    [Fact]
    public void Barcode_Bars_MatchOldGdiRaster_PixelForPixel()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare(_out);

        [SupportedOSPlatform("windows")]
        static void Compare(ITestOutputHelper o)
        {
            foreach (var (format, code) in new[]
                     {
                         (LabelBarcodeFormat.Auto, "4600682000130"), // EAN-13
                         (LabelBarcodeFormat.Auto, "ABC-12345"),     // Code 128
                         (LabelBarcodeFormat.Code128, "4600682000130"),
                     })
            {
                var t = Template();
                t.BarcodeFormat = format;
                using var sk = Skia(t, code: code);
                using var gdi = Gdi(t, code: code);
                sk.Width.Should().Be(gdi.Width);
                sk.Height.Should().Be(gdi.Height);
                // Полоса штрихов: от верха блока (1 мм) на высоту блока минус цифры (16) и 2 точки.
                var top = (int)Math.Round(BarcodeLabelService.MmToPx(1));
                var bars = (int)Math.Round(BarcodeLabelService.MmToPx(16)) - 16 - 2;
                var diff = 0;
                for (var y = top; y < top + bars; y++)
                    for (var x = 0; x < sk.Width; x++)
                    {
                        var a = SkiaRaster.Luminance(sk.GetPixel(x, y)) < 128;
                        var g = gdi.GetPixel(x, y);
                        var b = g.R * 0.299 + g.G * 0.587 + g.B * 0.114 < 128;
                        if (a != b)
                            diff++;
                    }
                Diag(o, $"{format} {code}: различающихся точек в полосе штрихов = {diff}");
                diff.Should().Be(0, $"штрихи {format}/{code} должны совпасть со старым растром");
            }
        }
    }

    [Fact]
    public void Text_Roughly_MatchesOldGdiRaster()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare(_out);

        [SupportedOSPlatform("windows")]
        static void Compare(ITestOutputHelper o)
        {
            var t = Template();
            using var sk = Skia(t);
            using var gdi = Gdi(t);
            // Название и цена: габариты чернил и число тёмных точек близки (шрифты рисуются разными движками — точное совпадение невозможно).
            foreach (var (name, y0mm, y1mm) in new[] { ("название", 18.0, 24.0), ("цена", 24.5, 29.5) })
            {
                var y0 = (int)BarcodeLabelService.MmToPx(y0mm);
                var y1 = Math.Min((int)BarcodeLabelService.MmToPx(y1mm), sk.Height);
                var (skBox, skDark) = Ink(sk.GetPixel, sk.Width, y0, y1);
                var (gdBox, gdDark) = Ink((x, y) =>
                {
                    var c = gdi.GetPixel(x, y);
                    return new SKColor(c.R, c.G, c.B);
                }, gdi.Width, y0, y1);
                Diag(o, $"{name}: Skia bbox {skBox} dark {skDark}; GDI bbox {gdBox} dark {gdDark}");
                skDark.Should().BeGreaterThan((int)(gdDark * 0.6)).And.BeLessThan((int)(gdDark * 1.6), name);
                Math.Abs(skBox.Left - gdBox.Left).Should().BeLessThanOrEqualTo(10, name + ": левый край");
                Math.Abs(skBox.Right - gdBox.Right).Should().BeLessThanOrEqualTo(10, name + ": правый край");
                Math.Abs(skBox.Top - gdBox.Top).Should().BeLessThanOrEqualTo(6, name + ": верх");
                Math.Abs(skBox.Bottom - gdBox.Bottom).Should().BeLessThanOrEqualTo(6, name + ": низ");
            }
        }
    }

    private static (Rectangle Box, int Dark) Ink(Func<int, int, SKColor> pixel, int width, int y0, int y1)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, dark = 0;
        for (var y = y0; y < y1; y++)
            for (var x = 0; x < width; x++)
            {
                if (SkiaRaster.Luminance(pixel(x, y)) >= 128)
                    continue;
                dark++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        return (Rectangle.FromLTRB(left, top, right, bottom), dark);
    }
}
