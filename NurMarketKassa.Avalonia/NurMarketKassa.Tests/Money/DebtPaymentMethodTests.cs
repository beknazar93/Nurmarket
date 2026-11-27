using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 (владелец: «при оплате долга должно быть видно — наличными или безналичными»): способ оплаты долга в кассе.
/// Что проверяется: по умолчанию кассе разрешены оба способа (раньше безнал был выключен, и выбора в окне не было); способ по умолчанию — наличные,
/// последний выбор кассира запоминается (если он сейчас разрешён); оплата долга наличными и безналом попадает в РАЗНЫЕ строки итогов смены (журнал
/// смены различает их видом события), в «в ящике должно быть» безнал не входит (это проверяет UiTests DebtPaymentMethodUiTests по печатному отчёту).</summary>
[Collection("ServerFeatures")]
public sealed class DebtPaymentMethodTests
{
    private static readonly string[] Both = [DebtPaymentSettings.MethodCash, DebtPaymentSettings.MethodTransfer];

    [Fact]
    public void Defaults_CashierGetsCashAndNonCash_OwnerToo()
    {
        var o = new DebtPaymentOptions();
        o.KassaCash.Should().BeTrue();
        o.KassaTransfer.Should().BeTrue("2026-11-26: выбор «Наличные / Безнал» виден по умолчанию");
        o.OwnerCash.Should().BeTrue();
        o.OwnerTransfer.Should().BeTrue();
        o.Normalized().SameAs(o).Should().BeTrue();
    }

    [Fact]
    public void DefaultMethod_NothingRemembered_IsCash()
    {
        DebtPaymentSettings.DefaultMethod(Both, null).Should().Be(DebtPaymentSettings.MethodCash);
    }

    [Fact]
    public void DefaultMethod_RememberedTransfer_AllowedNow_IsTransfer()
    {
        DebtPaymentSettings.DefaultMethod(Both, DebtPaymentSettings.MethodTransfer).Should().Be(DebtPaymentSettings.MethodTransfer);
    }

    [Fact]
    public void DefaultMethod_RememberedTransfer_NowSwitchedOff_FallsBackToCash()
    {
        DebtPaymentSettings.DefaultMethod([DebtPaymentSettings.MethodCash], DebtPaymentSettings.MethodTransfer).Should().Be(DebtPaymentSettings.MethodCash);
    }

    [Fact]
    public void DefaultMethod_OnlyTransferAllowed_IsTransfer_EvenWhenCashRemembered()
    {
        DebtPaymentSettings.DefaultMethod([DebtPaymentSettings.MethodTransfer], DebtPaymentSettings.MethodCash).Should().Be(DebtPaymentSettings.MethodTransfer);
    }

    [Fact]
    public void DefaultMethod_JunkRemembered_IsFirstAllowed()
    {
        DebtPaymentSettings.DefaultMethod(Both, "bitcoin").Should().Be(DebtPaymentSettings.MethodCash);
        DebtPaymentSettings.DefaultMethod([], null).Should().Be(DebtPaymentSettings.MethodCash);
    }

    [Fact]
    public void RememberMethod_IsKept_JunkIsIgnored()
    {
        var prefs = UserPreferences.Instance;
        var before = prefs.LastDebtPaymentMethod;
        try
        {
            DebtPaymentSettings.RememberMethod(DebtPaymentSettings.MethodTransfer);
            prefs.LastDebtPaymentMethod.Should().Be(DebtPaymentSettings.MethodTransfer);
            DebtPaymentSettings.RememberMethod("bitcoin");
            prefs.LastDebtPaymentMethod.Should().Be(DebtPaymentSettings.MethodTransfer, "чужое значение не запоминается");
            DebtPaymentSettings.RememberMethod(DebtPaymentSettings.MethodCash);
            prefs.LastDebtPaymentMethod.Should().Be(DebtPaymentSettings.MethodCash);
        }
        finally
        {
            prefs.LastDebtPaymentMethod = before;
        }
    }

    [Fact]
    public void ShiftSummary_DebtPayments_CashAndNonCash_GoToTheirOwnLines()
    {
        var id = "dpm-shift-" + Guid.NewGuid().ToString("N")[..10];
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPayment, id, "pay-debt:" + Guid.NewGuid().ToString("N")[..6], 300, "Нан");
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPaymentNonCash, id, "pay-debt:" + Guid.NewGuid().ToString("N")[..6], 70, "Нан");
        var totals = ShiftEventsStore.TotalsForShift(id);
        totals[ShiftEventsStore.KindDebtPayment].Should().Be(300);
        totals[ShiftEventsStore.KindDebtPaymentNonCash].Should().Be(70);

        UserPreferences.Instance.Language = AppLanguage.Russian;
        var shift = new ShiftModel
        {
            Id = id, ShiftNumber = "1", Cashier = "Тест", Status = "Закрыта",
            OpenedAt = new DateTime(2026, 11, 26, 9, 0, 0), ClosedAt = new DateTime(2026, 11, 26, 21, 0, 0),
            Revenue = 0m, SalesCount = 0, CashSales = 0m, NonCashSales = 0m, OpeningCash = 500m, ExpectedCash = 800m, ClosingCash = 800m,
        };
        var lines = TelegramBotService.BuildShiftSummary(shift, deposits: 0m, withdrawals: 0m, shopName: "ТЕСТ").Split('\n');
        var cashLine = lines.Single(l => l.Contains("Оплата долгов наличными"));
        var nonCashLine = lines.Single(l => l.Contains("Оплата долгов безналом"));
        cashLine.Should().Contain("300").And.NotContain("70");
        nonCashLine.Should().Contain("70").And.NotContain("300");
    }
}
