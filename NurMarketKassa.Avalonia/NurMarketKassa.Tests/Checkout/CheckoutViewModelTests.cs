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
        viewModel.CashReceived = "2000.00"; // ввод с точкой тоже разбирается

        viewModel.IsTransfer = true;
        viewModel.IsCash = true;

        viewModel.PaymentMethod.Should().Be("cash");
        // 2026-11-05: с 22.10.2026 (аудит 1.17.59) поле показывает запятую — «2000,00»; на сервер и в расчёт уходит точка.
        viewModel.CashReceived.Should().Be("2000,00");
        viewModel.CanPay.Should().BeTrue();
    }

    [Fact]
    public void SwitchingBackToCash_UsesExactTotal_WhenPreviousCashIsInvalid()
    {
        var viewModel = CreateViewModel(1870.09);
        viewModel.CashReceived = "";

        viewModel.IsTransfer = true;
        viewModel.IsCash = true;

        // 2026-11-05: запятая в поле ввода (см. выше), расчёт и API — через точку.
        viewModel.CashReceived.Should().Be("1870,09");
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

    // 2026-11-13 (находка проверяющего п. 4): скидка на чек, введённая в окне оплаты, проверяется на «ниже закупки» ДО приёма денег.
    [Fact]
    public void PaymentIsBlocked_WhenDiscountEnteredInWindowGoesBelowCost_AndFreedAfterChange()
    {
        var viewModel = CreateViewModel(100);
        var seen = new List<double>();
        // закупка 80: скидка на чек больше 20 уводит цену ниже закупки
        viewModel.BelowCostCheck = discount => { seen.Add(discount); return discount > 20 ? "ниже закупки" : null; };
        viewModel.CanPay.Should().BeTrue("скидки нет — продажа не ниже закупки");

        viewModel.IsDiscountSum = true;
        viewModel.DiscountInput = "30";
        viewModel.HasBelowCostBlock.Should().BeTrue();
        viewModel.BelowCostBlockText.Should().Be("ниже закупки");
        viewModel.CanPay.Should().BeFalse("оплату по QR/карте не принимаем, пока скидка уводит цену ниже закупки");
        viewModel.HasPayBlockedHint.Should().BeTrue();
        var closed = false;
        viewModel.RequestClose += _ => closed = true;
        viewModel.PayCommand.Execute(null);
        closed.Should().BeFalse();

        viewModel.DiscountInput = "10";
        viewModel.HasBelowCostBlock.Should().BeFalse();
        viewModel.CanPay.Should().BeTrue();
        seen.Should().Contain(30d).And.Contain(10d);
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
