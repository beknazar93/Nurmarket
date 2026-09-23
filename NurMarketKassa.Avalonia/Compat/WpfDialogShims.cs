// Minimal WPF shims for ported Avalonia dialog code-behind.
// Avoid defining Avalonia control names (Button, Grid, …) to prevent ambiguity.
namespace System.Windows
{
    public class FrameworkElement : global::Avalonia.Controls.Control
    {
        public new object? Tag
        {
            get => base.Tag;
            set => base.Tag = value;
        }
        public double ActualWidth => Bounds.Width;
        public double ActualHeight => Bounds.Height;
    }

    public class UIElement : FrameworkElement
    {
        public new double Opacity
        {
            get => base.Opacity;
            set => base.Opacity = value;
        }
        public new bool IsHitTestVisible
        {
            get => base.IsHitTestVisible;
            set => base.IsHitTestVisible = value;
        }
    }

    public class ApplicationShim
    {
        public static ApplicationShim Current { get; } = new();
        public object? MainWindow { get; set; }
        public object? TryFindResource(object key) => null;
        public object? FindResource(object key) => null;
        public void Shutdown() { }
    }

    public struct Duration
    {
        public Duration(TimeSpan t) => TimeSpan = t;
        public TimeSpan TimeSpan { get; }
        public static implicit operator Duration(TimeSpan t) => new(t);
    }
}

namespace System.Windows.Media
{
    public struct Color
    {
        public byte A, R, G, B;
        public static Color FromRgb(byte r, byte g, byte b) => new() { A = 255, R = r, G = g, B = b };
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new() { A = a, R = r, G = g, B = b };
    }

    public class SolidColorBrush
    {
        public SolidColorBrush() { }
        public SolidColorBrush(Color color) => Color = color;
        public Color Color { get; set; }
        public double Opacity { get; set; } = 1;
    }

    public static class Brushes
    {
        public static SolidColorBrush Transparent { get; } = new(Color.FromArgb(0, 0, 0, 0));
        public static SolidColorBrush White { get; } = new(Color.FromRgb(255, 255, 255));
        public static SolidColorBrush Black { get; } = new(Color.FromRgb(0, 0, 0));
    }
}

namespace System.Windows.Interop
{
    public class HwndSource
    {
        public static HwndSource? FromVisual(object visual) => null;
        public IntPtr Handle => IntPtr.Zero;
    }

    public class WindowInteropHelper
    {
        public WindowInteropHelper(object window) { }
        public IntPtr Handle => IntPtr.Zero;
    }
}

namespace System.Windows.Input
{
    public class MouseButtonEventArgs : EventArgs
    {
        public MouseButton ChangedButton { get; set; }
        public MouseButtonState ButtonState { get; set; }
        public int ClickCount { get; set; }
        public bool Handled { get; set; }
        public Point GetPosition(object? relativeTo) => default;
    }

    public class MouseEventArgs : EventArgs
    {
        public bool Handled { get; set; }
        public Point GetPosition(object? relativeTo) => default;
    }

    public struct Point
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}

namespace System.Windows.Controls
{
    // Prefer Avalonia.Controls via global usings for actual controls.
    public class ControlTemplate { }
}

namespace System.Windows.Media.Animation
{
    public class DoubleAnimation { }
    public class Storyboard { public void Begin() { } }
}

namespace System.Windows.Media.Effects
{
    public class DropShadowEffect { }
}

namespace System.Windows.Threading
{
    public class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
#pragma warning disable CS0067 // Reserved for WPF-ported timer call sites
        public event EventHandler? Tick;
#pragma warning restore CS0067
        public void Start() { }
        public void Stop() { }
    }
}
