using System.Text;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.AvaloniaHost.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Этот файл регистрирует в DI Avalonia-хоста инфраструктурные сервисы:
/// SQLite, REST API клиенты сайта, каталог, корзину, смену и вспомогательные зависимости кассы.
/// </summary>
internal static class AvaloniaHostServiceRegistration
{
    public static void AddAuthInfrastructure(IServiceCollection services)
    {
        var settings = AppSettings.Load();
        UserPreferences.LoadFromDiskAndMergeDefaults(settings);

        services.AddSingleton(settings);
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton<IAppUpdateService, VelopackUpdateService>();
        services.AddSingleton(_ => DatabaseService.Instance);
        services.AddSingleton<ILocalAccountsStore, LocalAccountsManager>();
        services.AddSingleton<IConnectivityService, ConnectivityService>();
        services.AddSingleton<IOfflineLoginSupport, OfflineLoginSupport>();
        services.AddSingleton<NurMarketApiClient>();
        services.AddSingleton<IAuthApiService, AuthApiService>();
        services.AddSingleton<IPermissionService, PermissionService>(sp =>
        {
            var service = new PermissionService(
                sp.GetRequiredService<IAuthApiService>(),
                sp.GetRequiredService<IAutonomousAuthService>());

            // Потолок скидки должен действовать во всех местах ввода одинаково, а сервис прав
            // доступен не везде (проект ViewModels его не видит). Крючок ставим один раз здесь,
            // когда сервис уже собран, — см. MaxDiscountGate.AppliesNow.
            MaxDiscountGate.IsExempt = () => service.HasPermission(PosPermissions.ViewSettings);
            return service;
        });
        services.AddSingleton<IAuthSessionManager, SecureStorageService>();
        services.AddSingleton<IOnlineOfflineAuthenticationService, OnlineOfflineAuthenticationService>();
        services.AddSingleton<IAutonomousAuthService, AutonomousAuthService>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<IAuthService, PosAuthService>();
    }

    public static void AddPosInfrastructure(IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(
            typeof(IPosSessionService).Assembly,
            typeof(PosCheckoutService).Assembly));

        services.AddSingleton<ICatalogApiService, CatalogApiService>();
        services.AddSingleton<ISalesApiService, SalesApiService>();
        services.AddSingleton<IClientsApiService, ClientsApiService>();
        services.AddSingleton<IShiftApiService, ShiftApiService>();
        services.AddSingleton<IInventoryApiService, InventoryApiService>();
        services.AddSingleton<ICatalogCacheService, AvaloniaCatalogCacheService>();
        services.AddSingleton<ICartService, CartService>();
        services.AddSingleton<IShiftStateService, ShiftStateService>();
        services.AddSingleton<Core.Contracts.IOfflinePosStateStore, OfflinePosStateStoreAdapter>();
        services.AddSingleton<ICashShiftService, CashShiftService>();
        services.AddSingleton<IPosCheckoutService, PosCheckoutService>();
        services.AddSingleton<IDeferredCartService, DeferredCartService>();
        services.AddSingleton<IMySqlConnectionSettings, AvaloniaMySqlConnectionSettings>();
        services.AddSingleton<IStockAuditWriter, AvaloniaStockAuditWriter>();
        services.AddSingleton<IStockService, StockService>();
        services.AddSingleton<ILocalStockLedger, LocalStockLedger>();
        services.AddSingleton<IServerStockGateway, AvaloniaServerStockGateway>();
        services.AddSingleton<AvaloniaLocalStockAdapter>();
        services.AddSingleton<ILocalStockProvider>(sp => sp.GetRequiredService<AvaloniaLocalStockAdapter>());
        services.AddSingleton<IStockCatalogUpdater>(sp => sp.GetRequiredService<AvaloniaLocalStockAdapter>());
        services.AddSingleton<ISyncConflictResolver, SyncConflictResolver>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<CustomerDisplayStateService>();
        services.AddSingleton<CustomerDisplayViewModel>();
        services.AddSingleton<AvaloniaCustomerDisplayService>();
        services.AddSingleton<ICustomerDisplayService>(sp => sp.GetRequiredService<AvaloniaCustomerDisplayService>());

        // Prefer AppSettings already registered by AddAuthInfrastructure.
        AppSettings settings = AppSettings.Load();
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(AppSettings)
                && descriptor.ImplementationInstance is AppSettings registered)
            {
                settings = registered;
                break;
            }
        }

        if (HardwareModeHelper.UsePhysicalScale())
            services.AddSingleton<IWeightScaleService, ComWeightScaleService>();
        else if (HardwareModeHelper.UseDemoHardware(settings))
            services.AddSingleton<IWeightScaleService, VirtualWeightScaleService>();
        else
            services.AddSingleton<IWeightScaleService, ComWeightScaleService>();

        if (HardwareModeHelper.UsePhysicalPrinter())
            services.AddSingleton<IReceiptPrinterService, LptReceiptPrinterService>();
        else if (HardwareModeHelper.UseDemoHardware(settings))
            services.AddSingleton<IReceiptPrinterService, VirtualReceiptPrinterService>();
        else
            services.AddSingleton<IReceiptPrinterService, LptReceiptPrinterService>();

        services.AddSingleton<MySqlSettings>(sp => sp.GetRequiredService<AppSettings>().MySql);
        services.AddSingleton<MySqlAuditService>(sp =>
        {
            var audit = new MySqlAuditService(sp.GetRequiredService<MySqlSettings>());
            try
            {
                audit.Initialize();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"MySql audit init skipped: {ex}", "AUDIT");
            }

            return audit;
        });

        services.AddSingleton<IUserPrompts, AvaloniaUserPrompts>();
        services.AddSingleton<IBarcodeInputService, AvaloniaKeyboardWedgeBarcodeService>();
        services.AddSingleton<IPosCheckoutUiFlow, AvaloniaPosCheckoutUiFlow>();
        services.AddSingleton<IWeightInputPrompt, AvaloniaWeightInputPrompt>();
        services.AddSingleton<IShiftOpenCoordinator, AvaloniaShiftOpenCoordinator>();
        services.AddSingleton<ScaleWeightProvider>();
        services.AddSingleton<IScaleWeightProvider>(sp => sp.GetRequiredService<ScaleWeightProvider>());
        services.AddSingleton<SpeakerVerificationService>();
        services.AddSingleton<IVoiceControlService, VoiceControlService>();
    }

    public static void InitializeAuthInfrastructure(IServiceProvider services)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var database = services.GetRequiredService<DatabaseService>();
            database.EnsureSchema();
            database.PurgeLegacySavedPasswords();
            OfflineAuthSessionStore.Clear();
            UserPreferences.Instance.LastLoginEmail = "";
            UserPreferences.Instance.LastLoginPassword = "";
            UserPreferences.Instance.SaveToDisk();
            var localAccounts = services.GetRequiredService<ILocalAccountsStore>();
            localAccounts.EnsureSchema();
            localAccounts.PurgeLegacyCredentials();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Auth schema init skipped (offline): {ex}", "AUTH");
        }
    }

    public static void InitializePosInfrastructure(IServiceProvider services)
    {
        try
        {
            services.GetRequiredService<IStockService>().Initialize();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"STOCK init failed: {ex}", "STOCK");
        }

        try
        {
            LocalProductRepository.Instance.EnsureSchema();
            _ = LocalProductRepository.Instance.WarmUpCacheAsync();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG warm-up failed: {ex}", "CATALOG");
        }

        try
        {
            services.GetRequiredService<IWeightScaleService>().Start();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"SCALE startup failed: {ex.Message}", "SCALE");
        }

        try
        {
            PoleDisplayService.Instance.Start();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"POLE_DISPLAY startup failed: {ex.Message}", "POLE_DISPLAY");
        }

        try
        {
            PrinterKeepAliveService.Instance.Start();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PRINTER keep-alive startup failed: {ex.Message}", "PRINTER");
        }
    }
}
