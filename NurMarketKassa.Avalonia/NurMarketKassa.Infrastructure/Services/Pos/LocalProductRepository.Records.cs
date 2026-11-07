using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;

#nullable enable

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса LocalProductRepository (разбор огромного файла на partial-файлы), логика не менялась; остальное — LocalProductRepository.cs
public sealed partial class LocalProductRepository
{
    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        // WAL позволяет читателям (UI) не блокироваться на время записи каталога
        // фоновой синхронизацией — на слабых устройствах это была одна из причин
        // подвисаний интерфейса каждые 45 секунд.
        using var pragmaCommand = connection.CreateCommand();
        // 2026-10-04, отчёт о производительности (п. 14): synchronous=NORMAL — для соединений КАТАЛОГА
        // (это соединение — только товары и catalog_meta). В режиме WAL это безопасно для целостности базы
        // (при пропаже питания теряется лишь последняя запись каталога, база не портится), а запись
        // остатка после продажи и синхронизация не ждут fsync на каждую транзакцию (было 4,4 мс на запись
        // при FULL). Продажи, очередь офлайн-чеков и прочее (DatabaseService) остаются с FULL: настройка
        // действует только на своё соединение.
        // 2026-11-06: В5 25.3 — busy_timeout 5000 (было 3000): один срок ожидания для обоих «хозяев» pos_local.db (DatabaseService тоже 5000),
        // иначе запись каталога и запись чека падали с «database is locked» в разное время.
        pragmaCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        pragmaCommand.ExecuteNonQuery();

        return connection;
    }

    private static List<LocalProductRecord> LoadAllRecords()
    {
        var list = new List<LocalProductRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu, hotkey_group, is_bundle, article,
                   bundle_items_json, alternate_barcodes, alternate_barcode_variants, product_code, kind, description, wholesale_price, IsWeight,
                   discount_percent, markup_percent, country, weight_kg, min_quantity
            FROM Products
            ORDER BY name COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LocalProductRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Price = reader.GetDouble(2),
                Barcode = reader.IsDBNull(3) ? null : reader.GetString(3),
                Stock = reader.GetDouble(4),
                Unit = reader.GetString(5),
                IsFavorite = reader.GetInt64(6) == 1,
                MustWeigh = reader.GetInt64(7) == 1,
                ImageUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
                Category = reader.IsDBNull(9) ? null : reader.GetString(9),
                Brand = reader.IsDBNull(10) ? null : reader.GetString(10),
                PurchasePrice = reader.GetDouble(11),
                PieceOptionJson = reader.IsDBNull(12) ? null : reader.GetString(12),
                Plu = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                HotkeyGroup = reader.IsDBNull(14) ? null : reader.GetString(14),
                IsBundle = reader.GetInt64(15) == 1,
                Article = reader.IsDBNull(16) ? null : reader.GetString(16),
                BundleItemsJson = reader.IsDBNull(17) ? null : reader.GetString(17),
                AlternateBarcodesRaw = reader.IsDBNull(18) ? null : reader.GetString(18),
                AlternateBarcodeVariantsJson = reader.IsDBNull(19) ? null : reader.GetString(19),
                ProductCode = reader.IsDBNull(20) ? null : reader.GetString(20),
                Kind = reader.IsDBNull(21) ? null : reader.GetString(21),
                Description = reader.IsDBNull(22) ? null : reader.GetString(22),
                WholesalePrice = reader.IsDBNull(23) ? 0 : reader.GetDouble(23),
                ServerIsWeight = reader.FieldCount > 24 && !reader.IsDBNull(24) ? reader.GetInt64(24) == 1 : null,
                DiscountPercent = reader.FieldCount > 25 && !reader.IsDBNull(25) ? reader.GetDouble(25) : 0,
                MarkupPercent = reader.FieldCount > 26 && !reader.IsDBNull(26) ? reader.GetDouble(26) : 0,
                Country = reader.FieldCount > 27 && !reader.IsDBNull(27) ? reader.GetString(27) : null,
                WeightKg = reader.FieldCount > 28 && !reader.IsDBNull(28) ? reader.GetDouble(28) : null,
                MinQuantity = reader.FieldCount > 29 && !reader.IsDBNull(29) ? reader.GetDouble(29) : null,
            });
        }

        return list;
    }

    /// <summary>Готовит INSERT-команду (с ON CONFLICT-апдейтом на 13 полей) один раз на весь
    /// цикл синхронизации — раньше она пересобиралась и заново парсилась на КАЖДЫЙ товар, что
    /// на паре десятков позиций незаметно, а при синхронизации каталога в тысячи товаров
    /// (сеть с 15000+ SKU) выливалось в тысячи лишних SQL-компиляций подряд внутри одной
    /// транзакции. ExecuteUpsert ниже только подставляет значения параметров на каждую
    /// запись — SQLite переиспользует уже подготовленный stmt.</summary>
    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Products (
                id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                image_url, category, brand, purchase_price, piece_option_json, plu, hotkey_group, is_bundle, article,
                bundle_items_json, alternate_barcodes, alternate_barcode_variants,
                intake_date, product_code, kind, description, wholesale_price, IsWeight,
                discount_percent, markup_percent, country, weight_kg, min_quantity
            ) VALUES (
                @id, @name, @price, @barcode, @stock, @unit, @is_favorite, @must_weigh,
                @image_url, @category, @brand, @purchase_price, @piece_option_json, @plu, @hotkey_group, @is_bundle, @article,
                @bundle_items_json, @alternate_barcodes, @alternate_barcode_variants,
                @intake_date, @product_code, @kind, @description, @wholesale_price, @IsWeight,
                @discount_percent, @markup_percent, @country, @weight_kg, @min_quantity
            )
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                price = excluded.price,
                barcode = excluded.barcode,
                stock = excluded.stock,
                unit = excluded.unit,
                is_favorite = excluded.is_favorite,
                must_weigh = excluded.must_weigh,
                image_url = excluded.image_url,
                category = excluded.category,
                brand = excluded.brand,
                purchase_price = excluded.purchase_price,
                piece_option_json = excluded.piece_option_json,
                plu = excluded.plu,
                hotkey_group = excluded.hotkey_group,
                is_bundle = excluded.is_bundle,
                article = excluded.article,
                bundle_items_json = excluded.bundle_items_json,
                alternate_barcodes = excluded.alternate_barcodes,
                alternate_barcode_variants = excluded.alternate_barcode_variants,
                product_code = excluded.product_code,
                kind = excluded.kind,
                description = excluded.description,
                wholesale_price = excluded.wholesale_price,
                -- 2026-11-01 (is_weight): запись без признака (лента/остаток) прежнее значение не затирает
                IsWeight = COALESCE(excluded.IsWeight, Products.IsWeight),
                -- 2026-11-06 (В12, Б-5, Б-8): скидка/наценка/страна/вес/мин. остаток — запись без значения (NULL) прежнее не затирает
                discount_percent = COALESCE(excluded.discount_percent, Products.discount_percent),
                markup_percent = COALESCE(excluded.markup_percent, Products.markup_percent),
                country = COALESCE(excluded.country, Products.country),
                weight_kg = COALESCE(excluded.weight_kg, Products.weight_kg),
                min_quantity = COALESCE(excluded.min_quantity, Products.min_quantity),
                intake_date = CASE
                    WHEN Products.intake_date IS NULL AND excluded.stock > 0 THEN @today
                    ELSE Products.intake_date
                END;
            """;
        command.Parameters.AddWithValue("@id", "");
        command.Parameters.AddWithValue("@name", "");
        command.Parameters.AddWithValue("@price", 0d);
        command.Parameters.AddWithValue("@barcode", DBNull.Value);
        command.Parameters.AddWithValue("@stock", 0d);
        command.Parameters.AddWithValue("@unit", "");
        command.Parameters.AddWithValue("@is_favorite", 0);
        command.Parameters.AddWithValue("@must_weigh", 0);
        command.Parameters.AddWithValue("@image_url", DBNull.Value);
        command.Parameters.AddWithValue("@category", DBNull.Value);
        command.Parameters.AddWithValue("@brand", DBNull.Value);
        command.Parameters.AddWithValue("@purchase_price", 0d);
        command.Parameters.AddWithValue("@piece_option_json", DBNull.Value);
        command.Parameters.AddWithValue("@plu", DBNull.Value);
        command.Parameters.AddWithValue("@hotkey_group", DBNull.Value);
        command.Parameters.AddWithValue("@is_bundle", 0);
        command.Parameters.AddWithValue("@article", DBNull.Value);
        command.Parameters.AddWithValue("@bundle_items_json", DBNull.Value);
        command.Parameters.AddWithValue("@alternate_barcodes", DBNull.Value);
        command.Parameters.AddWithValue("@alternate_barcode_variants", DBNull.Value);
        command.Parameters.AddWithValue("@product_code", DBNull.Value);
        command.Parameters.AddWithValue("@kind", DBNull.Value);
        command.Parameters.AddWithValue("@description", DBNull.Value);
        command.Parameters.AddWithValue("@wholesale_price", 0d);
        command.Parameters.AddWithValue("@IsWeight", DBNull.Value);
        command.Parameters.AddWithValue("@discount_percent", DBNull.Value);
        command.Parameters.AddWithValue("@markup_percent", DBNull.Value);
        command.Parameters.AddWithValue("@country", DBNull.Value);
        command.Parameters.AddWithValue("@weight_kg", DBNull.Value);
        command.Parameters.AddWithValue("@min_quantity", DBNull.Value);
        command.Parameters.AddWithValue("@intake_date", DBNull.Value);
        command.Parameters.AddWithValue("@today", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return command;
    }

    private static void ExecuteUpsert(SqliteCommand command, LocalProductRecord record)
    {
        command.Parameters["@id"].Value = record.Id;
        command.Parameters["@name"].Value = record.Name;
        command.Parameters["@price"].Value = record.Price;
        command.Parameters["@barcode"].Value = (object?)record.Barcode ?? DBNull.Value;
        command.Parameters["@stock"].Value = record.Stock;
        command.Parameters["@unit"].Value = record.Unit;
        command.Parameters["@is_favorite"].Value = record.IsFavorite ? 1 : 0;
        command.Parameters["@must_weigh"].Value = record.MustWeigh ? 1 : 0;
        command.Parameters["@image_url"].Value = (object?)record.ImageUrl ?? DBNull.Value;
        command.Parameters["@category"].Value = (object?)record.Category ?? DBNull.Value;
        command.Parameters["@brand"].Value = (object?)record.Brand ?? DBNull.Value;
        command.Parameters["@purchase_price"].Value = record.PurchasePrice;
        command.Parameters["@piece_option_json"].Value = (object?)record.PieceOptionJson ?? DBNull.Value;
        command.Parameters["@plu"].Value = (object?)record.Plu ?? DBNull.Value;
        command.Parameters["@hotkey_group"].Value = (object?)record.HotkeyGroup ?? DBNull.Value;
        command.Parameters["@is_bundle"].Value = record.IsBundle ? 1 : 0;
        command.Parameters["@article"].Value = (object?)record.Article ?? DBNull.Value;
        command.Parameters["@bundle_items_json"].Value = (object?)record.BundleItemsJson ?? DBNull.Value;
        command.Parameters["@alternate_barcodes"].Value = (object?)record.AlternateBarcodesRaw ?? DBNull.Value;
        command.Parameters["@alternate_barcode_variants"].Value = (object?)record.AlternateBarcodeVariantsJson ?? DBNull.Value;
        command.Parameters["@product_code"].Value = (object?)record.ProductCode ?? DBNull.Value;
        command.Parameters["@kind"].Value = (object?)record.Kind ?? DBNull.Value;
        command.Parameters["@description"].Value = string.IsNullOrWhiteSpace(record.Description) ? DBNull.Value : record.Description;
        command.Parameters["@wholesale_price"].Value = record.WholesalePrice;
        command.Parameters["@IsWeight"].Value = record.ServerIsWeight is { } serverWeight ? (serverWeight ? 1 : 0) : DBNull.Value;
        command.Parameters["@discount_percent"].Value = record.DiscountPercent;
        command.Parameters["@markup_percent"].Value = record.MarkupPercent;
        command.Parameters["@country"].Value = string.IsNullOrWhiteSpace(record.Country) ? DBNull.Value : record.Country;
        command.Parameters["@weight_kg"].Value = (object?)record.WeightKg ?? DBNull.Value;
        command.Parameters["@min_quantity"].Value = (object?)record.MinQuantity ?? DBNull.Value;
        // Только для ветки INSERT (совсем новый товар) — для уже существующих строк
        // реальное решение принимает CASE в ON CONFLICT DO UPDATE (см. CreateUpsertCommand).
        command.Parameters["@intake_date"].Value = record.Stock > 0
            ? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DBNull.Value;
        command.ExecuteNonQuery();
    }

    private static LocalProductRecord ReadRecord(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Price = reader.GetDouble(2),
            Barcode = reader.IsDBNull(3) ? null : reader.GetString(3),
            Stock = reader.GetDouble(4),
            Unit = reader.GetString(5),
            IsFavorite = reader.GetInt64(6) == 1,
            MustWeigh = reader.GetInt64(7) == 1,
            ImageUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
            Category = reader.IsDBNull(9) ? null : reader.GetString(9),
            Brand = reader.IsDBNull(10) ? null : reader.GetString(10),
            PurchasePrice = reader.GetDouble(11),
            PieceOptionJson = reader.IsDBNull(12) ? null : reader.GetString(12),
            Plu = reader.IsDBNull(13) ? null : reader.GetInt32(13),
            // Выборки по одному товару и «избранного» просят все колонки, а до 2026-09-24 здесь
            // читались только первые 14: такой товар терял горячую клавишу, комплект, артикул
            // и вид. Хвост читаем, только если он есть в выборке.
            HotkeyGroup = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
            IsBundle = reader.FieldCount > 15 && !reader.IsDBNull(15) && reader.GetInt64(15) == 1,
            Article = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : null,
            BundleItemsJson = reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetString(17) : null,
            AlternateBarcodesRaw = reader.FieldCount > 18 && !reader.IsDBNull(18) ? reader.GetString(18) : null,
            AlternateBarcodeVariantsJson = reader.FieldCount > 19 && !reader.IsDBNull(19) ? reader.GetString(19) : null,
            ProductCode = reader.FieldCount > 20 && !reader.IsDBNull(20) ? reader.GetString(20) : null,
            Kind = reader.FieldCount > 21 && !reader.IsDBNull(21) ? reader.GetString(21) : null,
            Description = reader.FieldCount > 22 && !reader.IsDBNull(22) ? reader.GetString(22) : null,
            WholesalePrice = reader.FieldCount > 23 && !reader.IsDBNull(23) ? reader.GetDouble(23) : 0,
            ServerIsWeight = reader.FieldCount > 24 && !reader.IsDBNull(24) ? reader.GetInt64(24) == 1 : null,
            DiscountPercent = reader.FieldCount > 25 && !reader.IsDBNull(25) ? reader.GetDouble(25) : 0,
            MarkupPercent = reader.FieldCount > 26 && !reader.IsDBNull(26) ? reader.GetDouble(26) : 0,
            Country = reader.FieldCount > 27 && !reader.IsDBNull(27) ? reader.GetString(27) : null,
            WeightKg = reader.FieldCount > 28 && !reader.IsDBNull(28) ? reader.GetDouble(28) : null,
            MinQuantity = reader.FieldCount > 29 && !reader.IsDBNull(29) ? reader.GetDouble(29) : null,
        };

    private static Product ToProduct(LocalProductRecord record) =>
        new()
        {
            Id = record.Id,
            Name = record.Name,
            Barcode = record.Barcode,
            Price = (decimal)record.Price,
            Quantity = record.Stock,
            IsWeight = record.MustWeigh,
            ImageUrl = record.ImageUrl,
            Category = record.Category,
            Brand = record.Brand,
            Unit = record.Unit,
            IsFavorite = record.IsFavorite,
        };

    private static Product TileToProduct(CatalogProductTileVm tile) => ToProduct(FromTileVm(tile));

    private static CatalogProductTileVm? ToTileVm(LocalProductRecord record)
    {
        var priceLine = $"{record.Price.ToString("0.00", CultureInfo.InvariantCulture)} сом";
        var vm = new CatalogProductTileVm(record.Id, record.Name, priceLine, record.MustWeigh, record.ImageUrl)
        {
            Barcode = record.Barcode,
            Category = record.Category,
            Brand = record.Brand,
            Unit = record.Unit,
            IsFavorite = record.IsFavorite,
            PurchasePrice = record.PurchasePrice,
            WholesalePrice = record.WholesalePrice,
            ServerIsWeight = record.ServerIsWeight,
            DiscountPercent = record.DiscountPercent, // 2026-11-06 (В12, Б-5): скидка/наценка/страна/вес больше не пропадают у плиток из базы
            MarkupPercent = record.MarkupPercent,
            Country = record.Country,
            WeightKg = record.WeightKg,
            MinQuantity = record.MinQuantity,
            PieceOption = DeserializePieceOption(record.PieceOptionJson),
            Plu = record.Plu,
            HotkeyGroup = record.HotkeyGroup,
            IsBundle = record.IsBundle,
            Kind = record.Kind,
            Article = record.Article,
            ProductCode = record.ProductCode,
            Description = record.Description,
            BundleItems = DeserializeBundleItems(record.BundleItemsJson),
            AlternateBarcodesRaw = record.AlternateBarcodesRaw,
            AlternateBarcodeVariants = DeserializeAlternateBarcodeVariants(record.AlternateBarcodeVariantsJson)
        };
        StockSyncService.ApplyQuantityToTile(vm, record.Stock, record.MustWeigh);
        return ProductUnitNormalizer.TryPrepareCatalogTile(vm) ? vm : null;
    }

    private static ProductPackageOption? DeserializePieceOption(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ProductPackageOption>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<BundleComponent>? DeserializeBundleItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<BundleComponent>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<AlternateBarcodeVariant>? DeserializeAlternateBarcodeVariants(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<AlternateBarcodeVariant>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LocalProductRecord FromTileVm(CatalogProductTileVm vm)
    {
        var kind = ProductUnitNormalizer.Classify(vm.Unit, vm.MustWeigh);
        return new LocalProductRecord
        {
            Id = vm.Id,
            Name = vm.Title,
            Price = ParsePrice(vm.PriceLine),
            Barcode = vm.Barcode,
            Stock = vm.Quantity,
            Unit = ProductUnitNormalizer.StorageUnit(vm.Unit, kind),
            IsFavorite = vm.IsFavorite,
            MustWeigh = kind == ProductUnitKind.Kilogram,
            ImageUrl = vm.ImageUrl,
            Category = vm.Category,
            Brand = vm.Brand,
            PurchasePrice = vm.PurchasePrice,
            WholesalePrice = vm.WholesalePrice,
            ServerIsWeight = vm.ServerIsWeight,
            DiscountPercent = vm.DiscountPercent,
            MarkupPercent = vm.MarkupPercent,
            Country = vm.Country,
            WeightKg = vm.WeightKg,
            MinQuantity = vm.MinQuantity,
            PieceOptionJson = vm.PieceOption is null ? null : JsonSerializer.Serialize(vm.PieceOption),
            Plu = vm.Plu,
            HotkeyGroup = vm.HotkeyGroup,
            IsBundle = vm.IsBundle,
            Kind = vm.Kind,
            Article = vm.Article,
            ProductCode = vm.ProductCode,
            Description = vm.Description,
            BundleItemsJson = vm.BundleItems is null or { Count: 0 } ? null : JsonSerializer.Serialize(vm.BundleItems),
            AlternateBarcodesRaw = vm.AlternateBarcodesRaw,
            AlternateBarcodeVariantsJson = vm.AlternateBarcodeVariants is null or { Count: 0 } ? null : JsonSerializer.Serialize(vm.AlternateBarcodeVariants)
        };
    }

    private static double ParsePrice(string priceLine)
    {
        if (string.IsNullOrWhiteSpace(priceLine))
            return 0;

        var digits = new string(priceLine
            .TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ',')
            .ToArray())
            .Replace(',', '.');

        return double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
            ? price
            : 0;
    }

    private static void MigrateLegacyDataIfNeeded(SqliteConnection connection)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM Products;";
        if (Convert.ToInt32(countCommand.ExecuteScalar()) > 0)
            return;

        if (!File.Exists(LegacyCachePath))
            return;

        try
        {
            var json = File.ReadAllText(LegacyCachePath);
            var legacyTiles = JsonSerializer.Deserialize<List<CatalogProductTileVm>>(json);
            if (legacyTiles == null || legacyTiles.Count == 0)
                return;

            var favoriteIds = LoadLegacyFavoriteIds();
            using var transaction = connection.BeginTransaction();
            using (var upsertCommand = CreateUpsertCommand(connection, transaction))
            {
                foreach (var vm in legacyTiles)
                {
                    if (favoriteIds.Contains(vm.Id))
                        vm.IsFavorite = true;
                    ExecuteUpsert(upsertCommand, FromTileVm(vm));
                }
            }
            transaction.Commit();

            if (File.Exists(LegacyCachePath))
            {
                var lastWrite = File.GetLastWriteTimeUtc(LegacyCachePath);
                using var metaCommand = connection.CreateCommand();
                metaCommand.CommandText = """
                    INSERT INTO catalog_meta(key, value) VALUES('last_sync_utc', @value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                metaCommand.Parameters.AddWithValue("@value", lastWrite.ToString("O", CultureInfo.InvariantCulture));
                metaCommand.ExecuteNonQuery();
            }
        }
        catch
        {
            // миграция не критична — каталог подтянется по кнопке «Обновить»
        }
    }

    private static HashSet<string> LoadLegacyFavoriteIds()
    {
        try
        {
            if (!File.Exists(LegacyFavoritesPath))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(LegacyFavoritesPath);
            return JsonSerializer.Deserialize<HashSet<string>>(json)
                   ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
