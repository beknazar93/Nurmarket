using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// 2026-11-05 (аудит скорости, Н-1 и 18.1): замеры для следующих шагов ускорения.
/// Н-1: раз в 5 минут в журнал (раздел API STATS) пишутся 10 самых частых путей API за период со счётчиками.
/// Путь пишется БЕЗ query-параметров и без значений (числа, UUID, длинные коды, почта/телефон в сегментах → «{id}»),
/// поэтому в журнал не попадают ни ID клиентов, ни штрихкоды, ни персональные данные.
/// 18.1: к строке API SLOW добавляются заголовки ответа x-request-id и x-response-time-ms (серверное время) рядом с клиентским.
/// Все методы разбора и форматирования — чистые функции, проверяются юнит-тестами.
/// </summary>
public static class ApiPathStats
{
    /// <summary>Период записи в журнал.</summary>
    public static readonly TimeSpan ReportPeriod = TimeSpan.FromMinutes(5);

    private const int TopCount = 10;
    private const int MaxKeys = 400;
    private const string OtherKey = "(прочие пути)";

    private static readonly ConcurrentDictionary<string, long[]> Counters = new(StringComparer.Ordinal);
    private static readonly object TimerGate = new();
    private static Timer? _timer;
    private static DateTime _periodStartUtc = DateTime.UtcNow;

    /// <summary>Учесть один запрос (вызывается из JwtBearerRefreshHandler на каждый запрос кассы к NurCRM).</summary>
    public static void Record(HttpMethod method, string? absolutePath, double elapsedMs)
    {
        try
        {
            EnsureTimer();
            var key = method.Method + " " + NormalizePath(absolutePath);
            if (!Counters.ContainsKey(key) && Counters.Count >= MaxKeys)
                key = OtherKey;
            PerfSummary.RecordApi(key, elapsedMs); // 2026-11-06: В5 шаг 24 (18.2) — те же пути для суточной строки PERF (p50/p95)
            var cell = Counters.GetOrAdd(key, _ => new long[2]);
            Interlocked.Increment(ref cell[0]);
            Interlocked.Add(ref cell[1], (long)Math.Max(0, elapsedMs));
        }
        catch
        {
            // счётчик — только для замеров; сбой не должен ломать запрос
        }
    }

    private static void EnsureTimer()
    {
        if (_timer is not null)
            return;
        lock (TimerGate)
        {
            if (_timer is not null)
                return;
            _periodStartUtc = DateTime.UtcNow;
            _timer = new Timer(_ => Flush(), null, ReportPeriod, ReportPeriod);
        }
    }

    private static void Flush()
    {
        try
        {
            var now = DateTime.UtcNow;
            var snapshot = new List<KeyValuePair<string, (long Count, long TotalMs)>>();
            foreach (var key in Counters.Keys.ToArray())
            {
                if (Counters.TryRemove(key, out var cell))
                    snapshot.Add(new(key, (Interlocked.Read(ref cell[0]), Interlocked.Read(ref cell[1]))));
            }
            var period = now - _periodStartUtc;
            _periodStartUtc = now;
            var line = BuildReport(snapshot, period);
            if (line is not null)
                PosLogger.Log(line, "API STATS");
        }
        catch
        {
            // журнал замеров не должен ломать программу
        }
    }

    /// <summary>Строка отчёта: «за 5 мин: 123 запр.; топ-10: GET /api/products/ ×40 (ср. 120 мс); …». Пусто — null.</summary>
    public static string? BuildReport(IEnumerable<KeyValuePair<string, (long Count, long TotalMs)>> snapshot, TimeSpan period, int top = TopCount)
    {
        var list = snapshot.Where(p => p.Value.Count > 0).ToList();
        if (list.Count == 0)
            return null;
        var total = list.Sum(p => p.Value.Count);
        var minutes = Math.Max(1, (int)Math.Round(period.TotalMinutes));
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"за {minutes} мин: {total} запр., путей {list.Count}; топ-{Math.Min(top, list.Count)}: ");
        var first = true;
        foreach (var p in list.OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal).Take(top))
        {
            if (!first)
                sb.Append("; ");
            first = false;
            var avg = p.Value.TotalMs / p.Value.Count;
            sb.Append(CultureInfo.InvariantCulture, $"{p.Key} ×{p.Value.Count} (ср. {avg} мс)");
        }
        return sb.ToString();
    }

    /// <summary>Путь без значений: query отбрасывается, «значения» в сегментах заменяются на «{id}».</summary>
    public static string NormalizePath(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return "/";
        var path = absolutePath;
        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0)
            path = path[..cut];
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
            if (IsValueSegment(segments[i]))
                segments[i] = "{id}";
        var result = string.Join('/', segments);
        return result.Length > 120 ? result[..120] : result;
    }

    private static bool IsValueSegment(string s)
    {
        if (s.Length == 0)
            return false;
        if (s.Contains('@') || s.Contains('%') || s.Contains('+'))
            return true;
        if (Guid.TryParse(s, out _))
            return true;
        var digits = 0;
        var allDigits = true;
        var allHex = true;
        foreach (var c in s)
        {
            if (char.IsDigit(c))
                digits++;
            else
                allDigits = false;
            if (!Uri.IsHexDigit(c))
                allHex = false;
        }
        if (allDigits)
            return true;
        if (s.Length >= 16 && allHex)
            return true;
        // коды, штрихкоды, номера: несколько цифр внутри длинного сегмента
        return digits >= 4 && s.Length >= 8;
    }

    /// <summary>18.1: хвост строки API SLOW — серверное время и идентификатор запроса из заголовков ответа. Нет заголовков — пустая строка.</summary>
    public static string FormatServerTimingSuffix(HttpResponseMessage? response)
    {
        if (response is null)
            return "";
        return FormatServerTimingSuffix(
            FirstHeader(response, "x-request-id"),
            FirstHeader(response, "x-response-time-ms"));
    }

    public static string FormatServerTimingSuffix(string? requestId, string? responseTimeMs)
    {
        var id = SanitizeHeaderValue(requestId);
        var ms = SanitizeHeaderValue(responseTimeMs);
        if (id.Length == 0 && ms.Length == 0)
            return "";
        var sb = new StringBuilder(" (");
        if (ms.Length > 0)
            sb.Append("сервер ").Append(ms).Append(" мс");
        if (id.Length > 0)
        {
            if (ms.Length > 0)
                sb.Append(", ");
            sb.Append("x-request-id ").Append(id);
        }
        return sb.Append(')').ToString();
    }

    private static string? FirstHeader(HttpResponseMessage response, string name)
    {
        try
        {
            if (response.Headers.TryGetValues(name, out var values))
                return values.FirstOrDefault();
        }
        catch
        {
            // заголовок мог быть недопустимым — просто не показываем
        }
        return null;
    }

    private static string SanitizeHeaderValue(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
            return "";
        var sb = new StringBuilder();
        foreach (var c in v.Trim())
        {
            if (sb.Length >= 64)
                break;
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
                sb.Append(c);
        }
        return sb.ToString();
    }
}
