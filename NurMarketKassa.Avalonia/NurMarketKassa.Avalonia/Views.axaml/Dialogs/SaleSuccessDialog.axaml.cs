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
        AmountText.Text = Tr.T($"{totalAmount:0.00} сом", $"{totalAmount:0.00} сом", $"{totalAmount:0.00} som", $"{totalAmount:0.00} som", $"{totalAmount:0.00} so'm");
        PrintButton.Content = receiptPrintRequested
            ? Tr.T("Напечатать чек ещё раз", "Чекти кайра басып чыгаруу", "Print receipt again", "Fişi tekrar yazdır", "Chekni qayta chop etish")
            : Tr.T("Напечатать чек", "Чекти басып чыгаруу", "Print receipt", "Fişi yazdır", "Chekni chop etish");
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
