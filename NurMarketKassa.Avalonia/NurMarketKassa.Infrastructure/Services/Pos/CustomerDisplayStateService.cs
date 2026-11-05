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
    private string? _selectedBankQrPath;
    private double? _qrAmount;

    public event Action? StateChanged;

    public CustomerDisplayCartSnapshot CurrentSnapshot => _snapshot;
    public CustomerDisplayPaymentStatus CurrentStatus => _status;
    public string? StatusMessage => _statusMessage;
    public string? SelectedBankQrPath => _selectedBankQrPath;
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

    public void SetSelectedBankQrPath(string? qrPath)
    {
        _selectedBankQrPath = qrPath;
        if (qrPath is null)
            _qrAmount = null; // диалог оплаты закрыт — сумма QR сбрасывается вместе с выбором банка
        StateChanged?.Invoke();
    }

    /// <summary>2026-11-05 (ревью п. 17): сумма динамического QR (безналичная часть смешанной оплаты); null — по итогу чека.</summary>
    public double? QrAmount => _qrAmount;

    public void SetQrAmount(double? amount)
    {
        var normalized = amount is > 0.004 ? amount : null;
        if (_qrAmount == normalized)
            return;
        _qrAmount = normalized;
        StateChanged?.Invoke();
    }
}
