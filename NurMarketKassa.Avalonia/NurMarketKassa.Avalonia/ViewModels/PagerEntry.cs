namespace NurMarketKassa.AvaloniaHost.ViewModels;

public enum ShiftPage
{
    History,
    Summary,
}

public enum PagerEntryKind
{
    Page,
    Ellipsis,
}

public sealed class PagerEntry
{
    public PagerEntryKind Kind { get; init; }
    public int? PageNumber { get; init; }
    public bool IsCurrent { get; init; }
    public string Display => Kind == PagerEntryKind.Ellipsis ? "…" : PageNumber?.ToString() ?? "";
}
