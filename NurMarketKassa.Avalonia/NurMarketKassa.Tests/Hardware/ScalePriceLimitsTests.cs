using FluentAssertions;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06, жалоба владельца «дальше 9 тысяч цена не отображается» (Rongta и Штрих-М): цена выше предела весов
/// не должна молча обрезаться до 9999,99 — вызывающий получает явную ошибку/признак «не помещается».</summary>
public sealed class ScalePriceLimitsTests
{
    // ---- Штрих-ПРИНТ (МДЕ 0..999999)
    [Fact]
    public void ShtrikhPrint_PriceToMde_DoesNotClampSilently()
    {
        // 12 500 сом при 2 знаках = 1 250 000 МДЕ: больше предела — раньше возвращалось 999999 (на весах 9999,99).
        ShtrikhPrintProtocol.PriceToMde(12_500m, 2).Should().Be(1_250_000);
        ShtrikhPrintProtocol.PriceToMde(12_500m, 2).Should().BeGreaterThan(ShtrikhPrintProtocol.MaxPriceMde);
    }

    [Fact]
    public void ShtrikhPrint_Boundaries()
    {
        ShtrikhPrintProtocol.PriceToMde(9_999.99m, 2).Should().Be(999_999);
        ShtrikhPrintProtocol.PriceToMde(10_000m, 2).Should().BeGreaterThan(ShtrikhPrintProtocol.MaxPriceMde);
        ShtrikhPrintProtocol.PriceToMde(12_500m, 0).Should().Be(12_500);       // при 0 знаков помещается
        ShtrikhPrintProtocol.PriceToMde(999_999m, 0).Should().Be(999_999);
        ShtrikhPrintProtocol.PriceToMde(1_000_000m, 0).Should().BeGreaterThan(ShtrikhPrintProtocol.MaxPriceMde);
        ShtrikhPrintProtocol.PriceToMde(0.005m, 2).Should().Be(1);              // округление от нуля
        ShtrikhPrintProtocol.PriceToMde(-5m, 2).Should().Be(0);
    }

    [Fact]
    public void ShtrikhPrint_CreateRecord_KeepsRealPriceForValidation()
    {
        var record = ShtrikhPrintLanScaleService.CreateRecord(1, 1, "Сыр", 12_500m, 2);
        record.PriceMde.Should().Be(1_250_000);   // не 999999: загрузка отклонит такую запись явной ошибкой
    }

    [Theory]
    [InlineData(2, 9_999.99, true)]
    [InlineData(2, 12_500, false)]
    [InlineData(0, 12_500, true)]
    [InlineData(1, 12_500, true)]   // у Штрих-ПРИНТ знаков 0 или 2 (1 → как 0), но у Dahua 1 знак даёт 99 999,9
    public void ScalePriceLimits_Fits(int decimals, double price, bool expected)
    {
        ScalePriceLimits.Fits((decimal)price, ScalePriceLimits.DahuaMaxUnits, decimals).Should().Be(expected);
    }

    [Fact]
    public void ScalePriceLimits_MaxPrice_PerBrandAndDecimals()
    {
        ScalePriceLimits.MaxPriceSom(ScalePriceLimits.DahuaMaxUnits, 2).Should().Be(9_999.99m);
        ScalePriceLimits.MaxPriceSom(ScalePriceLimits.DahuaMaxUnits, 1).Should().Be(99_999.9m);
        ScalePriceLimits.MaxPriceSom(ScalePriceLimits.DahuaMaxUnits, 0).Should().Be(999_999m);
        ScalePriceLimits.MaxPriceSom(ScalePriceLimits.DahuaMaxUnits, 3).Should().Be(999.999m);
        ScalePriceLimits.MaxPriceSom(ScalePriceLimits.ShtrikhPcMaxUnits, 2).Should().Be(99_999.99m);
        ScalePriceLimits.MaxPriceSom(ScalePriceLimits.ShtrikhPcMaxUnits, 0).Should().Be(9_999_999m);
        ScalePriceLimits.MaxPriceSom(ScalePriceLimits.RongtaRlsMaxUnits, 2).Should().Be(999_999.99m);
        ScalePriceLimits.ShtrikhPrintDigits(1).Should().Be(0);
        ScalePriceLimits.ShtrikhPrintDigits(2).Should().Be(2);
    }

    [Fact]
    public void ScalePriceLimits_Text_NamesLimitAndDecimals()
    {
        var text = ScalePriceLimits.DoesNotFitText(12_500m, ScalePriceLimits.DahuaMaxUnits, 2);
        text.Should().Contain("12").And.Contain("9").And.Contain("2");
        text.Should().NotBeNullOrWhiteSpace();
    }

    // ---- Dahua / Rongta напрямую / TM-30F (поле mprice — 6 цифр)
    [Fact]
    public void Dahua_Price12500_At2Decimals_IsRejectedNotTruncated()
    {
        var plu = new DahuaTmPlu { PluNumber = 1, ProductCode = 1, Price = 12_500m, Name = "Сыр" };
        DahuaTmProtocol.Validate(plu, 2).Should().Be(DahuaTmPluProblem.PriceDoesNotFit);
        var act = () => DahuaTmProtocol.BuildPluCommand(plu, 2);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dahua_Price9999_99_PassedAsIs_AndBigPriceFitsWithFewerDecimals()
    {
        var edge = new DahuaTmPlu { PluNumber = 1, ProductCode = 1, Price = 9_999.99m, Name = "Сыр" };
        DahuaTmProtocol.Validate(edge, 2).Should().Be(DahuaTmPluProblem.None);
        DahuaTmProtocol.BuildPluCommand(edge, 2).Substring(15, 6).Should().Be("999999");

        var big = new DahuaTmPlu { PluNumber = 1, ProductCode = 1, Price = 12_500m, Name = "Сыр" };
        DahuaTmProtocol.Validate(big, 0).Should().Be(DahuaTmPluProblem.None);
        DahuaTmProtocol.BuildPluCommand(big, 0).Substring(15, 6).Should().Be("012500");
        DahuaTmProtocol.BuildPluCommand(big, 1).Substring(15, 6).Should().Be("125000");
    }

    [Fact]
    public void Dahua_Boundary999999Units_Fits_And1000000_DoesNot()
    {
        var ok = new DahuaTmPlu { PluNumber = 1, ProductCode = 1, Price = 999_999m, Name = "A" };
        DahuaTmProtocol.Validate(ok, 0).Should().Be(DahuaTmPluProblem.None);
        var over = new DahuaTmPlu { PluNumber = 1, ProductCode = 1, Price = 1_000_000m, Name = "A" };
        DahuaTmProtocol.Validate(over, 0).Should().Be(DahuaTmPluProblem.PriceDoesNotFit);
        var rounding = new DahuaTmPlu { PluNumber = 1, ProductCode = 1, Price = 9_999.995m, Name = "A" };   // округляется до 10 000,00 → не помещается
        DahuaTmProtocol.Validate(rounding, 2).Should().Be(DahuaTmPluProblem.PriceDoesNotFit);
    }

    // ---- Штрих РС-200 (поле 5 БД: 7 знаков вместе с дробной частью)
    [Fact]
    public void ShtrikhPc_Price12500_Fits_At2Decimals_And_Over_Returns_Null()
    {
        ShtrikhPcImportWriter.FormatPrice(12_500m, 2, ".").Should().Be("12500.00");
        ShtrikhPcImportWriter.FormatPrice(99_999.99m, 2, ".").Should().Be("99999.99");
        ShtrikhPcImportWriter.FormatPrice(100_000m, 2, ".").Should().BeNull();
        ShtrikhPcImportWriter.FormatPrice(9_999.999m, 3, ".").Should().Be("9999.999");
        ShtrikhPcImportWriter.FormatPrice(12_500m, 3, ".").Should().BeNull();   // при 3 знаках предел 9999,999
        ShtrikhPcImportWriter.FormatPrice(9_999_999m, 0, ".").Should().Be("9999999");
    }

    // ---- Rongta RLS1000 по TCP (Unit price — 8 цифр)
    [Fact]
    public void RongtaTcp_Price12500_IsEncodedFully()
    {
        var packet = RongtaTcpProtocol.BuildPluRecord(1, "Test", 12_500);
        packet[8..].Substring(57, 8).Should().Be("01250000");
        RongtaTcpProtocol.BuildPluRecord(1, "Test", 9_999.99)[8..].Substring(57, 8).Should().Be("00999999");
        RongtaTcpProtocol.BuildPluRecord(1, "Test", 999_999.99)[8..].Substring(57, 8).Should().Be("99999999");
    }

    [Fact]
    public void RongtaTcp_PriceAboveField_ThrowsInsteadOfWrapping()
    {
        var act = () => RongtaTcpProtocol.BuildPluRecord(1, "Test", 1_000_000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- штрих-код с суммой: предел суммы в самом штрих-коде
    [Theory]
    [InlineData(3, 0, 9_999)]      // ППТТТТТТССССК, сом
    [InlineData(4, 0, 99_999)]     // ППТТТТТССССС К, сом
    [InlineData(3, 2, 99.99)]      // тыйыны
    [InlineData(4, 2, 999.99)]
    [InlineData(7, 2, 0)]          // структура по массе — суммы нет
    public void ShtrikhBarcode_MaxAmount(int structure, int digits, double expected)
    {
        ShtrikhBarcodeFormat.MaxAmountSom(structure, digits).Should().Be((decimal)expected);
    }
}
