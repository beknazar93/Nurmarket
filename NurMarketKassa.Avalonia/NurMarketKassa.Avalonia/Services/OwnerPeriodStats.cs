using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-21, владелец: «доведи Нур Советника до 10» (И5). Сравнение периодов считает ПРОГРАММА, а не ИИ: вчера к позавчера,
/// вчера к тому же дню прошлой недели, 7 полных дней к прошлым 7, месяц к тому же числу прошлого месяца, сегодня. Источник — тот же
/// отчёт сервера NurCRM, что «Сводка» (MarketSalesReportAsync: cards и charts.sales_dynamics), поэтому цифра советника = цифра программы.
/// Все запросы — одновременно, результат хранится 5 минут; не ответивший период просто пропускается (с пометкой в тексте).
/// Используют: сводка для ИИ (OwnerAiContext/AiAdvisorWindow), плитки ответа, утренний отчёт.</summary>
public static class OwnerPeriodStats
{
    /// <summary>Итоги периода (дни включительно). Revenue/Checks/AvgCheck/Profit — как карточки «Сводки».</summary>
    public sealed record Period(string Key, string Label, DateTime From, DateTime To, double Revenue, double Checks, double AvgCheck, double Profit, double Margin);

    /// <summary>Готовое сравнение: текущий и прошлый период, изменение выручки и прибыли в процентах (null — прошлого нет или он 0).</summary>
    public sealed record Comparison(string Key, string Title, Period Current, Period Previous)
    {
        public double? RevenueDeltaPercent => Delta(Current.Revenue, Previous.Revenue);
        public double? ProfitDeltaPercent => Delta(Current.Profit, Previous.Profit);
        public double? ChecksDeltaPercent => Delta(Current.Checks, Previous.Checks);

        private static double? Delta(double cur, double prev) => prev > 0.005 ? (cur - prev) / prev * 100 : null;
    }

    public sealed record Snapshot(DateTime BuiltAtLocal, IReadOnlyList<Period> Periods, IReadOnlyList<Comparison> Comparisons,
        IReadOnlyDictionary<DateTime, double> Daily, IReadOnlyList<string> Missing)
    {
        public Period? Get(string key) => Periods.FirstOrDefault(p => p.Key == key);

        public Comparison? Compare(string key) => Comparisons.FirstOrDefault(c => c.Key == key);

        /// <summary>Текст для ИИ: цифры и проценты готовы — ИИ их не пересчитывает.</summary>
        public string ToAiText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"СРАВНЕНИЕ ПЕРИОДОВ (посчитала программа по отчёту сервера NurCRM, как «Сводка», на {BuiltAtLocal:dd.MM HH:mm}; проценты готовые — не пересчитывай, называй как есть):");
            foreach (var p in Periods)
                sb.AppendLine($"• {p.Label}: выручка {Money(p.Revenue)}, чеков {p.Checks:0}, средний чек {Money(p.AvgCheck)}, валовая прибыль {Money(p.Profit)}" +
                              (p.Margin != 0 ? $" (маржа {p.Margin:0.#} %)" : "") + ".");
            foreach (var c in Comparisons)
                sb.AppendLine($"• {c.Title}: выручка {Money(c.Current.Revenue)} против {Money(c.Previous.Revenue)} — {Pct(c.RevenueDeltaPercent)}; " +
                              $"прибыль {Money(c.Current.Profit)} против {Money(c.Previous.Profit)} — {Pct(c.ProfitDeltaPercent)}; чеков {c.Current.Checks:0} против {c.Previous.Checks:0}.");
            if (Daily.Count > 0)
                sb.AppendLine("• Выручка по дням: " + string.Join(", ", Daily.OrderBy(d => d.Key).Select(d => $"{d.Key:dd.MM} ({DayName(d.Key)}) — {d.Value:0}")) + " сом.");
            if (Missing.Count > 0)
                sb.AppendLine("• Не ответил сервер для: " + string.Join(", ", Missing) + " — эти цифры не называй, предложи раздел «Сводка».");
            return sb.ToString().TrimEnd();
        }
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Snapshot? _cached;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    public static string Money(double v) => Math.Round(v).ToString("N0", Ru) + " сом";

    public static string Pct(double? v) => v is not { } d ? "сравнить не с чем (в прошлом периоде продаж не было)"
        : Math.Abs(d) < 0.5 ? "без изменений" : (d > 0 ? "▲ +" : "▼ −") + Math.Abs(d).ToString("0.#", Ru) + " %";

    private static string DayName(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "пн", DayOfWeek.Tuesday => "вт", DayOfWeek.Wednesday => "ср", DayOfWeek.Thursday => "чт",
        DayOfWeek.Friday => "пт", DayOfWeek.Saturday => "сб", _ => "вс",
    };

    /// <summary>Последний снимок, если он моложе 5 минут (без запросов) — для плиток, пока идёт новый.</summary>
    public static Snapshot? Cached => _cached is not null && DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromMinutes(5) ? _cached : null;

    /// <summary>Только для снимков дизайна (AiAdvisorWindow.PreviewScene): подставить готовый снимок без сервера.</summary>
    internal static void PreviewSet(Snapshot snap)
    {
        _cached = snap;
        _cachedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Снимок сравнений (кэш 5 минут). Сервер не ответил вовсе — null.</summary>
    public static async Task<Snapshot?> GetAsync(CancellationToken ct, bool force = false)
    {
        if (!force && Cached is { } fresh)
            return fresh;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && Cached is { } again)
                return again;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var today = DateTime.Today;
            var y = today.AddDays(-1);
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var prevMonthStart = monthStart.AddMonths(-1);
            var prevMonthTo = prevMonthStart.AddDays(Math.Min(today.Day, DateTime.DaysInMonth(prevMonthStart.Year, prevMonthStart.Month)) - 1);
            var specs = new (string Key, string Label, DateTime From, DateTime To)[]
            {
                ("today", $"Сегодня {today:dd.MM} (до {DateTime.Now:HH:mm}, день не закончен)", today, today),
                ("yesterday", $"Вчера {y:dd.MM} ({DayName(y)})", y, y),
                ("day_before", $"Позавчера {y.AddDays(-1):dd.MM} ({DayName(y.AddDays(-1))})", y.AddDays(-1), y.AddDays(-1)),
                ("yesterday_last_week", $"Тот же день прошлой недели {y.AddDays(-7):dd.MM} ({DayName(y.AddDays(-7))})", y.AddDays(-7), y.AddDays(-7)),
                ("week", $"7 дней как в «Сводке» {today.AddDays(-6):dd.MM}–{today:dd.MM} (с сегодняшним днём)", today.AddDays(-6), today),
                ("last7", $"7 полных дней {y.AddDays(-6):dd.MM}–{y:dd.MM}", y.AddDays(-6), y),
                ("prev7", $"Прошлые 7 дней {y.AddDays(-13):dd.MM}–{y.AddDays(-7):dd.MM}", y.AddDays(-13), y.AddDays(-7)),
                ("month", $"Месяц {monthStart:dd.MM}–{today:dd.MM}", monthStart, today),
                ("prev_month", $"Прошлый месяц те же дни {prevMonthStart:dd.MM}–{prevMonthTo:dd.MM}", prevMonthStart, prevMonthTo),
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            async Task<(string Key, JsonElement? Report)> Load(string key, DateTime from, DateTime to)
            {
                try
                {
                    var r = await App.SalesApi.MarketSalesReportAsync(from, to, timeout.Token).ConfigureAwait(false);
                    return (key, r.ValueKind == JsonValueKind.Object ? r.Clone() : null);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    PosLogger.Log($"Сравнение периодов: «{key}» не получен ({ex.GetType().Name}).", "WARNING");
                    return (key, null);
                }
            }
            var tasks = specs.Select(s => Load(s.Key, s.From, s.To)).ToList();
            var daily = Load("daily", today.AddDays(-13), today);
            var results = await Task.WhenAll(tasks.Append(daily)).ConfigureAwait(false);
            var byKey = results.ToDictionary(r => r.Key, r => r.Report);
            var periods = new List<Period>();
            var missing = new List<string>();
            foreach (var s in specs)
            {
                if (byKey.TryGetValue(s.Key, out var rep) && rep is { } report && report.TryGetProperty("cards", out var cards))
                    periods.Add(new Period(s.Key, s.Label, s.From, s.To, Num(cards, "revenue"), Num(cards, "transactions"), Num(cards, "avg_check"),
                        Num(cards, "gross_profit"), Num(cards, "margin_percent")));
                else
                    missing.Add(s.Label);
            }
            var dailyMap = new Dictionary<DateTime, double>();
            if (byKey.TryGetValue("daily", out var dr) && dr is { } dayReport && dayReport.TryGetProperty("charts", out var charts)
                && charts.TryGetProperty("sales_dynamics", out var dyn) && dyn.ValueKind == JsonValueKind.Array)
            {
                foreach (var point in dyn.EnumerateArray())
                    if (point.TryGetProperty("date", out var dEl) && DateTime.TryParseExact(dEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                        dailyMap[day.Date] = Num(point, "value");
                for (var d = today.AddDays(-13); d <= today; d = d.AddDays(1))
                    dailyMap.TryAdd(d, 0);
            }
            if (periods.Count == 0 && dailyMap.Count == 0)
                return null;
            Period? P(string k) => periods.FirstOrDefault(p => p.Key == k);
            var comparisons = new List<Comparison>();
            void Cmp(string key, string title, string cur, string prev)
            {
                if (P(cur) is { } c && P(prev) is { } p)
                    comparisons.Add(new Comparison(key, title, c, p));
            }
            Cmp("yesterday_vs_day_before", "Вчера к позавчера", "yesterday", "day_before");
            Cmp("yesterday_vs_last_week", "Вчера к тому же дню прошлой недели", "yesterday", "yesterday_last_week");
            Cmp("week_vs_prev", "7 полных дней к прошлым 7 дням", "last7", "prev7");
            Cmp("month_vs_prev", "Месяц к тем же дням прошлого месяца", "month", "prev_month");
            var snap = new Snapshot(DateTime.Now, periods, comparisons, dailyMap, missing);
            _cached = snap;
            _cachedAtUtc = DateTime.UtcNow;
            PosLogger.Log($"Сравнение периодов: {periods.Count} периодов, {comparisons.Count} сравнений за {watch.ElapsedMilliseconds} мс (одновременно).", "INFO");
            return snap;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static double Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }
}
