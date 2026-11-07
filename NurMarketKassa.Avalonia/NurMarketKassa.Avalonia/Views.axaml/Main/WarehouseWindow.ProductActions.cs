using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса WarehouseWindow (ProductActions): перенесено из WarehouseWindow.axaml.cs без изменений логики.
public partial class WarehouseWindow
{
    // 2026-11-02 (аудит №2, С2-16): двойной клик по «Добавить товар» не открывает два окна карточки.
    private bool _addProductOpen;

    private async void AddProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (_addProductOpen)
            return;
        _addProductOpen = true;
        try
        {
            await AddProductCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _addProductOpen = false;
        }
    }

    private async Task AddProductCoreAsync()
    {
        if (!EnsureCanEditProducts()) // 2026-11-05 (аудит №3, С2-28): добавление товара — тоже по праву «Склад: редактирование»
            return;
        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast(Tr.T("Добавление товара недоступно в этом режиме.", "Бул режимде товар кошууга болбойт.", "Adding products isn't available in this mode.", "Bu modda ürün eklenemez.", "Bu rejimda mahsulot qo'shib bo'lmaydi."));
            return;
        }

        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ProductEditDialog(catalogApi, null);
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        // 2026-11-03 (ревью Н-2): запись ещё идёт (SavePending) — «Товар сохранён» не пишем и каталог не перечитываем: итог придёт плашкой из диалога.
        if (saved && !dialog.SavePending)
        {
            SuccessToast.Show(Tr.T("Товар сохранён.", "Товар сакталды.", "Product saved.", "Ürün kaydedildi.", "Mahsulot saqlandi."));
            // 2026-11-07 («при каждом изменении товара весь склад загружается заново»): обновляем только этот товар, не весь каталог.
            await CatalogCacheService.RefreshProductsAsync([dialog.SavedProductId]).ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
    }

    /// <summary>2026-10-30: «Поступления и партии» из меню строки — та же карточка товара, открытая на вкладке «Поступления».</summary>
    private void ProductReceipts_Click(object? sender, RoutedEventArgs e)
    {
        _openCardOnReceipts = true;
        EditProduct_Click(sender, e);
    }

    private bool _openCardOnReceipts;

    /// <summary>2026-10-30 (приёмка 1.17.66, мелочь): карточка товара открывалась дважды от одного действия (вживую 19:19:42: два ProductEditDialog с разницей 0,25 с) — второй
    /// щелчок/событие приходил, пока первый ещё догружал карточку с сервера. Пока карточка открывается или открыта, новые открытия игнорируются.</summary>
    private bool _editProductBusy;

    private async void EditProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (_editProductBusy)
        {
            _openCardOnReceipts = false;
            return;
        }

        _editProductBusy = true;
        try
        {
            await EditProductCoreAsync(sender, e).ConfigureAwait(true);
        }
        finally
        {
            _editProductBusy = false;
        }
    }

    private async Task EditProductCoreAsync(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        var onReceipts = _openCardOnReceipts;
        _openCardOnReceipts = false;
        if (!onReceipts && !EnsureCanEditProducts()) // 2026-11-03 (аудит №3, Т3-03): вкладка «Поступления» — просмотр, правка карточки и цен — по праву «Склад: редактирование»
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast(Tr.T("Редактирование товара недоступно в этом режиме.", "Бул режимде товарды түзөтүүгө болбойт.", "Editing products isn't available in this mode.", "Bu modda ürün düzenlenemez.", "Bu rejimda mahsulotni tahrirlab bo'lmaydi."));
            return;
        }

        // Свежая карточка с сервера (2026-09-07): плитка из SQLite-кэша не хранит скидку, наценку,
        // описание, оптовую цену, страну, вес и доп. штрихкоды (LocalProductRecord их не имеет) —
        // форма показывала их пустыми, а «Сохранить» отправлял пустые значения и СТИРАЛ их на
        // сервере. Офлайн или ошибка — открываем как раньше, из кэша.
        var toEdit = product;
        var fromCache = true; // 2026-11-06 (класс Б, Б-3): свежую карточку не прочитали — форма наполнена данными из памяти кассы
        try
        {
            // 2026-11-03 (С-06, аудит «зависание при медленном сервере»): свежая карточка — не дольше 8 с (раньше до 55 с, клики терялись); через 3 с — «Ждём ответ сервера…».
            var productId = product.Id;
            var detail = await ServerAnswerWait.ForegroundAsync("products/detail", t => catalogApi.ProductsDetailAsync(productId, t),
                default, slow => { if (slow) Dispatcher.UIThread.Post(() => prompts?.ShowToast(ModalShell.ServerWaitText(3))); }).ConfigureAwait(true);
            var apiBaseUrl = App.AppHost?.Services.GetService<AppSettings>()?.ApiBaseUrl ?? "";
            if (detail is { } json && ProductCatalogMapper.TryTile(json, apiBaseUrl) is { } fresh)
            {
                fresh.ProductImagePath = product.ProductImagePath;
                toEdit = fresh;
                fromCache = false;
            }
        }
        catch (ServerNotAnsweringException)
        {
            // Карточка из кэша не хранит часть полей, и «Сохранить» стёр бы их на сервере — при медленном сервере не открываем, а просим повторить.
            prompts?.ShowToast(ServerAnswerWait.SlowMessage, isWarning: true);
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Product detail fetch before edit failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }

        if (fromCache && !OfflineModeHelper.UseLocalOperations)
        {
            // 2026-11-06 (класс Б, Б-3): сервер не отдал карточку (5xx/429/нет товара) — форма из памяти кассы; на сервер уйдут только поля, которые вы измените (см. ProductEditRequest.UnchangedKeys).
            PosLogger.Log($"Product edit: свежая карточка {product.Id} не прочитана — форма открыта из памяти кассы, неизменённые поля на сервер не отправляются.", "WARNING");
            prompts?.ShowToast(Tr.T("Карточка открыта из памяти кассы — сервер не ответил. Сохранятся только поля, которые вы измените.",
                "Карточка кассанын эсинен ачылды — сервер жооп берген жок. Сиз өзгөрткөн талаалар гана сакталат.",
                "The card was opened from the till's memory — the server did not answer. Only the fields you change will be saved.",
                "Kart kasanın belleğinden açıldı — sunucu yanıt vermedi. Yalnızca değiştirdiğiniz alanlar kaydedilir.",
                "Karta kassa xotirasidan ochildi — server javob bermadi. Faqat siz o'zgartirgan maydonlar saqlanadi."), isWarning: true);
        }

        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ProductEditDialog(catalogApi, toEdit) { StartOnReceiptsTab = onReceipts };
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        // 2026-11-03 (ревью Н-2): запись ещё идёт (SavePending) — «Товар сохранён» не пишем и каталог не перечитываем: итог придёт плашкой из диалога.
        if (saved && !dialog.SavePending)
        {
            SuccessToast.Show(Tr.T("Товар сохранён.", "Товар сакталды.", "Product saved.", "Ürün kaydedildi.", "Mahsulot saqlandi."));
            // 2026-11-07 («при каждом изменении товара весь склад загружается заново»): обновляем только этот товар (карточка по id), без полной загрузки каталога.
            await CatalogCacheService.RefreshProductsAsync([dialog.SavedProductId ?? product.Id]).ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Кнопка "Удалить" в строке таблицы товаров (2026-09-07). 2026-11-06 (просьба владельца «удаление товара даже если что-то связано через спец код, и как это отразится в аналитике»):
    /// единый путь вместо цепочки окон — ПРЕДПРОСМОТР последствий (остаток и убыток, комплекты, размеры, коды/PLU, весы, что будет в аналитике) → «Удалить» →
    /// акт «Списание» остатка (если он есть) → DELETE; сервер отказал из-за истории (409 product_has_history) — ОДИН вопрос «Отправить в архив?» со счётчиками документов;
    /// архива нет — объяснение, что делать (ProductRemoval.NoArchiveText), а не тупик. Логика шага «удалить → 409 → архив» — ProductRemoval.RunAsync (проверяется тестами).</summary>
    private async void DeleteProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        if (!EnsureCanEditProducts()) // 2026-11-03 (аудит №3, Т3-03)
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        var permissions = App.AppHost?.Services.GetService<IPermissionService>();
        if (catalogApi is null)
        {
            prompts?.ShowToast(Tr.T("Удаление товара недоступно в этом режиме.", "Бул режимде товарды өчүрүүгө болбойт.", "Deleting products isn't available in this mode.", "Bu modda ürün silinemez.", "Bu rejimda mahsulotni o'chirib bo'lmaydi."));
            return;
        }

        // 2026-11-06: остаток (свежий, со сроком 8 с) читаем ДО подтверждения — он нужен предпросмотру последствий. Раньше читали после первого вопроса.
        var stock = product.Quantity;
        if (!OfflineModeHelper.UseLocalOperations)
        {
            try
            {
                // 2026-11-03 (ревью Н-3): остаток читаем со сроком 8 с (раньше без срока, до 55 с); не успели — удаление не продолжаем.
                var deleteId = product.Id;
                if (await ServerAnswerWait.ForegroundAsync("products/detail", t => catalogApi.ProductsDetailAsync(deleteId, t),
                        default, slow => { if (slow) Dispatcher.UIThread.Post(() => prompts?.ShowToast(ModalShell.ServerWaitText(3))); }).ConfigureAwait(true) is { } detail)
                    stock = StockSyncService.ResolveStockQuantity(detail, product.MustWeigh);
            }
            catch (ServerNotAnsweringException)
            {
                ShowDeleteNotice(ServerAnswerWait.SlowMessage); // 2026-11-03 (Т3-07): info-окно без «Отложить ошибку»
                return;
            }
            catch (Exception sex) when (sex is not OutOfMemoryException)
            {
                PosLogger.Log($"Склад: остаток «{product.Title}» перед удалением не получен, берём из кэша ({sex.Message}).", "WARNING");
            }
        }

        bool confirmed;
        if (OfflineModeHelper.UseLocalOperations)
        {
            confirmed = PosConfirmDialog.Show(
                this,
                Tr.T("Удалить товар", "Товарды өчүрүү", "Delete product", "Ürünü sil", "Mahsulotni o'chirish"),
                Tr.T($"Удалить «{product.Title}»? Товар исчезнет из каталога NurCRM и из кассы. Это действие нельзя отменить.",
                     $"«{product.Title}» өчүрүлсүнбү? Товар NurCRM каталогунан жана кассадан жоголот. Бул аракетти артка кайтарууга болбойт.", $"Delete “{product.Title}”? The product will be removed from the NurCRM catalog and from the till. This cannot be undone.", $"«{product.Title}» silinsin mi? Ürün NurCRM kataloğundan ve kasadan kaldırılacak. Bu işlem geri alınamaz.", $"«{product.Title}» o'chirilsinmi? Mahsulot NurCRM katalogidan va kassadan yo'qoladi. Bu amalni bekor qilib bo'lmaydi."),
                Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                PosConfirmAccent.Danger);
        }
        else
        {
            // 2026-11-06: одно окно с последствиями. Кнопка называет главное действие: с остатком — «Списать остаток и удалить».
            var stockTextPreview = ProductActions.QtyWithUnit(stock, product.Unit, product.MustWeigh);
            var preview = ProductRemoval.PreviewText(new ProductRemovalInput(product.Title, stock, stockTextPreview, product.PurchasePrice, product.PriceValue, CollectRemovalLinks(product)));
            confirmed = PosConfirmDialog.Show(
                this,
                Tr.T("Удалить товар", "Товарды өчүрүү", "Delete product", "Ürünü sil", "Mahsulotni o'chirish"),
                preview,
                stock > 0.0005
                    ? Tr.T("Списать остаток и удалить", "Калдыкты эсептен чыгарып өчүрүү", "Write off stock and delete", "Stoku düş ve sil", "Qoldiqni chiqarib o'chirish")
                    : Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
                Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                PosConfirmAccent.Danger);
        }
        if (!confirmed)
            return;

        // 2026-09-08: личный код доступа сотрудника (владелец/админ проходит без кода) —
        // см. EmployeeAccessGate. Не активен, пока в Настройки → Сотрудники не задан хотя бы
        // один код для удаления со склада.
        if (EmployeeAccessGate.IsActiveFor(EmployeeAccessGate.WarehouseDelete, permissions) &&
            prompts != null &&
            !await prompts.ConfirmWithCodeAsync(
                Tr.T("Удаление товара со склада", "Товарды кампадан өчүрүү", "Delete a product from the warehouse", "Depodan ürün silme", "Mahsulotni ombordan o'chirish"),
                Tr.T($"Введите свой код доступа, чтобы удалить «{product.Title}» со склада.",
                    $"«{product.Title}» товарын кампадан өчүрүү үчүн жеке кодуңузду киргизиңиз.",
                    $"Enter your access code to delete \"{product.Title}\" from the warehouse.",
                    $"«{product.Title}» ürününü depodan silmek için erişim kodunuzu girin.",
                    $"«{product.Title}» mahsulotini ombordan o'chirish uchun kirish kodingizni kiriting."),
                entered => EmployeeAccessGate.TryValidate(EmployeeAccessGate.WarehouseDelete, entered)).ConfigureAwait(true))
            return;

        // 2026-11-03, «как в 1С»: товар с остатком не удаляется — сначала остаток списывается актом «Списание» (N → 0),
        // потом товар убирается (с историей — в архив, это «пометка на удаление»; без истории — удаляется).
        var stockCleared = false;
        double? remainingStock = null; // 2026-11-03 (ревью С-5): остаток, оставшийся после неполного списания — для предупреждения в окне архива
        if (!OfflineModeHelper.UseLocalOperations && stock > 0.0005)
        {
              // 2026-11-03 (ревью Н-3): весь блок списания в try — это async void, исключение не должно уйти в необработанные.
              try
              {
                var stockText = ProductActions.QtyWithUnit(stock, product.Unit, product.MustWeigh); // 2026-11-05 (Т3-10): с запятой и единицей, не «2.5»
                // 2026-11-06: отдельный вопрос «Списать остаток сейчас?» убран — согласие на списание уже дано в окне последствий («Списать остаток и удалить»).
                var woDialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.WriteOffDialog(new[] { product }, stock);
                if (!await woDialog.ShowDialog<bool>(this).ConfigureAwait(true))
                    return;
                // 2026-11-03 (аудит №3, Т3-02): сервер за 8 с не ответил на акт списания — итог придёт фоном; товар не удаляем, пока остаток не станет 0.
                if (woDialog.Pending)
                {
                    ShowDeleteNotice(Tr.T("Акт списания отправлен, но сервер ещё не ответил — товар не удалён. Проверьте остаток чуть позже и повторите удаление.", "Эсептен чыгаруу актысы жөнөтүлдү, бирок сервер али жооп берген жок — товар өчүрүлгөн жок. Бир аздан кийин калдыкты текшерип, өчүрүүнү кайталаңыз.", "The write-off document was sent but the server has not answered yet — the product was not deleted. Check the stock a little later and delete again.", "Düşüm belgesi gönderildi ancak sunucu henüz yanıt vermedi — ürün silinmedi. Biraz sonra stoğu kontrol edip silmeyi tekrarlayın.", "Hisobdan chiqarish hujjati yuborildi, lekin server hali javob bermadi — mahsulot o'chirilmadi. Birozdan keyin qoldiqni tekshirib, o'chirishni takrorlang."));
                    return;
                }
                // 2026-11-05 (Н-4): раньше писали «списан × остаток до акта» даже при списании части; фактически списанное — после перечитывания остатка ниже.
                PosLogger.Log($"AUDIT: перед удалением оформлен акт списания «{product.Title}» ({product.Id}); остаток до акта {stockText}.", "INFO");
                // 2026-11-03 (аудит №3, Т3-01): остаток перечитываем с сервера (срок 8 с) и удаляем товар ТОЛЬКО при нуле. Раньше при «осталось N» показывали
                // предупреждение и всё равно удаляли — оставшиеся штуки пропадали без акта. Не прочитали остаток — тоже не удаляем.
                double left;
                try
                {
                    var checkId = product.Id;
                    var afterDetail = await ServerAnswerWait.ForegroundAsync("products/detail", t => catalogApi.ProductsDetailAsync(checkId, t)).ConfigureAwait(true);
                    if (afterDetail is null)
                    {
                        PosLogger.Log($"Склад: остаток «{product.Title}» после списания не получен (пустой ответ) — товар не удалён.", "WARNING");
                        ShowDeleteNotice(Tr.T("Не удалось проверить остаток после списания — товар не удалён. Проверьте связь и повторите.", "Эсептен чыгаргандан кийин калдыкты текшерүү мүмкүн болгон жок — товар өчүрүлгөн жок. Байланышты текшерип, кайра аракет кылыңыз.", "Could not check the stock after the write-off — the product was not deleted. Check the connection and try again.", "Düşümden sonra stok doğrulanamadı — ürün silinmedi. Bağlantıyı kontrol edip tekrar deneyin.", "Hisobdan chiqargandan keyin qoldiqni tekshirib bo'lmadi — mahsulot o'chirilmadi. Ulanishni tekshirib qayta urinib ko'ring."));
                        return;
                    }
                    left = StockSyncService.ResolveStockQuantity(afterDetail.Value, product.MustWeigh);
                }
                catch (Exception cex) when (cex is not OutOfMemoryException)
                {
                    PosLogger.Log($"Склад: остаток «{product.Title}» после списания не перечитан ({cex.Message}) — товар не удалён.", "WARNING");
                    ShowDeleteNotice(cex is ServerNotAnsweringException
                        ? ServerAnswerWait.SlowMessage
                        : Tr.T("Не удалось проверить остаток после списания — товар не удалён. Проверьте связь и повторите.", "Эсептен чыгаргандан кийин калдыкты текшерүү мүмкүн болгон жок — товар өчүрүлгөн жок. Байланышты текшерип, кайра аракет кылыңыз.", "Could not check the stock after the write-off — the product was not deleted. Check the connection and try again.", "Düşümden sonra stok doğrulanamadı — ürün silinmedi. Bağlantıyı kontrol edip tekrar deneyin.", "Hisobdan chiqargandan keyin qoldiqni tekshirib bo'lmadi — mahsulot o'chirilmadi. Ulanishni tekshirib qayta urinib ko'ring."));
                    return;
                }
                if (left > 0.0005)
                {
                    PosLogger.Log($"AUDIT: удаление «{product.Title}» ({product.Id}) прервано — после списания осталось {left:0.###}.", "WARNING");
                    var leftText = ProductActions.QtyWithUnit(left, product.Unit, product.MustWeigh); // 2026-11-05 (Т3-10)
                    ShowDeleteNotice(Tr.T($"Остаток списан не полностью (осталось {leftText}) — товар не удалён. Спишите остаток полностью и повторите удаление.", $"Калдык толугу менен эсептен чыгарылган жок ({leftText} калды) — товар өчүрүлгөн жок. Калдыкты толук эсептен чыгарып, өчүрүүнү кайталаңыз.", $"The stock was not written off completely ({leftText} left) — the product was not deleted. Write off all of it and delete again.", $"Stok tamamen düşülmedi ({leftText} kaldı) — ürün silinmedi. Tüm stoku düşüp silmeyi tekrarlayın.", $"Qoldiq to'liq hisobdan chiqarilmadi ({leftText} qoldi) — mahsulot o'chirilmadi. Qoldiqni to'liq hisobdan chiqarib, o'chirishni takrorlang."));
                    await RefreshCatalogQuietlyAsync(product.Id).ConfigureAwait(true); // остаток на экране — как на сервере
                    return;
                }
                PosLogger.Log($"AUDIT: перед удалением списан остаток «{product.Title}» ({product.Id}) × {ProductActions.QtyWithUnit(Math.Max(0.0, stock - left), product.Unit, product.MustWeigh)}.", "INFO");
                stockCleared = true;
              }
              catch (Exception wex) when (wex is not OutOfMemoryException)
              {
                  PosLogger.Log($"Склад: списание остатка «{product.Title}» перед удалением: {wex.GetType().Name}: {wex.Message}", "WARNING");
                  ShowDeleteNotice(ServerOutageMonitor.FriendlyError(wex)); // 2026-11-03 (Т3-07): info-окно без «Отложить ошибку»
                  return;
              }
        }

        try
        {
            // 2026-09-09: офлайн (обычный или автономный режим) — удаляем прямо из локальной
            // SQLite, минуя ICatalogApiService.DeleteProductAsync (реальный HTTP к NurCRM).
            if (OfflineModeHelper.UseLocalOperations)
            {
                // 2026-11-05 (аудит №3, Т3-12): без связи товар с остатком не удаляем (списать остаток актом нельзя), а удаление без остатка честно называем «только на этой кассе»:
                // на сервер оно не уходит, и товар вернётся при синхронизации — его нужно будет удалить ещё раз при связи.
                if (product.Quantity > 0.0005)
                {
                    ShowDeleteNotice(Tr.T($"Нет связи с сервером: у «{product.Title}» остаток {ProductActions.QtyWithUnit(product.Quantity, product.Unit, product.MustWeigh)}, а списать его актом без связи нельзя. Удалите товар, когда связь появится.",
                        $"Сервер менен байланыш жок: «{product.Title}» калдыгы {ProductActions.QtyWithUnit(product.Quantity, product.Unit, product.MustWeigh)}, ал эми байланышсыз акт менен эсептен чыгарылбайт. Байланыш пайда болгондо товарды өчүрүңүз.",
                        $"No connection to the server: “{product.Title}” has {ProductActions.QtyWithUnit(product.Quantity, product.Unit, product.MustWeigh)} in stock and it cannot be written off by a document offline. Delete the product when the connection is back.",
                        $"Sunucu bağlantısı yok: «{product.Title}» için stok {ProductActions.QtyWithUnit(product.Quantity, product.Unit, product.MustWeigh)} ve bağlantısız belgeyle düşülemez. Bağlantı gelince ürünü silin.",
                        $"Server bilan aloqa yo'q: «{product.Title}» qoldig'i {ProductActions.QtyWithUnit(product.Quantity, product.Unit, product.MustWeigh)}, uni aloqasiz akt bilan hisobdan chiqarib bo'lmaydi. Aloqa paydo bo'lganda mahsulotni o'chiring."));
                    return;
                }
                LocalProductEditor.DeleteLocally(product.Id);
                PosLogger.Log($"AUDIT: товар «{product.Title}» ({product.Id}) удалён только на этой кассе (нет связи с сервером).", "INFO"); // 2026-11-05 (Д-9)
                SuccessToast.Show(Tr.T("Товар удалён только на этой кассе. На сервере он остаётся — при связи удалите его ещё раз.", "Товар ушул кассадан гана өчүрүлдү. Серверде калат — байланыш болгондо дагы бир жолу өчүрүңүз.", "Product deleted on this till only. It stays on the server — delete it again when online.", "Ürün yalnızca bu kasadan silindi. Sunucuda kalır — bağlantı gelince tekrar silin.", "Mahsulot faqat shu kassadan o'chirildi. Serverda qoladi — aloqa paydo bo'lganda yana o'chiring."));
                await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
                return;
            }

            // 2026-11-06: единый путь «DELETE → 409 → один вопрос про архив → архив» (ProductRemoval.RunAsync).
            var result = await ProductRemoval.RunAsync(catalogApi, product.Id,
                ct => ServerCapabilities.HasAsync(App.GetRequiredService<NurMarketApiClient>(), "product_archive", ct),
                counts => Task.FromResult(PosConfirmDialog.Show(this,
                    Tr.T("Отправить в архив?", "Архивге жиберүүбү?", "Send to the archive?", "Arşive gönderilsin mi?", "Arxivga yuborilsinmi?"),
                    ProductRemoval.ArchiveText(product.Title, counts, remainingStock ?? stock, product.PurchasePrice, product.PriceValue, stockCleared),
                    Tr.T("Отправить в архив", "Архивге жиберүү", "Send to archive", "Arşive gönder", "Arxivga yuborish"),
                    Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                    PosConfirmAccent.Primary))).ConfigureAwait(true);

            switch (result.Outcome)
            {
                case ProductRemovalOutcome.NotAccepted:
                    ShowDeleteNotice(Tr.T("Не удалось удалить товар: сервер не принял запрос на удаление.", "Товарды өчүрүү мүмкүн болгон жок: сервер өчүрүү сурамын кабыл алган жок.", "Could not delete the product: the server rejected the delete request.", "Ürün silinemedi: sunucu silme isteğini kabul etmedi.", "Mahsulotni o'chirib bo'lmadi: server o'chirish so'rovini qabul qilmadi."));
                    return;

                case ProductRemovalOutcome.Deleted:
                    PosLogger.Log($"AUDIT: товар «{product.Title}» ({product.Id}) удалён на сервере{(stockCleared ? " (остаток предварительно списан)" : "")}.", "INFO"); // 2026-11-05 (Д-9): удаление без истории остаётся в журнале
                    AfterProductRemoved(product, archived: false);
                    SuccessToast.Show(ProductRemoval.DeletedDoneText());
                    // 2026-11-07: на сервере товара уже нет — убираем из каталога кассы только его (раньше — полная загрузка каталога).
                    LocalProductEditor.DeleteLocally(product.Id);
                    await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
                    return;

                case ProductRemovalOutcome.Archived:
                    PosLogger.Log($"AUDIT: товар «{product.Title}» ({product.Id}) отправлен в архив (на сервере есть история: {result.Counts.ListText()}).", "INFO");
                    AfterProductRemoved(product, archived: true);
                    LocalProductEditor.DeleteLocally(product.Id); // сразу убираем из каталога кассы; сервер и так не отдаёт архивные товары в списках
                    SuccessToast.Show(ProductRemoval.ArchivedDoneText());
                    // 2026-11-07: товар уже убран из каталога кассы строкой выше — полная загрузка каталога не нужна.
                    await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
                    return;

                case ProductRemovalOutcome.ArchiveDeclined:
                    PosLogger.Log($"Склад: «{product.Title}» не удалён — по товару есть история ({result.Counts.ListText()}), владелец отказался от архива.", "INFO");
                    if (stockCleared)
                    {
                        SuccessToast.Show(Tr.T("Остаток списан, товар остался в каталоге с нулём на складе.", "Калдык эсептен чыгарылды, товар каталогто нөл калдык менен калды.", "The stock was written off; the product stays in the catalog with zero stock.", "Stok düşüldü, ürün katalogda sıfır stokla kaldı.", "Qoldiq chiqarildi, mahsulot katalogda nol qoldiq bilan qoldi."));
                        await RefreshCatalogQuietlyAsync(product.Id).ConfigureAwait(true);
                    }
                    return;

                case ProductRemovalOutcome.ArchiveFailed:
                    var why = ServerOutageMonitor.FriendlyError(result.Error!, showServerText: true);
                    PosLogger.Log($"Склад: «{product.Title}» не отправлен в архив ({result.Error!.Message}).", "WARNING");
                    PosAlertDialog.Show(this, Tr.T("Не удалось отправить в архив", "Архивге жөнөтүү мүмкүн болгон жок", "Could not send to the archive", "Arşive gönderilemedi", "Arxivga yuborib bo'lmadi"), why, PosAlertKind.Warning, null, info: true); // окном, а не исчезающей плашкой
                    if (stockCleared)
                        await RefreshCatalogQuietlyAsync(product.Id).ConfigureAwait(true);
                    return;

                default: // ArchiveUnavailable: сервер (Django ProtectedError) не удаляет товар с историей, а архива нет — не тупик, а понятный совет и переход в карточку
                    PosLogger.Log($"Склад: «{product.Title}» не удалён — по товару есть история (ответ сервера: {result.Error?.Message ?? "product_has_history"}), архив недоступен.", "INFO");
                    if (stockCleared)
                        await RefreshCatalogQuietlyAsync(product.Id).ConfigureAwait(true);
                    if (PosConfirmDialog.Show(this,
                            Tr.T("Товар нельзя удалить", "Товарды өчүрүүгө болбойт", "The product can't be deleted", "Ürün silinemez", "Mahsulotni o'chirib bo'lmaydi"),
                            ProductRemoval.NoArchiveText(product.Title, result.Counts, stockCleared),
                            Tr.T("Открыть карточку", "Карточканы ачуу", "Open the card", "Kartı aç", "Kartani ochish"),
                            Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"),
                            PosConfirmAccent.Primary))
                        await EditProductCoreAsync(new Button { Tag = product }, new RoutedEventArgs()).ConfigureAwait(true);
                    return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var delFail = ServerOutageMonitor.FriendlyError(ex); // 2026-11-05 (С2-38): без сырого ex.Message
            PosLogger.Log($"Склад: удаление «{product.Title}» не удалось: {ex.GetType().Name}: {ex.Message}", "WARNING");
            ShowDeleteNotice(Tr.T($"Не удалось удалить товар: {delFail}", $"Товарды өчүрүү мүмкүн болгон жок: {delFail}", $"Could not delete the product: {delFail}", $"Ürün silinemedi: {delFail}", $"Mahsulotni o'chirib bo'lmadi: {delFail}"));
        }
    }

    /// <summary>2026-11-06: после успешного удаления/архива — запомнить закупку, категорию и бренд (аналитика прошлых периодов не теряет товар) и убрать размеры/цвета из справочника
    /// («Размеры и цвета», «Заканчиваются размеры»). Ошибки не пробрасываются: операция на сервере уже прошла.</summary>
    private static void AfterProductRemoved(CatalogProductTileVm product, bool archived)
    {
        try { RemovedProductMemory.Remember(product, archived); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Склад: память об удалённом товаре не записана ({ex.Message}).", "WARNING"); }
        try { VariantBarcodeIndex.Update(product.Id, Array.Empty<ProductVariantDto>()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Склад: размеры удалённого товара не убраны из справочника ({ex.Message}).", "WARNING"); }
    }

    /// <summary>2026-11-06: что связано с товаром на этой кассе — для предпросмотра удаления. Комплекты: те, где товар в локальном составе (BundleItems; состав серверных комплектов касса не хранит —
    /// там сервер скажет сам ответом на удаление); размеры/цвета — справочник VariantBarcodeIndex; доп. штрихкоды; артикул/код/PLU; весы — профили LabelScaleStore, где у товара есть PLU, клавиша или он выбран.</summary>
    private static ProductLinks CollectRemovalLinks(CatalogProductTileVm product)
    {
        try
        {
            var bundles = CatalogCacheService.Products.ToArray()
                .Where(t => t.BundleItems is { Count: > 0 } items && items.Any(b => string.Equals(b.ProductId, product.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(t => t.Title).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            var altCount = string.IsNullOrWhiteSpace(product.AlternateBarcodesRaw) ? 0
                : product.AlternateBarcodesRaw!.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            var codes = new List<string>();
            if (product.Plu is > 0) codes.Add("PLU " + product.Plu.Value);
            if (!string.IsNullOrWhiteSpace(product.ProductCode)) codes.Add(Tr.T("код ", "код ", "code ", "kod ", "kod ") + product.ProductCode!.Trim());
            if (!string.IsNullOrWhiteSpace(product.Article)) codes.Add(Tr.T("артикул ", "артикул ", "article ", "stok kodu ", "artikul ") + product.Article!.Trim());
            var scales = LabelScaleStore.All
                .Where(s => s.PluNumbers.ContainsKey(product.Id) || s.SentPlus.ContainsKey(product.Id) || s.Hotkeys.ContainsKey(product.Id) || s.ProductIds.Contains(product.Id)
                            || (product.IsWeighted && s.Categories.Count > 0 && !string.IsNullOrWhiteSpace(product.Category) && s.Categories.Contains(product.Category!, StringComparer.OrdinalIgnoreCase)))
                .Select(s => string.IsNullOrWhiteSpace(s.Name) ? s.Ip : s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            return new ProductLinks(bundles, product.IsBundle, VariantBarcodeIndex.ForProduct(product.Id).Count, altCount, scales,
                codes.Count > 0 && (product.IsWeighted || scales.Count > 0) ? string.Join(", ", codes) : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: связи товара «{product.Title}» для предпросмотра удаления не собраны ({ex.Message}).", "WARNING");
            return ProductLinks.None;
        }
    }

    /// <summary>2026-11-03 (аудит №3, Т3-03): право «Склад: редактирование» (can_edit_products). Владелец и администратор — всегда; сотрудник — пока сервер явно не запретил.
    /// Нет права — понятное окно (без «Отложить ошибку»), действие не выполняется. Окончательную проверку всё равно делает сервер.</summary>
    internal bool EnsureCanEditProducts()
    {
        var permissions = App.AppHost?.Services.GetService<IPermissionService>();
        if (permissions is null || permissions.HasPermission(PosPermissions.EditProducts))
            return true;
        PosLogger.Log("Склад: действие отклонено — у сотрудника нет права «Склад: редактирование» (can_edit_products).", "INFO");
        ShowDeleteNotice(NoEditRightText);
        return false;
    }

    /// <summary>Текст отказа без права «Склад: редактирование» (5 языков).</summary>
    internal static string NoEditRightText => Tr.T(
        "У вас нет права «Склад: редактирование». Удалять, списывать, менять цены, остаток и карточку товара может владелец, администратор или сотрудник с этим правом. Попросите владельца выдать право в «Сотрудники».",
        "Сизде «Склад: түзөтүү» укугу жок. Товарды өчүрүү, эсептен чыгаруу, баасын, калдыгын жана карточкасын өзгөртүүнү ээси, администратор же ушул укугу бар кызматкер жасай алат. Ээсинен «Кызматкерлер» бөлүмүндө укук берүүнү сураныңыз.",
        "You do not have the “Warehouse: edit” permission. The owner, an administrator or an employee with this permission can delete, write off and change prices, stock and the product card. Ask the owner to grant it in “Employees”.",
        "«Depo: düzenleme» yetkiniz yok. Ürünü silmek, düşmek, fiyat, stok ve kartı değiştirmek için sahip, yönetici veya bu yetkisi olan çalışan gerekir. Sahipten «Personel» bölümünde yetki vermesini isteyin.",
        "Sizda «Ombor: tahrirlash» huquqi yo'q. Mahsulotni o'chirish, hisobdan chiqarish, narx, qoldiq va kartani o'zgartirishni egasi, administrator yoki shu huquqli xodim bajara oladi. Egasidan «Xodimlar» bo'limida huquq berishni so'rang.");

    /// <summary>2026-11-03 (аудит №3, Т3-07): предупреждение при удалении товара — info-окно «Внимание» без «Отложить ошибку» и без ожидания закрытия
    /// (раньше — модальное «Внимание» через ShowToast(isWarning), удаление стояло, пока его закроют).</summary>
    private void ShowDeleteNotice(string message) =>
        _ = PosAlertDialog.ShowAsync(this, Tr.T("Внимание", "Көңүл буруңуз", "Warning", "Uyarı", "Diqqat"), message, PosAlertKind.Warning, null, info: true);

    /// <summary>2026-11-03 (аудит №3, Т3-01): после списания перед удалением каталог перечитываем в любом исходе — остаток на экране должен совпасть с сервером.</summary>
    private async Task RefreshCatalogQuietlyAsync(string productId)
    {
        try
        {
            // 2026-11-07 («при каждом изменении товара весь склад загружается заново»): перечитываем только этот товар, не весь каталог.
            await CatalogCacheService.RefreshProductsAsync([productId]).ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
        catch (Exception rex) when (rex is not OutOfMemoryException)
        {
            PosLogger.Log($"Склад: каталог после списания перед удалением не обновлён ({rex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-10-30: ответ сервера «нельзя удалить — на объект ссылаются другие записи» (ProtectedError). 2026-11-06: логика перенесена в ProductRemoval.IsInUseError (общий код, тесты); здесь — прежнее имя для ClientsWindow.</summary>
    internal static bool IsProductInUseError(ApiException ex) => ProductRemoval.IsInUseError(ex);

    private async void UploadPhoto_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        if (!EnsureCanEditProducts()) // 2026-11-03 (аудит №3, Т3-03)
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var photoPicker = App.AppHost?.Services.GetService<ISettingsImagePicker>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null || photoPicker is null)
        {
            prompts?.ShowToast(Tr.T("Загрузка фото недоступна в этом режиме.", "Бул режимде сүрөт жүктөөгө болбойт.", "Photo upload isn't available in this mode.", "Bu modda fotoğraf yüklenemez.", "Bu rejimda rasm yuklab bo'lmaydi."));
            return;
        }

        var filePath = await photoPicker.PickProductPhotoAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        // 2026-10-27 (расследование витрины NBS): перед загрузкой — предпросмотр и явный ответ «Сделать главным / Добавить в галерею»
        // (раньше фото сразу уходило главным, и плохой кадр нельзя было отменить).
        var choice = await ConfirmProductPhotoAsync(product, filePath).ConfigureAwait(true);
        if (choice == ProductPhotoChoice.Cancel)
            return;
        var asPrimary = choice == ProductPhotoChoice.Primary;

        try
        {
            var uploadedUrl = await catalogApi
                .UploadProductImageAsync(product.Id, filePath, isPrimary: asPrimary)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(uploadedUrl))
            {
                prompts?.ShowToast(Tr.T("Не удалось загрузить фото.", "Сүрөттү жүктөө мүмкүн болгон жок.", "Could not upload the photo.", "Fotoğraf yüklenemedi.", "Rasmni yuklab bo'lmadi."), isWarning: true);
                return;
            }

            // Show the picked file immediately — same bytes just landed on the server,
            // no need to round-trip a redownload through the thumbnail cache.
            if (asPrimary)
                product.ProductImagePath = filePath;
            App.AppHost?.Services.GetService<MySqlAuditService>()?
                .LogEvent("catalog", "product_photo_upload", new { productId = product.Id });
            SuccessToast.Show(asPrimary
                ? Tr.T("Фото товара загружено.", "Товардын сүрөтү жүктөлдү.", "Product photo uploaded.", "Ürün fotoğrafı yüklendi.", "Mahsulot rasmi yuklandi.")
                : Tr.T("Фото добавлено в галерею товара.", "Сүрөт товардын галереясына кошулду.", "Photo added to the product gallery.", "Fotoğraf ürün galerisine eklendi.", "Rasm mahsulot galereyasiga qo'shildi."));
        }
        catch (ApiException ex)
        {
            var photoFail = ServerOutageMonitor.FriendlyError(ex); // 2026-11-05 (С2-38): без сырого ex.Message
            prompts?.ShowToast(Tr.T($"Загрузка фото: {photoFail}", $"Сүрөт жүктөө: {photoFail}", $"Photo upload: {photoFail}", $"Fotoğraf yükleme: {photoFail}", $"Rasm yuklash: {photoFail}"), isWarning: true);
        }
        catch (HttpRequestException)
        {
            prompts?.ShowToast(Tr.T("Не удалось загрузить фото — нет сети.", "Сүрөттү жүктөө мүмкүн болгон жок — тармак жок.", "Could not upload the photo — no network.", "Fotoğraf yüklenemedi — ağ bağlantısı yok.", "Rasmni yuklab bo'lmadi — tarmoq yo'q."), isWarning: true);
        }
    }

    private enum ProductPhotoChoice { Cancel, Primary, Gallery }

    /// <summary>2026-10-27: окно «Фото товара» перед загрузкой — крупный предпросмотр выбранного файла. У товара ещё нет фото — «Загрузить фото»
    /// (станет главным); есть — «Сделать главным» (по умолчанию, как было) или «Добавить в галерею» (главное остаётся прежним). Esc и ✕ — отмена.</summary>
    private async System.Threading.Tasks.Task<ProductPhotoChoice> ConfirmProductPhotoAsync(CatalogProductTileVm product, string filePath)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<ProductPhotoChoice>();
        var dialog = new Window { Title = Title };
        SectionCards.AsModal(dialog, 640);
        var hasPhoto = !string.IsNullOrWhiteSpace(product.ProductImagePath);
        var preview = new Avalonia.Controls.Image { Height = 280, Stretch = Avalonia.Media.Stretch.Uniform };
        try
        {
            preview.Source = new Avalonia.Media.Imaging.Bitmap(filePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: предпросмотр фото не показан ({ex.Message}).", "DEBUG");
        }
        var note = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13,
            Text = hasPhoto
                ? Tr.T("У товара уже есть фото. «Сделать главным» — новое заменит его на плитке товара и на сайте. «Добавить в галерею» — главным останется прежнее.",
                    "Товарда сүрөт бар. «Негизги кылуу» — жаңысы аны товар плиткасында жана сайтта алмаштырат. «Галереяга кошуу» — негизгиси мурункусу бойдон калат.",
                    "The product already has a photo. “Make main” — the new one replaces it on the tile and on the website. “Add to gallery” — the old one stays main.",
                    "Ürünün zaten bir fotoğrafı var. «Ana yap» — yenisi kartta ve sitede onun yerine geçer. «Galeriye ekle» — ana fotoğraf eskisi kalır.",
                    "Mahsulotda rasm bor. «Asosiy qilish» — yangisi uni kartochkada va saytda almashtiradi. «Galereyaga qo'shish» — asosiysi eskisi qoladi.")
                : Tr.T("Это фото станет главным: его увидят в кассе и на сайте. Проверьте, что на нём сам товар, а не штрихкод.",
                    "Бул сүрөт негизги болот: аны кассада жана сайтта көрүшөт. Анда товардын өзү экенин, штрихкод эмес экенин текшериңиз.",
                    "This photo becomes the main one: it shows in the till and on the website. Check it shows the product itself, not a barcode.",
                    "Bu fotoğraf ana fotoğraf olacak: kasada ve sitede görünür. Fotoğrafta barkodun değil ürünün kendisinin göründüğünden emin olun.",
                    "Bu rasm asosiy bo'ladi: kassada va saytda ko'rinadi. Unda shtrixkod emas, mahsulotning o'zi borligini tekshiring."),
        };
        note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushTextSoft"));
        var content = new StackPanel { Spacing = 10, Children = { preview, note } };
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => { tcs.TrySetResult(ProductPhotoChoice.Cancel); dialog.Close(); };
        var buttons = new List<Button> { cancel };
        Button primary;
        if (hasPhoto)
        {
            var gallery = SectionCards.Secondary(Tr.T("Добавить в галерею", "Галереяга кошуу", "Add to gallery", "Galeriye ekle", "Galereyaga qo'shish"));
            gallery.Click += (_, _) => { tcs.TrySetResult(ProductPhotoChoice.Gallery); dialog.Close(); };
            buttons.Add(gallery);
            primary = SectionCards.Primary(Tr.T("Сделать главным", "Негизги кылуу", "Make main", "Ana yap", "Asosiy qilish"), isDefault: true);
        }
        else
            primary = SectionCards.Primary(Tr.T("Загрузить фото", "Сүрөттү жүктөө", "Upload photo", "Fotoğrafı yükle", "Rasmni yuklash"), isDefault: true);
        primary.Click += (_, _) => { tcs.TrySetResult(ProductPhotoChoice.Primary); dialog.Close(); };
        buttons.Add(primary);
        dialog.Closed += (_, _) => tcs.TrySetResult(ProductPhotoChoice.Cancel);
        var shell = SectionCards.Shell(Tr.T("Фото товара", "Товардын сүрөтү", "Product photo", "Ürün fotoğrafı", "Mahsulot rasmi"), product.Title, content, SectionCards.Buttons(buttons.ToArray()));
        shell.InitialFocus = primary;
        dialog.Content = shell;
        await dialog.ShowDialog(this).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

}
