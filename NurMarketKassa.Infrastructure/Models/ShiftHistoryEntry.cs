using System;

namespace NurMarketKassa.Models;

public sealed class ShiftHistoryEntry
{
    public string ShiftNumber { get; init; } = "";
    public DateTime? OpenedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public string Cashier { get; init; } = "—";
    public string Status { get; init; } = "Закрыта";
    public decimal Revenue { get; init; }
}
