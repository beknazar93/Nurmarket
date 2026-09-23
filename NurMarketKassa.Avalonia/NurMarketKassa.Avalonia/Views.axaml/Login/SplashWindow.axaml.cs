using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// Minimal credential-free window displayed while the encrypted session is
/// loaded and validated before either LoginWindow or MainWindow is created.
/// </summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        ApplyApplicationTheme();
    }

    private void ApplyApplicationTheme()
    {
        // App.RequestedThemeVariant is set from the user's application theme
        // before this window is constructed. ThemeVariant.Default delegates the
        // choice to Avalonia/the operating system.
        RequestedThemeVariant = Application.Current?.RequestedThemeVariant
            ?? ThemeVariant.Default;
    }
}
