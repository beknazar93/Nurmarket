using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class CloseShiftDialog : Window
{
    public decimal? ClosingCash =>
        decimal.TryParse(ClosingCashBox.Text, out var value) ? value : null;

    public decimal? SuggestedBalance { get; set; }

    public CloseShiftDialog()
    {
        InitializeComponent();
        Opened += OnDialogOpened;
    }

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (SuggestedBalance.HasValue)
        {
            SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(SuggestedBalance);
            ClosingCashBox.Text = SuggestedBalance.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        ClosingCashBox.Focus();
        ClosingCashBox.SelectAll();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ClosingCashBox.Text) &&
            !decimal.TryParse(ClosingCashBox.Text, out _))
        {
            PosMessageBox.Show(this, "Введите корректную сумму.", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
