using System.Windows.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>2026-11-21 («Базар»): окно кассы подключает сюда действия, которым нужны окна (торг по строке и по чеку). Свои, а не ещё один параметр конструктора корзины — чтобы не трогать общую сборку.</summary>
public static class BazaarHooks
{
    public static Func<CartLineItemVm, Task>? HaggleLine { get; set; }

    public static Func<Task>? HaggleCheck { get; set; }
}

public sealed partial class BasketPanelViewModel
{
    private ICommand? _haggleLineCommand;
    private ICommand? _haggleCheckCommand;

    /// <summary>Модуль «Торг» включён: у строки и у чека есть кнопки «Торг» (До 10 / 50 / 100, «Договорились за …»).</summary>
    public bool ShowHaggle => BazaarModules.HaggleOn;

    public ICommand HaggleLineCommand => _haggleLineCommand ??= new AsyncRelayCommand<CartLineItemVm>(
        line => line != null && BazaarHooks.HaggleLine != null ? BazaarHooks.HaggleLine(line) : Task.CompletedTask,
        line => line != null && HasItems && !IsBusy);

    public ICommand HaggleCheckCommand => _haggleCheckCommand ??= new AsyncRelayCommand(
        () => BazaarHooks.HaggleCheck != null ? BazaarHooks.HaggleCheck() : Task.CompletedTask,
        () => HasItems && !IsBusy);

    /// <summary>Цена по количеству (модуль <c>volume_price</c>): строкам чека ставится цена подходящей ступени. Вызывается перед сборкой строк экрана; без модуля — ничего не делает.</summary>
    private void ApplyBazaarVolumePrices()
    {
        if (!BazaarModules.VolumePriceOn || !_cart.HasCart || _cart.Root.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;
        try
        {
            ReceiptSnapshotCartEditor.ApplyVolumePrices(_cart, BazaarVolumePrices.Effective);
            var need = BazaarVolumePrices.NeedServer(
                CartDisplayHelper.EnumerateItems(_cart.Root)
                    .Where(ReceiptSnapshotCartEditor.IsPriceLevelEligible)
                    .Select(CartDisplayHelper.TryProductId)
                    .Where(id => !string.IsNullOrEmpty(id))!);
            if (need.Count > 0)
                _ = LoadBazaarVolumeTiersAsync(need);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Цена по количеству: не применена ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private async Task LoadBazaarVolumeTiersAsync(List<string> productIds)
    {
        try
        {
            if (await BazaarVolumePrices.LoadServerAsync(productIds).ConfigureAwait(false))
                await RunOnUiThreadAsync(RefreshFromCart).ConfigureAwait(false); // у товара появились ступени с сервера — пересчитать строки
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Цена по количеству: ступени с сервера не загрузились ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Подписи «Опт от N — цена» для строк чека, у которых цена стоит по ступени (id строки → подпись).</summary>
    private Dictionary<string, string> BazaarVolumeNotes()
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!BazaarModules.VolumePriceOn || !_cart.HasCart || _cart.Root.ValueKind != System.Text.Json.JsonValueKind.Object)
            return notes;
        foreach (var it in CartDisplayHelper.EnumerateItems(_cart.Root))
            if (ReceiptSnapshotCartEditor.LineVolumeMin(it) is { } min && CartDisplayHelper.TryItemId(it) is { } id)
                notes[id] = BazaarVolumePrices.Caption(new VolumeTier(min, (decimal)CartDisplayHelper.UnitPrice(it)));
        return notes;
    }
}
