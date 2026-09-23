using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.ViewModels.Settings;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.SaveCustomizationCommand.CanExecute(null))
            vm.SaveCustomizationCommand.Execute(null);

        // Живое применение обоев на основной кассе сразу, без перезапуска — та же схема,
        // что и у AccentThemeService.Apply для тем (см. MainWindow.RefreshBackgroundWallpaper).
        App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshBackgroundWallpaper();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        (this.VisualRoot as Window)?.Close();
    }
}
