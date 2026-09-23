using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class ServicesWindow : Window
{
    public ServicesWindow()
    {
        InitializeComponent();
        if (UserPreferences.Instance.Fullscreen)
        {
            SystemDecorations = SystemDecorations.None;
            WindowState = WindowState.Maximized;
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
