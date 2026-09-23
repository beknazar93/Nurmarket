using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Реализация продаж/корзин/возвратов поверх настроенного транспорта <see cref="NurMarketApiClient"/>.
/// </summary>
public sealed class SalesApiService : ISalesApiService
{
    private readonly NurMarketApiClient _client;
    private string? _activeCartId;

    public SalesApiService(NurMarketApiClient client) => _client = client;

    public string? ActiveCartId => _activeCartId;

    public void SetActiveCartId(string? cartId) =>
        _activeCartId = string.IsNullOrWhiteSpace(cartId) ? null : cartId.Trim();

    public Task<JsonElement> PosSalesStartAsync(string? cashboxId = null, CancellationToken ct = default)
    {
        Dictionary<string, string>? body = null;
        if (!string.IsNullOrWhiteSpace(cashboxId))
            body = new Dictionary<string, string> { ["cashbox_id"] = cashboxId.Trim() };
        return PosSalesStartAsync(body, ct);
    }

    public Task<JsonElement> PosSalesStartAsync(IReadOnlyDictionary<string, string>? body, CancellationToken ct = default)
    {
        var payload = body == null
            ? new Dictionary<string, string>()
            : body.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
        return _client.RequestAsync(HttpMethod.Post, "api/main/pos/sales/start/", payload, null, ct);
    }

    public Task<JsonElement> PosCartGetAsync(string cartId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        return _client.RequestAsync(HttpMethod.Get, $"api/main/pos/carts/{id}/", null, null, ct);
    }

    public Task<JsonElement> PosScanAsync(string cartId, string barcode, string? quantity = null, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var body = new Dictionary<string, string> { ["barcode"] = barcode.Trim() };
        if (!string.IsNullOrEmpty(quantity))
            body["quantity"] = quantity;
        return _client.RequestAsync(HttpMethod.Post, $"api/main/pos/sales/{id}/scan/", body, null, ct, TimeSpan.FromSeconds(22));
    }

    public Task<JsonElement> PosCartItemPatchAsync(
        string cartId,
        string itemId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        var i = Uri.EscapeDataString(itemId.Trim());
        return _client.RequestAsync(HttpMethod.Patch, $"api/main/pos/carts/{c}/items/{i}/", body, null, ct);
    }

    public Task<JsonElement> PosCartItemDeleteAsync(string cartId, string itemId, CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        var i = Uri.EscapeDataString(itemId.Trim());
        return _client.RequestAsync(HttpMethod.Delete, $"api/main/pos/carts/{c}/items/{i}/", null, null, ct);
    }

    public Task<JsonElement> PosCheckoutAsync(
        string cartId,
        Dictionary<string, string> body,
        CancellationToken ct = default) =>
        PosCheckoutAsync(new[] { cartId.Trim() }, body, ct);

    public async Task<JsonElement> PosCheckoutAsync(
        IReadOnlyList<string> targetIds,
        Dictionary<string, string> body,
        CancellationToken ct = default)
    {
        if (targetIds == null || targetIds.Count == 0)
            throw new ApiException("Checkout: не указан идентификатор корзины или продажи.", 400);

        var ids = targetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            throw new ApiException("Checkout: не указан идентификатор корзины или продажи.", 400);

        var timeout = TimeSpan.FromSeconds(90);
        ApiException? last404 = null;

        foreach (var rawId in ids)
        {
            var id = Uri.EscapeDataString(rawId);
            var paths = new[]
            {
                $"api/main/pos/sales/{id}/checkout/",
                $"api/main/pos/carts/{id}/checkout/",
            };

            foreach (var path in paths)
            {
                try
                {
                    return await _client.RequestAsync(HttpMethod.Post, path, body, null, ct, timeout)
                        .ConfigureAwait(false);
                }
                catch (ApiException e)
                {
                    if (e.StatusCode == 404)
                    {
                        last404 = e;
                        continue;
                    }

                    var pm = body.GetValueOrDefault("payment_method") ?? "";
                    if (e.StatusCode == 400
                        && !body.ContainsKey("cash_received")
                        && !string.Equals(pm, "cash", StringComparison.OrdinalIgnoreCase))
                    {
                        var retry = new Dictionary<string, string>(body) { ["cash_received"] = "0.00" };
                        try
                        {
                            return await _client.RequestAsync(HttpMethod.Post, path, retry, null, ct, timeout)
                                .ConfigureAwait(false);
                        }
                        catch (ApiException)
                        {
                            throw e;
                        }
                    }

                    throw;
                }
            }
        }

        if (last404 != null)
            throw last404;
        throw new ApiException("Checkout: пустой список путей", 500);
    }

    public Task<JsonElement> PosSaleReceiptAsync(string saleId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        return _client.RequestAsync(HttpMethod.Get, $"api/main/pos/sales/{id}/receipt/", null, null, ct);
    }

    public Task<JsonElement> PosCartPatchAsync(string cartId, IReadOnlyDictionary<string, string> body, CancellationToken ct = default)
    {
        var c = Uri.EscapeDataString(cartId.Trim());
        return _client.RequestAsync(HttpMethod.Patch, $"api/main/pos/carts/{c}/", body, null, ct);
    }

    public Task<JsonElement> PosAddItemAsync(
        string cartId,
        string productId,
        string? quantity = null,
        string? unitPrice = null,
        string? discountTotal = null,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var body = new Dictionary<string, string> { ["product_id"] = productId.Trim() };
        if (!string.IsNullOrWhiteSpace(quantity))
            body["quantity"] = quantity.Trim();
        if (!string.IsNullOrWhiteSpace(unitPrice))
            body["unit_price"] = unitPrice.Trim();
        if (!string.IsNullOrWhiteSpace(discountTotal))
            body["discount_total"] = discountTotal.Trim();
        return _client.RequestAsync(
            HttpMethod.Post,
            $"api/main/pos/sales/{id}/add-item/",
            body,
            null,
            ct,
            TimeSpan.FromSeconds(28));
    }

    public Task<JsonElement> PosAddItemRawAsync(
        string cartId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(cartId.Trim());
        var payload = body.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal);
        return _client.RequestAsync(
            HttpMethod.Post,
            $"api/main/pos/sales/{id}/add-item/",
            payload,
            null,
            ct,
            TimeSpan.FromSeconds(28));
    }

    public async Task<List<JsonElement>> PosSalesListAsync(
        int page,
        int pageSize,
        string? cashboxId = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 80);
        var pageStr = page.ToString(CultureInfo.InvariantCulture);
        var sizeStr = pageSize.ToString(CultureInfo.InvariantCulture);

        var queries = new List<Dictionary<string, string>>
        {
            new() { ["page"] = pageStr, ["page_size"] = sizeStr },
            new() { ["page"] = pageStr, ["limit"] = sizeStr },
            new() { ["page"] = pageStr, ["page_size"] = sizeStr, ["ordering"] = "-created_at" },
            new() { ["page"] = pageStr, ["page_size"] = sizeStr, ["ordering"] = "-id" },
        };

        if (!string.IsNullOrWhiteSpace(cashboxId))
        {
            var cb = cashboxId.Trim();
            queries.Add(new Dictionary<string, string> { ["page"] = pageStr, ["page_size"] = sizeStr, ["cashbox_id"] = cb });
            queries.Add(new Dictionary<string, string> { ["page"] = pageStr, ["limit"] = sizeStr, ["cashbox_id"] = cb });
        }

        var paths = new[] { "api/main/pos/sales/", "api/main/pos/sales/list/", "api/main/pos/sale/list/" };

        ApiException? last = null;
        var sawEmptySuccess = false;
        foreach (var path in paths)
        {
            foreach (var qs in queries)
            {
                try
                {
                    var data = await _client.RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                    var root = UnwrapListRootElement(data);
                    var list = NurMarketApiClient.UnwrapList(root);
                    if (list.Count > 0)
                        return list;
                    sawEmptySuccess = true;
                }
                catch (ApiException e)
                {
                    last = e;
                    if (e.StatusCode is 404 or 405 or 410)
                        break;
                    if (e.StatusCode == 400)
                        continue;
                    throw;
                }
            }
        }

        if (sawEmptySuccess)
            return new List<JsonElement>();
        if (last != null)
            throw last;
        return new List<JsonElement>();
    }

    public async Task<JsonElement> PosSaleGetAsync(string saleId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи (UUID или id из чека).", 400);

        var paths = new[]
        {
            $"api/main/pos/sales/{id}/",
        };

        ApiException? last = null;
        foreach (var path in paths)
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                return UnwrapDataObject(data);
            }
            catch (ApiException e)
            {
                last = e;
                if (e.StatusCode is 404 or 405 or 410)
                    continue;
                throw;
            }
        }

        if (last != null)
            throw last;
        throw new ApiException("Чек не найден.", 404);
    }

    public Task<JsonElement> PosCartItemDeletionsGetAsync(CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Get, "api/main/pos/cart-item-deletions/get/", null, null, ct);

    public async Task<bool> TryPosCartItemDeletionReturnAsync(
        string saleId,
        string? cartId,
        PosRefundLineRequest line,
        string? reason,
        CancellationToken ct = default)
    {
        var n = (reason ?? "").Trim();
        var qty = FormatRefundQty(line.Quantity);
        var itemId = line.LineId.Trim();
        var productId = (line.ProductId ?? "").Trim();
        var sid = saleId.Trim();

        if (itemId.Length == 0)
            return false;
        if (sid.Length == 0 && string.IsNullOrEmpty(cartId))
            return false;

        var paramSets = new List<Dictionary<string, string>>();

        void AddParams(Action<Dictionary<string, string>> fill)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            fill(d);
            paramSets.Add(d);
        }

        AddParams(d =>
        {
            d["sale_id"] = sid;
            d["item_id"] = itemId;
            d["quantity"] = qty;
            if (!string.IsNullOrEmpty(n))
            {
                d["reason"] = n;
                d["refund_reason"] = n;
            }

            if (!string.IsNullOrEmpty(productId))
                d["product_id"] = productId;
            if (!string.IsNullOrEmpty(cartId))
                d["cart_id"] = cartId!;
        });

        if (!string.IsNullOrEmpty(sid))
        {
            AddParams(d =>
            {
                d["original_sale_id"] = sid;
                d["cart_item_id"] = itemId;
                d["quantity"] = qty;
                if (!string.IsNullOrEmpty(n))
                {
                    d["reason"] = n;
                    d["refund_reason"] = n;
                }

                if (!string.IsNullOrEmpty(productId))
                    d["product_id"] = productId;
            });
        }

        if (!string.IsNullOrEmpty(cartId))
        {
            AddParams(d =>
            {
                d["cart_id"] = cartId!;
                d["item_id"] = itemId;
                d["quantity"] = qty;
                if (!string.IsNullOrEmpty(n))
                    d["reason"] = n;
            });
        }

        foreach (var query in paramSets)
        {
            try
            {
                await _client.RequestAsync(
                        HttpMethod.Get,
                        "api/main/pos/cart-item-deletions/get/",
                        null,
                        query,
                        ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405 or 410)
            {
                /* пробуем другой набор полей */
            }
        }

        foreach (var body in paramSets)
        {
            try
            {
                await _client.RequestAsync(
                        HttpMethod.Post,
                        "api/main/pos/cart-item-deletions/get/",
                        body,
                        null,
                        ct)
                    .ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405 or 410)
            {
                /* пробуем другой набор полей */
            }
        }

        return false;
    }

    private async Task TryRegisterCartItemDeletionAsync(
        string cartId,
        string itemId,
        string reason,
        double returnQty,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        var line = new PosRefundLineRequest
        {
            LineId = itemId,
            Title = itemId,
            Quantity = returnQty,
        };

        await TryPosCartItemDeletionReturnAsync(string.Empty, cartId, line, reason, ct).ConfigureAwait(false);
    }

    public Task<JsonElement> PosSalePatchAsync(
        string saleId,
        IReadOnlyDictionary<string, string> body,
        CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        return _client.RequestAsync(HttpMethod.Patch, $"api/main/pos/sales/{id}/", body, null, ct);
    }

    public Task<JsonElement> PosSaleDeleteAsync(string saleId, CancellationToken ct = default)
    {
        var id = Uri.EscapeDataString(saleId.Trim());
        return _client.RequestAsync(HttpMethod.Delete, $"api/main/pos/sales/{id}/", null, null, ct);
    }

    public async Task<JsonElement> PosReturnCartLineAsync(
        string cartId,
        PosRefundLineRequest line,
        string? reason,
        CancellationToken ct = default)
    {
        var c = cartId.Trim();
        var itemId = line.LineId.Trim();
        if (c.Length == 0 || itemId.Length == 0)
            throw new ApiException("Укажите корзину и строку чека.", 400);

        var n = (reason ?? "").Trim();
        var returnQty = line.Quantity;
        var originalQty = line.OriginalQuantity > 0 ? line.OriginalQuantity : returnQty;
        var isPartial = returnQty > 0 && returnQty < originalQty - 1e-5;
        var remainingQty = originalQty - returnQty;

        await TryRegisterCartItemDeletionAsync(c, itemId, n, returnQty, ct).ConfigureAwait(false);

        if (isPartial)
        {
            var qtyStr = FormatRefundQty(remainingQty);
            var patchBodies = new List<Dictionary<string, string>>
            {
                new() { ["quantity"] = qtyStr, ["reason"] = n, ["refund_reason"] = n },
                new() { ["quantity"] = qtyStr },
            };
            ApiException? patchError = null;
            foreach (var body in patchBodies.Where(b => b.Count > 0))
            {
                try
                {
                    return await PosCartItemPatchAsync(c, itemId, body, ct).ConfigureAwait(false);
                }
                catch (ApiException ex)
                {
                    patchError = ex;
                    if (ex.StatusCode == 400)
                        continue;
                    throw;
                }
            }

            if (patchError != null)
                throw patchError;
        }

        if (!string.IsNullOrEmpty(n))
        {
            try
            {
                await PosCartItemPatchAsync(
                        c,
                        itemId,
                        new Dictionary<string, string> { ["reason"] = n, ["refund_reason"] = n, ["note"] = n },
                        ct)
                    .ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405)
            {
                /* причина необязательна для DELETE */
            }
        }

        try
        {
            return await PosCartItemDeleteAsync(c, itemId, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405 or 409)
        {
            return await TryMarkCartLineReturnedAsync(c, itemId, returnQty, n, ct).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> TryMarkCartLineReturnedAsync(
        string cartId,
        string itemId,
        double returnQty,
        string reason,
        CancellationToken ct)
    {
        var qty = FormatRefundQty(returnQty);
        var patchBodies = new List<Dictionary<string, string>>
        {
            new() { ["quantity"] = "0", ["reason"] = reason, ["refund_reason"] = reason },
            new() { ["returned_quantity"] = qty, ["reason"] = reason, ["refund_reason"] = reason },
            new() { ["quantity_refunded"] = qty, ["reason"] = reason },
            new() { ["refunded_quantity"] = qty, ["reason"] = reason },
            new() { ["is_returned"] = "true", ["refund_reason"] = reason },
            new() { ["quantity"] = "0" },
        };

        ApiException? last = null;
        foreach (var body in patchBodies.Where(b => b.Count > 0))
        {
            try
            {
                return await PosCartItemPatchAsync(cartId, itemId, body, ct).ConfigureAwait(false);
            }
            catch (ApiException ex)
            {
                last = ex;
                if (ex.StatusCode == 400)
                    continue;
                throw;
            }
        }

        throw last ?? new ApiException("Не удалось оформить возврат позиции.", 502);
    }

    public async Task<JsonElement> PosReturnWholeSaleAsync(string saleId, string? reason, CancellationToken ct = default)
    {
        var id = saleId.Trim();
        if (id.Length == 0)
            throw new ApiException("Укажите номер продажи.", 400);

        var n = (reason ?? "").Trim();
        if (!string.IsNullOrEmpty(n))
        {
            var patchBodies = new[]
            {
                new Dictionary<string, string> { ["refund_reason"] = n, ["reason"] = n, ["note"] = n },
                new Dictionary<string, string> { ["reason"] = n },
                new Dictionary<string, string> { ["status"] = "refunded", ["reason"] = n },
                new Dictionary<string, string> { ["is_refund"] = "true", ["refund_reason"] = n },
            };

            foreach (var body in patchBodies)
            {
                try
                {
                    await PosSalePatchAsync(id, body, ct).ConfigureAwait(false);
                    break;
                }
                catch (ApiException ex) when (ex.StatusCode is 400 or 404 or 405)
                {
                    /* PATCH необязателен */
                }
            }
        }

        return await PosSaleDeleteAsync(id, ct).ConfigureAwait(false);
    }

    private static JsonElement UnwrapListRootElement(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner))
        {
            if (inner.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return inner.Clone();
        }

        return data.Clone();
    }

    private static JsonElement UnwrapDataObject(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
            return inner.Clone();
        return data.Clone();
    }

    private static string FormatRefundQty(double q)
    {
        var s = q.ToString("0.####", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return string.IsNullOrEmpty(s) ? "0" : s;
    }
}
