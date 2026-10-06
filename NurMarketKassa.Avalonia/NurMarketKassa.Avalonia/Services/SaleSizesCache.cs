using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using NurMarketKassa.AvaloniaHost;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, владелец (снимок «Загружаю чеки: 890 из 1083…» за 30 дней): «очень долгая загрузка» отчёта
/// «Размеры и цвета». Отчёт при каждом открытии заново читал состав каждого чека периода — за месяц на тестовом
/// аккаунте 1083 запроса, 2–3 минуты. Быстрее качать нельзя: массовые загрузки идут через ApiThrottle, иначе сервер
/// отвечает 429 всей кассе. Списка чеков с составом и отчёта по размерам у сервера нет (ТЗ бэкенда, ч. 14, п. 14.10;
/// проверено 06.10: pos/sales/ без строк, analytics/market/variants/ — 404).
///
/// Теперь строки чеков с размером/цветом запоминаются на диске (sale_sizes_cache.json, данные компании), и следующее
/// открытие читает только новые чеки. Строки проведённого чека меняет только возврат: у полностью возвращённой строки
/// сервер убирает её из состава (проверено 06.10, чек №1598), у частичной уменьшает returnable_qty. Поэтому при каждом
/// открытии берётся список возвратов периода (GET pos/returns/, 1–3 запроса), и чек, по которому вернули товар после
/// того, как его запомнили, читается заново. Программа владельца в сфере «Одежда» подгружает чеки за 30 дней заранее,
/// в фоне и медленно (<see cref="RunBackgroundAsync"/>).</summary>
public static class SaleSizesCache
{
    /// <summary>Строка чека с размером или цветом: оставшееся у покупателя количество и выручка за него.</summary>
    public sealed record Line(string ProductId, string Name, string Size, string Color, double Quantity, double Revenue);

    /// <summary>Итог периода: строки, сколько чеков в периоде, сколько прочитано с сервера сейчас и сколько не прочиталось.</summary>
    public sealed record PeriodResult(List<Line> Lines, int Receipts, int Downloaded, int Failed);

    private sealed class StoredLine
    {
        public string P { get; set; } = "";
        public string N { get; set; } = "";
        public string S { get; set; } = "";
        public string C { get; set; } = "";
        public double Q { get; set; }
        public double R { get; set; }
    }

    private sealed class Entry
    {
        /// <summary>Когда чек прочитан с сервера (UTC).</summary>
        public DateTime F { get; set; }
        /// <summary>Дата чека — старше <see cref="KeepDays"/> дней из файла убираются.</summary>
        public DateTime D { get; set; }
        public List<StoredLine> L { get; set; } = new();
    }

    private sealed class FileModel
    {
        public int Version { get; set; }
        public string? Company { get; set; }
        public Dictionary<string, Entry> Sales { get; set; } = new();
    }

    private const int CurrentVersion = 1;
    private const int KeepDays = 100;
    /// <summary>Сервер отдаёт возвраты одним массивом; если пришло столько — период делится пополам (вдруг обрезал).</summary>
    private const int ReturnsPageCap = 50;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<string, Task<bool>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static ConcurrentDictionary<string, Entry>? _entries;
    private static string? _company;
    private static int _unsaved;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "sale_sizes_cache.json");

    private static string? CurrentCompany => CompanyInfoService.LastCompany?.Id;

    /// <summary>Чеки текущей компании; при смене компании — её файл (чужой файл не подхватывается: в нём записана компания).</summary>
    private static ConcurrentDictionary<string, Entry> Entries
    {
        get
        {
            lock (Sync)
            {
                var company = CurrentCompany;
                if (_entries is { } ready && string.Equals(_company, company, StringComparison.OrdinalIgnoreCase))
                    return ready;
                var d = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(FilePath)
                        && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), JsonOpts) is { Version: CurrentVersion } m
                        && string.Equals(m.Company, company, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var kv in m.Sales)
                            d[kv.Key] = kv.Value;
                    }
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Размеры и цвета: память чеков не прочитана ({ex.Message}) — начну заново.", "WARNING");
                }
                _company = company;
                _unsaved = 0;
                return _entries = d;
            }
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            _entries = null;
            _company = null;
            _unsaved = 0;
        }
    }

    private static void Save()
    {
        lock (Sync)
        {
            if (_entries is not { } entries || _company is null || !string.Equals(_company, CurrentCompany, StringComparison.OrdinalIgnoreCase))
                return;
            var border = DateTime.Today.AddDays(-KeepDays);
            var model = new FileModel
            {
                Version = CurrentVersion,
                Company = _company,
                Sales = entries.Where(kv => kv.Value.D >= border).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOpts));
                File.Move(tmp, FilePath, true);
                _unsaved = 0;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Размеры и цвета: память чеков не сохранена ({ex.Message}).", "WARNING");
            }
        }
    }

    /// <summary>Строки с размерами и цветами за период [from; toExclusive).</summary>
    /// <param name="background">Фоновая подгрузка: по одному чеку с паузой, чтобы не мешать кассе и отчётам.</param>
    /// <param name="onProgress">(прочитано, всего, строки на сейчас или null) — из фонового потока; строки — примерно
    /// раз в 150 чеков, чтобы окно дорисовывало таблицы по ходу.</param>
    public static async Task<PeriodResult> GetPeriodAsync(DateTime from, DateTime toExclusive, bool background,
        Action<int, int, List<Line>?>? onProgress, CancellationToken ct)
    {
        var entries = Entries;
        var company = _company;

        // 1. Чеки периода (до 500 на страницу; отменённые не считаются).
        var sales = new List<(string Id, DateTime Date)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 40; page++)
        {
            var batch = await App.SalesApi.PosSalesListAsync(page, 500, null, ct, from, toExclusive).ConfigureAwait(false);
            foreach (var s in batch)
            {
                if (string.Equals(Str(s, "status"), "canceled", StringComparison.OrdinalIgnoreCase) || Str(s, "id") is not { Length: > 0 } id || !seen.Add(id))
                    continue;
                var date = DateTimeOffset.TryParse(Str(s, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.LocalDateTime.Date : DateTime.Today;
                sales.Add((id, date));
            }
            if (batch.Count < 500)
                break;
        }

        // 2. Возвраты периода: чек, по которому вернули после того, как его запомнили, читаем заново.
        var stale = 0;
        var staleBeforeUtc = DateTime.MinValue;
        var returns = await ReturnsAsync(from.Date, toExclusive.Date.AddDays(-1), ct, cappedDay =>
        {
            // За один день сервер отдал 50 возвратов — могли быть ещё; всё, что запомнено до конца того дня, перечитываем.
            var end = cappedDay.AddDays(1).ToUniversalTime();
            if (end > staleBeforeUtc)
                staleBeforeUtc = end;
        }).ConfigureAwait(false);
        if (returns is null)
            PosLogger.Log("Размеры и цвета: список возвратов не получен — запомненные чеки взяты как есть.", "WARNING");
        else
            foreach (var r in returns)
            {
                if (r.SaleId is { Length: > 0 } saleId && entries.TryGetValue(saleId, out var e)
                    && (r.CreatedAt is not { } at || e.F < at.UtcDateTime.AddMinutes(5)) && entries.TryRemove(saleId, out _))
                {
                    SaleDetailCache.Forget(saleId);
                    stale++;
                }
            }
        if (staleBeforeUtc > DateTime.MinValue)
            foreach (var (id, _) in sales)
                if (entries.TryGetValue(id, out var e) && e.F < staleBeforeUtc && entries.TryRemove(id, out _))
                {
                    SaleDetailCache.Forget(id);
                    stale++;
                }

        // 3. Недостающие чеки — с сервера (новые первыми: список продаж идёт от новых к старым).
        var missing = sales.Where(s => !entries.ContainsKey(s.Id)).ToList();
        var done = sales.Count - missing.Count;
        var failed = 0;
        var lastSnapshot = done;
        onProgress?.Invoke(done, sales.Count, missing.Count > 0 && done > 0 ? Collect(entries, sales) : null);

        async Task One((string Id, DateTime Date) sale)
        {
            bool ok;
            try
            {
                ok = await InFlight.GetOrAdd(sale.Id, _ => FetchAsync(sale.Id, sale.Date, entries, company, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ok = false; // чек читал другой отчёт, и его закрыли — прочитаем в следующий раз
            }
            finally
            {
                InFlight.TryRemove(sale.Id, out _);
            }
            if (!ok)
                Interlocked.Increment(ref failed);
            var n = Interlocked.Increment(ref done);
            if (onProgress is not null && (n % 10 == 0 || n == sales.Count))
            {
                List<Line>? snapshot = null;
                if (n - Volatile.Read(ref lastSnapshot) >= 150 && n < sales.Count)
                {
                    Volatile.Write(ref lastSnapshot, n);
                    snapshot = Collect(entries, sales);
                }
                onProgress(n, sales.Count, snapshot);
            }
        }

        if (background)
        {
            foreach (var sale in missing)
            {
                ct.ThrowIfCancellationRequested();
                await One(sale).ConfigureAwait(false);
                await Task.Delay(700, ct).ConfigureAwait(false);
            }
        }
        else
        {
            using var gate = new SemaphoreSlim(3);
            await Task.WhenAll(missing.Select(async sale =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await One(sale).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }

        if (missing.Count > 0 || stale > 0)
            Save();
        if (missing.Count > 0 || stale > 0 || !background)
            PosLogger.Log($"Размеры и цвета: {from:dd.MM}–{toExclusive.AddDays(-1):dd.MM}, чеков {sales.Count}, из памяти {sales.Count - missing.Count}, " +
                          $"с сервера {missing.Count - failed} (после возврата {stale}), не прочитано {failed}{(background ? ", в фоне" : "")}.", "UI");
        return new PeriodResult(Collect(entries, sales), sales.Count, missing.Count - failed, failed);
    }

    private static async Task<bool> FetchAsync(string saleId, DateTime date, ConcurrentDictionary<string, Entry> entries, string? company, CancellationToken ct)
    {
        JsonElement detail;
        try
        {
            detail = await SaleDetailCache.GetWithRetryAsync(saleId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Размеры и цвета: чек {saleId} не прочитан ({SaleDetailCache.Describe(ex)}).", "WARNING");
            return false;
        }
        if (detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return false;
        // Компанию сменили, пока читали, — в память новой компании чужой чек не кладём.
        if (!string.Equals(company, CurrentCompany, StringComparison.OrdinalIgnoreCase))
            return false;
        entries[saleId] = new Entry { F = DateTime.UtcNow, D = date, L = Extract(items) };
        if (Interlocked.Increment(ref _unsaved) >= 100)
            Save();
        return true;
    }

    /// <summary>Строки с размером или цветом; возвращённое не считается (returnable_qty).</summary>
    private static List<StoredLine> Extract(JsonElement items)
    {
        var list = new List<StoredLine>();
        foreach (var it in items.EnumerateArray())
        {
            var size = Str(it, "variant_size")?.Trim() ?? "";
            var color = Str(it, "variant_color")?.Trim() ?? "";
            if (size.Length == 0 && color.Length == 0)
                continue;
            var qty = Num(it, "quantity");
            var kept = it.TryGetProperty("returnable_qty", out _) ? Num(it, "returnable_qty") : qty;
            if (kept <= 0)
                continue;
            var total = it.TryGetProperty("line_total", out _) ? Num(it, "line_total") : Num(it, "amount");
            list.Add(new StoredLine
            {
                P = Str(it, "product") ?? "",
                N = (Str(it, "product_name") ?? Str(it, "name_snapshot") ?? "").Trim(),
                S = size,
                C = color,
                Q = kept,
                R = qty > 0 ? total * kept / qty : total,
            });
        }
        return list;
    }

    private static List<Line> Collect(ConcurrentDictionary<string, Entry> entries, List<(string Id, DateTime Date)> sales)
    {
        var lines = new List<Line>();
        foreach (var (id, _) in sales)
            if (entries.TryGetValue(id, out var e))
                lines.AddRange(e.L.Select(l => new Line(l.P, l.N, l.S, l.C, l.Q, l.R)));
        return lines;
    }

    /// <summary>Возвраты за дни [from; to] (date_to у возвратов включает свой день). Пришло 50 — делим период пополам;
    /// если 50 и за один день — сообщаем этот день (<paramref name="onCappedDay"/>). null — сервер не ответил.</summary>
    private static async Task<List<NurCrmReportsApi.PosReturn>?> ReturnsAsync(DateTime from, DateTime to, CancellationToken ct, Action<DateTime> onCappedDay)
    {
        if (to < from)
            return new List<NurCrmReportsApi.PosReturn>();
        var rows = await NurCrmReportsApi.ListReturnsAsync(from, to, null, null, ct).ConfigureAwait(false);
        if (rows is null)
            return null;
        if (rows.Count < ReturnsPageCap)
            return rows.ToList();
        if (from >= to)
        {
            onCappedDay(from);
            return rows.ToList();
        }
        var mid = from.AddDays((to - from).Days / 2);
        var a = await ReturnsAsync(from, mid, ct, onCappedDay).ConfigureAwait(false);
        var b = await ReturnsAsync(mid.AddDays(1), to, ct, onCappedDay).ConfigureAwait(false);
        if (a is null || b is null)
            return null;
        a.AddRange(b);
        return a;
    }

    /// <summary>Фон программы владельца: через 2 минуты после запуска и потом раз в час запоминает чеки за 30 дней —
    /// по одному, с паузой. Отчёт после этого открывается сразу, читая только новые чеки.</summary>
    public static async Task RunBackgroundAsync(Func<bool> enabled, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (enabled())
                        await GetPeriodAsync(DateTime.Today.AddDays(-29), DateTime.Today.AddDays(1), true, null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Размеры и цвета: фоновая подгрузка чеков не удалась ({ex.Message}).", "WARNING");
                }
                await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
