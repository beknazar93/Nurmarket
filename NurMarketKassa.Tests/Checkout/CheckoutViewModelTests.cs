using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.Tests.Checkout;

public sealed class CheckoutViewModelTests
{
    [Fact]
    public void SwitchingTransferThenCash_RestoresCashAndEnablesPayment()
    {
        var viewModel = CreateViewModel(1870.09);
        viewModel.CashReceived = "2000.00";

        viewModel.IsTransfer = true;
        viewModel.IsCash = true;

        viewModel.PaymentMethod.Should().Be("cash");
        viewModel.CashReceived.Should().Be("2000.00");
        viewModel.CanPay.Should().BeTrue();
    }

    [Fact]
    public void SwitchingBackToCash_UsesExactTotal_WhenPreviousCashIsInvalid()
    {
        var viewModel = CreateViewModel(1870.09);
        viewModel.CashReceived = "";

        viewModel.IsTransfer = true;
        viewModel.IsCash = true;

        viewModel.CashReceived.Should().Be("1870.09");
        viewModel.CanPay.Should().BeTrue();
    }

    [Fact]
    public void TransferPayment_IsEnabledOnlyForExistingQrFile()
    {
        var qrPath = Path.GetTempFileName();
        try
        {
            var viewModel = CreateViewModel(100);
            viewModel.SelectedBank = new BankAccount
            {
                BankName = "Test Bank",
                QrCodeImagePath = qrPath,
            };
            viewModel.IsTransfer = true;

            viewModel.HasQrCode.Should().BeTrue();
            viewModel.CanPay.Should().BeTrue();
        }
        finally
        {
            File.Delete(qrPath);
        }
    }

    [Fact]
    public void PaymentIsBlocked_WhenFixedDiscountExceedsReceiptAmount()
    {
        var viewModel = CreateViewModel(100);
        var closed = false;
        viewModel.RequestClose += _ => closed = true;
        viewModel.IsDiscountSum = true;
        viewModel.DiscountInput = "150";

        viewModel.PayCommand.Execute(null);

        closed.Should().BeFalse();
        viewModel.ErrorMessage.Should().Contain("не может превышать");
    }

    private static CheckoutViewModel CreateViewModel(double total) =>
        new(
            new CartTotalsCalculator.CartTotals
            {
                Subtotal = total,
                TotalDue = total,
                LineCount = 1,
            },
            "",
            "");
}
