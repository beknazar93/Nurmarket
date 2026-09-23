using System.Globalization;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Этот файл собирает и регистрирует ViewModel главного окна кассира,
/// связывая каталог, корзину, панель инструментов и обработчики действий UI.
/// </summary>
internal static class MainViewModelRegistration
{
    public static void AddMainWindowViewModels(IServiceCollection services)
    {
        services.AddSingleton<MainWindowHostBridge>();
        services.AddTransient<MainStatusViewModel>();

        services.AddTransient<MainWindowViewModel>(sp =>
        {
            var bridge = sp.GetRequiredService<MainWindowHostBridge>();
            var session = sp.GetRequiredService<IAppSession>();
            var status = sp.GetRequiredService<MainStatusViewModel>();

            BasketPanelViewModel basket = null!;
            basket = new BasketPanelViewModel(
                sp.GetRequiredService<ICartService>(),
                sp.GetRequiredService<IUserPrompts>(),
                sp.GetRequiredService<IPosCheckoutService>(),
                sp.GetRequiredService<IDeferredCartService>(),
                sp.GetRequiredService<ICustomerDisplayService>(),
                sp.GetRequiredService<IWindowService>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IDispatcher>(),
                LookupCatalogProduct,
                productId => GetMaximumCartQuantity(productId, basket),
                sp.GetService<IPosCheckoutUiFlow>(),
                product => bridge.AddProductFromCatalog?.Invoke(product) ?? Task.CompletedTask,
                () => bridge.OpenDeferredCarts?.Invoke() ?? Task.CompletedTask,
                () => bridge.ApplyOrderDiscount?.Invoke() ?? Task.CompletedTask,
                line => bridge.ReweighCartLine?.Invoke(line) ?? Task.CompletedTask,
                line => bridge.ApplyLineDiscount?.Invoke(line) ?? Task.CompletedTask,
                sp.GetRequiredService<IPermissionService>());
            bridge.GetOtherOpenReceiptQuantity = basket.GetQuantityInOtherOpenReceipts;

            var catalog = new CatalogPanelViewModel(
                sp.GetRequiredService<ICatalogCacheService>(),
                sp.GetRequiredService<IDispatcher>(),
                sp.GetRequiredService<IConnectivityService>(),
                product => bridge.TryAddProduct(product),
                sp.GetService<ICatalogApiService>(),
                sp.GetService<MySqlAuditService>(),
                sp.GetService<IUserPrompts>());

            MainWindowViewModel? main = null;

            var toolbar = new MainToolbarViewModel(
                session,
                status,
                toggleSideMenu: () => main?.ToggleSideMenu(),
                checkCustomerDisplay: () => bridge.Window?.CheckCustomerDisplay(),
                toggleTheme: () => bridge.Window?.ToggleTheme(),
                toggleKeyboard: () => bridge.Window?.ToggleKeyboard(),
                openShiftHandler: () => bridge.Window?.OpenShiftAsync() ?? Task.CompletedTask,
                closeShiftHandler: () => bridge.Window?.CloseShiftAsync() ?? Task.CompletedTask);

            var sideMenu = new SideMenuViewModel(
                session,
                closeMenu: () => main?.CloseSideMenu(),
                navigateWarehouse: () => bridge.Window?.NavigateWarehouse(),
                navigateShifts: () => bridge.Window?.NavigateShifts(),
                navigateReturn: () => bridge.Window?.NavigateReturn(),
                navigateCashOperations: () => bridge.Window?.NavigateCashOperations(),
                navigateFinance: () => bridge.Window?.NavigateFinance(),
                navigateSales: () => bridge.Window?.NavigateSales(),
                navigateSettings: () => bridge.Window?.NavigateSettings(),
                logout: () => bridge.Window?.LogoutAsync() ?? Task.CompletedTask);

            main = new MainWindowViewModel(toolbar, catalog, basket, sideMenu, session);
            return main;
        });
    }

    private static CatalogProductTileVm? LookupCatalogProduct(string code)
    {
        var repo = LocalProductRepository.Instance;
        return repo.TryGetTileByBarcode(code) ?? repo.TryGetTileBySku(code);
    }

    private static double? GetMaximumCartQuantity(string productId, BasketPanelViewModel basket)
    {
        var product = LocalProductRepository.Instance.TryGetTileBySku(productId)
            ?? CatalogCacheService.Products.FirstOrDefault(item =>
                string.Equals(item.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (product is null)
            return null;

        var reserved = StockAvailabilityService.CalculateReservedQuantity(productId)
            + basket.GetQuantityInOtherOpenReceipts(productId);
        return Math.Max(0, product.Quantity - reserved);
    }
}
