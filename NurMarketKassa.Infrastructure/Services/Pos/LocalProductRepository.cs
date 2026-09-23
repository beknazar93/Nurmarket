using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;

#nullable enable

namespace NurMarketKassa.Services;

public sealed class LocalProductRepository
{
    private static readonly string DataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    private static string DbPath => DatabaseService.Instance.DatabasePath;
    private static readonly string LegacyCachePath = Path.Combine(DataDirectory, "products_cache.json");
    private static readonly string LegacyFavoritesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "favorites.json");

    private static LocalProductRepository? _instance;
    public static LocalProductRepository Instance => _instance ??= new LocalProductRepository();

    private readonly ReaderWriterLockSlim _cacheLock = new(LockRecursionPolicy.NoRecursion);
    private readonly object _warmUpGate = new();

    private Dictionary<string, CatalogProductTileVm> _barcodeCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CatalogProductTileVm> _skuCache = new(StringComparer.OrdinalIgnoreCase);
    private List<CatalogProductTileVm> _allProductsCache = new();
    private List<CachedProductRow> _searchRows = new();
    private volatile bool _cacheReady;
    private Task? _warmUpTask;

    private LocalProductRepository()
    {
    }

    public bool IsCacheReady => _cacheReady;

    /// <summary>Полностью сбрасывает и пересобирает in-memory кэш из локальной БД (после синхронизации).</summary>
    public void ResetCache()
    {
        lock (_warmUpGate)
        {
            _warmUpTask = null;
            _cacheReady = false;
        }

        RebuildCacheFromDatabase();
    }

    /// <summary>Полная очистка локальной БД каталога и in-memory кэша (смена пользователя).</summary>
    public void ClearAll()
    {
        lock (_warmUpGate)
        {
            _warmUpTask = null;
            _cacheReady = false;
        }

        EnsureSchema();
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var deleteProducts = connection.CreateCommand())
            {
                deleteProducts.Transaction = transaction;
                deleteProducts.CommandText = "DELETE FROM Products;";
                deleteProducts.ExecuteNonQuery();
            }

            using (var deleteMeta = connection.CreateCommand())
            {
                deleteMeta.Transaction = transaction;
                deleteMeta.CommandText = "DELETE FROM catalog_meta;";
                deleteMeta.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        _cacheLock.EnterWriteLock();
        try
        {
            _barcodeCache = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
            _skuCache = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
            _allProductsCache = new List<CatalogProductTileVm>();
            _searchRows = new List<CachedProductRow>();
            _cacheReady = true;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }

        PosLogger.Log("CATALOG local database cleared", "CATALOG");
    }

    /// <summary>Асинхронный прогрев кэша при старте приложения — один раз читает ВСЕ товары из SQLite.</summary>
    public Task WarmUpCacheAsync(CancellationToken cancellationToken = default)
    {
        if (_cacheReady)
            return Task.CompletedTask;

        lock (_warmUpGate)
        {
            if (_warmUpTask != null)
                return _warmUpTask;

            _warmUpTask = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RebuildCacheFromDatabase();
            }, cancellationToken);

            return _warmUpTask;
        }
    }

    /// <summary>Обратная совместимость.</summary>
    public void InvalidateMemoryIndex() => ResetCache();

    public async Task EnsureCacheReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_cacheReady)
            return;

        await WarmUpCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    public void EnsureSchema()
    {
        DatabaseService.Instance.EnsureSchema();
        Directory.CreateDirectory(DataDirectory);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Products (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                price REAL NOT NULL DEFAULT 0,
                barcode TEXT,
                stock REAL NOT NULL DEFAULT 0,
                unit TEXT NOT NULL DEFAULT 'шт',
                is_favorite INTEGER NOT NULL DEFAULT 0,
                must_weigh INTEGER NOT NULL DEFAULT 0,
                image_url TEXT,
                category TEXT,
                brand TEXT,
                purchase_price REAL NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS catalog_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_Products_barcode ON Products(barcode);
            CREATE INDEX IF NOT EXISTS idx_Products_must_weigh ON Products(must_weigh);
            CREATE INDEX IF NOT EXISTS idx_Products_name ON Products(name COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();

        MigrateLegacyDataIfNeeded(connection);
    }

    public IReadOnlyList<CatalogProductTileVm> LoadAllTiles()
    {
        EnsureCacheReady();
        _cacheLock.EnterReadLock();
        try
        {
            return _allProductsCache.ToArray();
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }
    }

    public CatalogProductTileVm? TryGetTileByBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode) || !EnsureCacheReady())
            return null;

        var key = barcode.Trim();
        _cacheLock.EnterReadLock();
        try
        {
            return _barcodeCache.TryGetValue(key, out var tile) ? tile : null;
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }
    }

    public CatalogProductTileVm? TryGetTileBySku(string sku) =>
        TryGetTileById(sku);

    public CatalogProductTileVm? TryGetTileById(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !EnsureCacheReady())
            return null;

        var key = id.Trim();
        _cacheLock.EnterReadLock();
        try
        {
            return _skuCache.TryGetValue(key, out var tile) ? tile : null;
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }
    }

    /// <summary>Полный текстовый поиск по 100% кэша (без раннего обрыва), с пагинацией результата.</summary>
    public IReadOnlyList<CatalogProductTileVm> SearchFullCatalogText(
        string query,
        int offset = 0,
        int limit = 30,
        bool? mustWeigh = null,
        FilterCriteria? criteria = null)
    {
        var (items, _) = SearchCache(query, mustWeigh, criteria, offset, limit);
        return items;
    }

    /// <summary>Все совпадения по тексту — полный проход по кэшу (для скоринга в Lookup).</summary>
    public IReadOnlyList<CatalogProductTileVm> SearchAllMatches(
        string query,
        bool? mustWeigh = null,
        FilterCriteria? criteria = null)
    {
        if (!EnsureCacheReady())
            return Array.Empty<CatalogProductTileVm>();

        var normalized = NormalizeSearchTerm(query);
        if (normalized == null)
            return Array.Empty<CatalogProductTileVm>();

        var matches = new List<CatalogProductTileVm>();

        _cacheLock.EnterReadLock();
        try
        {
            if (_barcodeCache.TryGetValue(normalized, out var exactBarcode)
                && MatchesRowFilters(CachedProductRow.From(exactBarcode), mustWeigh, criteria))
                return [exactBarcode];

            if (_skuCache.TryGetValue(normalized, out var exactSku)
                && MatchesRowFilters(CachedProductRow.From(exactSku), mustWeigh, criteria))
                return [exactSku];

            foreach (var row in _searchRows)
            {
                if (!MatchesRowFilters(row, mustWeigh, criteria))
                    continue;

                if (!row.MatchesText(normalized))
                    continue;

                matches.Add(row.Tile);
            }
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }

        return matches;
    }

    public HashSet<string> GetFavoriteIds()
    {
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM Products WHERE is_favorite = 1;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            favorites.Add(reader.GetString(0));
        return favorites;
    }

    public void SetFavorite(string id, bool isFavorite)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Products SET is_favorite = @favorite WHERE id = @id;";
        command.Parameters.AddWithValue("@favorite", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
        PatchFavoriteInCache(id, isFavorite);
    }

    public void UpsertFromTiles(IEnumerable<CatalogProductTileVm> products)
    {
        var favoriteIds = GetFavoriteIds();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var vm in products)
        {
            ProductUnitNormalizer.ApplyToTile(vm);
            var record = FromTileVm(vm);
            if (favoriteIds.Contains(record.Id))
                record.IsFavorite = true;

            vm.IsFavorite = record.IsFavorite;
            UpsertRecord(connection, transaction, record);
        }

        transaction.Commit();
        ResetCache();
    }

    private void RebuildCacheFromDatabase()
    {
        var records = LoadAllRecords();
        var tiles = records
            .Select(ToTileVm)
            .Where(vm => vm != null)
            .Cast<CatalogProductTileVm>()
            .ToList();
        BuildCache(tiles);
    }

    private void BuildCache(IReadOnlyList<CatalogProductTileVm> tiles)
    {
        var barcodeCache = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        var skuCache = new Dictionary<string, CatalogProductTileVm>(tiles.Count, StringComparer.OrdinalIgnoreCase);
        var allProducts = new List<CatalogProductTileVm>(tiles.Count);
        var searchRows = new List<CachedProductRow>(tiles.Count);

        foreach (var tile in tiles)
        {
            allProducts.Add(tile);
            skuCache[tile.Id.Trim()] = tile;

            var barcode = tile.Barcode?.Trim();
            if (!string.IsNullOrEmpty(barcode))
                barcodeCache[barcode] = tile;

            searchRows.Add(CachedProductRow.From(tile));
        }

        searchRows.Sort(static (a, b) =>
            string.Compare(a.SortKey, b.SortKey, StringComparison.OrdinalIgnoreCase));

        _cacheLock.EnterWriteLock();
        try
        {
            _barcodeCache = barcodeCache;
            _skuCache = skuCache;
            _allProductsCache = allProducts;
            _searchRows = searchRows;
            _cacheReady = true;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }

        PosLogger.Log($"CATALOG cache ready: {allProducts.Count} products", "CATALOG");
    }

    private bool EnsureCacheReady()
    {
        if (_cacheReady)
            return true;

        RebuildCacheFromDatabase();
        return _cacheReady;
    }

    private void PatchFavoriteInCache(string id, bool isFavorite)
    {
        if (!_cacheReady)
            return;

        _cacheLock.EnterWriteLock();
        try
        {
            if (_skuCache.TryGetValue(id, out var tile))
                tile.IsFavorite = isFavorite;

            var index = _searchRows.FindIndex(r => string.Equals(r.Tile.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                _searchRows[index] = CachedProductRow.From(_searchRows[index].Tile);
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    private void PatchStockInCache(string id, double stock, bool mustWeigh)
    {
        if (!_cacheReady)
            return;

        _cacheLock.EnterWriteLock();
        try
        {
            if (!_skuCache.TryGetValue(id, out var tile))
                return;

            StockSyncService.ApplyQuantityToTile(tile, stock, mustWeigh);
            var index = _searchRows.FindIndex(r => string.Equals(r.Tile.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                _searchRows[index] = CachedProductRow.From(tile);
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }

    public (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) SearchCache(
        string? searchText,
        bool? mustWeigh,
        FilterCriteria? criteria,
        int offset,
        int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        if (!EnsureCacheReady())
            return (Array.Empty<CatalogProductTileVm>(), false);

        var normalized = NormalizeSearchTerm(searchText) ?? NormalizeSearchTerm(criteria?.SearchQuery);

        _cacheLock.EnterReadLock();
        try
        {
            if (normalized != null)
            {
                if (_barcodeCache.TryGetValue(normalized, out var exactBarcode)
                    && MatchesRowFilters(CachedProductRow.From(exactBarcode), mustWeigh, criteria))
                    return PaginateSingle(exactBarcode, offset, limit);

                if (_skuCache.TryGetValue(normalized, out var exactSku)
                    && MatchesRowFilters(CachedProductRow.From(exactSku), mustWeigh, criteria))
                    return PaginateSingle(exactSku, offset, limit);
            }

            var matches = new List<CatalogProductTileVm>();

            foreach (var row in _searchRows)
            {
                if (!MatchesRowFilters(row, mustWeigh, criteria))
                    continue;

                if (normalized != null && !row.MatchesText(normalized))
                    continue;

                matches.Add(row.Tile);
            }

            var page = matches.Skip(offset).Take(limit).ToArray();
            return (page, matches.Count > offset + limit);
        }
        finally
        {
            _cacheLock.ExitReadLock();
        }
    }

    private static (IReadOnlyList<CatalogProductTileVm> Items, bool HasMore) PaginateSingle(
        CatalogProductTileVm tile,
        int offset,
        int limit)
    {
        if (offset > 0)
            return (Array.Empty<CatalogProductTileVm>(), false);

        return ([tile], false);
    }

    private static bool MatchesRowFilters(CachedProductRow row, bool? mustWeigh, FilterCriteria? criteria)
    {
        if (mustWeigh is { } weighFilter && row.MustWeigh != weighFilter)
            return false;

        if (criteria == null)
            return true;

        if (criteria.PriceMin.HasValue && row.Price < criteria.PriceMin.Value)
            return false;
        if (criteria.PriceMax.HasValue && row.Price > criteria.PriceMax.Value)
            return false;
        if (!string.IsNullOrWhiteSpace(criteria.Category)
            && !string.Equals(row.Category, criteria.Category.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(criteria.Brand)
            && !string.Equals(row.Brand, criteria.Brand.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (criteria.OnlyFavorite && !row.IsFavorite)
            return false;
        if (criteria.OnlyWeight && !row.MustWeigh)
            return false;
        if (criteria.OnlyPiece && row.MustWeigh)
            return false;
        if (criteria.OnlyInStock && row.Quantity <= 0)
            return false;

        return true;
    }

    private static string? NormalizeSearchTerm(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToLowerInvariant();

    private sealed class CachedProductRow
    {
        public required CatalogProductTileVm Tile { get; init; }
        public required string SortKey { get; init; }
        public required string NormalizedName { get; init; }
        public required string NormalizedSku { get; init; }
        public required string NormalizedBarcode { get; init; }
        public required bool MustWeigh { get; init; }
        public required double Price { get; init; }
        public required double Quantity { get; init; }
        public required bool IsFavorite { get; init; }
        public string? Category { get; init; }
        public string? Brand { get; init; }

        public static CachedProductRow From(CatalogProductTileVm tile) =>
            new()
            {
                Tile = tile,
                SortKey = tile.Title.Trim(),
                NormalizedName = tile.Title.Trim().ToLowerInvariant(),
                NormalizedSku = tile.Id.Trim().ToLowerInvariant(),
                NormalizedBarcode = tile.Barcode?.Trim().ToLowerInvariant() ?? string.Empty,
                MustWeigh = tile.MustWeigh,
                Price = ParsePrice(tile.PriceLine),
                Quantity = tile.Quantity,
                IsFavorite = tile.IsFavorite,
                Category = tile.Category,
                Brand = tile.Brand,
            };

        public bool MatchesText(string normalizedQuery)
        {
            if (NormalizedBarcode.Length > 0
                && (NormalizedBarcode == normalizedQuery
                    || NormalizedBarcode.StartsWith(normalizedQuery, StringComparison.Ordinal)
                    || NormalizedBarcode.Contains(normalizedQuery, StringComparison.Ordinal)))
                return true;

            if (NormalizedSku == normalizedQuery
                || NormalizedSku.StartsWith(normalizedQuery, StringComparison.Ordinal)
                || NormalizedSku.Contains(normalizedQuery, StringComparison.Ordinal))
                return true;

            return NormalizedName.StartsWith(normalizedQuery, StringComparison.Ordinal)
                   || NormalizedName.Contains(normalizedQuery, StringComparison.Ordinal);
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
    }

    public void UpdateStock(string id, double stock, bool mustWeigh)
    {
        var kind = mustWeigh ? ProductUnitKind.Kilogram : ProductUnitKind.Piece;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Products
            SET stock = @stock,
                unit = @unit,
                must_weigh = @must_weigh
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@stock", stock);
        command.Parameters.AddWithValue("@unit", ProductUnitNormalizer.DisplayUnit(kind));
        command.Parameters.AddWithValue("@must_weigh", mustWeigh ? 1 : 0);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
        PatchStockInCache(id, stock, mustWeigh);
    }

    public DateTime? GetLastSyncTime()
    {
        var value = GetMetaValue("last_sync_utc");
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    public void SetLastSyncTime(DateTime utcTime) => SetMetaValue("last_sync_utc", utcTime.ToString("O", CultureInfo.InvariantCulture));

    public string? GetCatalogVersionToken() => GetMetaValue("catalog_version");

    public void SetCatalogVersionToken(string token) => SetMetaValue("catalog_version", token);

    public string? GetMetaValue(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM catalog_meta WHERE key = @key LIMIT 1;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetMetaValue(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_meta(key, value) VALUES(@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>Полная синхронизация с подсчётом добавленных / изменённых / удалённых.</summary>
    public (int Added, int Changed, int Deleted) SyncReplaceAllWithDiff(IEnumerable<CatalogProductTileVm> products)
    {
        var favoriteIds = GetFavoriteIds();
        var existing = LoadAllRecords().ToDictionary(x => x.Id, x => Fingerprint(x), StringComparer.OrdinalIgnoreCase);
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

        var added = 0;
        var changed = 0;
        foreach (var (id, record) in incoming)
        {
            var fp = Fingerprint(record);
            if (!existing.ContainsKey(id))
                added++;
            else if (!string.Equals(existing[id], fp, StringComparison.Ordinal))
                changed++;
        }

        var deleted = existing.Keys.Count(id => !incoming.ContainsKey(id));

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
            foreach (var id in existing.Keys.Where(id => !incoming.ContainsKey(id)))
            {
                idParam.Value = id;
                deleteCmd.ExecuteNonQuery();
            }
        }

        foreach (var record in incoming.Values)
            UpsertRecord(connection, transaction, record);

        transaction.Commit();
        ResetCache();
        return (added, changed, deleted);
    }

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
            r.PurchasePrice.ToString("0.####", CultureInfo.InvariantCulture));

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
                   image_url, category, brand, purchase_price
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
                PurchasePrice = reader.GetDouble(11)
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
                   image_url, category, brand, purchase_price
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
                   image_url, category, brand, purchase_price
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
                   image_url, category, brand, purchase_price
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

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
    }

    private static List<LocalProductRecord> LoadAllRecords()
    {
        var list = new List<LocalProductRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price
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
                PurchasePrice = reader.GetDouble(11)
            });
        }

        return list;
    }

    private static void UpsertRecord(SqliteConnection connection, SqliteTransaction transaction, LocalProductRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Products (
                id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                image_url, category, brand, purchase_price
            ) VALUES (
                @id, @name, @price, @barcode, @stock, @unit, @is_favorite, @must_weigh,
                @image_url, @category, @brand, @purchase_price
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
                purchase_price = excluded.purchase_price;
            """;
        command.Parameters.AddWithValue("@id", record.Id);
        command.Parameters.AddWithValue("@name", record.Name);
        command.Parameters.AddWithValue("@price", record.Price);
        command.Parameters.AddWithValue("@barcode", (object?)record.Barcode ?? DBNull.Value);
        command.Parameters.AddWithValue("@stock", record.Stock);
        command.Parameters.AddWithValue("@unit", record.Unit);
        command.Parameters.AddWithValue("@is_favorite", record.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@must_weigh", record.MustWeigh ? 1 : 0);
        command.Parameters.AddWithValue("@image_url", (object?)record.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@category", (object?)record.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("@brand", (object?)record.Brand ?? DBNull.Value);
        command.Parameters.AddWithValue("@purchase_price", record.PurchasePrice);
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
            PurchasePrice = record.PurchasePrice
        };
        StockSyncService.ApplyQuantityToTile(vm, record.Stock, record.MustWeigh);
        return ProductUnitNormalizer.TryPrepareCatalogTile(vm) ? vm : null;
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
            Unit = ProductUnitNormalizer.DisplayUnit(kind),
            IsFavorite = vm.IsFavorite,
            MustWeigh = kind == ProductUnitKind.Kilogram,
            ImageUrl = vm.ImageUrl,
            Category = vm.Category,
            Brand = vm.Brand,
            PurchasePrice = vm.PurchasePrice
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
            foreach (var vm in legacyTiles)
            {
                if (favoriteIds.Contains(vm.Id))
                    vm.IsFavorite = true;
                UpsertRecord(connection, transaction, FromTileVm(vm));
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
