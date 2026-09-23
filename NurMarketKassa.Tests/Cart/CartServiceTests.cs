using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using System.Text.Json;

namespace NurMarketKassa.Tests.Cart;

public sealed class CartServiceTests : IDisposable
{
    private readonly CartService _sut = new();

    public void Dispose() => _sut.Dispose();

    [Fact]
    public void AddItem_AddsProductWithCorrectQuantityAndTotalAmount()
    {
        CartTestHelper.StartEmptyCart(_sut);
        var product = CartTestHelper.CreateProduct("sku-milk", "Молоко 3.2%", 120m);

        _sut.AddItem(product, 2);

        _sut.HasCart.Should().BeTrue();
        _sut.LineCount.Should().Be(1);
        _sut.TotalQuantity.Should().Be(2);
        _sut.TotalAmount.Should().Be(240m);

        var line = _sut.Items.Should().ContainSingle().Subject;
        line.ProductId.Should().Be("sku-milk");
        line.Name.Should().Be("Молоко 3.2%");
        line.Quantity.Should().Be(2);
        line.UnitPrice.Should().Be(120m);
        line.LineTotal.Should().Be(240m);
    }

    [Fact]
    public void AddItem_MergesQuantityForSamePieceProduct()
    {
        CartTestHelper.StartEmptyCart(_sut);
        var product = CartTestHelper.CreateProduct("sku-bread", "Хлеб", 50m);

        _sut.AddItem(product, 1);
        _sut.AddItem(product, 3);

        _sut.LineCount.Should().Be(1);
        _sut.TotalQuantity.Should().Be(4);
        _sut.TotalAmount.Should().Be(200m);
    }

    [Fact]
    public void AddItem_ToServerCart_MarksLocalSnapshotForMaterialization()
    {
        using var serverCart = JsonDocument.Parse(
            """{"id":"server-cart-2","items":[],"total":"0.00"}""");
        _sut.SetCart(serverCart.RootElement);
        _sut.CanRefresh.Should().BeTrue();

        _sut.AddItem(CartTestHelper.CreateProduct("sku-coffee", "Coffee", 250m), 1);

        _sut.LineCount.Should().Be(1);
        _sut.IsStaging.Should().BeTrue();
        _sut.CanRefresh.Should().BeFalse();
        _sut.CartId.Should().BeNull();
    }

    [Fact]
    public void UpdateQuantity_ChangesLineQuantityAndRecalculatesTotal()
    {
        CartTestHelper.StartEmptyCart(_sut);
        var product = CartTestHelper.CreateProduct("sku-tea", "Чай", 80m);
        _sut.AddItem(product, 1);

        var itemId = _sut.Items.Single().Id!;
        _sut.UpdateQuantity(itemId, 5);

        _sut.TotalQuantity.Should().Be(5);
        _sut.TotalAmount.Should().Be(400m);
        _sut.Items.Single().Quantity.Should().Be(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AddItem_RejectsInvalidQuantity(double quantity)
    {
        CartTestHelper.StartEmptyCart(_sut);
        var product = CartTestHelper.CreateProduct("sku-invalid", "Invalid", 10m);

        var action = () => _sut.AddItem(product, quantity);

        action.Should().Throw<ArgumentOutOfRangeException>();
        _sut.Items.Should().BeEmpty();
    }

    [Fact]
    public void OrderDiscount_RecalculatesTotal_AndCanBeCleared()
    {
        CartTestHelper.StartEmptyCart(_sut);
        _sut.AddItem(CartTestHelper.CreateProduct("sku-order-discount", "Discount", 200m), 1);

        ReceiptSnapshotCartEditor.PatchOrderDiscount(_sut, "10", null);
        _sut.TotalAmount.Should().Be(180m);

        ReceiptSnapshotCartEditor.PatchOrderDiscount(_sut, null, null);
        _sut.TotalAmount.Should().Be(200m);
    }

    [Fact]
    public void FixedDiscount_IsClampedToReceiptAmount()
    {
        CartTestHelper.StartEmptyCart(_sut);
        _sut.AddItem(CartTestHelper.CreateProduct("sku-fixed-discount", "Discount", 200m), 1);

        ReceiptSnapshotCartEditor.PatchOrderDiscount(_sut, null, "500");

        _sut.TotalAmount.Should().Be(0m);
        _sut.TotalDiscount.Should().Be(200m);
    }

    [Fact]
    public void OrderPercentDiscount_IsCalculatedAfterLineDiscounts()
    {
        CartTestHelper.StartEmptyCart(_sut);
        _sut.AddItem(CartTestHelper.CreateProduct("sku-combined-discount", "Discount", 200m), 1);
        var itemId = _sut.Items.Single().Id!;
        ReceiptSnapshotCartEditor.PatchLineDiscount(_sut, itemId, "percent", "10");

        ReceiptSnapshotCartEditor.PatchOrderDiscount(_sut, "10", null);

        _sut.TotalAmount.Should().Be(162m);
        _sut.TotalDiscount.Should().Be(38m);
    }

    [Theory]
    [InlineData("percent", "11", 11d, -1d)]
    [InlineData("sum", "100", -1d, 100d)]
    public void LineDiscount_PreservesSelectedType(
        string mode,
        string value,
        double expectedPercent,
        double expectedFixedAmount)
    {
        CartTestHelper.StartEmptyCart(_sut);
        _sut.AddItem(CartTestHelper.CreateProduct("sku-discount", "Discounted", 200m), 1);
        var itemId = _sut.Items.Single().Id!;

        ReceiptSnapshotCartEditor.PatchLineDiscount(_sut, itemId, mode, value);

        var line = _sut.Items.Single();
        line.DiscountPercent.Should().Be(expectedPercent < 0 ? null : (decimal)expectedPercent);
        line.FixedDiscountAmount.Should().Be(expectedFixedAmount < 0 ? null : (decimal)expectedFixedAmount);
    }

    [Fact]
    public void RemoveItem_RemovesLineAndZerosTotals()
    {
        CartTestHelper.StartEmptyCart(_sut);
        var product = CartTestHelper.CreateProduct("sku-juice", "Сок", 150m);
        _sut.AddItem(product, 2);

        var itemId = _sut.Items.Single().Id!;
        _sut.RemoveItem(itemId);

        _sut.LineCount.Should().Be(0);
        _sut.TotalQuantity.Should().Be(0);
        _sut.TotalAmount.Should().Be(0m);
        _sut.Items.Should().BeEmpty();
    }

    [Fact]
    public void Clear_EmptiesCartState()
    {
        CartTestHelper.StartEmptyCart(_sut);
        _sut.AddItem(CartTestHelper.CreateProduct("sku-water", "Вода", 40m), 3);

        _sut.Clear();

        _sut.HasCart.Should().BeFalse();
        _sut.LineCount.Should().Be(0);
        _sut.TotalQuantity.Should().Be(0);
        _sut.TotalAmount.Should().Be(0m);
        _sut.Items.Should().BeEmpty();
    }

    [Fact]
    public void AddItem_IsThreadSafe_WhenTenThreadsAddDifferentProducts()
    {
        CartTestHelper.StartEmptyCart(_sut);

        var products = Enumerable.Range(0, 10)
            .Select(i => CartTestHelper.CreateProduct($"sku-{i}", $"Товар {i}", (i + 1) * 10m))
            .ToArray();

        var expectedTotal = products.Sum(p => decimal.Parse(p.PriceLine.Split(' ')[0]));

        Parallel.For(0, products.Length, i => _sut.AddItem(products[i], 1));

        _sut.LineCount.Should().Be(10);
        _sut.TotalQuantity.Should().Be(10);
        _sut.TotalAmount.Should().Be(expectedTotal);
        _sut.Items.Select(i => i.ProductId).Should().BeEquivalentTo(products.Select(p => p.Id));
    }
}
