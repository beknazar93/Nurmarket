using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public class AppOverlayDialogBase : Window
{
    public AppOverlayDialogBase()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Background = Avalonia.Media.Brushes.Transparent;
    }

    protected void CloseWithAnimation() => Close();

    protected void CloseWithAnimation(bool? result)
    {
        // Avalonia: use Close(result) for dialog result when shown via ShowDialog
        Close(result);
    }
}
