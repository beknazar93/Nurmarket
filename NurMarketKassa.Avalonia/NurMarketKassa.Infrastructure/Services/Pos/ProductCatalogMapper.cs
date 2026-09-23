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

            // ---------- PLU (короткий код для весового штрих-кода) ----------
            // Сервер отдаёт "plu" то числом, то строкой ("21") в зависимости от типа поля в
            // NurCRM — раньше строковый вариант молча пропускался, из-за чего Plu оставался
            // пустым и весовой штрих-код никогда не находил товар (хотя число распознавалось
            // корректно). Принимаем оба варианта.
            if (p.TryGetProperty("plu", out var pluEl))
            {
                if (pluEl.ValueKind == JsonValueKind.Number && pluEl.TryGetInt32(out var pluNum))
                    vm.Plu = pluNum;
                else if (pluEl.ValueKind == JsonValueKind.String
                    && int.TryParse(pluEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pluStr))
                    vm.Plu = pluStr;
            }

            // ---------- Article/код товара (весы Rongta, раскладка "по коду") ----------
            // "article" в NurCRM часто присутствует, но ПУСТОЙ, а реальное значение лежит в
            // "code" — раньше проверялось только наличие свойства (TryGetProperty), а не то,
            // что оно непустое, поэтому пустой "article" "побеждал" непустой "code".
            // Как и "plu" выше (см. комментарий), сервер отдаёт "code"/"article" то строкой,
            // то числом — раньше принималась только строка, из-за чего числовой "Код товара"
            // (видно в NurCRM как "Код товара: 2952") молча пропускался, Article оставался
            // пустым, и весовой штрих-код никогда не находил такой товар в каталоге.
            vm.Article = TryStringOrNumber(p, "article") ?? TryStringOrNumber(p, "code");

            // 2026-09-21, живой баг ("весовые не находит когда артикул есть"): Article выше
            // берёт "code" только как запасной вариант, когда "article" пуст — если у товара
            // заполнены ОБА (обычная ситуация на сайте, где это два разных видимых поля
            // карточки, "Артикул" и "Код товара"), настоящий "code" терялся полностью и нигде
            // не сохранялся. ProductCode хранит "code" отдельно и всегда, независимо от
            // Article — весовой штрих-код в раскладке "по коду" нужно сверять именно с ним
            // (см. LocalCartService.FindByEmbeddedCode).
            vm.ProductCode = TryStringOrNumber(p, "code");

            // ---------- Остальные поля карточки товара (нужны форме редактирования) ----------
            if (p.TryGetProperty("markup_percent", out var mpEl) && TryGetDouble(mpEl, out double mpVal))
                vm.MarkupPercent = mpVal;
            if (p.TryGetProperty("description", out var descEl) && descEl.ValueKind == JsonValueKind.String)
                vm.Description = descEl.GetString();
            if (p.TryGetProperty("wholesale_price", out var wpEl) && TryGetDouble(wpEl, out double wpVal))
                vm.WholesalePrice = wpVal;
            if (p.TryGetProperty("discount_percent", out var dpEl) && TryGetDouble(dpEl, out double dpVal))
                vm.DiscountPercent = dpVal;
            if (p.TryGetProperty("country", out var countryEl) && countryEl.ValueKind == JsonValueKind.String)
                vm.Country = countryEl.GetString();
            if (p.TryGetProperty("weight_kg", out var wkgEl) && TryGetDouble(wkgEl, out double wkgVal))
                vm.WeightKg = wkgVal;
            if (p.TryGetProperty("alternate_barcodes", out var altArr) && altArr.ValueKind == JsonValueKind.Array)
            {
                var codes = new System.Collections.Generic.List<string>();
                var variants = new System.Collections.Generic.List<AlternateBarcodeVariant>();
                foreach (var altEl in altArr.EnumerateArray())
                {
                    if (altEl.ValueKind == JsonValueKind.String)
                    {
                        // Старый формат — голая строка штрихкода, без названия/количества.
                        var s = altEl.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            codes.Add(s);
                            variants.Add(new AlternateBarcodeVariant { Barcode = s });
                        }
                    }
                    else if (altEl.ValueKind == JsonValueKind.Object)
                    {
                        // Новый формат (2026-09-21, сайт): {"barcode","name","quantity"} — раньше
                        // такие записи молча пропускались (проверялся только JsonValueKind.String),
                        // из-за чего доп. штрихкод не индексировался ни локально, ни в форме
                        // редактирования, а название варианта не подставлялось при сканировании.
                        var altBarcode = TryStringOrNumber(altEl, "barcode");
                        if (string.IsNullOrWhiteSpace(altBarcode))
                            continue;
                        string? altName = altEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString()
                            : null;
                        double altQty = altEl.TryGetProperty("quantity", out var qEl) && TryGetDouble(qEl, out var qVal)
                            ? qVal
                            : 0;
                        codes.Add(altBarcode);
                        variants.Add(new AlternateBarcodeVariant { Barcode = altBarcode, Name = altName, Quantity = altQty });
                    }
                }
                if (codes.Count > 0)
                    vm.AlternateBarcodesRaw = string.Join("\n", codes);
                if (variants.Count > 0)
                    vm.AlternateBarcodeVariants = variants;
            }

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

            // ---------- Поштучная продажа из упаковки ----------
            // У "bundle" (набор) поле "packages" перечисляет РАЗНЫЕ товары комплекта, а не
            // варианты фасовки одного товара — поштучная продажа для наборов не применима.
            var kind = p.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
                ? kindEl.GetString()
                : null;
            vm.IsBundle = string.Equals(kind, "bundle", StringComparison.OrdinalIgnoreCase);
            if (!vm.IsBundle
                && p.TryGetProperty("packages", out var pkgArr) && pkgArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var pkg in pkgArr.EnumerateArray())
                {
                    if (pkg.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!pkg.TryGetProperty("quantity_in_package", out var qEl) || !TryGetDouble(qEl, out var qInPkg) || qInPkg <= 0)
                        continue;
                    if (!pkg.TryGetProperty("piece_unit_price", out var priceEl) || !TryGetDouble(priceEl, out var piecePrice) || piecePrice <= 0)
                        continue;

                    var pkgName = pkg.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString() ?? ""
                        : "";
                    var pkgUnit = pkg.TryGetProperty("unit", out var unitEl2) && unitEl2.ValueKind == JsonValueKind.String
                        ? unitEl2.GetString() ?? ""
                        : "";
                    var pkgId = pkg.TryGetProperty("id", out var pkgIdEl) && pkgIdEl.ValueKind == JsonValueKind.String
                        ? pkgIdEl.GetString()
                        : null;

                    vm.PieceOption = new ProductPackageOption
                    {
                        Id = pkgId,
                        Name = pkgName,
                        QuantityInPackage = qInPkg,
                        Unit = pkgUnit,
                        PieceUnitPrice = piecePrice,
                    };
                    break;
                }
            }

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

        /// <summary>Читает JSON-поле как строку независимо от того, прислал ли сервер его строкой
        /// или числом (см. комментарий у "plu"/"article" выше — NurCRM непоследователен в этом
        /// для коротких кодовых полей). Числовое значение форматируется без экспоненциальной
        /// записи и без лишних десятичных нулей (сервер иногда шлёт "2952" как число 2952 —
        /// GetRawText() тут безопасен, т.к. это всегда целочисленный код, не денежная сумма).</summary>
        private static string? TryStringOrNumber(JsonElement parent, string propertyName)
        {
            if (!parent.TryGetProperty(propertyName, out var el))
                return null;

            string? s = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                _ => null
            };

            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
    }
}