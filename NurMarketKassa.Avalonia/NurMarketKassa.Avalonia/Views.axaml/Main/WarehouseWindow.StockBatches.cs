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

    /// <summary>Подпись кнопки «Срок годности и партия» в карточке: что уже задано, иначе обычное название.</summary>
    public static string BatchButtonLabel(NurMarketKassa.Models.ReceivingLineVm line) =>
        ReceivingBatchDialog.Summary(line) is { Length: > 0 } summary ? summary : BatchButtonText;

    private async void ReceivingBatch_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not NurMarketKassa.Models.ReceivingLineVm line)
            return;
        // 2026-11-19 (ответ Умара 19.11, п. 5): без поставщика приёмка — прямая правка остатка (PATCH), сервер срок и № партии не принимает. С поставщиком (приход) — всё работает.
        // 2026-11-22 (владелец: «сделай возможным указать срок годности»): вместо тупика — понятно, что сделать (выбрать поставщика), срок вводится окном как в «Закупки → Приём товара».
        if (_viewModel is { HasReceivingSupplier: false })
        {
            App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(
                Tr.T("Срок годности и партию сервер принимает только в приходе от поставщика. Выберите поставщика в строке «Поставщик» выше — и кнопка откроет ввод срока и партии.",
                    "Жарактуулук мөөнөтүн жана партияны сервер жеткирүүчүдөн кабыл алууда гана алат. Жогорудагы «Жеткирүүчү» саптан жеткирүүчүнү тандаңыз — баскыч мөөнөт менен партияны киргизүүнү ачат.",
                    "The server accepts expiry date and batch only when receiving from a supplier. Choose a supplier in the “Supplier” row above — then this button opens the expiry and batch entry.",
                    "Sunucu son kullanma tarihini ve partiyi yalnızca tedarikçiden kabulde alır. Yukarıdaki «Tedarikçi» satırından tedarikçi seçin — düğme tarih ve parti girişini açar.",
                    "Server yaroqlilik muddati va partiyani faqat yetkazib beruvchidan qabul qilishda oladi. Yuqoridagi «Yetkazib beruvchi» qatoridan tanlang — tugma muddat va partiyani kiritishni ochadi."), isWarning: true);
            return;
        }
        var saved = await new ReceivingBatchDialog(line).ShowDialog<bool>(this).ConfigureAwait(true);
        // 2026-11-22: на кнопке — что задано («Срок до 01.12.2026 · партия A-12»), как в «Закупки → Приём товара»; пусто — прежняя подпись.
        if (saved && sender is Button button)
            button.Content = BatchButtonLabel(line);
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
