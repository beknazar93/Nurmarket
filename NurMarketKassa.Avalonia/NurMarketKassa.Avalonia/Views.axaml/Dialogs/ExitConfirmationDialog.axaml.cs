using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ExitConfirmationDialog : Window
{
    public ExitConfirmationDialog()
    {
        InitializeComponent();
        // 2026-10-22 (модалки, пакет 0): кнопки — глаголы (стандарт модалки), Esc/✕ — «Остаться».
        StayButton.Content = Tr.T("Остаться", "Калуу", "Stay", "Kal", "Qolish");
        ExitButton.Content = Tr.T("Выйти", "Чыгуу", "Exit", "Çık", "Chiqish");
    }

    /// <summary>Показывает модальный диалог и возвращает true, если пользователь нажал «Выйти».</summary>
    public static async Task<bool> ConfirmExitAsync(Window owner)
    {
        var dialog = new ExitConfirmationDialog();
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    private void YesButton_Click(object sender, RoutedEventArgs e) => Close(true);

    private void NoButton_Click(object sender, RoutedEventArgs e) => Close(false);
}
