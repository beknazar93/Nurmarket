namespace NurMarketKassa.Core.Contracts;

public interface IStockCatalogUpdater
{
    void UpdateCatalogStock(string productId, double quantity);
}
