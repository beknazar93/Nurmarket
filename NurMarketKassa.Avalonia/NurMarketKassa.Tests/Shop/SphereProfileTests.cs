using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-12 (СФ4, ТЗ хозмаг): признаки возможностей профиля сферы. У grocery / clothing / services все новые признаки
/// выключены (поведение как раньше); у hardware — по ТЗ 3.2; тексты на пяти языках заполнены.</summary>
public sealed class SphereProfileTests
{
    [Theory]
    [InlineData("grocery")]
    [InlineData("clothing")]
    [InlineData("services")]
    public void BaseSpheres_HaveAllNewFeaturesOff(string id)
    {
        var p = SphereCatalog.Get(id);
        p.Id.Should().Be(id);
        p.FractionalUnits.Should().BeFalse();
        p.PackScan.Should().BeFalse();
        p.QuantityPad.Should().BeFalse();
        p.ConsumptionCalc.Should().BeFalse();
        p.ForemanPriceAvailable.Should().BeFalse();
        p.ChoiceVisible.Should().BeTrue("три базовые сферы всегда в окне выбора");
    }

    [Fact]
    public void Hardware_HasFeaturesFromSpec()
    {
        var p = SphereCatalog.Get(HardwareProfile.Id);
        p.Id.Should().Be("hardware");
        p.BaseBehavior.Should().Be(MarketSpheres.Grocery, "на сервер уходит grocery");
        p.FractionalUnits.Should().BeTrue();
        p.PackScan.Should().BeTrue();
        p.QuantityPad.Should().BeTrue();
        p.ConsumptionCalc.Should().BeTrue();
        p.ForemanPriceAvailable.Should().BeTrue();
        p.ShowProductDetails.Should().BeTrue();
        p.ChoiceVisible.Should().BeTrue();
        p.Units.Should().HaveCount(14);
        p.CategoryHints.Should().HaveCount(15);
    }

    [Fact]
    public void AllProfiles_HaveCompleteTexts()
    {
        foreach (var p in SphereCatalog.All.Where(x => x.Id is "grocery" or "clothing" or "services" or "hardware"))
        {
            p.Title.IsComplete.Should().BeTrue(p.Id + " Title");
            p.ShortTitle.IsComplete.Should().BeTrue(p.Id + " ShortTitle");
            p.Description.IsComplete.Should().BeTrue(p.Id + " Description");
            foreach (var u in p.Units)
                u.Name.IsComplete.Should().BeTrue(p.Id + " unit " + u.Code);
            foreach (var c in p.CategoryHints)
                c.IsComplete.Should().BeTrue(p.Id + " category " + c.Ru);
        }
    }
}
