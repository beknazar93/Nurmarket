using System.Globalization;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

public sealed class AvaloniaPosCheckoutUiFlow : IPosCheckoutUiFlow
{
    private readonly ICartService _cart;
    private readonly MainWindowHostBridge _bridge;
    private PaymentStatusDialog? _paymentStatusDialog;
    private Task<bool?>? _paymentStatusTask;

    public AvaloniaPosCheckoutUiFlow(ICartService cart, MainWindowHostBridge bridge)
    {
        _cart = cart;
        _bridge = bridge;
    }

    public Task<bool> PrepareCheckoutAsync()
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            var owner = _bridge.Window;
            if (owner == null)
                return true;

            var issues = StockAvailabilityService.EvaluateCurrentCart(
                _cart,
                additionalReservedLookup: _bridge.GetOtherOpenReceiptQuantity);
            if (issues.Count == 0)
                return true;

            var dialog = new PaymentStockBlockedDialog(issues);
            PosDialogHost.Show(dialog, owner);
            return false;
        }).GetTask();
    }

    public async Task ShowPaymentProcessingAsync(double totalAmount)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowPaymentProcessingCoreAsync(totalAmount));
            return;
        }

        await ShowPaymentProcessingCoreAsync(totalAmount).ConfigureAwait(true);
    }

    public async Task ShowPaymentResultAsync(bool isSuccess, string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowPaymentResultCoreAsync(isSuccess, message));
            return;
        }

        await ShowPaymentResultCoreAsync(isSuccess, message).ConfigureAwait(true);
    }

    private async Task ShowPaymentProcessingCoreAsync(double totalAmount)
    {
        await ClosePaymentStatusDialogAsync().ConfigureAwait(true);

        var owner = _bridge.Window;
        if (owner is not { IsVisible: true })
            return;

        _paymentStatusDialog = new PaymentStatusDialog(totalAmount);
        _paymentStatusTask = _paymentStatusDialog.ShowDialog<bool?>(owner);

        // Allow the first loading frame to render before starting network work.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    private async Task ShowPaymentResultCoreAsync(bool isSuccess, string message)
    {
        var dialog = _paymentStatusDialog;
        var dialogTask = _paymentStatusTask;
        if (dialog is null || dialogTask is null)
            return;

        dialog.ShowResult(isSuccess, message);
        if (isSuccess)
        {
            await Task.Delay(1200).ConfigureAwait(true);
            if (dialog.IsVisible)
                dialog.Close(true);
        }

        try
        {
            await dialogTask.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_paymentStatusDialog, dialog))
            {
                _paymentStatusDialog = null;
                _paymentStatusTask = null;
            }
        }
    }

    private async Task ClosePaymentStatusDialogAsync()
    {
        var dialog = _paymentStatusDialog;
        var task = _paymentStatusTask;
        if (dialog is { IsVisible: true })
            dialog.Close(false);

        if (task is not null)
        {
            try { await task.ConfigureAwait(true); }
            catch (Exception ex) { PosLogger.Log($"Payment status close failed: {ex}", "PAYMENT"); }
        }

        _paymentStatusDialog = null;
        _paymentStatusTask = null;
    }

    public async Task ShowPaymentSuccessAsync(
        double totalAmount,
        bool defaultPrintReceipt,
        string? cartJson,
        string? paymentMethod,
        string? cashReceived)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ShowPaymentSuccessCoreAsync(
                    totalAmount,
                    defaultPrintReceipt,
                    cartJson,
                    paymentMethod,
                    cashReceived));
            return;
        }

        await ShowPaymentSuccessCoreAsync(
            totalAmount,
            defaultPrintReceipt,
            cartJson,
            paymentMethod,
            cashReceived).ConfigureAwait(true);
    }

    private async Task ShowPaymentSuccessCoreAsync(
        double totalAmount,
        bool defaultPrintReceipt,
        string? cartJson,
        string? paymentMethod,
        string? cashReceived)
    {
        var owner = _bridge.Window;
        if (owner is not { IsVisible: true })
            return;

        var successDialog = new SaleSuccessDialog(totalAmount, defaultPrintReceipt);
        if (await PosDialogHost.ShowAsync(successDialog, owner).ConfigureAwait(true) != true)
            return;

        // Give Avalonia one dispatcher turn to re-enable the owner before another modal.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

        switch (successDialog.Action)
        {
            case SaleSuccessDialogAction.Preview:
            {
                var receiptText = CartReceiptTextBuilder.BuildSimpleReceipt(
                    cartJson ?? "{}",
                    paymentMethodKey: paymentMethod,
                    cashReceived: cashReceived);
                var preview = new ReceiptPreviewDialog(receiptText);
                await PosDialogHost.ShowAsync(preview, owner).ConfigureAwait(true);
                if (preview.IsPrintRequested)
                    await PrintReceiptAsync(owner, cartJson, paymentMethod, cashReceived).ConfigureAwait(true);
                break;
            }
            case SaleSuccessDialogAction.Print:
                await PrintReceiptAsync(owner, cartJson, paymentMethod, cashReceived).ConfigureAwait(true);
                break;
        }
    }

    public async Task OpenNextDeferredCartIfAnyAsync()
    {
        if (_bridge.OpenNextDeferredCart != null)
            await _bridge.OpenNextDeferredCart().ConfigureAwait(true);
    }

    private static async Task PrintReceiptAsync(
        Window owner,
        string? cartJson,
        string? paymentMethod,
        string? cashReceived)
    {
        try
        {
            await Task.Run(() => ReceiptPrintService.PrintReceipt(
                cartJson ?? "{}",
                paymentMethodKey: paymentMethod,
                cashReceived: cashReceived)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Manual receipt print failed: {ex}", "PRINTER");
            await PosAlertDialog.ShowAsync(
                owner,
                "Печать чека",
                "Не удалось напечатать чек: " + ex.Message,
                PosAlertKind.Error).ConfigureAwait(true);
        }
    }
}

public sealed class AvaloniaWeightInputPrompt : IWeightInputPrompt
{
    private readonly ScaleWeightProvider _scaleProvider;
    private readonly MainWindowHostBridge _bridge;

    public AvaloniaWeightInputPrompt(ScaleWeightProvider scaleProvider, MainWindowHostBridge bridge)
    {
        _scaleProvider = scaleProvider;
        _bridge = bridge;
    }

    public Task<double?> PromptWeightKgAsync(string productTitle, CancellationToken cancellationToken = default)
    {
        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = _bridge.Window;
            if (owner == null)
                return (double?)null;

            var scale = HardwareModeHelper.UsePhysicalScale() ? _scaleProvider.Scale : null;
            var dlg = new WeighedProductDialog(productTitle, "", scale);
            if (PosDialogHost.Show(dlg, owner) != true || string.IsNullOrWhiteSpace(dlg.QuantityNormalized))
                return null;

            return double.TryParse(dlg.QuantityNormalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                ? weight
                : null;
        }, DispatcherPriority.Normal, cancellationToken).GetTask();
    }
}

public sealed class AvaloniaShiftOpenCoordinator : IShiftOpenCoordinator
{
    private readonly MainWindowHostBridge _bridge;
    private readonly ICashShiftService _cashShiftService;

    public AvaloniaShiftOpenCoordinator(MainWindowHostBridge bridge, ICashShiftService cashShiftService)
    {
        _bridge = bridge;
        _cashShiftService = cashShiftService;
    }

    public async Task<bool> TryOpenShiftAsync(CancellationToken cancellationToken = default)
    {
        var window = _bridge.Window;
        if (window == null)
            return false;

        return await window.OpenShiftFromCoordinatorAsync(cancellationToken).ConfigureAwait(true);
    }
}
