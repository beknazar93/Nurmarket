using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir;

public partial class MainWindow
{
    private ICartService? _cartService;
    private IDeferredCartService? _deferredCartService;

    private void WireDialogBridge()
    {
        _hostBridge.AddProductFromCatalog = AddProductFromCatalogAsync;
        _hostBridge.AddWeighedProductWithKnownWeight = AddWeighedProductWithKnownWeightAsync;
        _hostBridge.OpenDeferredCarts = OpenDeferredCartsAsync;
        _hostBridge.OpenNextDeferredCart = OpenNextDeferredCartAsync;
        _hostBridge.OpenCashOperations = OpenCashOperationsAsync;
        _hostBridge.ApplyOrderDiscount = ApplyOrderDiscountAsync;
        _hostBridge.AddCustomItem = AddCustomItemAsync;
        _hostBridge.OfferAddUnknownProduct = OfferAddUnknownProductAsync;
        _hostBridge.ReweighCartLine = ReweighCartLineAsync;
        _hostBridge.ApplyLineDiscount = ApplyLineDiscountAsync;
        _hostBridge.ReplenishStockForProduct = ReplenishStockForProductAsync;
    }

    /// <summary>
    /// Базовый остаток кассы с сервера/офлайн-состояния, БЕЗ локальных внесений/изъятий:
    /// X/Z-отчёт (CashShiftService.BuildReportText) прибавляет их сам, чтобы не задвоить.
    /// Для отображения используется EffectiveShiftCashBalance.
    /// </summary>
    internal decimal? GetCurrentBalance() => _shiftCashBalance;

    internal async Task<bool> OpenShiftFromCoordinatorAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await OpenShiftAsync().ConfigureAwait(true);
        return _session.IsShiftOpen;
    }

    internal async Task OpenDeferredCartsAsync()
    {
        var dlg = new DeferredCartsDialog(new DeferredCartsDialogActions
        {
            MergeIntoCurrentAsync = MergeDeferredIntoCurrentAsync,
            OpenAsSeparateAsync = OpenDeferredAsSeparateAsync,
        });
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true);
        _viewModel.Basket.RefreshFromCart();
    }

    internal async Task OpenCashOperationsAsync()
    {
        var dlg = App.GetRequiredService<CashOperationsDialog>();
        dlg.OpenShiftAction = async cash => await ApplyShiftOpenedAsync(cash).ConfigureAwait(true);
        dlg.CloseShiftAction = async cash => await ApplyShiftClosedAsync(cash).ConfigureAwait(true);
        await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true);
    }

    /// <summary>2026-10-01: варианты товара (размер/цвет) с сервера. null — сервер недоступен: товар
    /// добавляется как обычно (без варианта), причина — в журнал.
    /// 2026-10-04, владелец: «по одеждам очень тормозит, количество не уменьшается». Был свой кеш на 2 минуты:
    /// каждое первое нажатие ждало сервер до 6 с (без интернета — все 6 с), а после продажи окно ещё до 2 минут
    /// показывало прежний остаток размера. Теперь общий ProductVariantCache: из кеша — сразу, обновление в фоне,
    /// после продажи остаток размера уменьшен сразу, без связи с сервером — не ждём.</summary>
    private Task<List<ProductVariantDto>?> LoadProductVariantsAsync(string productId) =>
        ProductVariantCache.GetAsync(productId, TimeSpan.FromSeconds(4));

    internal async Task AddProductFromCatalogAsync(CatalogProductTileVm vm, string? lineNameOverride = null)
    {
        if (!_session.IsShiftOpen)
        {
            await OpenShiftAsync().ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                return;
        }

        var cart = ResolveCartService();
        double qtyToAdd;
        var mustWeigh = ProductUnitNormalizer.RequiresWeighing(vm);

        // 2026-10-01, владелец: «магазин одежды — при выборе нужно выбрать размер, цвет, возможно
        // изменение цены, если на какой-то размер или цвет есть скидка». В сфере «Одежда» у товара с
        // вариантами NurCRM (размер/цвет) сначала выбирается вариант; цену и остаток списывает сервер.
        if (MarketSpheres.IsClothing && !mustWeigh && lineNameOverride == null)
        {
            var variants = await LoadProductVariantsAsync(vm.Id).ConfigureAwait(true);
            if (variants is { Count: > 0 } && variants.Any(v => v.IsActive))
            {
                var picker = new VariantPickerWindow(vm, variants);
                await picker.ShowDialog(this).ConfigureAwait(true);
                if (picker.Result is not { Id: { } variantId } chosen)
                    return;
                if (chosen.Quantity < picker.Quantity
                    && !await PosDialogs.ConfirmYesNoModalAsync(this, Tr.T(
                        $"Остаток этого размера/цвета — {chosen.Quantity:0.###} шт. Всё равно добавить {picker.Quantity:0} шт.?",
                        $"Бул өлчөм/түстүн калдыгы — {chosen.Quantity:0.###} даана. Баары бир {picker.Quantity:0} даана кошулсунбу?",
                        $"Only {chosen.Quantity:0.###} pcs of this size/color left. Add {picker.Quantity:0} pcs anyway?",
                        $"Bu beden/renkten {chosen.Quantity:0.###} adet kaldı. Yine de {picker.Quantity:0} adet eklensin mi?",
                        $"Bu o'lcham/rangdan {chosen.Quantity:0.###} dona qoldi. Baribir {picker.Quantity:0} dona qo'shilsinmi?")))
                    return;

                var parts = new[] { chosen.Size, chosen.Color }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim());
                var label = $"{vm.Title} ({string.Join(", ", parts)})";
                var unitPrice = chosen.Price ?? LocalCartService.ParsePrice(vm.PriceLine);
                _viewModel.Basket.AddVariantFromCatalog(vm, picker.Quantity, unitPrice, variantId, label, chosen.Size, chosen.Color);
                return;
            }
        }

        if (vm.HasPieceOption && !mustWeigh)
        {
            var pkgDlg = new PackageChoiceDialog(vm.Title, vm.PriceLine, vm.Quantity, vm.PieceOption);
            if (await PosDialogHost.ShowModalAsync(pkgDlg, this).ConfigureAwait(true) != true)
                return;

            if (pkgDlg.IsPieceMode)
            {
                var pieceOption = vm.PieceOption!;
                // Остаток товара в каталоге хранится в единицах ПАЧКИ, поэтому для проверки
                // остатка количество штук нужно перевести в эквивалент пачек. Но в саму
                // корзину должно уйти РЕАЛЬНОЕ число штук по цене за штуку — раньше здесь
                // передавалось количество назад в долях пачки по цене целой пачки (0.2 пачки
                // × 200 сом), что арифметически совпадало с суммой, но давало ту же "цену за
                // единицу", что и целая пачка — из-за этого поштучная и упаковочная продажи
                // сливались в одну строку чека вместо двух отдельных.
                var packageEquivalentQty = pkgDlg.Quantity / pieceOption.QuantityInPackage;
                var reservedElsewhere = _viewModel.Basket.GetQuantityInOtherOpenReceipts(vm.Id);
                if (!StockAvailabilityService.CanAddQuantity(
                        vm.Id, packageEquivalentQty, cart, additionalReserved: reservedElsewhere))
                {
                    if (!await ShowNoStockBlockedAsync(vm.Title, vm.Id, mustWeigh: false, allowOverride: true).ConfigureAwait(true))
                        return;
                    if (!await TryReplenishStockForOverrideAsync(vm, pkgDlg.Quantity, mustWeigh: false).ConfigureAwait(true))
                        return;
                }

                _viewModel.Basket.AddProductFromCatalogWithOverride(
                    vm, pkgDlg.Quantity, pieceOption.PieceUnitPrice, pieceOption.Id);
                return;
            }

            qtyToAdd = pkgDlg.Quantity;
        }
        else if (mustWeigh)
        {
            var scale = HardwareModeHelper.UsePhysicalScale()
                ? App.GetRequiredService<ScaleWeightProvider>().Scale
                : null;
            var dlg = new WeighedProductDialog(vm.Title, vm.PriceLine, scale);
            if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true || string.IsNullOrEmpty(dlg.QuantityNormalized))
                return;

            if (!double.TryParse(dlg.QuantityNormalized, NumberStyles.Any, CultureInfo.InvariantCulture, out qtyToAdd) || qtyToAdd <= 0)
                return;
        }
        else
        {
            qtyToAdd = ParseManualQuantity(_viewModel.Basket.ManualQuantity, false);
            if (qtyToAdd <= 0)
            {
                PosMessageBox.Show(this, Tr.T("Укажите корректное количество.", "Туура санды көрсөтүңүз.", "Enter a valid quantity.", "Geçerli bir miktar girin.", "To'g'ri miqdorni kiriting."), Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var reservedInOtherOpenReceipts = _viewModel.Basket.GetQuantityInOtherOpenReceipts(vm.Id);
        if (!StockAvailabilityService.CanAddQuantity(
                vm.Id,
                qtyToAdd,
                cart,
                additionalReserved: reservedInOtherOpenReceipts))
        {
            if (!await ShowNoStockBlockedAsync(vm.Title, vm.Id, mustWeigh, allowOverride: true).ConfigureAwait(true))
                return;
            if (!await TryReplenishStockForOverrideAsync(vm, qtyToAdd, mustWeigh).ConfigureAwait(true))
                return;
        }

        // AddProductFromCatalog читает ManualQuantity в момент вызова (см. BasketPanelViewModel.AddProductFromCatalog),
        // поэтому сюда обязательно нужно положить реальное добавляемое количество, а не заглушку "1" —
        // иначе взвешенный товар всегда добавится как "1" вместо настоящего веса.
        // Сброс поля к "1" после добавления уже делает сам AddProductFromCatalog
        // (UserPreferences.ResetManualAddQtyAfterAdd, включено по умолчанию), поэтому здесь ничего досбрасывать не нужно.
        _viewModel.Basket.ManualQuantity = mustWeigh
            ? qtyToAdd.ToString("0.###", CultureInfo.InvariantCulture)
            : qtyToAdd.ToString("0", CultureInfo.InvariantCulture);
        _viewModel.Basket.AddProductFromCatalog(vm, lineNameOverride);
    }

    /// <summary>Голосовое добавление товара с поштучной упаковкой (2026-09-04) — раньше
    /// BasketPanelViewModel.AddProductByVoice всегда добавляло по цене ЦЕЛОЙ ПАЧКИ, потому что
    /// не знало про HasPieceOption вообще: кассир говорил "касса манго два", а получал 2 пачки
    /// по цене пачки — PackageChoiceDialog и его звуковая подсказка (VoicePromptPlayer)
    /// открывались только при клике по карточке товара (AddProductFromCatalogAsync выше), но не
    /// при голосовом добавлении.
    ///
    /// unitKind (VoiceCommandParser.ExtractQuantity) — если кассир сразу назвал единицу словом
    /// ("20 штук"/"2 пачки"), способ продажи уже однозначен и диалог не нужен вообще: "касса
    /// ессе манго 20 штук" сразу уходит поштучно, "касса ессе манго 2 пачки" — целыми пачками.
    /// PackageChoiceDialog остаётся только на случай голого числа без единицы (UnitKind.None) —
    /// тогда неясно, что кассир имел в виду, и нужно уточнение, как и раньше.</summary>
    internal async Task AddProductByVoiceAsync(CatalogProductTileVm vm, double quantity, VoiceUnitKind unitKind = VoiceUnitKind.None)
    {
        var basket = _viewModel.Basket;
        var mustWeigh = ProductUnitNormalizer.RequiresWeighing(vm);

        if (!vm.HasPieceOption || mustWeigh)
        {
            basket.AddProductByVoice(vm, quantity);
            return;
        }

        if (unitKind == VoiceUnitKind.Piece)
        {
            await AddPieceModeByVoiceAsync(vm, quantity).ConfigureAwait(true);
            return;
        }
        if (unitKind == VoiceUnitKind.Pack)
        {
            basket.AddProductByVoice(vm, quantity);
            return;
        }

        var pkgDlg = new PackageChoiceDialog(vm.Title, vm.PriceLine, vm.Quantity, vm.PieceOption, triggeredByVoice: true);
        if (await PosDialogHost.ShowModalAsync(pkgDlg, this).ConfigureAwait(true) != true)
            return;

        if (!pkgDlg.IsPieceMode)
        {
            basket.AddProductByVoice(vm, pkgDlg.Quantity);
            return;
        }

        await AddPieceModeByVoiceAsync(vm, pkgDlg.Quantity).ConfigureAwait(true);
    }

    /// <summary>Общая часть поштучного добавления — что при явном "N штук" в самой голосовой
    /// команде, что при выборе "Поштучно" в PackageChoiceDialog. Логика проверки остатка та же,
    /// что и в AddProductFromCatalogAsync (клик по карточке) — продублирована, а не вынесена в
    /// общий метод, т.к. у голосового пути нет ни открытия смены, ни диалога взвешивания.</summary>
    private async Task AddPieceModeByVoiceAsync(CatalogProductTileVm vm, double pieceQuantity)
    {
        var basket = _viewModel.Basket;
        var cart = ResolveCartService();
        var pieceOption = vm.PieceOption!;
        var packageEquivalentQty = pieceQuantity / pieceOption.QuantityInPackage;
        var reservedElsewhere = basket.GetQuantityInOtherOpenReceipts(vm.Id);
        if (!StockAvailabilityService.CanAddQuantity(
                vm.Id, packageEquivalentQty, cart, additionalReserved: reservedElsewhere))
        {
            if (!await ShowNoStockBlockedAsync(vm.Title, vm.Id, mustWeigh: false, allowOverride: true).ConfigureAwait(true))
                return;
            if (!await TryReplenishStockForOverrideAsync(vm, pieceQuantity, mustWeigh: false).ConfigureAwait(true))
                return;
        }

        basket.AddProductFromCatalogWithOverride(vm, pieceQuantity, pieceOption.PieceUnitPrice, pieceOption.Id);
    }

    /// <summary>Штрих-код от весов (Штрих-М и совместимые) уже несёт вес в самом коде —
    /// диалог взвешивания здесь не нужен, вес известен из штрих-кода.</summary>
    internal async Task AddWeighedProductWithKnownWeightAsync(CatalogProductTileVm vm, double weightKg)
    {
        if (!_session.IsShiftOpen)
        {
            await OpenShiftAsync().ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                return;
        }

        if (weightKg <= 0)
            return;

        var cart = ResolveCartService();
        var reservedInOtherOpenReceipts = _viewModel.Basket.GetQuantityInOtherOpenReceipts(vm.Id);
        if (!StockAvailabilityService.CanAddQuantity(
                vm.Id, weightKg, cart, additionalReserved: reservedInOtherOpenReceipts))
        {
            if (!await ShowNoStockBlockedAsync(vm.Title, vm.Id, mustWeigh: true, allowOverride: true).ConfigureAwait(true))
                return;
            if (!await TryReplenishStockForOverrideAsync(vm, weightKg, mustWeigh: true).ConfigureAwait(true))
                return;
        }

        _viewModel.Basket.ManualQuantity = weightKg.ToString("0.###", CultureInfo.InvariantCulture);
        _viewModel.Basket.AddProductFromCatalog(vm);
    }

    /// <summary>«Доп. услуга» (2026-09-07): произвольная строка чека без товара — доставка,
    /// упаковка, услуга; как на сайте («Интерфейс кассира» → Корзина → «Доп. услуга»). Тип
    /// «Расход» кладёт строку с отрицательной ценой (вычитается из чека), «Доход» — по умолчанию.
    /// 2026-09-21, по просьбе владельца: «Расход», когда в чеке ещё нет ни одного товара — это
    /// не строка продажи (такую всё равно нельзя оплатить, итог уйдёт в минус, см. PayAsync), а
    /// изъятие денег из кассы. Раньше для этого нужно было идти в «История смен → Изъятие» —
    /// теперь та же кнопка «Доп. услуга» сама оформляет изъятие, если корзина пуста.</summary>
    internal async Task AddCustomItemAsync()
    {
        var dialog = new CustomServiceDialog();
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        if (await PosDialogHost.ShowModalAsync(dialog, this).ConfigureAwait(true) != true)
            return;

        // 2026-10-03: кассир выбрал подсказанный товар каталога — продаём товаром, а не строкой без товара.
        if (dialog.SelectedProduct is { } product)
        {
            await AddProductFromCatalogAsync(product).ConfigureAwait(true);
            return;
        }

        if (dialog.IsExpense && !_viewModel.Basket.HasItems)
        {
            RecordCashWithdrawal(dialog.ServiceName, dialog.Price * dialog.Quantity);
            return;
        }

        _viewModel.Basket.AddCustomItem(dialog.ServiceName, dialog.Price, dialog.Quantity, dialog.IsExpense);
    }

    private void RecordCashWithdrawal(string reason, double amount)
    {
        if (string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.ActiveShiftId))
        {
            _viewModel.Basket.CartMessage = Tr.T(
                "Изъятие можно оформить только при открытой смене.",
                "Акча алып коюуну ачык смена учурунда гана жасоого болот.",
                "Cash out can only be recorded while a shift is open.",
                "Para çıkışı yalnızca vardiya açıkken yapılabilir.",
                "Chiqimni faqat smena ochiq bo'lganda rasmiylashtirish mumkin.");
            return;
        }

        var op = new CashOperationModel
        {
            Type = "Изъятие",
            Kind = CashOperationKind.Withdrawal,
            Amount = (decimal)amount,
            Cashier = NurMarketKassa.AvaloniaHost.App.CurrentUserId ?? "—",
            Reason = reason,
            Comment = reason,
        };
        _ = RecordCashOperationAsync(op);
    }

    /// <summary>«Внесение / изъятие» из бокового меню (2026-09-25, по замечанию владельца: окно
    /// «Внесение» было в коде, но на экране его нечем было открыть — оно жило только в «Истории
    /// смен», которой в меню давно нет, а изъять деньги можно было лишь через «Доп. услуга →
    /// Расход» при пустом чеке).</summary>
    internal async Task OpenCashOperationDialogAsync()
    {
        if (!_session.IsShiftOpen || string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.ActiveShiftId))
        {
            _prompts.ShowWarning(Tr.T(
                "Внесение и изъятие можно оформить только при открытой смене.",
                "Акча салууну жана алып коюуну ачык смена учурунда гана жасоого болот.",
                "Cash in and cash out can only be recorded while a shift is open.",
                "Para girişi ve çıkışı yalnızca vardiya açıkken yapılabilir.",
                "Kirim va chiqimni faqat smena ochiq bo'lganda rasmiylashtirish mumkin."));
            return;
        }

        var dialog = new NewOperationDialog(isDeposit: true);
        if (await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true) != true
            || dialog.ResultOperation is not { } op)
            return;

        await RecordCashOperationAsync(op).ConfigureAwait(true);
    }

    /// <summary>Общая запись внесения/изъятия — и из меню, и из «Доп. услуга → Расход»: журнал
    /// смены, расход в отчёте, остаток в шапке, сервер и приходный/расходный чек. Раньше путь
    /// через «Доп. услугу» и путь через «Историю смен» записывали операцию по-разному.</summary>
    private async Task RecordCashOperationAsync(CashOperationModel op)
    {
        var isWithdrawal = CashOperationModel.ResolveKind(op.Type) == CashOperationKind.Withdrawal;
        ShiftCashOperationsStore.Append(op);

        // 2026-09-23. Изъятие, сделанное отсюда, не попадало в строку «Расход» Z-отчёта: событие
        // смены писал только путь через «Историю смен». Одна и та же операция давала в отчёте
        // разные цифры в зависимости от того, какой кнопкой её сделали.
        if (isWithdrawal)
        {
            ShiftEventsStore.Record(
                ShiftEventsStore.KindExpense,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(op.Id),
                (double)op.Amount,
                op.Note);
        }

        UpdateShiftBalanceUi();
        // На сервер — в движения денег смены (ShiftCashFlowSync), затем свежий остаток смены.
        _ = RefreshShiftBalanceQuietAsync();

        // Приходный/расходный чек обязателен: по нему деньги в ящике сходятся при пересчёте
        // кассы. Раньше изъятие проходило молча — подтвердить его было нечем.
        // Печать — в фоне: медленный принтер не подвешивает кассу (2026-09-25).
        var cashier = NurMarketKassa.PosApp.CurrentUserDisplayName;
        string? printError;
        try
        {
            printError = await Task.Run(() => OperationReceiptPrinter.PrintCashOperation(
                isWithdrawal,
                op.Amount,
                op.Note,
                cashier)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            printError = ex.Message;
        }

        if (printError is not null)
        {
            _viewModel.Basket.CartMessage = printError;
            return;
        }

        _viewModel.Basket.CartMessage = isWithdrawal
            ? Tr.T(
                $"Изъятие оформлено: {op.Amount:0.00} сом.",
                $"Акча алынды: {op.Amount:0.00} сом.",
                $"Cash out recorded: {op.Amount:0.00} som.",
                $"Para çıkışı kaydedildi: {op.Amount:0.00} som.",
                $"Chiqim rasmiylashtirildi: {op.Amount:0.00} so'm.")
            : Tr.T(
                $"Внесение оформлено: {op.Amount:0.00} сом.",
                $"Акча салынды: {op.Amount:0.00} сом.",
                $"Cash in recorded: {op.Amount:0.00} som.",
                $"Para girişi kaydedildi: {op.Amount:0.00} som.",
                $"Kirim rasmiylashtirildi: {op.Amount:0.00} so'm.");
    }

    /// <summary>Неизвестный штрих-код при сканировании (2026-09-07, по просьбе владельца): вместо
    /// голого «Товар не найден» — предложить «Добавить на склад» (открывается карточка нового
    /// товара с уже подставленным штрих-кодом) или «Пропустить». После сохранения каталог
    /// перечитывается и созданный товар сразу кладётся в чек тем же путём, что и при обычном
    /// сканировании (с диалогом «пачка/поштучно», если он настроен).</summary>
    internal async Task OfferAddUnknownProductAsync(string barcode)
    {
        var code = (barcode ?? "").Trim();
        var confirmed = await PosConfirmDialog.ShowModalAsync(
            this,
            Tr.T("Товар не найден", "Товар табылган жок", "Product not found", "Ürün bulunamadı", "Mahsulot topilmadi"),
            Tr.T($"Штрих-код {code} не найден в каталоге. Добавить новый товар на склад?",
                 $"{code} штрих-коду каталогдон табылган жок. Кампага жаңы товар кошолубу?", $"Barcode {code} was not found in the catalog. Add a new product to the warehouse?", $"Barkod {code} katalogda bulunamadı. Depoya yeni ürün eklensin mi?", $"{code} shtrix-kodi katalogda topilmadi. Omborga yangi mahsulot qo'shilsinmi?"),
            Tr.T("Добавить на склад", "Кампага кошуу", "Add to warehouse", "Depoya ekle", "Omborga qo'shish"),
            Tr.T("Пропустить", "Өткөрүп жиберүү", "Skip", "Atla", "O'tkazib yuborish"));
        if (!confirmed)
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        if (catalogApi is null)
        {
            _prompts.ShowToast(Tr.T("Добавление товара недоступно в этом режиме.", "Бул режимде товар кошуу жеткиликсиз.", "Adding a product is not available in this mode.", "Bu modda ürün eklenemez.", "Bu rejimda mahsulot qo'shish mavjud emas."), isWarning: true);
            return;
        }

        var dialog = new ProductEditDialog(catalogApi, null, quickAddMode: true) { Barcode = code };
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (!saved)
            return;

        await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
        await _viewModel.Catalog.RepublishFromLocalAsync().ConfigureAwait(true);

        var created = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase));
        if (created is null)
        {
            _prompts.ShowToast(Tr.T("Товар добавлен на склад.", "Товар кампага кошулду.", "Product added to the warehouse.", "Ürün depoya eklendi.", "Mahsulot omborga qo'shildi."));
            return;
        }

        await AddProductFromCatalogAsync(created).ConfigureAwait(true);
    }

    /// <summary>2026-10-04, ТЗ разработчика NurCRM: скан QR клиента «NURCRM…», а клиента с этим телефоном
    /// в базе нет. Молча не заводим — открываем существующее окно выбора клиента сразу на «Новом клиенте»
    /// с подставленным телефоном; ФИО вводит кассир и жмёт «+ Добавить клиента» (то же добавление, что в
    /// окне оплаты). Возвращает выбранного или добавленного клиента, null — окно закрыли.</summary>
    internal async Task<NurMarketKassa.ViewModels.ClientOption?> OfferNewClientFromQrAsync(string phone)
    {
        var clientsApi = App.AppHost?.Services.GetService<IClientsApiService>();
        if (clientsApi is null)
            return null;

        var picker = new NurMarketKassa.ViewModels.CheckoutViewModel(new CartTotalsCalculator.CartTotals(), "", "", clientsApi)
        {
            NewClientPhone = phone,
        };
        picker.ErrorMessage = Tr.T(
            $"Клиента с номером {phone} нет в базе. Введите имя и нажмите «+ Добавить клиента» — или закройте окно, чтобы продать без клиента.",
            $"{phone} номерлүү кардар базада жок. Атын жазып, «+ Клиентти кошуу» баскычын басыңыз — же кардарсыз сатуу үчүн терезени жабыңыз.",
            $"There is no customer with number {phone}. Enter a name and press “+ Add client” — or close the window to sell without a customer.",
            $"{phone} numaralı müşteri veritabanında yok. Adını girip «+ Müşteri ekle»ye basın — ya da müşterisiz satış için pencereyi kapatın.",
            $"{phone} raqamli mijoz bazada yo'q. Ismini kiriting va «+ Mijoz qo'shish» tugmasini bosing — yoki mijozsiz sotish uchun oynani yoping.");

        await ClientPickerDialog.OpenForNewClient(this, picker).ConfigureAwait(true);
        RestoreScannerFocus();
        return picker.SelectedClient;
    }

    internal async Task ApplyOrderDiscountAsync()
    {
        if (!Authorize(PosPermissions.ApplyDiscount))
            return;
        var dlg = App.GetRequiredService<OrderDiscountDialog>();
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        if (await PosDialogHost.ShowModalAsync(dlg, this).ConfigureAwait(true) != true)
            return;

        if (dlg.ClearRequested)
        {
            if (_viewModel.Basket.ApplyOrderDiscount(null, null, clear: true))
                _viewModel.Basket.CartMessage = Tr.T("Скидка сброшена.", "Арзандатуу алынып салынды.", "Discount cleared.", "İndirim kaldırıldı.", "Chegirma bekor qilindi.");
            return;
        }

        var (previousPercent, previousTotal) = _viewModel.Basket.ReadOrderDiscount();
        if (_viewModel.Basket.ApplyOrderDiscount(dlg.DiscountMode, dlg.DiscountValue))
        {
            if (!await ConfirmSellingAtLossAsync().ConfigureAwait(true))
            {
                _viewModel.Basket.RestoreOrderDiscount(previousPercent, previousTotal);
                _viewModel.Basket.CartMessage = LossDiscountCancelledText();
                return;
            }
            _viewModel.Basket.CartMessage = dlg.DiscountMode == "percent"
                ? Tr.T($"Скидка {dlg.DiscountValue}% применена.", $"{dlg.DiscountValue}% арзандатуу колдонулду.", $"{dlg.DiscountValue}% discount applied.", $"%{dlg.DiscountValue} indirim uygulandı.", $"{dlg.DiscountValue}% chegirma qo'llandi.")
                : Tr.T($"Скидка {dlg.DiscountValue} сом применена.", $"{dlg.DiscountValue} сом арзандатуу колдонулду.", $"{dlg.DiscountValue} som discount applied.", $"{dlg.DiscountValue} som indirim uygulandı.", $"{dlg.DiscountValue} so'm chegirma qo'llandi.");
        }
    }

    internal async Task ReweighCartLineAsync(CartLineItemVm line)
    {
        if (!Authorize(PosPermissions.ViewScales))
            return;
        if (!line.IsWeight || string.IsNullOrWhiteSpace(line.ItemId))
            return;

        var scale = HardwareModeHelper.UsePhysicalScale()
            ? App.GetRequiredService<ScaleWeightProvider>().Scale
            : null;
        var dialog = new WeighedProductDialog(
            line.Title,
            Tr.T($"{line.UnitPrice:0.00} сом", $"{line.UnitPrice:0.00} сом", $"{line.UnitPrice:0.00} som", $"{line.UnitPrice:0.00} som", $"{line.UnitPrice:0.00} so'm"),
            scale,
            line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
            Tr.T("Обновить", "Жаңылоо", "Update", "Güncelle", "Yangilash"));

        if (await PosDialogHost.ShowModalAsync(dialog, this).ConfigureAwait(true) != true ||
            !double.TryParse(dialog.QuantityNormalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var quantity) ||
            quantity <= 0)
            return;

        ResolveCartService().UpdateQuantity(line.ItemId, quantity);
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = Tr.T($"Вес «{line.Title}» обновлён: {quantity:0.###} кг.", $"«{line.Title}» салмагы жаңыртылды: {quantity:0.###} кг.", $"Weight of “{line.Title}” updated: {quantity:0.###} kg.", $"«{line.Title}» ağırlığı güncellendi: {quantity:0.###} kg.", $"«{line.Title}» og'irligi yangilandi: {quantity:0.###} kg.");
    }

    internal async Task ApplyLineDiscountAsync(CartLineItemVm line)
    {
        if (!Authorize(PosPermissions.ApplyDiscount))
            return;
        if (string.IsNullOrWhiteSpace(line.ItemId))
            return;

        var cart = ResolveCartService();
        var (mode, value) = ReadLineDiscount(cart, line.ItemId);
        var dialog = App.GetRequiredService<OrderDiscountDialog>();
        dialog.SetItemMode(line.Title, mode, value);
        if (await PosDialogHost.ShowModalAsync(dialog, this).ConfigureAwait(true) != true)
            return;

        // 2026-09-08: "Максимальная скидка" — реальная серверная настройка (app.nurcrm.kg,
        // Моя компания → Касса), владелец попросил применять её и к скидке на позицию, а не
        // только на весь чек (см. тот же лимит в BasketPanelViewModel.ApplyOrderDiscount).
        // Владелец/админ (ViewSettings) не ограничены, как и на сайте.
        if (!dialog.ClearRequested
            && string.Equals(dialog.DiscountMode, "percent", StringComparison.OrdinalIgnoreCase)
            && MaxDiscountGate.Limit is { } lineLimitPercent
            && !(App.AppHost?.Services.GetService<IPermissionService>()?.HasPermission(PosPermissions.ViewSettings) ?? true)
            && decimal.TryParse(dialog.DiscountValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var enteredLineValue)
            && enteredLineValue > lineLimitPercent)
        {
            _prompts.ShowWarning(Tr.T(
                $"Скидка не может превышать {lineLimitPercent:0.##}% — таково ограничение для сотрудников.",
                $"Арзандатуу {lineLimitPercent:0.##}%дан ашпашы керек — бул кызматкерлер үчүн чектөө.",
                $"The discount can't exceed {lineLimitPercent:0.##}% — that's the limit set for employees.",
                $"İndirim en fazla %{lineLimitPercent:0.##} olabilir — personel için belirlenen sınır budur.",
                $"Chegirma {lineLimitPercent:0.##}%dan oshmasligi kerak — bu xodimlar uchun belgilangan chegara."));
            return;
        }

        // Тот же лимит для скидки, введённой СУММОЙ: диалог позволяет переключить режим, и без
        // этой проверки ограничение обходилось одним переключателем (см. BasketPanelViewModel).
        if (!dialog.ClearRequested
            && !string.Equals(dialog.DiscountMode, "percent", StringComparison.OrdinalIgnoreCase)
            && MaxDiscountGate.Limit is { } lineLimitForSum
            && !(App.AppHost?.Services.GetService<IPermissionService>()?.HasPermission(PosPermissions.ViewSettings) ?? true)
            && decimal.TryParse(dialog.DiscountValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var enteredLineSum))
        {
            var lineGross = (decimal)(line.UnitPrice * line.Quantity);
            if (lineGross > 0m)
            {
                var effectivePercent = enteredLineSum / lineGross * 100m;
                if (effectivePercent > lineLimitForSum)
                {
                    var allowedSum = lineGross * lineLimitForSum / 100m;
                    _prompts.ShowWarning(Tr.T(
                        $"Скидка не может превышать {lineLimitForSum:0.##}% — это {allowedSum:0.00} сом для этой позиции.",
                        $"Арзандатуу {lineLimitForSum:0.##}%дан ашпашы керек — бул позиция үчүн {allowedSum:0.00} сом.",
                        $"The discount can't exceed {lineLimitForSum:0.##}% — that is {allowedSum:0.00} som for this line.",
                        $"İndirim en fazla %{lineLimitForSum:0.##} olabilir — bu satır için {allowedSum:0.00} som.",
                        $"Chegirma {lineLimitForSum:0.##}%dan oshmasligi kerak — bu qator uchun {allowedSum:0.00} so'm."));
                    return;
                }
            }
        }

        ReceiptSnapshotCartEditor.PatchLineDiscount(
            cart,
            line.ItemId,
            dialog.ClearRequested ? null : dialog.DiscountMode,
            dialog.ClearRequested ? null : dialog.DiscountValue);
        _viewModel.Basket.RefreshFromCart();
        // 2026-10-04: скидка увела товар ниже закупки — без «Я знаю что делаю» возвращаем прежнюю скидку.
        if (!dialog.ClearRequested && !await ConfirmSellingAtLossAsync().ConfigureAwait(true))
        {
            ReceiptSnapshotCartEditor.PatchLineDiscount(
                cart,
                line.ItemId,
                mode,
                value?.ToString(CultureInfo.InvariantCulture));
            _viewModel.Basket.RefreshFromCart();
            _viewModel.Basket.CartMessage = LossDiscountCancelledText();
            return;
        }
        _viewModel.Basket.CartMessage = dialog.ClearRequested
            ? Tr.T($"Скидка на «{line.Title}» удалена.", $"«{line.Title}» үчүн арзандатуу алынып салынды.", $"Discount on “{line.Title}” removed.", $"«{line.Title}» için indirim kaldırıldı.", $"«{line.Title}» uchun chegirma olib tashlandi.")
            : Tr.T($"Скидка на «{line.Title}» применена.", $"«{line.Title}» үчүн арзандатуу колдонулду.", $"Discount on “{line.Title}” applied.", $"«{line.Title}» için indirim uygulandı.", $"«{line.Title}» uchun chegirma qo'llandi.");
    }

    /// <summary>2026-10-04, клиент: «если скидку случайно выдать в убыток — предупреждающий экран, и после
    /// подтверждения добавить скидку (кнопка «Я знаю что делаю»)». true — убытка нет или кассир подтвердил.</summary>
    private async Task<bool> ConfirmSellingAtLossAsync()
    {
        if (_viewModel.Basket.LossWarningText() is not { } text)
            return true;
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        var ok = await PosConfirmDialog.ShowModalAsync(
            this,
            Tr.T("Продажа в убыток", "Зыянга сатуу", "Selling at a loss", "Zararına satış", "Zarariga sotish"),
            text + "\n\n" + Tr.T("Применить скидку?", "Арзандатууну колдоносузбу?", "Apply the discount?", "İndirim uygulansın mı?", "Chegirma qo'llansinmi?"),
            Tr.T("Я знаю что делаю", "Эмне кылып жатканымды билем", "I know what I'm doing", "Ne yaptığımı biliyorum", "Nima qilayotganimni bilaman"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            PosConfirmAccent.Danger).ConfigureAwait(true);
        PosLogger.Log(ok ? "Скидка в убыток подтверждена кассиром («Я знаю что делаю»)." : "Скидка в убыток отменена кассиром.", "CART");
        return ok;
    }

    private static string LossDiscountCancelledText() => Tr.T(
        "Скидка не применена: товар ушёл бы в убыток.",
        "Арзандатуу колдонулган жок: товар зыянга сатылмак.",
        "Discount not applied: the item would sell at a loss.",
        "İndirim uygulanmadı: ürün zararına satılacaktı.",
        "Chegirma qo'llanmadi: mahsulot zarariga sotilardi.");

    private static (string? Mode, decimal? Value) ReadLineDiscount(ICartService cart, string itemId)
    {
        foreach (var item in CartDisplayHelper.EnumerateItems(cart.Root))
        {
            if (!string.Equals(CartDisplayHelper.TryItemId(item), itemId, StringComparison.Ordinal))
                continue;

            if (item.TryGetProperty("discount_percent", out var percent) &&
                JsonNumericReader.TryToDouble(percent, out var percentValue))
                return ("percent", (decimal)percentValue);
            if (item.TryGetProperty("discount_total", out var total) &&
                JsonNumericReader.TryToDouble(total, out var totalValue))
                return ("sum", (decimal)totalValue);
            break;
        }

        return (null, null);
    }

    private async Task<bool> MergeDeferredIntoCurrentAsync(IReadOnlyList<DeferredCartEntry> entries)
    {
        if (entries.Count == 0)
            return false;

        if (!_session.IsShiftOpen)
        {
            await OpenShiftAsync().ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                return false;
        }

        foreach (var entry in entries)
        {
            if (!await AddDeferredEntryItemsToActiveCartAsync(entry, applyOrderDiscount: true).ConfigureAwait(true))
                return false;

            DeferredCartsStore.RemoveIds(new[] { entry.Id });
        }

        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = Tr.T($"Позиции из {entries.Count} отложенных чеков добавлены в текущий чек.", $"{entries.Count} калтырылган чектин позициялары учурдагы чекке кошулду.", $"Items from {entries.Count} held receipt(s) added to the current receipt.", $"{entries.Count} bekletilen fişin kalemleri mevcut fişe eklendi.", $"{entries.Count} ta kechiktirilgan chekdagi pozitsiyalar joriy chekka qo'shildi.");
        return true;
    }

    private async Task<bool> OpenDeferredAsSeparateAsync(DeferredCartEntry entry)
    {
        if (!_session.IsShiftOpen)
        {
            await OpenShiftAsync().ConfigureAwait(true);
            if (!_session.IsShiftOpen)
                return false;
        }

        if (!await ValidateDeferredEntryStockAsync(entry).ConfigureAwait(true))
            return false;

        var cart = ResolveCartService();
        if (cart.HasCart && cart.LineCount > 0)
        {
            var deferResult = await ResolveDeferredCartService()
                .DeferCurrentCartAsync(startNewSale: false)
                .ConfigureAwait(true);
            if (!deferResult.IsSuccess)
            {
                PosMessageBox.Show(this, deferResult.ErrorMessage ?? Tr.T("Не удалось сохранить текущий чек.", "Учурдагы чекти сактоо мүмкүн болгон жок.", "Could not save the current receipt.", "Mevcut fiş kaydedilemedi.", "Joriy chekni saqlab bo'lmadi."),
                    Tr.T("Отложенные", "Калтырылган себеттер", "Held carts", "Bekleyen sepetler", "Kechiktirilgan savatlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        StagingCartService.StartEmpty(cart);
        OpenReceiptSnapshot.ApplyDeferredStaging(cart, entry.CartJson);
        DeferredCartsStore.RemoveIds(new[] { entry.Id });
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = Tr.T($"Открыт отложенный чек «{entry.Label}».", $"Калтырылган «{entry.Label}» чеги ачылды.", $"Opened held receipt “{entry.Label}”.", $"Bekletilen fiş açıldı: «{entry.Label}».", $"Kechiktirilgan chek ochildi: «{entry.Label}».");
        return true;
    }

    /// <summary>
    /// Вызывается сразу после успешной оплаты (см. AvaloniaPosCheckoutUiFlow.OpenNextDeferredCartIfAnyAsync).
    /// Если у кассира есть другие отложенные чеки (например, очередь ожидающих клиентов),
    /// автоматически открывает самый старый вместо пустого нового чека.
    /// </summary>
    private async Task OpenNextDeferredCartAsync()
    {
        var oldest = DeferredCartsStore.LoadAll()
            .OrderBy(x => x.SavedAt)
            .FirstOrDefault();
        if (oldest == null)
        {
            // Отложенных чеков нет — можно спокойно свернуть уже отработавшую доп. вкладку
            // ("Чек 2"/"Чек 3") обратно на "Негизги чек". Делаем это здесь, а не сразу после
            // оплаты, чтобы не конфликтовать с восстановлением отложенного чека выше.
            _viewModel.Basket.ReturnToPrimaryReceiptIfSecondary();
            return;
        }

        await OpenDeferredAsSeparateAsync(oldest).ConfigureAwait(true);
    }

    private async Task<bool> AddDeferredEntryItemsToActiveCartAsync(
        DeferredCartEntry entry,
        bool applyOrderDiscount = false)
    {
        if (!await ValidateDeferredEntryStockAsync(entry).ConfigureAwait(true))
            return false;

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
        var root = doc.RootElement;
        var lines = CartDisplayHelper.EnumerateItems(root).ToList();
        if (lines.Count == 0)
            return true;

        var cart = ResolveCartService();
        if (!cart.HasCart)
            StagingCartService.StartEmpty(cart);

        foreach (var line in lines)
        {
            var productId = CartDisplayHelper.TryProductId(line);
            if (string.IsNullOrEmpty(productId))
                continue;

            var qty = CartDisplayHelper.LineQuantity(line);
            if (qty <= 0)
                continue;

            var product = ResolveCatalogProductForCartLine(line, productId);
            if (product == null)
                continue;

            // 2026-09-29: строка варианта (доп. штрихкод) остаётся своей строкой со своим
            // названием, а не сливается в основной товар (см. ReceiptSnapshotCartEditor.AddProduct).
            var variantName = line.TryGetProperty(ReceiptSnapshotCartEditor.VariantNameField, out var variantEl)
                              && variantEl.ValueKind == JsonValueKind.String
                ? variantEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(variantName))
                cart.AddItem(product, qty);
            else
                cart.AddItem(product, qty, variantName);
            ApplyDeferredLineDiscount(cart, line, productId);
        }

        if (applyOrderDiscount)
            ApplyDeferredOrderDiscount(cart, root);

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Переносит построчную скидку из снимка отложенного чека на соответствующую строку
    /// текущего чека. Скидка переводится в сумму и накапливается, чтобы слияние нескольких
    /// отложенных чеков с одним товаром не затирало ранее перенесённую скидку.
    /// </summary>
    private static void ApplyDeferredLineDiscount(ICartService cart, JsonElement snapshotLine, string productId)
    {
        var deferredDiscount = DeferredLineDiscountAmount(snapshotLine);
        if (deferredDiscount <= 1e-6)
            return;

        foreach (var item in CartDisplayHelper.EnumerateItems(cart.Root))
        {
            if (!string.Equals(CartDisplayHelper.TryProductId(item), productId, StringComparison.OrdinalIgnoreCase))
                continue;

            var itemId = CartDisplayHelper.TryItemId(item);
            if (string.IsNullOrEmpty(itemId))
                return;

            var total = DeferredLineDiscountAmount(item) + deferredDiscount;
            ReceiptSnapshotCartEditor.PatchLineDiscount(
                cart,
                itemId,
                "sum",
                total.ToString("0.##", CultureInfo.InvariantCulture));
            return;
        }
    }

    private static double DeferredLineDiscountAmount(JsonElement line)
    {
        foreach (var key in new[] { "discount_total", "line_discount", "discount" })
        {
            if (line.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d) && d > 0)
                return d;
        }

        if (line.TryGetProperty("discount_percent", out var p)
            && JsonNumericReader.TryToDouble(p, out var pct) && pct > 0)
        {
            var gross = CartDisplayHelper.LineQuantity(line) * CartDisplayHelper.UnitPrice(line);
            return gross * Math.Min(pct, 100) / 100.0;
        }

        return 0;
    }

    private async Task<bool> ValidateDeferredEntryStockAsync(DeferredCartEntry entry)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
        var requestedProducts = CartDisplayHelper.EnumerateItems(doc.RootElement)
            .Select(line => new
            {
                ProductId = CartDisplayHelper.TryProductId(line),
                Quantity = CartDisplayHelper.LineQuantity(line),
                Title = CartDisplayHelper.ItemName(line),
                MustWeigh = CartDisplayHelper.LineMustWeigh(line),
            })
            .Where(line => !string.IsNullOrWhiteSpace(line.ProductId) && line.Quantity > 0)
            .GroupBy(line => line.ProductId!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ProductId = group.Key,
                Quantity = group.Sum(line => line.Quantity),
                Title = group.First().Title,
                MustWeigh = group.First().MustWeigh,
            });

        var cart = ResolveCartService();
        foreach (var product in requestedProducts)
        {
            var otherOpenReceipts = _viewModel.Basket.GetQuantityInOtherOpenReceipts(product.ProductId);
            if (StockAvailabilityService.CanAddQuantity(
                    product.ProductId,
                    product.Quantity,
                    cart,
                    excludeDeferredEntryId: entry.Id,
                    additionalReserved: otherOpenReceipts))
                continue;

            // allowOverride намеренно не передаётся здесь: возобновление отложенного чека
            // проверяет остаток заново уже после того, как товар мог быть распродан другим
            // кассиром — переопределение нуля остатка не должно молча удвоить продажу.
            await ShowNoStockBlockedAsync(
                    product.Title,
                    product.ProductId,
                    product.MustWeigh,
                    excludeDeferredEntryId: entry.Id)
                .ConfigureAwait(true);
            return false;
        }

        return true;
    }

    private static void ApplyDeferredOrderDiscount(ICartService cart, JsonElement cartRoot)
    {
        if (cartRoot.ValueKind != JsonValueKind.Object)
            return;

        var pct = cartRoot.TryGetProperty("order_discount_percent", out var p)
            ? FormatDiscountScalar(p)
            : "";
        var sum = cartRoot.TryGetProperty("order_discount_total", out var t)
            ? FormatDiscountMoney(t)
            : "";

        if (string.IsNullOrEmpty(pct) && string.IsNullOrEmpty(sum))
            return;

        ReceiptSnapshotCartEditor.PatchOrderDiscount(cart, pct, sum);
    }

    private static string FormatDiscountScalar(JsonElement value) =>
        JsonNumericReader.ToDouble(value).ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatDiscountMoney(JsonElement value) =>
        CartDisplayHelper.FormatMoney(JsonNumericReader.ToDouble(value));

    private static CatalogProductTileVm? ResolveCatalogProductForCartLine(JsonElement line, string productId)
    {
        var fromCache = LocalProductRepository.Instance.TryGetTileBySku(productId)
            ?? CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (fromCache != null)
            return fromCache;

        var title = CartDisplayHelper.ItemName(line);
        if (string.IsNullOrWhiteSpace(title))
            title = productId;

        var price = CartDisplayHelper.FormatMoney(CartDisplayHelper.UnitPrice(line));
        var mustWeigh = CartDisplayHelper.LineMustWeigh(line);
        return new CatalogProductTileVm(productId, title, price + " сом", mustWeigh);
    }

    /// <summary>
    /// Показывает предупреждение о нехватке остатка. Возвращает true только когда
    /// <paramref name="allowOverride"/> задан И кассир нажал «Подтвердить и добавить» — в этом
    /// случае вызывающий код должен добавить товар в чек как обычно (см.
    /// RecordInsufficientStockOverride). Если кассир вместо этого выбрал «Показать похожие
    /// товары» и выбрал один из них (AI-фичи 2026-09-03, п.12), этот метод сам добавляет
    /// выбранный товар в чек (через AddProductFromCatalogAsync) и возвращает false — вызывающий
    /// код должен просто прекратить обработку ИСХОДНОГО товара, что и делает существующий
    /// паттерн "if (!await ShowNoStockBlockedAsync(...)) return;".
    /// </summary>
    private async Task<bool> ShowNoStockBlockedAsync(
        string productName,
        string productId,
        bool mustWeigh,
        string? excludeDeferredEntryId = null,
        bool allowOverride = false)
    {
        var warehouse = StockAvailabilityService.GetWarehouseQuantity(productId);
        var deferred = StockAvailabilityService.CalculateReservedQuantity(productId, excludeDeferredEntryId);
        var otherOpenReceipts = _viewModel.Basket.GetQuantityInOtherOpenReceipts(productId);
        var reservedElsewhere = deferred + otherOpenReceipts;
        var cart = ResolveCartService();
        var inCurrentCart = StockAvailabilityService.GetCurrentCartQuantity(productId, cart);
        var available = StockAvailabilityService.GetAvailableToAdd(
            productId,
            cart,
            excludeDeferredEntryId,
            additionalReserved: otherOpenReceipts);

        var alternatives = FindAlternativesInCategory(productId);

        var dialog = new NoStockDialog(
            productName,
            warehouse,
            inCurrentCart,
            reservedElsewhere,
            available,
            mustWeigh,
            allowOverride,
            hasAlternatives: alternatives.Count > 0);
        var owner = PosDialogHost.ResolveOwner(this);
        var confirmed = await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true;

        if (!confirmed && dialog.RequestedAlternatives)
        {
            var chosen = await ProductAlternativesDialog.ShowAsync(owner, alternatives).ConfigureAwait(true);
            if (chosen != null)
                await AddProductFromCatalogAsync(chosen).ConfigureAwait(true);
        }

        return confirmed;
    }

    /// <summary>Товары той же категории с реальным остатком на складе > 0, кроме самого товара
    /// (AI-фичи 2026-09-03, п.12 — предлагается вместо отсканированного, если его нет в наличии).
    /// Без категории или без совпадений возвращает пустой список — тогда кнопка "Показать
    /// похожие" в NoStockDialog просто не показывается.</summary>
    private static List<(CatalogProductTileVm Product, string Reason)> FindAlternativesInCategory(string productId)
    {
        var current = CatalogCacheService.Products.FirstOrDefault(p => p.Id == productId);
        if (current is null)
            return new List<(CatalogProductTileVm, string)>();

        // 2026-10-02, владелец: «альтернатива, если товар закончился, как в аптеках» — сначала то же
        // действующее вещество, затем похожее название, затем та же категория (ProductAlternatives).
        return ProductAlternatives.Find(current, CatalogCacheService.Products);
    }

    /// <summary>Пополнение склада, запрошенное из строки чека (кнопка «+» или ручной ввод
    /// количества). Плитку товара берём из того же кэша каталога, что и остальная касса, —
    /// дальше работает ровно та же процедура, что и при добавлении товара из каталога.</summary>
    private async Task<bool> ReplenishStockForProductAsync(string productId, double neededQuantity, bool mustWeigh)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return false;

        var tile = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (tile == null)
        {
            _prompts.ShowError(Tr.T(
                "Товар не найден в каталоге — пополнение недоступно.",
                "Товар каталогдон табылган жок — толуктоо мүмкүн эмес.",
                "Product not found in the catalog — restocking is unavailable.",
                "Ürün katalogda bulunamadı — stok eklenemiyor.",
                "Mahsulot katalogda topilmadi — to'ldirish mumkin emas."));
            return false;
        }

        return await TryReplenishStockForOverrideAsync(tile, neededQuantity, mustWeigh).ConfigureAwait(true);
    }

    /// <summary>
    /// Второй шаг после «Подтвердить и добавить»: кассир указывает, сколько реально
    /// добавить на склад. Значение проводится через тот же серверный инвентаризационный
    /// акт, что использует «Ревизия» в Складе (см. WarehouseViewModel.CommitRevisionAsync) —
    /// это гарантирует, что остаток в каталоге/базе действительно обновится, а не просто
    /// "виртуально" обойдёт проверку. Возвращает true только если акт реально проведён —
    /// вызывающий код должен добавить товар в чек и продолжить продажу ТОЛЬКО в этом случае.
    /// </summary>
    private async Task<bool> TryReplenishStockForOverrideAsync(
        CatalogProductTileVm vm, double suggestedQty, bool mustWeigh)
    {
        var addDialog = new AddStockQuantityDialog(vm.Title, suggestedQty, mustWeigh);
        var owner = PosDialogHost.ResolveOwner(this);
        var addQty = await addDialog.ShowDialog<double?>(owner).ConfigureAwait(true);
        if (addQty is not > 0)
            return false;

        var inventoryApi = App.GetRequiredService<IInventoryApiService>();

        // Остаток перечитываем С СЕРВЕРА, а не берём из локального кэша: кэш обновляется раз
        // в ~2 минуты, а акт пополнения отправляется АБСОЛЮТНЫМ остатком (quantity_fact).
        // С устаревшим кэшем это возвращало чужие продажи: касса Б продала 8 (на сервере
        // стало 2), касса А из кэша «10» пополняла на 5 и отправляла 15 — сервер ВЫСТАВЛЯЛ 15
        // вместо правильных 7, и 8 проданных единиц появлялись из воздуха.
        // Тот же приём уже применён в списании со склада (WarehouseViewModel.WriteOffAsync).
        double currentQty;
        try
        {
            var detail = await PosApp.CatalogApi
                .ProductsDetailAsync(vm.Id, CancellationToken.None)
                .ConfigureAwait(true);
            if (detail is not { } el)
            {
                _prompts.ShowError(Tr.T(
                    "Сервер не вернул остаток товара. Пополнение отменено.",
                    "Сервер товардын калдыгын берген жок. Толуктоо жокко чыгарылды.",
                    "The server did not return the product's stock. Restocking canceled.",
                    "Sunucu ürünün stok miktarını döndürmedi. Stok ekleme iptal edildi.",
                    "Server mahsulot qoldig'ini qaytarmadi. To'ldirish bekor qilindi."));
                return false;
            }

            currentQty = StockSyncService.ResolveStockQuantity(el, vm.MustWeigh);
        }
        catch (Exception ex)
        {
            // Без достоверного остатка отправлять абсолютное значение нельзя — отменяем.
            PosLogger.Log($"Пополнение при продаже: не удалось перечитать остаток: {ex.Message}", "WARNING");
            _prompts.ShowError(Tr.T(
                "Нет связи с сервером — пополнение склада отменено.",
                "Сервер менен байланыш жок — кампаны толуктоо жокко чыгарылды.",
                "No connection to the server — restocking canceled.",
                "Sunucuyla bağlantı yok — stok ekleme iptal edildi.",
                "Server bilan aloqa yo'q — omborni to'ldirish bekor qilindi."));
            return false;
        }

        var newQty = currentQty + addQty.Value;

        try
        {
            var sessionId = await inventoryApi
                .CreateSessionAsync(
                    "Пополнение при продаже (нехватка остатка)",
                    new[] { new InventorySessionItem(vm.Id, newQty) },
                    CancellationToken.None)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _prompts.ShowError(Tr.T(
                    "Не удалось создать акт пополнения склада на сервере.",
                    "Кампаны толуктоо актысын серверде түзүү мүмкүн болгон жок.", "Could not create the restock document on the server.", "Sunucuda stok giriş belgesi oluşturulamadı.", "Serverda omborni to'ldirish dalolatnomasini yaratib bo'lmadi."));
                return false;
            }

            await inventoryApi.ApplySessionAsync(sessionId, allowNegative: false, CancellationToken.None)
                .ConfigureAwait(true);

            // Мгновенное оптимистичное обновление плитки (до сетевого ответа полной синхронизации) —
            // тот же VM-экземпляр, что показан в каталоге (StockAvailabilityService/CatalogPanelViewModel
            // читают его же из CatalogCacheService.Products), поэтому PropertyChanged обновит UI сразу же.
            var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, vm.Id, StringComparison.OrdinalIgnoreCase));
            if (tile != null)
            {
                StockSyncService.ApplyQuantityToTileOnUi(tile, newQty, mustWeigh);
                CatalogCacheService.PersistProductStock(vm.Id, newQty, mustWeigh);
            }

            // Плюс настоящая синхронизация каталога с сервером (как по кнопке "Обновить"),
            // запущенная в фоне сразу же — подтягивает авторитетный остаток и любые другие
            // серверные изменения, а не только эту одну позицию.
            _viewModel.Catalog.RefreshCatalogCommand.Execute(null);

            _viewModel.Basket.RecordInsufficientStockOverride(
                vm.Title, suggestedQty, addQty.Value, mustWeigh ? "кг" : "шт.");
            return true;
        }
        catch (ApiException ex)
        {
            _prompts.ShowError(Tr.T(
                $"Не удалось пополнить склад: {ex.Message}",
                $"Кампаны толуктоо мүмкүн болгон жок: {ex.Message}", $"Could not restock: {ex.Message}", $"Stok eklenemedi: {ex.Message}", $"Omborni to'ldirib bo'lmadi: {ex.Message}"));
            return false;
        }
        catch (HttpRequestException)
        {
            _prompts.ShowError(Tr.T(
                "Не удалось пополнить склад — нет сети.",
                "Кампаны толуктоо мүмкүн болгон жок — тармак жок.", "Could not restock — no network connection.", "Stok eklenemedi — ağ bağlantısı yok.", "Omborni to'ldirib bo'lmadi — tarmoq yo'q."));
            return false;
        }
    }

    private ICartService ResolveCartService() =>
        _cartService ??= App.GetRequiredService<ICartService>();

    private IDeferredCartService ResolveDeferredCartService() =>
        _deferredCartService ??= App.GetRequiredService<IDeferredCartService>();

    private static double ParseManualQuantity(string raw, bool mustWeigh)
    {
        raw = (raw ?? "1").Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty))
            return 0;

        return mustWeigh ? Math.Round(qty, 3) : Math.Round(qty, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>«Сменить кассира» (бэклог 2026-09-03, этап 10) — открывает SwitchCashierDialog,
    /// вся реальная логика — в ValidateAndSwitchCashierAsync.</summary>
    internal Task SwitchCashierAsync() =>
        SwitchCashierDialog.ShowAsync(this, ValidateAndSwitchCashierAsync);

    /// <summary>
    /// Проверяет логин/пароль НОВОГО кассира на сервере, и только при успехе закрывает текущую
    /// смену (от имени СТАРОГО кассира — см. комментарий про снимок сессии ниже) и переключает
    /// активную сессию. Ничего не меняется, пока данные не верны — явное требование пользователя
    /// ("не выходит из аккаунта пока не вводит правильный логин и пароль").
    ///
    /// ВАЖНАЯ ДЕТАЛЬ БЕЗОПАСНОСТИ: OnlineOfflineAuthenticationService.LoginAsync при 400/401/403
    /// вызывает _api.ClearSession() — это стирает токены ТЕКУЩЕГО (ещё легитимно вошедшего)
    /// кассира тоже, если новый пароль введён неверно. Поэтому здесь делается снимок текущих
    /// токенов ДО попытки входа и восстановление через RestoreOfflineSession при неудаче —
    /// иначе опечатка в пароле нового кассира вышибала бы старого из системы.
    /// </summary>
    private async Task<(SwitchCashierOutcome Outcome, string? ErrorMessage)> ValidateAndSwitchCashierAsync(
        string email, string password)
    {
        var api = App.AuthApi;
        var oldSnapshot = new OfflineAuthSession
        {
            UserId = App.CurrentUserId ?? "",
            AccessToken = api.AccessToken,
            RefreshToken = api.RefreshToken,
            BranchId = api.ActiveBranchId,
            CashierName = _session.PosCashboxDisplayName ?? "",
        };

        var auth = App.GetRequiredService<IOnlineOfflineAuthenticationService>();
        // 2026-09-26: LoginAsync с rememberMe: false СТИРАЕТ сохранённый вход кассы — после любой
        // смены кассира следующий запуск кассы открывался на окне входа (живой случай). Запоминаем
        // сохранённый вход и в конце кладём на место нужный: нового кассира или прежний.
        var sessionStore = App.GetRequiredService<IAuthSessionManager>();
        UserSession? savedBefore = null;
        try
        {
            savedBefore = await sessionStore.LoadSessionAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Смена кассира: сохранённый вход не прочитан: {ex.Message}", "AUTH");
        }

        async Task RestoreSavedLoginAsync()
        {
            if (savedBefore == null)
                return;
            try
            {
                await sessionStore.SaveSessionAsync(savedBefore).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смена кассира: прежний сохранённый вход не восстановлен: {ex.Message}", "AUTH");
            }
        }

        AuthenticationResult result;
        try
        {
            result = await auth.LoginAsync(email, password, rememberMe: false, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await RestoreSavedLoginAsync().ConfigureAwait(true);
            api.RestoreOfflineSession(oldSnapshot);
            return (SwitchCashierOutcome.InvalidCredentials,
                Tr.T($"Не удалось выполнить вход: {ex.Message}", $"Кирүү мүмкүн болгон жок: {ex.Message}",
                    $"Could not sign in: {ex.Message}", $"Giriş yapılamadı: {ex.Message}", $"Tizimga kirib bo'lmadi: {ex.Message}"));
        }

        if (!result.IsSuccess || result.Session is null)
        {
            // На всякий случай восстанавливаем снимок даже при "мягких" отказах (сеть/сервер) —
            // LoginAsync их не трогает, но явное восстановление здесь безопаснее, чем полагаться
            // на то, что реализация никогда не изменится.
            await RestoreSavedLoginAsync().ConfigureAwait(true);
            api.RestoreOfflineSession(oldSnapshot);
            return (SwitchCashierOutcome.InvalidCredentials, result.ErrorMessage);
        }

        var newSession = result.Session;
        var newSnapshot = new OfflineAuthSession
        {
            UserId = newSession.UserId,
            Login = newSession.Login,
            CashierName = newSession.DisplayName,
            Role = newSession.Role,
            AccessToken = api.AccessToken,
            RefreshToken = api.RefreshToken,
            BranchId = api.ActiveBranchId,
            Permissions = newSession.Permissions.ToList(),
        };

        // Закрываем смену от имени СТАРОГО кассира — временно возвращаем его токены, чтобы
        // отчёт/инкассация были атрибутированы правильно, как при обычном ручном закрытии смены.
        api.RestoreOfflineSession(oldSnapshot);
        var hadOpenShift = _session.IsShiftOpen;
        if (hadOpenShift)
        {
            await CloseShiftAsync().ConfigureAwait(true);
            if (_session.IsShiftOpen)
            {
                // Кассир отменил закрытие смены (или не подтвердил потерю незавершённого чека) —
                // остаёмся под старым кассиром, ничего больше не трогаем.
                await RestoreSavedLoginAsync().ConfigureAwait(true);
                return (SwitchCashierOutcome.Cancelled, null);
            }
        }

        // Смена закрыта (или её не было) — переключаемся на нового кассира.
        api.RestoreOfflineSession(newSnapshot);
        // Касса запоминала вход до смены кассира — теперь запоминает нового кассира.
        if (savedBefore != null)
        {
            try
            {
                await sessionStore.SaveSessionAsync(newSession).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Смена кассира: вход нового кассира не сохранён: {ex.Message}", "AUTH");
            }
        }
        _session.CurrentUserId = newSession.UserId;
        _session.CurrentUserDisplayName = newSession.DisplayName;
        _session.PosCashboxDisplayName = newSession.DisplayName;
        _session.ActiveTerminal = newSession.BranchId;

        App.AuditDb.LogEvent("auth", "switch_cashier", new { userId = newSession.UserId }, newSession.UserId);
        AccountCatalogIsolation.PrepareForAuthenticatedUser("", newSession.UserId);
        App.GetRequiredService<SyncService>().Start();
        // 2026-09-07: новый кассир может относиться к другой компании/тарифу (см. коммент у
        // SideMenuViewModel.CanViewClients) — SideMenuViewModel переживает смену кассира (то же
        // окно, тот же VM), поэтому без явного обновления пункты меню остаются от старого тарифа
        // до следующего открытия меню. Дожидаемся загрузки компании и обновляем меню сразу, а
        // также перепроверяем ID кассы (см. OnCompanyRefreshedAfterCashierSwitchAsync — тот же
        // баг "cashbox: Обязательное поле", что уже был у логаута).
        var previousCompanyId = CompanyInfoService.LastCompany?.Id;
        _ = CompanyInfoService.RefreshAsync(App.AuthApi, CancellationToken.None)
            .ContinueWith(_ => OnCompanyRefreshedAfterCashierSwitchAsync(previousCompanyId), TaskScheduler.FromCurrentSynchronizationContext())
            .Unwrap();

        return (SwitchCashierOutcome.Success, null);
    }

    /// <summary>Вызывается после того, как CompanyInfoService.RefreshAsync успел подтянуть
    /// компанию НОВОГО кассира. Обновляет боковое меню всегда; если компания реально сменилась —
    /// дополнительно сбрасывает кэш ID кассы (App.PosCashboxId/PosApp.PosCashboxId) и сохранённый
    /// PreferredCashboxId в настройках (он мог указывать на кассу СТАРОЙ компании) и заново
    /// прогоняет RefreshShiftStateAsync, чтобы для новой компании подобралась своя касса —
    /// иначе сервер отвечал "cashbox: Обязательное поле" при попытке открыть смену (тот же баг,
    /// что уже был пофикшен для логаута в NavigateToLoginAsync, но не для смены кассира).</summary>
    private async Task OnCompanyRefreshedAfterCashierSwitchAsync(string? previousCompanyId)
    {
        _viewModel.SideMenu.RefreshEntitlements();

        if (string.Equals(previousCompanyId, CompanyInfoService.LastCompany?.Id, StringComparison.Ordinal))
            return;

        // Компания сменилась вместе с кассиром — значит, и локальные данные должны быть
        // её собственными: история продаж, смены, бонусы, отложенные чеки. Раньше здесь
        // сбрасывался только ID кассы, а цифры оставались от прежней компании.
        AccountDataIsolation.SwitchTo(CompanyInfoService.LastCompany?.Id);

        App.PosCashboxId = null;
        PosApp.PosCashboxId = null;
        _session.PosCashboxDisplayName = null;
        UserPreferences.Instance.PreferredCashboxId = null;
        UserPreferences.Instance.PreferredCashboxName = null;
        UserPreferences.Instance.SaveToDisk();

        try
        {
            await RefreshShiftStateAsync(_windowCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
