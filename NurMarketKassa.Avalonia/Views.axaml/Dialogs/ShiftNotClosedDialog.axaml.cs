using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public enum ShiftNotClosedDialogResult
{
    Cancel,
    CloseShift,
}

public partial class ShiftNotClosedDialog : Window
{
    public ShiftNotClosedDialogResult Result { get; private set; } = ShiftNotClosedDialogResult.Cancel;

    public ShiftNotClosedDialog() => InitializeComponent();

    public static ShiftNotClosedDialogResult Prompt(Window? owner)
    {
        var dlg = new ShiftNotClosedDialog();
        PosDialogHost.Show(dlg, owner);
        return dlg.Result;
    }

    public static new ShiftNotClosedDialogResult Show(Window? owner) => Prompt(owner);

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = ShiftNotClosedDialogResult.Cancel;
        Close(false);
    }

    private void CloseShiftButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = ShiftNotClosedDialogResult.CloseShift;
        Close(true);
    }
}
