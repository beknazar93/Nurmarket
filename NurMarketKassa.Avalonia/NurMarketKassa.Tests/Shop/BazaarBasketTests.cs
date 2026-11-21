using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.ViewModels.Main;
using Xunit;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-21 («Базар»): корзина кассы — цена по количеству прямо в строках, кнопки «Торг», оптовая цена по умолчанию в режиме «Опт». Модель корзины — настоящая (BasketVmHarness),
/// настройки — общий UserPreferences.Instance (значения возвращаются), файл ступеней — временный.</summary>
[Collection("UserPreferences")]
public sealed class BazaarBasketTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _json = UserPreferences.Instance.SphereSettingsJson;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-bazaar-basket-" + Guid.NewGuid().ToString("N")[..8]);

    public BazaarBasketTests()
    {
        Directory.CreateDirectory(_dir);
        BazaarVolumePrices.FileOverride = Path.Combine(_dir, "volume.json");
        BazaarVolumePrices.ApiOverride = () => null;
        BazaarVolumePrices.ResetForTests();
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.SphereSettingsJson = _json;
        BazaarVolumePrices.ApiOverride = null;
        BazaarVolumePrices.FileOverride = null;
        BazaarVolumePrices.ResetForTests();
        BazaarHooks.HaggleLine = null;
        BazaarHooks.HaggleCheck = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static void Bazaar(BazaarMode mode)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = "bazaar";
        p.SphereSettingsJson = null;
        SphereSettings.SetBazaarMode(mode);
    }

    private static void OtherSphere()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = MarketSpheres.Grocery;
        p.MarketSphereDetail = null;
        p.SphereSettingsJson = null;
    }

    [Fact]
    public void Volume_PriceByQuantity_IsSetInTheBasketLine_AndShownUnderIt()
    {
        Bazaar(BazaarMode.Retail);
        var productId = Guid.NewGuid().ToString();
        BazaarVolumePrices.SetLocalTiers(productId, new[] { new VolumeTier(3m, 90m), new VolumeTier(10m, 80m) }, 100m).Should().BeTrue();
        using var h = new BasketVmHarness();

        h.AddProduct(productId, "Кроссовки", 100, qty: 2);
        h.Vm.Lines.Single().UnitPrice.Should().Be(100d);
        h.Vm.Lines.Single().PriceNote.Should().BeEmpty();

        h.AddProduct(productId, "Кроссовки", 100, qty: 1);
        h.Vm.Lines.Should().ContainSingle("тот же товар — та же строка");
        h.Vm.Lines.Single().Quantity.Should().Be(3);
        h.Vm.Lines.Single().UnitPrice.Should().Be(90d);
        h.Vm.Lines.Single().PriceNote.Should().Contain("3").And.Contain("90");
        h.Vm.Lines.Single().PriceQuantityLine.Should().Contain(h.Vm.Lines.Single().PriceNote);

        h.AddProduct(productId, "Кроссовки", 100, qty: 7);
        h.Vm.Lines.Should().ContainSingle();
        h.Vm.Lines.Single().Quantity.Should().Be(10);
        h.Vm.Lines.Single().UnitPrice.Should().Be(80d);
        h.Vm.Lines.Single().LineTotal.Should().Be(800d);
    }

    [Fact]
    public void Volume_WithoutTheModule_OrInOtherSpheres_PriceStaysRetail()
    {
        var productId = Guid.NewGuid().ToString();
        BazaarVolumePrices.SetLocalTiers(productId, new[] { new VolumeTier(3m, 90m) }, 100m);

        Bazaar(BazaarMode.Retail);
        BazaarModules.SetOwnerChoice(BazaarModules.VolumePrice, false);
        using (var off = new BasketVmHarness())
        {
            off.AddProduct(productId, "Кроссовки", 100, qty: 5);
            off.Vm.Lines.Single().UnitPrice.Should().Be(100d, "модуль выключен владельцем");
        }

        OtherSphere();
        using var other = new BasketVmHarness();
        other.AddProduct(productId, "Кроссовки", 100, qty: 5);
        other.Vm.Lines.Single().UnitPrice.Should().Be(100d, "в других сферах цена по количеству не действует");
        other.Vm.Lines.Single().ShowHaggle.Should().BeFalse();
    }

    [Fact]
    public async Task Haggle_Buttons_AppearOnlyWithTheModule_AndCallTheWindowHooks()
    {
        Bazaar(BazaarMode.Retail);
        var calledCheck = 0;
        CartLineItemVm? calledLine = null;
        BazaarHooks.HaggleCheck = () => { calledCheck++; return Task.CompletedTask; };
        BazaarHooks.HaggleLine = line => { calledLine = line; return Task.CompletedTask; };
        using var h = new BasketVmHarness();
        h.Vm.ShowHaggle.Should().BeTrue();
        h.AddProduct("p1", "Куртка", 1247);

        var line = h.Vm.Lines.Single();
        line.ShowHaggle.Should().BeTrue();
        line.HaggleCommand.Should().NotBeNull();
        line.HaggleText.Should().NotBeNullOrWhiteSpace();
        ((System.Windows.Input.ICommand)h.Vm.HaggleCheckCommand).CanExecute(null).Should().BeTrue();

        h.Vm.HaggleCheckCommand.Execute(null);
        line.HaggleCommand!.Execute(line);
        await Task.Delay(50);
        calledCheck.Should().Be(1);
        calledLine.Should().BeSameAs(line);

        BazaarModules.SetOwnerChoice(BazaarModules.Haggle, false);
        h.Vm.ShowHaggle.Should().BeFalse("владелец выключил торг");
        OtherSphere();
        using var other = new BasketVmHarness();
        other.Vm.ShowHaggle.Should().BeFalse();
    }

    [Fact]
    public void WholesaleMode_StartsEveryNewReceiptAsWholesale_RetailAndOtherSpheresDoNot()
    {
        Bazaar(BazaarMode.Wholesale);
        using (var w = new BasketVmHarness())
        {
            w.AddProduct("p1", "Куртка", 100);
            w.Vm.IsWholesaleMode.Should().BeTrue("режим «Опт»: оптовая цена по умолчанию");
            w.Vm.IsWholesaleMode = false; // кассир снял тумблер на этом чеке
            w.AddProduct("p2", "Шарф", 50);
            w.Vm.IsWholesaleMode.Should().BeFalse("выбор кассира на чеке не возвращается сам");
        }

        Bazaar(BazaarMode.Retail);
        using (var r = new BasketVmHarness())
        {
            r.AddProduct("p1", "Куртка", 100);
            r.Vm.IsWholesaleMode.Should().BeFalse();
        }

        OtherSphere();
        using var other = new BasketVmHarness();
        other.AddProduct("p1", "Куртка", 100);
        other.Vm.IsWholesaleMode.Should().BeFalse("вне «Базара» умолчание — розница, как всегда");
    }
}
