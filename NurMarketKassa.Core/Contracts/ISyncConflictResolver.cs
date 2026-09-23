namespace NurMarketKassa.Core.Contracts;

public interface ISyncConflictResolver
{
    Task ResolveAndSyncStockAsync(
        string productId,
        double localDelta,
        SyncStrategy strategy,
        CancellationToken cancellationToken = default);
}

public enum SyncStrategy
{
    ServerWins,
    LocalWins,
    Accumulate,
}
