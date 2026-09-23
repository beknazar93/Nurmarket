using System.Text.Json;

namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Контракт завершения продажи: онлайн-оплата, офлайн-сохранение, печать чека и новый чек.
/// </summary>
public interface IPosCheckoutService
{
    Task<PosCheckoutResult> CheckoutAsync(PosCheckoutRequest request, CancellationToken cancellationToken = default);

    Task<bool> ApplyOrderDiscountAsync(
        Dictionary<string, string> discountBody,
        CancellationToken cancellationToken = default);

    Task PrepareCartForCheckoutAsync(CancellationToken cancellationToken = default);

    Task<string?> RestartSaleSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>Параметры оплаты из диалога Checkout.</summary>
public sealed class PosCheckoutRequest
{
    public required string PaymentMethod { get; init; }
    public required string CashReceived { get; init; }
    public required bool PrintReceipt { get; init; }
    public Dictionary<string, string>? OrderDiscountBody { get; init; }
}

/// <summary>Результат оплаты для UI.</summary>
public sealed class PosCheckoutResult
{
    public bool IsSuccess { get; init; }
    public bool SavedOffline { get; init; }
    public string? ErrorMessage { get; init; }
    public string? InfoMessage { get; init; }
    public double TotalAmount { get; init; }
    public JsonElement? CheckoutResponse { get; init; }
    public string? CartJsonSnapshot { get; init; }
    public bool ReceiptPrintAttempted { get; init; }
    public bool ReceiptPrinted { get; init; }

    public static PosCheckoutResult Succeeded(
        double total,
        string? cartJson,
        JsonElement? response = null,
        string? info = null,
        bool receiptPrintAttempted = false,
        bool receiptPrinted = false) =>
        new()
        {
            IsSuccess = true,
            TotalAmount = total,
            CartJsonSnapshot = cartJson,
            CheckoutResponse = response,
            InfoMessage = info,
            ReceiptPrintAttempted = receiptPrintAttempted,
            ReceiptPrinted = receiptPrinted,
        };

    public static PosCheckoutResult OfflineSaved(
        double total,
        string cartJson,
        string? info = null,
        bool receiptPrintAttempted = false,
        bool receiptPrinted = false) =>
        new()
        {
            IsSuccess = true,
            SavedOffline = true,
            TotalAmount = total,
            CartJsonSnapshot = cartJson,
            InfoMessage = info,
            ReceiptPrintAttempted = receiptPrintAttempted,
            ReceiptPrinted = receiptPrinted,
        };

    public static PosCheckoutResult Failed(string error) =>
        new() { IsSuccess = false, ErrorMessage = error };
}
