using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class OpenShiftDialog : Window
{
    public decimal OpeningCash =>
        decimal.TryParse(OpeningCashBox.Text, out var value) ? value : 0;

    public decimal? SuggestedBalance { get; set; }

    public OpenShiftDialog()
    {
        InitializeComponent();
        Opened += OnDialogOpened;
    }

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (SuggestedBalance.HasValue)
        {
            SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(SuggestedBalance);
            OpeningCashBox.Text = SuggestedBalance.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        OpeningCashBox.Focus();
        OpeningCashBox.SelectAll();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(OpeningCashBox.Text, out _))
        {
            PosMessageBox.Show(this, "Введите корректную сумму.", "Ошибка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
