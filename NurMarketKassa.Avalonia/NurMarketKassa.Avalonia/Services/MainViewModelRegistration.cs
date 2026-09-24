using System.Globalization;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Configuration;
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

            CatalogPanelViewModel catalog = null!;
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
                (productId, salePackageId) => GetMaximumCartQuantity(productId, salePackageId, basket),
                sp.GetService<IPosCheckoutUiFlow>(),
                (product, lineNameOverride) => bridge.AddProductFromCatalog?.Invoke(product, lineNameOverride) ?? Task.CompletedTask,
                () => bridge.OpenDeferredCarts?.Invoke() ?? Task.CompletedTask,
                () => bridge.ApplyOrderDiscount?.Invoke() ?? Task.CompletedTask,
                line => bridge.ReweighCartLine?.Invoke(line) ?? Task.CompletedTask,
                line => bridge.ApplyLineDiscount?.Invoke(line) ?? Task.CompletedTask,
                sp.GetRequiredService<IPermissionService>(),
                sp.GetService<IClientsApiService>(),
                () => bridge.Window?.NavigatePayDebt(),
                (product, weightKg) => bridge.Window?.AddWeighedProductWithKnownWeightAsync(product, weightKg) ?? Task.CompletedTask,
                // После оплаты — перечитать каталог из SQLite без сети (2026-09-07, см. RepublishFromLocalAsync):
                // раньше здесь была полная синхронизация с сервером после КАЖДОЙ продажи.
                onCheckoutSuccess: () => _ = catalog.RepublishFromLocalAsync(),
                addCustomItem: () => bridge.AddCustomItem?.Invoke() ?? Task.CompletedTask,
                onBarcodeNotFound: barcode => bridge.OfferAddUnknownProduct?.Invoke(barcode) ?? Task.CompletedTask,
                replenishStock: (productId, needed, isWeight) =>
                    bridge.ReplenishStockForProduct?.Invoke(productId, needed, isWeight)
                        ?? Task.FromResult(false));
            bridge.GetOtherOpenReceiptQuantity = basket.GetQuantityInOtherOpenReceipts;

            catalog = new CatalogPanelViewModel(
                sp.GetRequiredService<ICatalogCacheService>(),
                sp.GetRequiredService<IDispatcher>(),
                sp.GetRequiredService<IConnectivityService>(),
                product => bridge.TryAddProduct(product),
                sp.GetService<ICatalogApiService>(),
                sp.GetService<MySqlAuditService>(),
                sp.GetService<IUserPrompts>(),
                sp.GetService<IAutonomousAuthService>());

            MainWindowViewModel? main = null;

            var toolbar = new MainToolbarViewModel(
                session,
                status,
                toggleSideMenu: () => main?.ToggleSideMenu(),
                checkCustomerDisplay: () => bridge.Window?.CheckCustomerDisplay(),
                toggleTheme: () => bridge.Window?.ToggleTheme(),
                toggleKeyboard: () => bridge.Window?.ToggleKeyboard(),
                openShiftHandler: () => bridge.Window?.OpenShiftAsync() ?? Task.CompletedTask,
                closeShiftHandler: () => bridge.Window?.CloseShiftAsync() ?? Task.CompletedTask,
                openUpdate: () => bridge.Window?.NavigateSettingsUpdates());

            var sideMenu = new SideMenuViewModel(
                session,
                closeMenu: () => main?.CloseSideMenu(),
                navigateWarehouse: () => bridge.Window?.NavigateWarehouse(),
                navigateStaffTimesheet: () => bridge.Window?.NavigateStaffTimesheet(),
                navigateReturn: () => bridge.Window?.NavigateReturn(),
                navigateDeferredReceipts: () => bridge.Window?.NavigateDeferredReceipts(),
                navigateFinance: () => bridge.Window?.NavigateFinance(),
                navigateSales: () => bridge.Window?.NavigateSales(),
                navigateClients: () => bridge.Window?.NavigateClients(),
                navigateAbc: () => bridge.Window?.NavigateAbc(),
                navigatePayDebt: () => bridge.Window?.NavigatePayDebt(),
                navigateSettings: () => bridge.Window?.NavigateSettings(),
                navigateMarketplace: () => bridge.Window?.NavigateMarketplace(),
                navigateCrm: () => bridge.Window?.NavigateCrm(),
                navigateErrorLogs: () => bridge.Window?.NavigateErrorLogs(),
                navigateRemoteSupport: () => bridge.Window?.NavigateRemoteSupport(),
                navigateKnowledgeBase: () => bridge.Window?.NavigateKnowledgeBase(),
                navigateRestock: () => bridge.Window?.NavigateRestock(),
                switchCashier: () => bridge.Window?.SwitchCashierAsync() ?? Task.CompletedTask,
                logout: () => bridge.Window?.LogoutAsync() ?? Task.CompletedTask,
                exitApplication: () => bridge.Window?.ExitApplication(),
                permissions: sp.GetService<IPermissionService>());

            main = new MainWindowViewModel(
                toolbar, catalog, basket, sideMenu, session,
                sp.GetService<IUpdateCheckService>(),
                sp.GetService<AppSettings>());
            return main;
        });
    }

    private static CatalogProductTileVm? LookupCatalogProduct(string code)
    {
        var repo = LocalProductRepository.Instance;
        return repo.TryGetTileByBarcode(code) ?? repo.TryGetTileBySku(code);
    }

    /// <summary>Максимум для строки чека В ЕЁ СОБСТВЕННЫХ единицах. Остаток каталога хранится в
    /// единицах товара (пачках), а строка поштучной продажи из пачки считается в штуках — поэтому
    /// при <paramref name="salePackageId"/> остаток переводится в штуки. Без этого 3 пачки по 12
    /// давали лимит «3» для строки в штуках: кассир вводил 10 шт, и количество молча обрезалось
    /// до 3 — покупателю пробивали 3 штуки вместо 10.</summary>
    private static double? GetMaximumCartQuantity(string productId, string? salePackageId, BasketPanelViewModel basket)
    {
        var product = LocalProductRepository.Instance.TryGetTileBySku(productId)
            ?? CatalogCacheService.Products.FirstOrDefault(item =>
                string.Equals(item.Id, productId, StringComparison.OrdinalIgnoreCase));
        // Услуге остаток не нужен — «+» в строке чека не упирается в ноль на складе.
        if (product is null || product.IsService)
            return null;

        var reserved = StockAvailabilityService.CalculateReservedQuantity(productId)
            + basket.GetQuantityInOtherOpenReceipts(productId);
        var availableInStockUnits = Math.Max(0, product.Quantity - reserved);

        if (!string.IsNullOrWhiteSpace(salePackageId)
            && product.PieceOption is { } piece
            && string.Equals(piece.Id, salePackageId, StringComparison.OrdinalIgnoreCase)
            && piece.QuantityInPackage > 0)
        {
            return availableInStockUnits * piece.QuantityInPackage;
        }

        return availableInStockUnits;
    }
}
