using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-05, срочный хотфикс (весы TM-30F у клиента, префикс 25, значение — сумма в ТЫЙЫНАХ): на сервере у компании стояло
/// scale_barcode_amount_unit = «som», и касса читала 2500001768001 как 76 800 сом и 384 кг вместо 768,00 сом и 3,840 кг.
/// Теперь единица может быть задана на кассе (ScaleBarcodeAmountUnitLocal) и важнее серверной; есть проверка «нереальный вес».
/// Коллекция «UserPreferences» — общие статические настройки (парсер и UserPreferences.Instance) не должны меняться параллельно.</summary>
[Collection("UserPreferences")]
public sealed class ScaleAmountUnitTests : IDisposable
{
    private readonly string _layout = WeightBarcodeParser.Layout;
    private readonly string _mode = WeightBarcodeParser.Mode;
    private readonly string _unit = WeightBarcodeParser.AmountUnit;
    private readonly string _prefsServerUnit = UserPreferences.Instance.ScaleBarcodeAmountUnit;
    private readonly string? _prefsLocalUnit = UserPreferences.Instance.ScaleBarcodeAmountUnitLocal;
    private readonly string _prefsLayout = UserPreferences.Instance.ScaleBarcodeLayout;
    private readonly string _prefsMode = UserPreferences.Instance.ScaleBarcodeMode;

    public ScaleAmountUnitTests()
    {
        WeightBarcodeParser.Layout = "plu";
        WeightBarcodeParser.Mode = "auto";
        WeightBarcodeParser.AmountUnit = "tiyin";
        WeightBarcodeParser.AmountPrefixes = new HashSet<string>();
        WeightBarcodeParser.WeightPrefixes = new HashSet<string>();
    }

    public void Dispose()
    {
        WeightBarcodeParser.Layout = _layout;
        WeightBarcodeParser.Mode = _mode;
        WeightBarcodeParser.AmountUnit = _unit;
        UserPreferences.Instance.ScaleBarcodeAmountUnit = _prefsServerUnit;
        UserPreferences.Instance.ScaleBarcodeAmountUnitLocal = _prefsLocalUnit;
        UserPreferences.Instance.ScaleBarcodeLayout = _prefsLayout;
        UserPreferences.Instance.ScaleBarcodeMode = _prefsMode;
    }

    // Этикетки весов клиента (EAN-13): штрихкод, PLU, цена за кг, сумма на этикетке (сом), вес (кг), цифры значения.
    public static IEnumerable<object[]> ClientLabels() => new[]
    {
        new object[] { "2500001768001", "00001", 200.0, 768.00, 3.840, 76800 },
        new object[] { "2500002019805", "00002", 90.0, 19.80, 0.220, 1980 },
        new object[] { "2500003008808", "00003", 40.0, 8.80, 0.220, 880 },
        new object[] { "2500004121001", "00004", 550.0, 121.00, 0.220, 12100 },
        new object[] { "2500005126500", "00005", 550.0, 126.50, 0.230, 12650 },
    };

    [Theory]
    [MemberData(nameof(ClientLabels))]
    public void Tiyin_ClientLabels_GiveRealAmountAndWeight(string barcode, string plu, double price, double amount, double weight, int raw)
    {
        WeightBarcodeParser.AmountUnit = "tiyin";

        WeightBarcodeParser.TryParse(barcode, out var parsed).Should().BeTrue();
        parsed.ProductCode.Should().Be(plu);
        parsed.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        parsed.Value.Should().BeApproximately(amount, 1e-9);
        parsed.ResolveWeightKg(price).Should().BeApproximately(weight, 1e-9);
        WeightBarcodeParser.IsUnrealisticWeight(parsed.ResolveWeightKg(price)).Should().BeFalse();
        WeightBarcodeParser.SuggestAmountUnit(raw, price, "tiyin").Should().BeNull("при верной единице подсказывать нечего");
    }

    [Theory]
    [MemberData(nameof(ClientLabels))]
    public void Som_ClientLabels_AmountIs100TimesBigger(string barcode, string plu, double price, double amount, double weight, int raw)
    {
        WeightBarcodeParser.AmountUnit = "som";

        WeightBarcodeParser.TryParse(barcode, out var parsed).Should().BeTrue();
        parsed.Value.Should().BeApproximately(amount * 100, 1e-6);
        var kg = parsed.ResolveWeightKg(price);
        kg.Should().BeApproximately(weight * 100, 1e-6);

        // Автоопределение предлагает «тыйыны» только там, где при текущей единице значения нереальны (сумма выше порога или вес > 600 кг).
        var suggestion = WeightBarcodeParser.SuggestAmountUnit(raw, price, "som");
        if (raw == 76800)
        {
            WeightBarcodeParser.IsUnrealisticLabel(parsed, kg).Should().BeTrue("76 800 сом / 384 кг — защита «нереальная этикетка» срабатывает");
            suggestion.Should().Be("tiyin");
        }
        else
        {
            WeightBarcodeParser.IsUnrealisticLabel(parsed, kg).Should().BeFalse("22–23 кг формально возможны — не угадываем");
            suggestion.Should().BeNull();
        }
    }

    [Fact]
    public void Som_Banana_WeightIs384Kg_AndGuardLimitsWork()
    {
        WeightBarcodeParser.AmountUnit = "som";
        WeightBarcodeParser.TryParse("2500001768001", out var parsed).Should().BeTrue();

        parsed.Value.Should().Be(76800);
        parsed.ResolveWeightKg(200).Should().BeApproximately(384, 1e-6);
        WeightBarcodeParser.IsUnrealisticLabel(parsed, 384).Should().BeTrue("сумма 76 800 выше порога 20 000");
        WeightBarcodeParser.IsUnrealisticLabel(new WeightBarcodeParseResult("00001", 768, WeightBarcodeValueKind.Amount), 3.84).Should().BeFalse();
        WeightBarcodeParser.IsUnrealisticLabel(new WeightBarcodeParseResult("00001", 5, WeightBarcodeValueKind.Weight), 700).Should().BeTrue("вес больше 600 кг");
        WeightBarcodeParser.IsUnrealisticAmount(76800, 200).Should().BeTrue();
        WeightBarcodeParser.IsUnrealisticAmount(768, 200).Should().BeFalse();
        WeightBarcodeParser.IsUnrealisticWeight(WeightBarcodeParser.MaxRealisticWeightKg).Should().BeFalse();
        WeightBarcodeParser.IsUnrealisticWeight(WeightBarcodeParser.MaxRealisticWeightKg + 0.001).Should().BeTrue();
    }

    [Fact]
    public void SuggestAmountUnit_DoesNotGuess_WhenBothReadingsAreUnrealistic_OrPriceUnknownAndSumSmall()
    {
        // Цена 1 сом/кг: при любой единице вес/сумма большие — не угадываем.
        WeightBarcodeParser.SuggestAmountUnit(99999, 1, "som").Should().BeNull();
        // Без цены (товар не найден) проверяется только порог суммы: 76800 сом > 20000, а 768 — нет.
        WeightBarcodeParser.SuggestAmountUnit(76800, 0, "som").Should().Be("tiyin");
        WeightBarcodeParser.SuggestAmountUnit(1980, 0, "som").Should().BeNull();
    }

    [Fact]
    public void MaxLabelAmount_IsConfigurable()
    {
        var old = WeightBarcodeParser.MaxLabelAmountSom;
        try
        {
            WeightBarcodeParser.MaxLabelAmountSom = 500;
            WeightBarcodeParser.IsUnrealisticAmount(768, 0).Should().BeTrue();
            WeightBarcodeParser.MaxLabelAmountSom = 20000;
            WeightBarcodeParser.IsUnrealisticAmount(768, 0).Should().BeFalse();
        }
        finally
        {
            WeightBarcodeParser.MaxLabelAmountSom = old;
        }
    }

    [Theory]
    [InlineData("tiyin", "som", "tiyin")] // локальная важнее серверной
    [InlineData("som", "tiyin", "som")]
    [InlineData(null, "som", "som")]      // локальной нет — серверная
    [InlineData("", "som", "som")]
    [InlineData("мусор", "som", "som")]   // неизвестное локальное игнорируется
    [InlineData(null, null, "tiyin")]
    [InlineData(null, "мусор", "tiyin")]
    public void ResolveAmountUnit_LocalBeatsServer(string? local, string? server, string expected) =>
        WeightBarcodeParser.ResolveAmountUnit(local, server).Should().Be(expected);

    [Fact]
    public void ServerSync_DoesNotOverwriteLocalUnit()
    {
        var prefs = UserPreferences.Instance;
        prefs.SetLocalScaleAmountUnit("tiyin");

        // Сервер говорит «som» — как у клиента.
        CompanyInfoService.ApplyServerScaleSettings("plu", "auto", "som");

        prefs.ScaleBarcodeAmountUnit.Should().Be("som", "серверное значение записывается как раньше");
        prefs.ScaleBarcodeAmountUnitLocal.Should().Be("tiyin", "локальное синхронизация не затирает");
        WeightBarcodeParser.AmountUnit.Should().Be("tiyin");
        WeightBarcodeParser.TryParse("2500001768001", out var parsed).Should().BeTrue();
        parsed.Value.Should().BeApproximately(768.00, 1e-9);
        parsed.ResolveWeightKg(200).Should().BeApproximately(3.840, 1e-9);

        // Вернулись «как на сайте» — снова работает серверная.
        prefs.SetLocalScaleAmountUnit(null);
        prefs.ScaleBarcodeAmountUnitLocal.Should().BeNull();
        WeightBarcodeParser.AmountUnit.Should().Be("som");
        CompanyInfoService.ApplyServerScaleSettings("plu", "auto", "tiyin");
        WeightBarcodeParser.AmountUnit.Should().Be("tiyin");
    }

    [Fact]
    public void SetLocalScaleAmountUnit_RejectsUnknownValue()
    {
        var prefs = UserPreferences.Instance;
        prefs.SetLocalScaleAmountUnit("som");
        prefs.ScaleBarcodeAmountUnitLocal.Should().Be("som");
        prefs.SetLocalScaleAmountUnit("килограммы");
        prefs.ScaleBarcodeAmountUnitLocal.Should().BeNull();
    }
}
