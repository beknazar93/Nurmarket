namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А, функция 1): правила про просрочку — чистая логика поверх справочника сроков с сервера (ProductExpiryIndex).
// Идея (не код) — OCA pos_product_expiry, Odoo product_expiry: при продаже просроченного — предупредить или не пускать без PIN владельца.

/// <summary>Итог по просроченному: сколько товаров и на какую сумму по закупке (остаток × закупочная цена).</summary>
public sealed record ExpiredSummary(int Count, double Cost, IReadOnlyList<ProductExpiryIndex.Mark> Marks);

public enum ExpirySaleDecision
{
    /// <summary>Срок в порядке или проверка выключена — продавать как обычно.</summary>
    Allow,
    /// <summary>Просрочен — спросить «Продать всё равно?».</summary>
    Warn,
    /// <summary>Просрочен — продавать только после PIN владельца.</summary>
    NeedOwnerPin,
}

public static class ExpiryRules
{
    /// <summary>Просрочен ли товар на дату <paramref name="today"/>: отметка сервера «expired» или срок раньше сегодняшнего дня.</summary>
    public static bool IsExpired(ProductExpiryIndex.Mark mark, DateTime today) => mark.Expired || mark.Date.Date < today.Date;

    /// <summary>Что делать при добавлении товара в чек. mark == null — у товара срока нет.</summary>
    public static ExpirySaleDecision Decide(string? mode, ProductExpiryIndex.Mark? mark, DateTime today)
    {
        var m = ExpiryModes.Normalize(mode);
        if (m == ExpiryModes.Off || mark is null || !IsExpired(mark, today))
            return ExpirySaleDecision.Allow;
        return m == ExpiryModes.Block ? ExpirySaleDecision.NeedOwnerPin : ExpirySaleDecision.Warn;
    }

    /// <summary>Сколько товаров просрочено и на какую сумму по закупке. Товары без остатка в сумму не входят (но в счёт — да).</summary>
    public static ExpiredSummary Summarize(IEnumerable<ProductExpiryIndex.Mark> marks, DateTime today, Func<string, double> purchasePrice)
    {
        var expired = marks.Where(m => IsExpired(m, today)).ToList();
        var cost = expired.Where(m => m.Quantity > 0).Sum(m => m.Quantity * Math.Max(0, purchasePrice(m.ProductId)));
        return new ExpiredSummary(expired.Count, Math.Round(cost, 2), expired);
    }

    /// <summary>Сводка по текущему справочнику сроков и каталогу кассы.</summary>
    public static ExpiredSummary SummarizeCurrent(DateTime? today = null)
    {
        var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in CatalogCacheService.Products.ToList())
                prices[p.Id] = p.PurchasePrice;
        }
        catch (InvalidOperationException)
        {
            // каталог обновлялся в этот момент — сумма посчитается без цен, число товаров верное
        }
        return Summarize(ProductExpiryIndex.Marks.Values, today ?? DateTime.Today, id => prices.GetValueOrDefault(id));
    }

    /// <summary>«Просрочено 7 товаров на 3 400 сом» (для утреннего отчёта и Telegram); пустая строка — просроченного нет.</summary>
    public static string SummaryText(ExpiredSummary s)
    {
        if (s.Count <= 0)
            return "";
        var sum = s.Cost > 0.005 ? " " + Tr.T($"на {MoneyText.Som(s.Cost)}", $"{MoneyText.Som(s.Cost)} суммасына", $"worth {MoneyText.Som(s.Cost)}", $"{MoneyText.Som(s.Cost)} değerinde", $"{MoneyText.Som(s.Cost)} miqdorida") : "";
        return Tr.T($"Просрочено {s.Count} {Plural(s.Count, "товар", "товара", "товаров")}{sum}",
            $"Мөөнөтү өткөн: {s.Count} товар{sum}",
            $"Expired: {s.Count} product{(s.Count == 1 ? "" : "s")}{sum}",
            $"Süresi geçmiş: {s.Count} ürün{sum}",
            $"Muddati o'tgan: {s.Count} ta mahsulot{sum}");
    }

    /// <summary>Окно предупреждения при продаже: «Товар просрочен (12.09). Продать всё равно?».</summary>
    public static string SaleWarningText(string title, ProductExpiryIndex.Mark mark, bool needPin)
    {
        var days = Math.Max(0, -mark.DaysLeft);
        var head = Tr.T($"Товар «{title}» просрочен ({mark.Date:dd.MM.yyyy}, {days} дн.).",
            $"«{title}» товарынын мөөнөтү өткөн ({mark.Date:dd.MM.yyyy}, {days} күн).",
            $"“{title}” is expired ({mark.Date:dd.MM.yyyy}, {days} d).",
            $"«{title}» ürününün süresi geçmiş ({mark.Date:dd.MM.yyyy}, {days} gün).",
            $"«{title}» mahsulotining muddati o'tgan ({mark.Date:dd.MM.yyyy}, {days} kun).");
        var tail = needPin
            ? Tr.T("Продать можно только с PIN владельца.", "Сатуу үчүн ээсинин PIN коду керек.", "Selling requires the owner PIN.", "Satış için sahip PIN'i gerekir.", "Sotish uchun egasining PIN kodi kerak.")
            : Tr.T("Продать всё равно?", "Баары бир сатасызбы?", "Sell anyway?", "Yine de satılsın mı?", "Baribir sotilsinmi?");
        return head + "\n" + tail;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14)
            return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
