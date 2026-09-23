using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IAppSession _session;
    private readonly MainWindowHostBridge _hostBridge;
    private readonly ICashShiftService _cashShiftService;
    private readonly IShiftStateService _shiftStateService;
    private readonly IUserPrompts _prompts;
    private readonly IPermissionService _permissions;
    private readonly AvaloniaCustomerDisplayService _customerDisplay;
    private readonly PosHotkeyService _hotkeys = new();
    private readonly IBarcodeInputService _barcodeInputService;
    private readonly CancellationTokenSource _windowCts = new();
    private readonly ApplicationStateService _applicationStateService = new();

    private bool _appInitialized;
    private bool _allowMainWindowClose;
    private bool _closeFlowActive;
    private decimal? _shiftCashBalance;
    private double _lastLoggedCatalogWidth = -1;
    private double _lastLoggedCartWidth = -1;

    private ColumnDefinition CatalogColumn => MainContentGrid.ColumnDefinitions[0];
    private ColumnDefinition CartColumn => MainContentGrid.ColumnDefinitions[2];
    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public MainWindow() : this(
        ResolveService<MainWindowViewModel>(),
        ResolveService<IAppSession>(),
        ResolveService<MainWindowHostBridge>(),
        ResolveService<ICashShiftService>(),
        ResolveService<IShiftStateService>(),
        ResolveService<IUserPrompts>(),
        ResolveService<AvaloniaCustomerDisplayService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public MainWindow(
        MainWindowViewModel viewModel,
        IAppSession session,
        MainWindowHostBridge hostBridge,
        ICashShiftService cashShiftService,
        IShiftStateService shiftStateService,
        IUserPrompts prompts,
        AvaloniaCustomerDisplayService customerDisplay)
    {
        _viewModel = viewModel;
        _session = session;
        _hostBridge = hostBridge;
        _cashShiftService = cashShiftService;
        _shiftStateService = shiftStateService;
        _prompts = prompts;
        _permissions = ResolveService<IPermissionService>();
        _customerDisplay = customerDisplay;
        _barcodeInputService = ResolveService<IBarcodeInputService>();
        _hostBridge.Window = this;
        WireDialogBridge();

        InitializeComponent();
        DataContext = _viewModel;
        CatalogGridSplitter.PointerReleased += OnCatalogGridSplitterPointerReleased;
        CatalogColumn.PropertyChanged += OnCatalogColumnPropertyChanged;
        CartColumn.PropertyChanged += OnCartColumnPropertyChanged;
        RestoreApplicationState();
        _viewModel.Catalog.StateChanged += OnViewModelStateChanged;
        _viewModel.Basket.StateChanged += OnViewModelStateChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        Screens.Changed += OnCashierScreensChanged;
        _barcodeInputService.BarcodeScanned += OnBarcodeScanned;
        AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        ApplyFullscreenPreference();
        _viewModel.Toolbar.UpdateThemeGlyph(UserPreferences.Instance.DarkTheme);
    }

    public async Task InitializeApplicationAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_appInitialized)
            return;

        progress?.Report("Загрузка кассы...");

        if (AccountCatalogIsolation.RequireForcedCatalogSync)
            _viewModel.Catalog.StatusText = "Требуется синхронизация каталога для нового пользователя.";

        progress?.Report("Загрузка профиля...");
        try
        {
            await CompanyInfoService.RefreshAsync(App.AuthApi, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Company profile unavailable during offline bootstrap: {ex}", "WARNING");
        }

        progress?.Report("Обновление смены...");
        await RefreshShiftStateAsync(cancellationToken).ConfigureAwait(true);

        progress?.Report("Подготовка рабочего места...");
        await _viewModel.InitializeAsync(cancellationToken).ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(_session.OfflineBootstrapMessage))
            _viewModel.Catalog.StatusText = _session.OfflineBootstrapMessage!;

        if (AccountCatalogIsolation.RequireForcedCatalogSync)
            AccountCatalogIsolation.ClearForcedCatalogSyncFlag();

        _appInitialized = true;
    }

    // ----------------------------------------------------------------
    //  Обработчики событий шапки и панели чека (MainWindow.axaml)
    // ----------------------------------------------------------------

    /// <summary>Переключение темы Light/Dark (иконка луны/солнца в шапке).</summary>
    private void ToggleTheme_Click(object? sender, RoutedEventArgs e) => ToggleTheme();

    /// <summary>Создание нового чека (кнопка «+» рядом с вкладками чеков).</summary>
    private void NewReceipt_Click(object? sender, RoutedEventArgs e) =>
        _viewModel.Basket.CreateNewReceipt();

    /// <summary>Открытие смены (кнопка «🔓 Открыть смену» в шапке).</summary>
    private async void OpenShift_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await OpenShiftAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Open shift canceled.", "DEBUG");
        }
    }

    /// <summary>Закрытие смены (кнопка «🔒 Закрыть смену» в шапке).</summary>
    private async void CloseShift_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await CloseShiftAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Close shift canceled.", "DEBUG");
        }
    }

    internal void ToggleTheme()
    {
        var app = Application.Current;
        if (app is null)
            return;

        var isDarkNow = app.ActualThemeVariant == ThemeVariant.Dark;
        var newIsDark = !isDarkNow;

        app.RequestedThemeVariant = newIsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        var prefs = UserPreferences.Instance;
        prefs.DarkTheme = newIsDark;
        prefs.SaveToDisk();
        _viewModel.Toolbar.UpdateThemeGlyph(newIsDark);
    }

    internal void ToggleKeyboard()
    {
        try
        {
            App.GetRequiredService<IOperatingSystemKeyboardService>().ShowSystemKeyboard();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OSK failed, fallback to FrmKeyboard: {ex.Message}", "UI");
            if (FrmKeyboard.CurrentForm is FrmKeyboard existing && existing.IsVisible)
                FrmKeyboard.KillKeyboard();
            else
                FrmKeyboard.ShowKeyboard(this);
        }
    }

    internal void CheckCustomerDisplay()
    {
        var result = _customerDisplay.TestSecondaryScreen(this);
        if (result.IsSuccess)
            _prompts.ShowToast(result.Message);
        else
            _prompts.ShowWarning(result.Message);

        Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
    }

    private async void OpenHotkeySettings_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new HotkeySettingsWindow(_hotkeys);
        await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        RestoreScannerFocus();
    }

    private void OnMainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_hotkeys.TryMatch(e, out var action))
        {
            e.Handled = true;
            ExecuteHotkey(action);
            return;
        }

        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        _barcodeInputService.ProcessKeyDown(e);
    }

    private void ExecuteHotkey(PosHotkeyAction action)
    {
        switch (action)
        {
            case PosHotkeyAction.Checkout:
                ExecuteCommand(_viewModel.Basket.PayCommand);
                break;
            case PosHotkeyAction.ClearCart:
                ExecuteCommand(_viewModel.Basket.ClearCartCommand);
                break;
            case PosHotkeyAction.ToggleCustomerDisplay:
                ToggleCustomerDisplay();
                break;
            case PosHotkeyAction.ApplyDiscount:
                ExecuteCommand(_viewModel.Basket.ApplyOrderDiscountCommand);
                break;
            case PosHotkeyAction.FocusProductSearch:
                CatalogPanel.FocusProductSearch();
                break;
        }
    }

    private void ToggleCustomerDisplay()
    {
        if (_customerDisplay.IsOpen)
        {
            _ = _customerDisplay.CloseAsync(true);
            _prompts.ShowToast("Экран покупателя скрыт.");
            RestoreScannerFocus();
            return;
        }

        var result = _customerDisplay.TestSecondaryScreen(this);
        if (result.IsSuccess)
            _prompts.ShowToast(result.Message);
        else
            _prompts.ShowWarning(result.Message);
        RestoreScannerFocus();
    }

    private static void ExecuteCommand(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Never move focus while an interactive control is processing a press.
        // Doing it between PointerPressed and PointerReleased cancels Click on
        // touchscreens and on some mouse drivers.
        if (IsInteractivePointerTarget(e.Source))
            return;

        Dispatcher.UIThread.Post(RestoreScannerFocus, DispatcherPriority.Background);
    }

    private static bool IsInteractivePointerTarget(object? source)
    {
        for (var control = source as Control; control is not null; control = control.Parent as Control)
        {
            if (control is Button or TextBox or ComboBox or ListBox or DataGrid or
                Slider or ScrollBar or ScrollViewer or GridSplitter or NumericUpDown or
                DatePicker or Calendar or MenuItem or TabItem or ToggleSwitch or
                CheckBox or RadioButton)
                return true;
        }
        return false;
    }

    private void RestoreScannerFocus()
    {
        FocusManager?.ClearFocus();
        Focus();
    }

    private void OnBarcodeScanned(string barcode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _viewModel.Basket.BarcodeInput = barcode;
            ExecuteCommand(_viewModel.Basket.AddByBarcodeCommand);
        });
    }

    internal void PlaceOnPrimaryScreen()
    {
        var primary = Screens?.Primary;
        if (primary is null)
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Position = primary.WorkingArea.TopLeft;
        WindowState = WindowState.Maximized;
    }

    internal Task OpenShiftAsync()
    {
        var dlg = App.GetRequiredService<OpenShiftDialog>();
        dlg.SuggestedBalance = _shiftCashBalance;
        if (PosDialogHost.Show(dlg, this) != true)
            return Task.CompletedTask;

        return ApplyShiftOpenedAsync(dlg.OpeningCash);
    }

    internal async Task CloseShiftAsync()
    {
        if (!_session.IsShiftOpen)
            return;

        var dlg = App.GetRequiredService<CloseShiftDialog>();
        dlg.SuggestedBalance = _shiftCashBalance;
        if (PosDialogHost.Show(dlg, this) != true)
            return;

        await ApplyShiftClosedAsync(dlg.ClosingCash).ConfigureAwait(true);
    }

    internal void NavigateWarehouse()
    {
        if (Authorize(PosPermissions.ViewProcurement))
            ShowModuleWindow<WarehouseWindow>();
    }

    internal void NavigateShifts()
    {
        if (Authorize(PosPermissions.ViewShifts))
            ShowModuleWindow<ShiftsHistoryWindow>();
    }

    internal void NavigateCashOperations() => _ = OpenCashOperationsAsync();

    internal void NavigateReturn()
    {
        if (!Authorize(PosPermissions.EmployeeReturn))
            return;
        _viewModel.CloseSideMenu();
        var dlg = App.GetRequiredService<ReturnSaleDialog>();
        PosDialogHost.Show(dlg, this);
    }

    internal void NavigateFinance() => ShowModuleWindow<FinanceWindow>();

    internal void NavigateSales()
    {
        if (Authorize(PosPermissions.ViewSales))
            ShowModuleWindow<SalesWindow>();
    }

    internal void NavigateSettings()
    {
        if (Authorize(PosPermissions.ViewSettings))
            ShowModuleWindow<PosSettingsWindow>();
    }

    private bool Authorize(string permission)
    {
        if (_permissions.HasPermission(permission))
            return true;
        PosLogger.Log($"Permission denied: {permission}", "WARNING");
        _prompts.ShowWarning("Недостаточно прав для выполнения этой операции.");
        return false;
    }

    internal async Task LogoutAsync()
    {
        try
        {
            if (_session.IsShiftOpen)
            {
                var shiftResult = ShiftNotClosedDialog.Prompt(this);
                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.Cancel)
                    return;

                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.CloseShift)
                    await CloseShiftAsync().ConfigureAwait(true);
            }

            await NavigateToLoginAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Logout canceled.", "DEBUG");
        }
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PlaceOnPrimaryScreen();
        if (!_appInitialized)
            await InitializeApplicationAsync(null, _windowCts.Token).ConfigureAwait(true);

        if (UserPreferences.Instance.CustomerDisplay.IsEnabled)
        {
            var result = _customerDisplay.OpenForSession(this);
            if (result.IsSuccess)
                Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
            else
                PosLogger.Log(result.Message, "CUSTOMER_DISPLAY");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!App.ExitWithoutLoginRedirect && !_allowMainWindowClose)
        {
            e.Cancel = true;
            if (_closeFlowActive)
                return;
            _closeFlowActive = true;
            _ = HandleCloseRequestAsync();
            return;
        }

        SaveApplicationState();
        try
        {
            _windowCts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            PosLogger.Log($"Window cancellation source already disposed: {ex.GetType().Name}", "DEBUG");
        }
    }

    private async Task HandleCloseRequestAsync()
    {
        try
        {
            if (_session.IsShiftOpen)
            {
                var shiftResult = ShiftNotClosedDialog.Prompt(this);
                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.Cancel)
                    return;
                if (shiftResult == Views.Dialogs.ShiftNotClosedDialogResult.CloseShift)
                    await CloseShiftAsync().ConfigureAwait(true);
            }

            await NavigateToLoginAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Window close flow canceled.", "DEBUG");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Window close flow failed: {ex}", "ERROR");
            _prompts.ShowError("Не удалось корректно завершить текущую операцию.");
        }
        finally
        {
            _closeFlowActive = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _applicationStateService.CancelPendingSave();
        Screens.Changed -= OnCashierScreensChanged;
        _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        _customerDisplay.CloseForSession();
        _viewModel.Dispose();
        _windowCts.Dispose();
        if (ReferenceEquals(_hostBridge.Window, this))
            _hostBridge.Window = null;
    }

    private void OnCashierScreensChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PlaceOnPrimaryScreen();
            if (!UserPreferences.Instance.CustomerDisplay.IsEnabled)
                return;

            var result = _customerDisplay.OpenForSession(this);
            PosLogger.Log(result.Message, "CUSTOMER_DISPLAY");
            Activate();
        }, DispatcherPriority.Background);
    }

    private void OnSideMenuBackdropPressed(object? sender, PointerPressedEventArgs e) =>
        _viewModel.CloseSideMenu();
    private void OnViewModelStateChanged(object? sender, EventArgs e) =>
        ScheduleApplicationStateSave();

    private void RestoreApplicationState()
    {
        var state = _applicationStateService.Load();
        _viewModel.Catalog.RestoreState(state.Catalog);
        _viewModel.Basket.RestoreState(state.Basket);

        if (state.CatalogWidth is > 0 && state.CartWidth is > 0)
        {
            // Restore the splitter ratio, not stale pixel widths. Star-sized columns
            // always consume the full window width after maximize/resize.
            CatalogColumn.Width = new GridLength(state.CatalogWidth.Value, GridUnitType.Star);
            CartColumn.Width = new GridLength(state.CartWidth.Value, GridUnitType.Star);
        }
    }

    private void ScheduleApplicationStateSave() =>
        _applicationStateService.SaveDebounced(CaptureApplicationState);

    private void SaveApplicationState() =>
        _applicationStateService.Save(CaptureApplicationState());

    private ApplicationState CaptureApplicationState() =>
        new()
        {
            CatalogWidth = MainContentGrid.ColumnDefinitions[0].ActualWidth,
            CartWidth = MainContentGrid.ColumnDefinitions[2].ActualWidth,
            Catalog = _viewModel.Catalog.CaptureState(),
            Basket = _viewModel.Basket.CaptureState(),
        };

    private void OnCatalogGridSplitterPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        LogGridSplitterColumnsIfChanged();

    private void OnCatalogColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ColumnDefinition.WidthProperty)
            LogGridSplitterColumnsIfChanged();
    }

    private void OnCartColumnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ColumnDefinition.WidthProperty)
            LogGridSplitterColumnsIfChanged();
    }

    private void LogGridSplitterColumnsIfChanged()
    {
        var catalogWidth = Math.Round(CatalogColumn.Width.Value);
        var cartWidth = Math.Round(CartColumn.Width.Value);
        if (Math.Abs(catalogWidth - _lastLoggedCatalogWidth) < 1 &&
            Math.Abs(cartWidth - _lastLoggedCartWidth) < 1)
            return;

        _lastLoggedCatalogWidth = catalogWidth;
        _lastLoggedCartWidth = cartWidth;
        ScheduleApplicationStateSave();
    }

    private void ShowModuleWindow<T>() where T : Window
    {
        _viewModel.CloseSideMenu();
        var window = App.GetRequiredService<T>();
        window.Show(this);
    }

    private async Task RefreshShiftStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(App.PosCashboxId))
            {
                var rawList = await App.ShiftApi.ConstructionCashboxesListAsync(cancellationToken).ConfigureAwait(true);
                if (CartDisplayHelper.TryFirstCashbox(rawList, out var id, out var displayName))
                {
                    App.PosCashboxId = id;
                    NurMarketKassa.App.PosCashboxId = id;
                    _session.ActiveTerminal = id;
                    _session.PosCashboxDisplayName = displayName;
                    NurMarketKassa.App.PosCashboxDisplayName = displayName;
                }
            }

            await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(true);
            NurMarketKassa.App.SyncToSession(_session);

            if (_session.IsShiftOpen)
                _shiftCashBalance = ShiftBalanceHelper.FindOpenShiftBalance(
                    await App.ShiftApi.ConstructionShiftsListAsync(cancellationToken).ConfigureAwait(true),
                    App.PosCashboxId) ?? OfflinePosStateStore.ReadShiftCashBalance();

            _viewModel.Toolbar.RefreshUserTitle();
            _viewModel.Toolbar.Status.RefreshFromSession();
            UpdateShiftBalanceUi();
            _viewModel.Toolbar.NotifyShiftStateChanged();

            if (_session.IsShiftOpen)
                _viewModel.Catalog.StatusText = "Смена открыта";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"SHIFT refresh failed: {ex.Message}", "SHIFT");
            UpdateShiftBalanceUi();
        }
    }

    private async Task ApplyShiftOpenedAsync(decimal openingCash)
    {
        NurMarketKassa.App.SyncFromSession(_session);
        var result = await _cashShiftService.OpenShiftAsync(openingCash, _windowCts.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _prompts.ShowError(result.ErrorMessage ?? "Не удалось открыть смену.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.InfoMessage))
            _prompts.ShowToast(result.InfoMessage);

        NurMarketKassa.App.SyncToSession(_session);
        App.PosCashboxId = NurMarketKassa.App.PosCashboxId;

        _shiftCashBalance = result.Balance ?? openingCash;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.SideMenu.ShiftBalanceText = ShiftBalanceHelper.FormatBalance(_shiftCashBalance);
        _viewModel.Catalog.StatusText = $"Смена открыта. Остаток: {openingCash:0.00} сом";
        if (UserPreferences.Instance.CustomerDisplay.IsEnabled)
        {
            var displayResult = _customerDisplay.OpenForSession(this);
            if (displayResult.IsSuccess)
                Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
            else
                PosLogger.Log(displayResult.Message, "CUSTOMER_DISPLAY");
        }
    }

    private async Task ApplyShiftClosedAsync(decimal? closingCash)
    {
        if (!_session.IsShiftOpen)
            return;

        NurMarketKassa.App.SyncFromSession(_session);
        var result = await _cashShiftService.CloseShiftAsync(closingCash, _windowCts.Token).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            _prompts.ShowError(result.ErrorMessage ?? "Не удалось закрыть смену.");
            return;
        }

        _viewModel.Basket.ClearAfterShiftClose();
        NurMarketKassa.App.SyncToSession(_session);

        _shiftCashBalance = result.Balance ?? closingCash ?? 0m;
        UpdateShiftBalanceUi();
        _viewModel.Toolbar.NotifyShiftStateChanged();
        _viewModel.SideMenu.ShiftBalanceText = "Смена не открыта";
        _viewModel.Catalog.StatusText = "Смена закрыта.";
        _customerDisplay.CloseForSession();
    }

    private void UpdateShiftBalanceUi()
    {
        var balanceText = _session.IsShiftOpen
            ? $"Касса: {ShiftBalanceHelper.FormatBalance(_shiftCashBalance)}"
            : "Касса: 0.00 сом";

        _viewModel.Toolbar.Status.SetShiftBalance(_shiftCashBalance ?? 0m);
        _viewModel.SideMenu.ShiftBalanceText = _session.IsShiftOpen
            ? ShiftBalanceHelper.FormatBalance(_shiftCashBalance)
            : "Смена не открыта";

        if (_viewModel.Toolbar.Status.ShiftBalanceText != balanceText)
            _viewModel.Toolbar.Status.ShiftBalanceText = balanceText;
    }

    private async Task NavigateToLoginAsync()
    {
        FrmKeyboard.KillKeyboard();

        try
        {
            if (!_windowCts.IsCancellationRequested)
                SaveApplicationState();
            _windowCts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            PosLogger.Log($"Window already closing: {ex.GetType().Name}", "DEBUG");
        }

        App.AuthApi.ClearSession();
        // Logout is explicit: remove the DPAPI session as well as in-memory tokens.
        await App.GetRequiredService<NurMarketKassa.Core.Contracts.IAuthSessionManager>()
            .ClearSessionAsync()
            .ConfigureAwait(true);
        OfflineAuthSessionStore.Clear();
        App.PosCashboxId = null;
        _session.ActiveShiftId = null;
        _session.ActiveTerminal = null;
        _session.PosCashboxDisplayName = null;
        _session.IsOfflineBootstrap = false;
        _session.OfflineBootstrapMessage = null;
        App.IsOfflineBootstrap = false;
        App.OfflineBootstrapMessage = null;

        Hide();

        var login = App.GetRequiredService<LoginWindow>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = login;

        login.Show();

        _allowMainWindowClose = true;
        try
        {
            Close();
        }
        finally
        {
            _allowMainWindowClose = false;
        }
    }

    private void ApplyFullscreenPreference()
    {
        if (!UserPreferences.Instance.Fullscreen)
            return;

        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        WindowState = WindowState.Maximized;
    }
}
