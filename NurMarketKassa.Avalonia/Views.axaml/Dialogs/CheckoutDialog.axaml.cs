using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// Диалог оплаты Avalonia-кассы: выбор способа оплаты, скидка и подтверждение суммы.
/// </summary>
public partial class CheckoutDialog : Window
{
    private readonly CheckoutViewModel _viewModel;
    private readonly IDialogService _dialogService;

    public CheckoutDialog() : this(
        new CheckoutViewModel(new CartTotalsCalculator.CartTotals(), "", ""),
        ResolveService<IDialogService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public CheckoutDialog(CheckoutViewModel viewModel, IDialogService dialogService)
    {
        _viewModel = viewModel;
        _dialogService = dialogService;
        InitializeComponent();
        DataContext = _viewModel;
        AttachCloseHandler();
    }

    private void AttachCloseHandler()
    {
        _viewModel.RequestClose += async result =>
        {
            if (!result)
            {
                Close(false);
                return;
            }

            if (_viewModel.IsPrintReceiptEnabled && !HardwareModeHelper.IsPrinterPortConfigured())
            {
                var decision = await _dialogService.ShowPrinterNotConnectedAsync().ConfigureAwait(true);
                if (decision == PrinterNotConnectedResult.Cancel)
                    return;

                _viewModel.IsPrintReceiptEnabled = false;
            }

            if (!await _dialogService.ConfirmPaymentAsync().ConfigureAwait(true))
                return;

            Close(true);
        };
    }

    public string PaymentMethodKey => _viewModel.PaymentMethod;

    public bool IsPrintReceiptEnabled => _viewModel.IsPrintReceiptEnabled;

    public string CashReceivedForApi
    {
        get
        {
            if (_viewModel.PaymentMethod == "cash")
            {
                var normalized = CheckoutValidation.NormalizeDecimal(_viewModel.CashReceived);
                var total = _viewModel.EffectiveTotalDue;
                return string.IsNullOrEmpty(normalized)
                    ? total.ToString("0.00", CultureInfo.InvariantCulture)
                    : normalized;
            }

            return "0.00";
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
