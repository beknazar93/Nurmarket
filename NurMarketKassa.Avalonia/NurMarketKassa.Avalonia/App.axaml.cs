using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using NurMarketKassa.ViewModels.Settings;

namespace NurMarketKassa.AvaloniaHost;

public partial class App : Application
{
    public static IHost? AppHost { get; private set; }

    // Отменяется в desktop.Exit ДО того, как хост будет уничтожен — без этого автовход
    // (медленная/зависшая сеть) продолжает выполняться после DisposeApplicationHost() и
    // падает с ObjectDisposedException на уже освобождённых сервисах/DI-контейнере.
    private static readonly CancellationTokenSource ShutdownCts = new();

    // Bridge for ported services/views (same pattern as WPF App.*).
    public static ICatalogApiService CatalogApi { get; private set; } = null!;
    public static ISalesApiService SalesApi { get; private set; } = null!;
    public static IShiftApiService ShiftApi { get; private set; } = null!;
    public static IAuthApiService AuthApi { get; private set; } = null!;
    public static MySqlAuditService AuditDb { get; private set; } = null!;
    public static string? PosCashboxId { get; set; }
    public static bool ExitWithoutLoginRedirect { get; set; }
    public static bool IsOfflineBootstrap { get; set; }
    public static string? OfflineBootstrapMessage { get; set; }

    public static string? CurrentUserId
    {
        get => TryGetSession()?.CurrentUserId;
        set
        {
            var session = TryGetSession();
            if (session != null) session.CurrentUserId = value;
        }
    }

    public static string? CurrentUserDisplayName
    {
        get => TryGetSession()?.CurrentUserDisplayName;
        set
        {
            var session = TryGetSession();
            if (session != null) session.CurrentUserDisplayName = value;
        }
    }

    public static T GetRequiredService<T>() where T : notnull =>
        AppHost!.Services.GetRequiredService<T>();

    public static void ApplyTheme(bool dark)
    {
        if (Current is not App app)
            return;

        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        AccentThemeService.Apply(UserPreferences.Instance.AccentTheme, dark);

        // Экран покупателя следует за темой кассы, когда в Настройки → Монитор выбрана
        // "Системная" (2026-09-07) — тот же ApplySettings-путь, что уже используется для
        // синхронизации акцентного цвета (см. SyncCustomerDisplayAccent) ниже.
        GetRequiredService<AvaloniaCustomerDisplayService>().ApplySettings(UserPreferences.Instance.CustomerDisplay);
    }

    /// <summary>Меняет только акцентный цвет (кнопки/вкладки/цены/фокус во всей программе),
    /// не трогая светлый/тёмный режим. См. AccentThemeService.</summary>
    public static void ApplyAccentTheme(string themeId)
    {
        UserPreferences.Instance.AccentTheme = themeId;
        SyncCustomerDisplayAccent(themeId);
        UserPreferences.Instance.SaveToDisk();
        AccentThemeService.Apply(themeId, UserPreferences.Instance.DarkTheme);

        // "Жидкое стекло" включает имитацию живого блюра на кассе автоматически (без ручной
        // настройки обоев в Кастомизации) — остальные темы её выключают, если фото не выбрано.
        GetRequiredService<MainWindowHostBridge>().Window?.RefreshBackgroundWallpaper();
    }

    /// <summary>Переключает раскладку главного экрана кассы (2026-09-06) — "standard" (текущая,
    /// каталог+корзина рядом) или "onec" (альтернативная, в стиле 1С "Рабочее место кассира":
    /// крупные плитки, чек фиксированной колонкой справа, numpad для количества). Применяется
    /// мгновенно на уже открытой кассе — тот же трёхшаговый идиом, что и ApplyAccentTheme.</summary>
    public static void ApplyMainLayoutMode(string mode)
    {
        UserPreferences.Instance.MainLayoutMode = mode;
        UserPreferences.Instance.SaveToDisk();
        GetRequiredService<MainWindowHostBridge>().Window?.RefreshLayoutMode();
    }

    /// <summary>Экран покупателя (2-й экран) подхватывает акцентный цвет выбранной темы кассы —
    /// при каждой смене темы, не только при первом запуске. Если окно экрана покупателя сейчас
    /// открыто, оно обновляется вживую через AvaloniaCustomerDisplayService.ApplySettings, а не
    /// только при следующем открытии.</summary>
    private static void SyncCustomerDisplayAccent(string themeId)
    {
        var swatchHex = AccentThemeService.GetAccentHex(themeId);
        if (swatchHex is null)
            return;

        var display = UserPreferences.Instance.CustomerDisplay;
        if (string.Equals(display.AccentColor, swatchHex, StringComparison.OrdinalIgnoreCase))
            return;

        display.AccentColor = swatchHex;
        GetRequiredService<AvaloniaCustomerDisplayService>().ApplySettings(display);
    }

    private static ResourceDictionary LoadThemeDictionary(string source) =>
        AvaloniaXamlLoader.Load(new Uri(source, UriKind.Absolute)) as ResourceDictionary
        ?? throw new InvalidOperationException($"Failed to load theme: {source}");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppHost = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        PosLogger.Configure(AppHost.Services.GetRequiredService<ILoggerFactory>());
        // Смена компании = смена набора локальных данных. Подписка здесь, один раз на запуск:
        // так разделение срабатывает на любом пути входа, включая смену кассира.
        CompanyInfoService.CompanyChanged += id => AccountDataIsolation.SwitchTo(id);
        // Догрузка истории продаж с сервера живёт в приложении, а вызывает её фоновая
        // синхронизация из Infrastructure — связываем их здесь.
        SalesHistoryBackfillHook.Register(SalesHistoryBackfill.RunAsync);
        RegisterGlobalExceptionHandlers();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Never construct LoginWindow speculatively. The credential-free
            // splash remains the only visual until auto-auth reaches a final state.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            // 2026-09-23. Чекпоинт WAL висел ТОЛЬКО на desktop.Exit. Когда кассир
            // выключает компьютер через «Пуск», Windows шлёт запрос на завершение сеанса и
            // затем убивает процесс — Exit не срабатывает, и несведённый WAL остаётся на
            // диске. Это самый частый способ выключения кассы и наиболее вероятный
            // оставшийся источник «database disk image is malformed».
            desktop.ShutdownRequested += (_, _) =>
            {
                try { NurMarketKassa.Services.DatabaseService.CheckpointWal(); }
                catch (Exception ex) { NurMarketKassa.Services.PosLogger.Log($"WAL checkpoint (shutdown) failed: {ex.Message}", "WARNING"); }
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { NurMarketKassa.Services.DatabaseService.CheckpointWal(); }
                catch { /* процесс уже завершается — логировать некуда */ }
            };

            desktop.Exit += (_, _) =>
            {
                ShutdownCts.Cancel();
                // Сливает WAL в основной файл БД при штатном закрытии — без этого несколько раз
                // за 2026-09-04 недописанный WAL для редко используемых таблиц приводил к
                // "database disk image is malformed" при следующем запуске (см.
                // DatabaseService.CheckpointWal). Не спасает от taskkill /F (тот вообще не даёт
                // коду выполниться), но перекрывает обычное закрытие окна/Stop-Process.
                try { NurMarketKassa.Services.DatabaseService.CheckpointWal(); }
                catch { /* завершение процесса не должно зависеть от этого */ }
                DisposeApplicationHost();
            };
            // Ежедневная резервная копия локальной базы — в фоне, чтобы не задерживать показ
            // экрана кассира. В базе лежит очередь непроведённых офлайн-продаж, которой нет
            // больше нигде: сервер про неё по определению не знает.
            _ = Task.Run(() =>
            {
                try { NurMarketKassa.Services.DatabaseService.EnsureDailyBackup(); }
                catch { /* страховка не должна мешать запуску кассы */ }
            });

            _ = StartHostAndAuthenticationAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartHostAndAuthenticationAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        try
        {
            await AppHost!.StartAsync().ConfigureAwait(true);
            UiDispatcherHolder.Current = AppHost.Services.GetRequiredService<IDispatcher>();
            AvaloniaHostServiceRegistration.InitializeAuthInfrastructure(AppHost.Services);
            AvaloniaHostServiceRegistration.InitializePosInfrastructure(AppHost.Services);

            var settings = AppHost.Services.GetRequiredService<AppSettings>();
            var session = AppHost.Services.GetRequiredService<IAppSession>();
            CatalogApi = AppHost.Services.GetRequiredService<ICatalogApiService>();
            SalesApi = AppHost.Services.GetRequiredService<ISalesApiService>();
            ShiftApi = AppHost.Services.GetRequiredService<IShiftApiService>();
            AuthApi = AppHost.Services.GetRequiredService<IAuthApiService>();
            AuditDb = AppHost.Services.GetRequiredService<MySqlAuditService>();

            NurMarketKassa.App.InitializeFromHost(
                settings, AuthApi, CatalogApi, SalesApi, ShiftApi, AuditDb, session);
            NurMarketKassa.App.SyncFromSession(session);
            // X/Z-отчёт должен учитывать локальные внесения/изъятия текущей смены.
            CashShiftService.ShiftCashOperationsNetProvider = ShiftCashOperationsStore.NetForShift;
            PosCashboxId = NurMarketKassa.App.PosCashboxId;
            CurrentUserId = NurMarketKassa.App.CurrentUserId;
            ApplyTheme(UserPreferences.Instance.DarkTheme);
            LocalizationManager.Apply(LanguagePackGate.EnforceOnStartup());
            // 2026-09-07: если 15-минутный мастер-доступ истёк, пока касса была закрыта —
            // сбрасываем сразу при старте, а не ждём таймера (который тоже переставится ниже).
            LicenseKeys.ExpireIfDue();

            // Пока CrashReportService.UploadEndpoint не задан (появится в будущем обновлении) —
            // не делает ничего, отчёты просто продолжают копиться локально.
            _ = CrashReportService.TryUploadPendingReportsAsync();

            await CompleteAuthenticationStartupAsync(desktop, splash, session).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Application startup canceled.", "DEBUG");
            if (!ShutdownCts.IsCancellationRequested)
                desktop.Shutdown();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application host startup failed: {ex}", "CRITICAL");
            // Хост уже мог быть уничтожен через desktop.Exit (ShutdownCts) — тогда
            // AppHost.Services недоступен, и попытка показать LoginWindow только
            // добавит вторую, ничего не объясняющую ошибку поверх первой.
            if (!ShutdownCts.IsCancellationRequested)
                ShowLoginWindow(desktop, splash);
        }
    }

    /// <summary>Сообщение, которое видит кассир при неожиданной ошибке — без технических деталей.</summary>
    private const string UserFacingErrorMessage =
        "Произошла ошибка в программе. Программисты уже знают о таких случаях и работают над " +
        "исправлением. Попробуйте повторить действие ещё раз — если ошибка повторяется, " +
        "сообщите администратору.";

    private static void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            PosLogger.Log(
                $"Unhandled application exception. Terminating={args.IsTerminating}. {exception}",
                "CRITICAL");
            if (exception != null)
                CrashReportService.WriteReport(exception, $"AppDomain (Terminating={args.IsTerminating})");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            var onlyCancellation = args.Exception.Flatten().InnerExceptions.All(
                exception => exception is OperationCanceledException);
            PosLogger.Log(
                onlyCancellation
                    ? "Unobserved task completed by cancellation."
                    : $"Unobserved task exception: {args.Exception}",
                onlyCancellation ? "DEBUG" : "ERROR");
            if (!onlyCancellation)
                CrashReportService.WriteReport(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            PosLogger.Log($"Unhandled Avalonia UI exception: {args.Exception}", "CRITICAL");
            CrashReportService.WriteReport(args.Exception, "Avalonia UI thread");

            // Пробуем удержать кассу живой вместо аварийного закрытия — ошибка уже
            // произошла и залогирована, но кассиру лучше увидеть понятное сообщение
            // и продолжить работу, чем потерять открытую смену/корзину из-за краша.
            try
            {
                var lifetime = Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                var owner = lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
                PosDialogs.Error(owner, UserFacingErrorMessage, "Ошибка");
                args.Handled = true;
            }
            catch (Exception dialogEx)
            {
                // Если даже диалог показать не удалось — не рискуем, даём приложению упасть штатно.
                PosLogger.Log($"Failed to show friendly error dialog: {dialogEx.GetType().Name}", "WARNING");
                args.Handled = false;
            }
        };
    }

    private static void DisposeApplicationHost()
    {
        if (AppHost is null)
            return;
        try
        {
            AppHost.Dispose();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application host disposal failed: {ex}", "ERROR");
        }
    }

    private static async Task CompleteAuthenticationStartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        IAppSession appSession)
    {
        try
        {
            // 2026-09-10: если последний успешный вход на этом ПК был автономным (локальный
            // логин/пароль, без NurCRM) — тихо продолжаем его здесь же, ДО обычного
            // AutoLoginAsync ниже. Без этой ветки касса на каждом перезапуске сначала
            // пыталась бы восстановить старую (часто уже протухшую) NurCRM-сессию — экран
            // "Работать автономно" даже не успевал бы показаться, MainWindow открывался бы
            // сразу в обычном (сетевом) режиме. Уровень доверия тот же, что и у обычного
            // автовхода ниже — пароль повторно не спрашивается.
            if (await TryAutoResumeAutonomousSessionAsync(desktop, splash, appSession).ConfigureAwait(true))
                return;

            var authentication = AppHost!.Services
                .GetRequiredService<IOnlineOfflineAuthenticationService>();
            var result = await authentication.AutoLoginAsync(ShutdownCts.Token);

            if (!result.IsSuccess || result.Session is null)
            {
                ShowLoginWindow(desktop, splash);
                return;
            }

            ApplyAuthenticatedSession(appSession, result);
            NurMarketKassa.App.SyncFromSession(appSession);
            PosCashboxId = appSession.ActiveTerminal;

            // Разделение данных аккаунтов (AccountDataIsolation) здесь НЕ вызывается: на
            // автовходе компания ещё не загружена, а ключ по пользователю разрезал бы данные
            // одной компании между её кассирами. Оно произойдёт в MainWindow.InitializeApplicationAsync
            // сразу после CompanyInfoService.RefreshAsync — до любой продажи.
            AccountCatalogIsolation.PrepareForAuthenticatedUser("", appSession.CurrentUserId);
            AuditDb.LogEvent(
                "auth",
                "auto_login",
                new { mode = result.Mode.ToString(), userId = appSession.CurrentUserId },
                appSession.CurrentUserId);
            AppHost.Services.GetRequiredService<SyncService>().Start();

            var mainWindow = ResolveMainShell();
            var shell = (IMainShell)mainWindow;
            var canOpen = await shell.InitializeApplicationAsync(null, ShutdownCts.Token);
            if (!canOpen)
            {
                splash.Close();
                desktop.Shutdown();
                return;
            }

            desktop.MainWindow = mainWindow;
            shell.PlaceOnPrimaryScreen();
            mainWindow.Show();
            splash.Close();
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Automatic startup authentication canceled (app closing).", "AUTH");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Automatic startup authentication failed: {ex}", "AUTH");
            // Если приложение уже закрывается (ShutdownCts), AppHost/desktop уничтожены —
            // показывать окно входа здесь означает обращаться к disposed-объектам.
            if (!ShutdownCts.IsCancellationRequested)
                ShowLoginWindow(desktop, splash);
        }
    }

    /// <summary>2026-09-10: см. вызов в CompleteAuthenticationStartupAsync. Возвращает true, если
    /// автономная сессия была продолжена (тогда MainWindow уже показан или приложение уже
    /// закрывается — вызывающий код должен просто return) — false означает "на этом ПК автономный
    /// режим не активен/не был последним входом", и обычный AutoLoginAsync должен выполняться как
    /// раньше, без каких-либо изменений.</summary>
    private static async Task<bool> TryAutoResumeAutonomousSessionAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        IAppSession appSession)
    {
        // Программа владельца работает только с NurCRM: автономного (локального) входа у неё нет.
        if (NurMarketKassa.Services.AppMode.IsOwner)
            return false;

        var autonomous = AppHost!.Services.GetRequiredService<IAutonomousAuthService>();
        var (resumed, email, displayName) = autonomous.TryAutoResume();
        if (!resumed)
            return false;

        appSession.CurrentUserId = email;
        appSession.CurrentUserDisplayName = displayName;
        appSession.PosCashboxDisplayName = displayName;
        appSession.ActiveTerminal = null;
        appSession.IsOfflineBootstrap = true;
        appSession.OfflineBootstrapMessage = "Автономный режим — работа без интернета.";

        NurMarketKassa.App.SyncFromSession(appSession);
        PosCashboxId = appSession.ActiveTerminal;
        IsOfflineBootstrap = true;
        OfflineBootstrapMessage = appSession.OfflineBootstrapMessage;

        // Ни CompanyInfoService.RefreshAsync, ни AccountCatalogIsolation.PrepareForAuthenticatedUser,
        // ни SyncService.Start() — все три про NurCRM, которого у автономного аккаунта нет (см. тот
        // же комментарий в LoginWindow.axaml.cs.OnLoginSuccess).
        var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
        var canOpen = await mainWindow.InitializeApplicationAsync(null, ShutdownCts.Token).ConfigureAwait(true);
        if (!canOpen)
        {
            splash.Close();
            desktop.Shutdown();
            return true;
        }

        desktop.MainWindow = mainWindow;
        mainWindow.PlaceOnPrimaryScreen();
        mainWindow.Show();
        splash.Close();
        return true;
    }

    private static void ApplyAuthenticatedSession(
        IAppSession appSession,
        AuthenticationResult result)
    {
        var authenticated = result.Session!;
        var offline = result.Mode == AuthenticationMode.Offline;

        appSession.CurrentUserId = authenticated.UserId;
        appSession.CurrentUserDisplayName = authenticated.DisplayName;
        appSession.ActiveTerminal = authenticated.BranchId;
        appSession.PosCashboxDisplayName = authenticated.DisplayName;
        appSession.IsOfflineBootstrap = offline;
        appSession.OfflineBootstrapMessage = offline
            ? "Нет связи с сервером. Используются локальные данные."
            : null;

        IsOfflineBootstrap = offline;
        OfflineBootstrapMessage = appSession.OfflineBootstrapMessage;
        CurrentUserId = authenticated.UserId;
    }

    /// <summary>Главное окно после входа: касса или программа владельца (см. AppMode). Три места
    /// входа — автовход, автономный вход и окно входа — берут окно только отсюда.</summary>
    internal static Window ResolveMainShell() =>
        NurMarketKassa.Services.AppMode.IsOwner
            ? GetRequiredService<OwnerShellWindow>()
            : GetRequiredService<MainWindow>();

    private static void ShowLoginWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash)
    {
        // This is the only place in startup where LoginWindow is resolved.
        var loginWindow = AppHost!.Services.GetRequiredService<LoginWindow>();
        desktop.MainWindow = loginWindow;
        loginWindow.Show();
        splash.Close();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            NurMarketKassa.Services.AppMode.DataFolderName,
            "Logs");
#if DEBUG
        const LogLevel minimumLogLevel = LogLevel.Debug;
#else
        const LogLevel minimumLogLevel = LogLevel.Information;
#endif
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(minimumLogLevel);
            builder.AddProvider(new SafeFileLoggerProvider(
                Path.Combine(logDirectory, "nurmarket-kassa.log"),
                minimumLogLevel));
        });

        services.AddSingleton<IDispatcher, AvaloniaDispatcher>();
        services.AddSingleton<ProductThumbService>();
        services.AddSingleton<ISettingsImagePicker, AvaloniaSettingsImagePicker>();
        services.AddSingleton<IOperatingSystemKeyboardService, WindowsOperatingSystemKeyboardService>();
        services.AddSingleton<IAppSession, AvaloniaAppSession>();
        services.AddSingleton<IWindowService, AvaloniaWindowService>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();

        AvaloniaHostServiceRegistration.AddAuthInfrastructure(services);
        AvaloniaHostServiceRegistration.AddPosInfrastructure(services);
        MainViewModelRegistration.AddMainWindowViewModels(services);

        // ViewModels (host-local + shared)
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<WarehouseViewModel>();

        // Windows / views
        services.AddTransient<LoginWindow>();
        services.AddTransient<MainWindow>();
        services.AddTransient<OwnerShellWindow>();
        services.AddTransient<CheckoutDialog>();
        services.AddTransient<AdminSupportWindow>();
        services.AddTransient<WarehouseWindow>();
        services.AddTransient<ShiftsHistoryWindow>();
        services.AddTransient<ShiftHistoryView>();
        services.AddTransient<ShiftSummaryView>();
        services.AddTransient<ServicesWindow>();
        services.AddTransient<SalesWindow>();
        services.AddTransient<AbcAnalysisWindow>();
        services.AddTransient<FinanceWindow>();
        services.AddTransient<IrregularReceiptsWindow>();
        services.AddTransient<ScalesPluWindow>();
        services.AddTransient<LogsAndErrorsWindow>();
        services.AddTransient<RemoteSupportWindow>();
        services.AddTransient<KnowledgeBaseWindow>();
        services.AddTransient<RestockSuggestionsWindow>();
        services.AddTransient<ClientsWindow>();
        services.AddTransient<PayDebtDialog>();
        services.AddTransient<CrmWebViewWindow>();
        services.AddTransient<PosSettingsWindow>();
        services.AddTransient<FilterWindow>();
        services.AddTransient<OpenShiftDialog>();
        services.AddTransient<CloseShiftDialog>();
        services.AddTransient<CashOperationsDialog>();
        services.AddTransient<CashHistoryDialog>();
        services.AddTransient<ReturnSaleDialog>();
        services.AddTransient<ReturnLineReasonDialog>();
        services.AddTransient<ProductDetailDialog>();
        services.AddTransient<WeighedProductDialog>();
        services.AddTransient<OrderDiscountDialog>();
        services.AddTransient<DeferredCartsDialog>();
        services.AddTransient<ReceiptPreviewDialog>();
        services.AddTransient<FinanceDateRangeDialog>();
        services.AddTransient<FrmKeyboard>();
        services.AddTransient<NoStockDialog>();
        services.AddTransient<NewOperationDialog>();
        services.AddTransient<ShiftDetailsDialog>();
        services.AddTransient<ShiftActionsMenu>();
        services.AddTransient<PosAlertDialog>();
        services.AddTransient<PosConfirmDialog>();
        services.AddTransient<PaymentConfirmationDialog>();
        services.AddTransient<PrinterNotConnectedDialog>();
        services.AddTransient<SaleSuccessDialog>();
        services.AddTransient<PaymentStockBlockedDialog>();
        services.AddTransient<DeferredStockIssuesDialog>();
    }

    private static IAppSession? TryGetSession()
    {
        try { return AppHost?.Services.GetService<IAppSession>(); }
        catch (Exception ex)
        {
            PosLogger.Log($"Session resolution failed: {ex}", "WARNING");
            return null;
        }
    }
}
