using System.Linq;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Старт продажи и гарантированно пустая серверная корзина для активного чека.</summary>
public static class CartSaleSessionHelper
{
    /// <summary>
    /// POST sales/start и привязка ответа к сессии. Затем всегда подтягивает полную корзину с сервера
    /// и удаляет все позиции (ответ start часто не содержит items, из‑за чего старые строки «утекали» в новый чек).
    /// </summary>
    public static async Task StartNewSaleAsync(
        ISalesApiService api,
        ICartService session,
        string? cashboxId,
        CancellationToken cancellationToken = default)
    {
        var cart = await api.PosSalesStartAsync(
            string.IsNullOrWhiteSpace(cashboxId) ? null : cashboxId,
            cancellationToken).ConfigureAwait(false);
        session.SetCart(cart);

        await EnsureServerCartEmptyAsync(api, session, cancellationToken).ConfigureAwait(false);

        if (!session.CanRefresh)
            throw new ApiException("Не удалось открыть новый чек: сервер не вернул идентификатор корзины.", 409);
    }

    /// <summary>Удаляет все позиции в текущей серверной корзине и обновляет локальную сессию.</summary>
    public static async Task EnsureServerCartEmptyAsync(
        ISalesApiService api,
        ICartService session,
        CancellationToken cancellationToken = default)
    {
        if (!session.CanRefresh || string.IsNullOrEmpty(session.CartId))
            return;

        var cartId = session.CartId!;
        for (var pass = 0; pass < 2; pass++)
        {
            var fresh = await api.PosCartGetAsync(cartId, cancellationToken).ConfigureAwait(false);
            var items = CartDisplayHelper.EnumerateItems(fresh).ToList();
            if (items.Count == 0)
            {
                session.SetCart(fresh);
                return;
            }

            foreach (var it in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemId = CartDisplayHelper.TryItemId(it);
                if (!string.IsNullOrEmpty(itemId))
                    await api.PosCartItemDeleteAsync(cartId, itemId, cancellationToken).ConfigureAwait(false);
            }
        }

        session.SetCart(await api.PosCartGetAsync(cartId, cancellationToken).ConfigureAwait(false));
    }
}
