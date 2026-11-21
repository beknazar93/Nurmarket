using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Scales;

/// <summary>2026-11-21, живой баг магазина (касса 1.17.90, Rongta, префикс 20): мастер «Настроить по этикетке» и настройки показывали «20 = вес» (1,805 кг), а скан в корзине
/// читал ту же этикетку 2000001018057 как СУММУ 1805 сом (1,641 кг × 1100). Причина — скан перед разбором подмешивал общие префиксы «сумма» весов с прямой отправкой
/// (файл/облако, ScaleLabelCodeRegistry.ApplySharedAmountPrefixes), и 20 возвращался в «сумма», которая сильнее «вес»; настройки этого не видели.
/// Правило кассы «вес» должно побеждать при любом режиме компании и единице суммы, а мастер и скан — решать одинаково.</summary>
[Collection("CloudSyncSerial")]
public sealed class ScaleWeightRuleScanConsistencyTests : IDisposable
{
    private const string Label = "2000001018057"; // PLU 00001, значение 01805
    private readonly UserPreferences _p = UserPreferences.Instance;
    private readonly (string Amount, string Weight, string? Unit, string Mode) _saved;
    private readonly (IReadOnlySet<string> A, IReadOnlySet<string> W, string Mode, string Unit) _parser;

    public ScaleWeightRuleScanConsistencyTests()
    {
        _saved = (_p.ScaleAmountPrefixes, _p.ScaleWeightPrefixes, _p.ScaleBarcodeAmountUnitLocal, _p.ScaleBarcodeMode);
        _parser = (WeightBarcodeParser.AmountPrefixes, WeightBarcodeParser.WeightPrefixes, WeightBarcodeParser.Mode, WeightBarcodeParser.AmountUnit);
        ScaleBarcodeRulesSync.ResetForTests();
    }

    public void Dispose()
    {
        _p.ScaleAmountPrefixes = _saved.Amount;
        _p.ScaleWeightPrefixes = _saved.Weight;
        _p.ScaleBarcodeAmountUnitLocal = _saved.Unit;
        _p.ScaleBarcodeMode = _saved.Mode;
        ScaleBarcodeRulesSync.ResetForTests();
        WeightBarcodeParser.AmountPrefixes = _parser.A;
        WeightBarcodeParser.WeightPrefixes = _parser.W;
        WeightBarcodeParser.Mode = _parser.Mode;
        WeightBarcodeParser.AmountUnit = _parser.Unit;
    }

    [Theory]
    [InlineData("amount", "tiyin")]
    [InlineData("amount", "som")]
    [InlineData("auto", "tiyin")]
    [InlineData("auto", "som")]
    [InlineData("weight", "som")]
    public void Prefix20_WeightRule_ScanReadsWeight_EvenIfSharedAmountListHasIt(string mode, string unit)
    {
        _p.ScaleBarcodeMode = mode;
        WeightBarcodeParser.Mode = mode;
        WeightBarcodeParser.AmountUnit = unit;
        // с другого ПК / из облака пришёл общий список «сумма» с префиксом 20 (весы Rongta отправлялись по типу «сумма»)
        ScaleBarcodeRulesSync.TryApply(_p, "{\"v\":1,\"amount\":[],\"weight\":[],\"shared_amount\":[\"20\"]}").Should().BeTrue();
        // кассир выбрал «вес» (мастер / список правил, без записи на диск)
        ScaleBarcodeRules.Apply("20", WeightBarcodeValueKind.Weight);

        // путь скана: общие префиксы подмешиваются перед разбором (ProductCodeLookup / BasketPanelViewModel)
        ScaleLabelCodeRegistry.ApplySharedAmountPrefixes();
        WeightBarcodeParser.TryParse(Label, out var parsed).Should().BeTrue();

        parsed.Kind.Should().Be(WeightBarcodeValueKind.Weight);
        parsed.ResolveWeightKg(1100).Should().BeApproximately(1.805, 1e-9);
        // единый источник: мастер/настройки отвечают так же, как скан
        ScaleBarcodeRules.Effective("20").Should().Be(parsed.Kind);
    }

    [Fact]
    public void Prefix20_AmountRule_StillWinsAndWizardAgreesWithScan()
    {
        WeightBarcodeParser.Mode = "weight";
        WeightBarcodeParser.AmountUnit = "som";
        ScaleBarcodeRules.Apply("20", WeightBarcodeValueKind.Amount);

        ScaleLabelCodeRegistry.ApplySharedAmountPrefixes();
        WeightBarcodeParser.TryParse(Label, out var parsed).Should().BeTrue();

        parsed.Kind.Should().Be(WeightBarcodeValueKind.Amount);
        ScaleBarcodeRules.Effective("20").Should().Be(parsed.Kind);
    }
}
