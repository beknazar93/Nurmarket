using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>
/// Базовая реализация экрана покупателя: хранит последний снимок корзины и статус оплаты.
/// UI-хост подписывается через <see cref="StateChanged"/>.
/// </summary>
public sealed class CustomerDisplayStateService : ICustomerDisplayService
{
    private CustomerDisplayCartSnapshot _snapshot = new();
    private CustomerDisplayPaymentStatus _status = CustomerDisplayPaymentStatus.Idle;
    private string? _statusMessage;

    public event Action? StateChanged;

    public CustomerDisplayCartSnapshot CurrentSnapshot => _snapshot;
    public CustomerDisplayPaymentStatus CurrentStatus => _status;
    public string? StatusMessage => _statusMessage;
    public bool IsVisible { get; private set; }

    public void Show()
    {
        IsVisible = true;
        StateChanged?.Invoke();
    }

    public void Hide()
    {
        IsVisible = false;
        StateChanged?.Invoke();
    }

    public void UpdateCart(CustomerDisplayCartSnapshot snapshot)
    {
        _snapshot = snapshot ?? new CustomerDisplayCartSnapshot();
        StateChanged?.Invoke();
    }

    public void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null)
    {
        _status = status;
        _statusMessage = message;
        StateChanged?.Invoke();
    }
}
