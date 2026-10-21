namespace NurMarketKassa.Services;

/// <summary>2026-10-20, живая проверка кассы («большой чек медленный: от скана до итога ~1 с при 275 строках»):
/// строки чека и экрана покупателя при КАЖДОМ скане стирались и создавались заново — список на экране
/// пересоздавал все строки, а подписчики (Lines.CollectionChanged) срабатывали сотни раз. Здесь список
/// приводится к новому виду наименьшей правкой: общее начало и общий конец остаются теми же объектами,
/// меняется только середина (новый товар сверху — одна вставка, +1 к количеству — одна замена).
/// Содержимое списка после правки то же, что после «очистить и заполнить заново».</summary>
public static class ObservableListPatch
{
    /// <summary>Приводит <paramref name="target"/> к <paramref name="desired"/>. Совпавшие (<paramref name="same"/>)
    /// элементы в начале и в конце не трогаются — в списке остаются старые объекты.</summary>
    public static void Apply<T>(IList<T> target, IReadOnlyList<T> desired, Func<T, T, bool> same)
    {
        var oldCount = target.Count;
        var newCount = desired.Count;
        var prefix = 0;
        while (prefix < oldCount && prefix < newCount && same(target[prefix], desired[prefix]))
            prefix++;
        var suffix = 0;
        while (suffix < oldCount - prefix && suffix < newCount - prefix
               && same(target[oldCount - 1 - suffix], desired[newCount - 1 - suffix]))
            suffix++;

        var oldMiddle = oldCount - prefix - suffix;
        var newMiddle = newCount - prefix - suffix;
        var common = Math.Min(oldMiddle, newMiddle);
        for (var i = 0; i < common; i++)
            target[prefix + i] = desired[prefix + i];
        for (var i = oldMiddle - 1; i >= common; i--)
            target.RemoveAt(prefix + i);
        for (var i = common; i < newMiddle; i++)
            target.Insert(prefix + i, desired[prefix + i]);
    }

    /// <summary>То же по ключам: элементы середины создаются (<paramref name="make"/>) только там, где ключ
    /// поменялся. <paramref name="targetKeys"/> — ключи текущих элементов, идут параллельно списку.</summary>
    public static void ApplyByKey<T>(IList<T> target, List<string> targetKeys, IReadOnlyList<string> newKeys, Func<int, T> make)
    {
        if (targetKeys.Count != target.Count)
        {
            // Список меняли в обход ключей — заполняем заново.
            target.Clear();
            targetKeys.Clear();
        }

        var oldCount = target.Count;
        var newCount = newKeys.Count;
        var prefix = 0;
        while (prefix < oldCount && prefix < newCount && string.Equals(targetKeys[prefix], newKeys[prefix], StringComparison.Ordinal))
            prefix++;
        var suffix = 0;
        while (suffix < oldCount - prefix && suffix < newCount - prefix
               && string.Equals(targetKeys[oldCount - 1 - suffix], newKeys[newCount - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        var oldMiddle = oldCount - prefix - suffix;
        var newMiddle = newCount - prefix - suffix;
        var common = Math.Min(oldMiddle, newMiddle);
        for (var i = 0; i < common; i++)
        {
            target[prefix + i] = make(prefix + i);
            targetKeys[prefix + i] = newKeys[prefix + i];
        }
        for (var i = oldMiddle - 1; i >= common; i--)
        {
            target.RemoveAt(prefix + i);
            targetKeys.RemoveAt(prefix + i);
        }
        for (var i = common; i < newMiddle; i++)
        {
            target.Insert(prefix + i, make(prefix + i));
            targetKeys.Insert(prefix + i, newKeys[prefix + i]);
        }
    }
}
