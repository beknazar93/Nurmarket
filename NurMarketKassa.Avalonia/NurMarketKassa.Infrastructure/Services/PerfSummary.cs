using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-06: В5, шаг 24 (аудит скорости 05.11.2026, п. 18.2 и 18.3). Видимость задержек без новых пакетов:
/// <list type="bullet">
/// <item>Запросы к NurCRM (<see cref="NurMarketKassa.Services.Api.ApiPathStats"/>.Record) и к локальной SQLite (<see cref="DatabaseService"/>) складывают времена сюда.</item>
/// <item>Раз в сутки (не чаще одной строки за календарный день) в журнал уходит ОДНА строка раздела PERF: p50/p95/max по самым частым путям API и по
/// операциям SQLite. Без тел запросов, логинов, имён, сумм и идентификаторов: пути уже приведены к шаблону («{id}»), операции SQLite — имена методов кассы.</item>
/// <item>Запрос SQLite дольше 200 мс сразу пишется в журнал строкой SQLITE SLOW (имя операции и миллисекунды, без текста запроса и параметров).</item>
/// </list>
/// Отправка сводки на сервер (kind=perf, п. 30.1) сюда не входит — это отдельное решение владельца и договорённость с Умаром.
/// Все расчёты — чистые функции (<see cref="Percentile"/>, <see cref="BuildLine"/>, <see cref="ShouldEmit"/>), проверяются тестами.
/// </summary>
public static class PerfSummary
{
    /// <summary>Порог «медленного» запроса SQLite, мс.</summary>
    public const int SlowSqliteMs = 200;

    /// <summary>Сколько последних значений времени помним на ключ (для p50/p95). Старые вытесняются — память ограничена.</summary>
    internal const int MaxSamplesPerKey = 400;

    private const int MaxKeys = 300;
    private const int TopPaths = 8;
    private const int TopSqlite = 6;
    private static readonly TimeSpan MinWindowForExit = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FullWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan SlowLogGap = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Series> ApiSeries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Series> SqliteSeries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> LastSlowLogTicks = new(StringComparer.Ordinal);
    private static readonly object TimerGate = new();
    private static Timer? _timer;
    private static DateTime _windowStartUtc = DateTime.UtcNow;
    private static int _exitHooked;

    /// <summary>Накопитель времён одного ключа: счётчик, сумма, максимум и последние <see cref="MaxSamplesPerKey"/> значений (кольцо).</summary>
    internal sealed class Series
    {
        private readonly object _gate = new();
        private readonly double[] _ring = new double[MaxSamplesPerKey];
        public long Count { get; private set; }
        public double TotalMs { get; private set; }
        public double MaxMs { get; private set; }
        public long Slow { get; private set; }

        public void Add(double ms, bool slow)
        {
            lock (_gate)
            {
                _ring[(int)(Count % MaxSamplesPerKey)] = ms;
                Count++;
                TotalMs += ms;
                if (ms > MaxMs)
                    MaxMs = ms;
                if (slow)
                    Slow++;
            }
        }

        public double[] Snapshot()
        {
            lock (_gate)
            {
                var n = (int)Math.Min(Count, MaxSamplesPerKey);
                var copy = new double[n];
                Array.Copy(_ring, copy, n);
                return copy;
            }
        }
    }

    /// <summary>Один запрос к API (ключ уже «МЕТОД /путь/{id}» из <see cref="NurMarketKassa.Services.Api.ApiPathStats"/>).</summary>
    public static void RecordApi(string key, double elapsedMs)
    {
        try
        {
            EnsureStarted();
            Add(ApiSeries, key, elapsedMs, slow: false);
        }
        catch
        {
            // сводка — только для замеров; сбой не должен ломать запрос
        }
    }

    /// <summary>Один вызов SQLite. Дольше <see cref="SlowSqliteMs"/> — сразу строка SQLITE SLOW в журнал (не чаще раза в 30 с на операцию).</summary>
    public static void RecordSqlite(string operation, double elapsedMs)
    {
        try
        {
            EnsureStarted();
            var slow = elapsedMs >= SlowSqliteMs;
            Add(SqliteSeries, operation, elapsedMs, slow);
            if (slow && ShouldLogSlow(operation))
                PosLogger.Log($"{operation}: {elapsedMs.ToString("F0", CultureInfo.InvariantCulture)} мс (порог {SlowSqliteMs} мс)", "SQLITE SLOW");
        }
        catch
        {
            // см. выше
        }
    }

    /// <summary>Таймер на операцию SQLite: <c>using var _ = PerfSummary.MeasureSqlite("LoadKnownSaleIds");</c> — на выходе пишет время.</summary>
    public static SqliteTimer MeasureSqlite(string operation) => new(operation);

    public readonly struct SqliteTimer : IDisposable
    {
        private readonly string _operation;
        private readonly long _start;

        public SqliteTimer(string operation)
        {
            _operation = operation;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_operation is not null)
                RecordSqlite(_operation, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
        }
    }

    private static void Add(ConcurrentDictionary<string, Series> map, string key, double ms, bool slow)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (!map.ContainsKey(key) && map.Count >= MaxKeys)
            key = "(прочие)";
        map.GetOrAdd(key, _ => new Series()).Add(Math.Max(0, ms), slow);
    }

    private static bool ShouldLogSlow(string operation)
    {
        var now = Environment.TickCount64;
        var last = LastSlowLogTicks.GetOrAdd(operation, now - (long)SlowLogGap.TotalMilliseconds - 1);
        if (now - last < (long)SlowLogGap.TotalMilliseconds)
            return false;
        LastSlowLogTicks[operation] = now;
        return true;
    }

    private static void EnsureStarted()
    {
        if (_timer is not null)
            return;
        lock (TimerGate)
        {
            if (_timer is not null)
                return;
            _windowStartUtc = DateTime.UtcNow;
            // Проверка раз в час: полные сутки окна наступают в любое время, а строка за календарный день — только одна.
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
            if (Interlocked.Exchange(ref _exitHooked, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushIfDue(onExit: true);
        }
    }

    private static void Tick() => FlushIfDue(onExit: false);

    /// <summary>Пора ли писать строку: не чаще одной за календарный день; по таймеру — после полных суток окна, при выходе из программы — если окно не короче 30 минут.</summary>
    internal static bool ShouldEmit(DateTime todayLocal, DateTime? lastEmitDay, TimeSpan windowAge, bool onExit, bool hasSamples)
    {
        if (!hasSamples)
            return false;
        if (lastEmitDay is { } last && last.Date == todayLocal.Date)
            return false;
        return windowAge >= (onExit ? MinWindowForExit : FullWindow);
    }

    private static void FlushIfDue(bool onExit)
    {
        try
        {
            var now = DateTime.UtcNow;
            var hasSamples = !ApiSeries.IsEmpty || !SqliteSeries.IsEmpty;
            if (!ShouldEmit(DateTime.Now, ReadLastEmitDay(), now - _windowStartUtc, onExit, hasSamples))
                return;
            var line = BuildLine(Snapshot(ApiSeries), Snapshot(SqliteSeries), now - _windowStartUtc);
            ApiSeries.Clear();
            SqliteSeries.Clear();
            _windowStartUtc = now;
            if (line is null)
                return;
            PosLogger.Log(line, "PERF");
            WriteLastEmitDay(DateTime.Now);
        }
        catch
        {
            // сводка не должна ломать программу, в том числе при выходе
        }
    }

    private static List<KeyValuePair<string, Stats>> Snapshot(ConcurrentDictionary<string, Series> map)
    {
        var list = new List<KeyValuePair<string, Stats>>();
        foreach (var pair in map)
        {
            var samples = pair.Value.Snapshot();
            list.Add(new(pair.Key, new Stats(pair.Value.Count, pair.Value.TotalMs, pair.Value.MaxMs, pair.Value.Slow, samples)));
        }
        return list;
    }

    /// <summary>Итог по ключу для <see cref="BuildLine"/>: сколько вызовов, сумма и максимум (мс), сколько дольше порога и последние значения.</summary>
    internal readonly record struct Stats(long Count, double TotalMs, double MaxMs, long Slow, double[] Samples);

    /// <summary>Процентиль (0..100) по значениям БЕЗ предварительной сортировки; пусто — 0. Метод «ближайшего ранга».</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double percent)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(Math.Clamp(percent, 0, 100) / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Одна строка сводки: «за 24 ч: API 1234 запр. p50 120 / p95 840 / max 5100 мс; пути: GET /api/x/ ×300 p50 90 p95 400; …; SQLite 5400 вызовов p95 3 мс, дольше 200 мс: 2 (LoadKnownSaleIds ×2 max 240 мс)».
    /// Пусто (нет ни одного замера) — null.</summary>
    internal static string? BuildLine(IEnumerable<KeyValuePair<string, Stats>> api, IEnumerable<KeyValuePair<string, Stats>> sqlite, TimeSpan window)
    {
        var apiList = api.Where(p => p.Value.Count > 0).ToList();
        var sqlList = sqlite.Where(p => p.Value.Count > 0).ToList();
        if (apiList.Count == 0 && sqlList.Count == 0)
            return null;

        var sb = new StringBuilder();
        var hours = Math.Max(1, (int)Math.Round(window.TotalHours));
        sb.Append(CultureInfo.InvariantCulture, $"за {hours} ч:");

        if (apiList.Count > 0)
        {
            var all = apiList.SelectMany(p => p.Value.Samples).ToList();
            var total = apiList.Sum(p => p.Value.Count);
            sb.Append(CultureInfo.InvariantCulture, $" API {total} запр., p50 {F(Percentile(all, 50))} / p95 {F(Percentile(all, 95))} / max {F(apiList.Max(p => p.Value.MaxMs))} мс; пути: ");
            var first = true;
            foreach (var p in apiList.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal).Take(TopPaths))
            {
                if (!first)
                    sb.Append("; ");
                first = false;
                sb.Append(CultureInfo.InvariantCulture, $"{p.Key} ×{p.Value.Count} p50 {F(Percentile(p.Value.Samples, 50))} p95 {F(Percentile(p.Value.Samples, 95))}");
            }
            sb.Append('.');
        }

        if (sqlList.Count > 0)
        {
            var all = sqlList.SelectMany(p => p.Value.Samples).ToList();
            var total = sqlList.Sum(p => p.Value.Count);
            var slowTotal = sqlList.Sum(p => p.Value.Slow);
            sb.Append(CultureInfo.InvariantCulture, $" SQLite {total} вызовов, p50 {F(Percentile(all, 50))} / p95 {F(Percentile(all, 95))} мс, дольше {SlowSqliteMs} мс: {slowTotal}");
            var worst = sqlList.Where(p => p.Value.Slow > 0).OrderByDescending(p => p.Value.MaxMs).Take(TopSqlite).ToList();
            if (worst.Count > 0)
            {
                sb.Append(" (");
                sb.Append(string.Join("; ", worst.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key} ×{p.Value.Slow} max {F(p.Value.MaxMs)} мс"))));
                sb.Append(')');
            }
            sb.Append('.');
        }

        return sb.ToString();
    }

    private static string F(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    // --- «одна строка в календарный день»: день последней записи хранится в маленьком файле рядом с журналом

    private static string MarkerPath => Path.Combine(AppPaths.Local, AppMode.DataFolderName, "perf-summary-day.txt");

    private static DateTime? ReadLastEmitDay()
    {
        try
        {
            return File.Exists(MarkerPath)
                && DateTime.TryParseExact(File.ReadAllText(MarkerPath).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLastEmitDay(DateTime nowLocal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        catch
        {
            // не записалось — завтра строка может появиться дважды, это не страшно
        }
    }

    /// <summary>Для тестов: сбросить накопленное.</summary>
    internal static void ResetForTests()
    {
        ApiSeries.Clear();
        SqliteSeries.Clear();
        LastSlowLogTicks.Clear();
        _windowStartUtc = DateTime.UtcNow;
    }
}
