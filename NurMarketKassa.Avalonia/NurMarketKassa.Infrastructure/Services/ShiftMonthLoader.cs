using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>Итог загрузки смен «с конца»: что набралось, не оборвалась ли загрузка, дошли ли до нужной даты.</summary>
public sealed class ShiftMonthResult
{
    public IReadOnlyList<ShiftHistoryEntry> Entries { get; init; } = Array.Empty<ShiftHistoryEntry>();
    /// <summary>Какая-то страница не пришла (нет связи, таймаут, ошибка сервера): в <see cref="Entries"/> — то, что успели получить.</summary>
    public bool Failed { get; init; }
    /// <summary>Набрано всё, что нужно: дошли до даты «с» или история закончилась.</summary>
    public bool Covered { get; init; }
}

/// <summary>2026-11-19 (живой обход владельца, «Табель»: смены не грузятся — окно тянуло ВСЮ историю смен компании, на аккаунте NBS &gt; 20 с и таймаут).
/// У сервера есть только постраничный список (page=N, по 100, новые первыми), фильтра по датам нет. Поэтому качаем страницы с конца: первая — одна
/// (даёт результат за 2–3 с), дальше волнами по <see cref="Parallelism"/> параллельно, пока последняя страница не дойдёт до начала нужного месяца.
/// Скачанные страницы запоминаются: переключение месяца ‹ › назад докачивает только недостающее, а вперёд/в уже покрытый месяц — мгновенно.
/// Кэш живёт <see cref="CacheTtl"/> и привязан к входу (<c>scopeKey</c>); «force» — всегда заново. Страница N+1 не пришла — отдаём страницы до неё и Failed=true (неполно, но не пусто).</summary>
public sealed class ShiftMonthLoader
{
    public const int Parallelism = 3;
    public const int MaxPages = 30;
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly Func<int, CancellationToken, Task<IReadOnlyList<ShiftHistoryEntry>?>> _fetchPage;
    private readonly Func<string?> _scopeKey;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IReadOnlyList<ShiftHistoryEntry>> _pages = new();
    private bool _endReached;
    private int _pageSize;
    private DateTime _startedUtc = DateTime.MinValue;
    private string? _key;

    // 2026-11-19 (ответ Умара, признак shifts_month_filter): смены месяца одним запросом ?month=YYYY-MM&page_size=100. Кэш — по месяцам, живёт и сбрасывается вместе со страничным.
    /// <summary>Размер страницы запроса месяца (page_size).</summary>
    public const int MonthPageSize = 100;
    /// <summary>Глубже стольких месяцев назад месячный запрос не используем (много запросов) — прежний перебор страниц.</summary>
    public const int MaxMonths = 12;
    private readonly Func<int, int, int, CancellationToken, Task<(IReadOnlyList<ShiftHistoryEntry> Rows, bool? HasNext)?>>? _fetchMonthPage;
    private readonly Func<CancellationToken, Task<bool>>? _monthFilterAvailable;
    private readonly Dictionary<(int Year, int Month), IReadOnlyList<ShiftHistoryEntry>> _months = new();
    private bool _monthFilterRejected;

    /// <param name="fetchPage">Страница списка (с 1): строки; null — такой страницы нет (конец истории); исключение — ошибка загрузки.</param>
    /// <param name="fetchMonthPage">Страница смен месяца (год, месяц, страница с 1): строки и «есть следующая» (null — решает число строк); null — страницы нет; исключение — ошибка (400 — сервер не принял месяц → прежний перебор).</param>
    /// <param name="monthFilterAvailable">У сервера есть признак shifts_month_filter. Нет делегата или false — прежний перебор страниц.</param>
    public ShiftMonthLoader(Func<int, CancellationToken, Task<IReadOnlyList<ShiftHistoryEntry>?>> fetchPage,
        Func<string?>? scopeKey = null, Func<DateTime>? utcNow = null,
        Func<int, int, int, CancellationToken, Task<(IReadOnlyList<ShiftHistoryEntry> Rows, bool? HasNext)?>>? fetchMonthPage = null,
        Func<CancellationToken, Task<bool>>? monthFilterAvailable = null)
    {
        _fetchPage = fetchPage;
        _scopeKey = scopeKey ?? (() => null);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _fetchMonthPage = fetchMonthPage;
        _monthFilterAvailable = monthFilterAvailable;
    }

    /// <summary>Первое число месяца 00:00 — граница, глубже которой смены не нужны.</summary>
    public static DateTime MonthStart(int year, int month) => new(year, month, 1);

    /// <summary>Загрузить смены не новее, чем с даты <paramref name="since"/> (включительно). <paramref name="progress"/> — после каждой волны страниц (всё набранное).</summary>
    public async Task<ShiftMonthResult> LoadAsync(DateTime since, bool force, Action<IReadOnlyList<ShiftHistoryEntry>>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = _scopeKey();
            if (force || key != _key || _utcNow() - _startedUtc > CacheTtl)
            {
                _pages.Clear();
                _endReached = false;
                _pageSize = 0;
                _months.Clear();
                _monthFilterRejected = false;
                _startedUtc = _utcNow();
                _key = key;
            }

            if (_fetchMonthPage != null && _monthFilterAvailable != null && !_monthFilterRejected && await _monthFilterAvailable(ct).ConfigureAwait(false))
            {
                var byMonth = await LoadByMonthsAsync(since, progress, ct).ConfigureAwait(false);
                if (byMonth != null)
                    return byMonth;
            }

            var failed = false;
            while (!Covers(since) && _pages.Count < MaxPages)
            {
                ct.ThrowIfCancellationRequested();
                var first = _pages.Count + 1;
                var count = first == 1 ? 1 : Math.Min(Parallelism, MaxPages - _pages.Count);
                var wave = await Task.WhenAll(Enumerable.Range(first, count).Select(p => FetchSafeAsync(p, ct))).ConfigureAwait(false);
                foreach (var (rows, error) in wave)
                {
                    if (error)
                    {
                        failed = true;
                        break;
                    }
                    if (rows == null || rows.Count == 0)
                    {
                        _endReached = true;
                        break;
                    }
                    if (_pages.Count == 0)
                        _pageSize = rows.Count;
                    _pages.Add(rows);
                    if (rows.Count < _pageSize)
                    {
                        _endReached = true;
                        break;
                    }
                }

                progress?.Invoke(All());
                if (failed || _endReached)
                    break;
            }

            return new ShiftMonthResult { Entries = All(), Failed = failed, Covered = Covers(since) };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>2026-11-19: смены с даты <paramref name="since"/> по сегодня — по одному запросу на месяц (недостающие, по <see cref="Parallelism"/> параллельно), скачанные месяцы берутся из кэша.
    /// null — месячный путь не годится (сервер ответил 400 на месяц или месяцев больше <see cref="MaxMonths"/>): грузим прежним перебором страниц. Месяц не пришёл — Failed=true, остальные остаются.</summary>
    private async Task<ShiftMonthResult?> LoadByMonthsAsync(DateTime since, Action<IReadOnlyList<ShiftHistoryEntry>>? progress, CancellationToken ct)
    {
        var now = _utcNow().ToLocalTime();
        var first = new DateTime(since.Year, since.Month, 1);
        var last = new DateTime(now.Year, now.Month, 1);
        if (first > last)
            last = first; // показали будущий месяц — нужен только он
        var months = new List<(int Year, int Month)>();
        for (var m = first; m <= last; m = m.AddMonths(1))
            months.Add((m.Year, m.Month));
        if (months.Count > MaxMonths)
            return null;

        var failed = false;
        var missing = months.Where(m => !_months.ContainsKey(m)).ToList();
        for (var i = 0; i < missing.Count; i += Parallelism)
        {
            ct.ThrowIfCancellationRequested();
            var wave = await Task.WhenAll(missing.Skip(i).Take(Parallelism).Select(m => FetchMonthSafeAsync(m.Year, m.Month, ct))).ConfigureAwait(false);
            foreach (var (month, rows, status) in wave)
            {
                if (status == MonthFetch.Rejected)
                {
                    _monthFilterRejected = true;
                    PosLogger.Log("Смены: сервер не принял запрос по месяцу (400) — листаем страницами", "SHIFTS");
                    return null;
                }
                if (status == MonthFetch.Failed)
                    failed = true;
                else
                    _months[month] = rows;
            }
            progress?.Invoke(MonthsSoFar(months));
        }

        return new ShiftMonthResult { Entries = MonthsSoFar(months), Failed = failed, Covered = !failed };
    }

    private IReadOnlyList<ShiftHistoryEntry> MonthsSoFar(List<(int Year, int Month)> months) =>
        Merge(months.Where(m => _months.ContainsKey(m)).Select(m => _months[m]));

    private enum MonthFetch { Ok, Failed, Rejected }

    /// <summary>Все страницы одного месяца: пока сервер говорит «есть следующая» (или страница полная — 100 строк).</summary>
    private async Task<((int Year, int Month) Month, IReadOnlyList<ShiftHistoryEntry> Rows, MonthFetch Status)> FetchMonthSafeAsync(int year, int month, CancellationToken ct)
    {
        var all = new List<ShiftHistoryEntry>();
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var res = await _fetchMonthPage!(year, month, page, ct).ConfigureAwait(false);
                if (res is not { } r || r.Rows.Count == 0)
                    break;
                all.AddRange(r.Rows);
                if (!(r.HasNext ?? r.Rows.Count >= MonthPageSize))
                    break;
            }
            return ((year, month), all, MonthFetch.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode == 400)
        {
            return ((year, month), Array.Empty<ShiftHistoryEntry>(), MonthFetch.Rejected);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смены: месяц {year}-{month:D2} не получен: {ex.Message}", "SHIFTS");
            return ((year, month), Array.Empty<ShiftHistoryEntry>(), MonthFetch.Failed);
        }
    }

    /// <summary>Покрыто: история закончилась, либо самая старая из скачанных страниц уже начинается раньше нужной даты (список идёт от новых к старым).</summary>
    private bool Covers(DateTime since)
    {
        if (_endReached)
            return true;
        if (_pages.Count == 0)
            return false;
        var dates = _pages[^1].Where(s => s.OpenedAt.HasValue).Select(s => s.OpenedAt!.Value).ToList();
        return dates.Count > 0 && dates.Min() < since;
    }

    private IReadOnlyList<ShiftHistoryEntry> All() => Merge(_pages);

    private static IReadOnlyList<ShiftHistoryEntry> Merge(IEnumerable<IReadOnlyList<ShiftHistoryEntry>> pages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Пока листали, могла открыться новая смена и сдвинуть страницы — без повторов.
        return pages.SelectMany(p => p).Where(s => string.IsNullOrEmpty(s.ShiftNumber) || seen.Add(s.ShiftNumber))
            .OrderByDescending(s => s.OpenedAt ?? DateTime.MinValue).ToList();
    }

    private async Task<(IReadOnlyList<ShiftHistoryEntry>? Rows, bool Error)> FetchSafeAsync(int page, CancellationToken ct)
    {
        try
        {
            return (await _fetchPage(page, ct).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смены: страница {page} не получена: {ex.Message}", "SHIFTS");
            return (null, true);
        }
    }
}
