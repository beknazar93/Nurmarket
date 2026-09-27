using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Ввод серийного номера покупки платной темы (см. MarketplaceView.BuyThemeButton_Click).
/// Сам диалог ничего не проверяет — просто собирает текст; валидация (в т.ч. мастер-ключ для
/// теста) — на стороне вызывающего кода, чтобы решать, разблокировать одну тему или все сразу.</summary>
public partial class SerialActivationDialog : Window
{
    public string EnteredSerial { get; private set; } = "";

    public SerialActivationDialog()
    {
        InitializeComponent();
    }

    public SerialActivationDialog(string themeName)
    {
        InitializeComponent();
        MessageText.Text = Tr.T(
            $"Введите серийный номер, полученный при оплате темы «{themeName}».",
            $"«{themeName}» темасы үчүн төлөгөндө алган сериялык номериңизди киргизиңиз.", $"Enter the serial number you received when paying for the “{themeName}” theme.", $"«{themeName}» teması için ödeme yaparken aldığınız seri numarasını girin.", $"«{themeName}» mavzusini to'laganda olingan seriya raqamini kiriting.");
        Opened += (_, _) => SerialBox.Focus();
    }

    public static string? Show(Window? owner, string themeName)
    {
        var dlg = new SerialActivationDialog(themeName);
        return PosDialogHost.Show(dlg, owner) == true ? dlg.EnteredSerial : null;
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ActivateButton_Click(object? sender, RoutedEventArgs e) => Confirm();

    private void SerialBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Confirm();
    }

    private void Confirm()
    {
        var text = (SerialBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            ErrorText.Text = Tr.T("Введите серийный номер.", "Сериялык номерди киргизиңиз.", "Enter the serial number.", "Seri numarasını girin.", "Seriya raqamini kiriting.");
            ErrorText.IsVisible = true;
            return;
        }

        EnteredSerial = text;
        Close(true);
    }
}
