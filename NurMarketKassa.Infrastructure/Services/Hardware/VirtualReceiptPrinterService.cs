using System.Diagnostics;
using System.Text.Json;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Виртуальный принтер: логирует чек вместо отправки на LPT.</summary>
public sealed class VirtualReceiptPrinterService : IReceiptPrinterService
{
    public Task<bool> PrintReceiptAsync(CartSnapshot cart, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = cart.ReceiptText;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = CartReceiptTextBuilder.BuildSimpleReceipt(
                cart.CartJson,
                cart.OfflineNote,
                cart.PaymentMethodKey,
                cart.CashReceived);
        }

        var preview = new
        {
            mode = "virtual",
            cart.PaymentMethodKey,
            cart.CashReceived,
            cart.OfflineNote,
            lines = text.Split('\n').Take(40),
        };

        var json = JsonSerializer.Serialize(preview, new JsonSerializerOptions { WriteIndented = true });
        PosLogger.Log($"Виртуальная печать чека завершена ({text.Length} симв.).", "PRINTER");

        return Task.FromResult(true);
    }
}
