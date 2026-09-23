namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для офлайн-очереди продаж (таблица OfflineSales).</summary>
public static class OfflineDatabase
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static string DatabasePath => Db.DatabasePath;

    public static void EnsureSchema() => Db.EnsureSchema();

    public static List<OfflineSaleEntry> LoadAll() => Db.LoadAllOfflineSales();

    public static OfflineSaleEntry? TryGetById(string id) => Db.TryGetOfflineSaleById(id);

    public static List<OfflineSaleEntry> LoadByStatus(IReadOnlyCollection<string> statuses) =>
        Db.LoadOfflineSalesByStatus(statuses);

    public static void SaveAll(IReadOnlyList<OfflineSaleEntry> items) => Db.SaveAllOfflineSales(items);

    public static void Append(OfflineSaleEntry entry) => Db.AppendOfflineSale(entry);

    public static void UpdateEntry(OfflineSaleEntry entry) => Db.UpdateOfflineSale(entry);

    public static void RemoveIds(IEnumerable<string> ids) => Db.RemoveOfflineSaleIds(ids);
}
