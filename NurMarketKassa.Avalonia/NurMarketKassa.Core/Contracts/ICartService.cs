using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Interfaces;

/// <summary>Неизменяемый снимок одной позиции чека для отображения и расчётов.</summary>
public sealed record CartItem(
    string? Id,
    string? ProductId,
    string? Barcode,
    string Name,
    double Quantity,
    decimal UnitPrice,
    decimal LineDiscount,
    decimal LineTotal,
    bool MustWeigh,
    decimal? DiscountPercent = null,
    decimal? FixedDiscountAmount = null,
    /// <summary>ID упаковки при поштучной продаже из пачки. Нужен, чтобы отличить строку,
    /// количество которой считается в ШТУКАХ, от обычной, где оно в единицах каталога —
    /// иначе остаток в пачках сравнивается со штуками (см. GetMaximumCartQuantity).</summary>
    string? SalePackageId = null);

/// <summary>
/// Этот файл описывает контракт работы с корзиной покупателя:
/// добавление товаров, изменение количества, удаление позиций и расчёт итоговой суммы.
/// </summary>
public interface ICartService
{
    string? CartId { get; }
    bool IsLocalOffline { get; }
    bool IsStaging { get; }
    bool HasCart { get; }
    bool CanRefresh { get; }
    JsonElement Root { get; }
    string GetRawText();
    IReadOnlyList<CartItem> Items { get; }
    int LineCount { get; }
    double TotalQuantity { get; }
    decimal TotalAmount { get; }
    decimal TotalDiscount { get; }
    void SetCart(JsonElement root);
    void SetLocalOfflineCart(string cartJson);
    void Clear();
    void AddItem(CatalogProductTileVm product, double quantity);
    /// <summary>Добавление с переопределённым названием строки — сканирование доп. штрихкода
    /// варианта товара (2026-09-21): название строки чека — «Название товара + название
    /// варианта» вместо обычного названия товара, как на сайте.</summary>
    void AddItem(CatalogProductTileVm product, double quantity, string? nameOverride);
    /// <summary>Добавление с собственной ценой строки — поштучная продажа из упаковки,
    /// где цена за единицу отличается от базовой цены товара.</summary>
    void AddItem(CatalogProductTileVm product, double quantity, double unitPriceOverride);
    /// <summary>Поштучная продажа из конкретной упаковки товара: salePackageId уходит на
    /// сервер как "sale_package_id" при оформлении (см. StagingCartService) — сервер сам
    /// подставляет цену пачки, минуя проверку "цена не ниже закупочной", которая иначе
    /// применяется при ручном оверрайде unit_price (см. историю в ReceiptSnapshotCartEditor).</summary>
    void AddItem(CatalogProductTileVm product, double quantity, double unitPriceOverride, string? salePackageId);
    /// <summary>«Доп. услуга» (2026-09-07): строка чека без товара — название, цена за единицу
    /// (отрицательная = «Расход», вычитается из чека) и количество. См. ReceiptSnapshotCartEditor.AddCustomItem.</summary>
    void AddCustomItem(string name, double unitPrice, double quantity);

    void UpdateQuantity(string itemId, double quantity);
    void RemoveItem(string itemId);
    void ResetForNewReceipt();
}
