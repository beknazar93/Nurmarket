using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Services;
using SkiaSharp;
using Xunit.Abstractions;

namespace NurMarketKassa.Tests.Wave18;

/// <summary>2026-11-06 (В18, АЛ-4, А4-08): лист A4 с этикетками на SkiaSharp — предпросмотр (раньше на Linux/Android пустой), картинка ячейки, PDF для CUPS.
/// Сравнение со старым растром GDI+ (только Windows): штрихи совпадают пиксель-в-пиксель на 203, 300 и 600 dpi, текст — по числу точек и доле расхождений.</summary>
public sealed class A4SheetSkiaTests
{
    private readonly ITestOutputHelper _out;

    public A4SheetSkiaTests(ITestOutputHelper output) => _out = output;

    private static A4SheetItem Item(string name = "Молоко «Айналайын» 1 л", string code = "4600682000130", string price = "95,50 сом", string? old = "120,00 сом", string? discount = "-20%") =>
        new(new PriceTagData(name, code, price, old, discount), 1, "123", "Пастеризованное, 3,2%");

    private static A4SheetOptions Options(Action<A4SheetOptions>? tune = null)
    {
        var o = new A4SheetOptions { Columns = 3, Rows = 8, ShowDiscount = true, ShowPlu = true, ShowDescription = true, Title = "ул. Токтогула 1" };
        tune?.Invoke(o);
        return o;
    }

    private static int Dark(SKBitmap b, int x0, int y0, int x1, int y1)
    {
        var n = 0;
        for (var y = Math.Max(y0, 0); y < Math.Min(y1, b.Height); y++)
            for (var x = Math.Max(x0, 0); x < Math.Min(x1, b.Width); x++)
                if (SkiaRaster.Luminance(b.GetPixel(x, y)) < 128)
                    n++;
        return n;
    }

    [Fact]
    public void Page_HasA4Size_NotBlank_AndSecondPageDiffers()
    {
        var o = Options();
        var items = Enumerable.Range(0, 30).Select(i => Item(code: "46006820001" + (i % 10) + "0")).ToList();
        var expanded = A4SheetService.Expand(items);
        using var page = A4SheetSkia.RenderPage(expanded, 0, o, 110);
        page.Width.Should().Be((int)Math.Round(210 / 25.4 * 110));
        page.Height.Should().Be((int)Math.Round(297 / 25.4 * 110));
        page.GetPixel(0, 0).Should().Be(SKColors.White);
        Dark(page, 0, 0, page.Width, page.Height).Should().BeGreaterThan(8000, "24 этикетки с названием, штрихкодом и ценой");
        A4SheetService.PageCount(expanded.Count, o).Should().Be(2);
        using var page2 = A4SheetSkia.RenderPage(expanded, 1, o, 110);
        Dark(page2, 0, 0, page2.Width, page2.Height).Should().BeLessThan(Dark(page, 0, 0, page.Width, page.Height), "на втором листе всего 6 этикеток");
        Dark(page2, 0, 0, page2.Width, page2.Height).Should().BeGreaterThan(1500);
    }

    [Fact]
    public void Landscape_And_CustomPreset_ChangeGeometry()
    {
        var o = Options(x => x.Landscape = true);
        using var page = A4SheetSkia.RenderPage(A4SheetService.Expand([Item()]), 0, o, 100);
        page.Width.Should().BeGreaterThan(page.Height);
        var custom = Options(x => { x.Preset = A4SheetService.CustomPreset; x.Columns = 2; x.Rows = 4; x.CellWmm = 90; x.CellHmm = 60; x.PitchXmm = 100; x.PitchYmm = 65; });
        using var page2 = A4SheetSkia.RenderPage(A4SheetService.Expand([Item()]), 0, custom, 100);
        Dark(page2, 0, 0, page2.Width, page2.Height).Should().BeGreaterThan(500);
    }

    [Fact]
    public void Pdf_IsValid_HasOnePagePerSheet()
    {
        var o = Options();
        var items = Enumerable.Range(0, 30).Select(_ => Item()).ToList();
        var pdf = A4SheetSkia.CreatePdf(items, o);
        pdf.Length.Should().BeGreaterThan(2000);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        var text = Encoding.Latin1.GetString(pdf);
        System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count.Should().Be(2, "30 этикеток по 24 на лист — 2 страницы");
        text.Should().Contain("%%EOF");
    }

    [Fact]
    public void PriceTagSheetPdf_UsesSameGridAsWindowsPrint()
    {
        var (cols, rows) = PriceTagSheetSkia.Grid(40, 30);
        cols.Should().Be(4); // (194+3)/(40+3)
        rows.Should().Be(8); // (281+3)/(30+3)
        var scenes = Enumerable.Range(0, 40).Select(_ => (Func<LabelScene>)(() => PriceTagService.BuildScene(PriceTagKind.WithBarcode,
            new PriceTagData("Хлеб", "4600682000130", "30 сом"), 40, 30))).ToList();
        var pdf = PriceTagSheetSkia.CreatePdf(scenes, 40, 30);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count.Should().Be(2, "32 на лист: 40 ценников — 2 страницы");
    }

    [Theory]
    [InlineData(203)]
    [InlineData(300)]
    [InlineData(600)]
    public void Cell_Barcode_MatchesOldGdiRaster_PixelForPixel(int dpi)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare(dpi, _out);

        [SupportedOSPlatform("windows")]
        static void Compare(int dpi, ITestOutputHelper o)
        {
            // Только штрихкод: полоса штрихов — середина ячейки.
            var opt = Options(x => { x.ShowName = false; x.ShowPrice = false; x.ShowPlu = false; x.ShowDescription = false; x.ShowDiscount = false; x.Title = ""; x.CutLines = false; });
            var geo = A4SheetService.GetGeometry(opt);
            var item = Item();
            using var sk = A4SheetSkia.RenderCell(item, opt, geo, dpi);
            using var gdi = A4SheetService.RenderCell(item, opt, geo, dpi);
            sk.Width.Should().Be(gdi.Width);
            sk.Height.Should().Be(gdi.Height);
            var mid = sk.Height / 2;
            var from = mid - (int)(3 / 25.4 * dpi);
            var to = mid + (int)(1.5 / 25.4 * dpi);
            var diff = 0;
            var darkSk = 0;
            for (var y = from; y < to; y++)
                for (var x = 0; x < sk.Width; x++)
                {
                    var a = SkiaRaster.Luminance(sk.GetPixel(x, y)) < 128;
                    var g = gdi.GetPixel(x, y);
                    var b = g.R * 0.299 + g.G * 0.587 + g.B * 0.114 < 128;
                    if (a)
                        darkSk++;
                    if (a != b)
                        diff++;
                }
            Diag(o, $"ячейка {dpi} dpi: различий в полосе штрихов {diff}, чёрных {darkSk}");
            darkSk.Should().BeGreaterThan(200);
            diff.Should().Be(0);
        }
    }

    [Fact]
    public void Cell_FullLayout_RoughlyMatchesOldGdiRaster()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare(_out);

        [SupportedOSPlatform("windows")]
        static void Compare(ITestOutputHelper o)
        {
            var opt = Options();
            var geo = A4SheetService.GetGeometry(opt);
            var item = Item();
            using var sk = A4SheetSkia.RenderCell(item, opt, geo, 203);
            using var gdi = A4SheetService.RenderCell(item, opt, geo, 203);
            var darkSk = Dark(sk, 0, 0, sk.Width, sk.Height);
            // Для глаза: обе картинки рядом во временной папке (разбор расхождений вручную).
            try
            {
                File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "nurmarket-wave18-cell-skia.png"), SkiaRaster.ToPng(sk));
                gdi.Save(Path.Combine(Path.GetTempPath(), "nurmarket-wave18-cell-gdi.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (IOException)
            {
            }
            int darkGdi = 0, diff = 0;
            for (var y = 0; y < gdi.Height; y++)
                for (var x = 0; x < gdi.Width; x++)
                {
                    var g = gdi.GetPixel(x, y);
                    var b = g.R * 0.299 + g.G * 0.587 + g.B * 0.114 < 128;
                    if (b)
                        darkGdi++;
                    if (b != (SkiaRaster.Luminance(sk.GetPixel(x, y)) < 128))
                        diff++;
                }
            Diag(o, $"ячейка целиком: Skia чёрных {darkSk}, GDI чёрных {darkGdi}, различающихся точек {diff} из {gdi.Width * gdi.Height}");
            darkSk.Should().BeGreaterThan((int)(darkGdi * 0.7)).And.BeLessThan((int)(darkGdi * 1.4));
            diff.Should().BeLessThan((int)(gdi.Width * gdi.Height * 0.12), "тексты рисуют разные движки — допускается небольшое расхождение");
        }
    }

    private static void Diag(ITestOutputHelper o, string line) => LabelSkiaRasterTests.Diag(o, line);
}
