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
}

/// <summary>Результат операции открытия или закрытия смены (успех, баланс, офлайн-режим, сообщения об ошибках).</summary>
public sealed record CashShiftOperationResult(
    bool IsSuccess,
    decimal? Balance,
    bool IsOffline,
    string? ErrorMessage,
    string? InfoMessage)
{
    public static CashShiftOperationResult Success(decimal? balance, bool isOffline = false, string? infoMessage = null) =>
        new(true, balance, isOffline, null, infoMessage);

    public static CashShiftOperationResult Failed(string error) =>
        new(false, null, false, error, null);
}

/// <summary>Текст X/Z-отчёта смены и признак успешной печати на принтере.</summary>
public sealed record CashShiftReportResult(string ReportText, bool Printed);
