using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Пользовательские подсказки. Любой вызов безопасен с фонового потока.
/// </summary>
public sealed class AvaloniaUserPrompts : IUserPrompts
{
    public async Task<bool> ConfirmAsync(string message)
    {
        return await RunOnUiAsync(() =>
            PosMessageBox.Show(message, "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes).ConfigureAwait(true);
    }

    public void ShowToast(string message, bool isWarning = false) =>
        RunOnUi(() =>
            PosMessageBox.Show(message, isWarning ? "Внимание" : "Сообщение",
                MessageBoxButton.OK, isWarning ? MessageBoxImage.Warning : MessageBoxImage.Information));

    public void ShowWarning(string message) =>
        RunOnUi(() =>
            PosMessageBox.Show(message, "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning));

    public void ShowError(string message) =>
        RunOnUi(() =>
            PosMessageBox.Show(message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error));

    public async Task<bool> ConfirmWithPasswordAsync(string title, string message, string expectedPassword)
    {
        return await RunOnUiAsync(() =>
            PasswordPromptDialog.Show(null, title, message, expectedPassword)).ConfigureAwait(true);
    }

    public async Task<bool> ConfirmWithCodeAsync(string title, string message, Func<string, bool> validator)
    {
        return await RunOnUiAsync(() =>
            PasswordPromptDialog.Show(null, title, message, validator)).ConfigureAwait(true);
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private static async Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return func();

        return await Dispatcher.UIThread.InvokeAsync(func);
    }
}
