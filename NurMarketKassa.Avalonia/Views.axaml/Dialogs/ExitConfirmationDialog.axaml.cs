using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ExitConfirmationDialog : Window
{
    public ExitConfirmationDialog() => InitializeComponent();

    /// <summary>Показывает модальный диалог и возвращает true, если пользователь нажал «Да».</summary>
    public static async Task<bool> ConfirmExitAsync(Window owner)
    {
        var dialog = new ExitConfirmationDialog();
        var result = await dialog.ShowDialog<bool?>(owner);
        return result == true;
    }

    private void YesButton_Click(object sender, RoutedEventArgs e) => Close(true);

    private void NoButton_Click(object sender, RoutedEventArgs e) => Close(false);
}
