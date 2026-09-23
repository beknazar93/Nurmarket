using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models.Local;

namespace NurMarketKassa.Services;

/// <summary>Единая локальная SQLite БД: Users, Products, OfflineSales.</summary>
public sealed class DatabaseService
{
    private static readonly string DataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
    private static readonly string DbPath = Path.Combine(DataDirectory, "pos_local.db");
    private static readonly string LegacyCatalogDbPath = Path.Combine(DataDirectory, "catalog.db");
    private static readonly string LegacyOfflineDbPath = Path.Combine(DataDirectory, "offline.db");

    private static DatabaseService? _instance;
    public static DatabaseService Instance => _instance ??= new DatabaseService();

    private readonly object _initLock = new();
    private bool _initialized;

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

    public void EnsureSchema()
    {
        lock (_initLock)
        {
            if (_initialized)
                return;

            Directory.CreateDirectory(DataDirectory);
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

                CREATE INDEX IF NOT EXISTS idx_products_barcode ON Products(barcode);
                CREATE INDEX IF NOT EXISTS idx_products_name ON Products(name COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_offline_sales_status ON OfflineSales(sync_status);
                CREATE INDEX IF NOT EXISTS idx_offline_sales_created ON OfflineSales(created_at);
                """;
            command.ExecuteNonQuery();

            MigrateLegacyCatalogDb(connection);
            MigrateLegacyOfflineDb(connection);
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

    #region Migrations

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
