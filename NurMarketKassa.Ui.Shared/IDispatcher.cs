namespace NurMarketKassa.Ui.Shared;

/// <summary>
/// Marshals work onto the UI thread for the active host (WPF Dispatcher / Avalonia Dispatcher).
/// </summary>
public interface IDispatcher
{
    void Post(Action action);

    Task InvokeAsync(Func<Task> action);

    Task InvokeAsync(Action action);
}
