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

        return PrintWithTimeoutAsync(cart);
    }

    // 2026-09-07: реальный баг — ReceiptPrintService.PrintReceipt блокирующий (сырые вызовы
    // WinUSB/COM/LPT без единого таймаута), и раньше вызывался прямо тут. Если принтер завис на
    // уровне драйвера (например, оборвался кабель во время записи), продажа на сервере уже
    // проходит, но касса зависала на "Проводим оплату..." навсегда — единственным выходом было
    // убить процесс. Печать теперь выполняется на отдельном потоке с жёстким лимитом времени:
    // после тайм-аута продажа считается завершённой (просто без чека), а не зависшей.
    private static async Task<bool> PrintWithTimeoutAsync(CartSnapshot cart)
    {
        var printTask = Task.Run(() =>
        {
            ReceiptPrintService.PrintReceipt(
                cart.CartJson,
                cart.OfflineNote,
                cart.PaymentMethodKey,
                cart.CashReceived,
                cart.ReceiptText);
            return true;
        });

        var finished = await Task.WhenAny(printTask, Task.Delay(TimeSpan.FromSeconds(8))).ConfigureAwait(false);
        if (finished != printTask)
        {
            PosLogger.Log("Печать LPT: тайм-аут — принтер не отвечает 8 секунд, продажа уже проведена, чек не распечатан.", "PRINTER");
            return false;
        }

        try
        {
            return await printTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Печать LPT: {ex.Message}\n{ex.StackTrace}", "PRINTER");
            return false;
        }
    }
}
