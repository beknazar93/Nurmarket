using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// Локальный черновик активного чека после откладывания: сервер не держит «вторую» корзину,
/// поэтому новый чек живёт в памяти до оплаты.
/// </summary>
public static class StagingCartService
{
    public static void StartEmpty(ICartService cart)
    {
        var root = new JsonObject
        {
            ["items"] = new JsonArray(),
            ["is_staging"] = true,
        };
        using var doc = JsonDocument.Parse(root.ToJsonString());
        cart.SetCart(doc.RootElement);
    }

    /// <summary>
    /// Перед оплатой: sales/start → очистка серверной корзины → перенос позиций из снимка.
    /// </summary>
    public static async Task MaterializeSnapshotOnServerAsync(
        ISalesApiService api,
        ICartService cart,
        string? cashboxId,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        if (!cart.HasCart)
            throw new ApiException("Нет данных чека для оплаты.", 400);

        var snapshotJson = cart.Root.GetRawText();
        var wasStaging = cart.IsStaging;

        if (!force && !wasStaging && cart.CanRefresh)
            return;

        cart.Clear();

        var serverCart = await api.PosSalesStartAsync(
            string.IsNullOrWhiteSpace(cashboxId) ? null : cashboxId,
            cancellationToken).ConfigureAwait(false);
        cart.SetCart(serverCart);

        if (!cart.CanRefresh || string.IsNullOrEmpty(cart.CartId))
            throw new ApiException("Не удалось получить серверную корзину для оплаты.", 409);

        var cartId = cart.CartId!;
        await CartSaleSessionHelper.EnsureServerCartEmptyAsync(api, cart, cancellationToken).ConfigureAwait(false);
        DeferredCartsStore.ReleaseServerCartId(cartId);

        await PushItemsFromSnapshotAsync(api, cartId, snapshotJson, cancellationToken).ConfigureAwait(false);
        await ApplyOrderDiscountFromSnapshotAsync(api, cartId, snapshotJson, cancellationToken).ConfigureAwait(false);

        cart.SetCart(await api.PosCartGetAsync(cartId, cancellationToken).ConfigureAwait(false));
        PosLogger.Log($"STAGING: снимок перенесён на сервер cartId={cartId}", "RECEIPT");
    }

    private static async Task PushItemsFromSnapshotAsync(
        ISalesApiService api,
        string cartId,
        string snapshotJson,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);
        foreach (var it in CartDisplayHelper.EnumerateItems(doc.RootElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var productId = CartDisplayHelper.TryProductId(it);
            if (string.IsNullOrEmpty(productId))
                continue;

            var qty = CartDisplayHelper.LineQuantity(it);
            var qtyStr = CartDisplayHelper.LineMustWeigh(it)
                ? qty.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')
                : Math.Round(qty, 0).ToString(CultureInfo.InvariantCulture);
            var unitPrice = CartDisplayHelper.FormatMoney(CartDisplayHelper.UnitPrice(it));
            var disc = CartDisplayHelper.OptionalDiscountTotalParam(it);

            await api.PosAddItemAsync(cartId, productId, qtyStr, unitPrice, disc, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ApplyOrderDiscountFromSnapshotAsync(
        ISalesApiService api,
        string cartId,
        string snapshotJson,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson);
        var root = doc.RootElement;
        var body = new Dictionary<string, string>();

        if (root.TryGetProperty("order_discount_percent", out var pct))
        {
            var value = pct.ValueKind == JsonValueKind.Number
                ? pct.GetDouble().ToString(CultureInfo.InvariantCulture)
                : pct.GetString();
            if (!OrderDiscountHelper.IsEmptyOrZeroLike(value))
                body["order_discount_percent"] = OrderDiscountHelper.NormalizeDecimal(value!);
        }

        if (root.TryGetProperty("order_discount_total", out var total))
        {
            var value = total.ValueKind == JsonValueKind.Number
                ? total.GetDouble().ToString(CultureInfo.InvariantCulture)
                : total.GetString();
            if (!OrderDiscountHelper.IsEmptyOrZeroLike(value))
                body["order_discount_total"] = OrderDiscountHelper.NormalizeDecimal(value!);
        }

        if (body.Count == 0)
            return;

        await api.PosCartPatchAsync(cartId, body, cancellationToken).ConfigureAwait(false);
    }
}
