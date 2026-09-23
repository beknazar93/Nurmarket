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

    /// <summary>Статусы, которые вообще могут оказаться "ждущими" — ими же фильтруется выборка в
    /// SQL, чтобы не разбирать JSON уже выгруженных продаж. Признак автономности живёт внутри
    /// json_data (колонки под него нет), поэтому его по-прежнему проверяет <see cref="IsPendingLike"/>
    /// уже в памяти — но только на этой короткой выборке, а не на всей таблице.</summary>
    private static readonly string[] PendingStatuses = [OfflineSaleEntry.PendingSync, OfflineSaleEntry.Syncing];

    public static int PendingCount => OfflineDatabase.LoadByStatus(PendingStatuses).Count(IsPendingLike);

    public static int SyncedCount => LoadAll().Count(s =>
        string.Equals(s.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase));

    public static int FailedCount => LoadAll().Count(s =>
        string.Equals(s.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<OfflineSaleEntry> LoadPendingForSync() =>
        OfflineDatabase.LoadByStatus(PendingStatuses)
            .Where(IsPendingLike)
            .OrderBy(x => x.CreatedAt)
            .ToList();

    public static OfflineSaleEntry? TryGetById(string id) => OfflineDatabase.TryGetById(id);

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

    /// <summary>Возвращает отклонённые чеки в очередь на выгрузку.
    ///
    /// До 2026-09-23 выхода из статуса failed не было вообще: MarkFailed(retryable: false)
    /// ставил его, LoadPendingForSync такие записи не выбирал, и ни одна строка кода их оттуда
    /// не доставала. Отказ сервера один раз — и деньги взяты, а продажи в NurCRM нет никогда,
    /// причём молча. Отказ бывает и временным по сути (товар удалили на сайте, а потом вернули),
    /// поэтому решение о повторе принимает владелец кнопкой, а не автоматика: молча повторять
    /// то, что сервер уже отверг, — прямой путь к дублям.</summary>
    /// <returns>Сколько чеков вернулось в очередь.</returns>
    public static int RequeueFailed()
    {
        var failed = OfflineDatabase.LoadByStatus([OfflineSaleEntry.Failed]);
        var count = 0;
        foreach (var entry in failed)
        {
            // Уже проведённые на сервере не трогаем: их повтор и есть дубль.
            if (entry.CheckoutCompleted)
                continue;

            Update(entry.Id, e =>
            {
                e.Status = OfflineSaleEntry.PendingSync;
                e.LastError = null;
            });
            count++;
        }

        if (count > 0)
            PosLogger.Log($"OFFLINE: {count} отклонённых чек(ов) возвращены в очередь вручную.", "OFFLINE");

        return count;
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

    // 2026-09-10: автономные продажи (см. OfflineSaleEntry.IsAutonomous) никогда не считаются
    // "ждущими" — им некуда синхронизироваться, и они не должны ни попадать в PendingCount
    // (баннер "В очереди" вводит в заблуждение), ни подхватываться SyncService.LoadPendingForSync
    // (иначе они могли бы однажды выгрузиться в чужой NurCRM-аккаунт, если тот же ПК потом
    // войдёт онлайн). Они остаются доступны для повторной печати через LoadAll().
    private static bool IsPendingLike(OfflineSaleEntry sale) =>
        !sale.IsAutonomous
        && (string.Equals(sale.Status, OfflineSaleEntry.PendingSync, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sale.Status, OfflineSaleEntry.Syncing, StringComparison.OrdinalIgnoreCase));
}
