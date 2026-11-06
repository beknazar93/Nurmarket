using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06 (В11, шаг 7, п. 26.2): список, который заменяется целиком ОДНИМ событием. <see cref="ObservableCollection{T}"/> на каждый Clear() и каждый Add()
/// шлёт своё событие, и список клиентов на 6000 строк на каждую букву поиска перерисовывался 6000 раз. <see cref="ReplaceAll"/> меняет содержимое без событий по одному
/// и в конце даёт одно «Reset» (таблица перечитывается один раз). Без Avalonia — чистая логика (проверяется тестами).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public BulkObservableCollection()
    {
    }

    public BulkObservableCollection(IEnumerable<T> items) : base(items)
    {
    }

    /// <summary>Заменяет всё содержимое. Событие — одно (Reset), и только если что-то изменилось (пусто → пусто событий не даёт).</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var fresh = items as IList<T> ?? items.ToList();
        if (Items.Count == 0 && fresh.Count == 0)
            return;

        CheckReentrancy();
        Items.Clear();
        foreach (var item in fresh)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
