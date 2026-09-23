using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class PaymentConfirmationDialog : Window
{
    public PaymentConfirmationDialog() => InitializeComponent();

    public static new bool Show(Window? owner) =>
        PosDialogHost.Show(new PaymentConfirmationDialog(), owner) == true;

    private void YesButton_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void NoButton_Click(object? sender, RoutedEventArgs e) => Close(false);
}
