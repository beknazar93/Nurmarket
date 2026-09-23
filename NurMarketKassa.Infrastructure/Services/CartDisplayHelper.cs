using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Разбор корзины и строк чека по полям API (как в main.py).</summary>
public static class CartDisplayHelper
{
    /// <summary>Товары, добавленные через весовой диалог каталога: API иногда не отдаёт is_weight/unit в строке.</summary>
    private static readonly ConcurrentDictionary<string, byte> WeighedProductDisplayHints = new(StringComparer.OrdinalIgnoreCase);

    public static void HintProductWeighedForDisplay(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        WeighedProductDisplayHints[productId.Trim()] = 1;
    }

    public static void ClearWeighedProductDisplayHints() => WeighedProductDisplayHints.Clear();
    public static string? TryCartId(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return null;
        if (!cart.TryGetProperty("id", out var id))
            return null;
        return JsonScalarToString(id);
    }

    /// <summary>
    /// Идентификаторы для POST checkout: cart id, sale id, cart_id из JSON (как в PosRefundService).
    /// </summary>
    public static IReadOnlyList<string> CollectCheckoutTargetIds(JsonElement cart, string? primaryCartId)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;
            id = id.Trim();
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                ids.Add(id);
        }

        Add(primaryCartId);
        if (cart.ValueKind != JsonValueKind.Object)
            return ids;

        Add(TryCartId(cart));
        Add(TryResolveCartIdFromSale(cart));
        if (cart.TryGetProperty("sale_id", out var saleIdProp))
            Add(JsonScalarToString(saleIdProp));

        return ids;
    }

    /// <summary>cart_id из ответа продажи (вложенный cart или поле cart_id), без подмены id продажи.</summary>
    public static string? TryResolveCartIdFromSale(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return null;

        if (sale.TryGetProperty("cart_id", out var cartIdProp))
        {
            var cartId = JsonScalarToString(cartIdProp);
            if (!string.IsNullOrEmpty(cartId))
                return cartId;
        }

        if (sale.TryGetProperty("cart", out var cart) && cart.ValueKind == JsonValueKind.Object)
        {
            var nestedId = TryCartId(cart);
            if (!string.IsNullOrEmpty(nestedId))
                return nestedId;
        }

        return null;
    }

    /// <summary>shift_id или shift.id из корзины POS.</summary>
    public static string? TryShiftIdFromCart(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return null;
        if (cart.TryGetProperty("shift_id", out var sid))
        {
            var s = JsonScalarToString(sid);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (!cart.TryGetProperty("shift", out var sh))
            return null;
        if (sh.ValueKind == JsonValueKind.Object && sh.TryGetProperty("id", out var id))
            return JsonScalarToString(id);
        return JsonScalarToString(sh);
    }

    /// <summary>Ответ POST …/shifts/open/.</summary>
    public static string? TryShiftIdFromOpenResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("id", out var id))
        {
            var s = JsonScalarToString(id);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (root.TryGetProperty("shift", out var sh) && sh.ValueKind == JsonValueKind.Object &&
            sh.TryGetProperty("id", out var sid))
            return JsonScalarToString(sid);

        return null;
    }

    public static IEnumerable<JsonElement> EnumerateItems(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            yield break;

        if (cart.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in it.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                    yield return el;
            }

            yield break;
        }

        if (cart.TryGetProperty("cart_items", out var ci) && ci.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in ci.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Object)
                    yield return el;
            }
        }
    }

    /// <summary>Строки готовой продажи/чека: items, cart_items, lines, sale_items (первый непустой массив).</summary>
    public static IEnumerable<JsonElement> EnumerateSaleLineItems(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var key in new[] { "items", "cart_items", "lines", "sale_items" })
        {
            if (!sale.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            var n = 0;
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;
                n++;
                yield return el;
            }

            if (n > 0)
                yield break;
        }
    }

    /// <summary>Позиция полностью возвращена — кнопку «Возврат» скрываем.</summary>
    public static bool LineLooksFullyReturned(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return true;

        if (TruthyBool(it, "is_returned") || TruthyBool(it, "fully_returned") || TruthyBool(it, "fully_refunded"))
            return true;

        if (it.TryGetProperty("refund_status", out var rs) && rs.ValueKind == JsonValueKind.String)
        {
            var s = rs.GetString()?.Trim().ToLowerInvariant() ?? "";
            if (s is "full" or "complete" or "done" or "returned")
                return true;
        }

        var q = TryDouble(it, "quantity");
        var qr = TryDouble(it, "quantity_refunded")
                 ?? TryDouble(it, "returned_quantity")
                 ?? TryDouble(it, "qty_returned")
                 ?? TryDouble(it, "refunded_quantity");
        if (q is > 0 && qr is > 0 && qr >= q - 1e-5)
            return true;

        return false;
    }

    public static string? FirstCashboxId(JsonElement data) =>
        TryFirstCashbox(data, out var id, out _) ? id : null;

    /// <summary>Первая касса в списке: id и отображаемое имя (не UUID).</summary>
    public static bool TryFirstCashbox(JsonElement data, out string? id, out string? displayName)
    {
        id = null;
        displayName = null;
        foreach (var el in UnwrapListElements(data))
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;
            var i = TryCashboxId(el);
            if (string.IsNullOrEmpty(i))
                continue;
            id = i;
            displayName = TryCashboxDisplayName(el) ?? i;
            return true;
        }

        return false;
    }

    public static string? TryCashboxDisplayName(JsonElement c)
    {
        if (c.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[]
                 {
                     "name", "title", "label", "display_name", "code", "cashbox_name", "number", "short_name",
                 })
        {
            if (c.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> UnwrapListElements(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
                yield return el;
            yield break;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("results", out var r) &&
            r.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in r.EnumerateArray())
                yield return el;
        }
    }

    private static string? TryCashboxId(JsonElement c)
    {
        foreach (var key in new[] { "id", "pk", "uuid" })
        {
            if (!c.TryGetProperty(key, out var v))
                continue;
            var s = JsonScalarToString(v);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        return null;
    }

    public static string ItemName(JsonElement it)
    {
        if (it.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            var n = NameFromProductDict(p);
            if (!string.IsNullOrEmpty(n))
                return n;
        }

        if (it.TryGetProperty("product_snapshot", out var snap) && snap.ValueKind == JsonValueKind.Object)
        {
            var n = NameFromProductDict(snap);
            if (!string.IsNullOrEmpty(n))
                return n;
        }

        foreach (var key in new[]
                 {
                     "product_name", "name", "title", "display_name", "label", "item_name", "description",
                 })
        {
            if (it.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        if (it.TryGetProperty("product_id", out var pid))
        {
            var ps = JsonScalarToString(pid);
            if (!string.IsNullOrEmpty(ps))
                return $"Товар #{ps}";
        }

        return "—";
    }

    public static string? TryBarcode(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "barcode", "bar_code", "product_barcode", "ean", "ean13" })
        {
            if (item.TryGetProperty(key, out var value) && JsonScalarToString(value) is { } barcode)
                return barcode;
        }

        foreach (var container in new[] { "product", "product_snapshot" })
        {
            if (!item.TryGetProperty(container, out var product) || product.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var key in new[] { "barcode", "bar_code", "product_barcode", "ean", "ean13" })
            {
                if (product.TryGetProperty(key, out var value) && JsonScalarToString(value) is { } barcode)
                    return barcode;
            }
        }

        return null;
    }

    private static string? NameFromProductDict(JsonElement p)
    {
        foreach (var key in new[] { "name", "title", "display_name", "label" })
        {
            if (p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }

        return null;
    }

    public static string QuantityPriceLine(JsonElement it)
    {
        var qty = TryDouble(it, "quantity") ?? 1;
        var up = TryDouble(it, "unit_price") ?? 0;
        return $"{FormatMoney(qty)} × {FormatMoney(up)} сом";
    }

    public static double UnitPrice(JsonElement it) => TryDouble(it, "unit_price") ?? 0;

    public static string LineTotal(JsonElement it)
    {
        foreach (var key in new[]
                 {
                     "line_total", "line_total_amount", "line_amount", "amount", "total", "sum",
                     "total_price", "line_total_display", "subtotal", "line_sum", "total_sum",
                 })
        {
            if (TryDouble(it, key) is { } v)
                return FormatMoney(v);
        }

        try
        {
            var q = TryDouble(it, "quantity") ?? 0;
            var up = TryDouble(it, "unit_price") ?? 0;
            var disc = TryDouble(it, "discount_total")
                       ?? TryDouble(it, "line_discount")
                       ?? TryDouble(it, "discount")
                       ?? 0;
            if (q > 0 && up >= 0)
                return FormatMoney(q * up - disc);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Cart line total fallback used: {ex.GetType().Name}", "DEBUG");
        }

        return FormatMoney(0);
    }

    public static double TotalDue(JsonElement cart) =>
        CartTotalsCalculator.Calculate(cart).TotalDue;

    private static JsonElement TryTotals(JsonElement cart) =>
        cart.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;

    public static string FormatMoney(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static double? TryDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return null;
        return JsonNumericReader.TryToDouble(v, out var d) ? d : null;
    }

    private static string? JsonScalarToString(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    public static string? TryItemId(JsonElement it) =>
        it.ValueKind == JsonValueKind.Object && it.TryGetProperty("id", out var id) ? JsonScalarToString(id) : null;

    /// <summary>Идентификатор строки продажи (id, line_id, sale_line_id, cart_item_id).</summary>
    public static string? TrySaleLineRecordId(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "id", "line_id", "sale_line_id", "cart_item_id" })
        {
            if (!it.TryGetProperty(key, out var v))
                continue;
            var s = JsonScalarToString(v);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        return null;
    }

    /// <summary>ID строки возврата по product_id в ответе продажи/корзины.</summary>
    public static string? TryRefundLineIdForProduct(JsonElement sale, string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;

        foreach (var line in EnumerateSaleLineItems(sale))
        {
            var pid = TryProductId(line);
            if (!string.Equals(pid, productId.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var lineId = TryRefundLineId(line);
            if (!string.IsNullOrEmpty(lineId))
                return lineId;
        }

        return null;
    }

    /// <summary>ID строки для возврата: cart_item_id / sale_line_id, не путать с product_id.</summary>
    public static string? TryRefundLineId(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return null;

        var productId = TryProductId(it);
        foreach (var key in new[]
                 {
                     "cart_item_id", "sale_line_id", "line_id", "item_id", "pos_line_id", "sale_item_id",
                 })
        {
            if (!it.TryGetProperty(key, out var v))
                continue;
            var s = JsonScalarToString(v);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (it.TryGetProperty("id", out var idEl))
        {
            var id = JsonScalarToString(idEl);
            if (!string.IsNullOrEmpty(id)
                && (string.IsNullOrEmpty(productId)
                    || !string.Equals(id, productId, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        return null;
    }

    /// <summary>Количество, доступное к возврату (с учётом уже возвращённого).</summary>
    public static double RefundableQuantity(JsonElement it)
    {
        var qty = LineQuantity(it);
        var returned = TryDouble(it, "quantity_refunded")
                       ?? TryDouble(it, "returned_quantity")
                       ?? TryDouble(it, "qty_returned")
                       ?? TryDouble(it, "refunded_quantity")
                       ?? 0;
        var left = qty - returned;
        return left > 1e-6 ? left : 0;
    }

    /// <summary>ID товара для POST add-item: product_id или product.id.</summary>
    public static string? TryProductId(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return null;
        if (it.TryGetProperty("product_id", out var pid))
        {
            var s = JsonScalarToString(pid);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        if (!it.TryGetProperty("product", out var p))
            return null;

        // В некоторых ответах API product — это сразу UUID строкой.
        if (p.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            var s = JsonScalarToString(p);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        if (p.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "id", "pk", "uuid" })
            {
                if (!p.TryGetProperty(key, out var id))
                    continue;
                var s = JsonScalarToString(id);
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
        }

        return null;
    }

    public static double LineQuantity(JsonElement it) => TryDouble(it, "quantity") ?? 1.0;

    /// <summary>Параметр discount_total для add-item, если в строке была скидка.</summary>
    public static string? OptionalDiscountTotalParam(JsonElement it)
    {
        var d = TryDouble(it, "discount_total")
                ?? TryDouble(it, "line_discount")
                ?? TryDouble(it, "discount")
                ?? 0;
        return d > 1e-6 ? FormatMoney(d) : null;
    }

    /// <summary>Шаг 0.05 (кг) или 1 (шт).</summary>
    public static bool LineMustWeigh(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return false;

        if (TruthyBool(it, "is_wait") || TruthyBool(it, "is_weigh") || TruthyBool(it, "is_weight"))
            return true;

        // Частые имена полей в ответах API для весовой строки
        if (TruthyBool(it, "is_weight_product") || TruthyBool(it, "sale_as_weight") ||
            TruthyBool(it, "sells_by_weight") || TruthyBool(it, "by_weight") ||
            TruthyBool(it, "weight_product") || TruthyBool(it, "is_kg"))
            return true;

        if (SaleModeImpliesWeight(it))
            return true;

        if (DictHasKgUnit(it))
            return true;

        if (it.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object && ProductMustWeigh(p))
            return true;

        if (it.TryGetProperty("product_snapshot", out var s) && s.ValueKind == JsonValueKind.Object && ProductMustWeigh(s))
            return true;

        var pid = TryProductId(it);
        if (!string.IsNullOrEmpty(pid) && WeighedProductDisplayHints.ContainsKey(pid))
            return true;

        return NameLooksWeighed(ItemName(it));
    }

    private static readonly string[] WeightNameHints =
    {
        "карто", "картоф", "помид", "томат", "огур", "лук", "морков", "капуст",
        "яблок", "банан", "апельсин", "груш", "перец", "свекл", "свёкл",
    };

    private static bool NameLooksWeighed(string name)
    {
        var raw = name.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw))
            return false;
        return WeightNameHints.Any(h => raw.Contains(h, StringComparison.Ordinal));
    }

    /// <summary>Как _product_must_weigh в main.py — для каталога и диалога взвешивания.</summary>
    public static bool ProductMustWeigh(JsonElement p) =>
        TruthyBool(p, "is_wait") || TruthyBool(p, "is_weigh") || TruthyBool(p, "is_weight") ||
        TruthyBool(p, "is_weight_product") ||
        TruthyBool(p, "sale_as_weight") || TruthyBool(p, "sells_by_weight") || DictHasKgUnit(p) ||
        ProductTypeImpliesWeight(p);

    private static bool DictHasKgUnit(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var key in new[]
                 {
                     "unit", "unit_display", "measure_unit", "sale_unit", "uom", "unit_code", "uom_code",
                     "sale_unit_code", "measurement_unit", "primary_unit", "default_unit", "base_unit",
                     "pricing_unit", "stock_unit", "weight_unit",
                 })
        {
            if (d.TryGetProperty(key, out var u) && UnitIsKg(u))
                return true;
        }

        return false;
    }

    /// <summary>Режим продажи строкой: weight / kg / вес и т.п.</summary>
    private static bool ProductTypeImpliesWeight(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var key in new[] { "type", "product_type", "kind", "sale_kind" })
        {
            if (!p.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var s = v.GetString()?.Trim().ToLowerInvariant() ?? "";
            if (s.Length == 0)
                continue;
            if (s.Contains("weight", StringComparison.Ordinal) || s.Contains("weigh", StringComparison.Ordinal))
                return true;
            if (s.Contains("вес", StringComparison.Ordinal) || s is "kg" or "weighable" or "weighted")
                return true;
        }

        return false;
    }

    private static bool SaleModeImpliesWeight(JsonElement it)
    {
        if (it.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var key in new[] { "sale_mode", "sale_type", "pricing_mode", "quantity_mode", "unit_mode" })
        {
            if (!it.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var s = v.GetString()?.Trim().ToLowerInvariant() ?? "";
            if (s.Length == 0)
                continue;
            if (s.Contains("weight", StringComparison.Ordinal) || s.Contains("weigh", StringComparison.Ordinal))
                return true;
            if (s.Contains("кг", StringComparison.Ordinal) || s is "kg" or "кg")
                return true;
            if (s.Contains("вес", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool UnitIsKg(JsonElement unit) =>
        unit.ValueKind switch
        {
            JsonValueKind.String => UnitStringIsKg(unit.GetString()),
            JsonValueKind.Object => UnitObjectLooksLikeKg(unit),
            _ => false,
        };

    private static bool UnitObjectLooksLikeKg(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var p in o.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String && UnitStringIsKg(p.Value.GetString()))
                return true;
            if (p.Value.ValueKind == JsonValueKind.Object && UnitObjectLooksLikeKg(p.Value))
                return true;
        }

        return false;
    }

    private static bool UnitStringIsKg(string? raw)
    {
        raw = (raw ?? "").Trim().ToLowerInvariant();
        if (raw.Length == 0)
            return false;
        var compact = raw.Replace(" ", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        // Граммы — не считаем «весовой позицией в кг» для подписи в корзине
        if (compact is "г" or "гр" or "gram" or "grams")
            return false;
        if (compact is "кг" or "kg" or "kг" or "kilogram" or "kilograms")
            return true;
        if (raw.Contains("килограм", StringComparison.Ordinal))
            return true;
        if (compact.EndsWith("кг", StringComparison.Ordinal) || raw.EndsWith(" kg", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static bool TruthyBool(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return false;
        if (v.ValueKind == JsonValueKind.True)
            return true;
        if (v.ValueKind == JsonValueKind.False)
            return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim().ToLowerInvariant();
            return s is "1" or "true" or "yes" or "on";
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim().ToLowerInvariant();
            return s is "1" or "true" or "yes" or "on";
        }

        if (v.ValueKind == JsonValueKind.Number)
            return JsonNumericReader.TryToDouble(v, out var d) && Math.Abs(d) > double.Epsilon;

        return false;
    }

    public static double WeightStepKg => (double)JsonNumericReader.WeightStepKg;

    public static string FormatWeightQuantity(double kg) => JsonNumericReader.FormatWeightDisplay(kg);
}
