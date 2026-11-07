using FluentAssertions;
using NurMarketKassa.Services;
using SkiaSharp;

namespace NurMarketKassa.Tests.Wave18;

/// <summary>2026-11-06 (В18, АЛ-5): печать A4 через CUPS на Linux/macOS — разбор очередей, проверка имени, аргументы <c>lp</c> без оболочки, PDF страниц-картинок.
/// Сам запуск lp на этом ПК не проверяется (нет CUPS, принтеры не трогаем): только то, что можно проверить без устройства.</summary>
public sealed class CupsPrintTests
{
    [Theory]
    [InlineData("HP_LaserJet", true)]
    [InlineData("Xprinter-XP-365B", true)]
    [InlineData("Brother_HL-L2350DW_series@host", true)]
    [InlineData("", false)]
    [InlineData("-o raw", false)]
    [InlineData("-evil", false)]
    [InlineData("a b", false)]
    [InlineData("x;rm -rf /", false)]
    [InlineData("q$(id)", false)]
    [InlineData("a/b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a#b", false)]
    public void QueueName_Validation_RejectsShellAndOptionInjection(string name, bool ok) =>
        CupsPrintService.IsValidQueueName(name).Should().Be(ok);

    [Theory]
    [InlineData("CUPS:HP_LaserJet", "HP_LaserJet")]
    [InlineData("cups:Office", "Office")]
    [InlineData("LP:Label1", "Label1")]
    [InlineData("/dev/usb/lp0", "")]
    [InlineData("TCP:192.168.1.50", "")]
    [InlineData("", "")]
    public void QueueName_FromPort(string port, string expected) => CupsPrintService.QueueName(port).Should().Be(expected);

    [Fact]
    public void ParseQueues_TakesOnlyValidNames_OnePerLine()
    {
        var output = "HP_LaserJet\r\nXprinter-XP-365B\n\n  Office  \nbad name\n-bad\n";
        CupsPrintService.ParseQueues(output).Should().Equal("HP_LaserJet", "Xprinter-XP-365B", "Office");
        CupsPrintService.ParseQueues(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("system default destination: HP_LaserJet\n", "HP_LaserJet")]
    [InlineData("системное назначение по умолчанию: Office\n", "Office")]
    [InlineData("no system default destination\n", null)]
    [InlineData("", null)]
    public void ParseDefaultQueue_ReadsLpstatD(string output, string? expected) => CupsPrintService.ParseDefaultQueue(output).Should().Be(expected);

    [Fact]
    public void LpArguments_AreAList_WithQueue_Copies_A4_NoScaling_AndFileAfterDoubleDash()
    {
        var args = CupsPrintService.BuildLpArguments("HP_LaserJet", "/tmp/nurcrm-print-abc.pdf", 3, "NurCRM labels");
        args.Should().ContainInOrder("-d", "HP_LaserJet", "-n", "3");
        args.Should().ContainInOrder("-t", "NurCRM labels");
        args.Should().ContainInOrder("-o", "media=A4");
        args.Should().ContainInOrder("-o", "print-scaling=none");
        args[^2].Should().Be("--");
        args[^1].Should().Be("/tmp/nurcrm-print-abc.pdf");
    }

    [Fact]
    public void LpArguments_ClampCopies_AndStripControlCharsFromTitle()
    {
        var args = CupsPrintService.BuildLpArguments("Q", "/tmp/f.pdf", 5000, "bad\r\ntitle\u0007");
        args.Should().ContainInOrder("-n", "99");
        args.Should().ContainInOrder("-t", "badtitle");
        CupsPrintService.BuildLpArguments("Q", "/tmp/f.pdf", -4, "").Should().ContainInOrder("-n", "1");
    }

    [Fact]
    public void PrintPdf_BadQueue_IsPrinterNotFound_WithoutStartingAnything()
    {
        CupsPrintService.PrintPdf("CUPS:", [1, 2, 3], 1, "t").Should().Be(LabelPrintResult.PrinterNotFound);
        CupsPrintService.PrintPdf("/dev/usb/lp0", [1, 2, 3], 1, "t").Should().Be(LabelPrintResult.PrinterNotFound);
        CupsPrintService.PrintPdf("CUPS:a;b", [1, 2, 3], 1, "t").Should().Be(LabelPrintResult.PrinterNotFound);
    }

    [Fact]
    public void PngPagesPdf_MakesOnePagePerImage_PortraitAndLandscape()
    {
        byte[] Png(int w, int h)
        {
            using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.White);
            using var p = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(10, 10, w / 2f, h / 4f, p);
            return SkiaRaster.ToPng(bmp);
        }
        var pdf = PngPagesPdf.Create([Png(1654, 2339), Png(2339, 1654), Png(1654, 2339)]);
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        text.Should().StartWith("%PDF-");
        System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count.Should().Be(3);
        text.Should().Contain("/MediaBox [0 0 842 595]", "альбомная страница A4").And.Contain("/MediaBox [0 0 595 842]");
    }
}
