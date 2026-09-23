namespace NurMarketKassa.Core.Contracts;

/// <summary>
/// Контракт экрана покупателя: отображение позиций чека, итога и статуса оплаты на втором мониторе.
/// </summary>
public interface ICustomerDisplayService
{
    void Show();

    void Hide();

    void UpdateCart(CustomerDisplayCartSnapshot snapshot);

    void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null);

}

/// <summary>Снимок корзины для экрана покупателя.</summary>
public sealed class CustomerDisplayCartSnapshot
{
    public IReadOnlyList<CustomerDisplayLine> Lines { get; init; } = Array.Empty<CustomerDisplayLine>();
    public double Subtotal { get; init; }
    public double Discount { get; init; }
    public double Total { get; init; }
}

/// <summary>Строка чека на экране покупателя.</summary>
public sealed class CustomerDisplayLine
{
    public required string Title { get; init; }
    public string? Barcode { get; init; }
    public string? ImageUrl { get; init; }
    public double Quantity { get; init; }
    public string Unit { get; init; } = "шт";
    public double LineTotal { get; init; }
}

/// <summary>Статус оплаты на экране покупателя.</summary>
public enum CustomerDisplayPaymentStatus
{
    Idle,
    Processing,
    Success,
    Failed,
}
