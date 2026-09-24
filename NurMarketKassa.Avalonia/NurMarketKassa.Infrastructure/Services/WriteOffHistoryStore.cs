namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для журнала списаний (AI-фичи 2026-09-04) —
/// IInventoryApiService не даёt прочитать историю актов обратно, поэтому касса ведёт свою копию
/// локально, только для показа в интерфейсе (не источник истины для остатков).</summary>
public static class WriteOffHistoryStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static void Append(string productId, string productName, double quantity, string reason, string? cashierName,
        double? unitCost = null) =>
        Db.AppendWriteOffHistory(productId, productName, quantity, reason, cashierName, unitCost);

    /// <summary>Строки для отчёта: с товаром и себестоимостью на момент списания.</summary>
    public static List<(string ProductId, string ProductName, double Quantity, string Reason, string? CashierName, DateTime CreatedAt, double? UnitCost)>
        LoadReport(int limit = 5000) => Db.LoadWriteOffReport(limit);

    public static List<(string ProductName, double Quantity, string Reason, string? CashierName, DateTime CreatedAt)> LoadRecent(int limit = 200) =>
        Db.LoadWriteOffHistory(limit);

    public static void BackfillCashierName(string userId, string displayName) =>
        Db.BackfillWriteOffCashierName(userId, displayName);
}
