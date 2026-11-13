using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-12 (ХМ6): формулы окна «Количество / длина / площадь» — «Д × Ш» вверх до шага, минимум, шаг, кнопки по единице, сумма строки.</summary>
[Collection("ServerFeatures")]
public sealed class QuantityPadMathTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;

    public QuantityPadMathTests()
    {
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = "hardware";
        QuantityRules.ServerFlagEnabled = () => true;
    }

    public void Dispose()
    {
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        QuantityRules.ServerFlagEnabled = () => NurMarketKassa.Services.Api.ServerCapabilities.Has(NurMarketKassa.Services.Api.ServerCapabilities.Hardware202611);
    }

    [Fact]
    public void Area_LengthTimesWidth_RoundsUpToStep()
    {
        QuantityPadMath.Area(2.4m, 3.15m, 0.01m).Should().Be(7.56m);
        QuantityPadMath.Area(2.333m, 3m, 0.01m).Should().Be(7.00m, "6,999 вверх до шага 0,01 = 7,00");
        QuantityPadMath.Area(2.001m, 3m, 0.01m).Should().Be(6.01m, "6,003 вверх");
        QuantityPadMath.Area(0m, 3m, 0.01m).Should().Be(0m);
    }

    [Fact]
    public void Check_BelowMinimum_ShowsMinimumCaption()
    {
        var m = QuantityRules.ForUnit("м")!;
        var r = QuantityPadMath.Check("0,5", m, "м");
        r.Ok.Should().BeFalse();
        r.Error.Should().Contain("Минимум").And.Contain("1").And.Contain("м");
    }

    [Fact]
    public void Check_NormalizesToStep_AndRejectsGarbage()
    {
        var m = QuantityRules.ForUnit("м")!;
        var ok = QuantityPadMath.Check("2,46", m, "м");
        ok.Ok.Should().BeTrue();
        ok.Value.Should().Be(2.5d, "шаг 0,1 м");
        QuantityPadMath.Check("", m, "м").Ok.Should().BeFalse();
        QuantityPadMath.Check("abc", m, "м").Ok.Should().BeFalse();
        QuantityPadMath.Check("0", m, "м").Ok.Should().BeFalse();
        QuantityPadMath.Check("-3", m, "м").Ok.Should().BeFalse();
        QuantityPadMath.Check("99999999", m, "м").Ok.Should().BeFalse();
        QuantityPadMath.Check("14.4", QuantityRules.ForUnit("м²")!, "м²").Value.Should().Be(14.4d);
    }

    [Fact]
    public void Steps_PerUnit_AndLineTotal()
    {
        QuantityPadMath.StepsFor("m").Should().Equal(0.5m, 1m, 2m, 3m, 5m, 10m, 25m, 50m, 100m);
        QuantityPadMath.StepsFor("m2").Should().Equal(1m, 2m, 5m, 10m, 20m);
        QuantityPadMath.StepsFor("l").Should().Equal(0.5m, 1m, 2m, 5m, 10m);
        QuantityPadMath.LineTotal(2.5, 41.05m).Should().Be(102.63m, "102,625 — половина вверх");
        QuantityPadMath.LineTotal(14.4, 520m).Should().Be(7488m);
    }
}
