using NurMarketKassa.Core.Application;

namespace NurMarketKassa.Models.Pos;

/// <summary>
/// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ6): «Аналоги» товара — карточки, связанные с ним (<c>related</c> сервера, в любую сторону) или имеющие общий номер:
/// артикул, каталожный номер, OEM или кросс-номер одной карточки равен номеру другой (после нормализации). Чистая функция по каталогу памяти.
/// </summary>
public static class PartAnalogs
{
    public static List<CatalogProductTileVm> Find(CatalogProductTileVm source, IEnumerable<CatalogProductTileVm> catalog)
    {
        var mine = NumbersOf(source);
        var myRelated = new HashSet<string>(source.Part?.Related ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var result = new List<CatalogProductTileVm>();
        foreach (var p in catalog)
        {
            if (string.Equals(p.Id, source.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            var related = myRelated.Contains(p.Id) || (p.Part?.Related.Any(id => string.Equals(id, source.Id, StringComparison.OrdinalIgnoreCase)) ?? false);
            if (related || (mine.Count > 0 && NumbersOf(p).Overlaps(mine)))
                result.Add(p);
        }
        return result;
    }

    /// <summary>Количество аналогов — для подписи «Аналоги (N)».</summary>
    public static int Count(CatalogProductTileVm source, IEnumerable<CatalogProductTileVm> catalog) => Find(source, catalog).Count;

    private static HashSet<string> NumbersOf(CatalogProductTileVm p)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var article = PartNumbers.Norm(p.Article);
        if (article.Length >= 3)
            set.Add(article);
        if (p.Part is { } info)
            foreach (var n in info.Numbers())
                if (n.Length >= 3)
                    set.Add(n);
        return set;
    }
}
