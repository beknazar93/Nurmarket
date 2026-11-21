using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20, решение владельца: список сфер — Продуктовый, Хозмаг, Строймаг, Магазин одежды, Магазин электроники, Базар, затем Автозапчасти, Канцтовары, Услуги.
/// Хозмаг и Строймаг — две сферы на одной серверной базе (grocery); старое hardware читается как Строймаг (Смета, прокат и уровни цен не теряются);
/// «Базар» — каркас (розница / опт внутри сферы), продуктовым не считается. Настройки — общий UserPreferences.Instance, значения возвращаются.</summary>
[Collection("UserPreferences")]
public sealed class SphereCatalogSplitTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly bool _estimate = UserPreferences.Instance.EstimateEnabled;
    private readonly bool _rental = UserPreferences.Instance.RentalEnabled;
    private readonly string? _sphereJson = UserPreferences.Instance.SphereSettingsJson;

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.EstimateEnabled = _estimate;
        p.RentalEnabled = _rental;
        p.SphereSettingsJson = _sphereJson;
    }

    private static void SetSphere(string baseSphere, string? detail)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = baseSphere;
        p.MarketSphereDetail = detail;
        p.EstimateEnabled = false;
        p.RentalEnabled = false;
    }

    [Fact]
    public void Choice_list_has_the_owners_order()
    {
        SphereCatalog.ChoiceList().Select(p => p.Id).Should().Equal(
            "grocery", "hozmag", "stroymag", "clothing", "electronics", "bazaar", "autoparts", "stationery", "services");
        SphereCatalog.ChoiceList().Take(6).Select(p => p.Title.Ru).Should().Equal(
            "Продуктовый", "Хозмаг", "Строймаг", "Магазин одежды", "Магазин электроники", "Базар");
    }

    [Fact]
    public void Every_tile_has_five_languages_without_gaps()
    {
        foreach (var p in SphereCatalog.ChoiceList())
        {
            p.Title.IsComplete.Should().BeTrue(p.Id + " Title");
            p.ShortTitle.IsComplete.Should().BeTrue(p.Id + " ShortTitle");
            p.Description.IsComplete.Should().BeTrue(p.Id + " Description");
        }
        SphereCatalog.Get("hozmag").Title.Should().Be(new L5("Хозмаг", "Чарба дүкөнү", "Hardware store", "Hırdavat", "Xo'jalik do'koni"));
        SphereCatalog.Get("stroymag").Title.Ky.Should().Be("Курулуш дүкөнү");
    }

    [Fact]
    public void Hozmag_has_no_estimate_and_no_rentals_by_default_but_keeps_the_hardware_base()
    {
        SetSphere(MarketSpheres.Grocery, "hozmag");
        MarketSpheres.Profile.Id.Should().Be("hozmag");
        MarketSpheres.IsHardware.Should().BeTrue("общая основа Хозмага и Строймага");
        MarketSpheres.IsHozmag.Should().BeTrue();
        MarketSpheres.IsStroymag.Should().BeFalse();
        MarketSpheres.Profile.BaseBehavior.Should().Be(MarketSpheres.Grocery, "на сервер уходит grocery");
        MarketSpheres.Profile.HasEstimate.Should().BeFalse();
        MarketSpheres.Profile.HasRentalsByDefault.Should().BeFalse();
        EstimateFeature.IsAvailable.Should().BeFalse();
        RentalFeature.IsEnabled(MarketSpheres.Profile.HasRentalsByDefault, false).Should().BeFalse();
        var p = MarketSpheres.Profile;
        p.FractionalUnits.Should().BeTrue();
        p.PackScan.Should().BeTrue();
        p.ForemanPriceAvailable.Should().BeTrue("цена по клиенту остаётся");
        p.Units.Should().HaveCount(14);

        UserPreferences.Instance.EstimateEnabled = true; // модуль включили в Маркетплейсе
        EstimateFeature.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Stroymag_has_everything_hardware_had()
    {
        SetSphere(MarketSpheres.Grocery, "stroymag");
        MarketSpheres.IsHardware.Should().BeTrue();
        MarketSpheres.IsStroymag.Should().BeTrue();
        MarketSpheres.Profile.HasEstimate.Should().BeTrue();
        MarketSpheres.Profile.HasRentalsByDefault.Should().BeTrue();
        MarketSpheres.Profile.ForemanPriceAvailable.Should().BeTrue();
        MarketSpheres.Profile.ConsumptionCalc.Should().BeTrue();
        EstimateFeature.IsAvailable.Should().BeTrue();
        RentalFeature.IsEnabled(MarketSpheres.Profile.HasRentalsByDefault, false).Should().BeTrue();
    }

    [Fact]
    public void Old_saved_hardware_works_as_stroymag_without_losses()
    {
        SetSphere(MarketSpheres.Grocery, "hardware");
        MarketSpheres.Profile.Id.Should().Be("stroymag");
        SphereCatalog.Get("hardware").Id.Should().Be("stroymag");
        SphereCatalog.Resolve("grocery", "hardware").Id.Should().Be("stroymag");
        MarketSpheres.IsHardware.Should().BeTrue();
        EstimateFeature.IsAvailable.Should().BeTrue("Смета у них была сразу");
        MarketSpheres.Profile.HasRentalsByDefault.Should().BeTrue("прокат у них был сразу");
        MarketSpheres.Profile.ForemanPriceAvailable.Should().BeTrue();
        MarketSpheres.Normalize("hardware").Should().Be(MarketSpheres.Grocery);
        SphereCatalog.SanitizeDetail("hardware").Should().Be("hardware", "сохранённая строка не переписывается");
        // «hardware» от другой кассы / поддержки при выборе сохраняется уже новым id
        MarketSpheres.Set("hardware");
        UserPreferences.Instance.MarketSphereDetail.Should().Be("stroymag");
        UserPreferences.Instance.MarketSphere.Should().Be(MarketSpheres.Grocery);
    }

    [Fact]
    public void Choosing_hozmag_or_stroymag_keeps_grocery_on_the_server_side()
    {
        SetSphere(MarketSpheres.Grocery, null);
        MarketSpheres.Set("hozmag");
        UserPreferences.Instance.MarketSphere.Should().Be(MarketSpheres.Grocery);
        UserPreferences.Instance.MarketSphereDetail.Should().Be("hozmag");
        MarketSpheres.Set("stroymag");
        UserPreferences.Instance.MarketSphereDetail.Should().Be("stroymag");
        MarketSpheres.Normalize("hozmag").Should().Be(MarketSpheres.Grocery);
        MarketSpheres.Normalize("stroymag").Should().Be(MarketSpheres.Grocery);
    }

    [Fact]
    public void Bazaar_is_its_own_sphere_and_not_grocery()
    {
        SetSphere(MarketSpheres.Grocery, "bazaar");
        MarketSpheres.Profile.Id.Should().Be("bazaar");
        MarketSpheres.IsBazaar.Should().BeTrue();
        MarketSpheres.IsHardware.Should().BeFalse();
        MarketSpheres.IsClothing.Should().BeFalse();
        MarketSpheres.IsServices.Should().BeFalse();
        MarketSpheres.Profile.BaseBehavior.Should().Be(MarketSpheres.Grocery, "на сервер уходит grocery");
        MarketSpheres.Profile.HasEstimate.Should().BeFalse();
        MarketSpheres.Profile.HasRentalsByDefault.Should().BeFalse();
        SphereCatalog.Get("bazaar").ChoiceVisible.Should().BeTrue();

        // «продуктовая» ветка поиска по доп. штрихкодам: у «Базара» не grocery (как у «Автозапчастей»)
        var grocery = typeof(CatalogPanelViewModel).GetProperty("IsGrocerySphere", BindingFlags.NonPublic | BindingFlags.Static)!;
        ((bool)grocery.GetValue(null)!).Should().BeFalse();
        SetSphere(MarketSpheres.Grocery, null);
        ((bool)grocery.GetValue(null)!).Should().BeTrue();
    }

    [Fact]
    public void Bazaar_modes_retail_and_wholesale()
    {
        SetSphere(MarketSpheres.Grocery, "bazaar");
        UserPreferences.Instance.SphereSettingsJson = null;
        SphereSettings.GetBazaarMode().Should().Be(BazaarMode.Retail, "по умолчанию розница");
        SphereSettings.SetHardwareChoice(HardwareSize.Market, packScan: true).Should().BeTrue();
        SphereSettings.SetBazaarMode(BazaarMode.Wholesale).Should().BeTrue();
        SphereSettings.GetBazaarMode().Should().Be(BazaarMode.Wholesale);
        SphereSettings.GetHardwareSize().Should().Be(HardwareSize.Market, "соседний раздел не тронут");
        SphereSettings.SetBazaarMode(BazaarMode.Retail).Should().BeTrue();
        SphereSettings.GetBazaarMode().Should().Be(BazaarMode.Retail);

        // значения market_profile с сервера читаются как тот же профиль
        BazaarProfile.ServerDetail(BazaarMode.Retail).Should().Be("bazaar_retail");
        BazaarProfile.ServerDetail(BazaarMode.Wholesale).Should().Be("bazaar_wholesale");
        SphereCatalog.Resolve("grocery", "bazaar_retail").Id.Should().Be("bazaar");
        SphereCatalog.Resolve("grocery", "bazaar_wholesale").Id.Should().Be("bazaar");
        UserPreferences.Instance.SphereSettingsJson = null;
        SetSphere(MarketSpheres.Grocery, "bazaar_wholesale");
        SphereSettings.GetBazaarMode().Should().Be(BazaarMode.Wholesale, "режима в настройках нет — берём из значения сервера");
        foreach (var m in new[] { BazaarMode.Retail, BazaarMode.Wholesale })
        {
            BazaarProfile.ModeName(m).Should().NotBeNullOrWhiteSpace();
            BazaarProfile.ModeHint(m).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("grocery", null)]
    [InlineData("clothing", null)]
    [InlineData("services", null)]
    [InlineData("grocery", "hozmag")]
    [InlineData("grocery", "bazaar")]
    [InlineData("grocery", "electronics")]
    public void Other_spheres_do_not_get_estimate_or_rentals_for_free(string baseSphere, string? detail)
    {
        SetSphere(baseSphere, detail);
        EstimateFeature.IsAvailable.Should().BeFalse($"{baseSphere}/{detail}: Смета — через Маркетплейс");
        MarketSpheres.Profile.HasRentalsByDefault.Should().BeFalse();
    }
}
