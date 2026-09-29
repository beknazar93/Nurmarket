using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-09-29, «увеличь скорость загрузки аналитики»: общий кэш отчётов сервера
/// (analytics/market/?tab=sales|products) на короткое время.
///
/// Один и тот же отчёт за один и тот же период окна запрашивали по нескольку раз подряд: «Финансы»
/// и «Аналитика» — вкладку «Товары» дважды (топ и ABC), «Продажи» — тоже дважды, «Сводка», ABC и
/// выгрузка — ещё раз; у большого магазина вкладка «Товары» за год — 3 МБ и несколько секунд.
/// Теперь одинаковые запросы, идущие одновременно, превращаются в один, а возврат к уже открытому
/// периоду показывает цифры сразу.
///
/// Свежесть: период, в который входит сегодня, — 15 с (меньше, чем «Сводка» обновляется сама — 20 с,
/// поэтому её живое обновление всегда ходит на сервер); прошедшие периоды — 10 мин. Любая продажа,
/// возврат или операция на этой кассе (PosDataEvents.SalesChanged) и кнопка «Обновить» кэш
/// сбрасывают. Ошибка или отмена в кэше не остаются.</summary>
public sealed partial class SalesApiService
{
    private static readonly ConcurrentDictionary<string, (DateTime AtUtc, Task<JsonElement> Task)> ReportCache = new();
    private const int ReportCacheLimit = 24;

    static SalesApiService()
    {
        PosDataEvents.SalesChanged += InvalidateReportCache;
    }

    /// <summary>Сбросить кэш отчётов — «Обновить» в окнах аналитики и новые продажи.</summary>
    public static void InvalidateReportCache() => ReportCache.Clear();

    private Task<JsonElement> CachedReportAsync(string tab, DateTime from, DateTime to, CancellationToken ct)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{tab}|{from:yyyy-MM-dd}|{to:yyyy-MM-dd}");
        var ttl = to.Date >= DateTime.Today ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(10);
        var now = DateTime.UtcNow;
        if (ReportCache.TryGetValue(key, out var hit)
            && now - hit.AtUtc < ttl
            && !hit.Task.IsFaulted
            && !hit.Task.IsCanceled)
            return hit.Task.WaitAsync(ct);

        var qs = new Dictionary<string, string>
        {
            ["tab"] = tab,
            ["period_start"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["period_end"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        // Запрос не привязан к токену одного окна: его ждут и другие. Окно, сменившее период, просто
        // перестаёт ждать (WaitAsync), а ответ остаётся в кэше — вернуться к периоду можно мгновенно.
        var task = GetRetryingThrottleAsync("api/main/analytics/market/", qs, CancellationToken.None);
        var entry = (now, task);
        ReportCache[key] = entry;
        _ = task.ContinueWith(
            _ => ReportCache.TryRemove(new KeyValuePair<string, (DateTime, Task<JsonElement>)>(key, entry)),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (ReportCache.Count > ReportCacheLimit)
        {
            foreach (var old in ReportCache.OrderBy(kv => kv.Value.AtUtc).Take(ReportCache.Count - ReportCacheLimit).ToList())
                ReportCache.TryRemove(old);
        }

        return task.WaitAsync(ct);
    }
}
