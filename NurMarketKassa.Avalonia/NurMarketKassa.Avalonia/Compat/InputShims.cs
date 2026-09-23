using Avalonia.VisualTree;

namespace System.Windows.Input;
public class TextCompositionEventArgs : EventArgs
{
    public string Text { get; }
    public bool Handled { get; set; }
    public TextCompositionEventArgs(string text) => Text = text;
}
public enum MouseButton { Left, Right, Middle }
public enum MouseButtonState { Released, Pressed }
public enum ModifierKeys { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }
public enum Key { Capital = 20 }
[Flags]
public enum KeyStates { None = 0, Down = 1, Toggled = 2 }

public static class Keyboard
{
    public static KeyStates GetKeyStates(Key key) => KeyStates.None;

    public static ModifierKeys Modifiers => ModifierKeys.None;
}

public sealed class InputLanguageManager
{
    public static InputLanguageManager Current { get; } = new();
    public System.Collections.Generic.IEnumerable<System.Globalization.CultureInfo> AvailableInputLanguages =>
        new[] { System.Globalization.CultureInfo.CurrentCulture };
    public System.Globalization.CultureInfo CurrentInputLanguage { get; set; } = System.Globalization.CultureInfo.CurrentCulture;
}

public struct Rect
{
    public Rect(double x, double y, double width, double height)
    {
        Left = x; Top = y; Width = width; Height = height;
        Right = x + width; Bottom = y + height;
    }
    public double Left, Top, Width, Height, Bottom, Right;
    public double X => Left;
    public double Y => Top;
}

public static class SystemParameters
{
    public static Rect WorkArea
    {
        get
        {
            var screen = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Screens?.Primary;
            if (screen is null) return new Rect { Left = 0, Top = 0, Width = 1920, Height = 1080, Bottom = 1080, Right = 1920 };
            var wa = screen.WorkingArea;
            return new Rect { Left = wa.X, Top = wa.Y, Width = wa.Width, Height = wa.Height, Bottom = wa.Y + wa.Height, Right = wa.X + wa.Width };
        }
    }
    public const double VerticalScrollBarWidth = 16;
}

public static class VisualTreeHelper
{
    public static System.Windows.DependencyObject? GetParent(System.Windows.DependencyObject child) => null;
}

public class GridViewColumnHeader : global::Avalonia.Controls.Control { public object? Content { get; set; } }
