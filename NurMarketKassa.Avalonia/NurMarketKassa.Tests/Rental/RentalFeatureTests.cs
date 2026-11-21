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
    [InlineData(true, false, false, true)]   // строймаркет — включено по умолчанию
    [InlineData(false, true, false, true)]   // модуль включён в Маркетплейсе
    [InlineData(false, false, true, true)]   // миграция: у компании уже есть данные проката
    [InlineData(false, false, false, false)] // новая компания в другой сфере — выключено
    public void IsEnabled_rule(bool hardware, bool module, bool existing, bool expected) =>
        RentalFeature.IsEnabled(hardware, module, () => existing).Should().Be(expected);

    [Fact]
    public void IsEnabled_does_not_look_for_data_when_answer_is_known()
    {
        var asked = false;
        RentalFeature.IsEnabled(true, false, () => asked = true).Should().BeTrue();
        asked.Should().BeFalse("строймаркет — ответ известен без чтения данных");
    }

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
    public void Migration_keeps_rentals_after_leaving_hardware_and_does_not_lose_data_on_sphere_change()
    {
        Sphere("grocery", "hardware");
        RentalFeature.EnableForExisting().Should().BeTrue("у компании уже есть прокат — признак ставится сам");
        UserPreferences.Instance.RentalEnabled.Should().BeTrue();
        RentalFeature.EnableForExisting().Should().BeFalse("повторно ничего не меняет");

        UserPreferences.Instance.MarketSphereDetail = null; // сменили сферу на «Продукты»
        MarketSpheres.HasRentals.Should().BeTrue("действующих клиентов не ломаем: пункты проката остаются");
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
