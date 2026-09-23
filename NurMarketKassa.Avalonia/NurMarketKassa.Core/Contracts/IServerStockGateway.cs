namespace NurMarketKassa.Core.Contracts;

public interface IServerStockGateway
{
    Task<double?> GetServerStockAsync(string productId, CancellationToken cancellationToken = default);

    Task<bool> TrySetServerStockAsync(string productId, double quantity, CancellationToken cancellationToken = default);
}
