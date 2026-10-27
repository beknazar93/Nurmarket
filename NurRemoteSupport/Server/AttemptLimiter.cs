using System.Collections.Concurrent;

namespace NurRemoteSupport.Server;

/// <summary>
/// Простой счётчик попыток по ключу в скользящем окне (IP, логин, оператор).
/// Для join/подключений хоста/неудачных входов — там, где встроенный RateLimiter неудобен (внутри WebSocket).
/// </summary>
public sealed class AttemptLimiter
{
    private readonly ConcurrentDictionary<string, Queue<long>> _hits = new();
    private long _lastSweep = Environment.TickCount64;

    /// <summary>Засчитать попытку. false — предел в окне уже исчерпан (попытка не засчитана).</summary>
    public bool TryHit(string key, int limit, TimeSpan window)
    {
        var now = Environment.TickCount64;
        var q = _hits.GetOrAdd(key, _ => new Queue<long>());
        bool ok;
        lock (q)
        {
            Trim(q, now, window);
            ok = q.Count < limit;
            if (ok) q.Enqueue(now);
        }
        Sweep(now);
        return ok;
    }

    /// <summary>Засчитать без проверки (например, неудачный вход).</summary>
    public void Hit(string key)
    {
        var q = _hits.GetOrAdd(key, _ => new Queue<long>());
        lock (q) q.Enqueue(Environment.TickCount64);
    }

    public int Count(string key, TimeSpan window)
    {
        if (!_hits.TryGetValue(key, out var q)) return 0;
        lock (q)
        {
            Trim(q, Environment.TickCount64, window);
            return q.Count;
        }
    }

    private static void Trim(Queue<long> q, long now, TimeSpan window)
    {
        var edge = now - (long)window.TotalMilliseconds;
        while (q.Count > 0 && q.Peek() < edge) q.Dequeue();
        // Защита от бесконечного роста при Hit без окна
        while (q.Count > 1000) q.Dequeue();
    }

    private void Sweep(long now)
    {
        if (now - Interlocked.Read(ref _lastSweep) < 60_000) return;
        Interlocked.Exchange(ref _lastSweep, now);
        foreach (var (key, q) in _hits)
        {
            lock (q)
            {
                Trim(q, now, TimeSpan.FromMinutes(30));
                if (q.Count == 0) _hits.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>2026-10-27 (В-6): счётчик одновременных соединений по ключу (сеть /64 или IPv4).</summary>
public sealed class ConnectionGate
{
    private readonly ConcurrentDictionary<string, int> _n = new();

    public bool TryEnter(string key, int max)
    {
        var ok = true;
        _n.AddOrUpdate(key, 1, (_, c) =>
        {
            ok = c < max;
            return ok ? c + 1 : c;
        });
        return ok;
    }

    public void Leave(string key)
    {
        while (_n.TryGetValue(key, out var c))
        {
            if (c <= 1) { if (_n.TryRemove(new KeyValuePair<string, int>(key, c))) return; }
            else if (_n.TryUpdate(key, c - 1, c)) return;
        }
    }
}
