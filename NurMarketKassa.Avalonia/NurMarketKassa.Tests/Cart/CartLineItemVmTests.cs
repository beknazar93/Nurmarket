using FluentAssertions;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Cart;

public sealed class CartLineItemVmTests
{
    [Fact]
    public void CanIncrease_IsFalse_WhenUnitStockLimitIsReached()
    {
        var line = new CartLineItemVm
        {
            Quantity = 5,
            MaxStockQuantity = 5,
            IsWeight = false,
        };

        line.CanIncrease.Should().BeFalse();
    }

    [Fact]
    public void CanIncrease_IsTrue_WhenOneUnitRemains()
    {
        var line = new CartLineItemVm
        {
            Quantity = 4,
            MaxStockQuantity = 5,
            IsWeight = false,
        };

        line.CanIncrease.Should().BeTrue();
    }

    [Fact]
    public void CanIncrease_UsesWeightStep_ForWeightedProduct()
    {
        var line = new CartLineItemVm
        {
            Quantity = 0.9,
            MaxStockQuantity = 1,
            IsWeight = true,
        };

        line.CanIncrease.Should().BeTrue();

        line.Quantity = 1;
        line.CanIncrease.Should().BeFalse();
    }

    [Fact]
    public void NumberedTitle_ContainsOneBasedItemNumber()
    {
        var line = new CartLineItemVm { ItemNumber = 3, Title = "Чай" };

        line.NumberedTitle.Should().Be("#3. Чай");
    }

    [Fact]
    public void HasDiscount_IsTrue_WhenLineDiscountExists()
    {
        var line = new CartLineItemVm
        {
            UnitPrice = 200,
            Quantity = 1,
            LineTotal = 100,
            DiscountAmount = 100,
        };

        line.HasDiscount.Should().BeTrue();
    }

    [Fact]
    public void HasDiscount_IsFalse_ForRegularPrice()
    {
        var line = new CartLineItemVm
        {
            UnitPrice = 200,
            Quantity = 1,
            LineTotal = 200,
        };

        line.HasDiscount.Should().BeFalse();
    }

    [Fact]
    public void DiscountDisplayText_UsesPercent_WhenPercentageDiscountWasSelected()
    {
        var line = new CartLineItemVm
        {
            UnitPrice = 200,
            Quantity = 1,
            LineTotal = 178,
            DiscountAmount = 22,
            DiscountPercent = 11,
        };

        line.IsPercentageDiscount.Should().BeTrue();
        line.DiscountDisplayText.Should().Be("-11%");
    }

    [Fact]
    public void DiscountDisplayText_UsesSom_WhenFixedDiscountWasSelected()
    {
        var line = new CartLineItemVm
        {
            UnitPrice = 200,
            Quantity = 1,
            LineTotal = 100,
            DiscountAmount = 100,
            FixedDiscountAmount = 100,
        };

        line.IsPercentageDiscount.Should().BeFalse();
        line.DiscountDisplayText.Should().Be("-100 сом");
    }

    // 2026-11-27 (живой проход): весовой «по сумме» — 100 сом при 70 сом/кг = 1,429 кг (100,03), строка ровно 100.
    // Раньше значок показывал «-0 сом» (DiscountAmount = 0); теперь настоящую разницу, а меньше копейки — ничего.
    [Fact]
    public void DiscountDisplayText_WeighBySumRounding_ShowsRealCentsNotZero()
    {
        var line = new CartLineItemVm { UnitPrice = 70, Quantity = 1.429, LineTotal = 100, DiscountAmount = 0 };
        line.HasDiscount.Should().BeTrue();
        line.DiscountDisplayText.Should().NotStartWith("-0 ").And.Contain("0,03");

        var noise = new CartLineItemVm { UnitPrice = 70, Quantity = 1, LineTotal = 69.999, DiscountAmount = 0 };
        noise.DiscountDisplayText.Should().BeEmpty();
    }
}
