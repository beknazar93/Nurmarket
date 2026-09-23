using System.Text.Json;
using MySqlConnector;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

public sealed class MySqlMonitorService
{
    private readonly MySqlSettings _settings;

    public MySqlMonitorService(MySqlSettings settings) => _settings = settings;

    public bool IsEnabled => _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.ConnectionString);

    public async Task VerifyConnectionAsync(CancellationToken ct = default)
    {
        if (!IsEnabled)
            throw new InvalidOperationException("MySQL отключён в appsettings.json (MySql.Enabled = false).");

        await using var conn = new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = "SELECT 1 FROM audit_events LIMIT 1;";
        await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = _settings.CommandTimeoutSeconds };
        try
        {
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        catch (MySqlException ex) when (ex.Number == 1146)
        {
            throw new InvalidOperationException(
                "Таблица audit_events не найдена. Запустите кассу NurMarketKassa хотя бы раз для инициализации схемы БД.", ex);
        }
    }

    public async Task<IReadOnlyList<ActiveTerminalRow>> GetActiveTerminalsAsync(int hours = 24, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return Array.Empty<ActiveTerminalRow>();

        const string sql = """
            SELECT device_name,
                   MAX(created_at) AS last_seen,
                   COUNT(*) AS event_count,
                   TIMESTAMPDIFF(MINUTE, MAX(created_at), NOW()) AS minutes_since_last
            FROM audit_events
            WHERE device_name IS NOT NULL AND device_name <> ''
              AND created_at >= DATE_SUB(NOW(), INTERVAL @hours HOUR)
            GROUP BY device_name
            ORDER BY last_seen DESC
            LIMIT 100;
            """;

        return await QueryAsync(sql, reader =>
        {
            var minutesSince = reader.IsDBNull(reader.GetOrdinal("minutes_since_last"))
                ? int.MaxValue
                : reader.GetInt32("minutes_since_last");

            return new ActiveTerminalRow
            {
                DeviceName = reader.GetString("device_name"),
                LastSeen = reader.GetDateTime("last_seen"),
                EventCount = reader.GetInt32("event_count"),
                Status = ResolveTerminalStatus(minutesSince),
            };
        }, ct, ("@hours", hours)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncLogRow>> GetSyncLogsAsync(int limit = 200, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return Array.Empty<SyncLogRow>();

        const string sql = """
            SELECT created_at, category, action, device_name, details_json
            FROM audit_events
            WHERE category IN ('system', 'stock', 'catalog', 'sale')
               OR action LIKE '%sync%'
            ORDER BY created_at DESC
            LIMIT @limit;
            """;

        return await QueryAsync(sql, reader => new SyncLogRow
        {
            CreatedAt = reader.GetDateTime("created_at"),
            Category = reader.GetString("category"),
            Action = reader.GetString("action"),
            DeviceName = reader.IsDBNull(reader.GetOrdinal("device_name")) ? "" : reader.GetString("device_name"),
            Details = reader.IsDBNull(reader.GetOrdinal("details_json")) ? "" : reader.GetString("details_json"),
        }, ct, ("@limit", limit)).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SalesSummaryRow>> GetSalesSummaryAsync(int days = 7, CancellationToken ct = default)
    {
        var to = DateTime.Today.AddDays(1);
        var from = DateTime.Today.AddDays(-Math.Max(1, days) + 1);
        return GetSalesSummaryAsync(from, to, ct);
    }

    public async Task<IReadOnlyList<SalesSummaryRow>> GetSalesSummaryAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return Array.Empty<SalesSummaryRow>();

        const string sql = """
            SELECT DATE(created_at) AS sale_date,
                   COUNT(*) AS sale_count,
                   SUM(CAST(JSON_UNQUOTE(JSON_EXTRACT(details_json, '$.total')) AS DECIMAL(18,2))) AS total_amount
            FROM audit_events
            WHERE category = 'sale' AND action = 'checkout'
              AND created_at >= @from AND created_at < @to
            GROUP BY DATE(created_at)
            ORDER BY sale_date DESC;
            """;

        return await QueryAsync(sql, reader => new SalesSummaryRow
        {
            Date = reader.GetDateTime("sale_date"),
            SaleCount = reader.GetInt32("sale_count"),
            TotalAmount = reader.IsDBNull(reader.GetOrdinal("total_amount")) ? 0m : reader.GetDecimal("total_amount"),
        }, ct, ("@from", from.Date), ("@to", to.Date)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StockLedgerRow>> GetStockLedgerAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
            return Array.Empty<StockLedgerRow>();

        const string sql = """
            SELECT created_at, product_id, delta, reason, reference_id, device_name
            FROM stock_ledger
            WHERE created_at >= @from AND created_at < @to
            ORDER BY created_at DESC;
            """;

        return await QueryAsync(sql, reader => new StockLedgerRow
        {
            CreatedAt = reader.GetDateTime("created_at"),
            ProductId = reader.GetString("product_id"),
            Delta = reader.GetDouble("delta"),
            Reason = reader.GetString("reason"),
            ReferenceId = reader.IsDBNull(reader.GetOrdinal("reference_id")) ? "" : reader.GetString("reference_id"),
            DeviceName = reader.IsDBNull(reader.GetOrdinal("device_name")) ? "" : reader.GetString("device_name"),
        }, ct, ("@from", from.Date), ("@to", to.Date)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StockSummaryRow>> GetStockEventsAsync(int limit = 200, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return Array.Empty<StockSummaryRow>();

        const string sql = """
            SELECT created_at, action, device_name, details_json
            FROM audit_events
            WHERE category = 'stock'
            ORDER BY created_at DESC
            LIMIT @limit;
            """;

        return await QueryAsync(sql, reader =>
        {
            var details = reader.IsDBNull(reader.GetOrdinal("details_json")) ? null : reader.GetString("details_json");
            string productId = "";
            double quantity = 0;
            if (!string.IsNullOrWhiteSpace(details))
            {
                try
                {
                    using var doc = JsonDocument.Parse(details);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("productId", out var pid))
                        productId = pid.ToString();
                    if (root.TryGetProperty("quantity", out var q) && q.TryGetDouble(out var qty))
                        quantity = qty;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Stock payload quantity parse failed: {ex.GetType().Name}", "DEBUG");
                }
            }

            return new StockSummaryRow
            {
                CreatedAt = reader.GetDateTime("created_at"),
                Action = reader.GetString("action"),
                DeviceName = reader.IsDBNull(reader.GetOrdinal("device_name")) ? "" : reader.GetString("device_name"),
                ProductId = productId,
                Quantity = quantity,
            };
        }, ct, ("@limit", limit)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        Func<MySqlDataReader, T> map,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        var result = new List<T>();
        try
        {
            await using var conn = new MySqlConnection(_settings.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = _settings.CommandTimeoutSeconds };
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(map(reader));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"MySQL monitor: {ex.Message}", "MYSQL");
            throw new InvalidOperationException($"Ошибка запроса к MySQL: {ex.Message}", ex);
        }

        return result;
    }

    public static TerminalStatus ResolveTerminalStatus(int minutesSinceLast)
    {
        if (minutesSinceLast < 5)
            return TerminalStatus.Online;
        if (minutesSinceLast <= 30)
            return TerminalStatus.Idle;
        return TerminalStatus.Offline;
    }

    public enum TerminalStatus
    {
        Online,
        Idle,
        Offline,
    }

    public sealed class ActiveTerminalRow
    {
        public string DeviceName { get; init; } = "";
        public DateTime LastSeen { get; init; }
        public int EventCount { get; init; }
        public TerminalStatus Status { get; init; }
        public string StatusLabel => Status switch
        {
            TerminalStatus.Online => "Online",
            TerminalStatus.Idle => "Idle",
            TerminalStatus.Offline => "Offline",
            _ => "—",
        };
    }

    public sealed class SyncLogRow
    {
        public DateTime CreatedAt { get; init; }
        public string Category { get; init; } = "";
        public string Action { get; init; } = "";
        public string DeviceName { get; init; } = "";
        public string Details { get; init; } = "";
    }

    public sealed class SalesSummaryRow
    {
        public DateTime Date { get; init; }
        public int SaleCount { get; init; }
        public decimal TotalAmount { get; init; }
    }

    public sealed class StockSummaryRow
    {
        public DateTime CreatedAt { get; init; }
        public string Action { get; init; } = "";
        public string DeviceName { get; init; } = "";
        public string ProductId { get; init; } = "";
        public double Quantity { get; init; }
    }

    public sealed class StockLedgerRow
    {
        public DateTime CreatedAt { get; init; }
        public string ProductId { get; init; } = "";
        public double Delta { get; init; }
        public string Reason { get; init; } = "";
        public string ReferenceId { get; init; } = "";
        public string DeviceName { get; init; } = "";
    }
}
