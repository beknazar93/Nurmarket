using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Диалоги Avalonia. Все Show* всегда маршалятся на UI-поток.
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
#if NURANDROID
    // 2026-10-04, Android-касса: окна оплаты (подтверждение, «принтер не подключён», сообщения)
    // ждём по-настоящему асинхронно — без вложенного цикла сообщений, которого у Avalonia на
    // Android нет. Тексты и окна те же, что в Windows (ветка #else ниже — без изменений).
    public Task<bool> ConfirmAsync(string title, string message) =>
        RunOnUiTaskAsync(() => PosConfirmDialog.ShowModalAsync(GetOwner(), title, message));

    public Task ShowInfoAsync(string message) =>
        RunOnUiTaskAsync(async () =>
        {
            await PosAlertDialog.ShowAsync(GetOwner(), "Сообщение", message, PosAlertKind.Info).ConfigureAwait(true);
            return true;
        });

    public Task ShowErrorAsync(string message) =>
        RunOnUiTaskAsync(async () =>
        {
            await PosAlertDialog.ShowAsync(GetOwner(), "Ошибка", message, PosAlertKind.Error).ConfigureAwait(true);
            return true;
        });

    public Task<PrinterNotConnectedResult> ShowPrinterNotConnectedAsync() =>
        RunOnUiTaskAsync(() => PrinterNotConnectedDialog.ShowCheckoutModalAsync(GetOwner()));

    public Task<bool> ConfirmPaymentAsync() =>
        RunOnUiTaskAsync(() => PaymentConfirmationDialog.ShowModalAsync(GetOwner()));

    private static async Task<T> RunOnUiTaskAsync<T>(Func<Task<T>> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return await func().ConfigureAwait(true);

        return await Dispatcher.UIThread.InvokeAsync(func);
    }
#else
    public Task<bool> ConfirmAsync(string title, string message) =>
        RunOnUiAsync(() => PosConfirmDialog.Show(GetOwner(), title, message));

    public Task ShowInfoAsync(string message) =>
        RunOnUiAsync(() =>
            PosAlertDialog.Show(GetOwner(), "Сообщение", message, PosAlertKind.Info));

    public Task ShowErrorAsync(string message) =>
        RunOnUiAsync(() =>
            PosAlertDialog.Show(GetOwner(), "Ошибка", message, PosAlertKind.Error));

    public Task<PrinterNotConnectedResult> ShowPrinterNotConnectedAsync() =>
        RunOnUiAsync(() => PrinterNotConnectedDialog.ShowCheckout(GetOwner()));

    public Task<bool> ConfirmPaymentAsync() =>
        RunOnUiAsync(() => PaymentConfirmationDialog.Show(GetOwner()));
#endif

    private static Window GetOwner() => PosDialogHost.ResolveOwner(null);

    private static async Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private static async Task<T> RunOnUiAsync<T>(Func<T> func)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return func();

        return await Dispatcher.UIThread.InvokeAsync(func);
    }
}
