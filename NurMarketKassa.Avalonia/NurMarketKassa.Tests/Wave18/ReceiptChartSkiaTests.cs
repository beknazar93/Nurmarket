using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using NurMarketKassa.Services;
using SkiaSharp;
using Xunit.Abstractions;

namespace NurMarketKassa.Tests.Wave18;

/// <summary>2026-11-06 (В18, АЛ-1, АЛ-3, А5-04): графический чек, графики отчётов и PDF чека на SkiaSharp (Linux/Android — System.Drawing там нет).
/// Без устройства: размеры, углы, текст на месте, одинаковые байты ESC * для Skia и GDI+, сравнение с GDI+ на Windows, PDF чека по числу страниц и ширине 80 мм.</summary>
public sealed class ReceiptChartSkiaTests
{
    private readonly ITestOutputHelper _out;

    public ReceiptChartSkiaTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] Lines =
    [
        "================================",
        "        МАГАЗИН «НУР»",
        "Чек №001601   05.11.2026 14:30",
        "--------------------------------",
        "Молоко Простоквашино 1 л",
        "      2 x 95,50            191,00",
        "Өтүк ңқғҳў тест",
        "      1 x 30,00             30,00",
        "ИТОГО: 221,00",
        "Спасибо за покупку!",
    ];

    private static GraphicReceiptSettings Settings() => new() { PaperWidthPixels = 384 };

    private static int Dark(SKBitmap b, int y0, int y1)
    {
        var n = 0;
        for (var y = Math.Max(y0, 0); y < Math.Min(y1, b.Height); y++)
            for (var x = 0; x < b.Width; x++)
                if (SkiaRaster.Luminance(b.GetPixel(x, y)) < 128)
                    n++;
        return n;
    }

    [Fact]
    public void ReceiptRaster_HasPaperWidth_TextInEveryLine_NoGrayAntialiasing()
    {
        using var bmp = GraphicReceiptSkia.RenderLines(Lines, Settings());
        bmp.Width.Should().Be(384);
        bmp.Height.Should().BeGreaterThan(Lines.Length * 8);
        bmp.GetPixel(0, 0).Should().Be(SKColors.White);
        bmp.GetPixel(383, bmp.Height - 1).Should().Be(SKColors.White);
        Dark(bmp, 0, bmp.Height).Should().BeGreaterThan(800);
        // Чёткий шрифт чека: только чёрные и белые точки.
        for (var y = 0; y < bmp.Height; y += 2)
            for (var x = 0; x < bmp.Width; x += 3)
            {
                var c = bmp.GetPixel(x, y);
                (c == SKColors.White || c == SKColors.Black).Should().BeTrue($"точка ({x},{y}) = {c} — без сглаживания");
            }
    }

    [Fact]
    public void ReceiptBytes_AreEscStarBands_WithInitAndCut()
    {
        var bytes = GraphicReceiptSkia.GenerateReceiptImage("Молоко 1 шт 95,50\nИТОГО: 95,50", Settings());
        bytes.Length.Should().BeGreaterThan(1000);
        bytes[0].Should().Be(0x1B);
        bytes[1].Should().Be(0x40, "ESC @ — сброс принтера в начале");
        // полоса растра: ESC * 33 nL nH
        var star = Array.FindIndex(bytes, i => i == 0x2A);
        star.Should().BeGreaterThan(0);
        bytes[star - 1].Should().Be(0x1B);
        bytes[star + 1].Should().Be(33);
        (bytes[star + 2] + bytes[star + 3] * 256).Should().Be(384);
    }

    [Fact]
    public void EscStar_SkiaAndGdi_GiveSameBytes_ForSamePixels()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare();

        [SupportedOSPlatform("windows")]
        static void Compare()
        {
            const int w = 50, h = 53; // высота не кратна 24: проверка последней неполной полосы
            using var sk = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using var gdi = new Bitmap(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var black = (x * 5 + y * 11) % 7 < 3 || x == y;
                    sk.SetPixel(x, y, black ? SKColors.Black : SKColors.White);
                    gdi.SetPixel(x, y, black ? Color.Black : Color.White);
                }
            var fromSkia = GraphicReceiptSkia.ConvertToEscPosRaster(sk);
            var fromGdi = GraphicReceiptSkia.EncodeEscStarBands(w, h, (x, y) =>
            {
                var p = gdi.GetPixel(x, y);
                return p.R + p.G + p.B < 384;
            });
            fromSkia.Should().Equal(fromGdi);
        }
    }

    [Fact]
    public void Receipt_Roughly_MatchesOldGdiRaster()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare(_out);

        [SupportedOSPlatform("windows")]
        static void Compare(ITestOutputHelper o)
        {
            using var sk = GraphicReceiptSkia.RenderLines(Lines, Settings());
            using var gdi = MonospaceReceiptRenderer.RenderLines(Lines, Settings());
            var dsk = Dark(sk, 0, sk.Height);
            var dg = 0;
            for (var y = 0; y < gdi.Height; y++)
                for (var x = 0; x < gdi.Width; x++)
                {
                    var c = gdi.GetPixel(x, y);
                    if (c.R + c.G + c.B < 384)
                        dg++;
                }
            LabelSkiaRasterTests.Diag(o, $"чек: Skia {sk.Width}x{sk.Height} чёрных {dsk}; GDI {gdi.Width}x{gdi.Height} чёрных {dg}");
            sk.Width.Should().Be(gdi.Width);
            Math.Abs(sk.Height - gdi.Height).Should().BeLessThanOrEqualTo(Math.Max(6, gdi.Height / 12), "высота ленты считается по шагу строк — почти та же");
            dsk.Should().BeGreaterThan((int)(dg * 0.6)).And.BeLessThan((int)(dg * 1.6));
        }
    }

    [Fact]
    public void Charts_AllKinds_ProducePng_WithContent_AndEmptyDataMessage()
    {
        var bars = new[] { ("Пн", 1200.0), ("Вт", 3400.0), ("Ср", 2800.0), ("Чт", 5600.0), ("Пт", 4100.0) };
        var pareto = new[] { ("Яблоки", 9000.0, 45.0), ("Груши", 6000.0, 75.0), ("Өрүк", 3000.0, 90.0), ("Сливы", 2000.0, 100.0) };
        var classic = new[] { ("Яблоки", 45.0, 45.0, "A"), ("Груши", 30.0, 75.0, "A"), ("Өрүк", 15.0, 90.0, "B"), ("Сливы", 10.0, 100.0, "C") };
        var pie = new[] { ("Наличные", 5000.0), ("Карта", 3000.0), ("QR", 1000.0) };
        var pngs = new Dictionary<string, byte[]>
        {
            ["bars"] = AnalyticsChartSkia.RenderBars("Выручка по дням", bars),
            ["pareto"] = AnalyticsChartSkia.RenderPareto("Парето", pareto, "Выручка"),
            ["classic"] = AnalyticsChartSkia.RenderParetoClassic("Парето ABC", classic),
            ["pie"] = AnalyticsChartSkia.RenderPie("Структура", pie),
            ["empty"] = AnalyticsChartSkia.RenderBars("Пусто", []),
        };
        foreach (var (name, png) in pngs)
        {
            using var bmp = SKBitmap.Decode(png);
            bmp.Should().NotBeNull(name);
            bmp.Width.Should().BeGreaterThan(1500, name + ": растр вдвое плотнее логических 900/980");
            bmp.GetPixel(2, 2).Should().Be(SKColors.White, name);
            var dark = Dark(bmp, 0, bmp.Height);
            dark.Should().BeGreaterThan(name == "empty" ? 400 : 5000, name + ": есть заголовок" + (name == "empty" ? "" : " и фигуры"));
            if (name != "empty")
            {
                // Цветные фигуры: хотя бы несколько тысяч не серых и не белых точек.
                var colored = 0;
                for (var y = 0; y < bmp.Height; y += 2)
                    for (var x = 0; x < bmp.Width; x += 2)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue)) > 60)
                            colored++;
                    }
                colored.Should().BeGreaterThan(1500, name + ": цветные столбцы или секторы");
            }
        }
    }

    [Fact]
    public void Charts_Dispatch_ToSkia_WhenNotWindows_AndSizeMatchesGdiOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Compare(_out);

        [SupportedOSPlatform("windows")]
        static void Compare(ITestOutputHelper o)
        {
            var bars = new[] { ("Пн", 1200.0), ("Вт", 3400.0), ("Ср", 2800.0), ("Чт", 5600.0), ("Пт", 4100.0) };
            var gdi = AnalyticsChartRenderer.RenderBars("Выручка по дням", bars);
            var skia = AnalyticsChartSkia.RenderBars("Выручка по дням", bars);
            using var a = SKBitmap.Decode(gdi);
            using var b = SKBitmap.Decode(skia);
            a.Width.Should().Be(b.Width);
            a.Height.Should().Be(b.Height);
            int da = Dark(a, 0, a.Height), db = Dark(b, 0, b.Height);
            // Цветные пиксели (столбцы) — почти поровну.
            int Colored(SKBitmap m)
            {
                var n = 0;
                for (var y = 0; y < m.Height; y++)
                    for (var x = 0; x < m.Width; x++)
                    {
                        var c = m.GetPixel(x, y);
                        if (c.Blue > 180 && c.Red < 90 && c.Green < 140)
                            n++;
                    }
                return n;
            }
            int ca = Colored(a), cb = Colored(b);
            LabelSkiaRasterTests.Diag(o, $"график столбцы: GDI тёмных {da} синих {ca}; Skia тёмных {db} синих {cb}");
            cb.Should().BeGreaterThan((int)(ca * 0.9)).And.BeLessThan((int)(ca * 1.1), "столбцы одного цвета и размера");
            db.Should().BeGreaterThan((int)(da * 0.6)).And.BeLessThan((int)(da * 1.6));
        }
    }

    [Fact]
    public void ReceiptPdf_Skia_IsOnePage_80mmWide_AndGrowsWithItems()
    {
        var small = Card(2);
        var big = Card(40);
        var pdfSmall = ReceiptSkiaPdf.Render(small);
        var pdfBig = ReceiptSkiaPdf.Render(big);
        Encoding.ASCII.GetString(pdfSmall, 0, 5).Should().Be("%PDF-");
        var textSmall = Encoding.Latin1.GetString(pdfSmall);
        System.Text.RegularExpressions.Regex.Matches(textSmall, @"/Type\s*/Page\b").Count.Should().Be(1);
        // MediaBox: ширина 80 мм = 226.77 pt.
        var m = System.Text.RegularExpressions.Regex.Match(textSmall, @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]");
        m.Success.Should().BeTrue();
        double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(226.77, 0.5);
        var hSmall = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var mBig = System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(pdfBig), @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]");
        double.Parse(mBig.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(hSmall + 300, "лента растёт с числом позиций");
        pdfBig.Length.Should().BeGreaterThan(pdfSmall.Length);
    }

    [Fact]
    public void ReceiptPdf_Create_WritesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "nurmarket-wave18-receipt-" + Guid.NewGuid().ToString("N")[..8], "чек.pdf");
        try
        {
            ReceiptSkiaPdf.Create(Card(3), path);
            new FileInfo(path).Length.Should().BeGreaterThan(1500);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static SaleReceiptCardData Card(int lines)
    {
        var d = new SaleReceiptCardData
        {
            Shop = "Магазин «Нур»", Inn = "1234567890", Address = "г. Бишкек, ул. Токтогула 1", Number = "1601", Shift = "12",
            DateTimeText = "05.11.2026 14:30", Cashier = "Айгуль К.", Cashbox = "Касса 1", Client = "Өмүрбек Ү.", Title = "ТОВАРНЫЙ ЧЕК",
            TotalLabel = "ИТОГО", TotalText = "221 сом", OfflineNote = "Сохранён без связи", QrUrl = "https://nurcrm.kg/r/abc123",
            ReturnsCaption = "Возвраты",
        };
        for (var i = 1; i <= lines; i++)
            d.Lines.Add(new SaleReceiptCardData.Line("Молоко Простоквашино 1 л, пастеризованное, позиция " + i, "2 шт × 95,50", "191", i % 3 == 0 ? "скидка −10" : null));
        d.BeforeTotal.Add(new SaleReceiptCardData.Row("Сумма", "230"));
        d.BeforeTotal.Add(new SaleReceiptCardData.Row("Скидка", "−9", 2));
        d.Payment.Add(new SaleReceiptCardData.Row("Оплата", "Наличные"));
        d.Payment.Add(new SaleReceiptCardData.Row("Сдача", "29", 3));
        d.Returns.Add(new SaleReceiptCardData.Row("Молоко", "−95,50"));
        return d;
    }
}
