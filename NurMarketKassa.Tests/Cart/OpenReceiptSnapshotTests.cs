using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Cart;

public sealed class OpenReceiptSnapshotTests
{
    [Fact]
    public void SumProductQuantity_CountsProductReservationsInInactiveReceipt()
    {
        const string receipt =
            """
            {
              "items": [
                { "product_id": "sku-mila", "quantity": 2 },
                { "product_id": "SKU-MILA", "quantity": 3 },
                { "product_id": "sku-other", "quantity": 9 }
              ]
            }
            """;

        OpenReceiptSnapshot.SumProductQuantity(receipt, "sku-mila").Should().Be(5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    public void SumProductQuantity_ReturnsZero_ForInvalidReceipt(string? receipt)
    {
        OpenReceiptSnapshot.SumProductQuantity(receipt, "sku-mila").Should().Be(0);
    }
}
