using System.Linq;
using System.Text.Json;
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

    /// <summary>Состояние серверной корзины при сверке «прошла ли уже оплата».</summary>
    public enum CartCheckoutState { Paid, Open, Missing, Unknown }

    /// <summary>Прошла ли оплата по корзине. 2026-09-26, найдено стресс-тестом: обе сверки
    /// (обычная оплата после тайм-аута и досылка офлайн-чека) спрашивали api/main/pos/sales/{id}/
    /// — а корзина не продажа, этот адрес на ID корзины ВСЕГДА отвечает 404 «No Sale matches».
    /// Защита от двойной продажи не срабатывала ни разу: после тайм-аута оплата считалась
    /// непрошедшей и уходила в очередь повторно, а досылка с потерянным ответом крутилась вечно.
    /// Правильный адрес — api/main/pos/carts/{id}/: оплаченная корзина остаётся там со статусом
    /// «checked_out», неоплаченная — «open» (проверено на живых корзинах тестового аккаунта).
    /// 404 — корзины больше нет (сервер убирает незакрытые корзины при закрытии смены);
    /// оплаченной она быть не может — оплаченные остаются.</summary>
    public static async Task<CartCheckoutState> GetCheckoutStateAsync(
        ISalesApiService api,
        string? cartId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cartId))
            return CartCheckoutState.Missing;

        try
        {
            var cart = await api.PosCartGetAsync(cartId, cancellationToken).ConfigureAwait(false);
            var status = cart.ValueKind == JsonValueKind.Object
                         && cart.TryGetProperty("status", out var statusEl)
                         && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString() ?? ""
                : "";
            var state = status.ToLowerInvariant() switch
            {
                "checked_out" or "paid" or "completed" or "closed" => CartCheckoutState.Paid,
                "open" or "new" or "draft" => CartCheckoutState.Open,
                _ => CartCheckoutState.Unknown,
            };
            PosLogger.Log($"Checkout reconciliation for {cartId}: status={status}, state={state}", "PAYMENT");
            return state;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            PosLogger.Log($"Checkout reconciliation for {cartId}: корзины на сервере больше нет.", "PAYMENT");
            return CartCheckoutState.Missing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            PosLogger.Log($"Checkout reconciliation check failed for {cartId}: {ex.Message}", "PAYMENT");
            return CartCheckoutState.Unknown;
        }
    }

    /// <summary>Удаляет все позиции в серверной корзине по её ID — для досылки офлайн-чеков, у
    /// которой нет локальной сессии корзины. Возвращает, сколько позиций пришлось удалить.</summary>
    public static async Task<int> EnsureServerCartEmptyAsync(
        ISalesApiService api,
        string cartId,
        CancellationToken cancellationToken = default)
    {
        var removed = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            var fresh = await api.PosCartGetAsync(cartId, cancellationToken).ConfigureAwait(false);
            var items = CartDisplayHelper.EnumerateItems(fresh).ToList();
            if (items.Count == 0)
                return removed;

            foreach (var it in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemId = CartDisplayHelper.TryItemId(it);
                if (string.IsNullOrEmpty(itemId))
                    continue;
                await api.PosCartItemDeleteAsync(cartId, itemId, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        }

        return removed;
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
