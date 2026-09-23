using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class CashOperationsDialog : Window
{
    public Func<decimal, Task>? OpenShiftAction { get; set; }
    public Func<decimal?, Task>? CloseShiftAction { get; set; }

    public CashOperationsDialog() => InitializeComponent();

    private async void OpenShift_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = App.GetRequiredService<OpenShiftDialog>();
        if (PosDialogHost.Show(dlg, this) != true || OpenShiftAction == null)
            return;

        await OpenShiftAction(dlg.OpeningCash).ConfigureAwait(true);
    }

    private async void CloseShift_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = App.GetRequiredService<CloseShiftDialog>();
        if (PosDialogHost.Show(dlg, this) != true || CloseShiftAction == null)
            return;

        await CloseShiftAction(dlg.ClosingCash).ConfigureAwait(true);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);
}
