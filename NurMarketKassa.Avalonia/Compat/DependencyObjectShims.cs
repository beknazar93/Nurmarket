namespace System.Windows;

public class DependencyProperty
{
    public static DependencyProperty Register(string name, Type propertyType, Type ownerType, PropertyMetadata? typeMetadata = null)
        => new DependencyProperty();
    public static DependencyProperty RegisterAttached(string name, Type propertyType, Type ownerType, PropertyMetadata? typeMetadata = null)
        => new DependencyProperty();
}

public class PropertyMetadata
{
    public PropertyMetadata() { }
    public PropertyMetadata(object? defaultValue) { }
    public PropertyMetadata(object? defaultValue, PropertyChangedCallback? propertyChangedCallback) { }
}

public delegate void PropertyChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e);

public class DependencyObject
{
    public object? GetValue(DependencyProperty dp) => null;
    public void SetValue(DependencyProperty dp, object? value) { }
}

public class DependencyPropertyChangedEventArgs : EventArgs
{
    public object? OldValue { get; }
    public object? NewValue { get; }
}
