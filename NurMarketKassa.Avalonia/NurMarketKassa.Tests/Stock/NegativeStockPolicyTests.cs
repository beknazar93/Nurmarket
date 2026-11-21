using FluentAssertions;
using System.Text.Json.Nodes;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Stock;

/// <summary>2026-11-21, владелец: «если товара нет на складе, кассир может продавать в минус». Настройка «Продавать товар без остатка (в минус)» (SellWithoutStock, по умолчанию включена):
/// остаток 0 и сверх остатка, весовой 0 кг, лимит «+» в строке чека, поле allow_negative_stock в теле pos/checkout. Выключено — всё как раньше.</summary>
public sealed class NegativeStockPolicyTests
{
    private static void With(bool enabled, Action body)
    {
        var before = UserPreferences.Instance.SellWithoutStock;
        UserPreferences.Instance.SellWithoutStock = enabled;
        try { body(); }
        finally { UserPreferences.Instance.SellWithoutStock = before; }
    }

    [Fact]
    public void Default_IsOn()
    {
        new UserPreferences().SellWithoutStock.Should().BeTrue("владелец хочет, чтобы кассир мог продавать в минус");
    }

    [Theory]
    [InlineData(0d, 1d)]      // остатка нет совсем
    [InlineData(2d, 5d)]      // сверх остатка
    [InlineData(0d, 0.35d)]   // весовой товар, 0 кг
    public void Enabled_CanAddWithoutStock(double available, double qty)
    {
        With(true, () =>
        {
            NegativeStockPolicy.CanAdd(available, qty, isService: false).Should().BeTrue();
            NegativeStockPolicy.HasStock(available, qty, isService: false).Should().BeFalse("мягкая отметка «остаток будет в минусе» опирается на HasStock");
        });
    }

    [Theory]
    [InlineData(0d, 1d)]
    [InlineData(2d, 5d)]
    [InlineData(0d, 0.35d)]
    public void Disabled_BlocksWithoutStock(double available, double qty)
    {
        With(false, () => NegativeStockPolicy.CanAdd(available, qty, isService: false).Should().BeFalse());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnoughStockOrService_AlwaysAllowed(bool enabled)
    {
        With(enabled, () =>
        {
            NegativeStockPolicy.CanAdd(5d, 5d, isService: false).Should().BeTrue("ровно остаток — не минус");
            NegativeStockPolicy.CanAdd(1.2d, 1.2d, isService: false).Should().BeTrue("весовой товар ровно по остатку");
            NegativeStockPolicy.CanAdd(0d, 3d, isService: true).Should().BeTrue("услуга остатка не требует");
        });
    }

    [Fact]
    public void LineLimit_RemovedOnlyWhenEnabled()
    {
        With(true, () => NegativeStockPolicy.LineLimit(0d).Should().BeNull());
        With(false, () => NegativeStockPolicy.LineLimit(0d).Should().Be(0d));
    }

    [Fact]
    public void Basket_PlusOnZeroStockLine_Works_WhenEnabled()
    {
        With(true, () =>
        {
            using var h = new BasketVmHarness();
            var cart = h.Cart;
            var vm = new BasketPanelViewModel(cart, h.Prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), h.Windows, new FakeDialogs(),
                new InlineDispatcher(), stockLimitLookup: (_, _) => NegativeStockPolicy.LineLimit(0d));
            vm.AddProductFromCatalogWithOverride(CartTestHelper.CreateProduct("p0", "Кирешки", 40m), 1, 40d);

            var line = vm.Lines.Single();
            line.MaxStockQuantity.Should().BeNull("остаток 0, но продажа в минус разрешена — лимита нет");
            vm.IncreaseQuantityCommand.Execute(line);

            vm.Lines.Single().Quantity.Should().Be(2);
            h.Prompts.Questions.Should().BeEmpty("кассиру не задают вопросов");
            vm.Dispose();
        });
    }

    [Fact]
    public void Basket_PlusOnZeroStockLine_Stops_WhenDisabled()
    {
        With(false, () =>
        {
            using var h = new BasketVmHarness();
            var vm = new BasketPanelViewModel(h.Cart, h.Prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), h.Windows, new FakeDialogs(),
                new InlineDispatcher(), stockLimitLookup: (_, _) => NegativeStockPolicy.LineLimit(0d));
            vm.AddProductFromCatalogWithOverride(CartTestHelper.CreateProduct("p0", "Кирешки", 40m), 1, 40d);

            var line = vm.Lines.Single();
            vm.IncreaseQuantityCommand.Execute(line);

            vm.Lines.Single().Quantity.Should().Be(1, "как раньше: упёрлись в остаток 0");
            vm.Dispose();
        });
    }

    [Theory]
    [InlineData(true, 0.45d)]
    [InlineData(false, 0.35d)]
    public void Basket_WeighedZeroKg_PlusStep(bool enabled, double expected)
    {
        With(enabled, () =>
        {
            using var h = new BasketVmHarness();
            var vm = new BasketPanelViewModel(h.Cart, h.Prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), h.Windows, new FakeDialogs(),
                new InlineDispatcher(), stockLimitLookup: (_, _) => NegativeStockPolicy.LineLimit(0d));
            vm.AddProductFromCatalogWithOverride(CartTestHelper.CreateProduct("w0", "Сыр", 600m, mustWeigh: true), 0.35, 600d);

            vm.IncreaseQuantityCommand.Execute(vm.Lines.Single());

            vm.Lines.Single().Quantity.Should().BeApproximately(expected, 1e-6);
            vm.Dispose();
        });
    }

    [Fact]
    public void CheckoutBody_CarriesAllowNegativeStock_OnlyWhenEnabled()
    {
        With(true, () =>
        {
            var body = new JsonObject { ["print_receipt"] = false };
            NegativeStockPolicy.ApplyTo(body);
            body[NegativeStockPolicy.BodyField]!.GetValue<bool>().Should().BeTrue();
        });
        With(false, () =>
        {
            var body = new JsonObject { ["print_receipt"] = false };
            NegativeStockPolicy.ApplyTo(body);
            body.ContainsKey(NegativeStockPolicy.BodyField).Should().BeFalse("выключено — тело прежнее");
        });
    }

    [Fact]
    public void RetryFingerprint_DoesNotDependOnSetting()
    {
        var with = new JsonObject { ["print_receipt"] = false, [NegativeStockPolicy.BodyField] = true }.ToJsonString();
        var without = new JsonObject { ["print_receipt"] = false }.ToJsonString();
        QuickCheckoutBody.Fingerprint(with).Should().Be(QuickCheckoutBody.Fingerprint(without), "настройку могли переключить между попыткой и повтором — ключ повтора прежний");
    }
}
