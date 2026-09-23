using MySqlConnector;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Core.Application;

public sealed class InventoryService : IInventoryService
{
    private readonly IMySqlConnectionSettings _settings;
    private readonly IStockAuditWriter _auditWriter;

    public InventoryService(IMySqlConnectionSettings settings, IStockAuditWriter auditWriter)
    {
        _settings = settings;
        _auditWriter = auditWriter;
    }

    public async Task<bool> CommitRevisionAsync(
        IReadOnlyList<InventoryLineDto> lines,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled || lines.Count == 0)
            return false;

        var referenceId = $"revision-{userId}-{Guid.NewGuid():N}";
        var wroteAny = false;

        await using var conn = new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line.ProductId))
                    continue;

                var delta = line.ActualQty - line.ExpectedQty;
                if (Math.Abs(delta) < 1e-9)
                    continue;

                await InsertLedgerAsync(
                    conn,
                    tx,
                    line.ProductId,
                    delta,
                    "inventory",
                    referenceId,
                    cancellationToken).ConfigureAwait(false);

                _auditWriter.LogStock(line.ProductId, Math.Abs(delta), "inventory");
                wroteAny = true;
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return wroteAny;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> WriteOffProductAsync(
        string productId,
        double quantity,
        string reason,
        string authorizedBy,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsEnabled || string.IsNullOrWhiteSpace(productId) || quantity <= 0)
            return false;

        var referenceId = $"writeoff-{authorizedBy}-{Guid.NewGuid():N}";
        var auditReason = string.IsNullOrWhiteSpace(reason) ? "writeoff" : reason.Trim();

        await using var conn = new MySqlConnection(_settings.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        await InsertLedgerAsync(
            conn,
            null,
            productId,
            -Math.Abs(quantity),
            "writeoff",
            referenceId,
            cancellationToken).ConfigureAwait(false);

        _auditWriter.LogStock(productId, quantity, $"writeoff:{auditReason}:{authorizedBy}");
        return true;
    }

    private async Task InsertLedgerAsync(
        MySqlConnection conn,
        MySqlTransaction? tx,
        string productId,
        double delta,
        string reason,
        string referenceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO stock_ledger (product_id, delta, reason, reference_id, device_name)
            VALUES (@productId, @delta, @reason, @referenceId, @device);
            """;

        await using var cmd = new MySqlCommand(sql, conn, tx);
        cmd.CommandTimeout = _settings.CommandTimeoutSeconds;
        cmd.Parameters.AddWithValue("@productId", productId.Trim());
        cmd.Parameters.AddWithValue("@delta", delta);
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.Parameters.AddWithValue("@referenceId", referenceId);
        cmd.Parameters.AddWithValue("@device", Environment.MachineName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
