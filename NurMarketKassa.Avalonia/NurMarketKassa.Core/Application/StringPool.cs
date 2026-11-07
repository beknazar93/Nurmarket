using System.Collections.Concurrent;

namespace NurMarketKassa.Core.Application;

/// <summary>2026-11-06 (В16, «чтобы у нас памяти было меньше»): пул повторяющихся коротких строк плиток каталога — единица измерения, вид товара, категория, бренд, страна.
/// База читает такие значения заново для каждой строки (на 15 000 товаров — 15 000 одинаковых «шт» и «product»), каждая копия — отдельный объект кучи.
/// Пул отдаёт ОДИН экземпляр на значение. Значения строк не меняются (сравнение, поиск, запись в базу — как раньше).
/// Только для полей с малым числом разных значений: цены-строки и остатки («93 шт») в пул не кладём — их тысячи разных, они вытеснили бы нужное.
/// Потокобезопасен (ConcurrentDictionary). Ограничен: не больше <see cref="MaxEntries"/> разных значений и длиной до <see cref="MaxLength"/> — иначе строка возвращается как есть,
/// так что пул не может разрастись от «мусорных» значений. Не использует string.Intern: тот живёт до конца процесса и не ограничен.</summary>
public sealed class StringPool
{
    /// <summary>Общий пул плиток каталога.</summary>
    public static StringPool Shared { get; } = new();

    private readonly ConcurrentDictionary<string, string> _pool = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly int _maxLength;

    public StringPool(int maxEntries = 4096, int maxLength = 64)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _maxLength = Math.Max(1, maxLength);
    }

    public int Count => _pool.Count;

    /// <summary>Тот же текст, но общий экземпляр. null и пустая строка возвращаются как есть.</summary>
    public string? Intern(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > _maxLength)
            return value;
        if (_pool.TryGetValue(value, out var shared))
            return shared;
        if (_pool.Count >= _maxEntries)
            return value;
        return _pool.GetOrAdd(value, value);
    }
}
