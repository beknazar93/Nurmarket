namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Этот файл описывает контракт управления кассовой сменой:
/// открытие, закрытие, печать отчётов и синхронизацию с REST API сайта.
/// </summary>
public interface ICashShiftService
{
    Task<CashShiftOperationResult> OpenShiftAsync(decimal openingCash, CancellationToken cancellationToken = default);
    Task<CashShiftOperationResult> CloseShiftAsync(decimal? closingCash, CancellationToken cancellationToken = default);
    Task<CashShiftReportResult> GenerateXReportAsync(decimal? currentBalance, CancellationToken cancellationToken = default);
    Task<CashShiftReportResult> GenerateZReportAsync(decimal? closingCash, CancellationToken cancellationToken = default);
    Task<bool> PrintReportAsync(string reportText, CancellationToken cancellationToken = default);

    /// <summary>2026-09-15, живой баг ("Долг для уже закрытых смен в Истории смен показывает
    /// не то/«—»") — тот же самый надёжный расчёт (реальный remaining_debt по каждой долговой
    /// продаже ИМЕННО этой смены, а не замороженная сумма продажи и не непроверенное поле
    /// сервера на смене), что уже применяется сразу после закрытия смены (см. CloseShiftAsync),
    /// но теперь доступен и для смены, выбранной из истории. salesCountHint — необязательная
    /// подсказка для размера запроса списка продаж, не влияет на корректность.</summary>
    Task<decimal?> ResolveShiftDebtTotalAsync(
        string shiftId, int? salesCountHint = null, CancellationToken cancellationToken = default);
}

/// <summary>Результат операции открытия или закрытия смены (успех, баланс, офлайн-режим, сообщения об ошибках).</summary>
public sealed record CashShiftOperationResult(
    bool IsSuccess,
    decimal? Balance,
    bool IsOffline,
    string? ErrorMessage,
    string? InfoMessage,
    CashShiftClosingTotals? Totals = null)
{
    public static CashShiftOperationResult Success(
        decimal? balance, bool isOffline = false, string? infoMessage = null, CashShiftClosingTotals? totals = null) =>
        new(true, balance, isOffline, null, infoMessage, totals);

    public static CashShiftOperationResult Failed(string error) =>
        new(false, null, false, error, null);
}

/// <summary>2026-09-15, по просьбе пользователя ("не считает наличную и безналичную") —
/// разбивка выручки закрытой смены ИЗ ОТВЕТА СЕРВЕРА НА САМО ЗАКРЫТИЕ (не из снимка,
/// подтянутого в фоне РАНЬШЕ, пока диалог закрытия был ещё открыт) — единственный источник,
/// гарантированно учитывающий самую последнюю продажу смены. Core не видит Infrastructure-тип
/// ShiftBalanceHelper.ShiftTotals, поэтому здесь — плоский набор полей с теми же значениями.</summary>
public sealed record CashShiftClosingTotals(
    decimal? OpeningCash,
    decimal? TotalSales,
    decimal? CashSales,
    decimal? NonCashSales,
    decimal? DebtSales,
    int? SalesCount);

/// <summary>Текст X/Z-отчёта смены и признак успешной печати на принтере.</summary>
public sealed record CashShiftReportResult(string ReportText, bool Printed);
