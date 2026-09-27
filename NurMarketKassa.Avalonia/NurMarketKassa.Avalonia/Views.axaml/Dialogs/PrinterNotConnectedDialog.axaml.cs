using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
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
            Title = Tr.T("Чековый аппарат не подключен", "Чек аппараты туташтырылган эмес", "Receipt printer not connected", "Fiş yazıcısı bağlı değil", "Chek apparati ulanmagan");
            TitleText.Text = Tr.T("Чековый аппарат не подключен", "Чек аппараты туташтырылган эмес", "Receipt printer not connected", "Fiş yazıcısı bağlı değil", "Chek apparati ulanmagan");
            MessageText.Text =
                Tr.T("Вы выбрали печать чека, однако чековый аппарат не подключён.\n" +
                "Подключите чековый аппарат либо отключите печать чека и продолжите без печати.", "Сиз чекти басып чыгарууну тандадыңыз, бирок чек аппараты туташтырылган эмес.\nЧек аппаратын туташтырыңыз же чек басып чыгарууну өчүрүп, басып чыгарбай эле улантыңыз.", "You chose to print the receipt, but the receipt printer is not connected.\nConnect the receipt printer, or turn off receipt printing and continue without printing.", "Fiş yazdırmayı seçtiniz, ancak fiş yazıcısı bağlı değil.\nFiş yazıcısını bağlayın ya da fiş yazdırmayı kapatıp yazdırmadan devam edin.", "Siz chekni chop etishni tanladingiz, lekin chek apparati ulanmagan.\nChek apparatini ulang yoki chek chop etishni o'chirib, chop etmasdan davom eting.");
            CancelButton.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
            ProceedButton.Content = Tr.T("Отключить печать и продолжить", "Басып чыгарууну өчүрүп, улантуу", "Turn off printing and continue", "Yazdırmayı kapat ve devam et", "Chop etishni o'chirib, davom etish");
            return;
        }

        Title = Tr.T("Принтер не подключен", "Принтер туташтырылган эмес", "Printer not connected", "Yazıcı bağlı değil", "Printer ulanmagan");
        TitleText.Text = Tr.T("Принтер не подключён", "Принтер туташтырылган эмес", "Printer not connected", "Yazıcı bağlı değil", "Printer ulanmagan");
        MessageText.Text = Tr.T("Чековый аппарат не подключен.", "Чек аппараты туташтырылган эмес.", "The receipt printer is not connected.", "Fiş yazıcısı bağlı değil.", "Chek apparati ulanmagan.");
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
