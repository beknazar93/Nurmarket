using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public static class PosModalHost
{
    public static Task ShowAsync(Window owner, Window dialog) => dialog.ShowDialog(owner);
}
