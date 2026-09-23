using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>Локальная корзина без API — для офлайн-продаж.</summary>
public static class LocalCartService
{
    public static bool IsLocalCart(ICartService cart) => cart.IsLocalOffline;

    public static int GetLocalItemCount(ICartService cart) =>
        !cart.HasCart ? 0 : CartDisplayHelper.EnumerateItems(cart.Root).Count();

    public static bool HasItems(ICartService cart) => GetLocalItemCount(cart) > 0;

    public static void StartNewLocalCart(ICartService cart, string? shiftId = null)
    {
        var root = new JsonObject
        {
            ["id"] = "local-" + Guid.NewGuid().ToString("N"),
            ["items"] = new JsonArray(),
            ["shift_id"] = shiftId ?? PosApp.ActiveShiftId ?? "",
            ["is_local_offline"] = true,
        };
        ApplyRoot(cart, root);
    }

    public static void AddProduct(
        ICartService cart,
        CatalogProductTileVm product,
        string? quantity = null)
    {
        EnsureLocalCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray ?? new JsonArray();
        root["items"] = items;

        var qty = ParseQuantity(quantity, product.MustWeigh, defaultQty: 1);
        var unitPrice = ParsePrice(product.PriceLine);
        var productId = product.Id;

        var existing = FindLine(items, productId);
        if (existing != null && !product.MustWeigh)
        {
            var oldQty = JsonNumericReader.ToDouble(existing["quantity"]);
            existing["quantity"] = oldQty + qty;
            RecalcLine(existing);
        }
        else
        {
            var line = BuildLine(productId, product.Title, unitPrice, qty, product.MustWeigh);
            items.Add(line);
        }

        if (product.MustWeigh)
            CartDisplayHelper.HintProductWeighedForDisplay(productId);

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    public static bool TryAddByBarcode(ICartService cart, string barcode)
    {
        var tile = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Barcode?.Trim(), barcode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (tile == null)
            return false;

        AddProduct(cart, tile);
        return true;
    }

    public static void PatchLineDiscount(
        ICartService cart,
        string itemId,
        string? mode,
        string? value)
    {
        EnsureLocalCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray;
        var line = items?.FirstOrDefault(n => string.Equals(n?["id"]?.GetValue<string>(), itemId, StringComparison.Ordinal)) as JsonObject;
        if (line == null)
            return;

        line.Remove("discount_percent");
        line.Remove("discount_total");
        if (mode == null)
        {
            RecalcLine(line);
            RecalcCartTotals(root);
            ApplyRoot(cart, root);
            return;
        }

        if (mode == "percent" && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
            line["discount_percent"] = pct;
        else if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var sum))
            line["discount_total"] = sum;

        RecalcLine(line);
        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    public static void PatchOrderDiscount(ICartService cart, string? percent, string? total)
    {
        EnsureLocalCart(cart);
        var root = ParseRoot(cart);
        root.Remove("order_discount_percent");
        root.Remove("order_discount_total");

        if (!OrderDiscountHelper.IsEmptyOrZeroLike(percent)
            && double.TryParse(OrderDiscountHelper.NormalizeDecimal(percent!), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
            && p > 0)
        {
            root["order_discount_percent"] = p;
        }
        else if (!OrderDiscountHelper.IsEmptyOrZeroLike(total)
                 && double.TryParse(OrderDiscountHelper.NormalizeDecimal(total!), NumberStyles.Any, CultureInfo.InvariantCulture, out var t)
                 && t > 0)
        {
            root["order_discount_total"] = t;
        }

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    public static void UpdateLineQuantity(ICartService cart, string itemId, double qty)
    {
        EnsureLocalCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray;
        var line = items?.FirstOrDefault(n => string.Equals(n?["id"]?.GetValue<string>(), itemId, StringComparison.Ordinal)) as JsonObject;
        if (line == null)
            return;

        line["quantity"] = IsWeighedLine(line)
            ? JsonNumericReader.RoundWeight(qty)
            : Math.Round(qty, 0);
        RecalcLine(line);
        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    private static bool IsWeighedLine(JsonObject line)
    {
        if (line["is_weight"] is JsonValue w && w.TryGetValue<bool>(out var weighed))
            return weighed;

        using var doc = JsonDocument.Parse(line.ToJsonString());
        return CartDisplayHelper.LineMustWeigh(doc.RootElement);
    }

    public static void RemoveLine(ICartService cart, string itemId)
    {
        EnsureLocalCart(cart);
        var root = ParseRoot(cart);
        if (root["items"] is not JsonArray items)
            return;

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (string.Equals(items[i]?["id"]?.GetValue<string>(), itemId, StringComparison.Ordinal))
                items.RemoveAt(i);
        }

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    private static void EnsureLocalCart(ICartService cart)
    {
        if (!cart.HasCart)
            StartNewLocalCart(cart);
        else if (!cart.IsLocalOffline)
            throw new InvalidOperationException("Нельзя изменять серверную корзину через LocalCartService.");
    }

        private static JsonObject ParseRoot(ICartService cart) =>
        CartJsonHelper.ParseCartRoot(cart);

    private static void ApplyRoot(ICartService cart, JsonObject root)
    {
        cart.SetLocalOfflineCart(root.ToJsonString());
    }

    private static JsonObject? FindLine(JsonArray items, string productId)
    {
        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;
            var pid = obj["product_id"]?.GetValue<string>()
                      ?? obj["product"]?["id"]?.GetValue<string>();
            if (string.Equals(pid, productId, StringComparison.OrdinalIgnoreCase))
                return obj;
        }

        return null;
    }

    private static JsonObject BuildLine(string productId, string title, double unitPrice, double qty, bool mustWeigh)
    {
        var line = new JsonObject
        {
            ["id"] = "line-" + Guid.NewGuid().ToString("N"),
            ["product_id"] = productId,
            ["product_name"] = title,
            ["quantity"] = qty,
            ["unit_price"] = unitPrice,
            ["is_weight"] = mustWeigh,
            ["product"] = new JsonObject
            {
                ["id"] = productId,
                ["name"] = title,
                ["must_weigh"] = mustWeigh,
            },
        };
        RecalcLine(line);
        return line;
    }

    private static void RecalcLine(JsonObject line)
    {
        var qty = JsonNumericReader.ToDouble(line["quantity"]);
        var unitPrice = JsonNumericReader.ToDouble(line["unit_price"]);
        var gross = qty * unitPrice;
        double discount = 0;
        if (line.TryGetPropertyValue("discount_total", out var dt) && dt != null)
            discount = JsonNumericReader.ToDouble(dt);
        else if (line.TryGetPropertyValue("discount_percent", out var dp) && dp != null)
            discount = gross * JsonNumericReader.ToDouble(dp) / 100.0;

        line["line_total"] = Math.Max(0, gross - discount);
    }

    private static void RecalcCartTotals(JsonObject root)
    {
        using var doc = JsonDocument.Parse(root.ToJsonString());
        var totals = CartTotalsCalculator.Calculate(doc.RootElement);
        root["subtotal"] = totals.Subtotal;
        root["total"] = totals.TotalDue;
        root["total_due"] = totals.TotalDue;
    }

    private static double ParseQuantity(string? quantity, bool weighed, double defaultQty)
    {
        if (!string.IsNullOrWhiteSpace(quantity)
            && double.TryParse(quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var q)
            && q > 0)
            return q;
        return weighed ? 0 : defaultQty;
    }

    private static double ParsePrice(string priceLine)
    {
        if (string.IsNullOrWhiteSpace(priceLine))
            return 0;
        var digits = new string(priceLine.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray())
            .Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }
}
