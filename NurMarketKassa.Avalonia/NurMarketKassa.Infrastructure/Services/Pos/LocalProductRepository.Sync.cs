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
    /// <summary>Полная синхронизация с подсчётом добавленных / изменённых / удалённых.</summary>
    /// <param name="deleteMissing">2026-11-06 (Б-9): false — «дописать пачку»: товары, которых нет в <paramref name="products"/>, НЕ удаляются (докачка каталога по пачкам
    /// страниц; лишнее удаляет <see cref="DeleteProductsNotIn"/> после последней пачки).</param>
    public (int Added, int Changed, int Deleted) SyncReplaceAllWithDiff(IEnumerable<CatalogProductTileVm> products, bool deleteMissing = true)
    {
        var favoriteIds = GetFavoriteIds();
        // 2026-11-06: В5 24.1 (шаг 18, п. 24) — для сравнения нужны только отпечатки: читаем строки потоком (без сортировки и без списка из 15 тыс. полных записей),
        // см. LoadAllFingerprints.
        var existing = LoadAllFingerprints();
        var incoming = new Dictionary<string, LocalProductRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var vm in products)
        {
            ProductUnitNormalizer.ApplyToTile(vm);
            var record = FromTileVm(vm);
            if (favoriteIds.Contains(record.Id))
            {
                record.IsFavorite = true;
                vm.IsFavorite = true;
            }

            incoming[record.Id] = record;
        }

        // Пишем только новые и изменившиеся записи (2026-09-07, оптимизация для слабых ПК):
        // раньше каждый sync (раз в 45 с) переписывал ВСЕ строки Products, даже когда diff был
        // added=0/changed=0 — лишняя запись в SQLite и рост WAL на каждом цикле. Отпечаток
        // (Fingerprint) теперь покрывает все поля записи, поэтому "не изменился" значит
        // действительно не изменился.
        var added = 0;
        var changed = 0;
        var toUpsert = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, record) in incoming)
        {
            var fp = Fingerprint(record);
            if (!existing.ContainsKey(id))
            {
                added++;
                toUpsert.Add(id);
            }
            else if (!string.Equals(existing[id], fp, StringComparison.Ordinal))
            {
                changed++;
                toUpsert.Add(id);
            }
        }

        var deletedIds = deleteMissing ? existing.Keys.Where(id => !incoming.ContainsKey(id)).ToList() : [];
        var deleted = deletedIds.Count;
        if (added == 0 && changed == 0 && deleted == 0)
            return (0, 0, 0);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (deleted > 0)
        {
            using var deleteCmd = connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM Products WHERE id = @id;";
            var idParam = deleteCmd.CreateParameter();
            idParam.ParameterName = "@id";
            deleteCmd.Parameters.Add(idParam);
            foreach (var id in deletedIds)
            {
                idParam.Value = id;
                deleteCmd.ExecuteNonQuery();
            }
        }

        if (toUpsert.Count > 0)
        {
            using var upsertCommand = CreateUpsertCommand(connection, transaction);
            foreach (var record in incoming.Values)
            {
                if (toUpsert.Contains(record.Id))
                    ExecuteUpsert(upsertCommand, record);
            }
        }

        transaction.Commit();
        // 2026-10-04, отчёт о производительности (п. 15): изменились остатки/цены нескольких товаров —
        // правим только их строки в кэше, а не пересобираем весь кэш из базы (15 000 товаров — ~0,24 с
        // чтения и разбора на каждую синхронизацию). Плитки неизменённых товаров остаются теми же
        // объектами — экран кассы меняет только изменённые. Новые товары и смена названия (порядок
        // списка по названию) — как раньше, полной пересборкой.
        if (!TryPatchCacheAfterSync(incoming, toUpsert, added, deletedIds))
            ResetCache();
        return (added, changed, deleted);
    }

    /// <summary>2026-11-06 (Б-9): после докачки каталога пачками — удалить из базы товары, которых сервер в полной загрузке не отдал (то, что раньше делал один
    /// <see cref="SyncReplaceAllWithDiff"/> в конце). Возвращает число удалённых.</summary>
    public int DeleteProductsNotIn(IReadOnlySet<string> keepIds)
    {
        var ids = new List<string>();
        using (var read = OpenConnection())
        using (var command = read.CreateCommand())
        {
            command.CommandText = "SELECT id FROM Products;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!keepIds.Contains(id))
                    ids.Add(id);
            }
        }

        if (ids.Count == 0)
            return 0;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var deleteCmd = connection.CreateCommand();
        deleteCmd.Transaction = transaction;
        deleteCmd.CommandText = "DELETE FROM Products WHERE id = @id;";
        var idParam = deleteCmd.CreateParameter();
        idParam.ParameterName = "@id";
        deleteCmd.Parameters.Add(idParam);
        foreach (var id in ids)
        {
            idParam.Value = id;
            deleteCmd.ExecuteNonQuery();
        }

        transaction.Commit();
        ResetCache();
        return ids.Count;
    }

    /// <summary>Сколько строк кэша правим точечно; больше — проще и не медленнее пересобрать целиком.</summary>
    private const int MaxRowsToPatch = 3000;

    /// <summary>2026-11-05 (Н-03): сколько НОВЫХ товаров вставляем в кэш точечно, не пересобирая его.</summary>
    private const int MaxNewRowsToPatch = 20;

    /// <summary>2026-10-04, п. 15: точечная правка кэша после синхронизации (см. SyncReplaceAllWithDiff).
    /// false — правка не применима (кэш не готов, есть новые товары, сменилось название, товара нет в
    /// кэше, кэш успели пересобрать) — вызывающий пересобирает кэш как раньше.</summary>
    private bool TryPatchCacheAfterSync(
        IReadOnlyDictionary<string, LocalProductRecord> incoming,
        IReadOnlyCollection<string> upserted,
        int added,
        IReadOnlyCollection<string> deletedIds)
    {
        // 2026-11-05 (аудит №3, Н-03): один-два новых товара больше не пересобирают весь кэш (15 тыс. товаров, ~1–2 с на каждое добавление) — вставляем их по названию; много новых сразу — как раньше.
        if (!_cacheReady || added > MaxNewRowsToPatch || upserted.Count + deletedIds.Count > MaxRowsToPatch)
            return false;

        try
        {
            List<CatalogProductTileVm> oldAll;
            List<CachedProductRow> oldRows;
            Dictionary<string, CatalogProductTileVm> oldSku;
            _cacheLock.EnterReadLock();
            try
            {
                oldAll = _allProductsCache;
                oldRows = _searchRows;
                oldSku = _skuCache;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }

            var replacements = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
            var newTiles = new List<CatalogProductTileVm>(); // 2026-11-05 (Н-03)
            foreach (var id in upserted)
            {
                if (!incoming.TryGetValue(id, out var record))
                    return false;
                if (!oldSku.TryGetValue(id.Trim(), out var current))
                {
                    // Новый товар: в кэше его ещё нет — готовим плитку, ниже вставим по названию.
                    if (string.IsNullOrWhiteSpace(record.Description) && record.Description is not null)
                        record.Description = null;
                    if (ToTileVm(record) is not { } newTile)
                        return false;
                    newTiles.Add(newTile);
                    continue;
                }
                // Кэш и строки поиска упорядочены по названию — новое название меняет порядок.
                if (!string.Equals(current.Title, record.Name, StringComparison.Ordinal))
                    return false;
                // Ровно то, что дала бы пересборка из базы: описание из одних пробелов база хранит как NULL.
                if (string.IsNullOrWhiteSpace(record.Description) && record.Description is not null)
                    record.Description = null;
                if (ToTileVm(record) is not { } tile)
                    return false;
                replacements[current.Id] = tile;
            }

            var deleted = new HashSet<string>(deletedIds, StringComparer.OrdinalIgnoreCase);
            var all = new List<CatalogProductTileVm>(oldAll.Count);
            foreach (var tile in oldAll)
            {
                if (deleted.Contains(tile.Id))
                    continue;
                all.Add(replacements.TryGetValue(tile.Id, out var replaced) ? replaced : tile);
            }

            var rows = new List<CachedProductRow>(oldRows.Count);
            foreach (var row in oldRows)
            {
                if (deleted.Contains(row.Tile.Id))
                    continue;
                rows.Add(replacements.TryGetValue(row.Tile.Id, out var replaced) ? CachedProductRow.From(replaced) : row);
            }

            // 2026-11-05 (Н-03): новые товары — на своё место по названию (список упорядочен по названию, как при полной пересборке).
            foreach (var inserted in newTiles)
            {
                var key = inserted.Title.Trim();
                var at = all.FindIndex(t => string.Compare(t.Title.Trim(), key, StringComparison.OrdinalIgnoreCase) > 0);
                all.Insert(at < 0 ? all.Count : at, inserted);
                var rowAt = rows.FindIndex(r => string.Compare(r.SortKey, key, StringComparison.OrdinalIgnoreCase) > 0);
                rows.Insert(rowAt < 0 ? rows.Count : rowAt, CachedProductRow.From(inserted));
            }

            var sku = new Dictionary<string, CatalogProductTileVm>(all.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var tile in all)
                sku[tile.Id.Trim()] = tile;
            var barcodes = BuildBarcodeIndex(all);

            _cacheLock.EnterWriteLock();
            try
            {
                // Пока строили, кэш пересобрали (ResetCache/ClearAll) — наша правка устарела.
                if (!ReferenceEquals(_allProductsCache, oldAll) || !_cacheReady)
                    return false;
                _barcodeCache = barcodes;
                _skuCache = sku;
                _allProductsCache = all;
                _searchRows = rows;
            }
            finally
            {
                _cacheLock.ExitWriteLock();
            }

            PosLogger.Log($"CATALOG cache patched: изменено {replacements.Count}, новых {newTiles.Count}, удалено {deleted.Count} (без пересборки, всего {all.Count})", "CATALOG");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG cache patch failed, полная пересборка: {ex.Message}", "CATALOG");
            return false;
        }
    }

    /// <summary>Отпечаток записи для diff-синхронизации. Покрывает ВСЕ поля LocalProductRecord,
    /// кроме IsFavorite (это локальный флаг, он подмешивается из таблицы избранного и на сервере
    /// не живёт). До 2026-09-07 не учитывались Unit/Plu/PieceOption/HotkeyGroup/IsBundle/Article —
    /// их изменение на сайте считалось "changed=0".</summary>
    private static string Fingerprint(LocalProductRecord r) =>
        string.Join("|",
            r.Name,
            r.Price.ToString("0.####", CultureInfo.InvariantCulture),
            r.Barcode ?? "",
            r.Stock.ToString("0.####", CultureInfo.InvariantCulture),
            r.Category ?? "",
            r.Brand ?? "",
            r.ImageUrl ?? "",
            r.MustWeigh ? "1" : "0",
            r.PurchasePrice.ToString("0.####", CultureInfo.InvariantCulture),
            r.Unit,
            r.Plu?.ToString(CultureInfo.InvariantCulture) ?? "",
            r.PieceOptionJson ?? "",
            r.HotkeyGroup ?? "",
            r.IsBundle ? "1" : "0",
            r.Article ?? "",
            r.BundleItemsJson ?? "",
            r.AlternateBarcodesRaw ?? "",
            r.AlternateBarcodeVariantsJson ?? "",
            r.ProductCode ?? "",
            r.Kind ?? "",
            r.Description ?? "",
            r.WholesalePrice.ToString("0.####", CultureInfo.InvariantCulture),
            r.ServerIsWeight is { } w ? (w ? "1" : "0") : "",
            // 2026-11-06 (В12, Б-5, Б-8): скидка/наценка/страна/вес/мин. остаток — смена одной скидки на сервере теперь считается изменением записи
            r.DiscountPercent.ToString("0.####", CultureInfo.InvariantCulture),
            r.MarkupPercent.ToString("0.####", CultureInfo.InvariantCulture),
            r.Country ?? "",
            r.WeightKg?.ToString("0.####", CultureInfo.InvariantCulture) ?? "",
            r.MinQuantity?.ToString("0.####", CultureInfo.InvariantCulture) ?? "");

    /// <summary>2026-11-06: В5 24.1 — «код товара → отпечаток» всего каталога. Читает строки потоком (запись → отпечаток → запись выбрасывается), без сортировки
    /// (порядок для сравнения не нужен; прежний ORDER BY name шёл по индексу с обращением к таблице на каждую строку). Кэш плиток (RebuildCacheFromDatabase)
    /// по-прежнему читает полные записи: описание и состав комплекта нужны карточке товара, печати и редактированию — убирать их из кэша без переделки карточки нельзя.</summary>
    private static Dictionary<string, string> LoadAllFingerprints()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu, hotkey_group, is_bundle, article,
                   bundle_items_json, alternate_barcodes, alternate_barcode_variants, product_code, kind, description, wholesale_price, IsWeight,
                   discount_percent, markup_percent, country, weight_kg, min_quantity
            FROM Products;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var record = ReadRecord(reader);
            map[record.Id] = Fingerprint(record);
        }

        return map;
    }

    public int CountProducts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Products;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<string> GetDistinctCategories()
    {
        return LoadDistinctValues("category");
    }

    public IReadOnlyList<string> GetDistinctBrands()
    {
        return LoadDistinctValues("brand");
    }

    public (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) SearchPaged(
        string? searchText,
        bool mustWeigh,
        int offset,
        int limit) =>
        SearchCache(searchText, mustWeigh, criteria: null, offset, limit);

    private (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) SearchPagedSql(
        string? searchText,
        bool mustWeigh,
        int offset,
        int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu
            FROM Products
            WHERE must_weigh = @must_weigh
            """);

        command.Parameters.AddWithValue("@must_weigh", mustWeigh ? 1 : 0);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            sql.Append(" AND (name LIKE @search OR IFNULL(barcode, '') LIKE @search OR IFNULL(id, '') LIKE @search)");
            command.Parameters.AddWithValue("@search", $"%{searchText.Trim()}%");
        }

        sql.Append(" ORDER BY name COLLATE NOCASE LIMIT @limit OFFSET @offset;");
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        command.CommandText = sql.ToString();

        var list = new List<CatalogProductTileVm>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var record = new LocalProductRecord
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
                Plu = reader.IsDBNull(13) ? null : reader.GetInt32(13)
            };
            var vm = ToTileVm(record);
            if (vm != null)
                list.Add(vm);
        }

        return (list, list.Count == limit);
    }

    public Task<Product?> GetByBarcodeAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return Task.FromResult<Product?>(null);

        if (EnsureCacheReady())
        {
            var tile = TryGetTileByBarcode(barcode);
            if (tile != null)
                return Task.FromResult<Product?>(TileToProduct(tile));
        }

        return GetByBarcodeSqlAsync(barcode);
    }

    private Task<Product?> GetByBarcodeSqlAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return Task.FromResult<Product?>(null);

        var cleanBarcode = barcode.Trim();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu
            FROM Products
            WHERE LOWER(IFNULL(barcode, '')) = LOWER(@barcode)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@barcode", cleanBarcode);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<Product?>(null);

        var record = ReadRecord(reader);
        return Task.FromResult<Product?>(ToProduct(record));
    }

    /// <summary>Весь каталог без разделения на весовые/штучные, с пагинацией SQL LIMIT/OFFSET.</summary>
    public (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) LoadAllPaged(
        int offset,
        int limit,
        string? searchText = null,
        FilterCriteria? criteria = null) =>
        SearchCache(searchText, mustWeigh: null, criteria, offset, limit);

    private (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) LoadAllPagedSql(
        int offset,
        int limit,
        string? searchText = null,
        FilterCriteria? criteria = null)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu
            FROM Products
            WHERE 1 = 1
            """);

        AppendFilterClauses(sql, command, criteria, searchText);
        sql.Append(" ORDER BY name COLLATE NOCASE LIMIT @limit OFFSET @offset;");
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        command.CommandText = sql.ToString();

        return ReadTileList(command);
    }

    private static (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) ReadTileList(SqliteCommand command)
    {
        var list = new List<CatalogProductTileVm>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var vm = ToTileVm(ReadRecord(reader));
            if (vm != null)
                list.Add(vm);
        }

        var limit = command.Parameters.Contains("@limit")
            ? Convert.ToInt32(command.Parameters["@limit"].Value)
            : list.Count;
        return (list, list.Count == limit);
    }

    public IReadOnlyList<CatalogProductTileVm> QueryFiltered(FilterCriteria criteria) =>
        SearchCache(criteria.SearchQuery, mustWeigh: null, criteria, offset: 0, limit: 10_000).Items;

    private IReadOnlyList<CatalogProductTileVm> QueryFilteredSql(FilterCriteria criteria)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu
            FROM Products
            WHERE 1 = 1
            """);

        AppendFilterClauses(sql, command, criteria, criteria.SearchQuery);
        sql.Append(" ORDER BY name COLLATE NOCASE;");
        command.CommandText = sql.ToString();

        return ReadTileList(command).Items;
    }

    private static void AppendFilterClauses(
        StringBuilder sql,
        SqliteCommand command,
        FilterCriteria? criteria,
        string? searchText)
    {
        var search = !string.IsNullOrWhiteSpace(searchText)
            ? searchText.Trim()
            : criteria?.SearchQuery?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            sql.Append(" AND (name LIKE @search OR IFNULL(barcode, '') LIKE @search OR IFNULL(id, '') LIKE @search)");
            command.Parameters.AddWithValue("@search", $"%{search}%");
        }

        if (criteria == null)
            return;

        if (criteria.PriceMin.HasValue)
        {
            sql.Append(" AND price >= @price_min");
            command.Parameters.AddWithValue("@price_min", criteria.PriceMin.Value);
        }

        if (criteria.PriceMax.HasValue)
        {
            sql.Append(" AND price <= @price_max");
            command.Parameters.AddWithValue("@price_max", criteria.PriceMax.Value);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Category))
        {
            sql.Append(" AND category = @category");
            command.Parameters.AddWithValue("@category", criteria.Category.Trim());
        }

        if (!string.IsNullOrWhiteSpace(criteria.Brand))
        {
            sql.Append(" AND brand = @brand");
            command.Parameters.AddWithValue("@brand", criteria.Brand.Trim());
        }

        if (criteria.OnlyFavorite)
            sql.Append(" AND is_favorite = 1");

        if (criteria.OnlyWeight)
            sql.Append(" AND must_weigh = 1");

        if (criteria.OnlyPiece)
            sql.Append(" AND must_weigh = 0");
    }

    private static List<string> LoadDistinctValues(string column)
    {
        var list = new List<string>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT {column}
            FROM Products
            WHERE {column} IS NOT NULL AND TRIM({column}) <> ''
            ORDER BY {column} COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(0));
        return list;
    }
}
