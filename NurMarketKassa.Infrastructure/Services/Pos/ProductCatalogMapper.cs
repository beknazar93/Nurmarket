using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using System;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services
{
    public static class ProductCatalogMapper
    {
        public static CatalogProductTileVm? TryTile(JsonElement p, string apiBaseUrl)
        {
            string? id = TryId(p);
            if (string.IsNullOrEmpty(id))
                return null;

            string title = Title(p);
            double? price = TryPrice(p);
            string priceLine = price is null ? "—" : $"{price.Value.ToString("0.00", CultureInfo.InvariantCulture)} сом";
            bool mustWeigh = CartDisplayHelper.ProductMustWeigh(p);
            string? imageUrl = ProductImageUrl.TryGet(p, apiBaseUrl);

            // ---------- Barcode ----------
            string? barcode = null;
            foreach (string key in new[] { "barcode", "sku", "article", "code", "ean" })
            {
                if (p.TryGetProperty(key, out var bVal))
                {
                    string? s = bVal.ValueKind switch
                    {
                        JsonValueKind.String => bVal.GetString(),
                        JsonValueKind.Number => bVal.GetRawText(),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(s) && s != "0")
                    {
                        barcode = s;
                        break;
                    }
                }
            }

            CatalogProductTileVm vm = new CatalogProductTileVm(id, title, priceLine, mustWeigh, imageUrl);
            vm.Barcode = barcode;

            // ---------- PurchasePrice ----------
            if (p.TryGetProperty("purchase_price", out var ppEl) && TryGetDouble(ppEl, out double ppVal))
                vm.PurchasePrice = ppVal;

            // ---------- Unit ----------
            if (p.TryGetProperty("unit", out var unitEl) && unitEl.ValueKind == JsonValueKind.String)
                vm.Unit = unitEl.GetString()?.Trim();

            var qty = StockSyncService.ResolveStockQuantity(p, mustWeigh);
            StockSyncService.ApplyQuantityToTile(vm, qty, mustWeigh);

            // Категория и бренд
            if (p.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String)
                vm.Category = cat.GetString();
            if (p.TryGetProperty("brand", out var brd) && brd.ValueKind == JsonValueKind.String)
                vm.Brand = brd.GetString();

            // Статус и горячая клавиша (если нужны для фильтра)
            if (p.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                vm.Status = st.GetString();
            if (p.TryGetProperty("hotkey_group", out var hk) && hk.ValueKind == JsonValueKind.String)
                vm.HotkeyGroup = hk.GetString();

            if (p.TryGetProperty("is_favorite", out var favEl) &&
                favEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                vm.IsFavorite = favEl.GetBoolean();
            }
            else
            {
                vm.IsFavorite = CatalogCacheService.FavoriteIds.Contains(vm.Id);
            }

            return ProductUnitNormalizer.TryPrepareCatalogTile(vm) ? vm : null;
        }

        public static CatalogProductTileVm? TryTile(ProductDto dto, string apiBaseUrl)
        {
            if (dto == null) return null;

            var title = !string.IsNullOrWhiteSpace(dto.Title)
                ? dto.Title
                : dto.Name;
            if (string.IsNullOrEmpty(dto.Id) || string.IsNullOrEmpty(title))
                return null;

            var mustWeigh = dto.ResolvesMustWeigh();
            var vm = new CatalogProductTileVm(
                dto.Id,
                title,
                $"{(dto.Price ?? 0m).ToString("0.00", CultureInfo.InvariantCulture)} сом",
                mustWeigh,
                dto.ImageUrl)
            {
                Barcode = dto.Barcode,
                Category = dto.Category,
                Brand = dto.Brand,
                IsFavorite = dto.IsFavorite || CatalogCacheService.FavoriteIds.Contains(dto.Id),
            };

            vm.Unit = dto.Unit;
            var qty = dto.ResolvesQuantity(mustWeigh);
            StockSyncService.ApplyQuantityToTile(vm, qty, mustWeigh);
            return ProductUnitNormalizer.TryPrepareCatalogTile(vm) ? vm : null;
        }

        public static string Title(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object)
                return "—";

            foreach (string key in new[] { "name", "title", "display_name", "label" })
            {
                if (p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    string? s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s.Trim();
                }
            }

            string? id = TryId(p);
            return string.IsNullOrEmpty(id) ? "—" : $"Товар #{id}";
        }

        public static string? TryId(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var idProp))
                return null;

            return idProp.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(idProp.GetString()) ? null : idProp.GetString(),
                JsonValueKind.Number => idProp.GetRawText(),
                _ => null
            };
        }

        public static double? TryPrice(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("price", out var v))
                return null;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d))
                return d;
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double x))
                return x;

            return null;
        }

        private static bool TryGetDouble(JsonElement element, out double value)
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.TryGetDouble(out value);
            if (element.ValueKind == JsonValueKind.String)
            {
                string? s = element.GetString();
                if (s != null && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                    return true;
            }
            value = 0;
            return false;
        }
    }
}