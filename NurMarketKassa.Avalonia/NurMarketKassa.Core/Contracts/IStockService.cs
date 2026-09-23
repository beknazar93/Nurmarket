namespace NurMarketKassa.Core.Contracts;

public interface IStockService
{
    void Initialize();

    Task CommitSaleAsync(
        string referenceId,
        string productId,
        double quantity,
        CancellationToken cancellationToken = default);
}
