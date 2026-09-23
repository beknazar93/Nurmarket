namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для журнала «Некорректные чеки» (таблица IrregularReceipts).</summary>
public static class IrregularReceiptsStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static List<IrregularReceiptEntry> LoadAll() => Db.LoadAllIrregularReceipts();

    public static void Append(IrregularReceiptEntry entry)
    {
        Db.AppendIrregularReceipt(entry);
        PosLogger.Log($"IRREGULAR receipt logged: id={entry.Id}, tag={entry.Tag}", "IRREGULAR");
    }
}
