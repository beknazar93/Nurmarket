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
                Tr.T("Окно покупателя сейчас открыто. Отключить и закрыть его?", "Сатып алуучунун терезеси азыр ачык. Аны өчүрүп, жабасызбы?", "The customer window is open now. Turn it off and close it?", "Müşteri penceresi şu anda açık. Devre dışı bırakılıp kapatılsın mı?", "Xaridor oynasi hozir ochiq. Uni o'chirib, yopasizmi?"),
                Tr.T("Монитор покупателя", "Сатып алуучунун монитору", "Customer monitor", "Müşteri monitörü", "Xaridor monitori"),
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
        var adMediaFilter = new FilePickerFileType(Tr.T("Изображение, GIF или видео", "Сүрөт, GIF же видео", "Image, GIF or video", "Görsel, GIF veya video", "Rasm, GIF yoki video"))
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif", "*.mp4", "*.webm"],
        };
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Tr.T("Выберите изображение, GIF или видео для рекламы", "Жарнама үчүн сүрөт, GIF же видео тандаңыз", "Select an image, GIF or video for the ad", "Reklam için görsel, GIF veya video seçin", "Reklama uchun rasm, GIF yoki video tanlang"),
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
            Title = Tr.T("Выберите фон", "Фонду тандаңыз", "Select a background", "Arka plan seçin", "Fonni tanlang"),
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count > 0)
            ViewModel.Settings.BackgroundImagePath = files[0].TryGetLocalPath() ?? "";
    }
}
