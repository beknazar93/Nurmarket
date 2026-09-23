using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Запись аудита остатков в MySQL через общий AuditDb Avalonia-хоста.
/// </summary>
public sealed class AvaloniaStockAuditWriter : IStockAuditWriter
{
    private readonly MySqlAuditService _auditDb;

    public AvaloniaStockAuditWriter(MySqlAuditService auditDb) => _auditDb = auditDb;

    public void LogStock(string productId, double quantity, string reason) =>
        _auditDb.LogStock(productId, quantity, reason);
}
