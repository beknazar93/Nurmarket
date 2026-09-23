namespace NurMarketKassa.Core.Contracts;

public sealed class OfflineSessionBootstrap
{
    public string UserId { get; init; } = "";

    public string? DisplayName { get; init; }

    public string? OfflineMessage { get; init; }
}

/// <summary>Restores a previously persisted offline session after local credential validation.</summary>
public interface IOfflineLoginSupport
{
    OfflineSessionBootstrap? TryBootstrapOfflineSession(string email);
}
