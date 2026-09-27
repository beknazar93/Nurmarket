using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

public partial class BasketPanelView : UserControl
{
    public BasketPanelView() => InitializeComponent();

    // Cashiers often click away (e.g. straight to "Оплатить") instead of pressing Enter
    // after typing a quantity — commit on blur too, not just on the Enter key binding.
    private void QuantityInput_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: CartLineItemVm line } && line.SetQuantityCommand?.CanExecute(line) == true)
            line.SetQuantityCommand.Execute(line);
    }

    /// <summary>«Списание» в меню «Ещё» (2026-09-27). Право — то же, что у вкладки «Списание»
    /// склада (закупки/склад): иначе любой кассир мог бы списывать товар без контроля.</summary>
    private async void WriteOff_Click(object? sender, RoutedEventArgs e)
    {
        var basket = DataContext as BasketPanelViewModel;
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

            if (TopLevel.GetTopLevel(this) is not Window owner)
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

    /// <summary>«Печать последнего чека» в меню «Ещё» (2026-09-27). Вся логика — в
    /// <see cref="ReceiptHistoryService.PrintLastReceiptAsync"/>; здесь только кнопка и сообщение.
    /// Успех — строкой под кнопками, как остальные сообщения корзины, ошибка — окном.</summary>
    private async void PrintLastReceipt_Click(object? sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var basket = DataContext as BasketPanelViewModel;
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
