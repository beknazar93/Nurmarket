namespace NurMarketKassa.Core.Contracts;

public interface ILocalStockProvider
{
    double GetExpectedQuantity(string productId);
}
