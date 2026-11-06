namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06 (В11, ПМ-1): учёт ЗАКРЫТЫХ разделов программы владельца «по слабой ссылке». Закрытый раздел должен исчезнуть из памяти после ближайших сборок
/// второго поколения; если он жив и через несколько минут после закрытия, его кто-то держит (статическое событие, таймер, лямбда в кэше…) — это и есть утечка окна.
/// Раньше о ней узнавали только по снимку кучи (dotnet-gcdump), а какой именно раздел держится — нет. Теперь <see cref="FindSurvivors"/> называет раздел по имени, и
/// строка попадает в журнал (MemoryDiagnostics, раз в 5 минут). Слабая ссылка сама окно в памяти не удерживает. Чистая логика без Avalonia (проверяется тестами).</summary>
public sealed class ClosedObjectWatch
{
    /// <summary>Живой экземпляр программы владельца.</summary>
    public static ClosedObjectWatch Instance { get; } = new();

    private sealed record Entry(WeakReference Ref, string Name, DateTime ClosedUtc, int Gen2AtClose);

    private readonly object _gate = new();
    private readonly List<Entry> _items = new();

    /// <summary>Сколько закрытых объектов отслеживается сейчас (живых или ещё не вычищенных).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    /// <summary>Запомнить объект как закрытый. <paramref name="gen2Count"/> — GC.CollectionCount(2) в момент закрытия.</summary>
    public void Track(object closed, string name, DateTime nowUtc, int gen2Count)
    {
        lock (_gate)
        {
            _items.Add(new Entry(new WeakReference(closed), name, nowUtc, gen2Count));
            if (_items.Count > 200)
                _items.RemoveRange(0, _items.Count - 200); // предел: закрытий за день бывают сотни, а учёт не должен расти сам
        }
    }

    /// <summary>Кто из закрытых ещё жив: прошло не меньше <paramref name="minAge"/> И с закрытия было не меньше <paramref name="minGen2Collections"/> сборок второго поколения
    /// (без них сборщик мог просто не успеть). Собранные записи убираются из учёта. Возвращает имя и возраст.</summary>
    public IReadOnlyList<(string Name, TimeSpan Age)> FindSurvivors(DateTime nowUtc, int gen2Now, TimeSpan minAge, int minGen2Collections)
    {
        var result = new List<(string, TimeSpan)>();
        lock (_gate)
        {
            _items.RemoveAll(e => !e.Ref.IsAlive);
            foreach (var e in _items)
            {
                var age = nowUtc - e.ClosedUtc;
                if (age >= minAge && gen2Now - e.Gen2AtClose >= minGen2Collections)
                    result.Add((e.Name, age));
            }
        }

        return result;
    }

    /// <summary>Только для тестов: забыть всё.</summary>
    internal void Clear()
    {
        lock (_gate)
            _items.Clear();
    }
}
