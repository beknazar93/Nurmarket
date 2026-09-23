using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>In-memory session store for the Avalonia host (replaces static <c>App.*</c> bridge).</summary>
public sealed class AvaloniaAppSession : IAppSession
{
    public string? CurrentUserId { get; set; }

    public string? ActiveShiftId { get; set; }

    public string? ActiveTerminal { get; set; }

    public string? PosCashboxDisplayName { get; set; }

    public bool IsShiftOpen => !string.IsNullOrEmpty(ActiveShiftId);

    public bool IsOfflineBootstrap { get; set; }

    public string? OfflineBootstrapMessage { get; set; }
}
