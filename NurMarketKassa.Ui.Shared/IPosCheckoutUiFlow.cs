namespace NurMarketKassa.Ui.Shared;

/// <summary>POS-specific checkout UI hooks (stock checks, post-payment dialogs).</summary>
public interface IPosCheckoutUiFlow
{
    /// <summary>Returns false to abort checkout before opening payment dialog.</summary>
    Task<bool> PrepareCheckoutAsync();

    Task ShowPaymentProcessingAsync(double totalAmount);

    Task ShowPaymentResultAsync(bool isSuccess, string message);

    Task ShowPaymentSuccessAsync(
        double totalAmount,
        bool defaultPrintReceipt,
        string? cartJson,
        string? paymentMethod,
        string? cashReceived);
}
