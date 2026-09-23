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

    internal Task OpenDeferredCartsAsync()
    {
        var dlg = new DeferredCartsDialog(new DeferredCartsDialogActions
        {
            MergeIntoCurrentAsync = MergeDeferredIntoCurrentAsync,
            OpenAsSeparateAsync = OpenDeferredAsSeparateAsync,
        });
        PosDialogHost.Show(dlg, this);
        _viewModel.Basket.RefreshFromCart();
        return Task.CompletedTask;
    }

    internal Task OpenCashOperationsAsync()
    {
        var dlg = App.GetRequiredService<CashOperationsDialog>();
        dlg.OpenShiftAction = async cash => await ApplyShiftOpenedAsync(cash).ConfigureAwait(true);
        dlg.CloseShiftAction = async cash => await ApplyShiftClosedAsync(cash).ConfigureAwait(true);
        PosDialogHost.Show(dlg, this);
        return Task.CompletedTask;
    }

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

        if (vm.HasPieceOption && !mustWeigh)
        {
            var pkgDlg = new PackageChoiceDialog(vm.Title, vm.PriceLine, vm.Quantity, vm.PieceOption);
            if (PosDialogHost.Show(pkgDlg, this) != true)
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
            if (PosDialogHost.Show(dlg, this) != true || string.IsNullOrEmpty(dlg.QuantityNormalized))
                return;

            if (!double.TryParse(dlg.QuantityNormalized, NumberStyles.Any, CultureInfo.InvariantCulture, out qtyToAdd) || qtyToAdd <= 0)
                return;
        }
        else
        {
            qtyToAdd = ParseManualQuantity(_viewModel.Basket.ManualQuantity, false);
            if (qtyToAdd <= 0)
            {
                PosMessageBox.Show(this, "Укажите корректное количество.", "Количество",
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
        if (PosDialogHost.Show(pkgDlg, this) != true)
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
    internal Task AddCustomItemAsync()
    {
        var dialog = new CustomServiceDialog();
        if (PosDialogHost.Show(dialog, this) != true)
            return Task.CompletedTask;

        if (dialog.IsExpense && !_viewModel.Basket.HasItems)
        {
            RecordCashWithdrawal(dialog.ServiceName, dialog.Price * dialog.Quantity);
            return Task.CompletedTask;
        }

        _viewModel.Basket.AddCustomItem(dialog.ServiceName, dialog.Price, dialog.Quantity, dialog.IsExpense);
        return Task.CompletedTask;
    }

    private void RecordCashWithdrawal(string reason, double amount)
    {
        if (string.IsNullOrWhiteSpace(NurMarketKassa.PosApp.ActiveShiftId))
        {
            _viewModel.Basket.CartMessage = Tr.T(
                "Изъятие можно оформить только при открытой смене.",
                "Изъятимди ачык смена учурунда гана жасаса болот.",
                "A withdrawal can only be recorded while a shift is open.",
                "Bir çekim yalnızca vardiya açıkken kaydedilebilir.",
                "Chiqim faqat smena ochiq bo'lganda amalga oshirilishi mumkin.");
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
        ShiftCashOperationsStore.Append(op);

        // 2026-09-23. Изъятие, сделанное отсюда, не попадало в строку «Расход» Z-отчёта: событие
        // смены писал только путь через «Историю смен». Одна и та же операция давала в отчёте
        // разные цифры в зависимости от того, какой кнопкой её сделали.
        if (CashOperationModel.ResolveKind(op.Type) == CashOperationKind.Withdrawal)
        {
            ShiftEventsStore.Record(
                ShiftEventsStore.KindExpense,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(op.Id),
                (double)op.Amount,
                op.Comment);
        }

        _viewModel.Basket.CartMessage = Tr.T(
            $"Изъятие оформлено: {amount:0.00} сом.",
            $"Изъятим жасалды: {amount:0.00} сом.",
            $"Withdrawal recorded: {amount:0.00} som.",
            $"Çekim kaydedildi: {amount:0.00} som.",
            $"Chiqim amalga oshirildi: {amount:0.00} som.");
    }

    /// <summary>Неизвестный штрих-код при сканировании (2026-09-07, по просьбе владельца): вместо
    /// голого «Товар не найден» — предложить «Добавить на склад» (открывается карточка нового
    /// товара с уже подставленным штрих-кодом) или «Пропустить». После сохранения каталог
    /// перечитывается и созданный товар сразу кладётся в чек тем же путём, что и при обычном
    /// сканировании (с диалогом «пачка/поштучно», если он настроен).</summary>
    internal async Task OfferAddUnknownProductAsync(string barcode)
    {
        var code = (barcode ?? "").Trim();
        var confirmed = PosConfirmDialog.Show(
            this,
            Tr.T("Товар не найден", "Товар табылган жок", "Product not found", "Ürün bulunamadı", "Mahsulot topilmadi"),
            Tr.T($"Штрих-код {code} не найден в каталоге. Добавить новый товар на склад?",
                 $"{code} штрих-коду каталогдон табылган жок. Кампага жаңы товар кошолубу?"),
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

    internal Task ApplyOrderDiscountAsync()
    {
        if (!Authorize(PosPermissions.ApplyDiscount))
            return Task.CompletedTask;
        var dlg = App.GetRequiredService<OrderDiscountDialog>();
        if (PosDialogHost.Show(dlg, this) != true)
            return Task.CompletedTask;

        if (dlg.ClearRequested)
        {
            if (_viewModel.Basket.ApplyOrderDiscount(null, null, clear: true))
                _viewModel.Basket.CartMessage = "Скидка сброшена.";
            return Task.CompletedTask;
        }

        if (_viewModel.Basket.ApplyOrderDiscount(dlg.DiscountMode, dlg.DiscountValue))
        {
            _viewModel.Basket.CartMessage = dlg.DiscountMode == "percent"
                ? $"Скидка {dlg.DiscountValue}% применена."
                : $"Скидка {dlg.DiscountValue} сом применена.";
        }
        return Task.CompletedTask;
    }

    internal Task ReweighCartLineAsync(CartLineItemVm line)
    {
        if (!Authorize(PosPermissions.ViewScales))
            return Task.CompletedTask;
        if (!line.IsWeight || string.IsNullOrWhiteSpace(line.ItemId))
            return Task.CompletedTask;

        var scale = HardwareModeHelper.UsePhysicalScale()
            ? App.GetRequiredService<ScaleWeightProvider>().Scale
            : null;
        var dialog = new WeighedProductDialog(
            line.Title,
            $"{line.UnitPrice:0.00} сом",
            scale,
            line.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
            Tr.T("Обновить", "Жаңылоо", "Update", "Güncelle", "Yangilash"));

        if (PosDialogHost.Show(dialog, this) != true ||
            !double.TryParse(dialog.QuantityNormalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var quantity) ||
            quantity <= 0)
            return Task.CompletedTask;

        ResolveCartService().UpdateQuantity(line.ItemId, quantity);
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = $"Вес «{line.Title}» обновлён: {quantity:0.###} кг.";
        return Task.CompletedTask;
    }

    internal Task ApplyLineDiscountAsync(CartLineItemVm line)
    {
        if (!Authorize(PosPermissions.ApplyDiscount))
            return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(line.ItemId))
            return Task.CompletedTask;

        var cart = ResolveCartService();
        var (mode, value) = ReadLineDiscount(cart, line.ItemId);
        var dialog = App.GetRequiredService<OrderDiscountDialog>();
        dialog.SetItemMode(line.Title, mode, value);
        if (PosDialogHost.Show(dialog, this) != true)
            return Task.CompletedTask;

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
                $"İndirim %{lineLimitPercent:0.##}'i geçemez — personel için belirlenen sınır budur.",
                $"Chegirma {lineLimitPercent:0.##}%dan oshmasligi kerak — bu xodimlar uchun belgilangan chegara."));
            return Task.CompletedTask;
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
                        $"İndirim %{lineLimitForSum:0.##}'i geçemez — bu satır için {allowedSum:0.00} som eder.",
                        $"Chegirma {lineLimitForSum:0.##}%dan oshmasligi kerak — bu qator uchun {allowedSum:0.00} so'm."));
                    return Task.CompletedTask;
                }
            }
        }

        ReceiptSnapshotCartEditor.PatchLineDiscount(
            cart,
            line.ItemId,
            dialog.ClearRequested ? null : dialog.DiscountMode,
            dialog.ClearRequested ? null : dialog.DiscountValue);
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = dialog.ClearRequested
            ? $"Скидка на «{line.Title}» удалена."
            : $"Скидка на «{line.Title}» применена.";
        return Task.CompletedTask;
    }

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
        _viewModel.Basket.CartMessage = $"Позиции из {entries.Count} отложенных чеков добавлены в текущий чек.";
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
                PosMessageBox.Show(this, deferResult.ErrorMessage ?? "Не удалось сохранить текущий чек.",
                    "Отложенные", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        StagingCartService.StartEmpty(cart);
        OpenReceiptSnapshot.ApplyDeferredStaging(cart, entry.CartJson);
        DeferredCartsStore.RemoveIds(new[] { entry.Id });
        _viewModel.Basket.RefreshFromCart();
        _viewModel.Basket.CartMessage = $"Открыт отложенный чек «{entry.Label}».";
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

            cart.AddItem(product, qty);
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
    private static List<CatalogProductTileVm> FindAlternativesInCategory(string productId)
    {
        var current = CatalogCacheService.Products.FirstOrDefault(p => p.Id == productId);
        if (current is null || string.IsNullOrWhiteSpace(current.Category))
            return new List<CatalogProductTileVm>();

        return CatalogCacheService.Products
            .Where(p => p.Id != productId
                        && p.Quantity > 1e-6
                        && string.Equals(p.Category, current.Category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Quantity)
            .Take(10)
            .ToList();
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
                "Product not found in the catalog - replenishment unavailable.",
                "Ürün katalogda bulunamadı - stok ekleme kullanılamıyor.",
                "Mahsulot katalogda topilmadi - toldirish mumkin emas."));
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
                    "Сервер товардын калдыгын кайтарган жок. Толуктоо жокко чыгарылды.",
                    "The server did not return the product stock. Replenishment canceled.",
                    "Sunucu ürün stokunu döndürmedi. Stok ekleme iptal edildi.",
                    "Server mahsulot qoldigini qaytarmadi. Toldirish bekor qilindi."));
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
                "Сервер менен байланыш жок — складды толуктоо жокко чыгарылды.",
                "No connection to the server - replenishment canceled.",
                "Sunucuya bağlanılamadı - stok ekleme iptal edildi.",
                "Server bilan aloqa yo'q - toldirish bekor qilindi."));
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
                    "Кампаны толуктоо актысын серверде түзүү мүмкүн болгон жок."));
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
                $"Кампаны толуктоо мүмкүн болгон жок: {ex.Message}"));
            return false;
        }
        catch (HttpRequestException)
        {
            _prompts.ShowError(Tr.T(
                "Не удалось пополнить склад — нет сети.",
                "Кампаны толуктоо мүмкүн болгон жок — тармак жок."));
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
        AuthenticationResult result;
        try
        {
            result = await auth.LoginAsync(email, password, rememberMe: false, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
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
                return (SwitchCashierOutcome.Cancelled, null);
            }
        }

        // Смена закрыта (или её не было) — переключаемся на нового кассира.
        api.RestoreOfflineSession(newSnapshot);
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
