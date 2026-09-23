using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

/// <summary>Синхронизация остатков каталога с API и корректировка после продаж.</summary>
public static class StockSyncService
{
    public const double LowStockThreshold = CatalogProductTileVm.LowStockQuantityThreshold;

    /// <summary>
    /// Читает остаток из JSON товара. Поддерживает плоские поля и массив stocks[] с привязкой к кассе.
    /// </summary>
    public static double ResolveStockQuantity(JsonElement product, bool mustWeigh)
    {
        if (product.ValueKind != JsonValueKind.Object)
            return 0;

        // 1) Остатки по кассам/складам — приоритет для POS.
        if (TryResolveFromStocksArray(product, mustWeigh) is { } fromStocks)
            return fromStocks;

        // 2) Весовые плоские поля.
        if (mustWeigh)
        {
            if (TryReadDouble(product, "stock_weight") is { } sw)
                return sw;
            if (TryReadDouble(product, "weight") is { } w)
                return w;
        }

        // 3) Плоские поля остатка.
        foreach (var key in new[]
                 {
                     "stock_quantity",
                     "available_quantity",
                     "available",
                     "current_stock",
                     "balance",
                     "remains",
                     "remain",
                     "stock",
                     "qty",
                     "quantity",
                 })
        {
            if (TryReadDouble(product, key) is { } value)
                return value;
        }

        // 4) Вложенный объект stock: { quantity / ... }
        if (product.TryGetProperty("stock", out var stockObj) && stockObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "stock_quantity", "quantity", "qty", "amount", "value" })
            {
                if (TryReadDouble(stockObj, key) is { } nested)
                    return nested;
            }
        }

        return 0;
    }

    /// <summary>
    /// Синхронно выставляет Quantity/StockInfo (для маппинга и записи в SQLite до привязки к UI).
    /// </summary>
    public static void ApplyQuantityToTile(CatalogProductTileVm vm, double quantity, bool mustWeigh)
    {
        ApplyQuantityCore(vm, quantity, mustWeigh);
    }

    /// <summary>
    /// Обновление уже отображаемой плитки: PropertyChanged только на UI-потоке.
    /// </summary>
    public static void ApplyQuantityToTileOnUi(CatalogProductTileVm vm, double quantity, bool mustWeigh) =>
        UiDispatcherHolder.Post(() => ApplyQuantityCore(vm, quantity, mustWeigh));

    private static void ApplyQuantityCore(CatalogProductTileVm vm, double quantity, bool mustWeigh)
    {
        vm.Quantity = quantity;
        var qtyCulture = CultureInfo.GetCultureInfo("ru-RU");
        vm.StockInfo = mustWeigh
            ? $"{quantity.ToString("F2", qtyCulture)} кг"
            : $"{quantity.ToString("F0", qtyCulture)} шт.";
        vm.IsLowStock = quantity < LowStockThreshold;
    }

    public static async Task OverlayAgentStockAsync(
        IEnumerable<CatalogProductTileVm> products,
        CancellationToken ct = default)
    {
        List<JsonElement> agentProducts;
        try
        {
            agentProducts = await PosApp.CatalogApi.GetAgentProductsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OverlayAgentStock failed: {ex}", "STOCK");
            return;
        }

        if (agentProducts.Count == 0)
        {
            PosLogger.Log("OverlayAgentStock: empty agent stock payload", "STOCK");
            return;
        }

        var qtyMap = BuildQuantityMap(agentProducts);
        var applied = 0;
        foreach (var vm in products)
        {
            if (!qtyMap.TryGetValue(vm.Id, out var qty))
                continue;

            // Синхронно: иначе SyncReplaceAllWithDiff сохранит Quantity=0 до Post.
            ApplyQuantityToTile(vm, qty, vm.MustWeigh);
            applied++;
        }

        PosLogger.Log($"OverlayAgentStock: applied={applied}, map={qtyMap.Count}", "STOCK");
    }

    public static void DecrementLocalStock(string productId, double soldQty)
    {
        if (string.IsNullOrWhiteSpace(productId) || soldQty <= 0)
            return;

        var id = productId.Trim();
        foreach (var vm in CatalogCacheService.Products)
        {
            if (!string.Equals(vm.Id, id, StringComparison.OrdinalIgnoreCase))
                continue;

            // Авторитетное значение считаем и персистим синхронно; UI-плитка обновится отложенно.
            var next = Math.Max(0, vm.Quantity - soldQty);
            ApplyQuantityToTileOnUi(vm, next, vm.MustWeigh);
            CatalogCacheService.PersistProductStock(id, next, vm.MustWeigh);
            break;
        }
    }

    public static async Task RefreshSoldItemsStockAsync(JsonElement cart, CancellationToken ct = default)
    {
        foreach (var line in CartDisplayHelper.EnumerateItems(cart))
        {
            var productId = CartDisplayHelper.TryProductId(line);
            if (string.IsNullOrEmpty(productId))
                continue;

            // 2026-09-13: конвертация в единицы остатка каталога (см. CartDisplayHelper.
            // LineQuantityInStockUnits) — раньше здесь бралось сырое количество ШТУК строки и
            // напрямую вычиталось из остатка в УПАКОВКАХ для поштучной продажи из упаковки.
            var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
            var soldQty = CartDisplayHelper.LineQuantityInStockUnits(line, tile);
            // Captured before DecrementLocalStock (whose UI-side effect is posted, not
            // synchronous) so it reflects the real pre-sale quantity, not a racy read.
            var expectedMax = FindExpectedPostSaleQuantity(productId, soldQty);
            DecrementLocalStock(productId, soldQty);

            try
            {
                var detail = await PosApp.CatalogApi.ProductsDetailAsync(productId, ct).ConfigureAwait(false);
                if (detail is not { } el)
                    continue;

                // 2026-09-16, живой баг ("штучные превращаются в весовые и наоборот") — раньше
                // здесь классификация товара (весовой/штучный) пересчитывалась заново из ответа
                // /products/detail и применялась к плитке (CatalogProductClassifier.ApplyWeightFlags,
                // теперь удалён как неиспользуемый). Эта функция
                // должна только обновлять ОСТАТОК после продажи — а классификация уже верно
                // выставлена полной синхронизацией каталога. Если detail-ответ по какой-то
                // причине не содержит те же поля unit/is_weight, что и каталог (на сайте эта
                // проблема не воспроизводилась — там классификация не пересчитывается на лету),
                // товар молча переклассифицировался. Берём mustWeigh с уже загруженной плитки
                // (tile), а не заново из detail — она не трогается этим путём.
                var mustWeigh = tile?.MustWeigh ?? CartDisplayHelper.ProductMustWeigh(el);
                var qty = ResolveStockQuantity(el, mustWeigh);

                // The server sometimes still returns the pre-sale (higher) quantity here —
                // stock decrement can be processed asynchronously with a short delay. Applying
                // that stale reading would silently undo the correct optimistic decrement above,
                // which is exactly what was happening: sales succeeded but the displayed stock
                // never dropped. Skip the overwrite when the server hasn't caught up yet.
                if (expectedMax is { } max && qty > max + 1e-6)
                {
                    PosLogger.Log(
                        $"RefreshSoldItemsStock: stale server qty for {productId} ({qty} > expected {max}) — keeping optimistic value",
                        "STOCK");
                    continue;
                }

                foreach (var vm in CatalogCacheService.Products)
                {
                    if (!string.Equals(vm.Id, productId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    UiDispatcherHolder.Post(() => ApplyQuantityCore(vm, qty, vm.MustWeigh));
                    CatalogCacheService.PersistProductStock(productId, qty, mustWeigh);
                    break;
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"RefreshSoldItemsStock failed for {productId}: {ex}", "STOCK");
            }
        }

        // Финальный проход по vm.Quantity убран намеренно: остаток уже персистится
        // синхронно в DecrementLocalStock (списание) и по детали товара из API.
        // Перечитывание VM после факта могло затереть корректное значение старым,
        // т.к. плитки обновляются отложенным UiDispatcherHolder.Post.
    }

    private static double? FindExpectedPostSaleQuantity(string productId, double soldQty)
    {
        foreach (var vm in CatalogCacheService.Products)
        {
            if (string.Equals(vm.Id, productId, StringComparison.OrdinalIgnoreCase))
                return Math.Max(0, vm.Quantity - soldQty);
        }

        return null;
    }

    private static double? TryResolveFromStocksArray(JsonElement product, bool mustWeigh)
    {
        foreach (var arrayName in new[] { "stocks", "stock_items", "warehouses", "balances" })
        {
            if (!product.TryGetProperty(arrayName, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            var cashboxId = (PosApp.PosCashboxId ?? "").Trim();
            double? matched = null;
            double sum = 0;
            var any = false;

            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                var qty = ResolveStockRowQuantity(row, mustWeigh);
                if (qty is null)
                    continue;

                any = true;
                sum += qty.Value;

                if (cashboxId.Length > 0 && StockRowMatchesCashbox(row, cashboxId))
                    matched = qty.Value;
            }

            if (!any)
                continue;

            if (matched is not null)
                return matched.Value;

            // Нет привязки к кассе — суммируем остатки по всем складам/точкам.
            return sum;
        }

        return null;
    }

    private static bool StockRowMatchesCashbox(JsonElement row, string cashboxId)
    {
        foreach (var key in new[] { "cashbox_id", "cashbox", "pos_cashbox_id", "warehouse_id", "store_id" })
        {
            if (!row.TryGetProperty(key, out var idEl))
                continue;

            var id = idEl.ValueKind switch
            {
                JsonValueKind.String => idEl.GetString()?.Trim() ?? "",
                JsonValueKind.Number => idEl.GetRawText(),
                _ => "",
            };

            if (string.Equals(id, cashboxId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static double? ResolveStockRowQuantity(JsonElement row, bool mustWeigh)
    {
        if (mustWeigh && TryReadDouble(row, "stock_weight") is { } sw)
            return sw;

        foreach (var key in new[] { "stock_quantity", "quantity", "qty", "amount", "balance", "remains" })
        {
            if (TryReadDouble(row, key) is { } q)
                return q;
        }

        return null;
    }

    private static Dictionary<string, double> BuildQuantityMap(IEnumerable<JsonElement> items)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in items)
        {
            if (!el.TryGetProperty("id", out var idEl)
                && !el.TryGetProperty("product_id", out idEl)
                && !el.TryGetProperty("product", out idEl))
                continue;

            // product: { id: ... }
            if (idEl.ValueKind == JsonValueKind.Object && idEl.TryGetProperty("id", out var nestedId))
                idEl = nestedId;

            var id = idEl.ValueKind switch
            {
                JsonValueKind.Number => idEl.GetRawText(),
                JsonValueKind.String => idEl.GetString() ?? "",
                _ => "",
            };
            if (string.IsNullOrEmpty(id))
                continue;

            var mustWeigh = CartDisplayHelper.ProductMustWeigh(el);
            map[id] = ResolveStockQuantity(el, mustWeigh);
        }

        return map;
    }

    private static double? TryReadDouble(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(
                v.GetString()?.Replace(',', '.'),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var x)
                ? x
                : null,
            _ => null,
        };
    }
}
