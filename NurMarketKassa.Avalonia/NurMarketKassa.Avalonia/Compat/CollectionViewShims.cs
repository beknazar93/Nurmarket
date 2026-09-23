using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace System.Windows.Data;

public enum ListSortDirection { Ascending, Descending }

public class SortDescription
{
    public SortDescription(string propertyName, ListSortDirection direction) { PropertyName = propertyName; Direction = direction; }
    public string PropertyName { get; }
    public ListSortDirection Direction { get; }
}

public interface ICollectionView : IEnumerable, INotifyPropertyChanged
{
    object? CurrentItem { get; }
    bool MoveCurrentTo(object? item);
    bool MoveCurrentToFirst();
    bool MoveCurrentToNext();
    Predicate<object>? Filter { get; set; }
    bool CanFilter { get; }
    IEnumerable SourceCollection { get; }
    void Refresh();
    SortDescriptionCollection SortDescriptions { get; }
    IDisposable DeferRefresh();
}

public class SortDescriptionCollection : Collection<SortDescription> { }

public class CollectionViewSource
{
    private object? _source;
    private ListCollectionView? _view;
    private event FilterEventHandler? _filter;

    public object? Source
    {
        get => _source;
        set
        {
            _source = value;
            _view = null;
        }
    }

    public ICollectionView View => _view ??= CreateView();

    public event FilterEventHandler Filter
    {
        add
        {
            _filter += value;
            _view = null;
        }
        remove
        {
            _filter -= value;
            _view = null;
        }
    }

    public static ICollectionView GetDefaultView(IEnumerable source) =>
        new ListCollectionView(source);

    private ListCollectionView CreateView()
    {
        var view = new ListCollectionView(_source as IEnumerable ?? Array.Empty<object>());
        if (_filter != null)
        {
            view.Filter = item =>
            {
                var args = new FilterEventArgs(item);
                _filter(this, args);
                return args.Accepted;
            };
        }
        return view;
    }
}

/// <summary>
/// Live view over <paramref name="source"/>: unlike a one-time snapshot, this always
/// re-enumerates the current source contents and forwards its
/// <see cref="INotifyCollectionChanged"/> notifications, so an ItemsControl/DataGrid
/// bound to this view keeps updating after the underlying ObservableCollection changes
/// (e.g. FinanceWindow reloading sales into an already-bound CollectionViewSource).
/// </summary>
internal sealed class ListCollectionView : ICollectionView, INotifyCollectionChanged
{
    private IEnumerable _source;
    private int _index = -1;

    public ListCollectionView(IEnumerable source)
    {
        _source = source;
        if (source is INotifyCollectionChanged incc)
            incc.CollectionChanged += OnSourceCollectionChanged;
    }

    private List<object> CurrentItems => _source.Cast<object>().ToList();

    public object? CurrentItem
    {
        get
        {
            var items = CurrentItems;
            return _index >= 0 && _index < items.Count ? items[_index] : null;
        }
    }

    public IEnumerable SourceCollection => _source;
    public Predicate<object>? Filter { get; set; }
    public bool CanFilter => true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public IEnumerator GetEnumerator()
    {
        foreach (var item in CurrentItems)
            if (Filter is null || Filter(item))
                yield return item;
    }

    public void ReplaceSource(IEnumerable source)
    {
        if (_source is INotifyCollectionChanged oldIncc)
            oldIncc.CollectionChanged -= OnSourceCollectionChanged;
        _source = source;
        if (source is INotifyCollectionChanged newIncc)
            newIncc.CollectionChanged += OnSourceCollectionChanged;
        _index = -1;
        RaiseReset();
    }

    public bool MoveCurrentTo(object? item)
    {
        _index = item is null ? -1 : CurrentItems.IndexOf(item);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
        return _index >= 0;
    }

    public bool MoveCurrentToFirst()
    {
        var items = CurrentItems;
        _index = items.Count > 0 ? 0 : -1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
        return _index >= 0;
    }

    public bool MoveCurrentToNext()
    {
        if (_index + 1 >= CurrentItems.Count) return false;
        _index++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
        return true;
    }

    public void Refresh() => RaiseReset();

    public SortDescriptionCollection SortDescriptions { get; } = new();

    public IDisposable DeferRefresh() => new DeferRefreshScope(this);

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RaiseReset();

    private void RaiseReset()
    {
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
    }

    private sealed class DeferRefreshScope : IDisposable
    {
        private readonly ListCollectionView _view;
        public DeferRefreshScope(ListCollectionView view) => _view = view;
        public void Dispose() => _view.Refresh();
    }
}

public class FilterEventArgs : EventArgs
{
    public object Item { get; }
    public bool Accepted { get; set; } = true;
    public FilterEventArgs(object item) => Item = item;
}

public delegate void FilterEventHandler(object sender, FilterEventArgs e);
