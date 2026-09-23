using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Core.Contracts;

public interface IPosCartGateway
{
    bool CanRefresh { get; }

    string? CartId { get; }

    Task<bool> EnsureSaleSessionAsync(CancellationToken cancellationToken = default);

    Task<bool> ScanBarcodeAsync(string barcode, string? quantity, CancellationToken cancellationToken = default);

    Task<bool> AddProductAsync(string productId, string quantity, CancellationToken cancellationToken = default);

    IReadOnlyList<CartLineDto> GetCurrentLines();
}
