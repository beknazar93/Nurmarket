using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-19 (склад «10 из 10», К-4/К-8): окна «Партии» и «Резервы» из меню строки товара. Меню показывает их только при признаках сервера stock_batches / reservations.
public partial class WarehouseWindow
{
    /// <summary>Кнопка «Срок годности и партия» в строке приёмки видна только при признаке сервера (читается при создании строки).</summary>
    public static bool BatchFieldsOn => ServerStock.ExpiryFieldsOn;

    public static string BatchButtonText => Tr.T("Срок годности и партия", "Жарактуулук мөөнөтү жана партия", "Expiry date and batch", "Son kullanma tarihi ve parti", "Yaroqlilik muddati va partiya");

    private async void ReceivingBatch_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not NurMarketKassa.Models.ReceivingLineVm line)
            return;
        // 2026-11-19 (ответ Умара 19.11, п. 5): без поставщика приёмка — прямая правка остатка (PATCH), сервер срок и № партии не принимает. С поставщиком (приход) — всё работает.
        if (_viewModel is { HasReceivingSupplier: false })
        {
            App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(
                Tr.T("Срок и партию можно указать при приёмке от поставщика (Закупки → Приём товара).", "Мөөнөттү жана партияны жеткирүүчүдөн кабыл алууда көрсөтсө болот (Сатып алуу → Товар кабыл алуу).",
                    "Expiry and batch can be set when receiving from a supplier (Purchases → Receive goods).", "Son kullanma tarihi ve parti, tedarikçiden kabulde belirtilebilir (Alımlar → Mal kabul).",
                    "Muddat va partiyani yetkazib beruvchidan qabul qilishda ko'rsatish mumkin (Xaridlar → Tovar qabul qilish)."), isWarning: true);
            return;
        }
        await new ReceivingBatchDialog(line).ShowDialog<bool>(this).ConfigureAwait(true);
    }

    private async void ProductBatches_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        var id = product.Id;
        var window = new ProductBatchesWindow(product.Title, product.Unit ?? "", RevisionShowPurchase, ct => StockServerApi.BatchesAsync(id, false, null, ct));
        await window.ShowDialog(this).ConfigureAwait(true);
    }

    private async void ProductReservations_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        var window = new ReservationsWindow(ReservationsWindow.Ops.Live(), product);
        await window.ShowDialog(this).ConfigureAwait(true);
        // Резерв меняет «свободно» у товара — перечитываем только этот товар (как после правки карточки).
        await CatalogCacheService.RefreshProductsAsync([product.Id]).ConfigureAwait(true);
    }
}
