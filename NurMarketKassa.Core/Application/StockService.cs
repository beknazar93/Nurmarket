using MySqlConnector;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Core.Application;

public sealed class StockService : IStockService
{
    private readonly IMySqlConnectionSettings _settings;
    private bool _schemaReady;

    public StockService(IMySqlConnectionSettings settings) => _settings = settings;

    public void Initialize()
    {
        if (!_settings.IsEnabled)
            return;

        try
        {
            EnsureSchema();
            _schemaReady = true;
        }
        catch
        {
            _schemaReady = false;
        }
    }

    public async Task CommitSaleAsync(
        string referenceId,
        string productId,
        double quantity,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled || !_schemaReady || string.IsNullOrWhiteSpace(productId) || quantity <= 0)
            return;

        await using var conn = new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            INSERT INTO stock_ledger (product_id, delta, reason, reference_id, device_name)
            VALUES (@productId, @delta, 'sale', @referenceId, @device);
            """;

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.CommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@productId", productId.Trim());
        cmd.Parameters.AddWithValue("@delta", -Math.Abs(quantity));
        cmd.Parameters.AddWithValue("@referenceId", string.IsNullOrWhiteSpace(referenceId) ? DBNull.Value : referenceId);
        cmd.Parameters.AddWithValue("@device", Environment.MachineName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSchema()
    {
        using var conn = new MySqlConnection(_settings.ConnectionString);
        conn.Open();

        const string sql = """
            CREATE TABLE IF NOT EXISTS stock_ledger (
              id BIGINT AUTO_INCREMENT PRIMARY KEY,
              product_id VARCHAR(64) NOT NULL,
              delta DOUBLE NOT NULL,
              reason ENUM('sale','return','inventory','writeoff','sync') NOT NULL,
              reference_id VARCHAR(128),
              created_at DATETIME(3) DEFAULT CURRENT_TIMESTAMP(3),
              device_name VARCHAR(128),
              INDEX idx_product (product_id),
              INDEX idx_ref (reference_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.CommandTimeoutSeconds;
        cmd.ExecuteNonQuery();
    }
}
