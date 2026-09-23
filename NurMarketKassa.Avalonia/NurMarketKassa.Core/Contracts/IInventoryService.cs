namespace NurMarketKassa.Core.Contracts;

public interface IInventoryService
{
    Task<bool> CommitRevisionAsync(IReadOnlyList<InventoryLineDto> lines, string userId, CancellationToken cancellationToken = default);

    Task<bool> WriteOffProductAsync(
        string productId,
        double quantity,
        string reason,
        string authorizedBy,
        CancellationToken cancellationToken = default);
}

public sealed record InventoryLineDto(string ProductId, double ExpectedQty, double ActualQty);
