using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;

namespace NurMarketKassa.Services;

/// <summary>
/// Общая реализация отложенных чеков: сохранение снимка корзины и открытие нового чека.
/// </summary>
public sealed class DeferredCartService : IDeferredCartService
{
    private readonly ICartService _cart;
    private readonly IShiftStateService _shiftState;

    public DeferredCartService(ICartService cart, IShiftStateService shiftState)
    {
        _cart = cart;
        _shiftState = shiftState;
    }

    public int PendingCount => DeferredCartsStore.LoadAll().Count;

    public async Task<DeferredCartResult> DeferCurrentCartAsync(
        string? label = null,
        bool startNewSale = true,
        CancellationToken cancellationToken = default)
    {
        if (!_cart.HasCart || _cart.LineCount == 0)
            return DeferredCartResult.Failed("Корзина пуста — нечего откладывать.");

        if (startNewSale)
        {
            await _shiftState.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!_shiftState.IsShiftOpen)
                return DeferredCartResult.Failed("Смена не открыта — откройте смену перед откладыванием чека.");
        }

        var snapshot = OpenReceiptSnapshot.Capture(_cart);
        var cartJson = OpenReceiptSnapshot.CloneCartJson(snapshot.CartJson);
        var entryLabel = string.IsNullOrWhiteSpace(label) ? BuildDeferredCartLabel() : label.Trim();

        var entry = new DeferredCartEntry
        {
            Label = entryLabel,
            CartJson = cartJson,
            ServerCartId = null,
        };

        DeferredCartsStore.Add(entry);
        PosLogger.Log($"RECEIPT defer: id={entry.Id}, startNewSale={startNewSale}", "RECEIPT");

        if (startNewSale)
        {
            _cart.Clear();
            StagingCartService.StartEmpty(_cart);
        }

        return DeferredCartResult.Succeeded(entry.Label);
    }

    private static string BuildDeferredCartLabel()
    {
        var count = DeferredCartsStore.LoadAll().Count + 1;
        return $"Отложенный #{count} ({DateTime.Now:HH:mm})";
    }
}
