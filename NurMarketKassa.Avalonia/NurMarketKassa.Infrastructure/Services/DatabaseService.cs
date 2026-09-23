using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models.Local;

namespace NurMarketKassa.Services;

/// <summary>Единая локальная SQLite БД: Users, Products, OfflineSales.</summary>
public sealed class DatabaseService
{
    // 2026-09-13, живой риск потери данных: раньше БД лежала рядом с exe (AppDomain.
    // CurrentDomain.BaseDirectory\data). Эта касса обновляется через Velopack, который на
    // каждое обновление подкладывает новую версию в свою собственную папку — офлайн-очередь
    // непроведённых продаж (OfflineSales) в старой папке становится невидимой для новой
    // версии сразу после каждого автообновления. %AppData% — то же самое место, что уже
    // используют OfflineAuthSessionStore/UserPreferences для этого продукта — стабильно
    // независимо от того, в какую папку Velopack развернул текущую версию.
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NurMarketKassa", "data");
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

    /// <summary>Переоткрыть базу после того, как AccountDataIsolation подменил набор данных:
    /// путь тот же, но файл под ним стал другим (или его ещё нет). Без сброса признака
    /// инициализации схема на новой базе не накатилась бы — EnsureSchema просто вышел бы.
    /// Пул соединений чистим первым: иначе переиспользованное соединение продолжит держать
    /// уже отложенный файл прежней компании.</summary>
    public void ReopenAfterAccountSwitch()
    {
        _legacyImportDone = true;
        SqliteConnection.ClearAllPools();
        lock (_initLock)
            _initialized = false;
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
                    created_at TEXT NOT NULL
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
                    source TEXT NOT NULL DEFAULT 'local'
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

                CREATE INDEX IF NOT EXISTS idx_products_barcode ON Products(barcode);
                CREATE INDEX IF NOT EXISTS idx_products_name ON Products(name COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_offline_sales_status ON OfflineSales(sync_status);
                CREATE INDEX IF NOT EXISTS idx_offline_sales_created ON OfflineSales(created_at);
                CREATE INDEX IF NOT EXISTS idx_irregular_receipts_created ON IrregularReceipts(created_at);
                CREATE INDEX IF NOT EXISTS idx_sold_line_items_product ON SoldLineItems(product_id);
                CREATE INDEX IF NOT EXISTS idx_sold_line_items_sold_at ON SoldLineItems(sold_at);
                """;
            command.ExecuteNonQuery();

            MigrateLegacyCatalogDb(connection);
            MigrateLegacyOfflineDb(connection);
            if (!_legacyImportDone)
                MigrateLegacyJsonSales(connection);

            _initialized = true;
        }
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
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

    #region Users

    /// <summary>
    /// One-way security migration. Older builds stored a DPAPI-encrypted password
    /// for password-based offline login; token-based auth must never retain it.
    /// </summary>
    public void PurgeLegacySavedPasswords()
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Users SET password_encrypted = '', remember_me = 0;";
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public void SaveRememberedUser(string email, string plainPassword, bool rememberMe, string? userId = null, string? cashierName = null)
    {
        if (string.IsNullOrWhiteSpace(email))
            return;

        // Compatibility entry point for older views. Password persistence is
        // disabled; Remember Me now stores tokens through IAuthSessionManager.
        var normalizedEmail = email.Trim();
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Users WHERE email = $email;";
            command.Parameters.AddWithValue("$email", normalizedEmail);
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public LocalUserRecord? TryGetRememberedUser(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, email, password_encrypted, remember_me, last_login_at, user_id, cashier_name
                FROM Users
                WHERE email = $email AND remember_me = 1
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$email", email.Trim());
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new LocalUserRecord
            {
                Id = reader.GetInt64(0),
                Email = reader.GetString(1),
                PasswordEncrypted = reader.GetString(2),
                RememberMe = reader.GetInt32(3) == 1,
                LastLoginAt = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
                UserId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CashierName = reader.IsDBNull(6) ? null : reader.GetString(6),
            };
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    public LocalUserRecord? TryGetLastRememberedUser()
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, email, password_encrypted, remember_me, last_login_at, user_id, cashier_name
                FROM Users
                WHERE remember_me = 1
                ORDER BY last_login_at DESC
                LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return new LocalUserRecord
            {
                Id = reader.GetInt64(0),
                Email = reader.GetString(1),
                PasswordEncrypted = reader.GetString(2),
                RememberMe = reader.GetInt32(3) == 1,
                LastLoginAt = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
                UserId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CashierName = reader.IsDBNull(6) ? null : reader.GetString(6),
            };
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    public bool ValidateOfflineCredentials(string email, string plainPassword) =>
        false; // password-based offline login was intentionally removed

    #endregion

    #region OfflineSales

    public List<OfflineSaleEntry> LoadAllOfflineSales()
    {
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<OfflineSaleEntry>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json_data FROM OfflineSales ORDER BY created_at ASC;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var entry = JsonSerializer.Deserialize<OfflineSaleEntry>(json, JsonOpts);
                if (entry != null)
                    list.Add(entry);
            }

            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Одна запись по id вместо чтения и разбора всей таблицы. Во время выгрузки очереди
    /// на каждую продажу приходилось 4 обращения (MarkSyncing → Update → TryGetById, MarkSynced →
    /// то же самое), и каждое разворачивало JSON всех накопленных продаж — на очереди в 300 чеков
    /// это тысячи полных разборов таблицы за один проход синхронизации.</summary>
    public OfflineSaleEntry? TryGetOfflineSaleById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json_data FROM OfflineSales WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? JsonSerializer.Deserialize<OfflineSaleEntry>(reader.GetString(0), JsonOpts)
                : null;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Только строки с нужным статусом — колонка sync_status уже проиндексирована
    /// (idx_offline_sales_status). Разбирать JSON выгруженных продаж, чтобы посчитать ждущие,
    /// смысла нет: их в очереди обычно единицы, а всего строк — за всё время работы кассы.</summary>
    public List<OfflineSaleEntry> LoadOfflineSalesByStatus(IReadOnlyCollection<string> statuses)
    {
        var list = new List<OfflineSaleEntry>();
        if (statuses.Count == 0)
            return list;

        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            var names = new List<string>(statuses.Count);
            var i = 0;
            foreach (var status in statuses)
            {
                var name = "$s" + i++;
                names.Add(name);
                command.Parameters.AddWithValue(name, status);
            }

            command.CommandText =
                $"SELECT json_data FROM OfflineSales WHERE sync_status IN ({string.Join(", ", names)}) ORDER BY created_at ASC;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var entry = JsonSerializer.Deserialize<OfflineSaleEntry>(reader.GetString(0), JsonOpts);
                if (entry != null)
                    list.Add(entry);
            }

            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    public void SaveAllOfflineSales(IReadOnlyList<OfflineSaleEntry> items)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();

            var incomingIds = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
            var toDelete = new List<string>();
            using (var existingCmd = connection.CreateCommand())
            {
                existingCmd.Transaction = tx;
                existingCmd.CommandText = "SELECT id FROM OfflineSales;";
                using var reader = existingCmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!incomingIds.Contains(id))
                        toDelete.Add(id);
                }
            }

            foreach (var id in toDelete)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM OfflineSales WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            foreach (var entry in items)
                UpsertOfflineSale(connection, tx, entry);

            tx.Commit();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public void AppendOfflineSale(OfflineSaleEntry entry)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            UpsertOfflineSale(connection, tx, entry);
            tx.Commit();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public void UpdateOfflineSale(OfflineSaleEntry entry)
    {
        AppendOfflineSale(entry);
    }

    public void RemoveOfflineSaleIds(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
            return;

        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            foreach (var id in set)
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = "DELETE FROM OfflineSales WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            tx.Commit();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    private static void UpsertOfflineSale(SqliteConnection connection, SqliteTransaction tx, OfflineSaleEntry entry)
    {
        var isSynced = string.Equals(entry.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO OfflineSales (id, created_at, receipt_number, json_data, sync_status, is_synced)
            VALUES ($id, $createdAt, $receiptNumber, $jsonData, $syncStatus, $isSynced)
            ON CONFLICT(id) DO UPDATE SET
                created_at = excluded.created_at,
                receipt_number = excluded.receipt_number,
                json_data = excluded.json_data,
                sync_status = excluded.sync_status,
                is_synced = excluded.is_synced;
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$receiptNumber", BuildReceiptNumber(entry));
        command.Parameters.AddWithValue("$jsonData", JsonSerializer.Serialize(entry, JsonOpts));
        command.Parameters.AddWithValue("$syncStatus", entry.Status ?? OfflineSaleEntry.PendingSync);
        command.Parameters.AddWithValue("$isSynced", isSynced);
        command.ExecuteNonQuery();
    }

    private static string BuildReceiptNumber(OfflineSaleEntry entry) =>
        entry.CreatedAt.LocalDateTime.ToString("yyyyMMdd-HHmmss");

    #endregion

    #region IrregularReceipts

    public List<IrregularReceiptEntry> LoadAllIrregularReceipts()
    {
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<IrregularReceiptEntry>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json_data FROM IrregularReceipts ORDER BY created_at DESC;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.GetString(0);
                var entry = JsonSerializer.Deserialize<IrregularReceiptEntry>(json, JsonOpts);
                if (entry != null)
                    list.Add(entry);
            }

            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    public void AppendIrregularReceipt(IrregularReceiptEntry entry)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO IrregularReceipts (id, created_at, tag, json_data)
                VALUES ($id, $createdAt, $tag, $jsonData)
                ON CONFLICT(id) DO UPDATE SET
                    created_at = excluded.created_at,
                    tag = excluded.tag,
                    json_data = excluded.json_data;
                """;
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$createdAt", entry.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$tag", entry.Tag);
            command.Parameters.AddWithValue("$jsonData", JsonSerializer.Serialize(entry, JsonOpts));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    #endregion

    #region WriteOffHistory

    /// <summary>Записывает акт списания — API ревизии/списания (IInventoryApiService) не даёт
    /// прочитать историю обратно (только Create/Apply), поэтому касса ведёт свой локальный
    /// журнал (AI-фичи 2026-09-04).</summary>
    public void AppendWriteOffHistory(string productId, string productName, double quantity, string reason, string? cashierName)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO WriteOffHistory (product_id, product_name, quantity, reason, cashier_name, created_at)
                VALUES ($productId, $productName, $quantity, $reason, $cashierName, $createdAt);
                """;
            command.Parameters.AddWithValue("$productId", productId);
            command.Parameters.AddWithValue("$productName", productName);
            command.Parameters.AddWithValue("$quantity", quantity);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$cashierName", (object?)cashierName ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>Разово чинит старые записи, где вместо имени кассира сохранился сырой GUID
    /// (WriteOffAsync до 2026-09-04 передавал PosApp.CurrentUserId вместо DisplayName) —
    /// подменяет их на реальное имя, как только оно стало известно для этого пользователя.</summary>
    public void BackfillWriteOffCashierName(string userId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(displayName) || userId == displayName)
            return;

        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE WriteOffHistory SET cashier_name = $displayName WHERE cashier_name = $userId;";
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$userId", userId);
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>Последние записи журнала списаний, самые новые первые.</summary>
    public List<(string ProductName, double Quantity, string Reason, string? CashierName, DateTime CreatedAt)> LoadWriteOffHistory(int limit = 200)
    {
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, double, string, string?, DateTime)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT product_name, quantity, reason, cashier_name, created_at FROM WriteOffHistory ORDER BY created_at DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var createdAt = DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add((
                    reader.GetString(0),
                    reader.GetDouble(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    createdAt));
            }
            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    #endregion

    #region LocalOwnerAccount

    /// <summary>2026-09-09: единственный локальный владелец автономного (офлайн, без NurCRM)
    /// режима — заводится один раз сразу после успешной активации ключом (см.
    /// GithubLicenseRegistryClient). Пароль хранится ТОЛЬКО как соль+PBKDF2-хеш, никогда в
    /// открытом виде — этим автономный режим сознательно отличается от LocalAccountsManager
    /// (тот кэширует УЖЕ существующий на сервере NurCRM-аккаунт, поэтому там пароль намеренно
    /// не хранится вообще — см. его комментарии; здесь же локальный аккаунт И ЕСТЬ единственный
    /// источник истины, хранить проверяемый секрет обязательно).</summary>
    public void UpsertLocalOwnerAccount(string email, string passwordSalt, string passwordHash, string? displayName)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO LocalOwnerAccount (email, password_salt, password_hash, display_name, created_at)
                VALUES ($email, $salt, $hash, $displayName, $createdAt)
                ON CONFLICT(email) DO UPDATE SET
                    password_salt = excluded.password_salt,
                    password_hash = excluded.password_hash,
                    display_name = excluded.display_name;
                """;
            command.Parameters.AddWithValue("$email", email.Trim());
            command.Parameters.AddWithValue("$salt", passwordSalt);
            command.Parameters.AddWithValue("$hash", passwordHash);
            command.Parameters.AddWithValue("$displayName", (object?)displayName?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public (string Email, string PasswordSalt, string PasswordHash, string? DisplayName)? FindLocalOwnerAccount(string? email = null)
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            if (email is null)
            {
                // Автономный режим предполагает РОВНО одного локального владельца на ПК —
                // при входе email можно не указывать, берём единственную запись.
                command.CommandText = "SELECT email, password_salt, password_hash, display_name FROM LocalOwnerAccount LIMIT 1;";
            }
            else
            {
                command.CommandText = "SELECT email, password_salt, password_hash, display_name FROM LocalOwnerAccount WHERE email = $email LIMIT 1;";
                command.Parameters.AddWithValue("$email", email.Trim());
            }

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    #endregion

    #region VoiceUnitWords

    /// <summary>Дополнительные слова-единицы измерения для голосового парсера (2026-09-04) —
    /// поверх встроенного русского/кыргызского списка в VoiceCommandParser. Позволяет кассиру
    /// добавить своё слово (например, "мешок", "ящик") без правки кода.</summary>
    public void AddVoiceUnitWord(string word, string? abbreviation)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO VoiceUnitWords (word, abbreviation, created_at)
                VALUES ($word, $abbreviation, $createdAt)
                ON CONFLICT(word) DO UPDATE SET abbreviation = excluded.abbreviation;
                """;
            command.Parameters.AddWithValue("$word", word.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("$abbreviation", (object?)abbreviation?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public void RemoveVoiceUnitWord(int id)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM VoiceUnitWords WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public List<(int Id, string Word, string? Abbreviation)> LoadVoiceUnitWords()
    {
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(int, string, string?)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, word, abbreviation FROM VoiceUnitWords ORDER BY word;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    #endregion

    #region VoiceProductAliases

    /// <summary>2026-09-08: "обучение" голосового помощника — фраза, которую кассир произносит,
    /// напрямую привязывается к конкретному товару (по id) в обход обычного поиска по словам
    /// названия (VoiceCommandParser.FindProducts). Нужно, когда офлайн-распознавание речи (Vosk)
    /// стабильно неверно слышит название товара, или когда кассиры используют разговорное
    /// название, не совпадающее со словами в карточке товара ("кола" вместо "Coca-Cola 0.5л").</summary>
    public void AddVoiceProductAlias(string phrase, string productId, string productTitle)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO VoiceProductAliases (phrase, product_id, product_title, created_at)
                VALUES ($phrase, $productId, $productTitle, $createdAt)
                ON CONFLICT(phrase) DO UPDATE SET product_id = excluded.product_id, product_title = excluded.product_title;
                """;
            command.Parameters.AddWithValue("$phrase", phrase.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("$productId", productId);
            command.Parameters.AddWithValue("$productTitle", productTitle);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public void RemoveVoiceProductAlias(int id)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM VoiceProductAliases WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public List<(int Id, string Phrase, string ProductId, string ProductTitle)> LoadVoiceProductAliases()
    {
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(int, string, string, string)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, phrase, product_id, product_title FROM VoiceProductAliases ORDER BY phrase;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    #endregion

    #region ClientLoyalty

    /// <summary>Бонусный баланс клиента — 0, если записи ещё нет (баланс никогда не начислялся/
    /// не списывался). Хранится ЛОКАЛЬНО на этой кассе — см. UserPreferences.LoyaltyEnabled.</summary>
    public double GetClientLoyaltyBalance(string clientId)
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT balance FROM ClientLoyalty WHERE client_id = $id;";
            command.Parameters.AddWithValue("$id", clientId);
            var raw = command.ExecuteScalar();
            return raw is double d ? d : (raw is null ? 0 : Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Начисляет (delta &gt; 0) или списывает (delta &lt; 0) бонусы. Баланс никогда не
    /// уходит ниже нуля (MAX(0, ...) в SQL) — защита от рассинхронизации, если один и тот же
    /// клиент случайно обслуживается на двух кассах одновременно (сейчас у пользователя одна
    /// касса, но баланс локальный, так что теоретически возможно в будущем).</summary>
    public void AdjustClientLoyaltyBalance(string clientId, double delta)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ClientLoyalty (client_id, balance, updated_at)
                VALUES ($id, MAX(0, $delta), $now)
                ON CONFLICT(client_id) DO UPDATE SET
                    balance = MAX(0, balance + $delta),
                    updated_at = $now;
                """;
            command.Parameters.AddWithValue("$id", clientId);
            command.Parameters.AddWithValue("$delta", delta);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public void RecordShiftEvent(string kind, string? shiftId, string? sourceId, double amount, string? note)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO ShiftEvents (kind, shift_id, source_id, amount, note, created_at)
                VALUES ($kind, $shift, $source, $amount, $note, $now);
                """;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$shift", (object?)shiftId ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$amount", amount);
            command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public Dictionary<string, double> GetShiftEventTotals(string shiftId) =>
        QueryShiftEventTotals("WHERE shift_id = $shift", ("$shift", shiftId));

    public Dictionary<string, double> GetShiftEventTotalsBetween(DateTime fromUtc, DateTime toUtc) =>
        QueryShiftEventTotals(
            "WHERE created_at >= $from AND created_at < $to",
            ("$from", fromUtc.ToString("O")),
            ("$to", toUtc.ToString("O")));

    private Dictionary<string, double> QueryShiftEventTotals(
        string where, params (string Name, string Value)[] parameters)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, COALESCE(SUM(amount), 0) FROM ShiftEvents " + where + " GROUP BY kind;";
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetDouble(1);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShiftEvents read failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return result;
    }

    public void SaveTelegramSubscriber(string chatId, string clientId, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(clientId))
            return;

        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // Покупатель может перейти по ссылке повторно (или по ссылке другой карточки) —
            // тогда привязка обновляется, а не дублируется.
            command.CommandText = """
                INSERT INTO TelegramSubscribers (chat_id, client_id, display_name, created_at)
                VALUES ($chatId, $clientId, $name, $now)
                ON CONFLICT(chat_id) DO UPDATE SET
                    client_id = excluded.client_id,
                    display_name = excluded.display_name;
                """;
            command.Parameters.AddWithValue("$chatId", chatId);
            command.Parameters.AddWithValue("$clientId", clientId);
            command.Parameters.AddWithValue("$name", (object?)displayName ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    public void DeleteTelegramSubscriber(string chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId))
            return;

        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TelegramSubscribers WHERE chat_id = $chatId;";
            command.Parameters.AddWithValue("$chatId", chatId);
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>Самый свежий чат покупателя: если он подписался с нескольких устройств,
    /// напоминание уходит на последнее — так меньше шансов написать в заброшенный чат.</summary>
    public string? GetTelegramChatIdForClient(string clientId) =>
        QueryTelegramScalar(
            "SELECT chat_id FROM TelegramSubscribers WHERE client_id = $value ORDER BY created_at DESC LIMIT 1;",
            clientId);

    public string? GetTelegramClientIdForChat(string chatId) =>
        QueryTelegramScalar("SELECT client_id FROM TelegramSubscribers WHERE chat_id = $value;", chatId);

    public int CountTelegramSubscribers()
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM TelegramSubscribers;";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"TelegramSubscribers count failed: {ex.Message}", "WARNING");
            return 0;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    private string? QueryTelegramScalar(string sql, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            return command.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"TelegramSubscribers read failed: {ex.Message}", "WARNING");
            return null;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Запоминает скидку и списанные бонусы проведённого чека. Повторный вызов для
    /// того же чека — no-op (ключ — id продажи), поэтому повтор офлайн-очереди не задвоит суммы.
    /// <paramref name="discountTotal"/> — ВСЯ скидка чека, включая бонусы;
    /// <paramref name="pointsRedeemed"/> — сколько из неё оплачено бонусами.</summary>
    public void RecordShiftSaleAdjustment(
        string saleId, string? shiftId, double discountTotal, double pointsRedeemed)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return;
        if (Math.Abs(discountTotal) < 1e-9 && Math.Abs(pointsRedeemed) < 1e-9)
            return;

        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO ShiftSaleAdjustments
                    (sale_id, shift_id, discount_total, points_redeemed, created_at)
                VALUES ($saleId, $shiftId, $discount, $points, $now);
                """;
            command.Parameters.AddWithValue("$saleId", saleId);
            command.Parameters.AddWithValue("$shiftId", (object?)shiftId ?? DBNull.Value);
            command.Parameters.AddWithValue("$discount", discountTotal);
            command.Parameters.AddWithValue("$points", pointsRedeemed);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>Скидки и оплата бонусами за период (UTC, границы включительно снизу и
    /// исключительно сверху) — для отчётов по дню/неделе/месяцу.</summary>
    public (double Discounts, double PointsRedeemed, int Receipts) GetShiftAdjustmentsBetween(
        DateTime fromUtc, DateTime toUtc) =>
        QueryShiftAdjustments(
            "WHERE created_at >= $from AND created_at < $to",
            ("$from", fromUtc.ToString("O")),
            ("$to", toUtc.ToString("O")));

    /// <summary>То же самое, но по конкретной смене — для X/Z-отчёта и итогов закрытия смены.</summary>
    public (double Discounts, double PointsRedeemed, int Receipts) GetShiftAdjustmentsForShift(string? shiftId) =>
        string.IsNullOrWhiteSpace(shiftId)
            ? (0, 0, 0)
            : QueryShiftAdjustments("WHERE shift_id = $shift", ("$shift", shiftId!));

    private (double Discounts, double PointsRedeemed, int Receipts) QueryShiftAdjustments(
        string where, params (string Name, string Value)[] parameters)
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COALESCE(SUM(discount_total), 0), COALESCE(SUM(points_redeemed), 0), COUNT(*) " +
                "FROM ShiftSaleAdjustments " + where + ";";
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return (0, 0, 0);

            return (reader.GetDouble(0), reader.GetDouble(1), reader.GetInt32(2));
        }
        catch (Exception ex)
        {
            // Отчёт не должен падать из-за этой подсобной таблицы.
            PosLogger.Log($"ShiftSaleAdjustments read failed: {ex.Message}", "WARNING");
            return (0, 0, 0);
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>2026-09-13, живой баг: возврат товара никак не отменял начисленные/списанные
    /// баллы лояльности этой продажи — покупатель сохранял бонусы за возвращённый товар
    /// навсегда, а списанные на оплату баллы не восстанавливались. Записывает связку
    /// "продажа → чистое изменение баллов" (earned - redeemed, см. CreditOrRedeemLoyaltyPoints),
    /// чтобы позже возврат мог её найти и отменить — INSERT OR IGNORE, т.к. на одну продажу
    /// должна быть ровно одна запись (повторный вызов для того же sale_id — no-op).</summary>
    public void RecordClientLoyaltyTransaction(string saleId, string clientId, double delta)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO ClientLoyaltyTransactions (sale_id, client_id, delta, reversed_delta, created_at)
                VALUES ($saleId, $clientId, $delta, 0, $now);
                """;
            command.Parameters.AddWithValue("$saleId", saleId);
            command.Parameters.AddWithValue("$clientId", clientId);
            command.Parameters.AddWithValue("$delta", delta);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>Отменяет ВЕСЬ ещё не отменённый остаток баллов по продаже (полный возврат чека) —
    /// вычитает (earned) или возвращает (redeemed) delta - reversed_delta клиенту одной
    /// операцией и помечает всё как отменённое. Идемпотентно: повторный вызов для уже полностью
    /// отменённой продажи ничего не делает (remaining == 0). Возвращает false, если для этой
    /// продажи не было записи (лояльность была выключена, либо продажа без клиента).</summary>
    public bool ReverseRemainingClientLoyaltyForSale(string saleId)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT client_id, delta, reversed_delta FROM ClientLoyaltyTransactions WHERE sale_id = $saleId;";
            select.Parameters.AddWithValue("$saleId", saleId);
            using var reader = select.ExecuteReader();
            if (!reader.Read())
                return false;

            var clientId = reader.GetString(0);
            var delta = reader.GetDouble(1);
            var reversed = reader.GetDouble(2);
            var remaining = delta - reversed;
            reader.Close();

            if (Math.Abs(remaining) < 1e-9)
                return true;

            using var updateBalance = connection.CreateCommand();
            updateBalance.CommandText = """
                INSERT INTO ClientLoyalty (client_id, balance, updated_at)
                VALUES ($id, MAX(0, -$remaining), $now)
                ON CONFLICT(client_id) DO UPDATE SET
                    balance = MAX(0, balance - $remaining),
                    updated_at = $now;
                """;
            updateBalance.Parameters.AddWithValue("$id", clientId);
            updateBalance.Parameters.AddWithValue("$remaining", remaining);
            updateBalance.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            updateBalance.ExecuteNonQuery();

            using var updateTx = connection.CreateCommand();
            updateTx.CommandText = "UPDATE ClientLoyaltyTransactions SET reversed_delta = delta WHERE sale_id = $saleId;";
            updateTx.Parameters.AddWithValue("$saleId", saleId);
            updateTx.ExecuteNonQuery();

            return true;
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    #endregion

    #region SoldLineItems

    /// <summary>Записывает проданные позиции чека — по одной строке на товар. Источник данных для
    /// прогноза пополнения склада (AI-фичи 2026-09-03, п.1). <paramref name="source"/> — "local"
    /// (записано сразу после продажи, см. BasketPanelViewModel) или "backfill" (подтянуто с сервера
    /// один раз, см. RestockSuggestionsWindow) — нужно, чтобы бэкфилл не задваивал уже локально
    /// записанные строки.</summary>
    public void AppendSoldLineItems(IEnumerable<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines, string source)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO SoldLineItems (product_id, product_name, quantity, unit_price, sold_at, source)
                VALUES ($productId, $productName, $quantity, $unitPrice, $soldAt, $source);
                """;
            var pProductId = command.CreateParameter(); pProductId.ParameterName = "$productId"; command.Parameters.Add(pProductId);
            var pProductName = command.CreateParameter(); pProductName.ParameterName = "$productName"; command.Parameters.Add(pProductName);
            var pQuantity = command.CreateParameter(); pQuantity.ParameterName = "$quantity"; command.Parameters.Add(pQuantity);
            var pUnitPrice = command.CreateParameter(); pUnitPrice.ParameterName = "$unitPrice"; command.Parameters.Add(pUnitPrice);
            var pSoldAt = command.CreateParameter(); pSoldAt.ParameterName = "$soldAt"; command.Parameters.Add(pSoldAt);
            var pSource = command.CreateParameter(); pSource.ParameterName = "$source"; command.Parameters.Add(pSource);
            pSource.Value = source;

            foreach (var line in lines)
            {
                pProductId.Value = line.ProductId;
                pProductName.Value = line.ProductName;
                pQuantity.Value = line.Quantity;
                pUnitPrice.Value = line.UnitPrice;
                pSoldAt.Value = line.SoldAt.ToString("O");
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            _dbLock.ExitWriteLock();
        }
    }

    /// <summary>Все проданные позиции с указанной даты (включительно) — используется для расчёта
    /// скорости продаж товара при построении подсказок пополнения склада.</summary>
    /// <summary>То же, что <see cref="LoadSoldLineItemsSince"/>, но с ЦЕНОЙ, по которой товар
    /// реально продали. Для выручки цена из каталога не годится: поштучная продажа из пачки
    /// уходит по своей цене (390 шт по 12 сом = 4 680), а в каталоге у того же товара стоит
    /// цена пачки — выручка по ней завышалась в разы.</summary>
    public List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)>
        LoadSoldLineItemsWithPriceSince(DateTime sinceUtc, DateTime? untilUtc = null)
    {
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, string, double, double, DateTime)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = untilUtc is null
                ? "SELECT product_id, product_name, quantity, unit_price, sold_at FROM SoldLineItems WHERE sold_at >= $since;"
                : "SELECT product_id, product_name, quantity, unit_price, sold_at FROM SoldLineItems WHERE sold_at >= $since AND sold_at < $until;";
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            if (untilUtc is { } until)
                command.Parameters.AddWithValue("$until", until.ToString("O"));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var soldAt = DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3), soldAt));
            }

            return list;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"SoldLineItems with price read failed: {ex.Message}", "WARNING");
            return new List<(string, string, double, double, DateTime)>();
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    public List<(string ProductId, string ProductName, double Quantity, DateTime SoldAt)> LoadSoldLineItemsSince(DateTime sinceUtc)
    {
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, string, double, DateTime)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT product_id, product_name, quantity, sold_at FROM SoldLineItems WHERE sold_at >= $since;";
            command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var soldAt = DateTime.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), soldAt));
            }
            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Самая ранняя дата, за которую уже есть записи продаж (любого источника) —
    /// используется, чтобы бэкфилл с сервера не дублировал то, что уже записано локально.</summary>
    public DateTime? GetEarliestSoldLineItemDate()
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MIN(sold_at) FROM SoldLineItems;";
            var raw = command.ExecuteScalar() as string;
            return !string.IsNullOrEmpty(raw)
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    #endregion

    #region Migrations

    /// <summary>Одноразовый перенос БД из старого расположения (рядом с exe) в новое
    /// (%AppData%, см. комментарий у DataDirectory) — иначе после первого автообновления на
    /// версию с этим фиксом непроведённые офлайн-продажи и локальный кэш оказались бы в
    /// старой (уже замененной Velopack-ом) папке версии, невидимой новой сборке. Ничего не
    /// делает, если в новом месте уже есть БД (перенос уже случился раньше или это чистая
    /// установка) или если рядом с exe ничего нет.</summary>
    private static void MigrateFromOldInstallDir()
    {
        try
        {
            if (File.Exists(DbPath) || !File.Exists(OldInstallDirDbPath))
                return;

            File.Copy(OldInstallDirDbPath, DbPath);
            PosLogger.Log($"Перенесена локальная БД из {OldInstallDirDbPath} в {DbPath}", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перенос локальной БД из старого расположения не удался: {ex}", "DB");
        }
    }

    private static void MigrateLegacyCatalogDb(SqliteConnection targetConnection)
    {
        if (!File.Exists(LegacyCatalogDbPath))
            return;

        try
        {
            using var countCmd = targetConnection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Products;";
            if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
                return;

            using var legacy = new SqliteConnection($"Data Source={LegacyCatalogDbPath}");
            legacy.Open();

            using (var copyProducts = targetConnection.CreateCommand())
            {
                copyProducts.CommandText = """
                    ATTACH DATABASE $legacy AS legacy_db;
                    INSERT OR IGNORE INTO Products
                        (id, barcode, name, price, stock, unit, is_favorite, must_weigh, image_url, category, brand, purchase_price)
                    SELECT id, barcode, name, price, stock, unit, is_favorite, must_weigh, image_url, category, brand, purchase_price
                    FROM legacy_db.local_products;
                    DETACH DATABASE legacy_db;
                    """;
                copyProducts.Parameters.AddWithValue("$legacy", LegacyCatalogDbPath);
                copyProducts.ExecuteNonQuery();
            }

            using (var copyMeta = targetConnection.CreateCommand())
            {
                copyMeta.CommandText = """
                    ATTACH DATABASE $legacy AS legacy_db;
                    INSERT OR IGNORE INTO catalog_meta (key, value)
                    SELECT key, value FROM legacy_db.catalog_meta;
                    DETACH DATABASE legacy_db;
                    """;
                copyMeta.Parameters.AddWithValue("$legacy", LegacyCatalogDbPath);
                copyMeta.ExecuteNonQuery();
            }

            PosLogger.Log($"Мигрирован каталог из {LegacyCatalogDbPath} в {DbPath}", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция catalog.db: {ex.Message}", "DB");
        }
    }

    private static void MigrateLegacyOfflineDb(SqliteConnection targetConnection)
    {
        if (!File.Exists(LegacyOfflineDbPath))
            return;

        try
        {
            using var countCmd = targetConnection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM OfflineSales;";
            if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
                return;

            using var copy = targetConnection.CreateCommand();
            copy.CommandText = """
                ATTACH DATABASE $legacy AS legacy_db;
                INSERT OR IGNORE INTO OfflineSales (id, created_at, receipt_number, json_data, sync_status, is_synced)
                SELECT id, created_at, receipt_number, json_data, sync_status,
                       CASE WHEN sync_status = 'synced' THEN 1 ELSE 0 END
                FROM legacy_db.pending_sales;
                DETACH DATABASE legacy_db;
                """;
            copy.Parameters.AddWithValue("$legacy", LegacyOfflineDbPath);
            copy.ExecuteNonQuery();

            PosLogger.Log($"Мигрирована офлайн-очередь из {LegacyOfflineDbPath} в {DbPath}", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция offline.db: {ex.Message}", "DB");
        }
    }

    private static void MigrateLegacyJsonSales(SqliteConnection connection)
    {
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM OfflineSales;";
        if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
            return;

        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NurMarketKassa",
            "offline_sales_pending.json");
        if (!File.Exists(legacyPath))
            return;

        try
        {
            var legacy = JsonSerializer.Deserialize<List<OfflineSaleEntry>>(File.ReadAllText(legacyPath), JsonOpts)
                         ?? new List<OfflineSaleEntry>();
            foreach (var entry in legacy)
                UpsertOfflineSale(connection, null!, entry);

            var backup = legacyPath + ".migrated";
            File.Move(legacyPath, backup, overwrite: true);
            PosLogger.Log($"Мигрировано {legacy.Count} офлайн-чеков из JSON в SQLite", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция offline_sales_pending.json: {ex.Message}", "DB");
        }
    }

    private static void MigrateUserPreferencesCredentials(SqliteConnection connection)
    {
        try
        {
            var prefs = UserPreferences.Instance;
            if (string.IsNullOrWhiteSpace(prefs.LastLoginEmail)
                || string.IsNullOrWhiteSpace(prefs.LastLoginPassword))
                return;

            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Users;";
            if (Convert.ToInt64(countCmd.ExecuteScalar()) > 0)
                return;

            var encrypted = WindowsDpapiHelper.ProtectToBase64(prefs.LastLoginPassword);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Users (email, password_encrypted, remember_me, last_login_at)
                VALUES ($email, $password, 1, $lastLogin);
                """;
            insert.Parameters.AddWithValue("$email", prefs.LastLoginEmail.Trim());
            insert.Parameters.AddWithValue("$password", encrypted);
            insert.Parameters.AddWithValue("$lastLogin", DateTimeOffset.Now.ToString("O"));
            insert.ExecuteNonQuery();

            prefs.LastLoginPassword = "";
            prefs.SaveToDisk();
            PosLogger.Log("Мигрированы учётные данные из user-settings.json в SQLite Users", "DB");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Миграция учётных данных: {ex.Message}", "DB");
        }
    }

    #endregion
}
