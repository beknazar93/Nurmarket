using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Приёмка товара на склад — по образцу «Массового сканирования» сайта (2026-09-24).
///
/// Раньше приёмка кассы проводилась актом инвентаризации: остаток прибавлялся, но ни цены
/// закупки, ни поставщика, ни следа в истории товара не оставалось. Теперь:
///  • штрихкод ищется по своему складу, затем по общей базе CRM (товар узнан, но у магазина
///    его ещё нет), и только потом считается неизвестным — те же три колонки, что на сайте;
///  • у каждой строки обязательны цена закупки и цена продажи;
///  • с поставщиком приход проводится документом «Закупки» сайта (сервер сам прибавляет
///    остаток и помнит цену закупки), без поставщика — как у сайта: остаток и цены пишутся
///    прямо в товар;
///  • каждая принятая строка ложится в историю закупок товара — её видно в карточке.</summary>
public sealed class PurchaseReceivingService
{
    public static PurchaseReceivingService Instance { get; } = new();

    public sealed record Supplier(string Id, string Name);

    public sealed record HistoryEntry(
        DateTime At,
        double Quantity,
        string? Unit,
        double PurchasePrice,
        double? SalePrice,
        string? SupplierName,
        string? Employee,
        string Source,
        string? ReceiptId);

    public sealed record PostResult(IReadOnlyList<ReceivingLineVm> PostedLines, double TotalAmount, int Created, IReadOnlyList<string> Errors);

    private bool _tableReady;

    private static ICatalogApiService Api => PosApp.CatalogApi;

    // ------------------------------------------------------------------ поставщики

    public async Task<List<Supplier>> LoadSuppliersAsync(CancellationToken ct = default)
    {
        var data = await Api.ListSuppliersAsync(ct).ConfigureAwait(false);
        var rows = data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray()
                : default;

        var list = new List<Supplier>();
        foreach (var row in rows)
        {
            var id = Str(row, "id");
            var name = Str(row, "full_name") ?? Str(row, "name") ?? Str(row, "llc") ?? Str(row, "phone");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new Supplier(id!, name!.Trim()));
        }

        return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ скан

    /// <summary>Строка приёмки по штрихкоду: свой склад (сначала локальный каталог, он мгновенный,
    /// потом сервер — вдруг товар добавили на сайте минуту назад), общая база CRM, неизвестный.</summary>
    public async Task<ReceivingLineVm> LookupAsync(string barcode, double quantity, CancellationToken ct = default)
    {
        var code = barcode.Trim();

        var tile = LocalProductRepository.Instance.TryGetTileByBarcode(code);
        if (tile is not null)
            return FromTile(tile, quantity);

        try
        {
            var product = await Api.FindWarehouseProductByBarcodeAsync(code, ct).ConfigureAwait(false);
            if (product is { } p)
            {
                var price = Num(p, "price");
                var purchase = Num(p, "purchase_price");
                var name = Str(p, "name") ?? code;
                var unit = Str(p, "unit") ?? "шт";
                return new ReceivingLineVm
                {
                    ProductId = Str(p, "id"),
                    Barcode = Str(p, "barcode") ?? code,
                    ProductName = name,
                    Unit = unit,
                    OriginalName = name,
                    OriginalUnit = unit,
                    Source = ReceivingSource.Warehouse,
                    StockBefore = Num(p, "quantity"),
                    Quantity = quantity,
                    PurchasePrice = purchase,
                    SalePrice = price,
                    OriginalPurchasePrice = purchase,
                    OriginalSalePrice = price,
                };
            }

            var global = await Api.FindGlobalProductByBarcodeAsync(code, ct).ConfigureAwait(false);
            if (global is { } g)
            {
                return new ReceivingLineVm
                {
                    Barcode = Str(g, "barcode") ?? code,
                    ProductName = Str(g, "name") ?? "",
                    Source = ReceivingSource.GlobalBase,
                    Quantity = quantity,
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Нет связи — не повод терять скан: строка встанет как новая, и название можно
            // вписать руками. При проведении сервер всё равно ещё раз увидит штрихкод.
            PosLogger.Log($"Приёмка: поиск штрихкода {code} на сервере не удался: {ex.Message}", "WARNING");
        }

        return new ReceivingLineVm
        {
            Barcode = code,
            ProductName = "",
            Source = ReceivingSource.Unknown,
            Quantity = quantity,
        };
    }

    public static ReceivingLineVm FromTile(NurMarketKassa.Models.Pos.CatalogProductTileVm tile, double quantity)
    {
        var price = LocalCartService.ParsePrice(tile.PriceLine ?? "");
        var unit = string.IsNullOrWhiteSpace(tile.Unit) ? "шт" : tile.Unit!;
        return new ReceivingLineVm
        {
            ProductId = tile.Id,
            Barcode = tile.Barcode ?? "",
            ProductName = tile.Title,
            Unit = unit,
            OriginalName = tile.Title,
            OriginalUnit = unit,
            Source = ReceivingSource.Warehouse,
            StockBefore = tile.Quantity,
            Quantity = quantity,
            PurchasePrice = tile.PurchasePrice,
            SalePrice = price,
            OriginalPurchasePrice = tile.PurchasePrice,
            OriginalSalePrice = price,
        };
    }

    // ------------------------------------------------------------------ проведение

    /// <summary>Проверка перед проведением: названия у новых товаров, количество и обе цены.
    /// Пустой список — можно проводить.</summary>
    public static List<string> Validate(IReadOnlyList<ReceivingLineVm> lines)
    {
        var problems = new List<string>();
        var newBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            // Строка «+ Новый товар» может быть без штрихкода — тогда в сообщении нечем её назвать.
            var label = string.IsNullOrWhiteSpace(line.Barcode)
                ? Tr.T("новый товар", "жаңы товар", "new product", "yeni ürün", "yangi mahsulot")
                : line.Barcode;
            var name = string.IsNullOrWhiteSpace(line.ProductName) ? label : line.ProductName;
            if (string.IsNullOrWhiteSpace(line.ProductName))
                problems.Add(Tr.T($"{label}: впишите название нового товара",
                    $"{label}: жаңы товардын атын жазыңыз", $"{label}: enter a name for the new product",
                    $"{label}: yeni ürünün adını girin", $"{label}: yangi mahsulot nomini kiriting"));

            // Штрихкод, вписанный руками у нового товара, не должен совпасть с уже существующим —
            // иначе на складе появятся два товара с одним кодом (2026-09-26).
            if (line.IsNew && !string.IsNullOrWhiteSpace(line.Barcode))
            {
                if (!newBarcodes.Add(line.Barcode))
                    problems.Add(Tr.T($"{name}: штрихкод {line.Barcode} встречается в приёмке дважды",
                        $"{name}: {line.Barcode} штрихкоду эки жолу бар", $"{name}: barcode {line.Barcode} appears twice",
                        $"{name}: {line.Barcode} barkodu iki kez var", $"{name}: {line.Barcode} shtrix-kodi ikki marta bor"));
                else if (line.Source == ReceivingSource.Unknown
                         && CatalogCacheService.Products.FirstOrDefault(p =>
                             string.Equals(p.Barcode, line.Barcode, StringComparison.OrdinalIgnoreCase)) is { } owner)
                    problems.Add(Tr.T($"{name}: штрихкод {line.Barcode} уже есть у товара «{owner.Title}» — отсканируйте его, чтобы принять",
                        $"{name}: {line.Barcode} штрихкоду «{owner.Title}» товарында бар — аны сканерлеңиз",
                        $"{name}: barcode {line.Barcode} already belongs to “{owner.Title}” — scan it to receive",
                        $"{name}: {line.Barcode} barkodu “{owner.Title}” ürününde var — kabul için onu okutun",
                        $"{name}: {line.Barcode} shtrix-kodi “{owner.Title}” mahsulotida bor — qabul uchun uni skanerlang"));
            }
            if (line.Quantity <= 0)
                problems.Add(Tr.T($"{name}: не указано количество", $"{name}: саны көрсөтүлгөн эмес",
                    $"{name}: quantity missing", $"{name}: miktar yok", $"{name}: miqdor ko'rsatilmagan"));
            if (line.PurchasePrice <= 0)
                problems.Add(Tr.T($"{name}: не указана цена закупки", $"{name}: сатып алуу баасы жок",
                    $"{name}: purchase price missing", $"{name}: alış fiyatı yok", $"{name}: xarid narxi yo'q"));
            if (line.SalePrice <= 0)
                problems.Add(Tr.T($"{name}: не указана цена продажи", $"{name}: сатуу баасы жок",
                    $"{name}: sale price missing", $"{name}: satış fiyatı yok", $"{name}: sotuv narxi yo'q"));
        }

        return problems;
    }

    /// <summary>Провести приёмку. <paramref name="paidNow"/> — только с поставщиком: true — оплачено
    /// сразу (деньги уходят из кассы расходов, как у сайта), false — в долг поставщику.</summary>
    public async Task<PostResult> PostAsync(
        IReadOnlyList<ReceivingLineVm> lines,
        Supplier? supplier,
        bool paidNow,
        string? employee,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var created = 0;

        // 1. Новые товары заводим на складе. Товар из общей базы — тем же запросом, что сайт
        //    (создаётся по его шаблону), неизвестный — обычной карточкой.
        foreach (var line in lines.Where(l => string.IsNullOrWhiteSpace(l.ProductId)))
        {
            try
            {
                JsonElement response;
                if (line.Source == ReceivingSource.GlobalBase)
                {
                    response = await Api.CreateProductFromGlobalBarcodeAsync(line.Barcode, line.ProductName, line.SalePrice, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    response = await Api.CreateProductAsync(new ProductEditRequest
                    {
                        Name = line.ProductName.Trim(),
                        Barcode = line.Barcode,
                        Unit = line.Unit,
                        Quantity = 0,
                        PurchasePrice = line.PurchasePrice,
                        Price = line.SalePrice,
                    }, ct).ConfigureAwait(false);
                }

                var id = Str(response, "id") ?? (response.TryGetProperty("product", out var pr) ? Str(pr, "id") : null);
                if (string.IsNullOrWhiteSpace(id))
                {
                    errors.Add($"{line.ProductName}: сервер не вернул номер созданного товара");
                    continue;
                }

                line.ProductId = id;
                created++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{line.ProductName}: не удалось создать товар — {ex.Message}");
            }
        }

        var ready = lines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId)).ToList();
        if (ready.Count == 0)
            return new PostResult(Array.Empty<ReceivingLineVm>(), 0, created, errors);

        var posted = new List<ReceivingLineVm>();
        string? receiptId = null;

        if (supplier is not null)
        {
            // 2. С поставщиком — документ прихода. Остаток прибавляет сервер.
            var body = new Dictionary<string, object?>
            {
                ["items"] = ready.Select(l => new Dictionary<string, object?>
                {
                    ["product"] = l.ProductId,
                    ["qty"] = l.Quantity,
                    ["purchase_price"] = l.PurchasePrice,
                }).ToList(),
                ["payment_type"] = paidNow ? "cash" : "debt",
            };
            // Как у сайта: оплата «сразу» уходит из кассы переменных расходов.
            if (paidNow)
                body["cashbox_role"] = "expense_variable";

            try
            {
                var response = await Api.CreateSupplierReceiptAsync(supplier.Id, body, ct).ConfigureAwait(false);
                receiptId = Str(response, "id");
                posted.AddRange(ready);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add("Приход от поставщика не проведён: " + ex.Message);
                return new PostResult(Array.Empty<ReceivingLineVm>(), 0, created, errors);
            }

            // Цена продажи в документ прихода не входит — у сайта она тоже пишется в товар
            // отдельным запросом, и только если её поменяли. Так же — поправленные в приёмке
            // название и единица товара со склада.
            foreach (var line in ready.Where(l => Math.Abs(l.SalePrice - l.OriginalSalePrice) > 0.004 || l.NameOrUnitChanged))
            {
                try
                {
                    var fields = new Dictionary<string, object?>();
                    if (Math.Abs(line.SalePrice - line.OriginalSalePrice) > 0.004)
                        fields["price"] = line.SalePrice;
                    AddNameAndUnit(line, fields);
                    await Api.PatchProductFieldsAsync(line.ProductId!, fields, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{line.ProductName}: товар принят, но цена продажи не обновилась — {ex.Message}");
                }
            }
        }
        else
        {
            // 3. Без поставщика — как у сайта: остаток и цены пишутся в сам товар. Остаток
            //    берётся свежий с сервера, а не из каталога кассы: за время приёмки товар могли
            //    продать на другой кассе.
            foreach (var line in ready)
            {
                try
                {
                    var current = await Api.ProductsDetailAsync(line.ProductId!, ct).ConfigureAwait(false);
                    var stock = current is { } c ? Num(c, "quantity") : line.StockBefore;

                    var fields = new Dictionary<string, object?>
                    {
                        ["quantity"] = Math.Round(stock + line.Quantity, 3),
                        ["purchase_price"] = line.PurchasePrice,
                        ["price"] = line.SalePrice,
                    };
                    AddNameAndUnit(line, fields);
                    await Api.PatchProductFieldsAsync(line.ProductId!, fields, ct).ConfigureAwait(false);
                    posted.Add(line);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{line.ProductName}: не принят — {ex.Message}");
                }
            }
        }

        // 4. История закупок товара — локально. С поставщиком она есть и на сервере (документ
        //    «Закупки»), но цену продажи на момент приёмки помнит только касса.
        foreach (var line in posted)
        {
            try
            {
                AppendHistory(line, supplier?.Name, employee, receiptId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приёмка: строка истории закупок не записана: {ex.Message}", "WARNING");
            }
        }

        return new PostResult(posted, posted.Sum(l => l.LineTotal), created, errors);
    }

    /// <summary>Название и единица, поправленные в приёмке у товара со склада, — в карточку.</summary>
    private static void AddNameAndUnit(ReceivingLineVm line, Dictionary<string, object?> fields)
    {
        if (!line.NameOrUnitChanged)
            return;
        if (!string.IsNullOrWhiteSpace(line.ProductName))
            fields["name"] = line.ProductName.Trim();
        fields["unit"] = line.Unit;
    }

    // ------------------------------------------------------------------ история закупок

    private void EnsureTable(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        if (_tableReady)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductPurchaseHistory (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                company_id TEXT,
                product_id TEXT NOT NULL,
                product_name TEXT,
                barcode TEXT,
                quantity REAL NOT NULL,
                unit TEXT,
                purchase_price REAL NOT NULL,
                sale_price REAL,
                supplier_name TEXT,
                receipt_id TEXT,
                employee TEXT,
                at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_purchase_history_product ON ProductPurchaseHistory(product_id);
            """;
        command.ExecuteNonQuery();
        _tableReady = true;
    }

    private void AppendHistory(ReceivingLineVm line, string? supplierName, string? employee, string? receiptId)
    {
        DatabaseService.Instance.WithConnection(connection =>
        {
            EnsureTable(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ProductPurchaseHistory
                    (company_id, product_id, product_name, barcode, quantity, unit, purchase_price,
                     sale_price, supplier_name, receipt_id, employee, at)
                VALUES ($company, $product, $name, $barcode, $qty, $unit, $purchase,
                        $sale, $supplier, $receipt, $employee, $at);
                """;
            command.Parameters.AddWithValue("$company", (object?)DatabaseService.Instance.CurrentCompany() ?? DBNull.Value);
            command.Parameters.AddWithValue("$product", line.ProductId);
            command.Parameters.AddWithValue("$name", line.ProductName);
            command.Parameters.AddWithValue("$barcode", line.Barcode);
            command.Parameters.AddWithValue("$qty", line.Quantity);
            command.Parameters.AddWithValue("$unit", line.Unit);
            command.Parameters.AddWithValue("$purchase", line.PurchasePrice);
            command.Parameters.AddWithValue("$sale", line.SalePrice);
            command.Parameters.AddWithValue("$supplier", (object?)supplierName ?? DBNull.Value);
            command.Parameters.AddWithValue("$receipt", (object?)receiptId ?? DBNull.Value);
            command.Parameters.AddWithValue("$employee", (object?)employee ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        });
    }

    public List<HistoryEntry> LoadLocalHistory(string productId)
    {
        var list = new List<HistoryEntry>();
        DatabaseService.Instance.WithConnection(connection =>
        {
            EnsureTable(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT at, quantity, unit, purchase_price, sale_price, supplier_name, employee, receipt_id "
                + "FROM ProductPurchaseHistory WHERE product_id = $id"
                + DatabaseService.Instance.OwnRowsClauseFor()
                + " ORDER BY at DESC;";
            command.Parameters.AddWithValue("$id", productId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                list.Add(new HistoryEntry(
                    at,
                    reader.GetDouble(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    Tr.T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        });
        return list;
    }

    /// <summary>История закупок товара: приёмки этой кассы плюс приходы от поставщиков с сервера
    /// (в том числе сделанные на сайте). Приход, который провела сама касса, есть в обоих
    /// местах — показываем его один раз, из кассы: там помнится ещё и цена продажи.</summary>
    public async Task<List<HistoryEntry>> LoadHistoryAsync(string productId, CancellationToken ct = default)
    {
        var local = LoadLocalHistory(productId);
        var known = local.Where(h => !string.IsNullOrWhiteSpace(h.ReceiptId)).Select(h => h.ReceiptId!).ToHashSet();
        var result = new List<HistoryEntry>(local);

        try
        {
            for (var page = 1; page <= 3; page++)
            {
                var data = await Api.ListSupplierReceiptsAsync(page, 100, ct).ConfigureAwait(false);
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("results", out var receipts)
                    || receipts.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var receipt in receipts.EnumerateArray())
                {
                    var receiptId = Str(receipt, "id");
                    if (receiptId is not null && known.Contains(receiptId))
                        continue;
                    if (!receipt.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                        continue;

                    DateTime.TryParse(Str(receipt, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!string.Equals(Str(item, "product_id") ?? Str(item, "product"), productId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        result.Add(new HistoryEntry(
                            at,
                            Num(item, "qty"),
                            Str(item, "unit"),
                            Num(item, "purchase_price"),
                            null,
                            Str(receipt, "supplier_name"),
                            Str(receipt, "created_by_name"),
                            Tr.T("Закупки на сайте", "Сайттагы сатып алуулар", "Website purchases", "Web alımları", "Saytdagi xaridlar"),
                            receiptId));
                    }
                }

                if (!data.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"История закупок: приходы с сервера не загружены: {ex.Message}", "WARNING");
        }

        return result.OrderByDescending(h => h.At).ToList();
    }

    // ------------------------------------------------------------------ JSON

    private static string? Str(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double Num(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
        && JsonNumericReader.TryToDouble(v, out var d)
            ? d
            : 0;
}
