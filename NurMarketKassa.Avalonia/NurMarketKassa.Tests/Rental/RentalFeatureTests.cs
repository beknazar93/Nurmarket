using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Rental;

/// <summary>2026-11-20, решение владельца: «аренда по умолчанию только в строймаге, у остальных — через Маркетплейс». Прокат включён в «Хозтовары и стройматериалы»;
/// в продуктах, одежде, электронике, канцтоварах и автозапчастях выключен, пока модуль не включён в Маркетплейсе или пока у компании нет данных проката (миграция).
/// Все входы (меню ☰, окно оплаты, раздел владельца) идут через MarketSpheres.HasRentals = RentalFeature.IsAvailable.</summary>
[Collection("Wave5Database")]
public sealed class RentalFeatureTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly bool _enabled = UserPreferences.Instance.RentalEnabled;

    public void Dispose()
    {
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        UserPreferences.Instance.RentalEnabled = _enabled;
    }

    private static void Sphere(string baseSphere, string? detail)
    {
        UserPreferences.Instance.MarketSphere = baseSphere;
        UserPreferences.Instance.MarketSphereDetail = detail;
        UserPreferences.Instance.RentalEnabled = false;
    }

    [Theory]
    [InlineData(true, false, true)]   // строймаркет — входит в тариф
    [InlineData(false, true, true)]   // модуль включён в Маркетплейсе
    [InlineData(false, false, false)] // другая сфера без модуля — выключено (старые данные проката не включают, 21.11.2026)
    public void IsEnabled_rule(bool hardware, bool module, bool expected) =>
        RentalFeature.IsEnabled(hardware, module).Should().Be(expected);

    [Theory]
    [InlineData("grocery", null)]
    [InlineData("clothing", null)]
    [InlineData("grocery", "electronics")]
    [InlineData("grocery", "stationery")]
    [InlineData("grocery", "autoparts")]
    public void Other_spheres_have_no_rentals_by_default(string baseSphere, string? detail)
    {
        Sphere(baseSphere, detail);
        MarketSpheres.HasRentals.Should().BeFalse($"{baseSphere}/{detail}: аренды по умолчанию нет — её подключают в Маркетплейсе");
        RentalFeature.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Hardware_has_rentals_by_default()
    {
        Sphere("grocery", "hardware");
        MarketSpheres.IsHardware.Should().BeTrue();
        MarketSpheres.HasRentals.Should().BeTrue();
    }

    [Fact]
    public void Marketplace_module_turns_rentals_on_in_any_sphere()
    {
        Sphere("grocery", null);
        MarketSpheres.HasRentals.Should().BeFalse();
        UserPreferences.Instance.RentalEnabled = true;
        MarketSpheres.HasRentals.Should().BeTrue();
    }

    [Fact]
    public void Old_rental_data_does_not_turn_the_module_on()
    {
        // 2026-11-21 (решение владельца): у «Продуктового» с прежними прокатами пункт «Прокат» не появляется сам; данные проката не удаляются.
        Sphere("grocery", null);
        UserPreferences.Instance.RentalEnabled = false;
        RentalFeature.IsAvailable.Should().BeFalse("старые данные проката модуль не включают");
        MarketSpheres.HasRentals.Should().BeFalse();
    }

    [Fact]
    public void Module_stays_on_after_leaving_hardware_when_owner_turned_it_on()
    {
        Sphere("grocery", "hardware");
        UserPreferences.Instance.RentalEnabled = true; // владелец включил в Маркетплейсе
        UserPreferences.Instance.MarketSphereDetail = null; // сменили сферу на «Продукты»
        MarketSpheres.HasRentals.Should().BeTrue("включённый владельцем модуль остаётся");
    }

    [Fact]
    public void Price_is_free_in_one_place() => RentalFeature.PriceSom.Should().Be(0m);

    [Theory]
    [InlineData("grocery", null, false)]
    [InlineData("clothing", null, false)]
    [InlineData("grocery", "hardware", true)]
    public void Kassa_menu_item_follows_rental_feature(string baseSphere, string? detail, bool expected)
    {
        Sphere(baseSphere, detail);
        var menu = new SideMenuViewModel(null!, () => { }) { NavigateRentalAction = () => { } };
        menu.ShowRental.Should().Be(expected);
        UserPreferences.Instance.RentalEnabled = true;
        menu.RefreshEntitlements();
        menu.ShowRental.Should().BeTrue("модуль включён в Маркетплейсе — пункт появился");
    }

    [Theory]
    [InlineData("grocery", null, false)]
    [InlineData("grocery", "autoparts", false)]
    [InlineData("grocery", "hardware", true)]
    public void Checkout_rental_link_follows_rental_feature(string baseSphere, string? detail, bool expected)
    {
        Sphere(baseSphere, detail);
        var vm = new CheckoutViewModel(new CartTotalsCalculator.CartTotals { Subtotal = 100, TotalDue = 100, LineCount = 1 }, "", "", null);
        vm.ShowRentalButton.Should().Be(expected);
    }
}
