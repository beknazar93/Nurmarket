using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class MonitorSettingsView : UserControl
{
    public MonitorSettingsView()
    {
        InitializeComponent();
    }

    public MonitorSettingsView(MonitorSettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    public MonitorSettingsViewModel? ViewModel => DataContext as MonitorSettingsViewModel;
    public void SaveSettings() => ViewModel?.Save();

    private void Refresh_Click(object? sender, RoutedEventArgs e) => ViewModel?.RefreshScreens();
    private void Preview_Click(object? sender, RoutedEventArgs e) => ViewModel?.Preview();
    private void OpenDisplay_Click(object? sender, RoutedEventArgs e) => ViewModel?.OpenDisplay();
    private void ShowDisplay_Click(object? sender, RoutedEventArgs e) => ViewModel?.ShowDisplay();
    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
            return;
        if (ViewModel.RequiresDisableConfirmation &&
            PosMessageBox.Show(
                "Окно покупателя сейчас открыто. Отключить и закрыть его?",
                "Монитор покупателя",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        ViewModel.Save();
    }
    private void CloseDisplay_Click(object? sender, RoutedEventArgs e) => ViewModel?.CloseDisplay();
    private void ColumnUp_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.MoveColumn(TableColumnsList.SelectedItem as MonitorColumnOption, -1);
    private void ColumnDown_Click(object? sender, RoutedEventArgs e) =>
        ViewModel?.MoveColumn(TableColumnsList.SelectedItem as MonitorColumnOption, 1);

    private async void PickAdImage_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var adMediaFilter = new FilePickerFileType("Изображение, GIF или видео")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif", "*.mp4", "*.webm"],
        };
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображение, GIF или видео для рекламы",
            AllowMultiple = false,
            FileTypeFilter = [adMediaFilter],
        });
        if (files.Count > 0)
            ViewModel.Settings.AdvertisementImagePath = files[0].TryGetLocalPath() ?? "";
    }

    private async void PickBackground_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите фон",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count > 0)
            ViewModel.Settings.BackgroundImagePath = files[0].TryGetLocalPath() ?? "";
    }
}
