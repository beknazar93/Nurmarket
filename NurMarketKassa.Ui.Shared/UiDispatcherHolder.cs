namespace NurMarketKassa.Ui.Shared;

/// <summary>
/// Optional app-wide UI dispatcher for services that cannot receive DI (static sync helpers).
/// Set once at host startup (Avalonia / WPF).
/// </summary>
public static class UiDispatcherHolder
{
    public static IDispatcher? Current { get; set; }

    public static void Post(Action action)
    {
        if (Current is { } dispatcher)
            dispatcher.Post(action);
        else
            action();
    }

    public static Task InvokeAsync(Action action) =>
        Current is { } dispatcher
            ? dispatcher.InvokeAsync(action)
            : Task.Run(action);
}
