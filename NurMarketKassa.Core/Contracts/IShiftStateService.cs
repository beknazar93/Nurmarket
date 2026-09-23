namespace NurMarketKassa.Core.Contracts;

public interface IShiftStateService
{
    event Action<ShiftStateSnapshot>? StateRefreshed;

    string? ActiveShiftId { get; }

    bool IsShiftOpen { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class ShiftStateSnapshot
{
    public string? ActiveShiftId { get; init; }
    public decimal? CashBalance { get; init; }
}
