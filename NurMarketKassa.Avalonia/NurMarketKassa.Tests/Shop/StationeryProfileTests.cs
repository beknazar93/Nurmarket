using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ1): профиль stationery — данные на 5 языках, единицы без дробных, группы, услуги,
/// раздел «Весы» скрыт только при выборе этой сферы; остальные сферы работают как раньше.</summary>
[Collection("UserPreferences")]
public sealed class StationeryProfileTests : IDisposable
{
    private readonly string _sphere;
    private readonly string? _detail;
    private readonly List<string> _hidden;

    public StationeryProfileTests()
    {
        var p = UserPreferences.Instance;
        _sphere = p.MarketSphere;
        _detail = p.MarketSphereDetail;
        _hidden = p.HiddenSections.ToList();
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.HiddenSections = _hidden;
    }

    private static void Prepare(string sphere, string? detail)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = sphere;
        p.MarketSphereDetail = detail;
        p.HiddenSections = new List<string>();
    }

    [Fact]
    public void Profile_is_registered_with_grocery_base_and_full_data()
    {
        var profile = SphereCatalog.TryGet("stationery");
        profile.Should().NotBeNull();
        profile!.BaseBehavior.Should().Be(MarketSpheres.Grocery);
        profile.Icon.Should().Be("✏️");
        profile.IconKey.Should().Be("pencil");
        profile.AccentHex.Should().Be("#2563EB");
        profile.Title.IsComplete.Should().BeTrue();
        profile.ShortTitle.IsComplete.Should().BeTrue();
        profile.Description.IsComplete.Should().BeTrue();
        profile.ShowVariants.Should().BeFalse();
        profile.ShowProductDetails.Should().BeFalse();
        MarketSpheres.Normalize("stationery").Should().Be(MarketSpheres.Grocery); // на сервер уходит grocery
    }

    [Fact]
    public void Stationery_is_in_the_choice_list_and_choosing_it_sets_it()
    {
        var ids = SphereCatalog.ChoiceList().Select(p => p.Id).ToList();
        ids.Should().Contain("stationery");
        ids.Take(3).Should().Equal("grocery", "clothing", "services"); // базовые — первыми, как раньше
        SphereCatalog.Get("stationery").PackScan.Should().BeTrue();
        SphereCatalog.Get("grocery").PackScan.Should().BeFalse();
        SphereCatalog.Get("clothing").ChoiceVisible.Should().BeTrue();

        Prepare(MarketSpheres.Grocery, null);
        MarketSpheres.Set(SphereCatalog.ChoiceList().Single(p => p.Id == "stationery").Id);
        MarketSpheres.Profile.Id.Should().Be("stationery");
        UserPreferences.Instance.MarketSphere.Should().Be(MarketSpheres.Grocery);
    }

    [Fact]
    public void Units_are_seven_and_none_is_fractional()
    {
        var units = SphereCatalog.Get("stationery").Units;
        units.Select(u => u.Code).Should().Equal("pcs", "pack", "box", "set", "sheet", "roll", "pair");
        units.Should().OnlyContain(u => !u.Fractional && u.Name.IsComplete);
        // «пачка» понимается как упаковка
        units.Single(u => u.Code == "pack").Synonyms.Should().Contain("пачка");
    }

    [Fact]
    public void Categories_groups_and_services_have_all_five_languages()
    {
        SphereCatalog.Get("stationery").CategoryHints.Should().HaveCount(11).And.OnlyContain(c => c.IsComplete);
        StationeryProfile.QuickGroups.Should().HaveCount(8).And.OnlyContain(c => c.IsComplete);
        StationeryProfile.ServiceTemplates.Should().HaveCount(4).And.OnlyContain(c => c.IsComplete);
        StationeryProfile.QuickGroups.Select(g => g.Ru).Should().Contain(new[] { "Тетради", "Наборы к школе", "Копицентр" });
    }

    [Fact]
    public void Features_match_the_spec()
    {
        var f = StationeryProfile.Features;
        f.FractionalUnits.Should().BeFalse();
        f.PackScan.Should().BeTrue();
        f.OrgWholesale.Should().BeTrue();
        f.QuantityPad.Should().BeFalse();
        f.ConsumptionCalc.Should().BeFalse();
        f.ShowVariants.Should().BeFalse();
        f.ExpiryDates.Should().BeFalse();
        f.WeightGoods.Should().BeFalse();
    }

    [Fact]
    public void Resolve_picks_stationery_only_over_grocery_base()
    {
        SphereCatalog.Resolve("grocery", "stationery").Id.Should().Be("stationery");
        SphereCatalog.Resolve("clothing", "stationery").Id.Should().Be("clothing"); // расхождение базы — базовая сфера
        SphereCatalog.Resolve("services", "stationery").Id.Should().Be("services");
    }

    [Fact]
    public void Other_spheres_are_unchanged()
    {
        foreach (var id in new[] { "grocery", "clothing", "services", "hardware" })
            SphereCatalog.TryGet(id).Should().NotBeNull();
        SphereCatalog.Get("grocery").BaseBehavior.Should().Be("grocery");
        SphereCatalog.Get("clothing").BaseBehavior.Should().Be("clothing");
        SphereCatalog.Get("services").BaseBehavior.Should().Be("services");
        SphereCatalog.Get("hardware").BaseBehavior.Should().Be("grocery");
        SphereCatalog.Get("hardware").Units.Should().HaveCount(14);
        SphereCatalog.Get("grocery").Units.Should().BeEmpty();
        // «Хозтовары» дробные единицы по-прежнему умеют
        HardwareProfile.IsFractionalUnit("м").Should().BeTrue();
    }

    [Theory]
    [InlineData("grocery", null)]
    [InlineData("clothing", null)]
    [InlineData("services", null)]
    [InlineData("grocery", "hardware")]
    public void Other_spheres_never_hide_scales_and_are_not_stationery(string sphere, string? detail)
    {
        Prepare(MarketSpheres.Grocery, null);
        MarketSpheres.Set(detail ?? sphere);
        MarketSpheres.IsStationery.Should().BeFalse();
        SectionVisibility.IsHidden("scales").Should().BeFalse();
    }

    [Fact]
    public void Selecting_stationery_hides_scales_once_and_owner_can_show_them_back()
    {
        Prepare(MarketSpheres.Grocery, null);
        SectionVisibility.IsHidden("scales").Should().BeFalse();

        MarketSpheres.Set("stationery");
        MarketSpheres.IsStationery.Should().BeTrue();
        UserPreferences.Instance.MarketSphere.Should().Be(MarketSpheres.Grocery);
        UserPreferences.Instance.MarketSphereDetail.Should().Be("stationery");
        SectionVisibility.IsHidden("scales").Should().BeTrue();

        SectionVisibility.Set("scales", false); // тумблер остаётся
        MarketSpheres.Set("stationery");        // повторный выбор того же вида ничего не прячет заново
        SectionVisibility.IsHidden("scales").Should().BeFalse();
    }
}
