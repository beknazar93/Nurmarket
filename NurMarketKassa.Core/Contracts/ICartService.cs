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
    decimal? FixedDiscountAmount = null);

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
    void UpdateQuantity(string itemId, double quantity);
    void RemoveItem(string itemId);
    void ResetForNewReceipt();
}
