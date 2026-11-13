using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06, жалоба владельца «у Rongta не видит суммы больше 999»: тип штрихкода 02 («отдел + код 5 + сумма 5 в сотых») вмещает не больше 999,99 сом; большая сумма
/// не помещается (2026-11-12: по этикетке клиента весы печатают её БЕЗ штрихкода, а не обрезанной, как считали 06.11). Проверяем потолки по структурам, рекомендацию «по весу», предупреждение по каталогу и сообщение кассы при скане «все девятки».</summary>
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
    public void BuildSample_Type02_Amount1250_NoBarcode_NeverTruncated()
    {
        // 2026-11-12: 1250,00 = 125000 в сотых — в 5 цифр не помещается. Раньше пример давал обрезанное «25000» (ложные 250,00); по этикетке клиента весы такой
        // штрихкод не печатают вовсе — пример тоже не строится.
        RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, 1250m).Should().BeNull();
        RongtaBarcodeFormat.AmountFits(Type(2), 1250m).Should().BeFalse();
    }

    // ---- 2026-11-12: этикетка клиента «Бука лакым» 1,150 кг × 1100,00 = 1265,00 — без штрихкода. Граница и обратный разбор.

    [Theory]
    [InlineData(999.99, true)]
    [InlineData(1000.00, false)]   // 100000 сотых — 6 цифр в поле из 5: граница пропажи штрихкода
    [InlineData(1265.00, false)]   // этикетка с фото
    [InlineData(9999.99, false)]
    [InlineData(19999.99, false)]
    public void Type02_PricePoint2_BarcodeOnlyUpTo999_99(double amount, bool fits)
    {
        var a = (decimal)amount;

        RongtaBarcodeFormat.AmountFits(Type(2), a).Should().Be(fits);
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, a);
        if (fits)
            sample.Should().NotBeNull().And.HaveLength(13);
        else
            sample.Should().BeNull("весы не печатают штрихкод с суммой, которая не помещается, — и касса не строит ложный");
    }

    [Theory]
    [InlineData(999.99)]
    [InlineData(1000.00)]
    [InlineData(1265.00)]
    [InlineData(9999.99)]
    public void Type03_SixDigitAmount_RoundTrip_ByCustomLayout(double amount)
    {
        var a = (decimal)amount;
        var sample = RongtaBarcodeFormat.BuildSample(Type(3), 20, 4567, 0, a)!;
        var layout = new WeightBarcodeCustomLayout("20", 4, true, WeightBarcodeValueKind.Amount);

        WeightBarcodeParser.TryParseWithLayout(sample, layout, out var parsed).Should().BeTrue();

        parsed.Value.Should().BeApproximately(amount, 0.001);
        parsed.ProductCode.Should().Be("4567");
    }

    [Fact]
    public void Type03_19999_99_DoesNotFit_Type04_Fits_RoundTrip()
    {
        RongtaBarcodeFormat.BuildSample(Type(3), 20, 4567, 0, 19999.99m).Should().BeNull("потолок типа 03 — 9999,99");

        var sample = RongtaBarcodeFormat.BuildSample(Type(4), 20, 456, 0, 19999.99m)!;
        var layout = new WeightBarcodeCustomLayout("20", 3, true, WeightBarcodeValueKind.Amount);
        WeightBarcodeParser.TryParseWithLayout(sample, layout, out var parsed).Should().BeTrue();
        parsed.Value.Should().BeApproximately(19999.99, 0.001);
    }

    [Theory]
    [InlineData(0, 99999.0)]
    [InlineData(1, 9999.9)]
    [InlineData(2, 999.99)]
    public void Type02_MaxAmount_DependsOnPricePoint(int pricePoint, double expected) =>
        RongtaBarcodeFormat.MaxAmountSom(Type(2), pricePoint).Should().Be((decimal)expected);

    [Theory]
    [InlineData(1265.00)]
    [InlineData(9999.00)]
    [InlineData(19999.00)]
    public void Type02_PricePoint0_AmountInSom_RoundTrip(double amount)
    {
        // Price point 0 на весах (цена целыми сомами) — сумма в штрихкоде целыми сомами, касса читает с единицей «сом».
        WeightBarcodeParser.AmountPrefixes = new HashSet<string> { "20" };
        WeightBarcodeParser.AmountUnit = "som";
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, (decimal)amount, pricePoint: 0)!;

        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();

        parsed.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        parsed.Value.Should().BeApproximately(amount, 0.001);
        parsed.ProductCode.Should().Be("34567");
    }

    [Fact]
    public void Type02_PricePoint2_TiyinUnit_RoundTrip_999_99()
    {
        WeightBarcodeParser.AmountPrefixes = new HashSet<string> { "20" };
        WeightBarcodeParser.AmountUnit = "tiyin";
        var sample = RongtaBarcodeFormat.BuildSample(Type(2), 20, 34567, 0, 999.99m, pricePoint: 2)!;

        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();
        parsed.Value.Should().BeApproximately(999.99, 0.001);
    }

    [Fact]
    public void WeightType07_PhotoLabel_1150g_At1100_KassaGets1265()
    {
        // Структура по весу (07) — штрихкод печатается при любой сумме: касса читает вес и сама считает сумму по цене товара.
        var sample = RongtaBarcodeFormat.BuildSample(Type(7), 20, 34567, 1150, 1265m)!;

        sample.Substring(7, 5).Should().Be("01150");
        WeightBarcodeParser.TryParse(sample, out var parsed).Should().BeTrue();
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        Math.Round(parsed.Value * 1100.0, 2).Should().Be(1265.00);
        RongtaBarcodeFormat.AmountFits(Type(7), 1265m).Should().BeTrue();
    }

    [Fact]
    public void MaxWeightWithBarcode_PhotoProduct_1100PerKg_Is0_909Kg()
    {
        RongtaBarcodeFormat.MaxWeightKgWithBarcode(Type(2), 1100m).Should().Be(0.909m);
        RongtaBarcodeFormat.AmountFits(Type(2), 1100m * 0.909m).Should().BeTrue();
        RongtaBarcodeFormat.AmountFits(Type(2), 1100m * 0.910m).Should().BeFalse();
        RongtaBarcodeFormat.MaxWeightKgWithBarcode(Type(7), 1100m).Should().BeNull("по весу предела суммы нет");
    }

    [Fact]
    public void SendWarning_NamesProductAndWeight_OnlyForAmountStructure()
    {
        var products = new[] { ("Бука лакым", 1100m), ("Морковь", 60m) };

        var warning = RongtaBarcodeFormat.SendWarning(Type(2), products);

        warning.Should().Contain("Бука лакым").And.Contain("0,909").And.Contain("999,99");
        warning.Should().NotContain("Морковь", "60 сом/кг помещается до 16 кг — предупреждать не о чем");
        RongtaBarcodeFormat.SendWarning(Type(7), products).Should().BeEmpty();
        RongtaBarcodeFormat.SendWarning(Type(2), new[] { ("Морковь", 60m) }).Should().BeEmpty();
    }

    [Fact]
    public void CeilingText_SaysNoBarcode_NotTruncated()
    {
        var prefs = UserPreferences.Instance;
        var saved = prefs.Language;
        try
        {
            prefs.Language = AppLanguage.Russian;
            RongtaBarcodeFormat.CeilingText(Type(2)).Should().Contain("БЕЗ штрихкода").And.NotContain("обрез");
        }
        finally
        {
            prefs.Language = saved;
        }
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
