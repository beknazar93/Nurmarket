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
                await ApplyServerWinsAsync(productId, serverStock.Value, catalogStock, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case SyncStrategy.LocalWins:
                await ApplyLocalWinsAsync(productId, localComputed, cancellationToken).ConfigureAwait(false);
                break;

            case SyncStrategy.Accumulate:
                await ApplyAccumulateAsync(productId, serverStock.Value, localDelta, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyServerWinsAsync(
        string productId,
        double serverStock,
        double catalogStock,
        CancellationToken cancellationToken)
    {
        var correction = serverStock - catalogStock;
        if (Math.Abs(correction) > 1e-9)
        {
            await _ledger.RecordSyncCorrectionAsync(
                productId,
                correction,
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
        CancellationToken cancellationToken)
    {
        if (Math.Abs(localDelta) < 1e-9)
            return;

        var target = Math.Max(0, serverStock + localDelta);
        var applied = await _serverStock.TrySetServerStockAsync(productId, target, cancellationToken)
            .ConfigureAwait(false);

        if (applied)
        {
            await _ledger.RecordSyncCorrectionAsync(
                productId,
                localDelta,
                $"sync-accumulate-{Guid.NewGuid():N}",
                cancellationToken).ConfigureAwait(false);

            _catalogUpdater.UpdateCatalogStock(productId, target);
        }
    }
}
