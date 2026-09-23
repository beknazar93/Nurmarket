namespace NurMarketKassa.Core.Contracts;

/// <summary>Result of a cashier login attempt.</summary>
public sealed class AuthResult
{
    public bool IsSuccess { get; init; }

    public string? UserId { get; init; }

    public string? ActiveShiftId { get; init; }

    public string? ActiveTerminal { get; init; }

    public string? PosCashboxDisplayName { get; init; }

    public bool IsOfflineBootstrap { get; init; }

    public string? OfflineBootstrapMessage { get; init; }

    public string? ErrorMessage { get; init; }

    public static AuthResult Success(
        string userId,
        string? activeShiftId = null,
        string? activeTerminal = null,
        string? posCashboxDisplayName = null,
        bool isOfflineBootstrap = false,
        string? offlineBootstrapMessage = null) =>
        new()
        {
            IsSuccess = true,
            UserId = userId,
            ActiveShiftId = activeShiftId,
            ActiveTerminal = activeTerminal,
            PosCashboxDisplayName = posCashboxDisplayName,
            IsOfflineBootstrap = isOfflineBootstrap,
            OfflineBootstrapMessage = offlineBootstrapMessage,
        };

    public static AuthResult Failure(string message) =>
        new() { IsSuccess = false, ErrorMessage = message };
}
