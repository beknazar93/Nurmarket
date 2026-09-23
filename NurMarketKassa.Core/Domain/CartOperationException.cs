namespace NurMarketKassa.Core.Domain;

public sealed class CartOperationException : Exception
{
    public CartOperationException(string userMessage) : base(userMessage)
    {
    }
}
