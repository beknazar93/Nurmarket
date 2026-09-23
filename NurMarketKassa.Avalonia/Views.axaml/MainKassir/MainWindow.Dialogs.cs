using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

public partial class MainWindow
{
    private ICartService? _cartService;
    private IDeferredCartService? _deferredCartService;

    private void WireDialogBridge()
    {
        _hostBridge.AddProductFromCatalog = AddProductFromCatalogAsync;
        _hostBridge.OpenDeferredCarts = OpenDeferredCartsAsync;
        _hostBridge.OpenCashOperations = OpenCashOperationsAsync;
        _hostBridge.ApplyOrderDiscount = ApplyOrderDiscountAsync;
        _hostBridge.ReweighCartLine = ReweighCartLineAsync;
        _hostBridge.ApplyLineDiscount = ApplyLineDiscountAsync;
    }

    internal decimal? GetCurrentBalance() => _shiftCashBalance;

    internal async Task<bool> OpenShiftFromCoordinatorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await OpenShiftAsync().ConfigureAwait(true);
        return _session.IsShiftOpen;
    }

    internal Task OpenDeferredCartsAsync()
    {
        var dlg = new DeferredCartsDialog(new DeferredCartsDialogActions
        {
            MergeIntoCurrentAsync = MergeDeferredIntoCurrentAsync,
            OpenAsSeparateAsync = OpenDeferredAsSeparateAsync,
        });
        PosDialogHost.Show(dlg, this);
        _viewModel.Basket.RefreshFromCart();
        return Task.CompletedTask;
    }

    internal Task OpenCashOperationsAsync()
    {
        var dlg = App.GetRequiredService<CashOperationsDialog>();
        dlg.OpenShiftAction = async cash => await ApplyShiftOpenedAsync(cash).ConfigureAwait(true);
        dlg.CloseShiftAction = async cash => await ApplyShiftClosedAsync(cash).ConfigureAwait(true);
        PosDialogHost.Show(dlg, this);
        return Task.CompletedTask;
    }

    internal async Task AddProductFromCatalogAsync(CatalogProductTileVm vm)
    {
        if (!_session.IsShiftOpen)
        {
            await OpenShiftAsync().ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                return;
        }

        var cart = ResolveCartService();
        double qtyToAdd;
        var mustWeigh = ProductUnitNormalizer.RequiresWeighing(vm);

        if (mustWeigh)
        {
            var scale = HardwareModeHelper.UsePhysicalScale()
                ? App.GetRequiredService<ScaleWeightProvider>().Scale
                : null;
            var dlg = new WeighedProductDialog(vm.Title, vm.PriceLine, scale);
            if (PosDialogHost.Show(dlg, this) != true || string.IsNullOrEmpty(dlg.QuantityNormalized))
                return;

            if (!double.TryParse(dlg.QuantityNormalized, NumberStyles.Any, CultureInfo.InvariantCulture, out qtyToAdd) || qtyToAdd <= 0)
                return;
        }
        else
        {
            qtyToAdd = ParseManualQuantity(_viewModel.Basket.ManualQuantity, false);
            if (qtyToAdd <= 0)
            {
                PosMessageBox.Show(this, "Укажите корректное количество.", "Количество",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var reservedInOtherOpenReceipts = _viewModel.Basket.GetQuantityInOtherOpenReceipts(vm.Id);
        if (!StockAvailabilityService.CanAddQuantity(
                vm.Id,
                qtyToAdd,
                cart,
                additionalReserved: reservedInOtherOpenReceipts))
        {
            await ShowNoStockBlockedAsync(vm.Title, vm.Id, mustWeigh).ConfigureAwait(true);
            return;
        }

        _viewModel.Basket.ManualQuantity = mustWeigh
            ? qtyToAdd.ToString("0.###", CultureInfo.InvariantCulture)
            : qtyToAdd.ToString("0", CultureInfo.InvariantCulture);
        _viewModel.Basket.AddProductFromCatalog(vm);
    }

    internal Task ApplyOrderDiscountAsync()
    {
        if (!Authorize(PosPermissions.ApplyDiscount))
            return Task.CompletedTask;
        var dlg = App.GetRequiredService<OrderDiscountDialog>();
        if (PosDialogHost.Show(dlg, this) != true)
            return Task.CompletedTask;

        if (dlg.ClearRequested)
        {
            if (_viewModel.Basket.ApplyOrderDiscount(null, null, clear: true))
                _viewModel.Basket.CartMessage = "Скидка сброшена.";
            return Task.CompletedTask;
        }

        if (_viewModel.Basket.ApplyOrderDiscount(dlg.DiscountMode, dlg.DiscountValue))
        {
            _viewModel.Basket.CartMessage = dlg.DiscountMode == "percent"
                ? $"Скидка {dlg.DiscountValue}% применена."
                : $"Скидка {dlg.DiscountValue} сом применена.";
        }
        return Task.CompletedTask;
    }

    internal Task ReweighCartLineAsync(CartLineItemVm line)
    {
        if (!Authorize(PosPermissions.ViewScales))
            return Task.CompletedTask;
        if (!line.IsWeight || string.IsNullOrWhiteSpace(line.ItemId))
            return Task.CompletedTask;

        var scale = HardwareModeHelper.UsePhysicalScale()
            ? App.GetRequiredService<ScaleWeightProvider>().Scale
            : null;
        var dialog = new WeighedProductDialog(
            line.Title,
            $"{line.UnitPrice:0.00} сом",
            scale,
            line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
            "Обновить");

        if (PosDialogHost.Show(dialog, this) != true ||
            !double.TryParse(dialog.QuantityNormalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var quantity) ||
            quantity <= 0)
            return Task.CompletedTask;

        ResolveCartService().UpdateQuantity(line.ItemId, quantity);
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = $"Вес «{line.Title}» обновлён: {quantity:0.###} кг.";
        return Task.CompletedTask;
    }

    internal Task ApplyLineDiscountAsync(CartLineItemVm line)
    {
        if (!Authorize(PosPermissions.ApplyDiscount))
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(line.ItemId))
            return Task.CompletedTask;

        var cart = ResolveCartService();
        var (mode, value) = ReadLineDiscount(cart, line.ItemId);
        var dialog = App.GetRequiredService<OrderDiscountDialog>();
        dialog.SetItemMode(line.Title, mode, value);
        if (PosDialogHost.Show(dialog, this) != true)
            return Task.CompletedTask;

        ReceiptSnapshotCartEditor.PatchLineDiscount(
            cart,
            line.ItemId,
            dialog.ClearRequested ? null : dialog.DiscountMode,
            dialog.ClearRequested ? null : dialog.DiscountValue);
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = dialog.ClearRequested
            ? $"Скидка на «{line.Title}» удалена."
            : $"Скидка на «{line.Title}» применена.";
        return Task.CompletedTask;
    }

    private static (string? Mode, decimal? Value) ReadLineDiscount(ICartService cart, string itemId)
    {
        foreach (var item in CartDisplayHelper.EnumerateItems(cart.Root))
        {
            if (!string.Equals(CartDisplayHelper.TryItemId(item), itemId, StringComparison.Ordinal))
                continue;

            if (item.TryGetProperty("discount_percent", out var percent) &&
                JsonNumericReader.TryToDouble(percent, out var percentValue))
                return ("percent", (decimal)percentValue);
            if (item.TryGetProperty("discount_total", out var total) &&
                JsonNumericReader.TryToDouble(total, out var totalValue))
                return ("sum", (decimal)totalValue);
            break;
        }

        return (null, null);
    }

    private async Task<bool> MergeDeferredIntoCurrentAsync(IReadOnlyList<DeferredCartEntry> entries)
    {
        if (entries.Count == 0)
            return false;

        if (!_session.IsShiftOpen)
        {
            await OpenShiftAsync().ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                return false;
        }

        foreach (var entry in entries)
        {
            if (!await AddDeferredEntryItemsToActiveCartAsync(entry).ConfigureAwait(true))
                return false;

            DeferredCartsStore.RemoveIds(new[] { entry.Id });
        }

        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = $"Позиции из {entries.Count} отложенных чеков добавлены в текущий чек.";
        return true;
    }

    private async Task<bool> OpenDeferredAsSeparateAsync(DeferredCartEntry entry)
    {
        if (!_session.IsShiftOpen)
        {
            await OpenShiftAsync().ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                return false;
        }

        if (!await ValidateDeferredEntryStockAsync(entry).ConfigureAwait(true))
            return false;

        var cart = ResolveCartService();
        if (cart.HasCart && cart.LineCount > 0)
        {
            var deferResult = await ResolveDeferredCartService()
                .DeferCurrentCartAsync(startNewSale: false)
                .ConfigureAwait(true);
            if (!deferResult.IsSuccess)
            {
                PosMessageBox.Show(this, deferResult.ErrorMessage ?? "Не удалось сохранить текущий чек.",
                    "Отложенные", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        StagingCartService.StartEmpty(cart);
        OpenReceiptSnapshot.ApplyDeferredStaging(cart, entry.CartJson);
        DeferredCartsStore.RemoveIds(new[] { entry.Id });
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = $"Открыт отложенный чек «{entry.Label}».";
        return true;
    }

    private async Task<bool> AddDeferredEntryItemsToActiveCartAsync(
        DeferredCartEntry entry,
        bool applyOrderDiscount = false)
    {
        if (!await ValidateDeferredEntryStockAsync(entry).ConfigureAwait(true))
            return false;

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
        var root = doc.RootElement;
        var lines = CartDisplayHelper.EnumerateItems(root).ToList();
        if (lines.Count == 0)
            return true;

        var cart = ResolveCartService();
        if (!cart.HasCart)
            StagingCartService.StartEmpty(cart);

        foreach (var line in lines)
        {
            var productId = CartDisplayHelper.TryProductId(line);
            if (string.IsNullOrEmpty(productId))
                continue;

            var qty = CartDisplayHelper.LineQuantity(line);
            if (qty <= 0)
                continue;

            var product = ResolveCatalogProductForCartLine(line, productId);
            if (product == null)
                continue;

            cart.AddItem(product, qty);
        }

        if (applyOrderDiscount)
            ApplyDeferredOrderDiscount(cart, root);

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ValidateDeferredEntryStockAsync(DeferredCartEntry entry)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
        var requestedProducts = CartDisplayHelper.EnumerateItems(doc.RootElement)
            .Select(line => new
            {
                ProductId = CartDisplayHelper.TryProductId(line),
                Quantity = CartDisplayHelper.LineQuantity(line),
                Title = CartDisplayHelper.ItemName(line),
                MustWeigh = CartDisplayHelper.LineMustWeigh(line),
            })
            .Where(line => !string.IsNullOrWhiteSpace(line.ProductId) && line.Quantity > 0)
            .GroupBy(line => line.ProductId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ProductId = group.Key,
                Quantity = group.Sum(line => line.Quantity),
                Title = group.First().Title,
                MustWeigh = group.First().MustWeigh,
            });

        var cart = ResolveCartService();
        foreach (var product in requestedProducts)
        {
            var otherOpenReceipts = _viewModel.Basket.GetQuantityInOtherOpenReceipts(product.ProductId);
            if (StockAvailabilityService.CanAddQuantity(
                    product.ProductId,
                    product.Quantity,
                    cart,
                    excludeDeferredEntryId: entry.Id,
                    additionalReserved: otherOpenReceipts))
                continue;

            await ShowNoStockBlockedAsync(
                    product.Title,
                    product.ProductId,
                    product.MustWeigh,
                    excludeDeferredEntryId: entry.Id)
                .ConfigureAwait(true);
            return false;
        }

        return true;
    }

    private static void ApplyDeferredOrderDiscount(ICartService cart, JsonElement cartRoot)
    {
        if (cartRoot.ValueKind != JsonValueKind.Object)
            return;

        var pct = cartRoot.TryGetProperty("order_discount_percent", out var p)
            ? FormatDiscountScalar(p)
            : "";
        var sum = cartRoot.TryGetProperty("order_discount_total", out var t)
            ? FormatDiscountMoney(t)
            : "";

        if (string.IsNullOrEmpty(pct) && string.IsNullOrEmpty(sum))
            return;

        ReceiptSnapshotCartEditor.PatchOrderDiscount(cart, pct, sum);
    }

    private static string FormatDiscountScalar(JsonElement value) =>
        JsonNumericReader.ToDouble(value).ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatDiscountMoney(JsonElement value) =>
        CartDisplayHelper.FormatMoney(JsonNumericReader.ToDouble(value));

    private static CatalogProductTileVm? ResolveCatalogProductForCartLine(JsonElement line, string productId)
    {
        var fromCache = LocalProductRepository.Instance.TryGetTileBySku(productId)
            ?? CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (fromCache != null)
            return fromCache;

        var title = CartDisplayHelper.ItemName(line);
        if (string.IsNullOrWhiteSpace(title))
            title = productId;

        var price = CartDisplayHelper.FormatMoney(CartDisplayHelper.UnitPrice(line));
        var mustWeigh = CartDisplayHelper.LineMustWeigh(line);
        return new CatalogProductTileVm(productId, title, price + " сом", mustWeigh);
    }

    private async Task ShowNoStockBlockedAsync(
        string productName,
        string productId,
        bool mustWeigh,
        string? excludeDeferredEntryId = null)
    {
        var warehouse = StockAvailabilityService.GetWarehouseQuantity(productId);
        var deferred = StockAvailabilityService.CalculateReservedQuantity(productId, excludeDeferredEntryId);
        var otherOpenReceipts = _viewModel.Basket.GetQuantityInOtherOpenReceipts(productId);
        var reservedElsewhere = deferred + otherOpenReceipts;
        var cart = ResolveCartService();
        var inCurrentCart = StockAvailabilityService.GetCurrentCartQuantity(productId, cart);
        var available = StockAvailabilityService.GetAvailableToAdd(
            productId,
            cart,
            excludeDeferredEntryId,
            additionalReserved: otherOpenReceipts);

        var dialog = new NoStockDialog(
            productName,
            warehouse,
            inCurrentCart,
            reservedElsewhere,
            available,
            mustWeigh);
        var owner = PosDialogHost.ResolveOwner(this);
        await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true);
    }

    private ICartService ResolveCartService() =>
        _cartService ??= App.GetRequiredService<ICartService>();

    private IDeferredCartService ResolveDeferredCartService() =>
        _deferredCartService ??= App.GetRequiredService<IDeferredCartService>();

    private static double ParseManualQuantity(string raw, bool mustWeigh)
    {
        raw = (raw ?? "1").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty))
            return 0;

        return mustWeigh ? Math.Round(qty, 3) : Math.Round(qty, 0);
    }
}
