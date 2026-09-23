using System;

#nullable enable
namespace NurMarketKassa.Services;

public class AnalyticsRecord
{
    public string ActionType { get; set; } = "";

    public DateTime Timestamp { get; set; }

    public string CashierName { get; set; } = "";

    public Decimal Balance { get; set; }

    public string Device { get; set; } = "";
}
