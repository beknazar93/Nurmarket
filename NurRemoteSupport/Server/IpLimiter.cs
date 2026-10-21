using System.Collections.Concurrent;

namespace NurRemoteSupport.Server;

/// <summary>2026-10-16: скользящее окно попыток по IP: вход оператора, неудачные входы, регистрация касс.</summary>
public sealed class IpLimiter(int limit, TimeSpan window)
{
    private readonly ConcurrentDictionary<string, Queue<long>> _hits = new();
    private readonly long _windowMs = (long)window.TotalMilliseconds;

    private static long Now => Environment.TickCount64;

    /// <summary>Засчитать попытку, если лимит не исчерпан.</summary>
    public bool TryHit(string ip)
    {
        var q = _hits.GetOrAdd(ip, _ => new Queue<long>());
        lock (q)
        {
            Trim(q);
            if (q.Count >= limit)
                return false;
            q.Enqueue(Now);
            return true;
        }
    }

    public void Hit(string ip)
    {
        var q = _hits.GetOrAdd(ip, _ => new Queue<long>());
        lock (q)
        {
            Trim(q);
            q.Enqueue(Now);
        }
    }

    public bool IsBlocked(string ip)
    {
        if (!_hits.TryGetValue(ip, out var q))
            return false;
        lock (q)
        {
            Trim(q);
            return q.Count >= limit;
        }
    }

    /// <summary>Через сколько секунд освободится попытка (0 — уже можно).</summary>
    public int RetryAfter(string ip)
    {
        if (!_hits.TryGetValue(ip, out var q))
            return 0;
        lock (q)
        {
            Trim(q);
            return q.Count < limit ? 0 : (int)Math.Ceiling((q.Peek() + _windowMs - Now) / 1000.0);
        }
    }

    /// <summary>Убрать пустые записи (раз в минуту из Relay.Tick).</summary>
    public void Cleanup()
    {
        foreach (var (ip, q) in _hits)
        {
            lock (q)
            {
                Trim(q);
                if (q.Count == 0)
                    _hits.TryRemove(new KeyValuePair<string, Queue<long>>(ip, q));
            }
        }
    }

    private void Trim(Queue<long> q)
    {
        var edge = Now - _windowMs;
        while (q.Count > 0 && q.Peek() <= edge)
            q.Dequeue();
    }
}
