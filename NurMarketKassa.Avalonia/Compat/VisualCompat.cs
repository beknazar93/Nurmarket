using Avalonia.Controls;
using System.Windows;

namespace NurMarketKassa.AvaloniaHost.Compat;

/// <summary>Helpers bridging WPF Visibility / DialogResult patterns to Avalonia.</summary>
public static class VisualCompat
{
    public static void SetVisibility(this Control? visual, Visibility visibility)
    {
        if (visual is not null)
            visual.IsVisible = visibility == Visibility.Visible;
    }

    public static Visibility GetVisibility(this Control? visual)
    {
        if (visual is not null)
            return visual.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
}
