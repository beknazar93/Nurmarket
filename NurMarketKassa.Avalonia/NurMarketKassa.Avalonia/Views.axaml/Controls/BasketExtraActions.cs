using Avalonia.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>Действия чека, у которых нет команды во ViewModel (нужны окно-владелец и кнопка):
/// «Списание» и «Печать последнего чека» из меню «⋮ Ещё».
///
/// 2026-09-28: вынесено из BasketPanelView без изменения логики — те же кнопки есть в меню «Ещё»
/// у каждой раскладки кассы, и код не должен расходиться между ними.</summary>
public static class BasketExtraActions
{
    /// <summary>2026-10-02: открыть окно «Прокат» — действие задаёт главное окно кассы (смена, строка в чеке).</summary>
    public static Action? OpenRentals { get; set; }

    public static void Rental(BasketPanelViewModel? basket)
    {
        if (basket != null)
            basket.IsMoreActionsVisible = false;
        OpenRentals?.Invoke();
    }

    /// <summary>«Списание» (2026-09-27). Право — то же, что у вкладки «Списание» склада
    /// (закупки/склад): иначе любой кассир мог бы списывать товар без контроля.</summary>
    public static async Task WriteOffAsync(Control anchor, BasketPanelViewModel? basket)
    {
        try
        {
            if (!App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewProcurement))
            {
                App.GetRequiredService<IUserPrompts>().ShowWarning(Tr.T(
                    "Нет права на списание. Его даёт владелец в NurCRM: роль сотрудника → «Закупки / склад».",
                    "Эсептен чыгарууга укук жок. Аны ээси NurCRMде берет: кызматкердин ролу → «Сатып алуу / кампа».",
                    "No permission to write off goods. The owner grants it in NurCRM: employee role → “Procurement / warehouse”.",
                    "Düşüm yetkiniz yok. Mağaza sahibi bunu NurCRM'de verir: çalışan rolü → «Satın alma / depo».",
                    "Hisobdan chiqarishga huquq yo'q. Uni do'kon egasi NurCRM'da beradi: xodim roli → «Xaridlar / ombor»."));
                return;
            }

            var cartProducts = (basket?.Lines ?? new System.Collections.ObjectModel.ObservableCollection<CartLineItemVm>())
                .Select(line => CatalogCacheService.Products.FirstOrDefault(p =>
                    string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase)))
                .Where(p => p != null)
                .Select(p => p!)
                .ToList();

            if (TopLevel.GetTopLevel(anchor) is not Window owner)
                return;
            if (basket != null)
                basket.IsMoreActionsVisible = false;

            var dialog = new WriteOffDialog(cartProducts);
            if (await dialog.ShowDialog<bool>(owner).ConfigureAwait(true) && basket != null)
                basket.CartMessage = dialog.ResultMessage ?? "";
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Списание из кассы: окно не открылось: {ex}", "WARNING");
        }
    }

    /// <summary>«Печать последнего чека» (2026-09-27). Вся логика — в
    /// <see cref="ReceiptHistoryService.PrintLastReceiptAsync"/>; здесь только кнопка и сообщение.
    /// Успех — строкой под кнопками, как остальные сообщения корзины, ошибка — окном.</summary>
    public static async Task PrintLastReceiptAsync(Control? button, BasketPanelViewModel? basket)
    {
        if (button != null)
            button.IsEnabled = false;
        try
        {
            if (basket != null)
                basket.CartMessage = Tr.T("Печатаю копию последнего чека…", "Акыркы чектин көчүрмөсү басылып жатат…", "Printing a copy of the last receipt…", "Son fişin kopyası yazdırılıyor…", "Oxirgi chek nusxasi chop etilmoqda…");

            var (ok, message) = await ReceiptHistoryService.PrintLastReceiptAsync().ConfigureAwait(true);
            if (basket != null)
            {
                basket.CartMessage = ok ? message : "";
                if (ok)
                    basket.IsMoreActionsVisible = false;
            }

            if (!ok)
                App.GetRequiredService<IUserPrompts>().ShowToast(message, isWarning: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Печать последнего чека: {ex}", "WARNING");
            if (basket != null)
                basket.CartMessage = "";
        }
        finally
        {
            if (button != null)
                button.IsEnabled = true;
        }
    }
}
