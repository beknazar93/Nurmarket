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
        // 2026-10-22 (модалки, пакет 1): сумма с разрядами («1 620,00 сом»), было «1620,00».
        AmountText.Text = NurMarketKassa.Services.MoneyText.Som(totalAmount);
        PrintButton.Content = receiptPrintRequested
            ? Tr.T("Напечатать чек ещё раз", "Чекти кайра басып чыгаруу", "Print receipt again", "Fişi tekrar yazdır", "Chekni qayta chop etish")
            : Tr.T("Напечатать чек", "Чекти басып чыгаруу", "Print receipt", "Fişi yazdır", "Chekni chop etish");
        // 2026-10-22 (модалки, пакет 1): Enter — «Напечатать чек», но не Enter сканера (кассир уже сканирует следующий товар).
        ScannerEnterGuard.Attach(this);
        Shell.InitialFocus = PrintButton;
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
