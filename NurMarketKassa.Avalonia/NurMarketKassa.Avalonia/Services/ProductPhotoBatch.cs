using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-03, владелец (15 252 товара без фото, найдено 10): «исправь это ограничение или по этапам добавляй по 50 или по 20 по очереди,
/// чтобы не нагружать систему». Поиск фото идёт пачками. Здесь — часть без окон и без сети (проверяется консольной имитацией):
/// <see cref="RequestPacer"/> — лимит запросов в минуту, <see cref="PhotoSearchMemory"/> — что уже проверено (файл),
/// <see cref="PhotoBatchRunner{T,TResult}"/> — пачка за пачкой с остановкой и продолжением.</summary>
public sealed class PhotoQuotaException : Exception
{
    /// <summary>Что успели найти до отказа (например, фото есть, а на описание лимит кончился) — чтобы не потерять.</summary>
    public object? Partial { get; }

    public PhotoQuotaException(string message, object? partial = null) : base(message) => Partial = partial;
}

/// <summary>Не чаще <c>maxPerWindow</c> запросов за окно (обычно минуту) и не чаще одного запроса за <c>minGap</c>. Ждущие запросы идут по одному,
/// ожидание прерывается отменой.</summary>
public sealed class RequestPacer
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly TimeSpan _gap;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Queue<DateTime> _times = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    public RequestPacer(int maxPerWindow, TimeSpan window, TimeSpan minGap = default,
        Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _max = Math.Max(1, maxPerWindow);
        _window = window;
        _gap = minGap;
        _now = now ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var now = _now();
                while (_times.Count > 0 && now - _times.Peek() >= _window)
                    _times.Dequeue();
                var wait = TimeSpan.Zero;
                if (_times.Count >= _max)
                    wait = _times.Peek() + _window - now;
                if (_gap > TimeSpan.Zero && now - _last < _gap)
                {
                    var gapWait = _last + _gap - now;
                    if (gapWait > wait)
                        wait = gapWait;
                }
                if (wait <= TimeSpan.Zero)
                {
                    _times.Enqueue(now);
                    _last = now;
                    return;
                }
                await _delay(wait + TimeSpan.FromMilliseconds(5), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Память поиска фото: по каждому товару без фото — когда проверен и чем кончилось. Следующий запрос «найди фото» продолжает
/// с того места, а не с начала. Файл в папке данных компании (стирается при смене компании, <c>AccountDataIsolation</c>).</summary>
public sealed class PhotoSearchMemory
{
    public const string Found = "found", NotFound = "notfound", Set = "set";

    /// <summary>«Не найдено» перепроверяется не раньше чем через 30 дней (или по кнопке «Искать заново»).</summary>
    public static readonly TimeSpan NotFoundAge = TimeSpan.FromDays(30);
    /// <summary>Найденное, но не поставленное владельцем, не показывается снова двое суток.</summary>
    public static readonly TimeSpan FoundAge = TimeSpan.FromDays(2);
    /// <summary>«Не найдено», когда интернет-поиск был недоступен (лимит ключа), — перепроверка через сутки.</summary>
    public static readonly TimeSpan NoWebAge = TimeSpan.FromDays(1);

    public sealed class Entry
    {
        [JsonPropertyName("s")] public string State { get; set; } = NotFound;
        [JsonPropertyName("t")] public DateTime CheckedUtc { get; set; }
        [JsonPropertyName("w")] public bool NoWeb { get; set; }
    }

    public sealed class Data
    {
        public int BatchSize { get; set; } = 20;
        public bool AutoNext { get; set; }
        public Dictionary<string, Entry> Items { get; set; } = new();
    }

    private static string DefaultPath => Path.Combine(
        NurMarketKassa.Services.AppPaths.Roaming,
        NurMarketKassa.Services.AppMode.DataFolderName, "photo-search-progress.json");

    private static PhotoSearchMemory? _instance;
    private static readonly object InstanceGate = new();

    /// <summary>Память этой программы. После смены компании — <see cref="ResetMemory"/> (файл уже стёрт).</summary>
    public static PhotoSearchMemory Instance
    {
        get
        {
            lock (InstanceGate)
                return _instance ??= new PhotoSearchMemory(DefaultPath);
        }
    }

    public static void ResetMemory()
    {
        lock (InstanceGate)
            _instance = null;
    }

    private readonly string _path;
    private readonly object _gate = new();
    private Data _data = new();
    private int _unsaved;

    public PhotoSearchMemory(string path)
    {
        _path = path;
        Load();
    }

    private void Load()
    {
        try
        {
            var text = AtomicFile.ReadAllText(_path);
            if (!string.IsNullOrWhiteSpace(text))
                _data = JsonSerializer.Deserialize<Data>(text) ?? new Data();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Память поиска фото не прочитана ({ex.Message}) — начинаю заново.", "WARNING");
            _data = new Data();
        }
        _data.Items ??= new();
    }

    /// <summary>Размер пачки: 20 или 50.</summary>
    public int BatchSize
    {
        get { lock (_gate) return _data.BatchSize == 50 ? 50 : 20; }
        set { lock (_gate) _data.BatchSize = value >= 50 ? 50 : 20; Save(); }
    }

    /// <summary>«Искать автоматически по очереди».</summary>
    public bool AutoNext
    {
        get { lock (_gate) return _data.AutoNext; }
        set { lock (_gate) _data.AutoNext = value; Save(); }
    }

    public int Count { get { lock (_gate) return _data.Items.Count; } }

    public bool ShouldSkip(string id, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_data.Items.TryGetValue(id, out var e))
                return false;
            var age = nowUtc - e.CheckedUtc;
            return e.State switch
            {
                Set => true,
                Found => age < FoundAge,
                _ => age < (e.NoWeb ? NoWebAge : NotFoundAge),
            };
        }
    }

    public string? StateOf(string id)
    {
        lock (_gate)
            return _data.Items.TryGetValue(id, out var e) ? e.State : null;
    }

    public void Mark(string id, string state, bool noWeb = false)
    {
        if (string.IsNullOrEmpty(id))
            return;
        bool flush;
        lock (_gate)
        {
            _data.Items[id] = new Entry { State = state, CheckedUtc = DateTime.UtcNow, NoWeb = noWeb && state == NotFound };
            flush = ++_unsaved >= 20;
        }
        if (flush)
            Save();
    }

    /// <summary>«Искать заново»: забыть проверенное (кроме поставленных). Возвращает, сколько записей забыто.</summary>
    public int ResetChecked()
    {
        int removed;
        lock (_gate)
        {
            var drop = _data.Items.Where(p => p.Value.State != Set).Select(p => p.Key).ToList();
            foreach (var key in drop)
                _data.Items.Remove(key);
            removed = drop.Count;
        }
        Save();
        return removed;
    }

    /// <summary>Записать на диск (в конце пачки, при остановке, раз в 20 отметок).</summary>
    public void Save()
    {
        string json;
        lock (_gate)
        {
            _unsaved = 0;
            json = JsonSerializer.Serialize(_data);
        }
        try
        {
            AtomicFile.WriteAllTextWithRetry(_path, json);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Память поиска фото не записана ({ex.Message}).", "WARNING");
        }
    }
}

/// <summary>Пачка за пачкой: берёт не проверенные ранее товары, ищет по два одновременно, отмечает результат в памяти сразу после товара
/// (остановка в любой момент не теряет сделанное). Лимиты запросов — в самой функции поиска (<see cref="RequestPacer"/>).</summary>
public sealed class PhotoBatchRunner<T, TResult> where TResult : class
{
    public sealed record BatchResult(List<(T Item, TResult Result)> Found, List<T> NotFound, int Processed, int BatchSize, int Remaining,
        bool QuotaStop, bool Cancelled);

    private readonly PhotoSearchMemory _memory;
    private readonly Func<T, string> _idOf;
    private readonly Func<bool> _webAvailable;

    public PhotoBatchRunner(PhotoSearchMemory memory, Func<T, string> idOf, Func<bool>? webAvailable = null)
    {
        _memory = memory;
        _idOf = idOf;
        _webAvailable = webAvailable ?? (() => true);
    }

    /// <summary>Сколько товаров без фото было в начале (первый запрос), просмотрено / найдено за этот поиск, сколько осталось.</summary>
    public int Total { get; private set; }
    public int Viewed { get; private set; }
    public int FoundCount { get; private set; }
    public int Remaining { get; private set; }
    public int Batches { get; private set; }

    public int CountPending(IReadOnlyList<T> candidates, bool redo)
    {
        if (redo)
            return candidates.Count;
        var now = DateTime.UtcNow;
        return candidates.Count(c => !_memory.ShouldSkip(_idOf(c), now));
    }

    /// <summary>Одна пачка. <paramref name="candidates"/> — товары без фото в нужном порядке; уже проверенные пропускаются (кроме <paramref name="redo"/>).</summary>
    public async Task<BatchResult> RunBatchAsync(IReadOnlyList<T> candidates, int size, bool redo,
        Func<T, CancellationToken, Task<TResult?>> search, Action<int, int>? progress, CancellationToken ct, int parallel = 2)
    {
        var now = DateTime.UtcNow;
        var pending = redo ? candidates.ToList() : candidates.Where(c => !_memory.ShouldSkip(_idOf(c), now)).ToList();
        if (Batches == 0)
            Total = candidates.Count;
        var batch = pending.Take(Math.Max(1, size)).ToList();
        var results = new TResult?[batch.Count];
        var finished = new bool[batch.Count];
        var done = 0;
        var quota = false;
        using var local = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(Math.Clamp(parallel, 1, 2));
        var tasks = batch.Select(async (item, i) =>
        {
            try
            {
                await gate.WaitAsync(local.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (local.IsCancellationRequested)
                    return;
                TResult? result = null;
                var failed = false;
                try
                {
                    result = await search(item, local.Token).ConfigureAwait(false);
                }
                catch (PhotoQuotaException q)
                {
                    quota = true;
                    result = q.Partial as TResult;   // что успели найти до отказа — не теряем
                    if (result is null)
                    {
                        local.Cancel();
                        return;
                    }
                    local.Cancel();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    failed = true;
                    PosLogger.Log($"Поиск фото: товар «{_idOf(item)}» не обработан ({ex.Message}).", "WARNING");
                }
                results[i] = result;
                finished[i] = true;
                // «Не найдено» из-за сбоя или недоступного интернета — перепроверка через сутки, а не через 30 дней.
                _memory.Mark(_idOf(item), result is null ? PhotoSearchMemory.NotFound : PhotoSearchMemory.Found, noWeb: failed || !_webAvailable());
                progress?.Invoke(Interlocked.Increment(ref done), batch.Count);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _memory.Save();

        var found = new List<(T, TResult)>();
        var notFound = new List<T>();
        for (var i = 0; i < batch.Count; i++)
        {
            if (!finished[i])
                continue;
            if (results[i] is { } r)
                found.Add((batch[i], r));
            else
                notFound.Add(batch[i]);
        }
        var processed = found.Count + notFound.Count;
        Batches++;
        Viewed += processed;
        FoundCount += found.Count;
        Remaining = CountPending(candidates, redo);
        if (redo)
            Remaining = Math.Max(0, candidates.Count - Viewed);
        PosLogger.Log($"Поиск фото пачкой: просмотрено {processed} из {batch.Count}, найдено {found.Count}, осталось ~{Remaining}{(quota ? ", остановка: лимит ключа" : "")}.", "CATALOG");
        return new BatchResult(found, notFound, processed, batch.Count, Remaining, quota, ct.IsCancellationRequested);
    }

    /// <summary>Пачки подряд: после каждой — <paramref name="onBatch"/>; если включено «по очереди» — пауза и следующая пачка, пока не нажали «Стоп»,
    /// не кончились товары или не кончился лимит ключа.</summary>
    public async Task<BatchResult?> RunAsync(Func<IReadOnlyList<T>> loadCandidates, Func<int> size, Func<bool> autoNext, bool redo, TimeSpan pause,
        Func<T, CancellationToken, Task<TResult?>> search, Action<int, int>? progress, Func<BatchResult, Task> onBatch, CancellationToken ct)
    {
        BatchResult? last = null;
        while (true)
        {
            var candidates = loadCandidates();
            last = await RunBatchAsync(candidates, size(), redo, search, progress, ct).ConfigureAwait(false);
            await onBatch(last).ConfigureAwait(false);
            if (!autoNext() || last.QuotaStop || last.Cancelled || last.Remaining <= 0 || last.Processed == 0 || redo)
                return last;
            try
            {
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return last;
            }
        }
    }
}
