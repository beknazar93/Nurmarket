namespace NurMarketKassa.Ui.Shared;

/// <summary>
/// Cross-platform POS session state (replaces static <c>App.CurrentUserId</c> / <c>App.ActiveShiftId</c> in portable code).
/// </summary>
public interface IAppSession
{
    string? CurrentUserId { get; set; }

    string? ActiveShiftId { get; set; }

    /// <summary>POS terminal / cashbox identifier (<c>App.PosCashboxId</c>).</summary>
    string? ActiveTerminal { get; set; }

    string? PosCashboxDisplayName { get; set; }

    bool IsShiftOpen { get; }

    bool IsOfflineBootstrap { get; set; }

    string? OfflineBootstrapMessage { get; set; }
}
