namespace NurMarketKassa.Services;

/// <summary>
/// Постоянный (не удаляется при синхронизации) журнал чеков, прошедших нестандартно —
/// «Некорректные чеки» (этап 2 бэклога «Доработки и добавление функционала»).
/// </summary>
public sealed class IrregularReceiptEntry
{
    public const string InsufficientStock = "insufficient_stock";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>insufficient_stock | ... (расширяется на этапе 3 логами системных сбоев).</summary>
    public string Tag { get; set; } = InsufficientStock;

    public string? CashierName { get; set; }

    public string? Note { get; set; }

    public double Total { get; set; }

    public string CartJson { get; set; } = "{}";
}
