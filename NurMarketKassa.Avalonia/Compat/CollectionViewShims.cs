using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;

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

internal sealed class ListCollectionView : ICollectionView
{
    private List<object> _items;
    private int _index = -1;

    public ListCollectionView(IEnumerable source) =>
        _items = source.Cast<object>().ToList();

    public object? CurrentItem => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    public IEnumerable SourceCollection => _items;
    public Predicate<object>? Filter { get; set; }
    public bool CanFilter => true;
    public event PropertyChangedEventHandler? PropertyChanged;

    public IEnumerator GetEnumerator()
    {
        // Re-read source items if SourceCollection was an ObservableCollection that changed
        foreach (var item in _items)
            if (Filter is null || Filter(item))
                yield return item;
    }

    public void ReplaceSource(IEnumerable source)
    {
        _items = source.Cast<object>().ToList();
        _index = -1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
    }

    public bool MoveCurrentTo(object? item)
    {
        _index = item is null ? -1 : _items.IndexOf(item);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
        return _index >= 0;
    }

    public bool MoveCurrentToFirst()
    {
        _index = _items.Count > 0 ? 0 : -1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
        return _index >= 0;
    }

    public bool MoveCurrentToNext()
    {
        if (_index + 1 >= _items.Count) return false;
        _index++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));
        return true;
    }

    public void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentItem)));

    public SortDescriptionCollection SortDescriptions { get; } = new();

    public IDisposable DeferRefresh() => new DeferRefreshScope(this);

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
