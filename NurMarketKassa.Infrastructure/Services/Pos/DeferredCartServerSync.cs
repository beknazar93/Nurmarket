using System.Globalization;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Синхронизация локального снимка отложенного чека с сервером перед оплатой.</summary>
public static class DeferredCartServerSync
{
    public static async Task<bool> PushSnapshotToServerAsync(
        ISalesApiService api,
        ICartService cart,
        string serverCartId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverCartId))
            return false;

        var localItems = CartDisplayHelper.EnumerateItems(cart.Root).ToList();
        if (localItems.Count == 0)
            return true;

        var serverCart = await api.PosCartGetAsync(serverCartId, cancellationToken).ConfigureAwait(false);
        foreach (var it in CartDisplayHelper.EnumerateItems(serverCart).ToList())
        {
            var itemId = CartDisplayHelper.TryItemId(it);
            if (!string.IsNullOrEmpty(itemId))
                await api.PosCartItemDeleteAsync(serverCartId, itemId, cancellationToken).ConfigureAwait(false);
        }

        foreach (var it in localItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var productId = CartDisplayHelper.TryProductId(it);
            if (string.IsNullOrEmpty(productId))
                continue;

            var qty = CartDisplayHelper.LineQuantity(it);
            var qtyStr = CartDisplayHelper.LineMustWeigh(it)
                ? qty.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.')
                : Math.Round(qty, 0).ToString(CultureInfo.InvariantCulture);

            await api.PosAddItemAsync(serverCartId, productId, qtyStr, ct: cancellationToken).ConfigureAwait(false);
        }

        var fresh = await api.PosCartGetAsync(serverCartId, cancellationToken).ConfigureAwait(false);
        cart.SetCart(fresh);
        return true;
    }
}
