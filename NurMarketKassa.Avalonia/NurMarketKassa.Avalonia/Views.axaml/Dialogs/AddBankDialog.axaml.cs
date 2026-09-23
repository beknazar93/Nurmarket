using System.Collections.Generic;
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

    /// <summary>Возвращает выбранное или введённое название банка либо null при отмене.</summary>
    /// <param name="known">Банки, которых ещё нет в списке настроек. Показываются готовым
    /// списком: от написания названия зависит ключ, по которому хранится QR-код банка,
    /// и опечатка привела бы к «потерянному» QR.</param>
    public static string? Show(Window? owner, IReadOnlyList<string>? known = null)
    {
        var dialog = new AddBankDialog();
        if (known is { Count: > 0 })
            dialog.KnownBox.ItemsSource = known;
        else
            dialog.KnownBox.IsVisible = false;

        return PosDialogHost.Show(dialog, owner) == true ? dialog.EnteredName : null;
    }

    /// <summary>Выбор из списка сразу подставляется в поле ввода — так работает и проверка
    /// на пустое значение, и обработка Enter, без второй ветки логики.</summary>
    private void KnownBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (KnownBox.SelectedItem is string name && !string.IsNullOrWhiteSpace(name))
        {
            NameBox.Text = name;
            ErrorText.IsVisible = false;
        }
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
