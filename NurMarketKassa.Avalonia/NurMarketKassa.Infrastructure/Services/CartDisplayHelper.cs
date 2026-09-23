using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>Разбор корзины и строк чека по полям API (как в main.py).</summary>
public static class CartDisplayHelper
{
    /// <summary>Имя кассира из ответа сервера о продаже. Поля называются по-разному в
    /// зависимости от эндпоинта, поэтому перебираем известные: «cashier_display» —
    /// подтверждённое имя в ответе смен, остальные встречаются в ответах по продажам.
    /// Возвращает null, если ни одного нет — вызывающий подставит текущего кассира.</summary>
    public static string? TryCashierName(System.Text.Json.JsonElement sale)
    {
        if (sale.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "cashier_display", "cashier_name", "cashier", "user_name", "created_by_name" })
        {
            if (!sale.TryGetProperty(key, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text) && !System.Guid.TryParse(text, out _))
                return text;
        }

        return null;
    }

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

    /// <summary>Слова в названии кассы, по которым угадываем "основную" — сервер не отдаёт
    /// явного флага (см. комментарий ниже), но на практике компании часто буквально называют
    /// главную кассу так.</summary>
    private static readonly string[] MainCashboxNameHints =
        ["основн", "главн", "центральн", "main", "default", "primary"];

    /// <summary>Основная касса: среди активных (is_active=true) — та, чьё название похоже на
    /// "основная"/"главная" (см. <see cref="MainCashboxNameHints"/>), иначе первая активная по
    /// порядку ответа API; если активных нет вообще — первая в списке. Сервер не отдаёт явного
    /// флага "основная/главная", поэтому это лучшее доступное приближение — для точного выбора
    /// см. <see cref="ListCashboxes"/> и ручной выбор кассы в настройках.</summary>
    public static bool TryFirstCashbox(JsonElement data, out string? id, out string? displayName)
    {
        id = null;
        displayName = null;
        var chosen = PreferMainCashbox(ListCashboxes(data));
        if (chosen is not { } c)
            return false;

        id = c.Id;
        displayName = c.DisplayName;
        return true;
    }

    /// <summary>2026-09-17: тот же выбор "основной" кассы, что и <see cref="TryFirstCashbox"/>,
    /// но из уже разобранного и отфильтрованного списка — раньше автовыбор кассы при запуске
    /// кассы (MainWindow) делал свой отдельный "первая активная по порядку ответа сервера"
    /// без учёта имени "Основная"/"Главная" вообще, потому что дублировал эту логику вручную
    /// вместо вызова TryFirstCashbox — из-за этого правка сентября 2026-09-14/30 (добавление
    /// подсказок по имени) на реальный автовыбор при перезапуске кассы не влияла.</summary>
    public static (string Id, string DisplayName, bool IsActive)? PreferMainCashbox(
        IEnumerable<(string Id, string DisplayName, bool IsActive)> candidates)
    {
        (string Id, string DisplayName, bool IsActive)? firstActive = null;
        (string Id, string DisplayName, bool IsActive)? namedMain = null;
        (string Id, string DisplayName, bool IsActive)? fallback = null;

        foreach (var c in candidates)
        {
            fallback ??= c;
            if (!c.IsActive)
                continue;

            firstActive ??= c;
            if (namedMain is null && MainCashboxNameHints.Any(h => c.DisplayName.Contains(h, StringComparison.OrdinalIgnoreCase)))
                namedMain = c;
        }

        return namedMain ?? firstActive ?? fallback;
    }

    /// <summary>Все кассы из ответа API (id, отображаемое имя, активна ли) — для выбора
    /// вручную в настройках, когда автоопределение выбирает не ту кассу.</summary>
    public static List<(string Id, string DisplayName, bool IsActive)> ListCashboxes(JsonElement data)
    {
        var result = new List<(string, string, bool)>();
        foreach (var el in UnwrapListElements(data))
        {
            if (el.ValueKind != JsonValueKind.Object)
                continue;
            var i = TryCashboxId(el);
            if (string.IsNullOrEmpty(i))
                continue;

            var name = TryCashboxDisplayName(el) ?? i;
            var isActive = !el.TryGetProperty("is_active", out var activeEl) ||
                           activeEl.ValueKind != JsonValueKind.False;
            result.Add((i, name, isActive));
        }

        return result;
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

    /// <summary>2026-09-13, живой баг: повторная печать чека показывала "2,000 x 0,00 = 0,00"
    /// для всех строк — раньше здесь было только одно поле "unit_price" без запасных
    /// вариантов (в отличие от LineTotal ниже, у которого их 8). Для поштучных/пакетных строк
    /// сервер присылает sale_package_id ВМЕСТО unit_price (см. SalePackageId) — прямого поля
    /// цены там может не быть вовсе, хотя сумма строки (line_total/amount/...) есть. Теперь при
    /// отсутствии unit_price цена восстанавливается делением найденной суммы строки на
    /// количество — тем же способом, каким LineTotal ниже устойчив к разным именам полей.</summary>
    public static double UnitPrice(JsonElement it)
    {
        if (TryDouble(it, "unit_price") is { } direct)
            return direct;

        var qty = TryDouble(it, "quantity") ?? 0;
        if (qty <= 1e-9)
            return 0;

        foreach (var key in new[]
                 {
                     "line_total", "line_total_amount", "line_amount", "amount", "total", "sum",
                     "total_price", "line_total_display", "subtotal", "line_sum", "total_sum",
                 })
        {
            if (TryDouble(it, key) is { } total)
                return total / qty;
        }

        return 0;
    }

    /// <summary>ID упаковки для поштучной продажи (см. ProductPackageOption.Id) — если задан,
    /// StagingCartService при оформлении отправляет его серверу как "sale_package_id" ВМЕСТО
    /// unit_price, как это делает сайт: сервер сам подставляет цену пачки и не спотыкается
    /// о проверку "цена не ниже закупочной", которая иначе применяется при ручном оверрайде
    /// unit_price на базовом товаре.</summary>
    public static string? SalePackageId(JsonElement it) =>
        it.TryGetProperty("sale_package_id", out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

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
    /// <summary>Строка «Доп. услуга» без товара (2026-09-07, см. ReceiptSnapshotCartEditor.AddCustomItem):
    /// при переносе чека на сервер уходит запросом custom-item, а не add-item.</summary>
    public static bool IsCustomLine(JsonElement it) =>
        it.ValueKind == JsonValueKind.Object
        && it.TryGetProperty("is_custom", out var flag)
        && (flag.ValueKind == JsonValueKind.True
            || (flag.ValueKind == JsonValueKind.String
                && string.Equals(flag.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>2026-09-13, живой баг: строка продана ШТУКАМИ (sale_package_id — поштучно из
    /// упаковки, см. SalePackageId), а остаток товара в каталоге хранится в УПАКОВКАХ. Списание
    /// напрямую вычитало число проданных штук из остатка в упаковках — продажа 3 шт из упаковки
    /// по 12 шт съедала 3 "упаковки" (36 фантомных штук) вместо правильных 3/12 упаковки. Эта
    /// функция — единственное место конвертации количества строки в единицы остатка каталога;
    /// используется и при списании (PosCheckoutService/StockSyncService), и при подсчёте
    /// зарезервированного количества (StockAvailabilityService), чтобы не разойтись.</summary>
    public static double LineQuantityInStockUnits(JsonElement it, CatalogProductTileVm? tile)
    {
        var qty = LineQuantity(it);
        var packageId = SalePackageId(it);
        if (string.IsNullOrEmpty(packageId) || tile?.PieceOption is not { } piece)
            return qty;

        if (!string.Equals(piece.Id, packageId, StringComparison.OrdinalIgnoreCase) || piece.QuantityInPackage <= 0)
            return qty;

        return qty / piece.QuantityInPackage;
    }

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

    /// <summary>
    /// Подсказка должна совпасть с началом отдельного слова и допускать только короткое
    /// словоизменительное окончание («яблок+и», «морков+ь»). Иначе «Сок апельсиновый»
    /// или «Лукошко» ошибочно считались бы весовыми из-за случайной подстроки.
    /// </summary>
    private static readonly Regex[] WeightNameHintPatterns = WeightNameHints
        .Select(h => new Regex(
            $@"(?<![\p{{L}}\p{{Nd}}]){Regex.Escape(h)}\p{{L}}{{0,3}}(?![\p{{L}}])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant))
        .ToArray();

    private static bool NameLooksWeighed(string name)
    {
        var raw = name.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw))
            return false;
        return WeightNameHintPatterns.Any(p => p.IsMatch(raw));
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
