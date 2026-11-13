using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

/// <summary>2026-11-13 (решение владельца: «Ремонт и trade-in — доп. услуги в Маркетплейсе»): точки входа окон «Ремонт и сервис» и «Trade-in / скупка» в кассу.
/// Деньги идут ТОЛЬКО существующими путями кассы: внесение и изъятие — общая запись операции смены (RecordCashOperationAsync: журнал смены, отчёт, сервер, чек),
/// оплата работ и зачёт trade-in — строки «Доп. услуга» в чеке (положительная и отрицательная). Новых денежных путей мимо кассы и сервера нет.</summary>
public partial class MainWindow
{
    internal void NavigateRepair()
    {
        if (!UserPreferences.Instance.RepairUnlocked)
            return;
        RepairOrdersWindow.Open(this);
    }

    internal void NavigateTradeIn()
    {
        if (!UserPreferences.Instance.TradeInUnlocked)
            return;
        TradeInWindow.Open(this);
    }

    /// <summary>Итог текущего чека (для зачёта trade-in).</summary>
    internal decimal ServiceDeskCartTotal => (decimal)_viewModel.Basket.Total;

    internal bool ServiceDeskCartHasItems => _viewModel.Basket.HasItems;

    /// <summary>Строка «Доп. услуга» в чек: положительная — оплата работ, отрицательная (credit) — зачёт trade-in.</summary>
    internal bool ServiceDeskAddLine(string name, decimal amount, bool credit) =>
        amount > 0 && _viewModel.Basket.AddCustomItem(name, (double)amount, 1, credit);

    /// <summary>Внесение (deposit) или изъятие из кассы смены с основанием. false — смена закрыта, кассир отказался или запись не прошла (операции нет в журнале смены).</summary>
    internal async Task<bool> ServiceDeskCashAsync(bool withdrawal, decimal amount, string reason)
    {
        if (amount <= 0)
            return false;
        if (!_session.IsShiftOpen || string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.ActiveShiftId))
        {
            _prompts.ShowWarning(Tr.T(
                "Внесение и изъятие можно оформить только при открытой смене.",
                "Акча салууну жана алып коюуну ачык смена учурунда гана жасоого болот.",
                "Cash in and cash out can only be recorded while a shift is open.",
                "Para girişi ve çıkışı yalnızca vardiya açıkken yapılabilir.",
                "Kirim va chiqimni faqat smena ochiq bo'lganda rasmiylashtirish mumkin."));
            return false;
        }

        if (withdrawal)
        {
            if (CashWithdrawalGuard.Refusal(amount) is { } refusal)
            {
                await PosAlertDialog.ShowAsync(this, CashWithdrawalGuard.Title, refusal, PosAlertKind.Warning).ConfigureAwait(true);
                return false;
            }
            if (!await PosDialogs.ConfirmYesNoModalAsync(this, CashWithdrawalGuard.ConfirmText(amount) + "\n" + reason, CashWithdrawalGuard.Title,
                    confirmText: Tr.T($"Изъять {MoneyText.Som(amount)}", $"{MoneyText.Som(amount)} алуу", $"Withdraw {MoneyText.Som(amount)}",
                        $"{MoneyText.Som(amount)} çek", $"{MoneyText.Som(amount)} olish")).ConfigureAwait(true))
                return false;
        }

        var op = new CashOperationModel
        {
            Type = withdrawal ? "Изъятие" : "Внесение",
            Kind = withdrawal ? CashOperationKind.Withdrawal : CashOperationKind.Deposit,
            Amount = amount,
            Cashier = NurMarketKassa.AvaloniaHost.App.CurrentUserId ?? "—",
            Reason = reason,
            Comment = reason,
        };
        await RecordCashOperationAsync(op).ConfigureAwait(true);
        // RecordCashOperationAsync при занятом файле истории показывает предупреждение и выходит, ничего не записав — сверяем с журналом смены.
        try
        {
            return ShiftCashOperationsStore.LoadAll().Any(o => o.Id == op.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
