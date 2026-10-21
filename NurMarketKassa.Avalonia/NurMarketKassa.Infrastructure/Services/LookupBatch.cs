namespace NurMarketKassa.Services;

/// <summary>2026-10-20, живая проверка кассы («большой чек медленный»): при перестройке строк чека для КАЖДОЙ
/// строки заново читался с диска и разбирался файл отложенных чеков (лимит остатка строки — минус то, что
/// отложено). Пока открыт пакет (<see cref="Begin"/>, одна перестройка строк в потоке UI), одинаковые
/// запросы считаются один раз: за время пакета ни файл, ни чеки не меняются, поэтому ответы те же.
/// Вне пакета <see cref="Memo{T}"/> просто вызывает функцию, как раньше.</summary>
public static class LookupBatch
{
    [ThreadStatic] private static Dictionary<string, object?>? _memo;
    [ThreadStatic] private static int _depth;

    public static IDisposable Begin()
    {
        if (_depth++ == 0)
            _memo = new Dictionary<string, object?>(StringComparer.Ordinal);
        return new Scope();
    }

    public static T Memo<T>(string key, Func<T> compute)
    {
        if (_memo is null)
            return compute();
        if (_memo.TryGetValue(key, out var cached) && cached is T typed)
            return typed;
        if (cached is null && _memo.ContainsKey(key) && default(T) is null)
            return default!;
        var value = compute();
        _memo[key] = value;
        return value;
    }

    private sealed class Scope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            if (--_depth == 0)
                _memo = null;
        }
    }
}
