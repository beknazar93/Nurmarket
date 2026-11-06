using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-06 (живой прогон 3, P3-51/P3-52): «Создать QR» на пустой строке не должен говорить «Коды есть у всех размеров», а «Этикетки» размеров
/// перед отправкой спрашивают «сколько, какие размеры, на какой принтер» и узнают чековый принтер (POS-80 / тот же порт, что у чеков).</summary>
public sealed class SizeLabelPrintGuardTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NothingCreated_ForRowsWithoutSize_AsksForSize_NotAllHaveCodes(bool qr)
    {
        var text = SizeCodeHints.NothingCreated(qr, sizedRows: 0);
        text.Should().Be(SizeCodeHints.NeedSize());
        text.Should().NotContain("есть у всех");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NothingCreated_WhenEverySizedRowHasCode_SaysAllHaveCodes(bool qr)
    {
        var text = SizeCodeHints.NothingCreated(qr, sizedRows: 3);
        text.Should().Contain("есть у всех");
        text.Should().NotBe(SizeCodeHints.NeedSize());
    }

    [Fact]
    public void SkippedNote_OnlyWhenRowsWithoutSizeWereSkipped()
    {
        SizeCodeHints.SkippedNote(0).Should().BeEmpty();
        SizeCodeHints.SkippedNote(2).Should().Contain("2").And.Contain("размер");
    }

    [Theory]
    [InlineData("POS-80", true)]
    [InlineData("POS80 Printer", true)]
    [InlineData("Xprinter XP-58 (POS-58)", true)]
    [InlineData("Receipt Printer", true)]
    [InlineData("Чековый принтер", true)]
    [InlineData("TSC TE244", false)]
    [InlineData("Xprinter XP-365B", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NameLooksLikeReceiptPrinter_RecognisesReceiptNames(string? name, bool expected) =>
        LabelPrinterSelector.NameLooksLikeReceiptPrinter(name).Should().Be(expected);

    [Fact]
    public void LabelPrinterOnTheSamePortAsReceiptPrinter_IsReceiptPrinter()
    {
        LabelPrinterSelector.LooksLikeReceiptPrinter("COM3", receiptPrinterPath: "COM3").Should().BeTrue();
        LabelPrinterSelector.LooksLikeReceiptPrinter("TSC TE244", receiptPrinterPath: "COM3").Should().BeFalse();
    }

    [Fact]
    public void PrintConfirmQuestion_NamesCount_Sizes_AndPrinter()
    {
        var text = LabelPrinterSelector.PrintConfirmQuestion(3, "S, Красный; M, Красный", "TSC TE244");
        text.Should().Contain("3").And.Contain("S, Красный; M, Красный").And.Contain("«TSC TE244»");
        LabelPrinterSelector.PrintConfirmQuestion(1, "", "POS-80").Should().NotContain("размеры").And.Contain("«POS-80»");
        LabelPrinterSelector.ReceiptPrinterWarning.Should().Contain("чековый принтер");
    }
}
