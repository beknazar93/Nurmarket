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

    public static T GetRequiredService<T>() where T : notnull =>
        AppHost!.Services.GetRequiredService<T>();

    public static void ApplyTheme(bool dark)
    {
        if (Current is not App app)
            return;

        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
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
        RegisterGlobalExceptionHandlers();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Never construct LoginWindow speculatively. The credential-free
            // splash remains the only visual until auto-auth reaches a final state.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();
            desktop.Exit += (_, _) => DisposeApplicationHost();
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
            PosCashboxId = NurMarketKassa.App.PosCashboxId;
            CurrentUserId = NurMarketKassa.App.CurrentUserId;
            ApplyTheme(UserPreferences.Instance.DarkTheme);

            await CompleteAuthenticationStartupAsync(desktop, splash, session).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Application startup canceled.", "DEBUG");
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Application host startup failed: {ex}", "CRITICAL");
            ShowLoginWindow(desktop, splash);
        }
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            PosLogger.Log(
                $"Unhandled application exception. Terminating={args.IsTerminating}. {exception}",
                "CRITICAL");
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
            args.SetObserved();
        };

        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            PosLogger.Log($"Unhandled Avalonia UI exception: {args.Exception}", "CRITICAL");
            args.Handled = false;
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
            var authentication = AppHost!.Services
                .GetRequiredService<IOnlineOfflineAuthenticationService>();
            var result = await authentication.AutoLoginAsync(CancellationToken.None);

            if (!result.IsSuccess || result.Session is null)
            {
                ShowLoginWindow(desktop, splash);
                return;
            }

            ApplyAuthenticatedSession(appSession, result);
            NurMarketKassa.App.SyncFromSession(appSession);
            PosCashboxId = appSession.ActiveTerminal;

            AccountCatalogIsolation.PrepareForAuthenticatedUser("", appSession.CurrentUserId);
            AuditDb.LogEvent(
                "auth",
                "auto_login",
                new { mode = result.Mode.ToString(), userId = appSession.CurrentUserId },
                appSession.CurrentUserId);
            AppHost.Services.GetRequiredService<SyncService>().Start();

            var mainWindow = AppHost.Services.GetRequiredService<MainWindow>();
            await mainWindow.InitializeApplicationAsync(null, CancellationToken.None);

            desktop.MainWindow = mainWindow;
            mainWindow.PlaceOnPrimaryScreen();
            mainWindow.Show();
            splash.Close();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Automatic startup authentication failed: {ex}", "AUTH");
            ShowLoginWindow(desktop, splash);
        }
    }

    private static void ApplyAuthenticatedSession(
        IAppSession appSession,
        AuthenticationResult result)
    {
        var authenticated = result.Session!;
        var offline = result.Mode == AuthenticationMode.Offline;

        appSession.CurrentUserId = authenticated.UserId;
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
            "NurMarketKassa",
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
        services.AddTransient<CheckoutDialog>();
        services.AddTransient<AdminSupportWindow>();
        services.AddTransient<WarehouseWindow>();
        services.AddTransient<ShiftsHistoryWindow>();
        services.AddTransient<ShiftHistoryView>();
        services.AddTransient<ShiftSummaryView>();
        services.AddTransient<ServicesWindow>();
        services.AddTransient<SalesWindow>();
        services.AddTransient<FinanceWindow>();
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
