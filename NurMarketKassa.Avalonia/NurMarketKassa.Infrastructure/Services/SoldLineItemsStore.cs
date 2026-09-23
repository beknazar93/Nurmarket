namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для истории проданных позиций (таблица
/// SoldLineItems) — источник данных для прогноза пополнения склада (AI-фичи 2026-09-03, п.1).</summary>
public static class SoldLineItemsStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static void AppendSale(IEnumerable<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines) =>
        Db.AppendSoldLineItems(lines, source: "local");

    public static void AppendBackfill(IEnumerable<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines) =>
        Db.AppendSoldLineItems(lines, source: "backfill");

    public static List<(string ProductId, string ProductName, double Quantity, DateTime SoldAt)> LoadSince(DateTime sinceUtc) =>
        Db.LoadSoldLineItemsSince(sinceUtc);

    /// <summary>Строки продаж с ценой, по которой товар реально продали — для выручки.
    /// <see cref="LoadSince"/> цену не отдаёт, и отчёт, считавший её по каталогу, завышал
    /// выручку на поштучных продажах из пачки в разы.</summary>
    public static List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)>
        LoadWithPriceSince(DateTime sinceUtc, DateTime? untilUtc = null) =>
        Db.LoadSoldLineItemsWithPriceSince(sinceUtc, untilUtc);

    /// <summary>null, если ещё вообще нет ни одной записи (ни локальной, ни бэкфилла).</summary>
    public static DateTime? GetEarliestDate() => Db.GetEarliestSoldLineItemDate();
}
