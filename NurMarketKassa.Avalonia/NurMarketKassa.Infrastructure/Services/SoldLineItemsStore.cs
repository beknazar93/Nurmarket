namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для истории проданных позиций (таблица
/// SoldLineItems) — источник данных для прогноза пополнения склада (AI-фичи 2026-09-03, п.1).</summary>
public static class SoldLineItemsStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    /// <param name="saleId">Номер продажи с сервера. По нему история этой кассы и история,
    /// подтянутая с сервера на другой кассе, не задваиваются.</param>
    public static void AppendSale(IEnumerable<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines, string? saleId = null)
    {
        Db.AppendSoldLineItems(lines, source: "local", saleId);
        // Открытые окна аналитики пересчитаются сами — см. PosDataEvents.
        PosDataEvents.RaiseSalesChanged();
    }

    public static void AppendBackfill(IEnumerable<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)> lines, string? saleId = null) =>
        Db.AppendSoldLineItems(lines, source: "backfill", saleId);

    /// <summary>Вся история одного товара — для разбора товара.</summary>
    public static List<(string ProductId, string ProductName, double Quantity, double UnitPrice, DateTime SoldAt)>
        LoadForProduct(string productName) => Db.LoadSoldLineItemsForProduct(productName);

    /// <summary>Дата, до которой история писалась без номера продажи — раньше неё сливать
    /// истории касс нельзя, будет задвоение.</summary>
    public static DateTime? LegacyWatermark() => Db.GetLegacyHistoryWatermark();

    /// <summary>Продажи, которые уже есть в локальной истории.</summary>
    public static HashSet<string> KnownSaleIds() => Db.LoadKnownSaleIds();

    /// <summary>Продажи локальной истории за промежуток.</summary>
    public static HashSet<string> KnownSaleIdsBetween(DateTime sinceUtc, DateTime untilUtc) =>
        Db.LoadKnownSaleIdsBetween(sinceUtc, untilUtc);

    /// <summary>Убрать продажи, которых на сервере больше нет (удалены или отменены).</summary>
    public static int RemoveSales(IReadOnlyCollection<string> saleIds)
    {
        var removed = Db.DeleteSoldLineItemsBySaleIds(saleIds);
        if (removed > 0)
            PosDataEvents.RaiseSalesChanged();
        return removed;
    }

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
