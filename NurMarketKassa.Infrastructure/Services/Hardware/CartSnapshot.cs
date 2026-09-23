namespace NurMarketKassa.Services.Hardware;

/// <summary>Снимок корзины для печати чека.</summary>
public sealed class CartSnapshot
{
    public string CartJson { get; init; } = "{}";

    public string? OfflineNote { get; init; }

    public string? PaymentMethodKey { get; init; }

    public string? CashReceived { get; init; }

    /// <summary>Готовый текст чека с сервера (если уже получен).</summary>
    public string? ReceiptText { get; init; }
}
