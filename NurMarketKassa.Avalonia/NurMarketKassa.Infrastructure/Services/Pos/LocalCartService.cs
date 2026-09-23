using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Core.Application;
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
        // Весовой товар без явно указанного веса добавлять нельзя: строка с qty=0
        // означала бы бесплатную продажу.
        if (!double.IsFinite(qty) || qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Количество должно быть больше нуля.");

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
        var code = (barcode ?? "").Trim();
        if (code.Length == 0)
            return false;

        // 2026-09-12: прямое совпадение по каталогу — ПЕРВЫМ (как в BasketPanelViewModel.
        // AddByBarcodeAsync, см. её комментарий, и теперь PosBarcodeScannerService.ScanAsync) —
        // у настоящего EAN-13 штрих-кода, начинающегося с "2", контрольная сумма технически
        // валидна (свойство всех корректно сгенерированных штрих-кодов, не только весовых).
        // Раньше здесь сначала пробовался весовой разбор — штучный товар с таким barcode
        // добавлялся в кг вне зависимости от реального MustWeigh=false в Складе. Весовые коды с
        // весов никогда не совпадают ни с одним товаром напрямую (каждое взвешивание
        // уникально), так что они всё равно корректно попадут в ветку ниже.
        var tile = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase));
        if (tile != null)
        {
            // Вес нужно ввести явно (диалог взвешивания), иначе товар ушёл бы с qty=0.
            if (tile.MustWeigh)
                return false;

            AddProduct(cart, tile);
            return true;
        }

        // Весовой штрих-код со встроенным весом — как в онлайн-пути
        // (PosBarcodeScannerService.ScanEmbeddedWeightAsync).
        if (WeightBarcodeParser.TryParse(code, out var weighted))
        {
            var weighedTile = FindByEmbeddedCode(weighted.ProductCode);
            if (weighedTile == null)
                return false;

            var weightKg = weighted.ResolveWeightKg(ParsePrice(weighedTile.PriceLine));
            AddProduct(cart, weighedTile, weightKg.ToString("0.###", CultureInfo.InvariantCulture));
            return true;
        }

        return false;
    }

    /// <summary>Ищет товар по коду, встроенному в весовой штрих-код (Штрих-М и совместимые весы).
    /// Раскладка задаётся настройками компании (WeightBarcodeParser.Layout, см. CompanyInfoService):
    /// "plu" (по умолчанию) — код это короткий PLU (поле "plu" в NurCRM, 1-2-3...);
    /// "code" (весы Rongta) — код это внутренний артикул/код товара (поле "article"/"code").
    /// В обоих случаях, если основное поле не совпало, пробуем ещё и другое: на практике
    /// оператор весов иногда программирует "слот PLU" собственным артикулом товара, а не
    /// системным Plu — оба выглядят одинаково коротким числом, и по одной этикетке не видно,
    /// какое поле реально используется (подтверждено реальным случаем: этикетка с Plu=1 несла
    /// в штрих-коде код "0003", совпадающий с Article="0003", а не с Plu). Штрих-код/id — их
    /// собственный отдельный запасной вариант, если ни PLU, ни артикул не совпали.</summary>
    public static CatalogProductTileVm? FindByEmbeddedCode(string embeddedProductCode)
    {
        var normalized = embeddedProductCode.TrimStart('0');
        if (normalized.Length == 0)
            normalized = "0";

        var useCodeLayout = string.Equals(
            NurMarketKassa.Core.Application.WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase);

        // Только среди весовых товаров: короткий PLU/артикул легко совпадает с "быстрым
        // ярлыком" случайного штучного товара — без фильтра по MustWeigh весовой штрих-код
        // мог найти не тот товар вместо реально взвешенного.
        if (int.TryParse(normalized, out var numericCode))
        {
            var byPluFirst = !useCodeLayout
                ? CatalogCacheService.Products.FirstOrDefault(p => p.MustWeigh && p.Plu == numericCode)
                : null;
            if (byPluFirst != null)
                return byPluFirst;
        }

        // 2026-09-21, живой баг ("весовые не находит когда артикул есть"): "Артикул" и "Код
        // товара" — два разных поля карточки на сайте (могут быть заполнены оба и различаться,
        // подтверждено живым примером: Артикул=0073, Код товара=0082 у одного товара). Раньше
        // здесь сверялся только Article, а ProductCode ("код товара") нигде не хранился и не
        // проверялся — если весы запрограммированы именно "кодом товара" (что и означает
        // раскладка "по коду"), товар с заполненным Article никогда не находился, даже если
        // ProductCode совпадал идеально.
        var byArticle = CatalogCacheService.Products.FirstOrDefault(p =>
            p.MustWeigh && (MatchesEmbeddedCode(p.Article, embeddedProductCode, normalized)
                            || MatchesEmbeddedCode(p.ProductCode, embeddedProductCode, normalized)));
        if (byArticle != null)
            return byArticle;

        if (useCodeLayout && int.TryParse(normalized, out var pluFallback))
        {
            var byPluFallback = CatalogCacheService.Products.FirstOrDefault(p => p.MustWeigh && p.Plu == pluFallback);
            if (byPluFallback != null)
                return byPluFallback;
        }

        return CatalogCacheService.Products.FirstOrDefault(p =>
            MatchesEmbeddedCode(p.Barcode, embeddedProductCode, normalized)
            || MatchesEmbeddedCode(p.Id, embeddedProductCode, normalized));
    }

    private static bool MatchesEmbeddedCode(string? candidate, string rawCode, string normalizedCode)
    {
        var value = (candidate ?? "").Trim();
        if (value.Length == 0)
            return false;

        return string.Equals(value, rawCode, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value.TrimStart('0'), normalizedCode, StringComparison.OrdinalIgnoreCase);
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
            line["discount_percent"] = Math.Clamp(pct, 0, 100);
        else if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var sum))
        {
            var gross = JsonNumericReader.ToDouble(line["quantity"]) * JsonNumericReader.ToDouble(line["unit_price"]);
            line["discount_total"] = Math.Clamp(sum, 0, gross);
        }

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
        // Скидка суммой не должна пережить уменьшение количества строки.
        if (line.TryGetPropertyValue("discount_total", out var dt) && dt != null)
        {
            var gross = JsonNumericReader.ToDouble(line["quantity"]) * JsonNumericReader.ToDouble(line["unit_price"]);
            line["discount_total"] = Math.Clamp(JsonNumericReader.ToDouble(dt), 0, gross);
        }

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
        var gross = Math.Round(qty * unitPrice, 2, MidpointRounding.AwayFromZero);
        double discount = 0;
        if (line.TryGetPropertyValue("discount_total", out var dt) && dt != null)
            discount = JsonNumericReader.ToDouble(dt);
        else if (line.TryGetPropertyValue("discount_percent", out var dp) && dp != null)
            discount = gross * JsonNumericReader.ToDouble(dp) / 100.0;

        discount = Math.Round(discount, 2, MidpointRounding.AwayFromZero);
        line["line_total"] = Math.Max(0, Math.Round(gross - discount, 2, MidpointRounding.AwayFromZero));
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

    /// <summary>Извлекает числовую цену из PriceLine ("180.00 сом" → 180.00) — public, чтобы
    /// не дублировать в BasketPanelViewModel при разборе весового штрих-кода "по сумме".</summary>
    public static double ParsePrice(string priceLine)
    {
        if (string.IsNullOrWhiteSpace(priceLine))
            return 0;
        var digits = new string(priceLine.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray())
            .Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
    }
}
