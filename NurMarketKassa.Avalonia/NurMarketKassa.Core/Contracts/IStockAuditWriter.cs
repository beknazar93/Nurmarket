namespace NurMarketKassa.Core.Contracts;

public interface IStockAuditWriter
{
    void LogStock(string productId, double quantity, string reason);
}
