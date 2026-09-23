namespace NurMarketKassa.Services;

/// <summary>Оценка срока годности по категории товара (AI-фичи 2026-09-03, п.5) — пока без
/// реального интернет-поиска (нужен API-ключ, которого пока нет), поэтому используется таблица
/// категорий по умолчанию. Ключевые слова ищутся в названии категории без учёта регистра, первое
/// совпадение побеждает — порядок важен (более специфичные слова раньше общих).</summary>
public static class ExpiryEstimator
{
    private static readonly (string Keyword, int Days, string Label)[] CategoryShelfLifeDays =
    {
        ("молоч", 7, "Молочные продукты"),
        ("кисломолоч", 7, "Молочные продукты"),
        ("йогурт", 10, "Молочные продукты"),
        ("сыр", 21, "Сыр"),
        ("хлеб", 3, "Хлебобулочные изделия"),
        ("выпечк", 3, "Хлебобулочные изделия"),
        ("мясо", 3, "Мясо (охлаждённое)"),
        ("колбас", 10, "Колбасные изделия"),
        ("рыба", 2, "Рыба (охлаждённая)"),
        ("овощ", 5, "Овощи"),
        ("фрукт", 5, "Фрукты"),
        ("зелен", 4, "Зелень"),
        ("замороз", 90, "Замороженные продукты"),
        ("консерв", 365, "Консервы"),
        ("напит", 180, "Напитки"),
        ("вода", 365, "Вода"),
        ("круп", 270, "Крупы и бакалея"),
        ("макарон", 270, "Крупы и бакалея"),
        ("муж", 270, "Крупы и бакалея"),
        ("сладост", 60, "Кондитерские изделия"),
        ("шоколад", 180, "Кондитерские изделия"),
        ("бытов", 730, "Бытовая химия"),
        ("гигиен", 730, "Гигиена"),
    };

    private const int DefaultShelfLifeDays = 30;

    /// <summary>IsKnownCategory=false означает "категория ни с чем не совпала, взят срок по
    /// умолчанию" — вызывающий код (см. RestockSuggestionsWindow) должен ТАКИЕ товары не
    /// показывать в списке "скоро истечёт", иначе непортящиеся товары (например, бытовая техника
    /// без распознанной категории) будут ложно помечены как "истекает через 30 дней".</summary>
    public static (int Days, string Label, bool IsKnownCategory) EstimateShelfLife(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            foreach (var (keyword, days, label) in CategoryShelfLifeDays)
            {
                if (category.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return (days, label, true);
            }
        }

        return (DefaultShelfLifeDays, "Прочее (срок по умолчанию)", false);
    }

    /// <summary>null, если дата поступления ещё не зафиксирована (товар без остатка > 0
    /// с момента внедрения этой фичи, см. LocalProductRepository.UpdateStock/SyncReplaceAllWithDiff)
    /// или категория не распознана (см. IsKnownCategory).</summary>
    public static DateTime? EstimateExpiryDate(DateTime? intakeDate, string? category)
    {
        if (intakeDate is null)
            return null;

        var (days, _, isKnown) = EstimateShelfLife(category);
        return isKnown ? intakeDate.Value.AddDays(days) : null;
    }
}
