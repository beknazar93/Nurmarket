using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System.Runtime.ExceptionServices;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public static class PosDialogHost
{
    public static Window ResolveOwner(Window? owner)
    {
        if (owner != null)
            return owner;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } main)
            return main;

        throw new InvalidOperationException("Нет активного окна для диалога.");
    }

    /// <summary>
    /// Shows a modal dialog. Safe to call from the UI thread (pumps the dispatcher
    /// instead of deadlocking on <c>GetResult()</c>).
    /// </summary>
    public static bool? Show(Window dialog, Window? owner)
    {
        owner = ResolveOwner(owner);
        var task = dialog.ShowDialog<bool?>(owner);

        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException(
                "Synchronous dialogs must be opened on the UI thread. Use ShowAsync from background work.");

        // UI thread: pump until the dialog closes so Click → Close can complete.
        using var cts = new CancellationTokenSource();
        bool? result = null;
        ExceptionDispatchInfo? failure = null;

        async Task ObserveDialogAsync()
        {
            try
            {
                result = await task.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                cts.Cancel();
            }
        }

        var observer = ObserveDialogAsync();

        if (!observer.IsCompleted)
            Dispatcher.UIThread.MainLoop(cts.Token);
        failure?.Throw();
        return result;
    }

    public static Task<bool?> ShowAsync(Window dialog, Window? owner)
    {
        owner = ResolveOwner(owner);
        return dialog.ShowDialog<bool?>(owner);
    }

    public static Task<T?> ShowDialogAsync<T>(Window owner, Window dialog) where T : class =>
        dialog.ShowDialog<T?>(owner);
}
