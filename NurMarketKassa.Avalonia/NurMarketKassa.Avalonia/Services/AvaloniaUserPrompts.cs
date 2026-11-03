using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views;
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
    public Task<bool> ConfirmAsync(string message) => ConfirmAsync(message, null);

    // 2026-10-22 (модалки, пакет 0): кнопки — глаголы: по умолчанию «Продолжить» / «Отмена» (было «Да» / «Нет»),
    // свои — confirmText/cancelText, danger — красная главная.
    public Task<bool> ConfirmAsync(string message, string? confirmText, string? cancelText = null, bool danger = false) =>
        RunOnUiTaskAsync(async () =>
            string.Equals(message, "Подтвердить оплату?", StringComparison.Ordinal)
                ? await PaymentConfirmationDialog.ShowModalAsync(null).ConfigureAwait(true)
                : await PosConfirmDialog.ShowModalAsync(
                    null,
                    Tr.T("Подтверждение", "Ырастоо", "Confirmation", "Onay", "Tasdiqlash"),
                    message,
                    confirmText: confirmText,
                    cancelText: cancelText,
                    accent: danger ? PosConfirmAccent.Danger : PosConfirmAccent.Primary).ConfigureAwait(true));

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
    public Task<bool> ConfirmAsync(string message) => ConfirmAsync(message, null);

    // 2026-10-22 (модалки, пакет 0): кнопки — глаголы: по умолчанию «Продолжить» / «Отмена» (было «Да» / «Нет»),
    // свои — confirmText/cancelText, danger — красная главная. Через PosMessageBox, как и раньше («Подтвердить оплату?»
    // по-прежнему открывает окно оплаты).
    public async Task<bool> ConfirmAsync(string message, string? confirmText, string? cancelText = null, bool danger = false)
    {
        // 2026-11-02 (аудит №2, Д3-01): окно подтверждения открывается следующим проходом цикла — не внутри обработчика нажатия (UIA Invoke).
        if (Dispatcher.UIThread.CheckAccess())
            await Task.Yield();
        return await RunOnUiAsync(() =>
            PosMessageBox.Show(null, message, Tr.T("Подтверждение", "Ырастоо", "Confirmation", "Onay", "Tasdiqlash"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.OK, confirmText, cancelText, danger)
            == MessageBoxResult.Yes).ConfigureAwait(true);
    }

    // 2026-11-01 (контрольный аудит, Д-10): ShowToast открывал модальное окно с кнопкой «ОК» (41 вызов — лишний клик на каждое сообщение).
    // 2026-11-02 (приёмка 1.17.73, п. 1): плашка гасла через 3–14 с, а среди сообщений — ошибки Склада, «Смена закрыта офлайн… не отправлено N»,
    // отказы сервера: важное для кассира не должно исчезать. Правило: isWarning = false (успех/сообщение: «Товар добавлен», «Сохранено»,
    // «Чек напечатан») — плашка в углу (SuccessToast); isWarning = true (ошибка, предупреждение, причина, числа, что делать дальше) — окно
    // «Внимание», как до Д-10. Короткие «недоступно в этом режиме» вызываются без isWarning (плашка). На Android — прежний диалог (ветка выше).
    public void ShowToast(string message, bool isWarning = false)
    {
        if (isWarning)
            ShowWarning(message);
        else
            SuccessToast.Show(message, ToastKind.Info);
    }

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
