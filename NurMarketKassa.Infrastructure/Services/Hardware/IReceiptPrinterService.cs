namespace NurMarketKassa.Services.Hardware;

public interface IReceiptPrinterService
{
    Task<bool> PrintReceiptAsync(CartSnapshot cart, CancellationToken cancellationToken = default);
}
