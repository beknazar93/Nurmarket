using NurMarketKassa.Core.Contracts;

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
    public static double GetWarehouseQuantity(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return 0;

        var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase))
            ?? LocalProductRepository.Instance.TryGetTileBySku(productId);
        return tile?.Quantity ?? 0;
    }

    public static double CalculateReservedQuantity(string productId, string? excludeDeferredEntryId = null)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return 0;

        double sum = 0;
        foreach (var entry in DeferredCartsStore.LoadAll())
        {
            if (!string.IsNullOrEmpty(excludeDeferredEntryId) &&
                string.Equals(entry.Id, excludeDeferredEntryId, StringComparison.Ordinal))
                continue;

            sum += SumProductQuantityInCartJson(entry.CartJson, productId);
        }

        return sum;
    }

    public static double GetCurrentCartQuantity(string productId, ICartService cart)
    {
        if (string.IsNullOrWhiteSpace(productId) || !cart.HasCart)
            return 0;

        return CartDisplayHelper.EnumerateItems(cart.Root)
            .Where(it => string.Equals(CartDisplayHelper.TryProductId(it), productId, StringComparison.OrdinalIgnoreCase))
            .Sum(CartDisplayHelper.LineQuantity);
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

            var qty = CartDisplayHelper.LineQuantity(it);
            var additionalReserved = additionalReservedLookup?.Invoke(productId) ?? 0;
            var status = EvaluateCartLine(productId, qty, excludeDeferredEntryId, additionalReserved);
            if (status.IsInsufficient)
                issues.Add((CartDisplayHelper.ItemName(it), status));
        }

        return issues;
    }

    private static double SumProductQuantityInCartJson(string cartJson, string productId)
    {
        try
        {
            return OpenReceiptSnapshot.SumProductQuantity(cartJson, productId);
        }
        catch
        {
            return 0;
        }
    }
}
