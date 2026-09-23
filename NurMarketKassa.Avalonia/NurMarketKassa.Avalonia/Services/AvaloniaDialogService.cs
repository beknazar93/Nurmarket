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
