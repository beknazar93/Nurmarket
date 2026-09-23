namespace NurMarketKassa.Services.Hardware;

/// <summary>Печать чека на физический ESC/POS принтер (LPT).</summary>
public sealed class LptReceiptPrinterService : IReceiptPrinterService
{
    public Task<bool> PrintReceiptAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var prefs = UserPreferences.Instance;
        if (!prefs.ReceiptEnabled)
        {
            PosLogger.Log("Печать пропущена: выключена в настройках кассы (ReceiptEnabled=false).", "PRINTER");
            return Task.FromResult(false);
        }

        if (HardwareModeHelper.IsNonePort(prefs.ReceiptDevicePath))
        {
            PosLogger.Log("Печать пропущена: порт принтера не указан.", "PRINTER");
            return Task.FromResult(false);
        }

        try
        {
            ReceiptPrintService.PrintReceipt(
                cart.CartJson,
                cart.OfflineNote,
                cart.PaymentMethodKey,
                cart.CashReceived,
                cart.ReceiptText);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Печать LPT: {ex.Message}\n{ex.StackTrace}", "PRINTER");
            return Task.FromResult(false);
        }
    }
}
