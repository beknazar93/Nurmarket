using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Core.Application;

public sealed class SyncConflictResolver : ISyncConflictResolver
{
    private readonly IServerStockGateway _serverStock;
    private readonly ILocalStockProvider _localStock;
    private readonly ILocalStockLedger _ledger;
    private readonly IStockCatalogUpdater _catalogUpdater;

    public SyncConflictResolver(
        IServerStockGateway serverStock,
        ILocalStockProvider localStock,
        ILocalStockLedger ledger,
        IStockCatalogUpdater catalogUpdater)
    {
        _serverStock = serverStock;
        _localStock = localStock;
        _ledger = ledger;
        _catalogUpdater = catalogUpdater;
    }

    public async Task ResolveAndSyncStockAsync(
        string productId,
        double localDelta,
        SyncStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;

        var serverStock = await _serverStock.GetServerStockAsync(productId, cancellationToken).ConfigureAwait(false);
        if (serverStock is null)
            return;

        var catalogStock = _localStock.GetExpectedQuantity(productId);
        var ledgerDelta = await _ledger.GetNetDeltaAsync(productId, cancellationToken).ConfigureAwait(false);
        var localComputed = Math.Max(0, catalogStock + ledgerDelta);

        switch (strategy)
        {
            case SyncStrategy.ServerWins:
                await ApplyServerWinsAsync(productId, serverStock.Value, ledgerDelta, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case SyncStrategy.LocalWins:
                await ApplyLocalWinsAsync(productId, localComputed, cancellationToken).ConfigureAwait(false);
                break;

            case SyncStrategy.Accumulate:
                await ApplyAccumulateAsync(productId, serverStock.Value, localDelta, ledgerDelta, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyServerWinsAsync(
        string productId,
        double serverStock,
        double ledgerDelta,
        CancellationToken cancellationToken)
    {
        // The server value becomes the new baseline, so any previously accumulated
        // ledger delta is now already reflected in it and must be zeroed out —
        // otherwise it keeps being added on top of every future baseline forever.
        if (Math.Abs(ledgerDelta) > 1e-9)
        {
            await _ledger.RecordSyncCorrectionAsync(
                productId,
                -ledgerDelta,
                $"sync-server-wins-{Guid.NewGuid():N}",
                cancellationToken).ConfigureAwait(false);
        }

        _catalogUpdater.UpdateCatalogStock(productId, serverStock);
    }

    private async Task ApplyLocalWinsAsync(
        string productId,
        double localStock,
        CancellationToken cancellationToken)
    {
        await _serverStock.TrySetServerStockAsync(productId, localStock, cancellationToken).ConfigureAwait(false);

        var serverStock = await _serverStock.GetServerStockAsync(productId, cancellationToken).ConfigureAwait(false);
        if (serverStock is not null)
            _catalogUpdater.UpdateCatalogStock(productId, serverStock.Value);
        else
            _catalogUpdater.UpdateCatalogStock(productId, localStock);
    }

    private async Task ApplyAccumulateAsync(
        string productId,
        double serverStock,
        double localDelta,
        double ledgerDelta,
        CancellationToken cancellationToken)
    {
        if (Math.Abs(localDelta) < 1e-9)
            return;

        var target = Math.Max(0, serverStock + localDelta);
        var applied = await _serverStock.TrySetServerStockAsync(productId, target, cancellationToken)
            .ConfigureAwait(false);

        if (applied)
        {
            // `target` already bakes in localDelta, so recording +localDelta again
            // would double-count it; instead zero out whatever was previously
            // pending in the ledger now that the catalog has this fresh baseline.
            if (Math.Abs(ledgerDelta) > 1e-9)
            {
                await _ledger.RecordSyncCorrectionAsync(
                    productId,
                    -ledgerDelta,
                    $"sync-accumulate-{Guid.NewGuid():N}",
                    cancellationToken).ConfigureAwait(false);
            }

            _catalogUpdater.UpdateCatalogStock(productId, target);
        }
    }
}
