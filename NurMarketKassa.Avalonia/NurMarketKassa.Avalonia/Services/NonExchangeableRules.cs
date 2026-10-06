namespace NurMarketKassa.Services;

/// <summary>2026-10-06, исследование «Кассы для одежды» (О-31, О-32) и закон КР «О защите прав потребителей»:
/// товар надлежащего качества обменивают и возвращают в течение 14 дней, не считая дня покупки; есть перечень товаров,
/// которые без брака не обменивают (нижнее бельё, чулки и носки, парфюмерия и косметика, ювелирные изделия, лекарства, книги).
/// Касса не запрещает, а предупреждает: брак принимают и позже, и из этих категорий.</summary>
public static class NonExchangeableRules
{
    /// <summary>Начала слов из перечня — по ним касса сама находит такие категории, пока владелец не выбрал их в настройках.
    /// Сравнение — с началом слова: «бель» находит «Бельё», но не «Мебель».</summary>
    private static readonly string[] Keywords =
        { "бель", "нижн", "носк", "чулоч", "колготк", "купальн", "парфюм", "духи", "космет", "ювелир", "лекарств", "книг" };

    /// <summary>Срок обмена из настроек; 0 — не проверять. Только в сфере «Одежда».</summary>
    public static int DaysLimit => MarketSpheres.IsClothing ? Math.Max(0, UserPreferences.Instance.ExchangeDaysLimit) : 0;

    /// <summary>Сколько дней прошло после дня покупки (день покупки не считается).</summary>
    public static int DaysPassed(DateTime saleDate) => Math.Max(0, (DateTime.Today - saleDate.Date).Days);

    public static bool IsOutOfTerm(DateTime? saleDate) =>
        saleDate is { } d && d > DateTime.MinValue && DaysLimit > 0 && DaysPassed(d) > DaysLimit;

    /// <summary>Все категории каталога (для настроек).</summary>
    public static List<string> AllCategories() =>
        CatalogCacheService.Products
            .Select(p => (p.Category ?? "").Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static bool LooksNonExchangeable(string category) =>
        category.ToLowerInvariant().Replace('ё', 'е')
            .Split(new[] { ' ', ',', '.', '-', '/', '(', ')', '«', '»', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => Keywords.Any(k => word.StartsWith(k.Replace('ё', 'е'), StringComparison.Ordinal)));

    /// <summary>Выбранные категории: из настроек, а если владелец их ещё не настраивал — найденные по словам перечня.</summary>
    public static HashSet<string> SelectedCategories()
    {
        var saved = UserPreferences.Instance.NonExchangeableCategories;
        var set = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        if (saved is null)
        {
            foreach (var c in AllCategories().Where(LooksNonExchangeable))
                set.Add(c);
        }
        else
        {
            foreach (var c in saved.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(c);
        }
        return set;
    }

    public static void SaveSelected(IEnumerable<string> categories)
    {
        var prefs = UserPreferences.Instance;
        prefs.NonExchangeableCategories = string.Join(";", categories.Select(c => c.Trim()).Where(c => c.Length > 0));
        prefs.SaveToDisk();
    }

    /// <summary>Категория товара, если она в списке «без обмена»; иначе null. Только в сфере «Одежда».</summary>
    public static string? NonExchangeableCategoryOf(string? productId)
    {
        if (!MarketSpheres.IsClothing || string.IsNullOrWhiteSpace(productId))
            return null;
        var category = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase))?.Category?.Trim();
        return !string.IsNullOrEmpty(category) && SelectedCategories().Contains(category) ? category : null;
    }
}
