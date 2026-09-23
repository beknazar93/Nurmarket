namespace NurMarketKassa.Ui.Shared;

/// <summary>
/// Cross-platform window / dialog host abstraction.
/// Implementations live in WPF and Avalonia hosts.
/// </summary>
public interface IWindowService
{
    Task<TResult?> ShowDialogAsync<TViewModel, TResult>(TViewModel viewModel)
        where TViewModel : class;

    void ShowWindow<TViewModel>(TViewModel viewModel)
        where TViewModel : class;

    void Close(object viewModel, bool? dialogResult = null);
}
