using System.Globalization;
using System.Windows.Input;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.ViewModels.Settings;

/// <summary>
/// ViewModel окна настроек: кастомизация фона и общие параметры UI.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsImagePicker _imagePicker;
    private string _backgroundImagePath = "";
    private double _backgroundOpacity = 0.15;
    private double _backgroundBlurPercent = 40;
    private bool _applyToCashierScreen;

    public SettingsViewModel(ISettingsImagePicker imagePicker)
    {
        _imagePicker = imagePicker;
        LoadFromPreferences();

        PickBackgroundCommand = new AsyncRelayCommand(PickBackgroundAsync);
        ClearBackgroundCommand = new RelayCommand(ClearBackground, () => HasBackgroundImage);
        SaveCustomizationCommand = new RelayCommand(SaveCustomization);
    }

    public string BackgroundImagePath
    {
        get => _backgroundImagePath;
        private set
        {
            if (!SetProperty(ref _backgroundImagePath, value ?? ""))
                return;
            OnPropertyChanged(nameof(BackgroundImage));
            OnPropertyChanged(nameof(HasBackgroundImage));
            (ClearBackgroundCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Путь для привязки Image через AssetPathToBitmapConverter.</summary>
    public string? BackgroundImage =>
        string.IsNullOrWhiteSpace(BackgroundImagePath) ? null : BackgroundImagePath;

    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(BackgroundImagePath);

    /// <summary>
    /// Плотность белой подложки поверх обоев (0.05–0.8).
    /// Не влияет на Opacity фонового Image — только на альфа-канал подложки.
    /// </summary>
    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0.05, 0.8);
            if (!SetProperty(ref _backgroundOpacity, clamped))
                return;
            OnPropertyChanged(nameof(BackgroundOpacityPercent));
            UserPreferences.Instance.BackgroundOpacity = clamped;
            UserPreferences.Instance.SaveToDisk();
        }
    }

    public string BackgroundOpacityPercent =>
        $"{Math.Round(BackgroundOpacity * 100).ToString(CultureInfo.InvariantCulture)}%";

    /// <summary>Размытие фоновых обоев (0–100%) — независимо от плотности стекла выше.</summary>
    public double BackgroundBlurPercent
    {
        get => _backgroundBlurPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _backgroundBlurPercent, clamped))
                return;
            OnPropertyChanged(nameof(BackgroundBlurPercentText));
            UserPreferences.Instance.BackgroundBlurPercent = clamped;
            UserPreferences.Instance.SaveToDisk();
        }
    }

    public string BackgroundBlurPercentText => $"{Math.Round(BackgroundBlurPercent)}%";

    /// <summary>Показывать обои на основном экране кассира (MainWindow), а не только здесь,
    /// в Настройках — живое применение сразу же (MainWindowHostBridge), см. code-behind.</summary>
    public bool ApplyToCashierScreen
    {
        get => _applyToCashierScreen;
        set
        {
            if (!SetProperty(ref _applyToCashierScreen, value))
                return;
            UserPreferences.Instance.ApplyBackgroundToCashierScreen = value;
            UserPreferences.Instance.SaveToDisk();
        }
    }

    public ICommand PickBackgroundCommand { get; }
    public ICommand ClearBackgroundCommand { get; }
    public ICommand SaveCustomizationCommand { get; }

    public void LoadFromPreferences()
    {
        var prefs = UserPreferences.Instance;
        _backgroundImagePath = prefs.BackgroundImagePath ?? "";
        _backgroundOpacity = Math.Clamp(prefs.BackgroundOpacity > 0 ? prefs.BackgroundOpacity : 0.15, 0.05, 0.8);
        _backgroundBlurPercent = Math.Clamp(prefs.BackgroundBlurPercent, 0, 100);
        _applyToCashierScreen = prefs.ApplyBackgroundToCashierScreen;
        OnPropertyChanged(nameof(BackgroundImagePath));
        OnPropertyChanged(nameof(BackgroundImage));
        OnPropertyChanged(nameof(BackgroundOpacity));
        OnPropertyChanged(nameof(BackgroundOpacityPercent));
        OnPropertyChanged(nameof(BackgroundBlurPercent));
        OnPropertyChanged(nameof(BackgroundBlurPercentText));
        OnPropertyChanged(nameof(ApplyToCashierScreen));
        OnPropertyChanged(nameof(HasBackgroundImage));
    }

    private async Task PickBackgroundAsync()
    {
        var path = await _imagePicker.PickBackgroundImageAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        BackgroundImagePath = path;
        UserPreferences.Instance.BackgroundImagePath = path;
        UserPreferences.Instance.SaveToDisk();
    }

    private void ClearBackground()
    {
        BackgroundImagePath = "";
        UserPreferences.Instance.BackgroundImagePath = "";
        UserPreferences.Instance.SaveToDisk();
    }

    private void SaveCustomization()
    {
        var prefs = UserPreferences.Instance;
        prefs.BackgroundImagePath = BackgroundImagePath;
        prefs.BackgroundOpacity = BackgroundOpacity;
        prefs.BackgroundBlurPercent = BackgroundBlurPercent;
        prefs.ApplyBackgroundToCashierScreen = ApplyToCashierScreen;
        prefs.SaveToDisk();
    }
}
