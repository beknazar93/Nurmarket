using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models.Local;

namespace NurMarketKassa.Services;

/// <summary>Единая локальная SQLite БД: Users, Products, OfflineSales.</summary>
public sealed partial class DatabaseService
{
    // 2026-09-13, живой риск потери данных: раньше БД лежала рядом с exe (AppDomain.
    // CurrentDomain.BaseDirectory\data). Эта касса обновляется через Velopack, который на
    // каждое обновление подкладывает новую версию в свою собственную папку — офлайн-очередь
    // непроведённых продаж (OfflineSales) в старой папке становится невидимой для новой
    // версии сразу после каждого автообновления. %AppData% — то же самое место, что уже
    // используют OfflineAuthSessionStore/UserPreferences для этого продукта — стабильно
    // независимо от того, в какую папку Velopack развернул текущую версию.
    private static readonly string DataDirectory = Path.Combine(
        NurMarketKassa.Services.AppPaths.Roaming,
        NurMarketKassa.Services.AppMode.DataFolderName, "data");
    private static readonly string DbPath = Path.Combine(DataDirectory, "pos_local.db");
    private static readonly string LegacyCatalogDbPath = Path.Combine(DataDirectory, "catalog.db");
    private static readonly string LegacyOfflineDbPath = Path.Combine(DataDirectory, "offline.db");

    /// <summary>Старое расположение БД (рядом с exe) — для одноразового переноса уже
    /// накопленных у пользователей данных (в т.ч. непроведённых офлайн-продаж) при первом
    /// запуске сборки с новым путём.</summary>
    private static readonly string OldInstallDirDbPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "pos_local.db");

    private static DatabaseService? _instance;
    /// <summary>Единственный экземпляр.
    ///
    /// 2026-09-23: было `_instance ??= new DatabaseService()`. Оператор ??= не атомарен: два
    /// потока могли одновременно увидеть null и создать ДВА объекта, у каждого со своим
    /// замком на базу — после чего блокировка переставала что-либо сериализовать. На
    /// старте кассы это как раз и происходит: фоновый бэкап базы, резолв синглтона в
    /// контейнере и прогрев каталога стартуют одновременно. Самый вероятный неучтённый
    /// источник повторяющегося повреждения локальной базы.</summary>
    public static DatabaseService Instance => LazyInstance.Value;

    private static readonly Lazy<DatabaseService> LazyInstance =
        new(() => new DatabaseService(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _initLock = new();
    private bool _initialized;

    /// <summary>Одноразовые миграции (перенос базы из папки установки, импорт
    /// offline_sales_pending.json) берут данные из мест, общих для всех аккаунтов. После
    /// подмены набора данных они бы влили в базу новой компании чужие продажи, поэтому
    /// со второго открытия базы в этом запуске отключаются.</summary>
    private static bool _legacyImportDone;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ReaderWriterLockSlim _dbLock = new(LockRecursionPolicy.NoRecursion);

    private DatabaseService()
    {
        EnsureSchema();
    }

    public string DatabasePath => DbPath;

    /// <summary>2026-10-27 (С-25): номер «переоткрытия» базы после смены аккаунта — по нему LocalProductRepository.EnsureSchema
    /// понимает, что схему надо накатить заново.</summary>
    public int SchemaGeneration { get; private set; }

    /// <summary>Переоткрыть базу после того, как AccountDataIsolation подменил набор данных:
    /// путь тот же, но файл под ним стал другим (или его ещё нет). Без сброса признака
    /// инициализации схема на новой базе не накатилась бы — EnsureSchema просто вышел бы.
    /// Пул соединений чистим первым: иначе переиспользованное соединение продолжит держать
    /// уже отложенный файл прежней компании.</summary>
    public void ReopenAfterAccountSwitch()
    {
        _legacyImportDone = true;
        SqliteConnection.ClearAllPools();
        Volatile.Write(ref _walState, 0); // 2026-11-06: В5 25.4 — режим журнала хранится в файле базы, под ним теперь другой файл
        lock (_initLock)
        {
            _initialized = false;
            SchemaGeneration++;
        }
        EnsureSchema();
    }

    public void EnsureSchema()
    {
        lock (_initLock)
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(DataDirectory);
            if (!_legacyImportDone)
                MigrateFromOldInstallDir();
            VerifyDatabaseAtStartup();
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS Users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    email TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    password_encrypted TEXT NOT NULL,
                    remember_me INTEGER NOT NULL DEFAULT 0,
                    last_login_at TEXT,
                    user_id TEXT,
                    cashier_name TEXT
                );

                CREATE TABLE IF NOT EXISTS Products (
                    id TEXT PRIMARY KEY,
                    barcode TEXT,
                    name TEXT NOT NULL,
                    price REAL NOT NULL DEFAULT 0,
                    stock REAL NOT NULL DEFAULT 0,
                    unit TEXT NOT NULL DEFAULT 'шт',
                    is_favorite INTEGER NOT NULL DEFAULT 0,
                    must_weigh INTEGER NOT NULL DEFAULT 0,
                    image_url TEXT,
                    category TEXT,
                    brand TEXT,
                    purchase_price REAL NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS OfflineSales (
                    id TEXT PRIMARY KEY NOT NULL,
                    created_at TEXT NOT NULL,
                    receipt_number TEXT,
                    json_data TEXT NOT NULL,
                    sync_status TEXT NOT NULL,
                    is_synced INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS catalog_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS IrregularReceipts (
                    id TEXT PRIMARY KEY NOT NULL,
                    created_at TEXT NOT NULL,
                    tag TEXT NOT NULL,
                    json_data TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS WriteOffHistory (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    product_id TEXT NOT NULL,
                    product_name TEXT NOT NULL,
                    quantity REAL NOT NULL,
                    reason TEXT NOT NULL,
                    cashier_name TEXT,
                    created_at TEXT NOT NULL,
                    company_id TEXT
                );

                CREATE TABLE IF NOT EXISTS ClientLoyalty (
                    client_id TEXT PRIMARY KEY NOT NULL,
                    balance REAL NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ClientLoyaltyTransactions (
                    sale_id TEXT PRIMARY KEY NOT NULL,
                    client_id TEXT NOT NULL,
                    delta REAL NOT NULL,
                    reversed_delta REAL NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS VoiceUnitWords (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    word TEXT NOT NULL UNIQUE,
                    abbreviation TEXT,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS VoiceProductAliases (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    phrase TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    product_id TEXT NOT NULL,
                    product_title TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS LocalOwnerAccount (
                    email TEXT PRIMARY KEY NOT NULL COLLATE NOCASE,
                    password_salt TEXT NOT NULL,
                    password_hash TEXT NOT NULL,
                    display_name TEXT,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SoldLineItems (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    product_id TEXT NOT NULL,
                    product_name TEXT NOT NULL,
                    quantity REAL NOT NULL,
                    unit_price REAL NOT NULL,
                    sold_at TEXT NOT NULL,
                    source TEXT NOT NULL DEFAULT 'local',
                    company_id TEXT,
                    sale_id TEXT
                );

                -- Скидки и списанные бонусы по каждому проведённому чеку. Сервер про бонусы
                -- не знает вообще (в API NurCRM нет понятия баллов), а скидку отдаёт одной
                -- суммой, в которой бонусы уже "растворены" — разделить их постфактум нельзя.
                -- Поэтому касса записывает обе величины у себя в момент оплаты.
                CREATE TABLE IF NOT EXISTS ShiftSaleAdjustments (
                    sale_id TEXT PRIMARY KEY NOT NULL,
                    shift_id TEXT,
                    discount_total REAL NOT NULL DEFAULT 0,
                    points_redeemed REAL NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_shift_sale_adjustments_created ON ShiftSaleAdjustments(created_at);
                CREATE INDEX IF NOT EXISTS idx_shift_sale_adjustments_shift ON ShiftSaleAdjustments(shift_id);

                -- Покупатели, разрешившие боту себе писать. Telegram не даёт отправить
                -- сообщение по номеру телефона: написать можно только тому, кто сам нажал
                -- «Старт» у бота. Ключ — chat_id: у одного человека бывает несколько устройств.
                CREATE TABLE IF NOT EXISTS TelegramSubscribers (
                    chat_id TEXT PRIMARY KEY NOT NULL,
                    client_id TEXT NOT NULL,
                    display_name TEXT,
                    created_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_telegram_subscribers_client ON TelegramSubscribers(client_id);

                -- Возвраты, списания, расходы и оплата долгов за смену. В итогах смены на
                -- сервере таких полей нет, а кассиру они нужны при закрытии — см. ShiftEventsStore.
                CREATE TABLE IF NOT EXISTS ShiftEvents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    shift_id TEXT,
                    source_id TEXT,
                    amount REAL NOT NULL,
                    note TEXT,
                    created_at TEXT NOT NULL
                );

                -- Уникальность по (вид, источник) — повтор офлайн-очереди не задваивает суммы.
                CREATE UNIQUE INDEX IF NOT EXISTS idx_shift_events_source
                    ON ShiftEvents(kind, source_id) WHERE source_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_shift_events_shift ON ShiftEvents(shift_id);
                CREATE INDEX IF NOT EXISTS idx_shift_events_created ON ShiftEvents(created_at);

                -- Перемещения товаров между складами, зонами и ячейками. Сервер NurCRM таких
                -- документов не знает (в его ответе по остаткам склады есть только как справочная
                -- разбивка), поэтому журнал перемещений живёт в кассе: он не уедет на другое
                -- устройство, пока на сервере не появится своя поддержка.
                CREATE TABLE IF NOT EXISTS StockPlaces (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    kind TEXT NOT NULL,            -- warehouse | zone | cell
                    parent_id TEXT,
                    company_id TEXT
                );

                CREATE TABLE IF NOT EXISTS StockTransfers (
                    id TEXT PRIMARY KEY,
                    number TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    status TEXT NOT NULL,          -- created | in_transit | delivered | cancelled
                    from_place_id TEXT,
                    to_place_id TEXT,
                    responsible TEXT,
                    carrier TEXT,
                    tracking_number TEXT,
                    total_weight REAL NOT NULL DEFAULT 0,
                    note TEXT,
                    company_id TEXT
                );

                CREATE TABLE IF NOT EXISTS StockTransferItems (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    transfer_id TEXT NOT NULL,
                    product_id TEXT,
                    product_name TEXT NOT NULL,
                    article TEXT,
                    barcode TEXT,
                    quantity REAL NOT NULL,
                    unit TEXT,
                    weight REAL NOT NULL DEFAULT 0
                );

                -- Хронология: когда собрали, когда отправили, кто принял. Пишется на каждую смену
                -- статуса и не перезаписывается — по ней потом разбирают недостачи.
                CREATE TABLE IF NOT EXISTS StockTransferLog (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    transfer_id TEXT NOT NULL,
                    at TEXT NOT NULL,
                    status TEXT NOT NULL,
                    employee TEXT,
                    note TEXT
                );

                -- Накладные, акты приёма-передачи, транспортные документы и фотографии повреждений.
                -- В базе лежит только путь к файлу: сами файлы копируются в папку кассы.
                CREATE TABLE IF NOT EXISTS StockTransferFiles (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    transfer_id TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    file_name TEXT NOT NULL,
                    kind TEXT,                     -- invoice | act | transport | damage | other
                    added_at TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_transfers_created ON StockTransfers(created_at);
                CREATE INDEX IF NOT EXISTS idx_transfers_status ON StockTransfers(status);
                CREATE INDEX IF NOT EXISTS idx_transfer_items_transfer ON StockTransferItems(transfer_id);
                CREATE INDEX IF NOT EXISTS idx_transfer_log_transfer ON StockTransferLog(transfer_id);
                CREATE INDEX IF NOT EXISTS idx_transfer_files_transfer ON StockTransferFiles(transfer_id);

                CREATE INDEX IF NOT EXISTS idx_products_barcode ON Products(barcode);
                CREATE INDEX IF NOT EXISTS idx_products_name ON Products(name COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_offline_sales_status ON OfflineSales(sync_status);
                CREATE INDEX IF NOT EXISTS idx_offline_sales_created ON OfflineSales(created_at);
                CREATE INDEX IF NOT EXISTS idx_irregular_receipts_created ON IrregularReceipts(created_at);
                CREATE INDEX IF NOT EXISTS idx_sold_line_items_product ON SoldLineItems(product_id);
                CREATE INDEX IF NOT EXISTS idx_sold_line_items_sold_at ON SoldLineItems(sold_at);

                -- 2026-10-01, «Умная допродажа»: показы подсказки «С этим часто берут» и ответы
                -- кассира (accepted / skipped). cart_key — чек, в котором показали; sale_id
                -- проставляется после оплаты, по нему видно, что добавленный товар реально продан.
                CREATE TABLE IF NOT EXISTS UpsellEvents (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event TEXT NOT NULL,
                    product_id TEXT NOT NULL,
                    product_name TEXT,
                    price REAL,
                    trigger_product_id TEXT,
                    score REAL,
                    cart_key TEXT,
                    sale_id TEXT,
                    company_id TEXT,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_upsell_events_created ON UpsellEvents(created_at);
                CREATE INDEX IF NOT EXISTS idx_upsell_events_cart ON UpsellEvents(cart_key);
                """;
            command.ExecuteNonQuery();

            // 2026-11-06 (Б-13): Products выше создаётся сокращённой — без piece_option_json, intake_date и др. Раньше недостающие столбцы добавлял только
            // LocalProductRepository.EnsureSchema, который после смены компании никто не вызывал: новая база читалась с ошибкой «no such column» (~500 строк в журнале каждые 30 с).
            // Теперь любая открываемая или создаваемая база приводится к актуальной схеме здесь же (идемпотентно, данные не трогаются).
            try { LocalProductRepository.EnsureProductColumns(connection); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Схема товаров: столбцы не достроены: {ex.Message}", "WARNING"); }

            AddSoldLineItemsCompanyColumn(connection);
            AddWriteOffsCompanyColumn(connection);
            AddWriteOffsUnitCostColumn(connection);
            AddSoldLineItemsSaleIdColumn(connection);
            AddCompanyHistoryIndexes(connection);
            EnsureSoldLineItemsSaleIndex(connection);
            EnsurePerfIndexes(connection); // 2026-11-06: В5 шаг 30 (ОК владельца на схему)
            MigrateLegacyCatalogDb(connection);
            MigrateLegacyOfflineDb(connection);
            if (!_legacyImportDone)
                MigrateLegacyJsonSales(connection);

            _initialized = true;
        }
    }

    /// <summary>2026-11-01 (Н-04, «да» владельца): индекс строк продаж по номеру чека. Чек читается по
    /// <c>WHERE sale_id = $id</c> (отчёт смены, история чеков, экспорт, удаление чека при сверке), а индекса не было:
    /// старый код создавал <c>idx_sold_line_items_sale</c> только сразу после ALTER TABLE ADD COLUMN, но колонка
    /// sale_id давно входит в CREATE TABLE — ALTER падал, и индекс не создавался НИКОГДА. На 1 млн строк чек читался
    /// 167–233 мс и больше (полный перебор таблицы).
    ///
    /// Строим СИНХРОННО в EnsureSchema, до запуска остальных писателей и до показа окон. Фоновое построение не годится:
    /// другие писатели той же pos_local.db (LocalProductRepository, LocalAccountsManager и др.) работают со своими
    /// соединениями и не знают про _dbLock этого класса, а CREATE INDEX держит блокировку записи SQLite секунды —
    /// они ждали бы busy_timeout и падали с «database is locked». Здесь же пока никто, кроме нас, в базу не пишет.
    /// Разово на базу (~2,3 с на 1 млн строк, на обычной базе мгновенно); повторный запуск — только проверка sqlite_master.
    /// Таймаут команды 120 с; ошибка не ломает запуск (WARNING) — без индекса всё работает, просто медленнее.
    /// Откат: <c>DROP INDEX idx_sold_line_items_sale;</c></summary>
    private static void EnsureSoldLineItemsSaleIndex(SqliteConnection connection)
    {
        try
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'idx_sold_line_items_sale';";
            if (check.ExecuteScalar() is not null)
                return;

            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM SoldLineItems;";
            var rows = Convert.ToInt64(count.ExecuteScalar() ?? 0L);
            PosLogger.Log($"Создаётся индекс idx_sold_line_items_sale по {rows} строкам истории продаж (разово).", "INFO");

            var started = DateTime.UtcNow;
            using var create = connection.CreateCommand();
            create.CommandTimeout = 120;
            create.CommandText = "CREATE INDEX IF NOT EXISTS idx_sold_line_items_sale ON SoldLineItems(sale_id);";
            create.ExecuteNonQuery();
            PosLogger.Log($"Индекс idx_sold_line_items_sale создан за {(DateTime.UtcNow - started).TotalMilliseconds:F0} мс.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Индекс idx_sold_line_items_sale не создан: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-11-06: В5 шаг 30 (аудит скорости 05.11.2026, И-1, И-2, И-3; «да» владельца на схему базы) — версия схемы, записываемая в <c>PRAGMA user_version</c>
    /// файла pos_local.db. 0 — базы до этой правки (индексов ещё нет), 1 — индексы избранного, списаний и штрихкода построены. Следующие миграции повышают число.
    /// Версия живёт в самом файле базы, поэтому подмена базы при смене аккаунта и откат из резервной копии подхватываются сами:
    /// старая копия (версия 0) просто достроит индексы при открытии.</summary>
    internal const int PerfIndexesSchemaVersion = 1;

    /// <summary>Индексы шага 30: имя и DDL. Все — <c>CREATE INDEX IF NOT EXISTS</c> (повторный запуск ничего не ломает и данные не трогает).
    /// <list type="bullet">
    /// <item>И-1 — частичный индекс избранного (4 КБ): избранное обновляется на каждом обновлении каталога, раньше обходило все 15 тыс. строк (15 мс → 0,014 мс).</item>
    /// <item>И-2 — два индекса списаний (нужны ОБА: с одним по дате запрос «по товару» становится медленнее): последние 200 — 52 мс → 0,67 мс.</item>
    /// <item>И-3 — штрихкод без учёта регистра: неизвестный код с буквами не перебирает таблицу (3–4 мс → 0,011 мс).</item>
    /// </list>
    /// Откат: <c>DROP INDEX имя;</c> и <c>PRAGMA user_version = 0;</c>.</summary>
    internal static readonly (string Name, string Ddl)[] PerfIndexes =
    {
        ("idx_products_favorite_name", "CREATE INDEX IF NOT EXISTS idx_products_favorite_name ON Products(name COLLATE NOCASE) WHERE is_favorite = 1;"),
        ("idx_writeoff_company_created", "CREATE INDEX IF NOT EXISTS idx_writeoff_company_created ON WriteOffHistory(company_id, created_at);"),
        ("idx_writeoff_company_name", "CREATE INDEX IF NOT EXISTS idx_writeoff_company_name ON WriteOffHistory(company_id, product_name COLLATE NOCASE, created_at);"),
        ("idx_products_barcode_nocase", "CREATE INDEX IF NOT EXISTS idx_products_barcode_nocase ON Products(barcode COLLATE NOCASE);"),
    };

    /// <summary>Миграция схемы шага 30. Строится СИНХРОННО в EnsureSchema (по тем же причинам, что и <see cref="EnsureSoldLineItemsSaleIndex"/>: другие писатели
    /// той же базы не знают про наш замок), быстро (на 15 тыс. товаров, 20 тыс. списаний — десятки миллисекунд), без потери данных.
    /// Ошибка одного индекса — WARNING и запуск идёт дальше (без индекса всё работает, просто медленнее); версия схемы повышается ТОЛЬКО если построены все индексы,
    /// иначе следующий запуск повторит.</summary>
    internal static void EnsurePerfIndexes(SqliteConnection connection)
    {
        try
        {
            if (ReadUserVersion(connection) >= PerfIndexesSchemaVersion)
                return;

            var allOk = true;
            foreach (var (name, ddl) in PerfIndexes)
            {
                try
                {
                    using var check = connection.CreateCommand();
                    check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $n;";
                    check.Parameters.AddWithValue("$n", name);
                    if (check.ExecuteScalar() is not null)
                        continue;

                    var started = DateTime.UtcNow;
                    using var create = connection.CreateCommand();
                    create.CommandTimeout = 120;
                    create.CommandText = ddl;
                    create.ExecuteNonQuery();
                    PosLogger.Log($"Индекс {name} создан за {(DateTime.UtcNow - started).TotalMilliseconds:F0} мс.", "INFO");
                }
                catch (Exception ex)
                {
                    allOk = false;
                    PosLogger.Log($"Индекс {name} не создан: {ex.Message}", "WARNING");
                }
            }

            if (allOk)
            {
                using var stamp = connection.CreateCommand();
                stamp.CommandText = $"PRAGMA user_version = {PerfIndexesSchemaVersion};"; // число-константа, не ввод пользователя
                stamp.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция индексов (шаг 30) не выполнена: {ex.Message}", "WARNING");
        }
    }

    internal static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    /// <summary>Открывает соединение, отдаёт его действию и закрывает. Нужен службам, которые
    /// живут рядом с базой, но своей копией строки подключения обзаводиться не должны — например
    /// журналу перемещений (StockTransferService).</summary>
    public void WithConnection(Action<SqliteConnection> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var slow = PerfSummary.MeasureSqlite(OperationName(action)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            action(connection);
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>2026-11-06: В5 25.4 — то же, что <see cref="WithConnection"/>, но ТОЛЬКО ДЛЯ ЧТЕНИЯ (SELECT): замок записи класса не берётся. Журнал закупок, отчёты и справочники
    /// больше не ждут запись продажи и не выстраиваются в очередь друг за другом (раньше любое чтение через WithConnection брало замок ЗАПИСИ: 300 чтений подряд —
    /// 300 захватов, пока продажа ждёт). База pos_local.db в режиме WAL (его включает LocalProductRepository): читатель видит целый снимок и не мешает писателю.
    /// Если WAL почему-то выключен, чтение берёт общий замок чтения — как остальные Load* этого класса. Писать и создавать таблицы внутри нельзя — для этого WithConnection.</summary>
    public void WithReadConnection(Action<SqliteConnection> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var slow = PerfSummary.MeasureSqlite(OperationName(action));
        using var connection = OpenConnection();
        var locked = !IsWalMode(connection);
        if (locked)
            _dbLock.EnterReadLock();
        try
        {
            action(connection);
        }
        finally
        {
            if (locked)
                _dbLock.ExitReadLock();
        }
    }

    /// <summary>0 — ещё не проверяли, 1 — WAL, 2 — не WAL. Сбрасывается при подмене базы (<see cref="ReopenAfterAccountSwitch"/>): режим хранится в файле.</summary>
    private int _walState;

    private bool IsWalMode(SqliteConnection connection)
    {
        var state = Volatile.Read(ref _walState);
        if (state != 0)
            return state == 1;
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var wal = string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase);
        Volatile.Write(ref _walState, wal ? 1 : 2);
        return wal;
    }

    /// <summary>Имя операции для журнала медленных запросов: метод, из которого вызвано действие (без текста запроса и параметров).</summary>
    private static string OperationName(Action<SqliteConnection> action)
    {
        var name = action.Method.DeclaringType?.DeclaringType?.Name ?? action.Method.DeclaringType?.Name ?? "?";
        var method = action.Method.Name;
        // лямбда: «<LoadLocalHistory>b__12_0» -> «LoadLocalHistory»
        if (method.StartsWith('<') && method.IndexOf('>') is var end and > 1)
            method = method[1..end];
        return name + "." + method;
    }

    /// <summary>Компания текущей сессии — для таблиц, которые ведут свои службы.</summary>
    public string? CurrentCompany() => CurrentCompanyId();

    /// <summary>Условие «только строки текущей компании» для чужих таблиц. Префикс — псевдоним
    /// таблицы в запросе, если он есть.</summary>
    /// <summary>2026-11-06: В5 Б-5 — то же условие, но компания уходит ПАРАМЕТРОМ <c>$ownCompany</c> этой команды (не литералом в тексте запроса).
    /// Вариант без команды ниже оставлен для чужих служб (StockTransferService), которые В5 не правит.</summary>
    public string OwnRowsClauseFor(SqliteCommand command, string? alias = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = CurrentCompanyId();
        if (company is null)
            return "";
        if (!command.Parameters.Contains("$ownCompany"))
            command.Parameters.AddWithValue("$ownCompany", company);
        var prefix = string.IsNullOrWhiteSpace(alias) ? "" : alias + ".";
        return " AND " + prefix + "company_id = $ownCompany";
    }

    public string OwnRowsClauseFor(string? alias = null)
    {
        var company = CurrentCompanyId();
        if (company is null)
            return "";

        var prefix = string.IsNullOrWhiteSpace(alias) ? "" : alias + ".";
        return " AND " + prefix + "company_id = '" + company.Replace("'", "''") + "'";
    }

    /// <summary>Папка с данными кассы — туда складываются файлы, привязанные к записям базы
    /// (например, накладные и фотографии в перемещениях).</summary>
    public string DataFolder => Path.GetDirectoryName(DbPath) ?? AppContext.BaseDirectory;

    /// <summary>2026-10-29 (аварийный аудит кассы, п. 1 и 9): строка подключения ЭТОГО класса (чеки офлайн-очереди, смена, списания…)
    /// отличается от строки каталога (LocalProductRepository: «Data Source=…» без добавок), поэтому у неё свой пул соединений.
    /// Раньше пул был общий, и соединение каталога с synchronous=NORMAL могло достаться записи офлайн-чека — при пропаже питания
    /// последние чеки откатывались. Default Timeout=5 — ожидание блокировки базы 5 с (было 30: кассир видел «Проводим оплату…»
    /// полминуты, потом английский «SQLite Error 5»).</summary>
    private static readonly string ConnectionString = $"Data Source={DbPath};Default Timeout=5";

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        // synchronous=FULL — запись чека и смены доходит до диска до подтверждения (WAL + NORMAL теряет последние записи при
        // пропаже питания). busy_timeout повторяем явно: он действует на само соединение, а не на пул.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static bool _integrityChecked;
    private static string? _startupNotice;

    /// <summary>2026-10-29 (аварийный аудит кассы, п. 7): сообщение для кассира, если при запуске база оказалась повреждена и была
    /// заменена копией (или создана заново). Показывается один раз главным окном, затем null.</summary>
    public static string? TakeStartupNotice()
    {
        lock (typeof(DatabaseService))
        {
            var notice = _startupNotice;
            _startupNotice = null;
            return notice;
        }
    }

    /// <summary>2026-10-29: быстрая проверка целостности pos_local.db при первом открытии за запуск. Повреждённый файл не удаляется —
    /// переименовывается (pos_local.corrupt_*.db), касса берёт самую свежую целую копию из backups; нет копии — пустая база
    /// (каталог придёт с сервера). Проверка не удалась из-за блокировки/диска — просто продолжаем, как раньше.</summary>
    private static void VerifyDatabaseAtStartup()
    {
        if (_integrityChecked)
            return;
        _integrityChecked = true;

        try
        {
            if (!File.Exists(DbPath))
                return;

            var verdict = QuickCheck(DbPath);
            if (verdict is null || verdict == "ok")
                return;

            PosLogger.Log($"pos_local.db повреждена (quick_check: {verdict}) — пробую восстановить из резервной копии.", "ERROR");
            SqliteConnection.ClearAllPools();
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var corrupt = Path.Combine(DataDirectory, $"pos_local.corrupt_{stamp}.db");
            File.Move(DbPath, corrupt);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(DbPath + suffix))
                    File.Move(DbPath + suffix, corrupt + suffix);
            }

            string? restoredFrom = null;
            if (Directory.Exists(BackupDirectory))
            {
                foreach (var backup in new DirectoryInfo(BackupDirectory).GetFiles("pos_local_*.db").OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    if (QuickCheck(backup.FullName) != "ok")
                        continue;
                    File.Copy(backup.FullName, DbPath, overwrite: true);
                    restoredFrom = backup.Name;
                    break;
                }
            }

            PosLogger.Log(restoredFrom is null
                ? $"pos_local.db: целой копии нет — создана пустая база, повреждённая сохранена как {Path.GetFileName(corrupt)}."
                : $"pos_local.db восстановлена из копии {restoredFrom}, повреждённая сохранена как {Path.GetFileName(corrupt)}.",
                "ERROR");
            _startupNotice = restoredFrom is null
                ? Tr.T("База данных кассы была повреждена и создана заново. Каталог загрузится с сервера, а чеки, не отправленные до сбоя, надо проверить: повреждённый файл сохранён рядом (pos_local.corrupt_*.db) — сообщите администратору.",
                    "Кассанын маалымат базасы бузулуп, кайра түзүлдү. Каталог серверден жүктөлөт, ал эми кырсыкка чейин жөнөтүлбөгөн чектерди текшерүү керек: бузулган файл жанында сакталды (pos_local.corrupt_*.db) — администраторго кабарлаңыз.",
                    "The till database was damaged and has been recreated. The catalog will load from the server, but receipts that were not sent before the failure must be checked: the damaged file is kept next to it (pos_local.corrupt_*.db) — tell your administrator.",
                    "Kasa veritabanı bozulmuştu ve yeniden oluşturuldu. Katalog sunucudan yüklenecek, ancak arızadan önce gönderilmemiş fişler kontrol edilmeli: bozuk dosya yanında saklandı (pos_local.corrupt_*.db) — yöneticinize bildirin.",
                    "Kassa ma'lumotlar bazasi shikastlangan va qaytadan yaratildi. Katalog serverdan yuklanadi, nosozlikdan oldin yuborilmagan cheklarni esa tekshirish kerak: shikastlangan fayl yonida saqlandi (pos_local.corrupt_*.db) — administratorga xabar bering.")
                : Tr.T($"База данных кассы была повреждена и восстановлена из копии ({restoredFrom}). Продажи после этой копии могли пропасть — сверьте последние чеки с сервером и сообщите администратору.",
                    $"Кассанын маалымат базасы бузулуп, көчүрмөдөн калыбына келтирилди ({restoredFrom}). Ошол көчүрмөдөн кийинки сатуулар жоголушу мүмкүн — акыркы чектерди сервер менен салыштырып, администраторго кабарлаңыз.",
                    $"The till database was damaged and restored from a backup ({restoredFrom}). Sales made after that backup may be missing — compare the latest receipts with the server and tell your administrator.",
                    $"Kasa veritabanı bozulmuştu ve yedekten geri yüklendi ({restoredFrom}). Bu yedekten sonraki satışlar eksik olabilir — son fişleri sunucuyla karşılaştırın ve yöneticinize bildirin.",
                    $"Kassa ma'lumotlar bazasi shikastlangan va nusxadan tiklandi ({restoredFrom}). Shu nusxadan keyingi sotuvlar yo'qolgan bo'lishi mumkin — oxirgi cheklarni server bilan solishtiring va administratorga xabar bering.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Проверка целостности pos_local.db не завершена: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    /// <summary>"ok" — база целая; другой текст — повреждена; null — проверить не удалось (занята, нет доступа).</summary>
    private static string? QuickCheck(string path)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Default Timeout=5");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            return command.ExecuteScalar()?.ToString();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            return $"{ex.SqliteErrorCode}: {ex.Message}"; // CORRUPT / NOTADB
        }
        catch (Exception ex)
        {
            PosLogger.Log($"quick_check {Path.GetFileName(path)} не выполнен: {ex.Message}", "WARNING");
            return null;
        }
    }

    /// <summary>Сливает WAL в основной файл (см. LocalProductRepository.OpenConnection, которое
    /// первым включает journal_mode=WAL — режим "липнет" на файл целиком, так что им пользуются
    /// и короткоживущие соединения этого класса). Без явного чекпоинта ничто в приложении не
    /// делало этого вообще (ни на выходе, ни периодически) — при резком завершении процесса
    /// (а не только принудительном taskkill /F) недописанный WAL для редко используемых таблиц
    /// (WriteOffHistory/ClientLoyalty/VoiceUnitWords/…) несколько раз за 2026-09-04 приводил к
    /// "database disk image is malformed". Вызывается из App.axaml.cs при штатном закрытии кассы
    /// и периодически из AvaloniaCatalogCacheService после каждой полной синхронизации каталога —
    /// самой частой операции записи в приложении, поэтому WAL никогда не копится надолго.</summary>
    public static void CheckpointWal()
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={DbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"WAL checkpoint failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Папка с резервными копиями — рядом с самой базой, в %AppData%.</summary>
    public static string BackupDirectory => Path.Combine(DataDirectory, "backups");

    /// <summary>Делает резервную копию базы и удаляет самые старые, оставляя
    /// <paramref name="keepCount"/> штук. Возвращает путь к созданной копии или null.
    ///
    /// Почему VACUUM INTO, а не File.Copy: копирование файла на живой базе даёт «рваную»
    /// копию — часть данных может лежать в ещё не слитом WAL, а часть страниц меняться прямо
    /// во время чтения. VACUUM INTO — штатная операция SQLite, она пишет согласованный
    /// снимок под своей блокировкой и заодно дефрагментирует его. WAL перед этим сливаем,
    /// чтобы снимок включал самые свежие продажи.
    ///
    /// Зачем это вообще: в базе живёт очередь непроведённых офлайн-продаж, история списаний,
    /// бонусные балансы клиентов и закупочные цены. Всё это существует в ЕДИНСТВЕННОМ
    /// экземпляре на диске кассы — сервер про офлайн-очередь ничего не знает по определению.</summary>
    public static string? CreateBackup(int keepCount = 7)
    {
        try
        {
            CheckpointWal();
            Directory.CreateDirectory(BackupDirectory);

            var target = Path.Combine(
                BackupDirectory,
                $"pos_local_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db");

            using (var connection = new SqliteConnection($"Data Source={DbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // Путь подставляем параметром — в нём может быть кириллица из имени пользователя
                // Windows, а склейка строк в SQL ещё и ломается на кавычках.
                command.CommandText = "VACUUM INTO $target;";
                command.Parameters.AddWithValue("$target", target);
                command.ExecuteNonQuery();
            }

            PruneOldBackups(keepCount);
            PosLogger.Log($"Резервная копия базы создана: {Path.GetFileName(target)}", "BACKUP");
            return target;
        }
        catch (Exception ex)
        {
            // Бэкап — страховка, а не условие работы кассы: не смогли скопировать (нет места,
            // нет прав) — пишем в лог и продолжаем торговать.
            PosLogger.Log($"Не удалось создать резервную копию базы: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return null;
        }
    }

    private static void PruneOldBackups(int keepCount)
    {
        if (keepCount < 1)
            keepCount = 1;

        var stale = new DirectoryInfo(BackupDirectory)
            .GetFiles("pos_local_*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(keepCount)
            .ToList();

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (Exception)
            {
                // Файл занят антивирусом или открыт — не беда, удалим в следующий раз.
            }
        }

        // 2026-11-05 (аудит №3, Н-11): копий 7, но база с каталогом на 15 тыс. товаров — сотни МБ, и 7 копий съедали гигабайты на слабом диске.
        // Сверх числа копий — предел по общему размеру (1,5 ГБ): самые старые убираем, но две последние оставляем всегда.
        try
        {
            const long maxTotalBytes = 1_500_000_000L;
            var left = new DirectoryInfo(BackupDirectory).GetFiles("pos_local_*.db").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var total = left.Sum(f => f.Length);
            for (var i = left.Count - 1; i >= 2 && total > maxTotalBytes; i--)
            {
                try
                {
                    var size = left[i].Length;
                    left[i].Delete();
                    total -= size;
                }
                catch (Exception)
                {
                    // занят — уберём в следующий раз
                }
            }
        }
        catch (Exception)
        {
            // каталог недоступен — предел применим в следующий раз
        }
    }

    /// <summary>Делает копию не чаще раза в сутки. Вызывается при старте в фоне: касса не
    /// должна ждать копирования базы, чтобы показать экран кассира.</summary>
    public static void EnsureDailyBackup(int keepCount = 7)
    {
        try
        {
            // Базы ещё нет (первый запуск до создания схемы) — копировать нечего, и открывать
            // соединение нельзя: SQLite создаст пустой файл и мы «забэкапим» пустоту.
            if (!File.Exists(DbPath))
                return;

            Directory.CreateDirectory(BackupDirectory);
            var newest = new DirectoryInfo(BackupDirectory)
                .GetFiles("pos_local_*.db")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is not null && (DateTime.UtcNow - newest.LastWriteTimeUtc) < TimeSpan.FromHours(20))
                return; // сегодня уже делали

            CreateBackup(keepCount);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Проверка ежедневной копии базы не удалась: {ex.Message}", "WARNING");
        }
    }
}
