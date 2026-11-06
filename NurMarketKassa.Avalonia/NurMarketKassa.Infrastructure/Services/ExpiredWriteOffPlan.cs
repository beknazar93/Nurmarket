namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (В12, С7 «Ревью волн 0–3 — функции магазина»): строка списка «Списать просроченное». Дата годности у товара одна на всю карточку, поэтому если ПОСЛЕ неё
/// был приход (последний приход из истории закупок этой кассы позже даты годности), в остатке, скорее всего, свежая партия — такую строку по умолчанию не отмечаем и
/// предупреждаем. Количество — min(остаток по справочнику срока, остаток в каталоге), как и раньше.</summary>
public sealed record ExpiredWriteOffItem(ProductExpiryIndex.Mark Mark, double Qty, double UnitCost, DateTime? LastReceiptAt)
{
    public string ProductId => Mark.ProductId;

    /// <summary>После даты годности был приход — в остатке может быть свежий товар.</summary>
    public bool MayHaveFreshStock => LastReceiptAt is { } at && at.Date > Mark.Date.Date;

    /// <summary>Сумма по закупке (остаток × закупочная цена).</summary>
    public double Cost => Qty * Math.Max(0, UnitCost);
}

public static class ExpiredWriteOffPlan
{
    /// <summary>Просроченные товары с остатком. <paramref name="catalogQty"/> — остаток в каталоге (null — товара нет в каталоге, берём остаток срока);
    /// <paramref name="purchasePrice"/> — закупка; <paramref name="lastReceipt"/> — когда последний раз приходовали (null — не знаем).</summary>
    public static List<ExpiredWriteOffItem> Build(IEnumerable<ProductExpiryIndex.Mark> marks, DateTime today, Func<string, double?> catalogQty,
        Func<string, double> purchasePrice, Func<string, DateTime?> lastReceipt)
    {
        var items = new List<ExpiredWriteOffItem>();
        foreach (var m in marks.Where(m => ExpiryRules.IsExpired(m, today) && m.Quantity > 0.0005))
        {
            // Остаток в каталоге мог уменьшиться после продаж — не списываем больше, чем есть.
            var qty = catalogQty(m.ProductId) is { } stock ? Math.Min(m.Quantity, stock) : m.Quantity;
            if (qty > 0.0005)
                items.Add(new ExpiredWriteOffItem(m, qty, purchasePrice(m.ProductId), lastReceipt(m.ProductId)));
        }
        return items;
    }

    /// <summary>Что отмечено по умолчанию: всё, кроме строк, где после даты годности был приход.</summary>
    public static HashSet<string> DefaultSelection(IEnumerable<ExpiredWriteOffItem> items) =>
        new(items.Where(i => !i.MayHaveFreshStock).Select(i => i.ProductId), StringComparer.OrdinalIgnoreCase);

    /// <summary>Только отмеченные строки, в исходном порядке.</summary>
    public static List<ExpiredWriteOffItem> Selected(IEnumerable<ExpiredWriteOffItem> items, IReadOnlySet<string> selected) =>
        items.Where(i => selected.Contains(i.ProductId)).ToList();

    public static double Cost(IEnumerable<ExpiredWriteOffItem> items) => Math.Round(items.Sum(i => i.Cost), 2);
}
