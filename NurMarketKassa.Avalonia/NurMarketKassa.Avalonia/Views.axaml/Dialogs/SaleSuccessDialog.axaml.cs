using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public enum SaleSuccessDialogAction
{
    Close,
    Print,
    Preview,
}

public partial class SaleSuccessDialog : Window
{
    public SaleSuccessDialogAction Action { get; private set; } = SaleSuccessDialogAction.Close;

    public SaleSuccessDialog() : this(0, false) { }

    public SaleSuccessDialog(double totalAmount, bool receiptPrintRequested = false)
    {
        InitializeComponent();
        AmountText.Text = $"{totalAmount:0.00} сом";
        PrintButton.Content = receiptPrintRequested
            ? "Напечатать чек ещё раз"
            : "Напечатать чек";
    }

    private void PrintButton_Click(object? sender, RoutedEventArgs e)
    {
        Action = SaleSuccessDialogAction.Print;
        Close(true);
    }

    private void PreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        Action = SaleSuccessDialogAction.Preview;
        Close(true);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Action = SaleSuccessDialogAction.Close;
        Close(true);
    }
}
