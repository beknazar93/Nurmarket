using NurMarketKassa.Models.Pos;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Deferred host callbacks so main-window ViewModels can invoke UI actions without circular DI.</summary>
public sealed class MainWindowHostBridge
{
    public Views.MainKassir.MainWindow? Window { get; set; }

    /// <summary>Второй параметр — комбинированное название строки при сканировании доп.
    /// штрихкода варианта (2026-09-21, см. BasketPanelViewModel.ResolveVariantLineName), null
    /// для обычного добавления.</summary>
    public Func<CatalogProductTileVm, string?, Task>? AddProductFromCatalog { get; set; }

    /// <summary>Штрих-код весов со встроенным весом уже несёт вес в самом коде — диалог
    /// взвешивания в этом случае не нужен, вес известен заранее.</summary>
    public Func<CatalogProductTileVm, double, Task>? AddWeighedProductWithKnownWeight { get; set; }

    public Func<Task>? OpenDeferredCarts { get; set; }

    public Func<Task>? OpenNextDeferredCart { get; set; }

    public Func<Task>? OpenCashOperations { get; set; }

    public Func<Task>? ApplyOrderDiscount { get; set; }

    /// <summary>«Доп. услуга» — диалог названия/суммы/количества и типа доход/расход (2026-09-07).</summary>
    public Func<Task>? AddCustomItem { get; set; }

    /// <summary>Неизвестный штрих-код при сканировании — «Добавить на склад / Пропустить» (2026-09-07).</summary>
    public Func<string, Task>? OfferAddUnknownProduct { get; set; }

    public Func<CartLineItemVm, Task>? ReweighCartLine { get; set; }

    public Func<CartLineItemVm, Task>? ApplyLineDiscount { get; set; }

    /// <summary>Пополнение склада при упоре в остаток ПРЯМО В ЧЕКЕ (кнопка «+» или ручной
    /// ввод количества в строке). Параметры: id товара, сколько всего нужно, весовой ли товар.
    /// true — склад реально пополнен инвентаризационным актом и количество можно ставить.
    ///
    /// 2026-09-22: на пути «добавить из каталога» это предложение было всегда, а внутри уже
    /// собранного чека — нет: кассир упирался в «Достигнут лимит остатка» с единственной
    /// кнопкой «Закрыть». Обработчик один и тот же (TryReplenishStockForOverrideAsync).</summary>
    public Func<string, double, bool, Task<bool>>? ReplenishStockForProduct { get; set; }

    public Func<string, double>? GetOtherOpenReceiptQuantity { get; set; }

    public void TryAddProduct(CatalogProductTileVm product)
    {
        if (AddProductFromCatalog != null)
            _ = AddProductFromCatalog(product, null);
    }
}
