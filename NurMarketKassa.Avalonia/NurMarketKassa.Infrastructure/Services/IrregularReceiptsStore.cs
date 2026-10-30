namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для журнала «Некорректные чеки» (таблица IrregularReceipts).</summary>
public static class IrregularReceiptsStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static List<IrregularReceiptEntry> LoadAll() => Db.LoadAllIrregularReceipts();

    /// <summary>2026-10-30: закрыть запись без отправки (скрыть из списка, причина и кто закрыл — в записи и в журнал кассы).</summary>
    public static bool Close(string id, string reason, string? by)
    {
        var entry = LoadAll().FirstOrDefault(e => e.Id == id);
        if (entry is null || entry.ClosedAt is not null)
            return false;
        entry.ClosedAt = DateTimeOffset.Now;
        entry.ClosedReason = reason;
        entry.ClosedBy = by;
        Db.AppendIrregularReceipt(entry);
        PosLogger.Log($"IRREGULAR receipt closed: id={id}, tag={entry.Tag}, by={by ?? "?"}, reason={reason}", "IRREGULAR");
        return true;
    }

    public static void Append(IrregularReceiptEntry entry)
    {
        Db.AppendIrregularReceipt(entry);
        PosLogger.Log($"IRREGULAR receipt logged: id={entry.Id}, tag={entry.Tag}", "IRREGULAR");
    }
}
