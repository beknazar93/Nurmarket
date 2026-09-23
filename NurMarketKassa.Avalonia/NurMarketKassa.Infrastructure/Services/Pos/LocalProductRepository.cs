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
    /// <summary>Единственный экземпляр.
    ///
    /// 2026-09-23: было `_instance ??= new LocalProductRepository()`. Оператор ??= не атомарен: два
    /// потока могли одновременно увидеть null и создать ДВА объекта, у каждого со своим
    /// замком на базу — после чего блокировка переставала что-либо сериализовать. На
    /// старте кассы это как раз и происходит: фоновый бэкап базы, резолв синглтона в
    /// контейнере и прогрев каталога стартуют одновременно. Самый вероятный неучтённый
    /// источник повторяющегося повреждения локальной базы.</summary>
    public static LocalProductRepository Instance => LazyInstance.Value;

    private static readonly Lazy<LocalProductRepository> LazyInstance =
        new(() => new LocalProductRepository(), LazyThreadSafetyMode.ExecutionAndPublication);

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
        // RebuildCacheFromDatabase выполняется ВНУТРИ того же лока, что и в EnsureCacheReady —
        // иначе фоновая пересинхронизация каталога и параллельный вызов с UI-потока (например,
        // сканирование штрих-кода сразу после синхронизации) независимо гоняли бы по SQLite
        // два полных прохода одновременно; на каталоге в 10-20 тыс. товаров это ощутимая
        // лишняя нагрузка, которая выглядит как зависание/краш кассы.
        lock (_warmUpGate)
        {
            _warmUpTask = null;
            _cacheReady = false;
            RebuildCacheFromDatabase();
        }
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
                purchase_price REAL NOT NULL DEFAULT 0,
                piece_option_json TEXT,
                plu INTEGER,
                hotkey_group TEXT,
                is_bundle INTEGER NOT NULL DEFAULT 0,
                article TEXT,
                bundle_items_json TEXT,
                alternate_barcodes TEXT
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

        // Существующие БД (созданные до появления поштучной продажи) не получат новую колонку
        // от CREATE TABLE IF NOT EXISTS — добавляем её отдельно, если её ещё нет.
        try
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Products ADD COLUMN piece_option_json TEXT;";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует — ожидаемо для БД, созданных этой версией схемы.
        }

        try
        {
            using var alterPlu = connection.CreateCommand();
            alterPlu.CommandText = "ALTER TABLE Products ADD COLUMN plu INTEGER;";
            alterPlu.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            using var alterHotkey = connection.CreateCommand();
            alterHotkey.CommandText = "ALTER TABLE Products ADD COLUMN hotkey_group TEXT;";
            alterHotkey.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            using var alterBundle = connection.CreateCommand();
            alterBundle.CommandText = "ALTER TABLE Products ADD COLUMN is_bundle INTEGER NOT NULL DEFAULT 0;";
            alterBundle.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            using var alterArticle = connection.CreateCommand();
            alterArticle.CommandText = "ALTER TABLE Products ADD COLUMN article TEXT;";
            alterArticle.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            using var alterBundleItems = connection.CreateCommand();
            alterBundleItems.CommandText = "ALTER TABLE Products ADD COLUMN bundle_items_json TEXT;";
            alterBundleItems.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            using var alterAltBarcodes = connection.CreateCommand();
            alterAltBarcodes.CommandText = "ALTER TABLE Products ADD COLUMN alternate_barcodes TEXT;";
            alterAltBarcodes.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            using var alterAltBarcodeVariants = connection.CreateCommand();
            alterAltBarcodeVariants.CommandText = "ALTER TABLE Products ADD COLUMN alternate_barcode_variants TEXT;";
            alterAltBarcodeVariants.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            // 2026-09-21: "Код товара" — отдельное от "article" поле сайта (см. CatalogProductTileVm.ProductCode).
            using var alterProductCode = connection.CreateCommand();
            alterProductCode.CommandText = "ALTER TABLE Products ADD COLUMN product_code TEXT;";
            alterProductCode.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

        try
        {
            // Дата, когда у товара впервые появился остаток > 0 (см. UpdateStock/SyncReplaceAllWithDiff) —
            // используется как "дата поступления" для срока годности (AI-фичи 2026-09-03, п.5).
            // Срок годности НЕ хранится отдельной колонкой — считается на лету из этой даты
            // + таблица категорий по умолчанию (см. ExpiryEstimator), чтобы не дублировать логику
            // категорийных сроков в нескольких местах записи стока.
            using var alterIntake = connection.CreateCommand();
            alterIntake.CommandText = "ALTER TABLE Products ADD COLUMN intake_date TEXT;";
            alterIntake.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Колонка уже существует.
        }

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

    /// <summary>Товар по штрихкоду.
    ///
    /// Сначала смотрим в готовый кэш — если он прогрет, это словарь и ответ мгновенный. Если
    /// НЕ прогрет, раньше здесь вызывался EnsureCacheReady(), а он блокирует вызывающий поток
    /// на полной пересборке кэша из базы. Путь достижим со сканирования штрихкода, то есть с
    /// UI-потока: касса замирала на всё время пересборки, а на каталоге в 20 тыс. товаров это
    /// заметная пауза ровно в тот момент, когда кассир пробивает товар.
    ///
    /// Поэтому непрогретый кэш больше не ждём: один товар достаём запросом к базе по
    /// индексу idx_products_barcode — это доли миллисекунды. Прогрев при этом запускается
    /// фоном, чтобы следующие обращения шли уже по словарю.</summary>
    public CatalogProductTileVm? TryGetTileByBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return null;

        var key = barcode.Trim();

        if (_cacheReady)
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (_barcodeCache.TryGetValue(key, out var cached))
                    return cached;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        var direct = LoadSingleTile("barcode", key);
        if (direct != null || _cacheReady)
            return direct;

        // Кэш ещё не строился — пусть строится, но уже без нас.
        _ = EnsureCacheReadyAsync();
        return null;
    }

    /// <summary>Один товар из базы по точному совпадению поля. Нужен, когда кэш ещё не готов:
    /// достать одну строку по индексу дешевле, чем поднять весь каталог.</summary>
    private CatalogProductTileVm? LoadSingleTile(string column, string value)
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                       image_url, category, brand, purchase_price, piece_option_json, plu, hotkey_group,
                       is_bundle, article, bundle_items_json, alternate_barcodes,
                       alternate_barcode_variants, product_code
                FROM Products WHERE {column} = $value COLLATE NOCASE LIMIT 1;
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$value";
            parameter.Value = value;
            command.Parameters.Add(parameter);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ToTileVm(ReadRecord(reader)) : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Товар по {column} не прочитан: {ex.Message}", "WARNING");
            return null;
        }
    }

    public CatalogProductTileVm? TryGetTileBySku(string sku) =>
        TryGetTileById(sku);

    public CatalogProductTileVm? TryGetTileById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var key = id.Trim();
        if (!_cacheReady)
        {
            var direct = LoadSingleTile("id", key);
            if (direct != null)
                return direct;

            _ = EnsureCacheReadyAsync();
            return null;
        }

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

    /// <summary>Товары, отмеченные звёздочкой. Берутся запросом к базе, а не из in-memory
    /// кэша каталога: тот наполняется страницей, которую сейчас показывает каталог, и у
    /// вызывающего кода (панель чека) он может быть пуст вовсе.</summary>
    public IReadOnlyList<CatalogProductTileVm> LoadFavoriteTiles(int limit = 12)
    {
        var result = new List<CatalogProductTileVm>();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                       image_url, category, brand, purchase_price, piece_option_json, plu, hotkey_group,
                       is_bundle, article, bundle_items_json, alternate_barcodes,
                       alternate_barcode_variants, product_code
                FROM Products WHERE is_favorite = 1 ORDER BY name COLLATE NOCASE LIMIT $limit;
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$limit";
            parameter.Value = limit;
            command.Parameters.Add(parameter);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tile = ToTileVm(ReadRecord(reader));
                if (tile != null)
                    result.Add(tile);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Избранные товары не прочитаны: {ex.Message}", "WARNING");
        }

        return result;
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

        using var upsertCommand = CreateUpsertCommand(connection, transaction);
        foreach (var vm in products)
        {
            ProductUnitNormalizer.ApplyToTile(vm);
            var record = FromTileVm(vm);
            if (favoriteIds.Contains(record.Id))
                record.IsFavorite = true;

            vm.IsFavorite = record.IsFavorite;
            ExecuteUpsert(upsertCommand, record);
        }

        transaction.Commit();
        ResetCache();
    }

    /// <summary>2026-09-09: удаление одного товара — нужно для Склада в автономном (офлайн)
    /// режиме, где ICatalogApiService.DeleteProductAsync (реальный HTTP к NurCRM) недоступен.
    /// Тот же SQL, что уже использует SyncReplaceAllWithDiff для удалённых на сервере товаров.</summary>
    public void DeleteProduct(string id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Products WHERE id = @id;";
        var idParam = command.CreateParameter();
        idParam.ParameterName = "@id";
        idParam.Value = id;
        command.Parameters.Add(idParam);
        command.ExecuteNonQuery();
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

            // 2026-09-12: "Дополнительные штрихкоды" из карточки товара сохранялись (см.
            // AlternateBarcodesRaw), но никогда не участвовали в поиске — ни при сканировании на
            // кассе, ни в Складе, потому что TryGetTileByBarcode индексировал только основной
            // Barcode. Регистрируем каждый дополнительный код в том же индексе — основной
            // штрихкод в приоритете при совпадении (не даём чужому доп. коду перекрыть чей-то
            // основной).
            if (!string.IsNullOrWhiteSpace(tile.AlternateBarcodesRaw))
            {
                foreach (var alt in tile.AlternateBarcodesRaw.Split(
                             ['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!barcodeCache.ContainsKey(alt))
                        barcodeCache[alt] = tile;
                }
            }

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

        // На большом каталоге (10-20 тыс. товаров) полная пересборка кэша — это SELECT всей
        // таблицы плюс конструирование тайлов, реально несколько секунд. Без этого лока
        // несколько потоков (фоновый WarmUpCacheAsync при старте + UI-поток на первом
        // сканировании штрих-кода) запускали ЭТУ ЖЕ пересборку параллельно и независимо —
        // на большом каталоге совпадающие по времени пересборки удваивали/утраивали нагрузку
        // на SQLite и CPU и выглядели как зависание/краш кассы. Теперь конкурентный вызов
        // просто ждёт уже идущую пересборку вместо того, чтобы начинать свою.
        lock (_warmUpGate)
        {
            if (_cacheReady)
                return true;

            if (_warmUpTask != null)
            {
                try
                {
                    _warmUpTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Catalog warm-up wait failed: {ex.GetType().Name}", "WARNING");
                }

                return _cacheReady;
            }

            RebuildCacheFromDatabase();
            return _cacheReady;
        }
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
                must_weigh = @must_weigh,
                intake_date = CASE WHEN intake_date IS NULL AND @stock > 0 THEN @today ELSE intake_date END
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@stock", stock);
        command.Parameters.AddWithValue("@unit", ProductUnitNormalizer.DisplayUnit(kind));
        command.Parameters.AddWithValue("@must_weigh", mustWeigh ? 1 : 0);
        command.Parameters.AddWithValue("@today", DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
        PatchStockInCache(id, stock, mustWeigh);
    }

    /// <summary>id → дата, когда у товара впервые появился остаток > 0 (см. UpdateStock/
    /// SyncReplaceAllWithDiff) — только для товаров, где эта дата уже зафиксирована.</summary>
    public Dictionary<string, DateTime> LoadIntakeDates()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, intake_date FROM Products WHERE intake_date IS NOT NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                result[reader.GetString(0)] = date;
        }
        return result;
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

        var deleted = existing.Keys.Count(id => !incoming.ContainsKey(id));
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
            foreach (var id in existing.Keys.Where(id => !incoming.ContainsKey(id)))
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
        ResetCache();
        return (added, changed, deleted);
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
            r.ProductCode ?? "");

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

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        // WAL позволяет читателям (UI) не блокироваться на время записи каталога
        // фоновой синхронизацией — на слабых устройствах это была одна из причин
        // подвисаний интерфейса каждые 45 секунд.
        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=3000;";
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
                   bundle_items_json, alternate_barcodes, alternate_barcode_variants, product_code
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
                ProductCode = reader.IsDBNull(20) ? null : reader.GetString(20)
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
                intake_date, product_code
            ) VALUES (
                @id, @name, @price, @barcode, @stock, @unit, @is_favorite, @must_weigh,
                @image_url, @category, @brand, @purchase_price, @piece_option_json, @plu, @hotkey_group, @is_bundle, @article,
                @bundle_items_json, @alternate_barcodes, @alternate_barcode_variants,
                @intake_date, @product_code
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
            PieceOption = DeserializePieceOption(record.PieceOptionJson),
            Plu = record.Plu,
            HotkeyGroup = record.HotkeyGroup,
            IsBundle = record.IsBundle,
            Article = record.Article,
            ProductCode = record.ProductCode,
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
            Unit = ProductUnitNormalizer.DisplayUnit(kind),
            IsFavorite = vm.IsFavorite,
            MustWeigh = kind == ProductUnitKind.Kilogram,
            ImageUrl = vm.ImageUrl,
            Category = vm.Category,
            Brand = vm.Brand,
            PurchasePrice = vm.PurchasePrice,
            PieceOptionJson = vm.PieceOption is null ? null : JsonSerializer.Serialize(vm.PieceOption),
            Plu = vm.Plu,
            HotkeyGroup = vm.HotkeyGroup,
            IsBundle = vm.IsBundle,
            Article = vm.Article,
            ProductCode = vm.ProductCode,
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
