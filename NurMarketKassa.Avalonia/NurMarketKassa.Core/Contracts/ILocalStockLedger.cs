namespace NurMarketKassa.Core.Contracts;

public interface ILocalStockLedger
{
    Task<double> GetNetDeltaAsync(string productId, CancellationToken cancellationToken = default);

    Task RecordSyncCorrectionAsync(
        string productId,
        double correctionDelta,
        string referenceId,
        CancellationToken cancellationToken = default);
}
