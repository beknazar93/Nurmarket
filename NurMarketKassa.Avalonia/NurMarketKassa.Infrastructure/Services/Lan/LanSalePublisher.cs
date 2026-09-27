using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.Lan;

/// <summary>
/// Превращает оплаченный чек в запись журнала обмена: состав для сводки владельца и списание
/// остатка для соседних касс. Списание считается так же, как ApplyOfflineStockDecrement
/// (единицы склада, состав комплекта) — соседи вычитают ровно то, что вычла бы сама касса.
/// </summary>
public static class LanSalePublisher
{
    /// <param name="saleKey">У офлайн-чека — номер записи очереди, у онлайн — номер продажи.</param>
    /// <param name="uploaded">Продажа уже на сервере (онлайн-оплата).</param>
    public static void Publish(string? saleKey, string? serverSaleId, bool uploaded, string cartJson, double total, string? paymentMethod)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(saleKey) || !UserPreferences.Instance.LanSyncEnabled)
                return;

            var sale = new LanSalePayload
            {
                SaleKey = saleKey,
                ServerSaleId = serverSaleId,
                Uploaded = uploaded,
                CreatedAtUtc = DateTime.UtcNow,
                Total = total,
                PaymentMethod = paymentMethod ?? "",
                CashboxName = PosApp.PosCashboxDisplayName,
                CashierName = PosApp.CurrentUserDisplayName,
            };

            var stock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            foreach (var item in CartDisplayHelper.EnumerateItems(doc.RootElement))
            {
                var productId = CartDisplayHelper.TryProductId(item);
                double.TryParse(CartDisplayHelper.LineTotal(item), NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal);
                sale.Items.Add(new LanSaleItem
                {
                    ProductId = productId ?? "",
                    Name = CartDisplayHelper.ItemName(item),
                    Qty = CartDisplayHelper.LineQuantity(item),
                    LineTotal = lineTotal,
                });

                if (string.IsNullOrEmpty(productId))
                    continue;
                var tile = FindTile(productId);
                if (tile == null)
                    continue;
                var soldQty = CartDisplayHelper.LineQuantityInStockUnits(item, tile);
                if (soldQty <= 0)
                    continue;

                Add(stock, productId, soldQty);
                if (tile.IsBundle && tile.BundleItems is { Count: > 0 } bundleItems)
                    foreach (var component in bundleItems)
                        if (!string.IsNullOrEmpty(component.ProductId))
                            Add(stock, component.ProductId, component.Quantity * soldQty);
            }

            sale.Stock = stock.Select(kv => new LanStockDelta { ProductId = kv.Key, Qty = kv.Value }).ToList();
            LanJournal.PublishSale(sale);
        }
        catch (Exception ex)
        {
            // Оплата уже прошла — обмен не должен её ломать.
            PosLogger.Log($"LAN: продажа не подготовлена для журнала: {ex.Message}", "LAN");
        }
    }

    private static void Add(Dictionary<string, double> map, string id, double qty) =>
        map[id] = map.TryGetValue(id, out var q) ? q + qty : qty;

    internal static CatalogProductTileVm? FindTile(string productId) =>
        CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
}
