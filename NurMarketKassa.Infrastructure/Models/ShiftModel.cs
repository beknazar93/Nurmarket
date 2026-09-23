namespace NurMarketKassa.Models;

public sealed class ShiftModel
{
    public string Id { get; init; } = "";

    public string ShiftNumber { get; init; } = "";

    public DateTime? OpenedAt { get; init; }

    public DateTime? ClosedAt { get; init; }

    public string Cashier { get; init; } = "—";

    public string Status { get; init; } = "Закрыта";

    public decimal Revenue { get; init; }

    public bool IsActive => string.Equals(Status, "Активна", StringComparison.OrdinalIgnoreCase);

    public static ShiftModel FromEntry(ShiftHistoryEntry entry) => new()
    {
        Id = entry.ShiftNumber,
        ShiftNumber = entry.ShiftNumber,
        OpenedAt = entry.OpenedAt,
        ClosedAt = entry.ClosedAt,
        Cashier = entry.Cashier,
        Status = entry.Status,
        Revenue = entry.Revenue,
    };
}
