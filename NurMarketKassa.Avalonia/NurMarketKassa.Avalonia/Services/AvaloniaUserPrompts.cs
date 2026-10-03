using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Пользовательские подсказки. Любой вызов безопасен с фонового потока.
/// </summary>
public sealed class AvaloniaUserPrompts : IUserPrompts
{
#if NURANDROID
    // 2026-10-04, Android-касса: подтверждения ждём асинхронно (без вложенного цикла, которого у
    // Avalonia на Android нет), сообщения показываем без ожидания. Окна и тексты — как в Windows
    // (PosMessageBox: «Подтвердить оплату?» — отдельное окно оплаты, остальное — «Да / Нет»).
    public Task<bool> ConfirmAsync(string message) =>
        RunOnUiTaskAsync(async () =>
            string.Equals(message, "Подтвердить оплату?", StringComparison.Ordinal)
                ? await PaymentConfirmationDialog.ShowModalAsync(null).ConfigureAwait(true)
                : await PosConfirmDialog.ShowModalAsync(
                    null,
                    Tr.T("Подтверждение", "Ырастоо", "Confirmation", "Onay", "Tasdiqlash"),
                    message,
                    confirmText: Tr.T("Да", "Ооба", "Yes", "Evet", "Ha"),
                    cancelText: Tr.T("Нет", "Жок", "No", "Hayır", "Yo'q")).ConfigureAwait(true));

    public void ShowToast(string message, bool isWarning = false) =>
        RunOnUi(() => _ = PosAlertDialog.ShowAsync(null,
            isWarning ? Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat") : Tr.T("Сообщение", "Билдирүү", "Message", "Mesaj", "Xabar"),
            message, isWarning ? PosAlertKind.Warning : PosAlertKind.Info));

    public void ShowWarning(string message) =>
        RunOnUi(() => _ = PosAlertDialog.ShowAsync(null, Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat"), message, PosAlertKind.Warning));

    public void ShowError(string message) =>
        RunOnUi(() => _ = PosAlertDialog.ShowAsync(null, Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"), message, PosAlertKind.Error));

    public Task<bool> ConfirmWithPasswordAsync(string title, string message, string expectedPassword) =>
        RunOnUiTaskAsync(async () =>
            await PosDialogHost.ShowAsync(new PasswordPromptDialog(title, message, expectedPassword), null).ConfigureAwait(true) == true);

    public Task<bool> ConfirmWithCodeAsync(string title, string message, Func<string, bool> validator) =>
        RunOnUiTaskAsync(async () =>
            await PosDialogHost.ShowAsync(new PasswordPromptDialog(title, message, validator), null).ConfigureAwait(true) == true);

    private static async Task<T> RunOnUiTaskAsync<T>(Func<Task<T>> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await func().ConfigureAwait(true);

        return await Dispatcher.UIThread.InvokeAsync(func);
    }
#else
    public async Task<bool> ConfirmAsync(string message)
    {
        return await RunOnUiAsync(() =>
            PosMessageBox.Show(message, Tr.T("Подтверждение", "Ырастоо", "Confirmation", "Onay", "Tasdiqlash"), MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes).ConfigureAwait(true);
    }

    public void ShowToast(string message, bool isWarning = false) =>
        RunOnUi(() =>
            PosMessageBox.Show(message, isWarning ? Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat") : Tr.T("Сообщение", "Билдирүү", "Message", "Mesaj", "Xabar"),
                MessageBoxButton.OK, isWarning ? MessageBoxImage.Warning : MessageBoxImage.Information));

    public void ShowWarning(string message) =>
        RunOnUi(() =>
            PosMessageBox.Show(message, Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat"), MessageBoxButton.OK, MessageBoxImage.Warning));

    public void ShowError(string message) =>
        RunOnUi(() =>
            PosMessageBox.Show(message, Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"), MessageBoxButton.OK, MessageBoxImage.Error));

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
#endif

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
