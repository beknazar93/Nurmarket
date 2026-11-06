using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.Tests.Checkout;

/// <summary>2026-11-06: В9 «Оплата» (план «десктоп 10 из 10»): срок долга по умолчанию 14 дней (Д4-13), сегмент «Карта · QR» (Д4-15), цифровой блок (Д4-16).
/// Деньги (сдача, смешанная, итог) не менялись — отдельные тесты «до/после» сверяют, что они прежние. Общая статика (UserPreferences.Instance) — коллекция «ServerFeatures».</summary>
[Collection("ServerFeatures")]
public sealed class CheckoutWave9Tests
{
    private static CheckoutViewModel Create(double total) =>
        new(new CartTotalsCalculator.CartTotals { Subtotal = total, TotalDue = total, LineCount = 1 }, "", "");

    private static ClientOption Client() => new() { Id = "c1", DisplayName = "Асан · +996555000111", Name = "Асан", Phone = "+996555000111" };

    // ───────────── Д4-13: срок долга по умолчанию ─────────────

    [Fact]
    public void Debt_DefaultDueDate_Is14DaysFromToday_AndPaymentAllowedWithClient()
    {
        var vm = Create(1000);
        vm.PreselectClient(Client());
        var closed = false;
        vm.RequestClose += ok => closed = ok;

        vm.IsDebt = true;

        vm.DebtDueDate.Should().Be(DateTime.Today.AddDays(14));
        vm.IsDue14Selected.Should().BeTrue("срок «14 дней» подсвечен сам");
        vm.IsDebtDueMissing.Should().BeFalse();
        vm.DebtDueHint.Should().NotContain("Спросите", "красной причины «укажите срок» до действия нет");
        vm.CanPay.Should().BeTrue("клиент выбран, срок стоит, внесено 0 — весь чек в долг");

        vm.PayCommand.Execute(null);
        closed.Should().BeTrue();
    }

    [Fact]
    public void Debt_WithoutClient_StillBlocked_AndHintAsksForClient()
    {
        var vm = Create(1000);

        vm.IsDebt = true;

        vm.DebtDueDate.Should().NotBeNull();
        vm.CanPay.Should().BeFalse("без клиента «в долг» не оформить — как раньше");
        vm.PayBlockedHint.Should().Contain("клиент");
    }

    [Fact]
    public void Debt_ClearedDate_BlocksPaymentAsBefore()
    {
        var vm = Create(1000);
        vm.PreselectClient(Client());
        vm.IsDebt = true;

        vm.DebtDueDate = null;

        vm.CanPay.Should().BeFalse("пустой срок по-прежнему не даёт оплатить");
        vm.IsDebtDueMissing.Should().BeTrue();
        vm.PayBlockedHint.Should().Contain("до какого числа");
    }

    [Fact]
    public void Debt_ChosenDueDate_IsNotOverwrittenOnReturn()
    {
        var vm = Create(1000);
        vm.IsDebt = true;
        vm.SetDebtDueDaysCommand.Execute("30");

        vm.IsCash = true;
        vm.IsDebt = true;

        vm.DebtDueDate.Should().Be(DateTime.Today.AddDays(30), "выбранный срок остаётся");
        vm.IsDue30Selected.Should().BeTrue();
    }

    [Fact]
    public void Debt_Installment_KeepsItsOwnDates_NoDefaultSingleDueDate()
    {
        var vm = Create(900);
        vm.IsInstallment = true;

        vm.IsDebt = true;

        vm.DebtDueDate.Should().BeNull("при рассрочке срок «одним платежом» не ставится");
        vm.InstallmentFirstDate.Should().NotBeNull();
    }

    [Fact]
    public void Debt_PayNowAndSplit_Unchanged()
    {
        var vm = Create(1000);
        vm.PreselectClient(Client());
        vm.IsDebt = true;
        vm.DebtCashReceived = "300";

        vm.CanPay.Should().BeTrue();
        vm.DebtRemainingText.Should().NotBeNullOrEmpty();
        vm.DebtScheduleForApi.Should().NotBeNull();
        vm.DebtScheduleForApi!.Payments.Should().ContainSingle().Which.Amount.Should().Be(700m, "в долг уходит 1000 − 300");
    }

    [Fact]
    public void InstallmentLabel_IsShort_FullTextInHint()
    {
        var vm = Create(100);

        vm.InstallmentCheckText.Should().Be("Рассрочка");
        vm.InstallmentCheckHint.Should().Contain("несколько платежей");
    }

    // ───────────── Д4-15: Карта · QR ─────────────

    [Fact]
    public void CardPay_HidesQr_ButKeepsPaymentAndBank()
    {
        var qrPath = Path.GetTempFileName();
        var saved = UserPreferences.Instance.CheckoutCardPay;
        try
        {
            var vm = Create(500);
            vm.SelectedBank = new BankAccount { BankName = "Test Bank", QrCodeImagePath = qrPath };
            vm.IsTransfer = true;
            vm.IsQrPay.Should().BeTrue("без выбора — QR, как было");
            vm.HasQrCode.Should().BeTrue();
            vm.CanPay.Should().BeTrue();

            vm.IsCardPay = true;

            vm.HasQrCode.Should().BeFalse("картой через терминал QR не показывается");
            vm.QrCodePath.Should().BeNull();
            vm.ShowQrCard.Should().BeFalse();
            vm.CanPay.Should().BeTrue("банк по-прежнему выбран; сумма и способ те же");
            vm.PaymentMethod.Should().Be("transfer", "способ на сервер уходит прежний");
            UserPreferences.Instance.CheckoutCardPay.Should().BeTrue("выбор запоминается");

            vm.SetCardPayCommand.Execute("qr");
            vm.IsCardPay.Should().BeFalse();
            vm.HasQrCode.Should().BeTrue();
        }
        finally
        {
            UserPreferences.Instance.CheckoutCardPay = saved;
            File.Delete(qrPath);
        }
    }

    [Fact]
    public void CardPay_StartsFromLastChoice()
    {
        var saved = UserPreferences.Instance.CheckoutCardPay;
        try
        {
            UserPreferences.Instance.CheckoutCardPay = true;
            Create(100).IsCardPay.Should().BeTrue();
            UserPreferences.Instance.CheckoutCardPay = false;
            Create(100).IsCardPay.Should().BeFalse();
        }
        finally
        {
            UserPreferences.Instance.CheckoutCardPay = saved;
        }
    }

    // ───────────── Д4-16: цифровой блок ─────────────

    [Fact]
    public void Keypad_ShownOnlyInTouchTheme_ForAmountModes()
    {
        var saved = UserPreferences.Instance.AccentTheme;
        try
        {
            UserPreferences.Instance.AccentTheme = "nurmarket";
            Create(100).ShowKeypad.Should().BeFalse();

            UserPreferences.Instance.AccentTheme = "touch";
            var vm = Create(100);
            vm.ShowKeypad.Should().BeTrue("наличные");
            vm.IsTransfer = true;
            vm.ShowKeypad.Should().BeFalse("у безнала нет поля суммы");
            vm.IsMixed = true;
            vm.ShowKeypad.Should().BeTrue();
        }
        finally
        {
            UserPreferences.Instance.AccentTheme = saved;
        }
    }

    [Theory]
    [InlineData("", 0, 0, "7", "7", 1)]
    [InlineData("50,00", 0, 5, "7", "7", 1)]            // всё выделено — заменяет предложенную сумму
    [InlineData("12", 2, 2, "5", "125", 3)]
    [InlineData("12", 2, 2, ",", "12,", 3)]
    [InlineData("12,5", 4, 4, ",", "12,5", 4)]           // вторая запятая не вводится
    [InlineData("12,50", 5, 5, "7", "12,50", 5)]         // третий знак после запятой не вводится
    [InlineData("", 0, 0, ",", "0,", 2)]                 // запятая первой — «0,»
    [InlineData("0", 1, 1, "5", "5", 1)]                 // «05» → «5»
    [InlineData("0,5", 3, 3, "0", "0,50", 4)]
    [InlineData("125", 3, 3, "back", "12", 2)]
    [InlineData("125", 0, 3, "back", "", 0)]             // стереть выделенное
    [InlineData("", 0, 0, "back", "", 0)]
    [InlineData("123456789012", 12, 12, "3", "123456789012", 12)] // не больше 12 цифр
    public void Keypad_Apply(string text, int selStart, int selEnd, string key, string expected, int caret)
    {
        var (result, resultCaret) = CheckoutKeypad.Apply(text, selStart, selEnd, key);

        result.Should().Be(expected);
        resultCaret.Should().Be(caret);
    }

    [Fact]
    public void Keypad_TypedAmount_GivesSameChangeAsKeyboard()
    {
        var vm = Create(670);
        // Кассир набирает 1000 кнопками поверх выделенной суммы, как клавишами.
        var text = vm.CashReceived;
        var (t, caret) = CheckoutKeypad.Apply(text, 0, text.Length, "1");
        foreach (var d in new[] { "0", "0", "0" })
            (t, caret) = CheckoutKeypad.Apply(t, caret, caret, d);
        vm.CashReceived = t;

        t.Should().Be("1000");
        vm.CanPay.Should().BeTrue();
        vm.ChangeAmountDisplay.Should().Be(MoneyText.Amount(330.0));
    }

    // ───────────── деньги: до/после — сдача и смешанная прежние ─────────────

    [Fact]
    public void Cash_InsufficientAndChange_Unchanged()
    {
        var vm = Create(670);

        vm.CashReceived = "600";
        vm.CanPay.Should().BeFalse();
        vm.ChangeStatusText.Should().Contain("Недостаточно");

        vm.CashReceived = "700";
        vm.CanPay.Should().BeTrue();
        vm.ChangeAmountDisplay.Should().Be(MoneyText.Amount(30.0));
    }

    [Fact]
    public void Mixed_DefaultAllCash_PayBlockedUntilBothPartsPositive_Unchanged()
    {
        var vm = Create(1000);

        vm.IsMixed = true;
        vm.CanPay.Should().BeFalse("обе части должны быть больше нуля");

        vm.MixedCashAmount = "400";
        vm.MixedNonCashAmount.Should().NotBeNullOrEmpty();
        vm.CanPay.Should().BeTrue();
    }
}
