using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06, жалоба владельца «у Rongta не видит суммы больше 999»: тип штрихкода 02 («отдел + код 5 + сумма 5 в сотых») вмещает не больше 999,99 сом; большая сумма
/// печатается обрезанной (старшие цифры теряются). Проверяем потолки по структурам, рекомендацию «по весу», предупреждение по каталогу и сообщение кассы при скане «все девятки».</summary>
[Collection("UserPreferences")]
public sealed class RongtaBarcodeCeilingTests : IDisposable
{
    public RongtaBarcodeCeilingTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        WeightBarcodeParser.Layout = "plu";
        WeightBarcodeParser.Mode = "auto";
        WeightBarcodeParser.AmountUnit = "tiyin";
        WeightBarcodeParser.AmountPrefixes = new HashSet<string>();
        WeightBarcodeParser.WeightPrefixes = new HashSet<string>();
        WeightBarcodeParser.CustomLayouts = Array.Empty<WeightBarcodeCustomLayout>();
    }

    private static RongtaBarcodeFormat.BarcodeType Type(int n) => RongtaBarcodeFormat.Find(n)!;

    [Theory]
    [InlineData(1, 99.99)]
    [InlineData(2, 999.99)]       // тип владельца: 20 34567 00290 8 — «Не видит суммы больше 999»
    [InlineData(3, 9999.99)]
    [InlineData(4, 99999.99)]
    [InlineData(12, 999.99)]      // тип 12 — префикс 22 фиксирован, код 5, сумма 5
    [InlineData(22, 999.99)]      // отдел из 1 цифры, код 6, сумма 5
    [InlineData(5, 0)]            // по весу — суммы нет
    [InlineData(7, 0)]
    [InlineData(0, 0)]
    public void MaxAmountSom_ByStructure(int type, double expected) =>
        RongtaBarcodeFormat.MaxAmountSom(Type(type)).Should().Be((decimal)expected);

    [Theory]
    [InlineData(5, 9.999)]        // W.WWW
    [InlineData(6, 99.99)]        // WW.WW
    [InlineData(7, 99.999)]       // WW.WWW
    [InlineData(8, 9999.9)]       // WWWW.W
    [InlineData(9, 0)]            // WWWWW — единица зависит от весов
    [InlineData(2, 0)]
    public void MaxWeightKg_ByStructure(int type, double expected) =>
        RongtaBarcodeFormat.MaxWeightKg(Type(type)).Should().Be((decimal)expected);

    [Fact]
    public void Recommended_ForType02_IsWeightType07_WithSameCodeDigitsAndPrefixKind()
    {
        var current = Type(2);

        var recommended = RongtaBarcodeFormat.RecommendedWeightType(current);

        recommended.Should().NotBeNull();
        recommended!.Type.Should().Be(7);
        recommended.Value.Should().Be(RongtaBarcodeFormat.ValueKind.Weight);
        recommended.CodeDigits.Should().Be(current.CodeDigits, "коды товаров на этикетках не меняются");
        recommended.Prefix.Should().Be(current.Prefix);
        recommended.GramsPerUnit.Should().Be(1);
        RongtaBarcodeFormat.MaxAmountSom(recommended).Should().Be(0m, "у структуры по весу нет предела суммы");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(1)]
    public void Recommended_IsAlwaysWeightInGrams_NeverOneDigitDepartment(int type)
    {
        var recommended = RongtaBarcodeFormat.RecommendedWeightType(Type(type));

        recommended.Should().NotBeNull();
        recommended!.Value.Should().Be(RongtaBarcodeFormat.ValueKind.Weight);
        recommended.GramsPerUnit.Should().Be(1);
        recommended.Prefix.Should().NotBe(RongtaBarcodeFormat.PrefixKind.Department1);
    }

    [Fact]
    public void CheckCatalog_CountsProductsAboveCeiling()
    {
        var prices = new[] { 100.0, 500.0, 1250.0, 20000.0, 0.0 };

        var report = RongtaBarcodeFormat.CheckCatalog(Type(2), prices);

        report.MaxAmount.Should().Be(999.99m);
        report.Products.Should().Be(4, "цена 0 не считается");
        report.OverAtOneKg.Should().Be(2, "1250 и 20000 не влезают даже при 1 кг");
        report.OverAtFiveKg.Should().Be(3, "500 × 5 кг = 2500 тоже не влезает");
        report.MaxPrice.Should().Be(20000m);
        report.HasProblem.Should().BeTrue();
        RongtaBarcodeFormat.CatalogWarning(report).Should().NotBeNullOrWhiteSpace().And.Contain("2");
    }

    [Fact]
    public void CheckCatalog_WeightStructure_NoProblem()
    {
        var report = RongtaBarcodeFormat.CheckCatalog(Type(7), new[] { 100.0, 1250.0, 20000.0 });

        report.HasProblem.Should().BeFalse();
        RongtaBarcodeFormat.CatalogWarning(report).Should().BeEmpty();
        RongtaBarcodeFormat.CeilingText(Type(7)).Should().NotBeNullOrWhiteSpace().And.Contain("99");
        RongtaBarcodeFormat.CeilingText(Type(0)).Should().BeEmpty();
    }

    [Fact]
    public void CheckCatalog_SmallPrices_Fit()
    {
        RongtaBarcodeFormat.CheckCatalog(Type(2), new[] { 50.0, 150.0, 199.0 }).HasProblem.Should().BeFalse();
    }

    [Fact]
    public void CeilingText_Type02_NamesTheLimit() =>
        RongtaBarcodeFormat.CeilingText(Type(2)).Should().Contain("999");

    // ---- что происходит с суммой выше потолка и как её читает касса

    [Fact]
    public void BuildSample_Type02_Amount999_99_FitsExactly()
    {
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, 999.99m)!;

        sample.Substring(2, 5).Should().Be("34567");
        sample.Substring(7, 5).Should().Be("99999");
    }

    [Fact]
    public void BuildSample_Type02_Amount1250_IsTruncated_NotRaised()
    {
        // 1250,00 = 125000 в сотых: в 5 цифр помещаются только младшие 25000 — этикетка скажет 250,00 вместо 1250,00 (корень жалобы).
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, 1250m)!;

        sample.Substring(7, 5).Should().Be("25000");
    }

    [Fact]
    public void Kassa_ReadsType02_Amount999_99_AndFlagsFullField()
    {
        WeightBarcodeParser.AmountPrefixes = new HashSet<string> { "20" };
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, 999.99m)!;

        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();

        parsed.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        parsed.Value.Should().BeApproximately(999.99, 0.001);
        WeightBarcodeParser.IsAmountFieldFull(sample).Should().BeTrue("все девять в поле суммы — настоящая сумма могла не поместиться");
        RongtaBarcodeFormat.AmountFieldFullMessage(parsed.Value).Should().Contain("999,99");
    }

    [Fact]
    public void Kassa_ReadsType02_NormalAmount_NoWarning()
    {
        WeightBarcodeParser.AmountPrefixes = new HashSet<string> { "20" };
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, 290m)!;

        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();

        parsed.Value.Should().BeApproximately(290.0, 0.001);
        WeightBarcodeParser.IsAmountFieldFull(sample).Should().BeFalse();
    }

    [Fact]
    public void Kassa_Type02_Amount1250_TruncatedLabelLooksNormal_ButNotFull()
    {
        // Обрезанная сумма (250,00) неотличима от настоящей — поэтому единственная защита: структура по весу или предупреждение в настройках по каталогу.
        WeightBarcodeParser.AmountPrefixes = new HashSet<string> { "20" };
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, 1250m)!;

        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();

        parsed.Value.Should().BeApproximately(250.0, 0.001);
        WeightBarcodeParser.IsAmountFieldFull(sample).Should().BeFalse();
    }

    [Fact]
    public void Kassa_ReadsWeightType07_3_250Kg_NoAmountLimit()
    {
        var sample = RongtaBarcodeFormat.BuildSample(Type(7), 20, 34567, 3250, 0m)!;

        sample.Substring(7, 5).Should().Be("03250");
        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        parsed.Value.Should().BeApproximately(3.250, 0.0001);
        WeightBarcodeParser.IsAmountFieldFull(sample).Should().BeFalse("по весу предупреждение не нужно");
    }

    [Fact]
    public void Kassa_ReadsSixDigitAmount_1250_00_ByCustomLayout()
    {
        // Тип 03 («отдел + код 4 + сумма 6»): 1250,00 помещается (потолок 9999,99). Касса читает такую раскладку, если задан её пользовательский разбор (2 + 4 + 6 + КС).
        var type03 = Type(3);
        var sample = RongtaBarcodeFormat.BuildSample(type03, 20, 4567, 0, 1250m)!;
        var layout = new WeightBarcodeCustomLayout("20", 4, true, WeightBarcodeValueKind.Amount);

        sample.Substring(6, 6).Should().Be("125000");
        WeightBarcodeParser.TryParseWithLayout(sample, layout, out var parsed).Should().BeTrue();

        parsed.Value.Should().BeApproximately(1250.0, 0.001);
        parsed.ProductCode.Should().Be("4567");
        RongtaBarcodeFormat.MaxAmountSom(type03).Should().Be(9999.99m);
    }

    [Fact]
    public void IsAmountFieldFull_CodeLayout_UsesFourDigits()
    {
        WeightBarcodeParser.Layout = "code";
        WeightBarcodeParser.AmountPrefixes = new HashSet<string> { "20" };
        var full = Ean13("200012349999");
        var notFull = Ean13("200012349990");

        WeightBarcodeParser.IsAmountFieldFull(full).Should().BeTrue();
        WeightBarcodeParser.IsAmountFieldFull(notFull).Should().BeFalse();
        WeightBarcodeParser.IsAmountFieldFull("123").Should().BeFalse();
    }

    private static string Ean13(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + (10 - sum % 10) % 10;
    }
}
