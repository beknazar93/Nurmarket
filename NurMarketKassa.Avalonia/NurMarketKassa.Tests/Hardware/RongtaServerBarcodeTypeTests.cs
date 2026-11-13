using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-13 (руководство RLS1000, команда 0110 «PLU back stage sending»: поле «Barcode type, 2 цифры» сразу после «Art. No. (10)»):
/// способ «свой сервер» пишет в каждую запись тип штрихкода и отдел выбранной структуры; пока структура не выбиралась — 05 и 21, байт в байт как раньше.</summary>
public sealed class RongtaServerBarcodeTypeTests
{
    // Данные записи (после 4 цифр длины и 4 цифр команды): Operate 1, Rank 2, Name 36, LFCode 6, Code 10, Barcode type 2, Unit price 8, Weighing unit 1, Dept 2.
    private const int BarcodeTypeAt = 1 + 2 + 36 + 6 + 10;
    private const int PriceAt = BarcodeTypeAt + 2;
    private const int DeptAt = PriceAt + 8 + 1;

    [Fact]
    public void Default_Is05_Dept21_AsBefore()
    {
        var data = RongtaTcpProtocol.BuildPluRecord(5, "Мясо Барана", 1000)[8..];

        data.Length.Should().Be(100);
        data.Substring(BarcodeTypeAt, 2).Should().Be("05");
        data.Substring(DeptAt, 2).Should().Be("21");
        data.Substring(PriceAt, 8).Should().Be("00100000");
    }

    [Fact]
    public void Type07_Dept20_OnlyThoseFieldsChange()
    {
        var before = RongtaTcpProtocol.BuildPluRecord(5, "Мясо Барана", 1000)[8..];
        var after = RongtaTcpProtocol.BuildPluRecord(5, "Мясо Барана", 1000, '4', 2, barcodeType: 7, department: 20)[8..];

        after.Substring(BarcodeTypeAt, 2).Should().Be("07");
        after.Substring(DeptAt, 2).Should().Be("20");
        after.Length.Should().Be(100);
        Mask(after).Should().Be(Mask(before), "кроме типа и отдела запись не меняется");
    }

    [Theory]
    [InlineData(2, "02")]
    [InlineData(17, "17")]
    [InlineData(150, "99")]
    [InlineData(-3, "00")]
    public void BarcodeType_IsTwoDigits_Clamped(int type, string expected) =>
        RongtaTcpProtocol.BuildPluRecord(1, "Test", 10, barcodeType: type)[8..].Substring(BarcodeTypeAt, 2).Should().Be(expected);

    [Fact]
    public void ServerBarcode_NotChosen_Is05_21()
    {
        var prefs = new UserPreferences { RongtaServerBarcodeType = null, RongtaLanBarcodePrefix = 20 };

        RongtaTcpServerService.ServerBarcode(prefs).Should().Be((5, 21));
    }

    [Fact]
    public void ServerBarcode_Chosen07_UsesDepartmentFromSettings()
    {
        var prefs = new UserPreferences { RongtaServerBarcodeType = 7, RongtaLanBarcodePrefix = 20 };

        RongtaTcpServerService.ServerBarcode(prefs).Should().Be((7, 20));
    }

    private static string Mask(string data) =>
        data[..BarcodeTypeAt] + "##" + data[(BarcodeTypeAt + 2)..DeptAt] + "##" + data[(DeptAt + 2)..];
}
