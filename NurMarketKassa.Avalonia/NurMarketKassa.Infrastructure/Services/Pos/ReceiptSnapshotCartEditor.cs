using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

public static class ReceiptSnapshotCartEditor
{
    public static void AddProduct(ICartService cart, CatalogProductTileVm product, double qty) =>
        AddProduct(cart, product, qty, unitPriceOverride: null);

    /// <summary>Доп. штрихкод варианта товара (2026-09-21): nameOverride — комбинированное
    /// название строки («Название товара + название варианта»), см. BasketPanelViewModel.ResolveVariantLineName.</summary>
    public static void AddProduct(ICartService cart, CatalogProductTileVm product, double qty, string? nameOverride) =>
        AddProduct(cart, product, qty, unitPriceOverride: null, salePackageId: null, nameOverride: nameOverride);

    public static void AddProduct(ICartService cart, CatalogProductTileVm product, double qty, double? unitPriceOverride) =>
        AddProduct(cart, product, qty, unitPriceOverride, salePackageId: null);

    public static void AddProduct(
        ICartService cart, CatalogProductTileVm product, double qty, double? unitPriceOverride, string? salePackageId) =>
        AddProduct(cart, product, qty, unitPriceOverride, salePackageId, nameOverride: null);

    public static void AddProduct(
        ICartService cart, CatalogProductTileVm product, double qty, double? unitPriceOverride, string? salePackageId,
        string? nameOverride)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!double.IsFinite(qty) || qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qty), "Количество должно быть больше нуля.");

        EnsureCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray ?? new JsonArray();
        root["items"] = items;

        // Восстанавливаем product_id, если он отсутствует или повреждён
        RepairMissingProductIds(items);

        var unitPrice = unitPriceOverride ?? ParsePrice(product.PriceLine);
        var productId = product.Id;
        var title = string.IsNullOrWhiteSpace(nameOverride) ? product.Title : nameOverride;
        var variantName = string.IsNullOrWhiteSpace(nameOverride) ? null : nameOverride.Trim();

        // Сливаем только со строкой той же цены за единицу — иначе, например, продажа целой
        // упаковкой и поштучная продажа того же товара (или наоборот, в любом порядке
        // добавления) слились бы в одну строку по чужой цене и исказили сумму чека.
        //
        // 2026-09-29, жалоба магазина «при сканировании доп. штрихкода резко переходит на
        // основное»: скан доп. штрихкода варианта («Asu Клубничный») сливался в уже пробитую
        // строку основного товара («Asu» — количество +1, варианта в чеке нет), и наоборот.
        // Теперь вариант — своя строка: сливаются только строки того же варианта (у основного
        // товара варианта нет). Исключения — весовой товар и товар с акцией NurCRM: сервер
        // хранит такой товар одной строкой и считает акцию от её суммы, две строки дали бы
        // другую скидку, чем у сервера, — они сливаются, как раньше. На сервер строки одного
        // товара уходят одной позицией (QuickCheckoutBody / StagingCartService) — сервер
        // вариантов не различает (проверено на тестовом аккаунте: скан доп. штрихкода даёт строку
        // с названием основного товара, своё название строки add-item и PATCH не принимают).
        var existing = FindLineByProductIdAndPrice(items, productId, unitPrice, variantName);
        if (existing == null && (product.MustWeigh || PromotionRules.ApplyToLine(new JsonObject(), productId)))
            existing = FindLineByProductIdAndPrice(items, productId, unitPrice);

        if (existing != null)
        {
            // Если товар уже есть в чеке — просто увеличиваем количество, НЕ проверяя MustWeigh
            var oldQty = JsonNumericReader.ToDouble(existing["quantity"]);
            existing["quantity"] = oldQty + qty;

            // Исправляем потенциальный баг, когда is_weight ошибочно стало true после возврата в чек
            if (existing["is_weight"]?.GetValue<bool>() != product.MustWeigh)
            {
                existing["is_weight"] = product.MustWeigh;
                if (existing["product"] is JsonObject productObj)
                {
                    productObj["must_weigh"] = product.MustWeigh;
                }
            }

            // 2026-09-28: акции товара могли смениться с момента первого добавления.
            PromotionRules.ApplyToLine(existing, productId);
            RecalcLine(existing);
        }
        else
        {
            var line = BuildLine(productId, title, product.Barcode, unitPrice, qty, product.MustWeigh, salePackageId);
            // 2026-09-29: метка варианта — по ней следующий скан того же варианта сливается в эту
            // строку, а скан основного штрихкода или другого варианта — нет.
            if (variantName != null)
                line[VariantNameField] = variantName;
            items.Add(line);
        }

        if (product.MustWeigh)
            CartDisplayHelper.HintProductWeighedForDisplay(productId);

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    /// <summary>«Доп. услуга» (2026-09-07): строка чека без товара — product_id/product отсутствуют,
    /// is_custom = true (по нему StagingCartService при оплате отправляет её на сервер запросом
    /// custom-item, а не add-item). Каждая услуга — отдельная строка, с другими не сливается.
    /// Отрицательная unitPrice = «Расход» (вычитается из чека, см. RecalcLine). Имя кладём и в
    /// product_name, и в name — оба читает CartDisplayHelper.ItemName.</summary>
    public static void AddCustomItem(ICartService cart, string name, double unitPrice, double qty)
    {
        var title = (name ?? "").Trim();
        if (title.Length == 0)
            throw new ArgumentException("Не указано название услуги.", nameof(name));
        if (!double.IsFinite(qty) || qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qty), "Количество должно быть больше нуля.");
        if (!double.IsFinite(unitPrice) || Math.Abs(unitPrice) < 0.005)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Сумма услуги должна быть отлична от нуля.");

        EnsureCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray ?? new JsonArray();
        root["items"] = items;
        RepairMissingProductIds(items);

        var line = new JsonObject
        {
            ["id"] = "line-" + Guid.NewGuid().ToString("N"),
            ["product_name"] = title,
            ["name"] = title,
            ["quantity"] = qty,
            ["unit_price"] = unitPrice,
            ["is_weight"] = false,
            ["is_custom"] = true,
        };
        RecalcLine(line);
        items.Add(line);

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    private static bool PriceMatches(JsonObject existingLine, double unitPrice) =>
        Math.Abs(JsonNumericReader.ToDouble(existingLine["unit_price"]) - unitPrice) < 0.005;

    public static void UpdateLineQuantity(ICartService cart, string itemId, double qty)
    {
        if (!double.IsFinite(qty) || qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qty), "Количество должно быть больше нуля.");

        EnsureCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray ?? new JsonArray();
        RepairMissingProductIds(items);
        root["items"] = items;

        var line = FindLineByItemId(items, itemId);
        if (line == null)
            throw new InvalidOperationException("CartItem not found in this cart.");

        line["quantity"] = IsWeighedLine(line)
            ? JsonNumericReader.RoundWeight(qty)
            : Math.Round(qty, 0);
        // Скидка суммой была выставлена под прежний gross: после смены количества
        // её нужно переклампить, иначе «лишняя» скидка утечёт на другие строки чека.
        ClampLineDiscountToGross(line);
        RecalcLine(line);
        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    private static void ClampLineDiscountToGross(JsonObject line)
    {
        if (!line.TryGetPropertyValue("discount_total", out var dt) || dt == null)
            return;

        var gross = JsonNumericReader.ToDouble(line["quantity"]) * JsonNumericReader.ToDouble(line["unit_price"]);
        line["discount_total"] = Math.Clamp(JsonNumericReader.ToDouble(dt), 0, gross);
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
        EnsureCart(cart);
        var root = ParseRoot(cart);
        if (root["items"] is not JsonArray items)
            return;

        RepairMissingProductIds(items);

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (string.Equals(items[i]?["id"]?.GetValue<string>(), itemId, StringComparison.Ordinal))
                items.RemoveAt(i);
        }

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    /// <summary>2026-10-03, клиент: «облегчить продажу оптового и розничного: опт не только на весь чек, но и на сам
    /// товар — переключатель». Строка переходит на оптовую цену товара (wholesale_price) или обратно на розничную
    /// (запоминается в retail_price строки). Скидка суммой переклампливается под новую сумму строки.</summary>
    public static bool SetLineWholesale(ICartService cart, string itemId, bool wholesale, double wholesalePrice)
    {
        EnsureCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray ?? new JsonArray();
        RepairMissingProductIds(items);
        root["items"] = items;
        var line = FindLineByItemId(items, itemId);
        if (line == null)
            return false;

        var isWholesale = line["is_wholesale"] is JsonValue w && w.TryGetValue<bool>(out var was) && was;
        if (wholesale == isWholesale)
            return false;
        if (wholesale)
        {
            if (!(wholesalePrice > 0))
                return false;
            line["retail_price"] = JsonNumericReader.ToDouble(line["unit_price"]);
            line["unit_price"] = wholesalePrice;
            line["is_wholesale"] = true;
        }
        else
        {
            if (line["retail_price"] is { } retail)
                line["unit_price"] = JsonNumericReader.ToDouble(retail);
            line.Remove("retail_price");
            line.Remove("is_wholesale");
        }

        ClampLineDiscountToGross(line);
        RecalcLine(line);
        RecalcCartTotals(root);
        ApplyRoot(cart, root);
        return true;
    }

    public static void PatchLineDiscount(ICartService cart, string itemId, string? mode, string? value)
    {
        EnsureCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray ?? new JsonArray();
        RepairMissingProductIds(items);
        root["items"] = items;

        var line = FindLineByItemId(items, itemId);
        if (line == null)
            throw new InvalidOperationException("CartItem not found in this cart.");

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
            line["discount_percent"] = Math.Clamp(pct, 0, 100);
        else if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var sum))
        {
            line["discount_total"] = sum;
            ClampLineDiscountToGross(line);
        }

        RecalcLine(line);
        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    public static void PatchOrderDiscount(ICartService cart, string? percent, string? total)
    {
        EnsureCart(cart);
        var root = ParseRoot(cart);
        root.Remove("order_discount_percent");
        root.Remove("order_discount_total");

        if (!OrderDiscountHelper.IsEmptyOrZeroLike(percent)
            && double.TryParse(OrderDiscountHelper.NormalizeDecimal(percent!), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
            && p > 0)
        {
            root["order_discount_percent"] = Math.Clamp(p, 0, 100);
        }
        else if (!OrderDiscountHelper.IsEmptyOrZeroLike(total)
                 && double.TryParse(OrderDiscountHelper.NormalizeDecimal(total!), NumberStyles.Any, CultureInfo.InvariantCulture, out var t)
                 && t > 0)
        {
            using var beforeDiscount = JsonDocument.Parse(root.ToJsonString());
            var totals = CartTotalsCalculator.Calculate(beforeDiscount.RootElement);
            root["order_discount_total"] = Math.Clamp(t, 0, Math.Max(0, totals.Subtotal - totals.LineDiscounts));
        }

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    public static bool ContainsItemId(ICartService cart, string itemId)
    {
        if (!cart.HasCart || string.IsNullOrEmpty(itemId))
            return false;

        return CartDisplayHelper.EnumerateItems(cart.Root)
            .Any(it => string.Equals(CartDisplayHelper.TryItemId(it), itemId, StringComparison.Ordinal));
    }

    private static void EnsureCart(ICartService cart)
    {
        if (!cart.HasCart)
            throw new InvalidOperationException("Чек не открыт.");
    }

    private static JsonObject ParseRoot(ICartService cart) =>
        CartJsonHelper.ParseCartRoot(cart);

    private static void ApplyRoot(ICartService cart, JsonObject root)
    {
        // These editor operations mutate only the in-memory receipt. A cart that
        // previously came from the server is no longer synchronized after such
        // a mutation, so it must be materialized again before checkout.
        //
        // Keeping the old server cart id here made the second sale look valid
        // locally while the corresponding server cart still had zero items.
        if (!cart.IsLocalOffline)
        {
            root["is_staging"] = true;
            root.Remove("is_local_offline");
        }

        CartJsonHelper.TryApplyObjectToCart(cart, root);
    }

    private static JsonObject? FindLineByItemId(JsonArray? items, string itemId)
    {
        if (items == null)
            return null;

        foreach (var node in items)
        {
            if (node is not JsonObject obj)
                continue;
            if (string.Equals(obj["id"]?.GetValue<string>(), itemId, StringComparison.Ordinal))
                return obj;
        }

        return null;
    }

    /// <summary>Совпадение по товару и цене за единицу — нужно, чтобы продажа целой упаковкой
    /// и поштучная продажа того же товара (в любом порядке добавления) не сливались в одну
    /// строку по чужой цене.</summary>
    private static JsonObject? FindLineByProductIdAndPrice(JsonArray items, string productId, double unitPrice) =>
        FindLineByProductIdAndPrice(items, productId, unitPrice, variantName: null, matchVariant: false);

    /// <summary>2026-09-29: то же, но строка должна быть того же варианта (доп. штрихкод):
    /// variantName = null — строка основного товара (без метки варианта).</summary>
    private static JsonObject? FindLineByProductIdAndPrice(
        JsonArray items, string productId, double unitPrice, string? variantName) =>
        FindLineByProductIdAndPrice(items, productId, unitPrice, variantName, matchVariant: true);

    private static JsonObject? FindLineByProductIdAndPrice(
        JsonArray items, string productId, double unitPrice, string? variantName, bool matchVariant)
    {
        if (items == null) return null;
        string normalizedSearchId = productId?.Trim() ?? "";
        if (string.IsNullOrEmpty(normalizedSearchId)) return null;

        foreach (var node in items)
        {
            if (node is not JsonObject obj) continue;

            string? pid = ExtractId(obj["product_id"]);
            if (string.IsNullOrEmpty(pid) && obj["product"] is JsonObject productObj)
                pid = ExtractId(productObj["id"]);

            if (string.Equals(pid?.Trim(), normalizedSearchId, StringComparison.OrdinalIgnoreCase)
                && PriceMatches(obj, unitPrice)
                && (!matchVariant || string.Equals(LineVariantName(obj), variantName, StringComparison.Ordinal)))
                return obj;
        }
        return null;
    }

    /// <summary>2026-09-29: поле строки чека с названием варианта (доп. штрихкод), с которым её
    /// пробили. Только для кассы: на сервер не уходит, сервер вариантов не хранит.</summary>
    public const string VariantNameField = "variant_name";

    /// <summary>2026-10-01, владелец: «магазин одежды — при выборе нужно выбрать размер, цвет, возможно
    /// изменение цены, если на какой-то размер или цвет есть скидка». Вариант NurCRM — своя строка
    /// чека: сливается только с тем же вариантом (никогда — с основным товаром или другим размером,
    /// даже при акции или одинаковой цене), несёт server_variant_id, размер и цвет. На сервер уходит
    /// variant_id — сервер сам ставит цену варианта (StagingCartService).</summary>
    public static void AddVariant(
        ICartService cart, CatalogProductTileVm product, double qty, double unitPrice,
        string variantId, string label, string? size, string? color)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!double.IsFinite(qty) || qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qty), "Количество должно быть больше нуля.");

        EnsureCart(cart);
        var root = ParseRoot(cart);
        var items = root["items"] as JsonArray ?? new JsonArray();
        root["items"] = items;
        RepairMissingProductIds(items);

        JsonObject? existing = null;
        foreach (var node in items)
        {
            if (node is JsonObject obj
                && obj["server_variant_id"] is JsonValue v && v.TryGetValue<string>(out var vid)
                && string.Equals(vid, variantId, StringComparison.OrdinalIgnoreCase))
            {
                existing = obj;
                break;
            }
        }

        if (existing != null)
        {
            existing["quantity"] = JsonNumericReader.ToDouble(existing["quantity"]) + qty;
            RecalcLine(existing);
        }
        else
        {
            var line = BuildLine(product.Id, label, product.Barcode, unitPrice, qty, mustWeigh: false);
            line[VariantNameField] = label;
            line["server_variant_id"] = variantId;
            if (!string.IsNullOrWhiteSpace(size))
                line["variant_size"] = size;
            if (!string.IsNullOrWhiteSpace(color))
                line["variant_color"] = color;
            // 2026-10-02: акционная цена варианта ниже обычной — запоминаем обычную для корзины и чека.
            var basePrice = LocalCartService.ParsePrice(product.PriceLine);
            if (basePrice > unitPrice + 0.005)
                line["variant_base_price"] = basePrice;
            RecalcLine(line);
            items.Add(line);
        }

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
    }

    private static string? LineVariantName(JsonObject line) =>
        line[VariantNameField] is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;

    private static JsonObject BuildLine(
        string productId,
        string title,
        string? barcode,
        double unitPrice,
        double qty,
        bool mustWeigh,
        string? salePackageId = null)
    {
        var line = new JsonObject
        {
            ["id"] = "line-" + Guid.NewGuid().ToString("N"),
            ["product_id"] = productId,
            ["product_name"] = title,
            ["barcode"] = barcode,
            ["quantity"] = qty,
            ["unit_price"] = unitPrice,
            ["is_weight"] = mustWeigh,
            ["product"] = new JsonObject
            {
                ["id"] = productId,
                ["name"] = title,
                ["barcode"] = barcode,
                ["must_weigh"] = mustWeigh,
            },
        };
        if (!string.IsNullOrWhiteSpace(salePackageId))
            line["sale_package_id"] = salePackageId;
        // 2026-09-28, продажа №1136: акции товара NurCRM едут в строке чека — сервер применит
        // их сам, и касса должна показать и взять ту же сумму (см. PromotionRules).
        PromotionRules.ApplyToLine(line, productId);
        RecalcLine(line);
        return line;
    }

    /// <summary>2026-09-28, продажа №1136: перед оплатой обновляет в локальном чеке акции товаров
    /// по последнему каталогу (строка могла попасть в чек до первой загрузки каталога или акцию
    /// поменяли на сайте, пока чек лежал отложенным). Серверную корзину не трогает — её строки
    /// сервер прислал уже со своими акциями. true — чек изменился (надо обновить экран).</summary>
    public static bool RefreshPromotionRules(ICartService cart)
    {
        if (!cart.HasCart || !(cart.IsStaging || cart.IsLocalOffline))
            return false;

        var root = ParseRoot(cart);
        if (root["items"] is not JsonArray items)
            return false;

        var changed = false;
        foreach (var node in items)
        {
            if (node is not JsonObject line)
                continue;
            var productId = ExtractId(line["product_id"])
                            ?? (line["product"] is JsonObject productObj ? ExtractId(productObj["id"]) : null);
            if (PromotionRules.ApplyToLine(line, productId))
            {
                RecalcLine(line);
                changed = true;
            }
        }

        if (!changed)
            return false;

        RecalcCartTotals(root);
        ApplyRoot(cart, root);
        return true;
    }

    /// <summary>Есть ли у строки чека акция товара NurCRM — тогда скидку строки назначает сервер
    /// (скидку кассира на такую строку он не принимает, см. PromotionRules).</summary>
    public static bool LineHasPromotion(ICartService cart, string? itemId)
    {
        if (!cart.HasCart || string.IsNullOrEmpty(itemId))
            return false;
        return CartDisplayHelper.EnumerateItems(cart.Root)
            .Any(it => string.Equals(CartDisplayHelper.TryItemId(it), itemId, StringComparison.Ordinal)
                       && PromotionRules.LineHasRules(it));
    }

    private static void RecalcLine(JsonObject line)
    {
        var qty = JsonNumericReader.ToDouble(line["quantity"]);
        var unitPrice = JsonNumericReader.ToDouble(line["unit_price"]);
        var gross = Math.Round(qty * unitPrice, 2, MidpointRounding.AwayFromZero);
        // 2026-09-28, продажа №1136: сумма строки — с той же скидкой, что в итоге чека и в запросе
        // на сервер (CartDisplayHelper.EffectiveLineDiscount: скидка кассира или акция товара).
        // Раньше здесь был свой разбор (только discount_total / discount_percent, без акций).
        double discount;
        using (var lineDoc = JsonDocument.Parse(line.ToJsonString()))
            discount = CartDisplayHelper.EffectiveLineDiscount(lineDoc.RootElement);

        discount = Math.Round(discount, 2, MidpointRounding.AwayFromZero);
        // Отрицательная строка = «Расход» (доп. услуга, 2026-09-07) — её сумму не обнуляем.
        line["line_total"] = gross < 0
            ? Math.Round(gross, 2, MidpointRounding.AwayFromZero)
            : Math.Max(0, Math.Round(gross - discount, 2, MidpointRounding.AwayFromZero));
    }

    private static void RecalcCartTotals(JsonObject root)
    {
        using var doc = JsonDocument.Parse(root.ToJsonString());
        var totals = CartTotalsCalculator.Calculate(doc.RootElement);
        root["subtotal"] = totals.Subtotal;
        root["total"] = totals.TotalDue;
        root["total_due"] = totals.TotalDue;
        if (totals.LineCount == 0 && root["totals"] is JsonObject nested)
        {
            nested["total"] = 0;
            nested["grand_total"] = 0;
            nested["amount_due"] = 0;
        }
    }

    private static double ParsePrice(string priceLine)
    {
        if (string.IsNullOrWhiteSpace(priceLine))
            return 0;
        var digits = new string(priceLine.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray())
            .Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }

    public static void RepairMissingProductIds(JsonArray items)
    {
        if (items == null) return;

        foreach (var node in items)
        {
            if (node is not JsonObject line) continue;

            string? pid = ExtractId(line["product_id"]);
            string? innerPid = null;

            if (line["product"] is JsonObject productObj)
            {
                innerPid = ExtractId(productObj["id"]);
            }

            // Сценарий 1: product_id пуст, но есть внутри product["id"]
            if (string.IsNullOrEmpty(pid) && !string.IsNullOrEmpty(innerPid))
            {
                line["product_id"] = innerPid;
            }
            // Сценарий 2: product["id"] пуст, но есть product_id (восстанавливаем вложенный)
            else if (!string.IsNullOrEmpty(pid) && string.IsNullOrEmpty(innerPid) && line["product"] is JsonObject productObj2)
            {
                productObj2["id"] = pid;
            }
            // Сценарий 3: Оба есть, но разные (например, один строка, другой число) - синхронизируем
            else if (!string.IsNullOrEmpty(pid) && !string.IsNullOrEmpty(innerPid)
                     && !string.Equals(pid, innerPid, StringComparison.OrdinalIgnoreCase))
            {
                // Принудительно ставим единый ID (берем из вложенного, как более достоверного)
                line["product_id"] = innerPid;
            }
        }
    }

    private static string? ExtractId(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            // Если ID записан как строка
            if (val.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                return s.Trim();
            // Если ID записан как целое число (например, 12345)
            if (val.TryGetValue<long>(out var l))
                return l.ToString();
            // Если ID записан как число с плавающей точкой
            if (val.TryGetValue<double>(out var d))
                return d.ToString(CultureInfo.InvariantCulture);
        }
        return null;
    }
}
