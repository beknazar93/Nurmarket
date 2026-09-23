using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class AddBankDialog : Window
{
    public AddBankDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameBox.Focus();
    }

    /// <summary>Возвращает введённое название банка (обрезанное, непустое) или null при отмене.</summary>
    public static string? Show(Window? owner)
    {
        var dialog = new AddBankDialog();
        return PosDialogHost.Show(dialog, owner) == true ? dialog.EnteredName : null;
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e) => TryConfirm();

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            TryConfirm();
    }

    private void TryConfirm()
    {
        var name = (NameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorText.Text = Tr.T("Введите название банка.", "Банктын атын жазыңыз.", "Enter the bank name.", "Banka adını girin.", "Bank nomini kiriting.");
            ErrorText.IsVisible = true;
            NameBox.Focus();
            return;
        }

        EnteredName = name;
        Close(true);
    }

    public string? EnteredName { get; private set; }
}
