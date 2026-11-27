using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-27 (живой проход, A — деньги): из чего строится Z-отчёт сразу после закрытия смены (вынесено из MainWindow.ShowShiftClosedReport, чтобы проверять тестом).
/// Живой случай: сервер не ответил на закрытие за 3 с, касса закрыла смену у себя, а Z собрался из журнала кассы — выручка 423 вместо 1 189,75, «Остаток долга 65» (долг уже оплачен),
/// «Корректировка +838,98». При этом окно «Закрыть смену» уже держало полные итоги сервера (список смен и отчёт смены). Порядок теперь такой:
/// 1) ответ самого закрытия (fresh) — как раньше, отчёт окончательный;
/// 2) нет ответа, смена серверная, а итоги сервера до закрытия есть — цифры из них (отчёт смены сервера применяет окно Z), отчёт помечен «предварительный»
///    и сам обновится, когда сервер отдаст отчёт закрытой смены;
/// 3) ничего с сервера нет — данные кассы (LocalShiftTotals + журналы смены в окне), тоже с пометкой; остаток долга при оплатах долга в смене неизвестен (null), а не «долг целиком».</summary>
public static class ShiftClosedReportBuilder
{
    public sealed record Result(ShiftModel Shift, ServerShiftReport? PreCloseReport, bool Provisional, bool LocalReport);

    public static bool IsServerShift(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !id.StartsWith("offline-", StringComparison.OrdinalIgnoreCase);

    /// <param name="expectedForReport">«В ящике должно быть» из окна закрытия уже БЕЗ поправок ExchangeCashCorrections (окно Z прибавит их само).</param>
    /// <param name="localDebtPaid">Оплаты долгов в смене по журналу кассы (наличные + безнал) — только для запасного пути 3.</param>
    public static Result Build(string? shiftId, ShiftBalanceHelper.ShiftTotals? before, CashShiftClosingTotals? fresh, ServerShiftReport? preCloseReport,
        LocalShiftTotals.Result? local, DateTime? openedAt, DateTime closedAt, string cashier, decimal actualClosingCash, decimal? expectedForReport,
        decimal? localOpening, decimal localDebtPaid)
    {
        var serverShift = IsServerShift(shiftId);
        // Отчёт сервера другой смены (касса успела сменить смену) не подставляем.
        if (preCloseReport is not null && !string.IsNullOrWhiteSpace(preCloseReport.ShiftId) && !string.Equals(preCloseReport.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase))
            preCloseReport = null;
        // code-review 27.11: только отчёт смены сервера, полученный окном закрытия; снимок списка смен (before) мог устареть без связи — им цифры продаж не подменяем.
        var hasServerBefore = fresh is null && serverShift && preCloseReport is not null;
        var useLocal = fresh is null && !hasServerBefore && local is not null;

        decimal? debt;
        if (fresh is not null)
            debt = fresh.DebtSales ?? before?.DebtSales;
        else if (hasServerBefore)
            debt = preCloseReport?.Debt ?? before?.DebtSales;
        else if (useLocal)
            debt = localDebtPaid > 0.005m ? null : local!.DebtSales; // долг могли погасить в этой же смене — без сервера остаток неизвестен
        else
            debt = before?.DebtSales;

        var shift = new ShiftModel
        {
            Id = shiftId ?? "",
            ShiftNumber = shiftId ?? "",
            OpenedAt = openedAt,
            ClosedAt = closedAt,
            Cashier = cashier,
            Status = "Закрыта",
            Revenue = fresh?.TotalSales ?? (useLocal ? local!.TotalSales : null as decimal?) ?? before?.TotalSales ?? 0m,
            ClosingCash = actualClosingCash,
            OpeningCash = fresh?.OpeningCash ?? before?.OpeningCash ?? preCloseReport?.OpeningCash ?? (useLocal ? localOpening : null as decimal?),
            CashSales = fresh?.CashSales ?? (useLocal ? local!.CashSales : null as decimal?) ?? before?.CashSales,
            NonCashSales = fresh?.NonCashSales ?? (useLocal ? local!.NonCashSales : null as decimal?) ?? before?.NonCashSales,
            DebtSales = debt,
            SalesCount = fresh?.SalesCount ?? (useLocal ? local!.SalesCount : null as int?) ?? before?.SalesCount,
            // Расход и приход — с сервера (см. MainWindow.ShowShiftClosedReport).
            ExpenseTotal = before?.ExpenseTotal,
            IncomeTotal = before?.IncomeTotal,
            ExpectedCash = expectedForReport,
        };
        var provisional = fresh is null && serverShift;
        return new Result(shift, hasServerBefore ? preCloseReport : null, provisional, useLocal);
    }
}
