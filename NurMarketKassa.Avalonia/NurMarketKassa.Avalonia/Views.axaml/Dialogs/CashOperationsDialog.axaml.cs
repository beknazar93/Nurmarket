using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class CashOperationsDialog : Window
{
    public Func<decimal, Task>? OpenShiftAction { get; set; }
    public Func<decimal?, Task>? CloseShiftAction { get; set; }

    public CashOperationsDialog() => InitializeComponent();

    private async void OpenShift_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = App.GetRequiredService<OpenShiftDialog>();
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true || OpenShiftAction == null)
            return;

        await OpenShiftAction(dlg.OpeningCash).ConfigureAwait(true);
    }

    private async void CloseShift_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = App.GetRequiredService<CloseShiftDialog>();
        if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true || CloseShiftAction == null)
            return;

        await CloseShiftAction(dlg.ClosingCash).ConfigureAwait(true);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);
}
