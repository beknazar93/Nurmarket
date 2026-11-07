using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Models.Local;

namespace NurMarketKassa.Services;

// 2026-11-06: В15 — часть класса DatabaseService (разбор огромного файла на partial-файлы), логика не менялась; остальное — DatabaseService.cs
public sealed partial class DatabaseService
{
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

    /// <summary>2026-11-01 (code-review, Ж-01): сумма событий смены видов <paramref name="kinds"/> с этим примечанием (имя клиента у оплат долга) за всё время.</summary>
    public double SumShiftEventsByNote(string[] kinds, string note, DateTime? sinceUtc = null)
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < kinds.Length; i++)
            {
                names.Add("$k" + i);
                command.Parameters.AddWithValue("$k" + i, kinds[i]);
            }
            // 2026-11-01 (приёмка 1.17.72, С-2): только события не раньше sinceUtc (дата продажи чека) — оплаты старых долгов клиента к этому чеку не относятся.
            command.CommandText = $"SELECT COALESCE(SUM(amount), 0) FROM ShiftEvents WHERE kind IN ({string.Join(",", names)}) AND note = $note"
                                  + (sinceUtc is null ? ";" : " AND created_at >= $since;");
            command.Parameters.AddWithValue("$note", note);
            if (sinceUtc is { } since)
                command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
            return Convert.ToDouble(command.ExecuteScalar() ?? 0.0, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShiftEvents sum by note read failed: {ex.Message}", "WARNING");
            return 0;
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

    /// <summary>Записи одного вида за смену — для подробного отчёта по плитке смены.</summary>
    public List<(DateTime CreatedAt, double Amount, string? Note)> ListShiftEvents(string shiftId, string kind)
    {
        var list = new List<(DateTime, double, string?)>();
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT created_at, amount, note FROM ShiftEvents WHERE shift_id = $shift AND kind = $kind ORDER BY created_at;";
            command.Parameters.AddWithValue("$shift", shiftId);
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var at = DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTime.MinValue;
                list.Add((at, reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShiftEvents list read failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return list;
    }

    /// <summary>Строки продаж по номерам продаж — товары чеков для подробного отчёта смены.</summary>
    public Dictionary<string, List<(string ProductName, double Quantity, double UnitPrice)>> LoadSoldLinesBySaleIds(IReadOnlyCollection<string> saleIds)
    {
        using var slowSql = PerfSummary.MeasureSqlite(nameof(LoadSoldLinesBySaleIds)); // 2026-11-06: В5 18.3 — запрос дольше 200 мс виден в журнале
        var result = new Dictionary<string, List<(string, double, double)>>(StringComparer.OrdinalIgnoreCase);
        if (saleIds.Count == 0)
            return result;

        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            // 2026-11-06: В5 К-1 (шаг 3, п. 8) — пачками по 500 номеров одним запросом вместо запроса на каждый чек. «+company_id»
            // (OwnRowsClause со skipIndex) не даёт планировщику взять индекс по компании и перебрать всю историю (300 тыс. строк: 2505 мс → ~2 мс).
            // Порядок строк внутри чека сохраняется: ORDER BY rowid, как и при чтении по одному (rowid — порядок записи).
            var distinct = saleIds.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (var offset = 0; offset < distinct.Count; offset += SaleIdBatchSize)
            {
                var batch = distinct.Skip(offset).Take(SaleIdBatchSize).ToList();
                using var command = connection.CreateCommand();
                var names = new string[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    names[i] = "$s" + i;
                    command.Parameters.AddWithValue(names[i], batch[i]);
                }
                command.CommandText = "SELECT sale_id, product_name, quantity, unit_price FROM SoldLineItems WHERE sale_id IN (" + string.Join(",", names) + ")"
                    + OwnRowsClause(command, skipIndex: true) + " ORDER BY rowid;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var saleId = reader.GetString(0);
                    if (!result.TryGetValue(saleId, out var lines))
                        result[saleId] = lines = new List<(string, double, double)>();
                    lines.Add((reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3)));
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"SoldLineItems by sale read failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return result;
    }

    public Dictionary<string, double> GetShiftEventTotals(string shiftId) =>
        QueryShiftEventTotals("WHERE shift_id = $shift", ("$shift", shiftId));

    /// <summary>Сумма событий одного вида за период без событий по указанным чекам. Источник
    /// события — «номер чека:время» (ShiftEventsStore.OperationKey), по нему и отсекаем.</summary>
    public double SumShiftEventsBetweenExcluding(string kind, DateTime fromUtc, DateTime toUtc, IReadOnlySet<string> excludedSaleIds)
    {
        var sum = 0.0;
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_id, amount FROM ShiftEvents WHERE kind = $kind AND created_at >= $from AND created_at < $to;";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
            command.Parameters.AddWithValue("$to", toUtc.ToString("O"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var source = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var colon = source.IndexOf(':');
                var saleId = colon > 0 ? source[..colon] : source;
                if (excludedSaleIds.Contains(saleId))
                    continue;
                sum += reader.GetDouble(1);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ShiftEvents sum read failed: {ex.Message}", "WARNING");
        }
        finally
        {
            _dbLock.ExitReadLock();
        }

        return sum;
    }

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
    /// <summary>2026-10-05: все локальные бонусные балансы — для разовой выгрузки на сервер (ServerLoyalty).</summary>
    public List<(string ClientId, double Balance)> GetAllClientLoyaltyBalances()
    {
        var list = new List<(string, double)>();
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT client_id, balance FROM ClientLoyalty WHERE balance > 0;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetString(0), reader.GetDouble(1)));
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
        return list;
    }

    /// <summary>2026-10-05: сколько баллов ещё не отменено по продаже (для отмены на сервере при возврате чека).</summary>
    public (string ClientId, double Remaining)? GetRemainingClientLoyaltyForSale(string saleId)
    {
        _dbLock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT client_id, delta, reversed_delta FROM ClientLoyaltyTransactions WHERE sale_id = $saleId;";
            command.Parameters.AddWithValue("$saleId", saleId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return (reader.GetString(0), reader.GetDouble(1) - reader.GetDouble(2));
        }
        finally
        {
            _dbLock.ExitReadLock();
        }
    }

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
}
