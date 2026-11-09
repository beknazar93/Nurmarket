using System.Drawing;
using System.Runtime.Versioning;
using FluentAssertions;
using NurMarketKassa.Services;
using SkiaSharp;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-08 (жалоба владельца «этикетки не подходят по размеру»): размер этикетки в мм → точки растра (мм × dpi / 25,4) для всех
/// типовых размеров, все элементы и штрихи внутри этикетки, текст не выходит за свои блоки, разрешение текста не зависит от масштаба экрана,
/// поля принтера вычитаются при печати через драйвер, редактор растягивает раскладку вместе с этикеткой, TSPL получает тот же размер.</summary>
public sealed class LabelSizeFitTests
{
    /// <summary>Размеры из окна «Печать штрихкодов», редактора этикетки и распространённых рулонов.</summary>
    public static IEnumerable<object[]> Sizes() => new[]
    {
        new object[] { 30.0, 20.0 }, new object[] { 39.0, 29.0 }, new object[] { 40.0, 30.0 }, new object[] { 58.0, 30.0 },
        new object[] { 58.0, 40.0 }, new object[] { 60.0, 40.0 }, new object[] { 100.0, 70.0 }, new object[] { 100.0, 150.0 },
    };

    private const string Name = "Молоко Простоквашино 3,2% 1 л";
    private const string Code = "4600682000130";
    private const string Price = "95.50 сом";

    private static LabelTemplate Laid(double w, double h, bool all)
    {
        var t = LabelTemplate.CreateDefault();
        t.WidthMm = w;
        t.HeightMm = h;
        if (all)
        {
            t.Sku.Enabled = true;
            t.Unit.Enabled = true;
            t.StoreName.Enabled = true;
        }
        LabelAutoLayout.Apply(t, LabelAutoLayout.BarcodeHeightPx(t));
        return t;
    }

    private static IEnumerable<LabelElementLayout> Enabled(LabelTemplate t) =>
        new[] { t.Barcode, t.ProductName, t.Price, t.Sku, t.Unit, t.StoreName, t.Variant }.Where(e => e is { Enabled: true });

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AutoLayout_KeepsEveryElementInsideTheLabel(double w, double h)
    {
        foreach (var all in new[] { false, true })
        {
            var t = Laid(w, h, all);
            foreach (var e in Enabled(t))
            {
                e.XMm.Should().BeGreaterThanOrEqualTo(0);
                e.YMm.Should().BeGreaterThanOrEqualTo(0);
                (e.XMm + e.WidthMm).Should().BeLessThanOrEqualTo(w + 0.01, $"{w}×{h}: элемент не шире этикетки");
                (e.YMm + e.HeightMm).Should().BeLessThanOrEqualTo(h + 0.01, $"{w}×{h}: элемент не ниже этикетки");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Scene_IsMmTimesDpi_AndBarsAndTextStayInsideTheLabel(double w, double h)
    {
        var t = Laid(w, h, all: true);
        var scene = BarcodeLabelService.BuildLabelScene(Name, Code, Price, t, "ART-1", "шт", "Нур");
        scene.WidthPx.Should().Be((int)(w / 25.4 * 203));
        scene.HeightPx.Should().Be((int)(h / 25.4 * 203));
        var bars = scene.Items.OfType<LabelSceneBars>().Single();
        bars.X.Should().BeGreaterThanOrEqualTo(1);
        (bars.X + bars.Cells.GetLength(0) * bars.Module).Should().BeLessThanOrEqualTo(scene.WidthPx - 1, "штрихи не выходят за правый край");
        (bars.Y + bars.HeightPx).Should().BeLessThanOrEqualTo(scene.HeightPx, "штрихи не выходят за нижний край");
        bars.Module.Should().BeGreaterThanOrEqualTo(1);
        foreach (var text in scene.Items.OfType<LabelSceneText>())
        {
            text.X.Should().BeGreaterThanOrEqualTo(0);
            (text.X + text.W).Should().BeLessThanOrEqualTo(scene.WidthPx + 0.5f);
            (text.Y + text.H).Should().BeLessThanOrEqualTo(scene.HeightPx + 2.5f, "рамка текста (цифры под штрихом — +2 точки) внутри этикетки");
        }
    }

    [SupportedOSPlatform("windows")]
    private static int DarkOutside(Bitmap bmp, LabelTemplate t, float scale, float inflateDots)
    {
        var rects = Enabled(t).Select(e => RectangleF.FromLTRB(
            (BarcodeLabelService.MmToPx(e.XMm) - inflateDots) * scale, (BarcodeLabelService.MmToPx(e.YMm) - inflateDots) * scale,
            (BarcodeLabelService.MmToPx(e.XMm + e.WidthMm) + inflateDots) * scale, (BarcodeLabelService.MmToPx(e.YMm + e.HeightMm) + inflateDots) * scale)).ToList();
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R * 0.299 + c.G * 0.587 + c.B * 0.114 < 128 && !rects.Any(r => r.Contains(x + 0.5f, y + 0.5f)))
                    n++;
            }
        return n;
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void GdiRaster_IsMmTimesDpi_At203And300_AndInkStaysInItsBlocks(double w, double h)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var t = Laid(w, h, all: true);
        foreach (var dpi in new[] { 203, 300 })
        {
            using var bmp = BarcodeLabelService.GenerateLabelBitmap(Name, Code, Price, t, "ART-1", "шт", "Нур", dpi);
            bmp.Width.Should().Be((int)(w / 25.4 * dpi), $"{w} мм при {dpi} dpi");
            bmp.Height.Should().Be((int)(h / 25.4 * dpi));
            bmp.HorizontalResolution.Should().Be(96f, "шрифт в пунктах считается от 96 на дюйм, как в Skia, — не от масштаба экрана Windows");
            // Чернила (штрихи, цифры, текст) — только в своих блоках (+3 точки 203 dpi на сглаживание и цифры под штрихом).
            DarkOutside(bmp, t, dpi / 203f, 3f).Should().Be(0, $"{w}×{h} при {dpi} dpi: ничего не выходит за блоки этикетки");
        }
    }

    [Fact]
    public void PriceTagBitmap_TextDpiIsPinned()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var bmp = PriceTagService.GenerateBitmap(PriceTagKind.WithBarcode, new PriceTagData(Name, Code, Price), 58, 40);
        bmp.HorizontalResolution.Should().Be(96f);
        bmp.Width.Should().Be((int)(58 / 25.4 * 203));
    }

    [Fact]
    public void PrinterPage_SubtractsHardMargins_SoLabelStartsAtThePaperEdge()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var page = new Bitmap(600, 600);
        page.SetResolution(300, 300); // «принтер» 300 dpi
        using var square = new Bitmap(20, 20);
        using (var sg = Graphics.FromImage(square))
            sg.Clear(Color.Black);

        int FirstDarkX(int y)
        {
            for (var x = 0; x < page.Width; x++)
                if (page.GetPixel(x, y).R < 128)
                    return x;
            return -1;
        }

        using (var g = Graphics.FromImage(page))
        {
            g.Clear(Color.White);
            // 10 мм от края бумаги; печатная область начинается в 4,191 мм (16,5 сотых дюйма) — рисовать надо в 10 − 4,191 мм от неё.
            BarcodeLabelService.DrawOnPrinterPage(g, square, 10, 10, 300, 16.5f, 16.5f);
        }
        var expected = (int)Math.Round((10 / 25.4 - 0.165) * 300);
        FirstDarkX(expected + 5).Should().BeInRange(expected - 1, expected + 1);

        using (var g = Graphics.FromImage(page))
        {
            g.Clear(Color.White);
            BarcodeLabelService.DrawOnPrinterPage(g, square, 10, 10, 300); // без полей — как раньше
        }
        var plain = (int)Math.Round(10 / 25.4 * 300);
        FirstDarkX(plain + 5).Should().BeInRange(plain - 1, plain + 1);
    }

    [Fact]
    public void Editor_ResizingScalesLayout_AndBackRestoresIt()
    {
        var t = LabelTemplate.CreateDefault(); // 40×30
        t.ProductName.FontSizePx = 12;
        var original = t.Clone();

        LabelAutoLayout.ScaleToSize(t, 58, 40);
        t.WidthMm.Should().Be(58);
        t.HeightMm.Should().Be(40);
        t.Barcode.XMm.Should().BeApproximately(2 * 58 / 40.0, 1e-9);
        t.Barcode.WidthMm.Should().BeApproximately(36 * 58 / 40.0, 1e-9, "штрихкод растянулся по ширине новой этикетки");
        t.Price.YMm.Should().BeApproximately(24.5 * 40 / 30.0, 1e-9, "цена осталась внизу этикетки, а не в верхних 30 мм");
        t.ProductName.FontSizePx.Should().BeApproximately(12 * Math.Sqrt(58 / 40.0 * (40 / 30.0)), 1e-9);
        foreach (var e in Enabled(t))
        {
            (e.XMm + e.WidthMm).Should().BeLessThanOrEqualTo(58 + 1e-9);
            (e.YMm + e.HeightMm).Should().BeLessThanOrEqualTo(40 + 1e-9);
        }

        // Набор числа по цифрам (40 → 15 → 58 по ширине) даёт то же, что сразу 58; обратно 40×30 — исходная раскладка.
        var typed = original.Clone();
        LabelAutoLayout.ScaleToSize(typed, 15, 30);
        LabelAutoLayout.ScaleToSize(typed, 58, 30);
        LabelAutoLayout.ScaleToSize(typed, 58, 40);
        typed.Barcode.WidthMm.Should().BeApproximately(t.Barcode.WidthMm, 1e-9);
        typed.Price.YMm.Should().BeApproximately(t.Price.YMm, 1e-9);
        typed.ProductName.FontSizePx!.Value.Should().BeApproximately(t.ProductName.FontSizePx!.Value, 1e-9, "шрифт не уменьшается навсегда при наборе размера по цифрам");

        LabelAutoLayout.ScaleToSize(t, 40, 30);
        foreach (var (a, b) in new[] { (t.Barcode, original.Barcode), (t.ProductName, original.ProductName), (t.Price, original.Price) })
        {
            a.XMm.Should().BeApproximately(b.XMm, 1e-9);
            a.YMm.Should().BeApproximately(b.YMm, 1e-9);
            a.WidthMm.Should().BeApproximately(b.WidthMm, 1e-9);
            a.HeightMm.Should().BeApproximately(b.HeightMm, 1e-9);
        }
        t.ProductName.FontSizePx.Should().BeApproximately(12, 1e-9);
    }

    [Theory]
    [InlineData(58.0, 40.0)]
    [InlineData(30.0, 20.0)]
    [InlineData(100.0, 150.0)]
    public void Tspl_SizeAndBitmap_MatchTheLabel(double w, double h)
    {
        var t = Laid(w, h, all: false);
        using var sk = LabelSceneSkia.Render(BarcodeLabelService.BuildLabelScene(Name, Code, Price, t));
        var job = System.Text.Encoding.ASCII.GetString(BarcodeLabelService.BuildTsplJob(sk, w, h, 2, 0, 0, 1));
        job.Should().StartWith(string.Format(System.Globalization.CultureInfo.InvariantCulture, "SIZE {0:0.##} mm,{1:0.##} mm\r\n", w, h));
        var bitmapLine = job.Split("\r\n").First(l => l.StartsWith("BITMAP ", StringComparison.Ordinal));
        var parts = bitmapLine["BITMAP ".Length..].Split(',');
        var bytesPerRow = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        var rows = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
        bytesPerRow.Should().Be((sk.Width + 7) / 8);
        (bytesPerRow * 8).Should().BeLessThanOrEqualTo((int)Math.Ceiling(w / 25.4 * 203) + 7, "картинка не шире этикетки (203 dpi)");
        rows.Should().Be((int)(h / 25.4 * 203), "картинка ровно высотой этикетки");
    }
}
