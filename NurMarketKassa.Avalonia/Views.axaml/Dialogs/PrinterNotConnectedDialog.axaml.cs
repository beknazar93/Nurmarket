using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PrinterNotConnectedDialog : Window
{
    public PrinterNotConnectedResult Result { get; private set; } = PrinterNotConnectedResult.Cancel;

    public PrinterNotConnectedDialog() : this(checkoutMode: false) { }

    private PrinterNotConnectedDialog(bool checkoutMode)
    {
        InitializeComponent();

        if (checkoutMode)
        {
            Title = "Чековый аппарат не подключен";
            TitleText.Text = "Чековый аппарат не подключен";
            MessageText.Text =
                "Вы выбрали печать чека, однако чековый аппарат не подключён.\n" +
                "Подключите чековый аппарат либо отключите печать чека и продолжите без печати.";
            CancelButton.Content = "Отмена";
            ProceedButton.Content = "Отключить печать и продолжить";
            return;
        }

        Title = "Принтер не подключен";
        TitleText.Text = "Принтер не подключён";
        MessageText.Text = "Чековый аппарат не подключен.";
        TwoButtonRow.IsVisible = false;
        OkButton.IsVisible = true;
    }

    public static PrinterNotConnectedResult ShowCheckout(Window? owner)
    {
        var dlg = new PrinterNotConnectedDialog(checkoutMode: true);
        PosDialogHost.Show(dlg, owner);
        return dlg.Result;
    }

    public static void ShowOk(Window? owner, string? message = null)
    {
        var dlg = new PrinterNotConnectedDialog(checkoutMode: false);
        if (!string.IsNullOrWhiteSpace(message))
            dlg.MessageText.Text = message;
        PosDialogHost.Show(dlg, owner);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = PrinterNotConnectedResult.Cancel;
        Close(false);
    }

    private void ProceedButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = PrinterNotConnectedResult.ContinueWithoutPrint;
        Close(true);
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = PrinterNotConnectedResult.ContinueWithoutPrint;
        Close(true);
    }
}
