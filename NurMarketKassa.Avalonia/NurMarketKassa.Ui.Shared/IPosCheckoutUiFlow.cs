namespace NurMarketKassa.Ui.Shared;

/// <summary>POS-specific checkout UI hooks (stock checks, post-payment dialogs).</summary>
public interface IPosCheckoutUiFlow
{
    /// <summary>Returns false to abort checkout before opening payment dialog.</summary>
    Task<bool> PrepareCheckoutAsync();

    Task ShowPaymentProcessingAsync(double totalAmount);

    Task ShowPaymentResultAsync(bool isSuccess, string message);

    /// <summary>2026-10-30 (приёмка 1.17.66, «Важно» 1): закрыть окно «Проводим оплату…» без результата — перед вопросом «Сервер занят: повторить / отложить в очередь».</summary>
    Task ClosePaymentProcessingAsync() => Task.CompletedTask;

    Task ShowPaymentSuccessAsync(
        double totalAmount,
        bool defaultPrintReceipt,
        string? cartJson,
        string? paymentMethod,
        string? cashReceived);

    /// <summary>Если есть отложенные чеки, открывает самый старый из них вместо пустого нового.</summary>
    Task OpenNextDeferredCartIfAnyAsync();
}
