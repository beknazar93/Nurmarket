using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06: весы «Штрих РС-200С2», вкладка «Штрих-код» (руководство, п. 7.6 и 12.1): EAN-13 = префикс (2) + 10 знаков «код + значение» + контрольная цифра;
/// число знаков кода 1…6, формат «Код-Вес» / «Код-Стоимость» / «Вес-Код» / «Стоимость-Код», «КС кода товара» (младший разряд суммы цифр кода).
/// Раскладка действует только на свой префикс; остальные префиксы и старые марки читаются как раньше. Парсер статический — коллекция «UserPreferences».</summary>
[Collection("UserPreferences")]
public sealed class ShtrikhPcBarcodeTests : IDisposable
{
    private readonly string _layout = WeightBarcodeParser.Layout;
    private readonly string _mode = WeightBarcodeParser.Mode;
    private readonly string _unit = WeightBarcodeParser.AmountUnit;
    private readonly IReadOnlyList<WeightBarcodeCustomLayout> _custom = WeightBarcodeParser.CustomLayouts;
    private readonly IReadOnlySet<string> _amountPrefixes = WeightBarcodeParser.AmountPrefixes;
    private readonly IReadOnlySet<string> _weightPrefixes = WeightBarcodeParser.WeightPrefixes;

    public ShtrikhPcBarcodeTests()
    {
        WeightBarcodeParser.Layout = "plu";
        WeightBarcodeParser.Mode = "auto";
        WeightBarcodeParser.AmountUnit = "tiyin";
        WeightBarcodeParser.AmountPrefixes = new HashSet<string>();
        WeightBarcodeParser.WeightPrefixes = new HashSet<string>();
        WeightBarcodeParser.CustomLayouts = Array.Empty<WeightBarcodeCustomLayout>();
    }

    public void Dispose()
    {
        WeightBarcodeParser.Layout = _layout;
        WeightBarcodeParser.Mode = _mode;
        WeightBarcodeParser.AmountUnit = _unit;
        WeightBarcodeParser.CustomLayouts = _custom;
        WeightBarcodeParser.AmountPrefixes = _amountPrefixes;
        WeightBarcodeParser.WeightPrefixes = _weightPrefixes;
    }

    private static WeightBarcodeCustomLayout Layout(int codeLength, string format = ShtrikhPcSettings.FormatCodeWeight, bool kc = false, int decimals = 2, int prefix = 20) =>
        new ShtrikhPcSettings { BarcodePrefix = prefix, CodeLength = codeLength, BarcodeFormat = format, CodeChecksum = kc, PriceDecimals = decimals }.Normalize().ToLayout();

    private static string Ean(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + (10 - sum % 10) % 10;
    }

    [Fact]
    public void LiveExample_Label0144Kg_Code69312_FiveDigitCode_CodeWeight()
    {
        // Фото весов владельца: «КОД ТОВАРА: 69312», вес 0,144 кг; формат «Код-Вес», 5 знаков кода → 20 + 69312 + 00144 + контрольная.
        var layout = Layout(5);
        var barcode = layout.BuildBarcode(69312, 144);

        barcode.Should().Be("2069312001448");
        WeightBarcodeParser.CustomLayouts = new[] { layout };
        WeightBarcodeParser.TryParse(barcode!, out var parsed).Should().BeTrue();
        parsed.ProductCode.Should().Be("69312");
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        parsed.Value.Should().BeApproximately(0.144, 1e-9);
        parsed.ResolveWeightKg(0).Should().BeApproximately(0.144, 1e-9);
    }

    [Theory]
    [InlineData(4, 1234, 5000)]    // 20 | 1234 | 005000
    [InlineData(5, 12345, 1234)]
    [InlineData(6, 123456, 144)]
    [InlineData(1, 7, 99999)]
    public void CodeWeight_ParsesAllCodeLengths(int codeLength, long code, long grams)
    {
        var layout = Layout(codeLength);
        var barcode = layout.BuildBarcode(code, grams)!;
        WeightBarcodeParser.CustomLayouts = new[] { layout };

        WeightBarcodeParser.TryParse(barcode, out var parsed).Should().BeTrue();

        barcode.Length.Should().Be(13);
        long.Parse(parsed.ProductCode).Should().Be(code);
        parsed.ProductCode.Length.Should().Be(codeLength);
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        parsed.Value.Should().BeApproximately(grams / 1000.0, 1e-9);
    }

    [Fact]
    public void CodeWeight_DefaultFourDigits_ValueIsSixDigits()
    {
        // «Число знаков кода товара» по умолчанию 4 → на вес остаётся 6 знаков.
        var layout = Layout(4);
        WeightBarcodeParser.CustomLayouts = new[] { layout };

        var barcode = Ean("20" + "0042" + "000540");

        WeightBarcodeParser.TryParse(barcode, out var parsed).Should().BeTrue();
        parsed.ProductCode.Should().Be("0042");
        parsed.Value.Should().BeApproximately(0.540, 1e-9);
    }

    [Theory]
    [InlineData(4, "0042", "001500", 15.00, 2)]     // 2 знака цены: 1500 → 15,00
    [InlineData(5, "00042", "01500", 15.00, 2)]
    [InlineData(6, "000042", "1500", 15.00, 2)]
    [InlineData(5, "00042", "00015", 15.0, 0)]      // 0 знаков цены: целые сомы
    [InlineData(5, "00042", "01500", 1.5, 3)]       // 3 знака: 1500 → 1,500
    public void CodeAmount_ParsesCostInMinimalUnits(int codeLength, string codeDigits, string valueDigits, double expectedSom, int decimals)
    {
        WeightBarcodeParser.CustomLayouts = new[] { Layout(codeLength, ShtrikhPcSettings.FormatCodeAmount, decimals: decimals) };

        WeightBarcodeParser.TryParse(Ean("20" + codeDigits + valueDigits), out var parsed).Should().BeTrue();

        parsed.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        parsed.ProductCode.Should().Be(codeDigits);
        parsed.Value.Should().BeApproximately(expectedSom, 1e-9);
    }

    [Fact]
    public void CodeAmount_IgnoresCompanyAmountUnit_DecimalsOfThePriceRule()
    {
        // единица суммы компании (тыйын/сом) на РС-200 не влияет: стоимость — по числу знаков цены на весах
        WeightBarcodeParser.AmountUnit = "som";
        WeightBarcodeParser.CustomLayouts = new[] { Layout(5, ShtrikhPcSettings.FormatCodeAmount, decimals: 2) };

        WeightBarcodeParser.TryParse(Ean("20" + "00042" + "01500"), out var parsed).Should().BeTrue();

        parsed.Value.Should().BeApproximately(15.00, 1e-9);
    }

    [Fact]
    public void AmountCode_And_WeightCode_ReverseOrder()
    {
        WeightBarcodeParser.CustomLayouts = new[] { Layout(5, ShtrikhPcSettings.FormatAmountCode) };
        WeightBarcodeParser.TryParse(Ean("20" + "01500" + "00042"), out var cost).Should().BeTrue();
        cost.ProductCode.Should().Be("00042");
        cost.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        cost.Value.Should().BeApproximately(15.00, 1e-9);

        WeightBarcodeParser.CustomLayouts = new[] { Layout(4, ShtrikhPcSettings.FormatWeightCode) };
        WeightBarcodeParser.TryParse(Ean("20" + "000144" + "0042"), out var weight).Should().BeTrue();
        weight.ProductCode.Should().Be("0042");
        weight.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        weight.Value.Should().BeApproximately(0.144, 1e-9);
    }

    [Fact]
    public void CodeChecksum_FiveDigitField_FourCodeDigitsPlusControl()
    {
        // руководство 12.1: КС — младший разряд суммы цифр кода. Код 6931 → 6+9+3+1 = 19 → КС 9 → поле «69319».
        var layout = Layout(5, kc: true);
        layout.RealCodeDigits.Should().Be(4);
        layout.ControlDigit("6931").Should().Be(9);
        var barcode = layout.BuildBarcode(6931, 144)!;
        barcode.Should().Be(Ean("20" + "69319" + "00144"));

        WeightBarcodeParser.CustomLayouts = new[] { layout };
        WeightBarcodeParser.TryParse(barcode, out var parsed).Should().BeTrue();
        parsed.ProductCode.Should().Be("6931");   // КС в код товара не попадает
        parsed.Value.Should().BeApproximately(0.144, 1e-9);
    }

    [Fact]
    public void CodeChecksum_WrongControlDigit_IsNotAWeightLabel()
    {
        WeightBarcodeParser.CustomLayouts = new[] { Layout(5, kc: true) };

        WeightBarcodeParser.TryParse(Ean("20" + "69310" + "00144"), out _).Should().BeFalse(); // правильная КС — 9
    }

    [Fact]
    public void CodeChecksum_SixDigitField_FiveCodeDigits()
    {
        // пример из руководства (стр. 27): 6 знаков + КС → 5 цифр кода + 1 КС
        var layout = Layout(6, kc: true);
        layout.RealCodeDigits.Should().Be(5);
        layout.MaxCode.Should().Be(99999);
        var barcode = layout.BuildBarcode(12345, 250)!;
        WeightBarcodeParser.CustomLayouts = new[] { layout };

        WeightBarcodeParser.TryParse(barcode, out var parsed).Should().BeTrue();

        parsed.ProductCode.Should().Be("12345");
        parsed.Value.Should().BeApproximately(0.250, 1e-9);
    }

    [Fact]
    public void BuildBarcode_ReturnsNull_WhenCodeOrValueDoesNotFit()
    {
        var layout = Layout(4);   // код до 9999, значение до 6 знаков
        layout.BuildBarcode(10000, 100).Should().BeNull();
        layout.BuildBarcode(1, 1_000_000).Should().BeNull();
        Layout(6).BuildBarcode(1, 10_000).Should().BeNull();          // на значение 4 знака: вес до 9,999 кг
        Layout(5, kc: true).BuildBarcode(10000, 1).Should().BeNull(); // с КС код — 4 цифры
    }

    [Fact]
    public void ZeroCost_LabelWithoutPrice_IsNotParsed_ButDetectedAsZeroValue()
    {
        // цены в весы не загружены: «Код-Стоимость» печатает 000000 вместо суммы — касса не должна молча дать 0
        WeightBarcodeParser.CustomLayouts = new[] { Layout(5, ShtrikhPcSettings.FormatCodeAmount) };
        var barcode = Ean("20" + "69312" + "00000");

        WeightBarcodeParser.TryParse(barcode, out _).Should().BeFalse();
        WeightBarcodeParser.IsCustomLabelWithZeroValue(barcode, out var code, out var kind).Should().BeTrue();
        code.Should().Be("69312");
        kind.Should().Be(WeightBarcodeValueKind.Amount);
        WeightBarcodeParser.IsCustomLabelWithZeroValue(Ean("20" + "69312" + "00144"), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void CodeWeight_WorksWithoutPricesOnTheScale()
    {
        // «Код-Вес» не зависит от цены на весах (у владельца весы печатают без цены)
        WeightBarcodeParser.CustomLayouts = new[] { Layout(5) };

        WeightBarcodeParser.TryParse(Ean("20" + "69312" + "00144"), out var parsed).Should().BeTrue();

        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
    }

    [Fact]
    public void CustomLayout_AppliesOnlyToItsPrefix_OthersReadAsBefore()
    {
        WeightBarcodeParser.CustomLayouts = new[] { Layout(4, prefix: 20) };

        // префикс 21 — по настройке компании «plu»: 2 + PLU(5) + значение(5) + контрольная
        WeightBarcodeParser.TryParse(Ean("21" + "00042" + "00540"), out var other).Should().BeTrue();
        other.ProductCode.Should().Be("00042");
        other.Value.Should().BeApproximately(0.540, 1e-9);
        // префикс 25 — режим «auto»: сумма в тыйынах
        WeightBarcodeParser.TryParse(Ean("25" + "00001" + "76800"), out var amount).Should().BeTrue();
        amount.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        amount.Value.Should().BeApproximately(768.00, 1e-9);
    }

    [Fact]
    public void NoCustomLayouts_ParserBehavesAsBefore()
    {
        WeightBarcodeParser.CustomLayouts.Should().BeEmpty();

        WeightBarcodeParser.TryParse(Ean("20" + "00042" + "00540"), out var parsed).Should().BeTrue();

        parsed.ProductCode.Should().Be("00042");
        parsed.Value.Should().BeApproximately(0.540, 1e-9);
    }

    [Fact]
    public void WrongEan13CheckDigit_IsRejectedByCustomLayoutToo()
    {
        WeightBarcodeParser.CustomLayouts = new[] { Layout(5) };
        var good = Ean("20" + "69312" + "00144");
        var bad = good[..12] + (good[12] == '0' ? '1' : '0');

        WeightBarcodeParser.TryParse(bad, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseWithLayout_DoesNotNeedTheLayoutToBeActive()
    {
        var layout = Layout(5);

        WeightBarcodeParser.TryParseWithLayout("2069312001448", layout, out var parsed).Should().BeTrue();

        parsed.ProductCode.Should().Be("69312");
        WeightBarcodeParser.CustomLayouts.Should().BeEmpty();
        WeightBarcodeParser.TryParseWithLayout(Ean("21" + "69312" + "00144"), layout, out _).Should().BeFalse("префикс не тот");
    }

    [Fact]
    public void Settings_ToLayout_And_MaxProductCode()
    {
        new ShtrikhPcSettings { CodeLength = 4 }.Normalize().MaxProductCode.Should().Be(9999);
        new ShtrikhPcSettings { CodeLength = 6 }.Normalize().MaxProductCode.Should().Be(999_999);
        new ShtrikhPcSettings { CodeLength = 5, CodeChecksum = true }.Normalize().MaxProductCode.Should().Be(9999);
        new ShtrikhPcSettings { BarcodeFormat = ShtrikhPcSettings.FormatAmountCode }.Normalize().ToLayout().CodeFirst.Should().BeFalse();
    }

    [Fact]
    public void ApplyToParser_RegistersLayoutOnlyWhenEnabled()
    {
        var before = UserPreferences.Instance.ShtrikhPcConfig;
        try
        {
            ShtrikhPcSettings.Save(new ShtrikhPcSettings { BarcodeEnabled = false, CodeLength = 5 });
            WeightBarcodeParser.CustomLayouts.Should().BeEmpty();

            ShtrikhPcSettings.Save(new ShtrikhPcSettings { BarcodeEnabled = true, CodeLength = 5 });
            WeightBarcodeParser.CustomLayouts.Should().HaveCount(2, "основная (27) и штучная/стоимостная (20)");
            WeightBarcodeParser.CustomLayouts.Should().OnlyContain(l => l.CodeLength == 5);
        }
        finally
        {
            UserPreferences.Instance.ShtrikhPcConfig = before;
        }
    }

    [Fact]
    public void Defaults_MatchTheOwnersScale_Prefix27_CodeWeight_FiveDigits()
    {
        // Config.txt весов владельца: 303 = 27 (префикс), 305 = 0 (код-вес), 307 = 5 (цифр кода), КС выкл
        var layout = new ShtrikhPcSettings().Normalize().ToLayout();

        layout.Prefix.Should().Be("27");
        layout.CodeLength.Should().Be(5);
        layout.CodeFirst.Should().BeTrue();
        layout.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        layout.CodeChecksum.Should().BeFalse();
        new ShtrikhPcSettings().Normalize().MaxProductCode.Should().Be(99999);
    }

    [Fact]
    public void OwnersLabel_27_Plu123_1250Grams_IsReadAsPlu123AndOnePointTwoFiveKg()
    {
        // Этикетка 27 + PLU(5) + вес в граммах(5) + контрольная. В ТЗ Умара пример записан как 2700123012508, но у EAN-13 для этих цифр контрольная цифра 5
        // (веса 1/3 по первым 12 цифрам даёт сумму 45), поэтому верный код — 2700123012505; код с «8» касса отвергает как «прочитан неверно».
        var layout = new ShtrikhPcSettings().Normalize().ToLayout();
        var good = layout.BuildBarcode(123, 1250)!;
        good.Should().Be("2700123012505");
        WeightBarcodeParser.CustomLayouts = new[] { layout };

        WeightBarcodeParser.TryParse(good, out var parsed).Should().BeTrue();

        long.Parse(parsed.ProductCode).Should().Be(123);
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        parsed.Value.Should().BeApproximately(1.250, 1e-9);
        WeightBarcodeParser.IsCustomLabelWithBadCheckDigit(good).Should().BeFalse();
    }

    [Fact]
    public void LabelFromTz_2700123012508_HasWrongEan13CheckDigit_IsRejected()
    {
        WeightBarcodeParser.CustomLayouts = new[] { new ShtrikhPcSettings().Normalize().ToLayout() };

        WeightBarcodeParser.TryParse("2700123012508", out _).Should().BeFalse();
        WeightBarcodeParser.IsCustomLabelWithBadCheckDigit("2700123012508").Should().BeTrue();
        // чужой префикс и не-весовой код «неверным чтением» не считаются
        WeightBarcodeParser.IsCustomLabelWithBadCheckDigit("2000123012508").Should().BeFalse();
        WeightBarcodeParser.IsCustomLabelWithBadCheckDigit("4870001234560").Should().BeFalse();
    }

    [Fact]
    public void Prefix27_WithAutoMode_IsWeight_NotAmount_WhenLayoutIsActive()
    {
        // в режиме компании «авто» префикс 27 читался бы как СУММА; раскладка РС-200 задаёт вес
        WeightBarcodeParser.Mode = "auto";
        WeightBarcodeParser.CustomLayouts = new[] { new ShtrikhPcSettings().Normalize().ToLayout() };

        WeightBarcodeParser.TryParse("2700123012505", out var parsed).Should().BeTrue();

        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
    }

    [Fact]
    public void Password_IsNotInSettingsJson_ButIsKeptInPreferences()
    {
        var beforeConfig = UserPreferences.Instance.ShtrikhPcConfig;
        var beforePassword = UserPreferences.Instance.ShtrikhPcPassword;
        try
        {
            var s = new ShtrikhPcSettings { Folder = @"\\192.168.0.200\Shared", Login = "scale", Password = "секрет-123" };
            ShtrikhPcSettings.Save(s);

            UserPreferences.Instance.ShtrikhPcConfig.Should().NotContain("секрет-123").And.Contain("scale");
            UserPreferences.Instance.ShtrikhPcPassword.Should().Be("секрет-123");
            ShtrikhPcSettings.Current.Password.Should().Be("секрет-123");
            ShtrikhPcSettings.Current.Login.Should().Be("scale");
        }
        finally
        {
            UserPreferences.Instance.ShtrikhPcConfig = beforeConfig;
            UserPreferences.Instance.ShtrikhPcPassword = beforePassword;
        }
    }
}
