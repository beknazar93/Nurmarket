using MySqlConnector;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Core.Application;

public sealed class LocalStockLedger : ILocalStockLedger
{
    private readonly IMySqlConnectionSettings _settings;

    public LocalStockLedger(IMySqlConnectionSettings settings) => _settings = settings;

    public async Task<double> GetNetDeltaAsync(string productId, CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled || string.IsNullOrWhiteSpace(productId))
            return 0;

        await using var conn = new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT COALESCE(SUM(delta), 0)
            FROM stock_ledger
            WHERE product_id = @productId;
            """;

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.CommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@productId", productId.Trim());
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToDouble(result);
    }

    public async Task RecordSyncCorrectionAsync(
        string productId,
        double correctionDelta,
        string referenceId,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled || string.IsNullOrWhiteSpace(productId) || Math.Abs(correctionDelta) < 1e-9)
            return;

        await using var conn = new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            INSERT INTO stock_ledger (product_id, delta, reason, reference_id, device_name)
            VALUES (@productId, @delta, 'sync', @referenceId, @device);
            """;

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = _settings.CommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@productId", productId.Trim());
        cmd.Parameters.AddWithValue("@delta", correctionDelta);
        cmd.Parameters.AddWithValue("@referenceId", referenceId);
        cmd.Parameters.AddWithValue("@device", Environment.MachineName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
