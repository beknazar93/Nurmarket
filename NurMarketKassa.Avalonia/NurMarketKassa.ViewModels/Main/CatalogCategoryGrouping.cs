using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>2026-11-27 (просьба владельца по фото кассы магазина: «Добавь линии, разделение по категориям»): группы каталога по категориям.
/// Чистые функции без окна — ключ категории, деление на страницы с учётом заголовков групп, счётчики. Их же использует панель плиток
/// (CatalogGroupedPanel) — чтобы заголовки на экране стояли ровно там, где их посчитали страницы.
///
/// Правила страниц (сгруппированный каталог): страница — <c>pageSize</c> «мест»; плитка — одно место, заголовок группы — тоже одно.
/// Каждая страница начинается с заголовка (у продолжения группы с прошлой страницы — тот же заголовок). Заголовок новой группы
/// не ставится в конец страницы, если под ним на этой странице не поместятся хотя бы <see cref="MinTilesUnderHeader"/> плитки
/// (или вся группа, если она меньше) — тогда группа начинается со следующей страницы. Так заголовок никогда не остаётся «сиротой».</summary>
public static class CatalogCategoryGrouping
{
    /// <summary>Сколько плиток минимум должно стоять под заголовком новой группы на той же странице (меньше — только если группа сама меньше).</summary>
    public const int MinTilesUnderHeader = 2;

    /// <summary>Ключ группы: категория без пробелов по краям; "" — «Без категории».</summary>
    public static string KeyOf(CatalogProductTileVm? product) => (product?.Category ?? "").Trim();

    public static bool SameGroup(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Подпись группы «Без категории» на языке программы.</summary>
    public static string UncategorizedTitle => Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz");

    public static string TitleOf(string key) => string.IsNullOrEmpty(key) ? UncategorizedTitle : key;

    /// <summary>Порядок групп: «Без категории» последней, остальные — по названию (без учёта регистра).</summary>
    public static int GroupRank(CatalogProductTileVm product) => KeyOf(product).Length == 0 ? 1 : 0;

    /// <summary>Есть ли в каталоге хотя бы две разные (непустые) категории — только тогда разделение имеет смысл (продуктовый с категориями — да, каталог без категорий — нет).</summary>
    public static bool HasSeveralCategories(IReadOnlyList<CatalogProductTileVm> products)
    {
        string? first = null;
        foreach (var p in products)
        {
            var key = KeyOf(p);
            if (key.Length == 0)
                continue;
            if (first is null)
                first = key;
            else if (!SameGroup(first, key))
                return true;
        }

        return false;
    }

    /// <summary>Сколько товаров в каждой группе отобранного списка (для заголовка «Напитки · 24»).</summary>
    public static Dictionary<string, int> CountByGroup(IReadOnlyList<CatalogProductTileVm> products)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products)
        {
            var key = KeyOf(p);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        return counts;
    }

    /// <summary>Начала страниц (индексы в отобранном списке). Без группировки — каждые <paramref name="pageSize"/> товаров, как раньше.</summary>
    public static List<int> PageStarts(IReadOnlyList<CatalogProductTileVm> products, int pageSize, bool grouped)
    {
        if (!grouped)
            return PageStarts(products.Count, pageSize);
        var keys = new string[products.Count];
        for (var i = 0; i < keys.Length; i++)
            keys[i] = KeyOf(products[i]);
        return PageStarts(keys, pageSize);
    }

    /// <summary>Без группировки: 0, pageSize, 2·pageSize…</summary>
    public static List<int> PageStarts(int count, int pageSize)
    {
        pageSize = Math.Max(1, pageSize);
        var starts = new List<int> { 0 };
        for (var i = pageSize; i < count; i += pageSize)
            starts.Add(i);
        return starts;
    }

    /// <summary>С группировкой по ключам (см. правила в описании класса). Список ключей должен быть уже упорядочен группами.</summary>
    public static List<int> PageStarts(IReadOnlyList<string> keys, int pageSize)
    {
        // Страница должна вмещать заголовок и хотя бы две плитки, иначе правило «не сирота» не выполнить.
        pageSize = Math.Max(1 + MinTilesUnderHeader, pageSize);
        var n = keys.Count;
        var starts = new List<int> { 0 };
        if (n == 0)
            return starts;

        // Конец группы для каждого индекса — один проход с конца (O(n), на 15 000 товаров — доли миллисекунды).
        var groupEnd = new int[n];
        groupEnd[n - 1] = n;
        for (var i = n - 2; i >= 0; i--)
            groupEnd[i] = SameGroup(keys[i], keys[i + 1]) ? groupEnd[i + 1] : i + 1;

        var pageStart = 0;
        var slots = 0;
        for (var i = 0; i < n; i++)
        {
            var groupStart = i == 0 || !SameGroup(keys[i], keys[i - 1]);
            if (i > pageStart)
            {
                var need = groupStart ? 1 + Math.Min(MinTilesUnderHeader, groupEnd[i] - i) : 1;
                if (slots + need > pageSize)
                {
                    starts.Add(i);
                    pageStart = i;
                    slots = 0;
                }
            }

            // Первая плитка страницы и первая плитка группы идут с заголовком — два места.
            slots += i == pageStart || groupStart ? 2 : 1;
        }

        return starts;
    }
}
