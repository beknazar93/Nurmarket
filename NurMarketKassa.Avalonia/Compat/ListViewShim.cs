namespace Avalonia.Controls;
// WPF ListView often used as ItemsControl; map to ListBox for Avalonia builds.
public class ListView : ListBox
{
    public object? View { get; set; }
    public double ActualWidth => Bounds.Width;
}

public class GridView
{
    public GridViewColumnCollection Columns { get; } = new();
}

public class GridViewColumnCollection : List<GridViewColumn> { }

public class GridViewColumn
{
    public double Width { get; set; }
}
