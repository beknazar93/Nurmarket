using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
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
        // Блок «Консультант» (сфера «Одежда»): сотрудники и их процент — с сервера, с кэшем.
        _viewModel.ConfigureConsultants(ConsultantDirectory.ListAsync, ConsultantDirectory.DefaultPercentAsync);
        InitializeComponent();
        DataContext = _viewModel;
        AttachCloseHandler();
        this.ClampToScreenHeight();

        // Сумма наличными сразу выделена — кассир может просто начать печатать цифры,
        // не стирая предложенную сумму вручную, либо сразу нажать Enter для оплаты без изменений.
        Opened += (_, _) =>
        {
            if (_viewModel.IsCashMode)
            {
                CashReceivedBox.Focus();
                CashReceivedBox.SelectAll();
            }
        };
    }

    private void CashReceivedBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (_viewModel.PayCommand.CanExecute(null))
            _viewModel.PayCommand.Execute(null);
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

            // Clicking "Оплатить" here is already the cashier's confirmation — the extra
            // "Подтвердить оплату?" Да/Нет dialog this used to show was a redundant second
            // click on top of it.
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

    private async void ChooseClient_Click(object? sender, RoutedEventArgs e) =>
        await ClientPickerDialog.Open(this, _viewModel).ConfigureAwait(true);
}
