using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using MediatR;
using NurMarketKassa.Core.Application.Notifications;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

/// <summary>
/// Общая реализация оплаты POS: онлайн checkout, офлайн-очередь, печать и новый чек.
/// </summary>
public sealed class PosCheckoutService : IPosCheckoutService
{
    private readonly ICartService _cart;
    private readonly ISalesApiService _salesApi;
    private readonly IShiftStateService _shiftStateService;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly IMediator _mediator;

    public PosCheckoutService(
        ICartService cart,
        ISalesApiService salesApi,
        IShiftStateService shiftStateService,
        IReceiptPrinterService receiptPrinter,
        IMediator mediator)
    {
        _cart = cart;
        _salesApi = salesApi;
        _shiftStateService = shiftStateService;
        _receiptPrinter = receiptPrinter;
        _mediator = mediator;
    }

    public async Task PrepareCartForCheckoutAsync(CancellationToken cancellationToken = default)
    {
        if (!_cart.HasCart || _cart.LineCount == 0)
            throw new ApiException("Добавьте товары в корзину.", 400);

        if (OfflineModeHelper.UseLocalOperations || _cart.IsLocalOffline)
            return;

        if (_cart.IsStaging || ! _cart.CanRefresh)
        {
            await StagingCartService.MaterializeSnapshotOnServerAsync(
                _salesApi,
                _cart,
                PosApp.PosCashboxId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool> ApplyOrderDiscountAsync(
        Dictionary<string, string> discountBody,
        CancellationToken cancellationToken = default)
    {
        if (discountBody.Count == 0)
            return true;

        if (OfflineModeHelper.UseLocalOperations || _cart.IsLocalOffline || string.IsNullOrWhiteSpace(_cart.CartId))
        {
            var percent = discountBody.TryGetValue("order_discount_percent", out var pct) ? pct : null;
            var total = discountBody.TryGetValue("order_discount_total", out var sum) ? sum : null;
            ReceiptSnapshotCartEditor.PatchOrderDiscount(_cart, percent, total);
            return true;
        }

        try
        {
            await _salesApi
                .PosCartPatchAsync(_cart.CartId!, discountBody, cancellationToken)
                .ConfigureAwait(false);
            _cart.SetCart(await _salesApi.PosCartGetAsync(_cart.CartId!, cancellationToken).ConfigureAwait(false));
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout discount failed: {ex}", "PAYMENT");
            return false;
        }
    }

    public async Task<PosCheckoutResult> CheckoutAsync(
        PosCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_cart.HasCart || _cart.LineCount == 0)
            return PosCheckoutResult.Failed("Добавьте товары в корзину.");

        // Materialization replaces a staging cart before its network work is
        // complete. Keep a recoverable copy for an offline fallback.
        var fallbackCartJson = _cart.GetRawText();
        var fallbackTotal = CartTotalsCalculator.Calculate(_cart.Root).TotalDue;

        try
        {
            await PrepareCartForCheckoutAsync(cancellationToken).ConfigureAwait(false);

            if (request.OrderDiscountBody != null
                && !await ApplyOrderDiscountAsync(request.OrderDiscountBody, cancellationToken).ConfigureAwait(false))
            {
                return PosCheckoutResult.Failed(PaymentErrorMessages.DiscountFailure);
            }

            // Capture the authoritative receipt only after server refresh and
            // after the final discount selected in the payment dialog.
            var cartJsonSnapshot = _cart.GetRawText();
            var total = CartTotalsCalculator.Calculate(_cart.Root).TotalDue;

            if (OfflineModeHelper.UseLocalOperations || _cart.IsLocalOffline)
                return await CompleteOfflineCheckoutAsync(request, cartJsonSnapshot, total)
                    .ConfigureAwait(false);

            return await CompleteOnlineCheckoutAsync(request, cartJsonSnapshot, total, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException ex)
        {
            PaymentErrorMessages.Log("Checkout API error", ex);
            return PosCheckoutResult.Failed(PaymentErrorMessages.ForCashier(ex));
        }
        catch (HttpRequestException ex)
        {
            PosLogger.Log($"Checkout network error, saving offline: {ex}", "PAYMENT");
            var fallback = BuildOfflineFallback(fallbackCartJson, fallbackTotal, request.OrderDiscountBody);
            return await CompleteOfflineCheckoutAsync(request, fallback.CartJson, fallback.Total, ex.Message)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PosLogger.Log("Checkout canceled by caller.", "DEBUG");
            throw;
        }
        catch (TaskCanceledException ex)
        {
            PosLogger.Log($"Checkout HTTP timeout: {ex.GetType().Name}", "WARNING");
            var fallback = BuildOfflineFallback(fallbackCartJson, fallbackTotal, request.OrderDiscountBody);
            return await CompleteOfflineCheckoutAsync(
                    request, fallback.CartJson, fallback.Total, "Таймаут оплаты или потеря сети.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PaymentErrorMessages.Log("Checkout unexpected error", ex);
            return PosCheckoutResult.Failed(PaymentErrorMessages.ForCashier(ex));
        }
    }

    public async Task<string?> RestartSaleSessionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(PosApp.ActiveShiftId))
            {
                _cart.ResetForNewReceipt();
                return "Новый чек не открыт: смена не открыта. Откройте смену и нажмите «Новый чек».";
            }

            if (OfflineModeHelper.UseLocalOperations)
            {
                LocalCartService.StartNewLocalCart(_cart);
                return null;
            }

            var serverCart = await _salesApi
                .PosSalesStartAsync(PosApp.PosCashboxId, cancellationToken)
                .ConfigureAwait(false);
            _cart.SetCart(serverCart);
            return null;
        }
        catch (ApiException ex) when (OfflineModeHelper.CanOperateWithoutServer)
        {
            PosLogger.Log($"Restart sale offline after API error: {ex}", "PAYMENT");
            LocalCartService.StartNewLocalCart(_cart);
            return null;
        }
        catch (HttpRequestException ex) when (OfflineModeHelper.CanOperateWithoutServer)
        {
            LocalCartService.StartNewLocalCart(_cart);
            PosLogger.Log($"Restart sale offline after network error: {ex}", "PAYMENT");
            return null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Restart sale failed: {ex}", "PAYMENT");
            return ex.Message;
        }
    }

    private async Task<PosCheckoutResult> CompleteOfflineCheckoutAsync(
        PosCheckoutRequest request,
        string cartJsonSnapshot,
        double total,
        string? reason = null)
    {
        var entry = new OfflineSaleEntry
        {
            PaymentMethod = request.PaymentMethod ?? "",
            CashReceived = request.CashReceived,
            CartJson = cartJsonSnapshot,
            CartId = _cart.CartId,
            ShiftId = PosApp.ActiveShiftId,
            BranchId = PosApp.AuthApi.ActiveBranchId,
            CashboxId = PosApp.PosCashboxId,
        };

        OfflinePendingSalesStore.Append(entry);
        ApplyOfflineStockDecrement(cartJsonSnapshot);

        var printed = request.PrintReceipt && await TryPrintReceiptAsync(
            cartJsonSnapshot,
            request.PaymentMethod,
            request.CashReceived,
            offlineNote: "ОФФЛАЙН (ожидает выгрузку)").ConfigureAwait(false);

        // The completed receipt must disappear before the cashier can start
        // another operation; a delayed background reset could erase new items.
        LocalCartService.StartNewLocalCart(_cart);

        var info = reason != null
            ? $"Оплата сохранена локально ({reason}). В очереди: {OfflinePendingSalesStore.PendingCount}."
            : $"Оплата сохранена локально. В очереди: {OfflinePendingSalesStore.PendingCount}.";

        if (request.PrintReceipt && !printed)
            info += " Продажа сохранена, но чек не напечатан; используйте повторную печать.";

        return PosCheckoutResult.OfflineSaved(
            total,
            cartJsonSnapshot,
            info,
            request.PrintReceipt,
            printed);
    }

    private async Task<PosCheckoutResult> CompleteOnlineCheckoutAsync(
        PosCheckoutRequest request,
        string cartJsonSnapshot,
        double total,
        CancellationToken cancellationToken)
    {
        var cartId = _cart.CartId;
        if (string.IsNullOrWhiteSpace(cartId))
            return PosCheckoutResult.Failed("Корзина не привязана к серверу. Начните продажу заново.");

        var body = BuildCheckoutRequestBody(request.PaymentMethod, request.CashReceived, request.PrintReceipt);
        var checkoutIds = CartDisplayHelper.CollectCheckoutTargetIds(_cart.Root, cartId);

        PosLogger.Log(
            $"Checkout API: ids=[{string.Join(", ", checkoutIds)}], method={body.GetValueOrDefault("payment_method")}, " +
            $"cash={body.GetValueOrDefault("cash_received")}, shift={body.GetValueOrDefault("shift_id")}, " +
            $"cashbox={body.GetValueOrDefault("cashbox_id")}",
            "PAYMENT");

        JsonElement checkoutResponse;
        try
        {
            checkoutResponse = await _salesApi
                .PosCheckoutAsync(checkoutIds, body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiException ex) when (IsRecoverableCheckoutCartError(ex))
        {
            PosLogger.Log(
                $"Checkout cart is stale or empty; rebuilding server cart once. " +
                $"Status={ex.StatusCode}, old ids=[{string.Join(", ", checkoutIds)}]",
                "PAYMENT");

            await StagingCartService.MaterializeSnapshotOnServerAsync(
                    _salesApi,
                    _cart,
                    PosApp.PosCashboxId,
                    cancellationToken,
                    force: true)
                .ConfigureAwait(false);

            var recoveredCartId = _cart.CartId;
            if (string.IsNullOrWhiteSpace(recoveredCartId))
                throw;

            var recoveredIds = CartDisplayHelper.CollectCheckoutTargetIds(_cart.Root, recoveredCartId);
            PosLogger.Log(
                $"Checkout retry after cart rebuild: ids=[{string.Join(", ", recoveredIds)}]",
                "PAYMENT");
            checkoutResponse = await _salesApi
                .PosCheckoutAsync(recoveredIds, body, cancellationToken)
                .ConfigureAwait(false);
        }

        CheckoutResponseHelper.FormatSuccess(checkoutResponse);

        var saleId = CheckoutResponseHelper.TrySaleId(checkoutResponse) ?? cartId;
        PosLogger.Log($"Checkout API OK: saleId={saleId}", "PAYMENT");

        var cartSnapshot = _cart.Root.Clone();
        try
        {
            await PublishSaleFinalizedAsync(saleId, cartSnapshot).ConfigureAwait(false);
            PosLogger.Log("Checkout stock commit published", "PAYMENT");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Checkout already succeeded on the server. A local projection failure
            // must never invite the cashier to charge the same sale again.
            PosLogger.Log($"Checkout stock commit skipped after successful sale: {ex}", "STOCK");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await StockSyncService.RefreshSoldItemsStockAsync(cartSnapshot, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Background stock refresh failed: {ex}", "STOCK");
            }
        });

        try
        {
            PosApp.AuditDb.LogSale(saleId, total, request.PaymentMethod ?? "", PosApp.CurrentUserId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Checkout audit skipped after successful sale: {ex}", "AUDIT");
        }

        var printed = request.PrintReceipt && await TryPrintReceiptAsync(
            cartJsonSnapshot,
            request.PaymentMethod,
            request.CashReceived,
            checkoutResponse: checkoutResponse).ConfigureAwait(false);

        // Clear the paid cart immediately. If creating the next server cart fails,
        // the cashier still sees an empty receipt instead of a chargeable duplicate.
        _cart.ResetForNewReceipt();
        PosLogger.Log("Checkout restart sale session", "PAYMENT");
        var restartWarning = await RestartSaleSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(restartWarning))
            PosLogger.Log($"Checkout next sale warning: {restartWarning}", "PAYMENT");

        var info = restartWarning;
        if (request.PrintReceipt && !printed)
            info = string.Join(" ", new[]
            {
                restartWarning,
                "Оплата выполнена, но чек не напечатан; используйте повторную печать.",
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return PosCheckoutResult.Succeeded(
            total,
            cartJsonSnapshot,
            checkoutResponse,
            info,
            request.PrintReceipt,
            printed);
    }

    private static bool IsRecoverableCheckoutCartError(ApiException exception)
    {
        if (exception.StatusCode == 404)
            return true;

        if (exception.StatusCode != 400 || string.IsNullOrWhiteSpace(exception.Message))
            return false;

        return exception.Message.Contains("пуст", StringComparison.OrdinalIgnoreCase)
               && (exception.Message.Contains("корзин", StringComparison.OrdinalIgnoreCase)
                   || exception.Message.Contains("cart", StringComparison.OrdinalIgnoreCase));
    }

    private async Task PublishSaleFinalizedAsync(string saleId, JsonElement cartSnapshot)
    {
        var saleLines = CartDisplayHelper.EnumerateItems(cartSnapshot)
            .Select(it =>
            {
                var productId = CartDisplayHelper.TryProductId(it);
                var qty = CartDisplayHelper.LineQuantity(it);
                return string.IsNullOrEmpty(productId) ? null : new CartLineDto(productId, qty);
            })
            .Where(line => line != null)
            .Cast<CartLineDto>()
            .ToList();

        if (saleLines.Count > 0)
        {
            await _mediator.Publish(new SaleFinalizedNotification(saleId, saleLines), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> BuildCheckoutRequestBody(
        string paymentMethod,
        string cashReceived,
        bool printReceipt)
    {
        var body = new Dictionary<string, string>
        {
            ["payment_method"] = paymentMethod ?? "",
            ["print_receipt"] = printReceipt ? "true" : "false",
            ["cash_received"] = cashReceived ?? "",
        };

        if (!string.IsNullOrWhiteSpace(PosApp.PosCashboxId))
            body["cashbox_id"] = PosApp.PosCashboxId.Trim();

        var shiftId = PosApp.ActiveShiftId;
        if (!string.IsNullOrWhiteSpace(shiftId)
            && !shiftId.StartsWith("offline-", StringComparison.OrdinalIgnoreCase))
            body["shift_id"] = shiftId.Trim();

        return body;
    }

    private static void ApplyOfflineStockDecrement(string cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            foreach (var item in CartDisplayHelper.EnumerateItems(doc.RootElement))
            {
                var productId = CartDisplayHelper.TryProductId(item);
                if (string.IsNullOrEmpty(productId))
                    continue;

                var soldQty = CartDisplayHelper.LineQuantity(item);
                if (soldQty <= 0)
                    continue;

                var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                    string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
                if (tile == null)
                    continue;

                var next = Math.Max(0, tile.Quantity - soldQty);
                LocalProductRepository.Instance.UpdateStock(productId, next, tile.MustWeigh);
                StockSyncService.ApplyQuantityToTile(tile, next, tile.MustWeigh);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Offline stock decrement skipped: {ex}", "STOCK");
        }
    }

    private async Task<bool> TryPrintReceiptAsync(
        string cartJson,
        string? paymentMethod,
        string? cashReceived,
        string? offlineNote = null,
        JsonElement? checkoutResponse = null)
    {
        try
        {
            var printed = await _receiptPrinter.PrintReceiptAsync(new CartSnapshot
            {
                CartJson = cartJson,
                OfflineNote = offlineNote,
                PaymentMethodKey = paymentMethod,
                CashReceived = cashReceived,
                ReceiptText = checkoutResponse.HasValue
                    ? CheckoutResponseHelper.TryReceiptTextFromCheckout(checkoutResponse.Value)
                    : null,
            }, CancellationToken.None).ConfigureAwait(false);
            PosLogger.Log(printed ? "Receipt printed." : "Receipt printer returned failure.",
                printed ? "PRINTER" : "WARNING");
            return printed;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Print failed: {ex}", "PRINTER");
            return false;
        }
    }

    private static (string CartJson, double Total) BuildOfflineFallback(
        string cartJson,
        double originalTotal,
        Dictionary<string, string>? discountBody)
    {
        if (discountBody is null || discountBody.Count == 0)
            return (cartJson, originalTotal);

        using var fallbackCart = new CartService();
        fallbackCart.SetLocalOfflineCart(cartJson);
        var percent = discountBody.TryGetValue("order_discount_percent", out var pct) ? pct : null;
        var total = discountBody.TryGetValue("order_discount_total", out var sum) ? sum : null;
        ReceiptSnapshotCartEditor.PatchOrderDiscount(fallbackCart, percent, total);
        return (fallbackCart.GetRawText(), CartTotalsCalculator.Calculate(fallbackCart.Root).TotalDue);
    }
}
