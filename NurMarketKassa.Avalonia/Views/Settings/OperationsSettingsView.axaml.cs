using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class OperationsSettingsView : UserControl
{
    private ObservableCollection<BankQrSetting> _bankSettings = new();
    private readonly string[] _banks = { "Элкарт", "MBank", "ФинкаБанк" };
    private readonly Dictionary<string, string> _logoMap = new()
    {
        { "Элкарт", "avares://NurMarketKassa.Avalonia/Assets/Elkart-logo.png" },
        { "MBank", "avares://NurMarketKassa.Avalonia/Assets/Mbank-logo.png" },
        { "ФинкаБанк", "avares://NurMarketKassa.Avalonia/Assets/Finca-logo.png" }
    };

    public OperationsSettingsView()
    {
        InitializeComponent();
    }

    public void LoadBankQrSettings()
    {
        _bankSettings = new ObservableCollection<BankQrSetting>();
        var prefs = UserPreferences.Instance;
        foreach (var bank in _banks)
        {
            string? qrPath = prefs.BankQrPaths?.TryGetValue(bank, out var qr) == true ? qr : null;
            _bankSettings.Add(new BankQrSetting
            {
                BankName = bank,
                LogoPath = _logoMap[bank],
                QrCodePath = qrPath
            });
        }

        BankQrItemsControl.ItemsSource = _bankSettings;
    }

    private async void LoadQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BankQrSetting setting)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Выберите QR-код для банка {setting.BankName}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Изображения") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
            return;

        if (topLevel is Window owner)
            await OpenQrEditorAsync(owner, setting, path);
    }

    private async void EditQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting }
            || string.IsNullOrWhiteSpace(setting.QrCodePath)
            || !File.Exists(setting.QrCodePath)
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        await OpenQrEditorAsync(owner, setting, setting.QrCodePath);
    }

    private void RemoveQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting })
            return;

        if (PosMessageBox.Show(
                $"Убрать QR-код банка {setting.BankName}?",
                "Удаление QR-кода",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        string? previousPath = setting.QrCodePath;
        setting.QrCodePath = null;
        SaveBankQrSettings();
        DeleteManagedQrFile(previousPath);
    }

    private async Task OpenQrEditorAsync(Window owner, BankQrSetting setting, string sourcePath)
    {
        string? previousPath = setting.QrCodePath;
        var dialog = new QrCropDialog(sourcePath, setting.BankName);
        string? editedPath = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(editedPath))
            return;

        setting.QrCodePath = editedPath;
        SaveBankQrSettings();

        if (!string.Equals(previousPath, editedPath, StringComparison.OrdinalIgnoreCase))
            DeleteManagedQrFile(previousPath);
    }

    private static void DeleteManagedQrFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            string managedDirectory = Path.GetFullPath(QrCropDialog.GetManagedQrDirectory())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            if (candidate.StartsWith(managedDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                File.Delete(candidate);
        }
        catch
        {
            // The preference is already removed; failure to clean an old managed copy is harmless.
        }
    }

    private void SaveBankQrSettings()
    {
        var prefs = UserPreferences.Instance;
        prefs.BankQrPaths ??= new Dictionary<string, string>();
        prefs.BankQrPaths.Clear();
        foreach (var bs in _bankSettings)
        {
            if (!string.IsNullOrEmpty(bs.QrCodePath))
                prefs.BankQrPaths[bs.BankName] = bs.QrCodePath;
        }

        prefs.SaveToDisk();
    }
}
