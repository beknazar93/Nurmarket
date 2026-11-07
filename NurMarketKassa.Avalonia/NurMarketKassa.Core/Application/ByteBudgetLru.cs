namespace NurMarketKassa.Core.Application;

/// <summary>2026-11-06 (В16, память): порядок «последний использованный» для кэша картинок с пределом не только по штукам, но и по СУММЕ байтов пикселей.
/// Предел «160 миниатюр» не учитывал размер: высокое старое фото, расшифрованное в ширину 320 px, занимает до 1,3 МБ, и 160 таких — 200 МБ родной памяти.
/// Класс сам ничего не хранит, кроме ключей и их размеров: что удалять из словаря-кэша, решает вызывающий по списку, который вернул <see cref="Touch"/> / <see cref="Trim"/>.
/// НЕ потокобезопасен: вызывающий берёт свой замок. Самый свежий ключ не вытесняется никогда (одна огромная картинка остаётся, пока её показывают).</summary>
public sealed class ByteBudgetLru
{
    private readonly int _maxCount;
    private readonly long _maxBytes;
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, long Bytes)> _nodes = new(StringComparer.Ordinal);

    public ByteBudgetLru(int maxCount, long maxBytes)
    {
        _maxCount = Math.Max(1, maxCount);
        _maxBytes = Math.Max(1, maxBytes);
    }

    public int Count => _order.Count;
    public long TotalBytes { get; private set; }

    public bool Contains(string key) => _nodes.ContainsKey(key);

    /// <summary>Ключ использован (новый — добавлен с размером <paramref name="bytes"/>). Возвращает ключи, которые вытеснены и которые вызывающий должен убрать из кэша.</summary>
    public List<string> Touch(string key, long bytes)
    {
        if (_nodes.TryGetValue(key, out var entry))
        {
            _order.Remove(entry.Node);
            _order.AddFirst(entry.Node);
            return new List<string>();
        }

        bytes = Math.Max(0, bytes);
        _nodes[key] = (_order.AddFirst(key), bytes);
        TotalBytes += bytes;
        return Evict(_maxCount, _maxBytes);
    }

    /// <summary>Оставить не больше <paramref name="keepCount"/> последних ключей (и не больше обычного предела байтов). Возвращает вытесненные ключи.</summary>
    public List<string> Trim(int keepCount)
    {
        var evicted = Evict(Math.Max(0, keepCount), _maxBytes, keepNewest: false);
        return evicted;
    }

    private List<string> Evict(int maxCount, long maxBytes, bool keepNewest = true)
    {
        var evicted = new List<string>();
        while (_order.Last is { } oldest
               && (_order.Count > maxCount || TotalBytes > maxBytes)
               && (!keepNewest || _order.Count > 1))
        {
            _order.RemoveLast();
            if (_nodes.Remove(oldest.Value, out var entry))
                TotalBytes -= entry.Bytes;
            evicted.Add(oldest.Value);
        }

        return evicted;
    }
}
