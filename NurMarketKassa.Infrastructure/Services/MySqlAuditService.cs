using System.Text.Json;
using MySqlConnector;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

/// <summary>Локальный аудит событий кассы в MySQL для мониторинга и отчётности.</summary>
public sealed class MySqlAuditService : IDisposable
{
    private readonly MySqlSettings _settings;
    private bool _schemaReady;

    public MySqlAuditService(MySqlSettings settings) => _settings = settings;

    public bool IsEnabled => _settings.Enabled && !string.IsNullOrWhiteSpace(_settings.ConnectionString);

    public void Initialize()
    {
        if (!IsEnabled)
            return;

        try
        {
            EnsureSchema();
            _schemaReady = true;
            LogEvent("system", "mysql_connected", new { machine = Environment.MachineName });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"MySQL init: {ex.Message}", "MYSQL");
            _schemaReady = false;
        }
    }

    public void LogEvent(string category, string action, object? details = null, string? userId = null)
    {
        if (!IsEnabled || !_schemaReady)
            return;

        _ = Task.Run(() => WriteEventAsync(category, action, details, userId));
    }

    public void LogSale(string cartId, double total, string paymentMethod, string? cashierId = null) =>
        LogEvent("sale", "checkout", new { cartId, total, paymentMethod }, cashierId);

    public void LogShift(string action, decimal? balance, string? shiftId = null) =>
        LogEvent("shift", action, new { shiftId, balance });

    public void LogStock(string productId, double quantity, string reason) =>
        LogEvent("stock", reason, new { productId, quantity });

    public void LogFavorite(string productId, bool isFavorite) =>
        LogEvent("catalog", isFavorite ? "favorite_on" : "favorite_off", new { productId });

    private void EnsureSchema()
    {
        using var conn = CreateConnection();
        conn.Open();

        const string sql = """
            CREATE TABLE IF NOT EXISTS audit_events (
              id BIGINT AUTO_INCREMENT PRIMARY KEY,
              created_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
              category VARCHAR(64) NOT NULL,
              action VARCHAR(128) NOT NULL,
              user_id VARCHAR(128) NULL,
              device_name VARCHAR(128) NULL,
              details_json JSON NULL,
              INDEX idx_created_at (created_at),
              INDEX idx_category_action (category, action)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.CommandTimeoutSeconds;
        cmd.ExecuteNonQuery();
    }

    private async Task WriteEventAsync(string category, string action, object? details, string? userId)
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync().ConfigureAwait(false);

            const string sql = """
                INSERT INTO audit_events (category, action, user_id, device_name, details_json)
                VALUES (@category, @action, @userId, @device, @details);
                """;

            await using var cmd = new MySqlCommand(sql, conn);
            cmd.CommandTimeout = _settings.CommandTimeoutSeconds;
            cmd.Parameters.AddWithValue("@category", category);
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@userId", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@device", Environment.MachineName);
            cmd.Parameters.AddWithValue("@details", details == null ? DBNull.Value : JsonSerializer.Serialize(details));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"MySQL write: {ex.Message}", "MYSQL");
        }
    }

    private MySqlConnection CreateConnection() => new(_settings.ConnectionString);

    public void Dispose() { }
}
