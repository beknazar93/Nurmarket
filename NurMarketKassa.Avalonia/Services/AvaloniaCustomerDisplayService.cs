using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Customer;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

public sealed record DisplayScreenOption(
    string Id,
    string DisplayName,
    PixelRect Bounds,
    PixelRect WorkingArea,
    bool IsPrimary)
{
    public string Label =>
        $"{DisplayName} — {Bounds.Width}×{Bounds.Height}{(IsPrimary ? " — основной" : "")}";
}

public sealed record CustomerDisplayOpenResult(bool IsSuccess, string Message);

/// <summary>Owns the single, non-modal customer display window.</summary>
public sealed class AvaloniaCustomerDisplayService : ICustomerDisplayService, IDisposable
{
    private readonly CustomerDisplayStateService _state;
    private readonly CustomerDisplayViewModel _viewModel;
    private CustomerDisplayWindow? _window;
    private bool _closedManually;
    private bool _closingInternally;
    private bool _requiresSecondaryScreen;

    public AvaloniaCustomerDisplayService(
        CustomerDisplayStateService state,
        CustomerDisplayViewModel viewModel)
    {
        _state = state;
        _viewModel = viewModel;
        _viewModel.CloseRequested = () => _ = CloseAsync(true);
    }

    public bool IsOpen => _window?.IsVisible == true;
    public bool IsDisplayVisible => IsOpen;

    /// <summary>Automatic open. A manual close suppresses it for the current session.</summary>
    public void Show()
    {
        if (_closedManually)
            return;
        if (!UserPreferences.Instance.CustomerDisplay.IsEnabled)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (TryGetMainWindow() is { } owner)
                OpenForSession(owner);
        });
    }

    public void Hide()
    {
        _state.Hide();
        Dispatcher.UIThread.Post(() =>
        {
            CaptureWindowBounds();
            _window?.Hide();
        });
    }

    public void UpdateCart(CustomerDisplayCartSnapshot snapshot) => _state.UpdateCart(snapshot);

    public void SetPaymentStatus(CustomerDisplayPaymentStatus status, string? message = null) =>
        _state.SetPaymentStatus(status, message);

    public void SetSelectedBankQrPath(string? qrPath) => _state.SetSelectedBankQrPath(qrPath);

    public Task OpenAsync()
    {
        OpenManually();
        return Task.CompletedTask;
    }

    public Task CloseAsync() => CloseAsync(true);

    public Task ApplySettingsAsync()
    {
        ApplySettings(UserPreferences.Instance.CustomerDisplay);
        return Task.CompletedTask;
    }

    public Task MoveToConfiguredScreenAsync()
    {
        if (_window is not null)
            ApplyWindowPlacement(
                _window,
                UserPreferences.Instance.CustomerDisplay,
                _requiresSecondaryScreen);
        return Task.CompletedTask;
    }

    public CustomerDisplayOpenResult OpenForSession(Window owner) =>
        OpenOnSecondaryScreen(owner, respectEnabledSetting: true);

    public CustomerDisplayOpenResult TestSecondaryScreen(Window owner) =>
        OpenOnSecondaryScreen(owner, respectEnabledSetting: false);

    private CustomerDisplayOpenResult OpenOnSecondaryScreen(
        Window owner,
        bool respectEnabledSetting)
    {
        var preferences = UserPreferences.Instance;
        if (respectEnabledSetting && !preferences.CustomerDisplay.IsEnabled)
            return new CustomerDisplayOpenResult(false, "Экран покупателя отключён в настройках.");

        var secondaryScreens = GetScreens(owner).Where(screen => !screen.IsPrimary).ToList();
        if (secondaryScreens.Count == 0)
        {
            _state.Hide();
            if (_window is { IsVisible: true } && _requiresSecondaryScreen)
                _window.Hide();
            return new CustomerDisplayOpenResult(
                false,
                "Второй монитор не найден. Экран покупателя не открыт поверх кассы.");
        }

        var settings = preferences.CustomerDisplay.Clone();
        var target = secondaryScreens.FirstOrDefault(screen => screen.Id == settings.SelectedScreenId)
                     ?? secondaryScreens[0];
        settings.SelectedScreenId = target.Id;

        if (!string.Equals(
                preferences.CustomerDisplay.SelectedScreenId,
                target.Id,
                StringComparison.Ordinal))
        {
            preferences.CustomerDisplay.SelectedScreenId = target.Id;
            preferences.SaveToDisk();
        }

        _closedManually = false;
        _state.Show();
        EnsureWindowVisible(false, settings, requireSecondaryScreen: true);
        return IsOpen
            ? new CustomerDisplayOpenResult(
                true,
                $"Экран покупателя открыт на «{target.DisplayName}» ({target.Bounds.Width}×{target.Bounds.Height}).")
            : new CustomerDisplayOpenResult(false, "Не удалось открыть экран покупателя.");
    }

    public void CloseForSession() => _ = CloseAsync(false);

    public void OpenManually()
    {
        _closedManually = false;
        _state.Show();
        Dispatcher.UIThread.Post(() => EnsureWindowVisible(false, requireSecondaryScreen: false));
    }

    public void ShowManually()
    {
        _closedManually = false;
        _state.Show();
        Dispatcher.UIThread.Post(() =>
        {
            EnsureWindowVisible(false, requireSecondaryScreen: false);
            if (_window is null)
                return;
            ApplyWindowPlacement(_window, UserPreferences.Instance.CustomerDisplay, false);
            _window.Activate();
        });
    }

    public Task CloseAsync(bool closedManually)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => _ = CloseAsync(closedManually));
            return Task.CompletedTask;
        }

        _closedManually = closedManually;
        _state.Hide();
        CaptureWindowBounds();
        var window = _window;
        if (window is null)
            return Task.CompletedTask;

        _closingInternally = true;
        _window = null;
        window.Close();
        _closingInternally = false;
        return Task.CompletedTask;
    }

    public IReadOnlyList<DisplayScreenOption> GetScreens(Window owner)
    {
        var screens = owner.Screens?.All;
        if (screens is null)
            return [];

        var result = new List<DisplayScreenOption>(screens.Count);
        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            var name = string.IsNullOrWhiteSpace(screen.DisplayName) ? $"Монитор {i + 1}" : screen.DisplayName!;
            result.Add(new DisplayScreenOption(
                BuildScreenId(screen), name, screen.Bounds, screen.WorkingArea, screen.IsPrimary));
        }
        return result;
    }

    public string Preview(CustomerDisplaySettings settings)
    {
        _closedManually = false;
        settings.Normalize();
        EnsureWindowVisible(true, settings, requireSecondaryScreen: false);
        _window?.Activate();
        if (_window is null)
            return "Не удалось открыть экран покупателя.";
        var target = ResolveTarget(_window, settings, out _, out var warning);
        return target.Label + (string.IsNullOrEmpty(warning) ? "" : $". {warning}");
    }

    public string ApplySettings(CustomerDisplaySettings settings)
    {
        settings.Normalize();
        UserPreferences.Instance.CustomerDisplay = settings.Clone();
        UserPreferences.Instance.SaveToDisk();

        if (!settings.IsEnabled)
        {
            if (_state.CurrentStatus == CustomerDisplayPaymentStatus.Processing)
                return "Настройки сохранены. Монитор закроется после завершения оплаты.";
            _ = CloseAsync(false);
            return "Монитор покупателя отключён.";
        }

        if (_window is { IsVisible: true })
        {
            _window.SetPreviewMode(false);
            _window.ApplySettings(settings);
            ApplyWindowPlacement(_window, settings, _requiresSecondaryScreen);
        }
        return "Настройки применены.";
    }

    public void CloseDisplay() => _ = CloseAsync(true);

    private void CaptureWindowBounds()
    {
        if (_window is null ||
            UserPreferences.Instance.CustomerDisplay.WindowMode != CustomerDisplayWindowMode.Windowed)
            return;
        var settings = UserPreferences.Instance.CustomerDisplay;
        settings.WindowX = _window.Position.X;
        settings.WindowY = _window.Position.Y;
        settings.WindowWidth = _window.Width;
        settings.WindowHeight = _window.Height;
        settings.Normalize();
        UserPreferences.Instance.SaveToDisk();
    }

    private void EnsureWindowVisible(
        bool preview,
        CustomerDisplaySettings? suppliedSettings = null,
        bool requireSecondaryScreen = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
                EnsureWindowVisible(preview, suppliedSettings, requireSecondaryScreen));
            return;
        }

        try
        {
            _requiresSecondaryScreen = requireSecondaryScreen;
            var settings = (suppliedSettings ?? UserPreferences.Instance.CustomerDisplay).Clone();
            settings.Normalize();
            if (_window is null)
            {
                var window = new CustomerDisplayWindow(_viewModel);
                _window = window;
                window.Screens.Changed += OnScreensChanged;
                window.Closed += (_, _) =>
                {
                    window.Screens.Changed -= OnScreensChanged;
                    if (ReferenceEquals(_window, window))
                        _window = null;
                    if (!_closingInternally)
                        _closedManually = true;
                };
            }

            _window.SetPreviewMode(preview);
            _window.ApplySettings(settings);
            ApplyWindowPlacement(_window, settings, requireSecondaryScreen);
            if (!_window.IsVisible)
                _window.Show();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CustomerDisplay EnsureWindowVisible failed: {ex}", "CUSTOMER_DISPLAY");
        }
    }

    private static void ApplyWindowPlacement(
        Window window,
        CustomerDisplaySettings settings,
        bool requireSecondaryScreen)
    {
        var target = ResolveTarget(
            window,
            settings,
            out var screen,
            out _,
            requireSecondaryScreen);
        settings.SelectedScreenId = target.Id;
        var safePrimaryFallback = target.IsPrimary &&
                                  settings.WindowMode != CustomerDisplayWindowMode.Windowed;
        var mode = safePrimaryFallback
            ? CustomerDisplayWindowMode.Windowed
            : settings.WindowMode;
        var area = mode == CustomerDisplayWindowMode.FullScreen ? screen.Bounds : screen.WorkingArea;

        window.ShowInTaskbar = !requireSecondaryScreen;
        window.Topmost = settings.KeepCustomerDisplayOnTop && !target.IsPrimary;
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = area.TopLeft;

        if (mode == CustomerDisplayWindowMode.Windowed)
        {
            window.SystemDecorations = SystemDecorations.Full;
            window.CanResize = true;
            var maxWidth = area.Width / screen.Scaling;
            var maxHeight = area.Height / screen.Scaling;
            var width = Math.Clamp(settings.WindowWidth ?? 1000, Math.Min(640, maxWidth), maxWidth);
            var height = Math.Clamp(settings.WindowHeight ?? 700, Math.Min(480, maxHeight), maxHeight);
            window.Width = width;
            window.Height = height;
            window.Position = !safePrimaryFallback && settings.WindowX.HasValue && settings.WindowY.HasValue
                ? new PixelPoint(settings.WindowX.Value, settings.WindowY.Value)
                : new PixelPoint(
                    area.X + Math.Max(0, (area.Width - (int)(width * screen.Scaling)) / 2),
                    area.Y + Math.Max(0, (area.Height - (int)(height * screen.Scaling)) / 2));
            return;
        }

        // Borderless normal window: avoids exclusive/kiosk-like fullscreen behavior.
        window.SystemDecorations = SystemDecorations.None;
        window.CanResize = false;
        window.Width = area.Width / screen.Scaling;
        window.Height = area.Height / screen.Scaling;
    }

    private static DisplayScreenOption ResolveTarget(
        Window window,
        CustomerDisplaySettings settings,
        out Screen screen,
        out string warning,
        bool requireSecondaryScreen = false)
    {
        var screens = window.Screens?.All ??
                      throw new InvalidOperationException("Список мониторов недоступен.");
        var options = new List<(DisplayScreenOption option, Screen screen)>(screens.Count);
        for (var i = 0; i < screens.Count; i++)
        {
            var item = screens[i];
            var name = string.IsNullOrWhiteSpace(item.DisplayName) ? $"Монитор {i + 1}" : item.DisplayName!;
            options.Add((new DisplayScreenOption(
                BuildScreenId(item), name, item.Bounds, item.WorkingArea, item.IsPrimary), item));
        }

        var selected = options.FirstOrDefault(x =>
            x.option.Id == settings.SelectedScreenId
            && (!requireSecondaryScreen || !x.option.IsPrimary));
        warning = "";
        if (selected.option is null)
        {
            selected = options.FirstOrDefault(x => !x.option.IsPrimary);
            if (requireSecondaryScreen && selected.option is null)
                throw new InvalidOperationException(
                    "Второй монитор отключён. Экран покупателя закрыт, чтобы не перекрывать кассу.");
            if (selected.option is null)
                selected = options.First();
            if (!string.IsNullOrWhiteSpace(settings.SelectedScreenId))
                warning = "Выбранный ранее экран не найден; использован доступный экран";
        }

        if (selected.option.IsPrimary && !options.Any(x => !x.option.IsPrimary))
            warning = "Внешний монитор не найден. Экран покупателя открыт в безопасном оконном режиме";

        screen = selected.screen;
        return selected.option;
    }

    private static string BuildScreenId(Screen screen)
    {
        var name = string.IsNullOrWhiteSpace(screen.DisplayName) ? "unnamed" : screen.DisplayName!.Trim();
        var bounds = screen.Bounds;
        return $"display:{name}:{bounds.X}:{bounds.Y}:{bounds.Width}x{bounds.Height}";
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        if (_window is not { IsVisible: true })
            return;
        try
        {
            ApplyWindowPlacement(
                _window,
                UserPreferences.Instance.CustomerDisplay,
                _requiresSecondaryScreen);
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Customer display screen change fallback failed: {ex}", "CUSTOMER_DISPLAY");
            _ = CloseAsync(false);
        }
    }

    public void Dispose()
    {
        _viewModel.CloseRequested = null;
        _ = CloseAsync(false);
    }

    private static Window? TryGetMainWindow() =>
        (Application.Current?.ApplicationLifetime as
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
        ?.MainWindow;
}
