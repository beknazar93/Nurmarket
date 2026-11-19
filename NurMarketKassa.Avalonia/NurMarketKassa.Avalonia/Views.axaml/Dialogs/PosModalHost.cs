using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public static class PosModalHost
{
    // 2026-11-19 (список владельца, п. 7): повтор того же диалога за 0,7 с (двойное нажатие) не открывает второе окно.
    public static Task ShowAsync(Window owner, Window dialog) => PosDialogHost.IsDuplicateOpen(dialog, owner) ? Task.CompletedTask : dialog.ShowDialog(owner);
}
