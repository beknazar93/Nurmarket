using Avalonia.Threading;
using UiDispatcher = NurMarketKassa.Ui.Shared.IDispatcher;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Маршалинг на UI-поток Avalonia. При вызове уже с UI-потока выполняет синхронно
/// (без постановки в очередь), чтобы не создавать deadlock на Invoke/GetResult.
/// </summary>
public sealed class AvaloniaDispatcher : UiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public async Task InvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    public async Task InvokeAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }
}
