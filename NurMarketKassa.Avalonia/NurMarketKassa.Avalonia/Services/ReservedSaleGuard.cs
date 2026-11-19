using System.Windows;
using Avalonia.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-11-19 (склад «10 из 10», К-8): продажа сверх СВОБОДНОГО остатка (остаток минус резервы). Политика — settings/stock/ reserved_sale_policy: warn — «Продать всё равно / Отмена»,
// block — только сообщение. Работает только при признаке reservations, полученных настройках склада и поле available у товара; иначе всегда «можно» (касса как раньше).
public static class ReservedSaleGuard
{
    /// <summary>true — товар можно добавить. <paramref name="wanted"/> — сколько этого товара будет в чеке вместе с добавляемым. Ничего не бросает: ошибка = «можно».</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, CatalogProductTileVm vm, double wanted)
    {
        try
        {
            if (!ServerStock.ReservationsOn || ServerStock.CachedPolicies is not { } policies || vm.Available is not { } free)
                return true;
            var decision = ServerStock.OverAvailable(policies.ReservedSalePolicy, free, wanted);
            if (decision == StockSaleDecision.Ok)
                return true;
            var text = ServerStock.OverAvailableText(vm.Title, free, vm.Reserved ?? 0);
            var caption = Tr.T("Товар в резерве", "Товар резервде", "Goods on reserve", "Ürün rezervde", "Tovar zaxirada");
            if (decision == StockSaleDecision.Block)
            {
                PosLogger.Log($"Резерв: «{vm.Title}» — продажа сверх свободного ({free:0.###}, нужно {wanted:0.###}) запрещена политикой склада (block).", "INFO");
                await PosMessageBox.ShowModalAsync(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.Warning).ConfigureAwait(true);
                return false;
            }
            var sell = await PosDialogs.ConfirmYesNoModalAsync(owner, text, caption,
                confirmText: Tr.T("Продать всё равно", "Баары бир сатуу", "Sell anyway", "Yine de sat", "Baribir sotish"),
                cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), danger: true, enterCancels: true).ConfigureAwait(true);
            PosLogger.Log($"Резерв: «{vm.Title}» — сверх свободного ({free:0.###}, нужно {wanted:0.###}), политика warn, кассир {(sell ? "продал" : "отменил")}.", "INFO");
            return sell;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Резерв: проверка не выполнена ({ex.GetType().Name}: {ex.Message}) — товар добавлен.", "WARNING");
            return true;
        }
    }
}
