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

public sealed partial class LocalProductRepository
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

    // 2026-10-27, аудит С-25: схема накатывалась при каждом вызове (после каждой продажи, при каждом перечитывании каталога) —
    // десяток ALTER TABLE, из которых почти все падали исключением «колонка уже есть», на UI-потоке. Теперь один раз на базу
    // (путь + номер переоткрытия после смены аккаунта).
    private readonly object _schemaGate = new();
    private string? _schemaKey;

    public void EnsureSchema()
    {
        DatabaseService.Instance.EnsureSchema();
        var key = DatabaseService.Instance.DatabasePath + "|" + DatabaseService.Instance.SchemaGeneration;
        lock (_schemaGate)
        {
            if (_schemaKey == key && File.Exists(DatabaseService.Instance.DatabasePath))
                return;
            EnsureSchemaCore();
            _schemaKey = key;
        }
    }

    private void EnsureSchemaCore()
    {
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

        // 2026-11-06 (Б-13): добавление недостающих столбцов вынесено в EnsureProductColumns — оно же вызывается из DatabaseService.EnsureSchema,
        // иначе новая база после смены компании создавалась без этих столбцов (там своя, сокращённая CREATE TABLE Products).
        EnsureProductColumns(connection);

        MigrateLegacyDataIfNeeded(connection);
    }

    /// <summary>2026-11-06 (Б-13): столбцы таблицы Products, которых нет в «сокращённой» CREATE TABLE у DatabaseService (и у баз старых версий): (имя, определение,
    /// сбрасывать ли версию каталога, чтобы ближайшая полная загрузка записала новое поле). Порядок и определения — как были в отдельных ALTER TABLE.</summary>
    private static readonly (string Name, string Definition, bool ResetsCatalogVersion)[] ProductColumns =
    {
        ("piece_option_json", "TEXT", false),
        ("plu", "INTEGER", false),
        ("hotkey_group", "TEXT", false),
        ("is_bundle", "INTEGER NOT NULL DEFAULT 0", false),
        ("article", "TEXT", false),
        ("bundle_items_json", "TEXT", false),
        ("alternate_barcodes", "TEXT", false),
        ("alternate_barcode_variants", "TEXT", false),
        // 2026-09-21: "Код товара" — отдельное от "article" поле сайта (см. CatalogProductTileVm.ProductCode).
        ("product_code", "TEXT", false),
        // 2026-09-24: вид товара сайта (product/service/bundle). Нужен услугам: у них нет остатка, и касса не должна требовать его при продаже.
        ("kind", "TEXT", false),
        // Дата, когда у товара впервые появился остаток > 0 (UpdateStock/SyncReplaceAllWithDiff) — «дата поступления» для срока годности (AI-фичи 2026-09-03, п.5).
        ("intake_date", "TEXT", false),
        // 2026-10-01: описание товара от продавца — кнопка «Подробнее» на плитке.
        ("description", "TEXT", false),
        // 2026-10-04: оптовая цена товара. Колонка новая — версия каталога сбрасывается, чтобы ближайшая синхронизация была полной и записала оптовые цены.
        ("wholesale_price", "REAL NOT NULL DEFAULT 0", true),
        // 2026-11-01 (is_weight): признак сервера «сумма округляется вниз»; NULL = неизвестно → прежняя эвристика. Версия каталога сбрасывается по той же причине.
        ("IsWeight", "INTEGER NULL", true),
        // 2026-11-06 (В12, Б-5, Б-8): скидка, наценка, страна, вес и «Минимальный остаток» карточки. Пустые столбцы без значения по умолчанию, версию каталога НЕ сбрасываем
        // (по решению владельца — чтобы не вызвать полную перезагрузку каталога у клиентов): заполнятся при ближайшей загрузке; отпечаток записи (Fingerprint) их уже учитывает.
        ("discount_percent", "REAL", false),
        ("markup_percent", "REAL", false),
        ("country", "TEXT", false),
        ("weight_kg", "REAL", false),
        ("min_quantity", "REAL", false),
    };

    /// <summary>2026-11-06 (Б-13): приводит таблицу Products к актуальным столбцам. Идемпотентно: смотрит PRAGMA table_info и добавляет ТОЛЬКО недостающие (ALTER TABLE ADD COLUMN),
    /// данные не трогает. Вызывается и из <see cref="EnsureSchemaCore"/>, и из DatabaseService.EnsureSchema (любая открываемая или создаваемая база, в том числе новая после смены компании).
    /// Возвращает, сколько столбцов добавлено.</summary>
    internal static int EnsureProductColumns(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Products);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        if (existing.Count == 0)
            return 0; // таблицы нет — создаёт вызывающий

        var added = 0;
        foreach (var (name, definition, resetsVersion) in ProductColumns)
        {
            if (existing.Contains(name))
                continue;
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE Products ADD COLUMN {name} {definition};"; // имена и определения — константы выше, не ввод пользователя
                alter.ExecuteNonQuery();
                added++;
                if (resetsVersion)
                {
                    using var resetVersion = connection.CreateCommand();
                    resetVersion.CommandText = "DELETE FROM catalog_meta WHERE key = 'catalog_version';";
                    try { resetVersion.ExecuteNonQuery(); }
                    catch (SqliteException) { /* catalog_meta ещё нет — версии и так нет */ }
                }
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
            {
                // Столбец успели добавить параллельно — ожидаемо.
            }
        }

        if (added > 0)
            PosLogger.Log($"Схема товаров: добавлено недостающих столбцов — {added}.", "INFO");
        return added;
    }

    private static readonly object SelfHealGate = new();
    private static string? _selfHealKey;

    /// <summary>2026-11-06 (Б-13): самолечение при ошибке «no such column». Один раз на открытие базы (путь + номер переоткрытия) заново проверяет столбцы Products;
    /// true — столбцы достроены (или проверены), вызывающему стоит повторить запрос ОДИН раз. Другая ошибка или лечение уже было — false.</summary>
    public static bool TryHealMissingColumn(Exception? ex)
    {
        if (ex is not SqliteException || ex.Message.IndexOf("no such column", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        string key;
        try { key = DatabaseService.Instance.DatabasePath + "|" + DatabaseService.Instance.SchemaGeneration; }
        catch (Exception) { return false; }
        lock (SelfHealGate)
        {
            if (_selfHealKey == key)
                return false;
            _selfHealKey = key;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={DatabaseService.Instance.DatabasePath}");
            connection.Open();
            var added = EnsureProductColumns(connection);
            PosLogger.Log($"Схема товаров: ошибка «{ex.Message}» — столбцы перепроверены, добавлено {added}; запрос повторяется.", "WARNING");
            return true;
        }
        catch (Exception healEx) when (healEx is not OutOfMemoryException)
        {
            PosLogger.Log($"Схема товаров: самолечение не удалось: {healEx.Message}", "WARNING");
            return false;
        }
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
        var found = TryGetTileByExactBarcode(key);
        if (found != null)
            return found;

        // 2026-10-03, живой случай (клиент palma): в каталоге «0762497741537» (EAN-13), а сканер присылает
        // «762497741537» (UPC-A, 12 цифр) — это один и тот же код, но точный поиск его не находил, а ручной
        // поиск находил по части кода. Пробуем код с ведущим нулём и без него.
        return BarcodeForms.Alternate(key) is { } alternate ? TryGetTileByExactBarcode(alternate) : null;
    }

    private CatalogProductTileVm? TryGetTileByExactBarcode(string key)
    {

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
    /// <summary>2026-10-03: два товара с одним штрихкодом (дубль) — новый заменяет уже найденный, только если у
    /// найденного нет остатка, а у нового есть. Иначе остаётся первый (каталог читается по порядку создания).</summary>
    private static bool PreferForBarcode(CatalogProductTileVm candidate, CatalogProductTileVm current) =>
        current.Quantity <= 0 && candidate.Quantity > 0;

    private CatalogProductTileVm? LoadSingleTile(string column, string value) => LoadSingleTile(column, value, healed: false);

    private CatalogProductTileVm? LoadSingleTile(string column, string value, bool healed)
    {
        try
        {
            using var connection = OpenConnection();
            // 2026-10-04, отчёт о производительности (п. 14): раньше сразу «= $value COLLATE NOCASE» —
            // индекс idx_products_barcode и первичный ключ id созданы без NOCASE, и SQLite перебирал всю
            // таблицу: скан незнакомого штрихкода на каталоге 15 000 — 4,1 мс вместо сотых долей. Теперь
            // сначала точное совпадение (по индексу); без учёта регистра — только если в значении есть
            // буквы (штрихкоды из цифр регистра не имеют; id с сервера — UUID в нижнем регистре).
            var found = QuerySingleTile(connection, column, value, ignoreCase: false);
            if (found != null || !value.Any(char.IsLetter))
                return found;
            if (column == "id")
            {
                var lower = value.ToLowerInvariant();
                return lower == value ? null : QuerySingleTile(connection, column, lower, ignoreCase: false);
            }

            return QuerySingleTile(connection, column, value, ignoreCase: true);
        }
        catch (Exception ex)
        {
            // 2026-11-06 (Б-13): «no such column» — один раз пере-проверить схему и повторить; журнал — не чаще раза в 10 минут на причину (было ~500 строк за секунду).
            if (!healed && TryHealMissingColumn(ex))
                return LoadSingleTile(column, value, healed: true);
            PosLogger.LogThrottled("product-by-" + column, $"Товар по {column} не прочитан: {ex.Message}", "WARNING");
            return null;
        }
    }

    private static CatalogProductTileVm? QuerySingleTile(SqliteConnection connection, string column, string value, bool ignoreCase)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, name, price, barcode, stock, unit, is_favorite, must_weigh,
                   image_url, category, brand, purchase_price, piece_option_json, plu, hotkey_group,
                   is_bundle, article, bundle_items_json, alternate_barcodes,
                   alternate_barcode_variants, product_code, kind, description, wholesale_price, IsWeight,
                   discount_percent, markup_percent, country, weight_kg, min_quantity
            FROM Products WHERE {column} = $value{(ignoreCase ? " COLLATE NOCASE" : "")} LIMIT 1;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$value";
        parameter.Value = value;
        command.Parameters.Add(parameter);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ToTileVm(ReadRecord(reader)) : null;
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
    public IReadOnlyList<CatalogProductTileVm> LoadFavoriteTiles(int limit = 12) => LoadFavoriteTiles(limit, healed: false);

    private IReadOnlyList<CatalogProductTileVm> LoadFavoriteTiles(int limit, bool healed)
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
                       alternate_barcode_variants, product_code, kind, description, wholesale_price, IsWeight,
                   discount_percent, markup_percent, country, weight_kg, min_quantity
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
            // 2026-11-06 (Б-13): самолечение схемы и журнал не чаще раза в 10 минут (см. LoadSingleTile).
            if (!healed && TryHealMissingColumn(ex))
                return LoadFavoriteTiles(limit, healed: true);
            PosLogger.LogThrottled("favorites-read", $"Избранные товары не прочитаны: {ex.Message}", "WARNING");
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

            // 2026-10-29: единый поиск SearchMatch — слова в любом порядке, ё=е; нет совпадений как набрано — пробуем раскладку/транслит (Asu↔Асу↔фыг).
            var sq = SearchMatch.Prepare(query);
            for (var pass = 0; pass < 2 && matches.Count == 0; pass++)
            {
                foreach (var row in _searchRows)
                {
                    if (!MatchesRowFilters(row, mustWeigh, criteria))
                        continue;

                    if (!row.MatchesText(normalized, sq, pass == 1))
                        continue;

                    matches.Add(row.Tile);
                }
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

    /// <summary>2026-10-27 (Б-46 сервера, CATALOG_FEED): свежая копия плитки товара из базы (не общий объект кэша) — чтобы сохранить
    /// остаток, если по ленте его применять нельзя. null — товара в базе нет.</summary>
    public CatalogProductTileVm? LoadTileCopy(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : LoadSingleTile("id", id.Trim());
}
