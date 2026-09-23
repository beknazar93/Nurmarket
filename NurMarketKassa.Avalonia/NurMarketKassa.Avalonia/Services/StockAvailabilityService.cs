using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

public sealed class StockLineStatus
{
    public double Warehouse { get; init; }
    public double Reserved { get; init; }
    public double Available { get; init; }
    public double LineQty { get; init; }
    public bool IsInsufficient { get; init; }
}

/// <summary>Доступный остаток с учётом резерва в отложенных чеках (Avalonia).</summary>
public static class StockAvailabilityService
{
    /// <summary>2026-09-13: общий поиск плитки товара — раньше был только внутри
    /// GetWarehouseQuantity; теперь нужен и другим методам ниже, чтобы конвертировать строки
    /// поштучной продажи из упаковки в единицы остатка (см. CartDisplayHelper.
    /// LineQuantityInStockUnits) — иначе зарезервированное/находящееся в корзине количество
    /// считалось в штуках, а склад — в упаковках, что завышало "резерв" и могло ложно
    /// заблокировать добавление товара в чек.</summary>
    private static CatalogProductTileVm? ResolveTile(string productId) =>
        CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase))
        ?? LocalProductRepository.Instance.TryGetTileBySku(productId);

    public static double GetWarehouseQuantity(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return 0;

        return ResolveTile(productId)?.Quantity ?? 0;
    }

    public static double CalculateReservedQuantity(string productId, string? excludeDeferredEntryId = null)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return 0;

        var tile = ResolveTile(productId);
        double sum = 0;
        foreach (var entry in DeferredCartsStore.LoadAll())
        {
            if (!string.IsNullOrEmpty(excludeDeferredEntryId) &&
                string.Equals(entry.Id, excludeDeferredEntryId, StringComparison.Ordinal))
                continue;

            sum += SumProductQuantityInCartJson(entry.CartJson, productId, tile);
        }

        return sum;
    }

    public static double GetCurrentCartQuantity(string productId, ICartService cart)
    {
        if (string.IsNullOrWhiteSpace(productId) || !cart.HasCart)
            return 0;

        var tile = ResolveTile(productId);
        return CartDisplayHelper.EnumerateItems(cart.Root)
            .Where(it => string.Equals(CartDisplayHelper.TryProductId(it), productId, StringComparison.OrdinalIgnoreCase))
            .Sum(it => CartDisplayHelper.LineQuantityInStockUnits(it, tile));
    }

    public static double GetAvailableToAdd(
        string productId,
        ICartService cart,
        string? excludeDeferredEntryId = null,
        double additionalReserved = 0)
    {
        var warehouse = GetWarehouseQuantity(productId);
        var reserved = CalculateReservedQuantity(productId, excludeDeferredEntryId);
        var inCart = GetCurrentCartQuantity(productId, cart);
        return Math.Max(0, warehouse - reserved - Math.Max(0, additionalReserved) - inCart);
    }

    public static bool CanAddQuantity(
        string productId,
        double qtyToAdd,
        ICartService cart,
        string? excludeDeferredEntryId = null,
        double additionalReserved = 0)
    {
        if (qtyToAdd <= 0)
            return true;

        var available = GetAvailableToAdd(productId, cart, excludeDeferredEntryId, additionalReserved);
        return available + 1e-6 >= qtyToAdd;
    }

    public static StockLineStatus EvaluateCartLine(
        string productId,
        double lineQty,
        string? excludeDeferredEntryId = null,
        double additionalReserved = 0)
    {
        var warehouse = GetWarehouseQuantity(productId);
        var reserved = CalculateReservedQuantity(productId, excludeDeferredEntryId);
        var totalReserved = reserved + Math.Max(0, additionalReserved);
        var available = Math.Max(0, warehouse - totalReserved);
        var insufficient = lineQty > available + 1e-6;

        return new StockLineStatus
        {
            Warehouse = warehouse,
            Reserved = totalReserved,
            Available = available,
            LineQty = lineQty,
            IsInsufficient = insufficient,
        };
    }

    public static IReadOnlyList<(string Title, StockLineStatus Status)> EvaluateCurrentCart(
        ICartService cart,
        string? excludeDeferredEntryId = null,
        Func<string, double>? additionalReservedLookup = null)
    {
        if (!cart.HasCart)
            return Array.Empty<(string, StockLineStatus)>();

        var issues = new List<(string, StockLineStatus)>();
        foreach (var it in CartDisplayHelper.EnumerateItems(cart.Root))
        {
            var productId = CartDisplayHelper.TryProductId(it);
            if (string.IsNullOrEmpty(productId))
                continue;

            var qty = CartDisplayHelper.LineQuantityInStockUnits(it, ResolveTile(productId));
            var additionalReserved = additionalReservedLookup?.Invoke(productId) ?? 0;
            var status = EvaluateCartLine(productId, qty, excludeDeferredEntryId, additionalReserved);
            if (status.IsInsufficient)
                issues.Add((CartDisplayHelper.ItemName(it), status));
        }

        return issues;
    }

    private static double SumProductQuantityInCartJson(string cartJson, string productId, CatalogProductTileVm? tile)
    {
        try
        {
            return OpenReceiptSnapshot.SumProductQuantity(cartJson, productId, tile);
        }
        catch
        {
            return 0;
        }
    }
}
