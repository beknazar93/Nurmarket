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
            {
                // «Доп. услуга» (2026-09-07): строка без товара — отдельный запрос custom-item.
                if (CartDisplayHelper.IsCustomLine(it))
                {
                    await api.PosAddCustomItemAsync(
                        serverCartId,
                        CartDisplayHelper.ItemName(it),
                        CartDisplayHelper.UnitPrice(it),
                        CartDisplayHelper.LineQuantity(it),
                        cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var qty = CartDisplayHelper.LineQuantity(it);
            var qtyStr = CartDisplayHelper.LineMustWeigh(it)
                // Без TrimEnd — см. StagingCartService: у целого количества формат не печатает
                // точку, и TrimEnd('0') превращал 10 в 1.
                ? qty.ToString("0.###", CultureInfo.InvariantCulture)
                : Math.Round(qty, 0).ToString(CultureInfo.InvariantCulture);

            await api.PosAddItemAsync(serverCartId, productId, qtyStr, ct: cancellationToken).ConfigureAwait(false);
        }

        var fresh = await api.PosCartGetAsync(serverCartId, cancellationToken).ConfigureAwait(false);
        cart.SetCart(fresh);
        return true;
    }
}
