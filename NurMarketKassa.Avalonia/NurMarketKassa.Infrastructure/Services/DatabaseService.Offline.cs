using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models.Local;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса DatabaseService (разбор огромного файла на partial-файлы), логика не менялась; остальное — DatabaseService.cs
public sealed partial class DatabaseService
{
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
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadAllOfflineSales)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
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

    /// <summary>2026-11-06: В5 24.2 (аудит скорости, п. 24) — число записей очереди с нужными статусами БЕЗ разбора JSON (SELECT COUNT(*) по индексу sync_status).
    /// Раньше «сколько выгруженных» считали полным чтением и разбором json_data всех продаж за всё время работы кассы.</summary>
    public int CountOfflineSalesByStatus(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Count == 0)
            return 0;

        using var slow = PerfSummary.MeasureSqlite(nameof(CountOfflineSalesByStatus));
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

            command.CommandText = $"SELECT COUNT(*) FROM OfflineSales WHERE sync_status IN ({string.Join(", ", names)});";
            return Convert.ToInt32(command.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>2026-11-06: В5 24.2 — все записи очереди, КРОМЕ выгруженных (<paramref name="excludedStatus"/>): JSON уже доставленных продаж не разбирается.
    /// На базе с тысячами выгруженных чеков и единицами ждущих читает единицы, а не всё. Порядок — как у <see cref="LoadAllOfflineSales"/>.</summary>
    public List<OfflineSaleEntry> LoadOfflineSalesExceptStatus(string excludedStatus)
    {
        using var slow = PerfSummary.MeasureSqlite(nameof(LoadOfflineSalesExceptStatus));
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<OfflineSaleEntry>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // «статус < X OR статус > X» (= «не X» для NOT NULL колонки) идёт по индексу статуса двумя диапазонами и пропускает все выгруженные строки;
            // «+created_at» не даёт планировщику вместо этого обходить всю таблицу по индексу даты (замер на 5000 выгруженных: 5 мс → 0,08 мс в SQLite).
            command.CommandText = "SELECT json_data FROM OfflineSales WHERE (sync_status < $excluded OR sync_status > $excluded) ORDER BY +created_at ASC;";
            command.Parameters.AddWithValue("$excluded", excludedStatus);
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

    /// <summary>Только строки с нужным статусом — колонка sync_status уже проиндексирована
    /// (idx_offline_sales_status). Разбирать JSON выгруженных продаж, чтобы посчитать ждущие,
    /// смысла нет: их в очереди обычно единицы, а всего строк — за всё время работы кассы.</summary>
    public List<OfflineSaleEntry> LoadOfflineSalesByStatus(IReadOnlyCollection<string> statuses)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadOfflineSalesByStatus)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
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
        using var slowSql = PerfSummary.MeasureSqlite(nameof(SaveAllOfflineSales)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
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
        using var slowSql = PerfSummary.MeasureSqlite(nameof(AppendOfflineSale)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
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
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadAllIrregularReceipts)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
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
    public void AppendWriteOffHistory(string productId, string productName, double quantity, string reason, string? cashierName,
        double? unitCost = null)
    {
        _dbLock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO WriteOffHistory (product_id, product_name, quantity, reason, cashier_name, created_at, company_id, unit_cost)
                VALUES ($productId, $productName, $quantity, $reason, $cashierName, $createdAt, $companyId, $unitCost);
                """;
            command.Parameters.AddWithValue("$unitCost", (object?)unitCost ?? DBNull.Value);
            command.Parameters.AddWithValue("$productId", productId);
            command.Parameters.AddWithValue("$productName", productName);
            command.Parameters.AddWithValue("$quantity", quantity);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$cashierName", (object?)cashierName ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$companyId", (object?)CurrentCompanyId() ?? DBNull.Value);
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
    /// <summary>Списания одного товара — для карточки «Движение товара». Ищем по названию:
    /// в разборе товар приходит из ABC, где есть только оно.</summary>
    public List<(double Quantity, string Reason, DateTime CreatedAt)> LoadWriteOffsForProduct(string productName)
    {
        var result = new List<(double, string, DateTime)>();
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT quantity, reason, created_at FROM WriteOffHistory WHERE product_name = $name COLLATE NOCASE"
                + OwnRowsClause(command) + " ORDER BY created_at DESC;";
            var pName = command.CreateParameter();
            pName.ParameterName = "$name";
            pName.Value = productName;
            command.Parameters.Add(pName);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!DateTime.TryParse(reader.GetString(2), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var created))
                    continue;
                result.Add((reader.GetDouble(0), reader.IsDBNull(1) ? "" : reader.GetString(1), created));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Списания товара не прочитаны: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>Движение товаров за период: проданное и списанное в одном списке, от свежего к
    /// старому. Нужен вкладке «Перемещение» на складе — до этого посмотреть, что уходило со
    /// склада, можно было только по одному товару за раз, через его карточку.
    ///
    /// Приход отдельной таблицей не ведётся: остаток приходит с сервера NurCRM целиком, поэтому
    /// здесь только расход — продажи и списания.</summary>
    public List<(string ProductName, double Quantity, string Kind, string Note, DateTime At)> LoadStockMovements(
        DateTime from, DateTime to, int limit = 500)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadStockMovements)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, double, string, string, DateTime)>();
            using var connection = OpenConnection();

            using (var sales = connection.CreateCommand())
            {
                sales.CommandText = "SELECT product_name, quantity, sold_at, source FROM SoldLineItems "
                    + "WHERE sold_at >= $from AND sold_at <= $to" + OwnRowsClause(sales)
                    + " ORDER BY sold_at DESC LIMIT $limit;";
                sales.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
                sales.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
                sales.Parameters.AddWithValue("$limit", limit);
                using var reader = sales.ExecuteReader();
                while (reader.Read())
                {
                    var at = DateTime.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                        ? parsed
                        : DateTime.MinValue;
                    list.Add((reader.GetString(0), reader.GetDouble(1), "sale",
                        reader.IsDBNull(3) ? "" : reader.GetString(3), at));
                }
            }

            using (var writeOffs = connection.CreateCommand())
            {
                writeOffs.CommandText = "SELECT product_name, quantity, reason, cashier_name, created_at FROM WriteOffHistory "
                    + "WHERE created_at >= $from AND created_at <= $to" + OwnRowsClause(writeOffs)
                    + " ORDER BY created_at DESC LIMIT $limit;";
                writeOffs.Parameters.AddWithValue("$from", from.ToString("o", CultureInfo.InvariantCulture));
                writeOffs.Parameters.AddWithValue("$to", to.ToString("o", CultureInfo.InvariantCulture));
                writeOffs.Parameters.AddWithValue("$limit", limit);
                using var reader = writeOffs.ExecuteReader();
                while (reader.Read())
                {
                    var at = DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                        ? parsed
                        : DateTime.MinValue;
                    var note = reader.GetString(2);
                    if (!reader.IsDBNull(3))
                        note += " · " + reader.GetString(3);
                    list.Add((reader.GetString(0), reader.GetDouble(1), "writeoff", note, at));
                }
            }

            return list.OrderByDescending(x => x.Item5).Take(limit).ToList();
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Строки отчёта по списаниям — с товаром и себестоимостью (null у записей до 2026-09-24).</summary>
    public List<(string ProductId, string ProductName, double Quantity, string Reason, string? CashierName, DateTime CreatedAt, double? UnitCost)>
        LoadWriteOffReport(int limit)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadWriteOffReport)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, string, double, string, string?, DateTime, double?)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT product_id, product_name, quantity, reason, cashier_name, created_at, unit_cost FROM WriteOffHistory WHERE 1=1"
                + OwnRowsClause(command) + " ORDER BY created_at DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var createdAt = DateTime.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    createdAt,
                    reader.IsDBNull(6) ? null : reader.GetDouble(6)));
            }
            return list;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    public List<(string ProductName, double Quantity, string Reason, string? CashierName, DateTime CreatedAt)> LoadWriteOffHistory(int limit = 200)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadWriteOffHistory)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        _dbLock.EnterReadLock();
        try
        {
            var list = new List<(string, double, string, string?, DateTime)>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT product_name, quantity, reason, cashier_name, created_at FROM WriteOffHistory WHERE 1=1"
                + OwnRowsClause(command) + " ORDER BY created_at DESC LIMIT $limit;";
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
}
