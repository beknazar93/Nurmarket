using System.Windows.Input;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>Верхняя панель: меню, пользователь, действия смены и настроек.</summary>
public sealed class MainToolbarViewModel : ViewModelBase
{
    private readonly IAppSession _session;
    private readonly Action? _openUpdate;
    private bool _hasUpdateAvailable;
    private string _updateNoticeText = "";
    private string _userTitle = "Касса";
    private string _themeGlyph = "\uE706";
    private string _themeTooltip = "Светлая тема";

    private readonly Func<Task>? _openShiftHandler;
    private readonly Func<Task>? _closeShiftHandler;

    public MainToolbarViewModel(
        IAppSession session,
        MainStatusViewModel status,
        Action toggleSideMenu,
        Action? checkCustomerDisplay = null,
        Action? toggleTheme = null,
        Action? toggleKeyboard = null,
        Func<Task>? openShiftHandler = null,
        Func<Task>? closeShiftHandler = null,
        Action? openUpdate = null)
    {
        _session = session;
        Status = status;
        _openShiftHandler = openShiftHandler;
        _closeShiftHandler = closeShiftHandler;
        _openUpdate = openUpdate;

        ToggleSideMenuCommand = new RelayCommand(toggleSideMenu);
        CheckCustomerDisplayCommand = new RelayCommand(() => checkCustomerDisplay?.Invoke());
        ToggleThemeCommand = new RelayCommand(() => toggleTheme?.Invoke());
        ToggleKeyboardCommand = new RelayCommand(() => toggleKeyboard?.Invoke());
        OpenShiftCommand = new AsyncRelayCommand(OpenShiftAsync, () => CanOpenShift);
        CloseShiftCommand = new AsyncRelayCommand(CloseShiftAsync, () => CanCloseShift);
        OpenUpdateCommand = new RelayCommand(() => _openUpdate?.Invoke());

        RefreshUserTitle();
    }

    public MainStatusViewModel Status { get; }

    public string UserTitle
    {
        get => _userTitle;
        private set => SetProperty(ref _userTitle, value);
    }

    /// <summary>Segoe MDL2 glyph: sun (light) when dark is active, moon when light is active.</summary>
    public string ThemeGlyph
    {
        get => _themeGlyph;
        private set => SetProperty(ref _themeGlyph, value);
    }

    public string ThemeTooltip
    {
        get => _themeTooltip;
        private set => SetProperty(ref _themeTooltip, value);
    }

    /// <summary>Updates the toggle icon: show a sun in dark mode (switch to light) and a moon in light mode.</summary>
    public void UpdateThemeGlyph(bool isDark)
    {
        ThemeGlyph = isDark ? "\uE706" : "\uE708";
        ThemeTooltip = isDark ? "Светлая тема" : "Тёмная тема";
    }

    public bool CanOpenShift => !_session.IsShiftOpen;
    public bool CanCloseShift => _session.IsShiftOpen;

    public bool HasUpdateAvailable
    {
        get => _hasUpdateAvailable;
        private set => SetProperty(ref _hasUpdateAvailable, value);
    }

    public string UpdateNoticeText
    {
        get => _updateNoticeText;
        private set => SetProperty(ref _updateNoticeText, value);
    }

    public ICommand ToggleSideMenuCommand { get; }
    public ICommand CheckCustomerDisplayCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ToggleKeyboardCommand { get; }
    public ICommand OpenShiftCommand { get; }
    public ICommand CloseShiftCommand { get; }
    public ICommand OpenUpdateCommand { get; }

    /// <summary>Показывает ненавязчивый значок в шапке кассы — клик ведёт в Настройки →
    /// Обновления (см. OpenUpdateCommand), а не качает файл напрямую из системного браузера
    /// (2026-09-06, по просьбе пользователя — раньше клик открывал ссылку на сборку с GitHub
    /// прямо в браузере в обход уже существующего экрана обновлений).</summary>
    public void SetUpdateAvailable(string version)
    {
        UpdateNoticeText = $"Доступна версия {version}";
        HasUpdateAvailable = true;
    }

    public void RefreshUserTitle()
    {
        UserTitle = string.IsNullOrWhiteSpace(_session.PosCashboxDisplayName)
            ? "Касса — Nur Market"
            : _session.PosCashboxDisplayName!;
    }

    public void NotifyShiftStateChanged()
    {
        OnPropertyChanged(nameof(CanOpenShift));
        OnPropertyChanged(nameof(CanCloseShift));
        (OpenShiftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CloseShiftCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private Task OpenShiftAsync() =>
        _openShiftHandler?.Invoke() ?? Task.CompletedTask;

    private Task CloseShiftAsync() =>
        _closeShiftHandler?.Invoke() ?? Task.CompletedTask;
}
