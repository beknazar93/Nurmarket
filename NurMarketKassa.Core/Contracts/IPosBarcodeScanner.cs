namespace NurMarketKassa.Core.Contracts;

public interface IPosBarcodeScanner
{
    Task<bool> ScanAsync(string barcode, CancellationToken cancellationToken = default);
}
