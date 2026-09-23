namespace NurMarketKassa.Services;

public static class OfflinePendingSalesStore
{
    private static readonly object UpdateGate = new();
    public static List<OfflineSaleEntry> LoadAll() => OfflineDatabase.LoadAll();

    public static void SaveAll(List<OfflineSaleEntry> items) => OfflineDatabase.SaveAll(items);

    public static void Append(OfflineSaleEntry entry)
    {
        OfflineDatabase.Append(entry);
        PosLogger.Log($"OFFLINE queue append: id={entry.Id}, receipt={entry.CreatedAt:yyyyMMdd-HHmmss}", "OFFLINE");
    }

    public static int PendingCount => LoadAll().Count(IsPendingLike);

    public static int SyncedCount => LoadAll().Count(s =>
        string.Equals(s.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase));

    public static int FailedCount => LoadAll().Count(s =>
        string.Equals(s.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<OfflineSaleEntry> LoadPendingForSync() =>
        LoadAll()
            .Where(IsPendingLike)
            .OrderBy(x => x.CreatedAt)
            .ToList();

    public static OfflineSaleEntry? TryGetById(string id) =>
        LoadAll().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    public static void Update(string id, Action<OfflineSaleEntry> update)
    {
        lock (UpdateGate)
        {
            var entry = TryGetById(id);
            if (entry == null)
                return;
            update(entry);
            OfflineDatabase.UpdateEntry(entry);
        }
    }

    public static void MarkSyncing(string id)
    {
        Update(id, entry =>
        {
            entry.Status = OfflineSaleEntry.Syncing;
            entry.LastAttemptAt = DateTimeOffset.Now;
            entry.LastError = null;
        });
    }

    public static void MarkSynced(string id, string? saleId = null)
    {
        Update(id, entry =>
        {
            entry.Status = OfflineSaleEntry.Synced;
            entry.SyncedAt = DateTimeOffset.Now;
            entry.LastAttemptAt = entry.SyncedAt;
            entry.LastError = null;
            if (!string.IsNullOrWhiteSpace(saleId))
                entry.SyncedSaleId = saleId.Trim();
        });
    }

    public static void MarkFailed(string id, string error, bool retryable)
    {
        Update(id, entry =>
        {
            entry.Status = retryable ? OfflineSaleEntry.PendingSync : OfflineSaleEntry.Failed;
            entry.LastAttemptAt = DateTimeOffset.Now;
            entry.LastError = string.IsNullOrWhiteSpace(error) ? "Неизвестная ошибка синхронизации." : error.Trim();
        });
    }

    public static void RemoveSynced(string id) => OfflineDatabase.RemoveIds(new[] { id });

    private static bool IsPendingLike(OfflineSaleEntry sale) =>
        string.Equals(sale.Status, OfflineSaleEntry.PendingSync, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sale.Status, OfflineSaleEntry.Syncing, StringComparison.OrdinalIgnoreCase);
}
