using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Сервис показа окон Avalonia-хоста, включая модальный CheckoutDialog.
/// Все операции с Window выполняются только на UI-потоке.
/// </summary>
public sealed class AvaloniaWindowService : IWindowService
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<object, Window> _openWindows = new();

    public AvaloniaWindowService(IServiceProvider services)
    {
        _services = services;
    }

    public async Task<TResult?> ShowDialogAsync<TViewModel, TResult>(TViewModel viewModel)
        where TViewModel : class
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = CreateWindow(viewModel);
            window.DataContext = viewModel;
            _openWindows[viewModel] = window;

            try
            {
                var owner = GetDialogOwner(window)
                    ?? throw new InvalidOperationException("Cannot show dialog: no active Avalonia window.");

                PosLogger.Log(
                    $"ShowDialogAsync<{typeof(TViewModel).Name}> on UI thread={Dispatcher.UIThread.CheckAccess()}, " +
                    $"owner={owner.GetType().Name}, visible={owner.IsVisible}, active={owner.IsActive}",
                    "PAYMENT");

                return await window.ShowDialog<TResult?>(owner).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log(
                    $"ShowDialogAsync<{typeof(TViewModel).Name}> failed: {ex}",
                    "PAYMENT");
                throw;
            }
            finally
            {
                _openWindows.Remove(viewModel);
            }
        }).ConfigureAwait(true);
    }

    public void ShowWindow<TViewModel>(TViewModel viewModel)
        where TViewModel : class
    {
        void ShowCore()
        {
            var window = CreateWindow(viewModel);
            window.DataContext = viewModel;
            _openWindows[viewModel] = window;
            window.Closed += (_, _) => _openWindows.Remove(viewModel);

            var owner = GetDialogOwner(window);
            if (owner is not null)
                window.Show(owner);
            else
                window.Show();
        }

        if (Dispatcher.UIThread.CheckAccess())
            ShowCore();
        else
            Dispatcher.UIThread.Post(ShowCore);
    }

    public void Close(object viewModel, bool? dialogResult = null)
    {
        void CloseCore()
        {
            if (!_openWindows.TryGetValue(viewModel, out var window))
                return;

            if (dialogResult.HasValue)
                window.Close(dialogResult.Value);
            else
                window.Close();
        }

        if (Dispatcher.UIThread.CheckAccess())
            CloseCore();
        else
            Dispatcher.UIThread.Post(CloseCore);
    }

    private Window CreateWindow(object viewModel) =>
        viewModel switch
        {
            CheckoutViewModel checkout => new CheckoutDialog(checkout, _services.GetRequiredService<IDialogService>()),
            _ => throw new NotSupportedException(
                $"No Avalonia window registered for view model type '{viewModel.GetType().FullName}'.")
        };

    private static Window? GetDialogOwner(Window dialog)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        // MainWindow can temporarily still reference a closed login window during
        // navigation. Passing such a window to Avalonia ShowDialog reaches a null
        // native owner and results in an internal NullReferenceException.
        var activeWindow = desktop.Windows
            .LastOrDefault(window =>
                !ReferenceEquals(window, dialog) &&
                window.IsVisible &&
                window.IsActive);
        if (activeWindow is not null)
            return activeWindow;

        if (desktop.MainWindow is { IsVisible: true } mainWindow &&
            !ReferenceEquals(mainWindow, dialog))
        {
            return mainWindow;
        }

        return desktop.Windows.LastOrDefault(window =>
            !ReferenceEquals(window, dialog) && window.IsVisible);
    }
}
