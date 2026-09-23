using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-09-14: покрывает два живых бага, найденных сверкой с официальной
/// документацией NurCRM по весам (scales-and-plu.md): (1) единица суммы весового штрих-кода
/// (scale_barcode_amount_unit="som" vs "tiyin") нигде не учитывалась — сумма всегда считалась
/// тыйынами; (2) вес, восстановленный из суммового штрих-кода, вычислялся наивным делением
/// (Value/price), хотя весы обрезают дробную часть суммы при печати этикетки — наивное деление
/// систематически занижает вес почти на каждой продаже по суммовому штрих-коду.</summary>
public sealed class WeightBarcodeParserTests
{
    public WeightBarcodeParserTests()
    {
        // Статические Layout/Mode/AmountUnit — сброс к дефолтам перед каждым тестом, чтобы
        // тесты не зависели от порядка выполнения (WeightBarcodeParser — общий на процесс).
        WeightBarcodeParser.Layout = "plu";
        WeightBarcodeParser.Mode = "auto";
        WeightBarcodeParser.AmountUnit = "tiyin";
    }

    /// <summary>Строит валидный EAN-13 (та же контрольная цифра, что WeightBarcodeParser
    /// проверяет) из 12 цифр данных.</summary>
    private static string BuildBarcode(string first12)
    {
        first12.Length.Should().Be(12);
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var check = (10 - sum % 10) % 10;
        return first12 + check;
    }

    [Fact]
    public void TryParse_WeightMode_PluLayout_ParsesGramsToKg()
    {
        // prefix "20" + PLU "00042" + value "00540" (540 г)
        var barcode = BuildBarcode("200004200540");

        WeightBarcodeParser.TryParse(barcode, out var result).Should().BeTrue();
        result.ProductCode.Should().Be("00042");
        result.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        result.Value.Should().BeApproximately(0.540, 1e-9);
    }

    [Fact]
    public void TryParse_CodeLayout_SplitsSixPlusFourDigits()
    {
        WeightBarcodeParser.Layout = "code";
        // prefix "21" + Код "001000" + value "0145" (145 г) — тот же пример, что в документации.
        var barcode = BuildBarcode("210010000145");

        WeightBarcodeParser.TryParse(barcode, out var result).Should().BeTrue();
        result.ProductCode.Should().Be("001000");
        result.Value.Should().BeApproximately(0.145, 1e-9);
    }

    [Fact]
    public void TryParse_AmountMode_TiyinUnit_DividesByHundred()
    {
        WeightBarcodeParser.Mode = "amount";
        WeightBarcodeParser.AmountUnit = "tiyin";
        // prefix "25" + PLU "00001" + value "03800" (3800 тыйын = 38.00 сом)
        var barcode = BuildBarcode("250000103800");

        WeightBarcodeParser.TryParse(barcode, out var result).Should().BeTrue();
        result.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        result.Value.Should().BeApproximately(38.00, 1e-9);
    }

    [Fact]
    public void TryParse_AmountMode_SomUnit_DoesNotDivide()
    {
        // 2026-09-14: живой баг — раньше AmountUnit не существовало вовсе, сумма всегда делилась
        // на 100 даже для компаний с настройкой "som", давая сумму (и вес) в 100 раз меньше.
        WeightBarcodeParser.Mode = "amount";
        WeightBarcodeParser.AmountUnit = "som";
        // prefix "25" + PLU "00001" + value "00036" (36 сом, без деления)
        var barcode = BuildBarcode("250000100036");

        WeightBarcodeParser.TryParse(barcode, out var result).Should().BeTrue();
        result.Value.Should().BeApproximately(36.0, 1e-9);
    }

    [Fact]
    public void TryParse_AutoMode_Prefix25_ResolvesAsAmount()
    {
        WeightBarcodeParser.Mode = "auto";
        var barcode = BuildBarcode("250000100036");

        WeightBarcodeParser.TryParse(barcode, out var result).Should().BeTrue();
        result.Kind.Should().Be(WeightBarcodeValueKind.Amount);
    }

    [Fact]
    public void TryParse_AutoMode_Prefix20_ResolvesAsWeight()
    {
        WeightBarcodeParser.Mode = "auto";
        var barcode = BuildBarcode("200004200540");

        WeightBarcodeParser.TryParse(barcode, out var result).Should().BeTrue();
        result.Kind.Should().Be(WeightBarcodeValueKind.Weight);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1000004200540")] // не начинается с "2"
    [InlineData("2000042005401")] // валидная длина, но неверная контрольная цифра
    public void TryParse_ReturnsFalse_ForInvalidInput(string barcode)
    {
        WeightBarcodeParser.TryParse(barcode, out _).Should().BeFalse();
    }

    [Fact]
    public void ResolveWeightKg_WeightKind_ReturnsValueAsIs()
    {
        var result = new WeightBarcodeParseResult("00001", 0.340, WeightBarcodeValueKind.Weight);
        result.ResolveWeightKg(pricePerKg: 999).Should().BeApproximately(0.340, 1e-9);
    }

    /// <summary>Ровно пример из документации NurCRM (scales-and-plu.md, раздел про
    /// _weight_from_amount): бананы 540 сом/кг, взвешено 0.170 кг, точная сумма 91.80 сом,
    /// весы печатают усечённые "91" на этикетке. Наивное деление (91/540) даёт 0.168518.. →
    /// округлилось бы до 0.169 — недостача в 1 грамм на глазах у кассира. Сеточный алгоритм
    /// должен восстановить ровно 0.170.</summary>
    [Fact]
    public void ResolveWeightKg_AmountKind_ReconstructsExactWeight_MatchingDocumentedExample()
    {
        var result = new WeightBarcodeParseResult("00001", 91, WeightBarcodeValueKind.Amount);
        result.ResolveWeightKg(pricePerKg: 540).Should().BeApproximately(0.170, 1e-9);
    }

    [Theory]
    [InlineData(320, 75, 0.235)]  // 0.235 кг × 320 = 75.2 сом → этикетка "75" (обрезано)
    [InlineData(289, 119, 0.415)] // 0.415 кг × 289 = 119.935 сом → этикетка "119" (обрезано)
    public void ResolveWeightKg_AmountKind_ReconstructsExactWeight_ForOtherPricePoints(
        double pricePerKg, double amount, double expectedKg)
    {
        var result = new WeightBarcodeParseResult("00001", amount, WeightBarcodeValueKind.Amount);
        result.ResolveWeightKg(pricePerKg).Should().BeApproximately(expectedKg, 1e-9);
    }

    [Fact]
    public void ResolveWeightKg_AmountKind_ZeroOrNegativePrice_FallsBackToRawValue()
    {
        var result = new WeightBarcodeParseResult("00001", 91, WeightBarcodeValueKind.Amount);
        result.ResolveWeightKg(pricePerKg: 0).Should().Be(91);
    }
}
