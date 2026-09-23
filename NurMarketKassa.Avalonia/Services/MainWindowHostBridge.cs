using NurMarketKassa.Models.Pos;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Deferred host callbacks so main-window ViewModels can invoke UI actions without circular DI.</summary>
public sealed class MainWindowHostBridge
{
    public Views.MainKassir.MainWindow? Window { get; set; }

    public Func<CatalogProductTileVm, Task>? AddProductFromCatalog { get; set; }

    public Func<Task>? OpenDeferredCarts { get; set; }

    public Func<Task>? OpenCashOperations { get; set; }

    public Func<Task>? ApplyOrderDiscount { get; set; }

    public Func<CartLineItemVm, Task>? ReweighCartLine { get; set; }

    public Func<CartLineItemVm, Task>? ApplyLineDiscount { get; set; }

    public Func<string, double>? GetOtherOpenReceiptQuantity { get; set; }

    public void TryAddProduct(CatalogProductTileVm product)
    {
        if (AddProductFromCatalog != null)
            _ = AddProductFromCatalog(product);
    }
}
