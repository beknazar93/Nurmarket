namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В3 Л5-17 — какие наборы в карантинной папке <c>_previous</c> пора удалить. Раньше только по возрасту (14 дней): каждая смена компании
/// оставляла копию базы (сотни МБ), и за две недели набиралось много наборов. Теперь ещё и предел числа: хранятся не больше <paramref name="maxSets"/> самых новых.
/// Чистая функция (без диска) — проверяется тестом.</summary>
public static class QuarantineRetention
{
    /// <summary>Сколько наборов (папок <c>yyyyMMdd-HHmmss</c>) держим в карантине одновременно.</summary>
    public const int MaxSets = 2;

    /// <returns>Пути наборов, которые нужно удалить: старше <paramref name="keepDays"/> дней либо не входящие в <paramref name="maxSets"/> самых новых.</returns>
    public static IReadOnlyList<string> SelectToDelete(IEnumerable<(string Path, DateTime Created)> sets, DateTime now, int keepDays, int maxSets = MaxSets)
    {
        var ordered = sets.OrderByDescending(s => s.Created).ToList();
        var result = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
            if (i >= Math.Max(1, maxSets) || now - ordered[i].Created > TimeSpan.FromDays(keepDays))
                result.Add(ordered[i].Path);
        return result;
    }
}
