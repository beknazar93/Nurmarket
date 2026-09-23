namespace NurMarketKassa.Core.Contracts;

/// <summary>Network reachability probe for the POS host.</summary>
public interface IConnectivityService
{
    Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default);
}
