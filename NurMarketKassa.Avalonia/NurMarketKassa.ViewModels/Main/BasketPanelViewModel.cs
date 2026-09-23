using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>
/// Этот файл отвечает за панель чека на главном экране кассира:
/// строки корзины, пересчёт итогов, добавление по штрихкоду и управление текущим чеком.
/// </summary>
public sealed class BasketPanelViewModel : ViewModelBase
{
    public const int MaxOpenReceipts = 10;

    /// <summary>2026-09-17: скан промахнулся мимо локального кэша — прежде чем спросить сервер
    /// напрямую, ждём не дольше этого времени (короткий сетевой запрос не должен подвешивать
    /// сканирование на кассе при плохой связи).</summary>
    private static readonly TimeSpan ServerBarcodeLookupTimeout = TimeSpan.FromSeconds(4);

    /// <summary>Антидребезг: код, который сервер только что не нашёл, не переспрашиваем это
    /// время — иначе повторное сканирование одного и того же несуществующего штрих-кода долбило
    /// бы сервер на каждый скан.</summary>
    private static readonly TimeSpan ServerBarcodeMissTtl = TimeSpan.FromSeconds(45);
    private readonly Dictionary<string, DateTime> _serverBarcodeMissCache = new(StringComparer.Ordinal);

    private readonly ICartService _cart;
    private readonly IUserPrompts _prompts;
    private readonly IPosCheckoutService _checkout;
    private readonly IDeferredCartService _deferredCart;
    private readonly ICustomerDisplayService _customerDisplay;
    private readonly IWindowService _windowService;
    private readonly IDialogService _dialogService;
    private readonly IDispatcher _dispatcher;
    private readonly IPosCheckoutUiFlow? _checkoutUiFlow;
    private readonly IPermissionService? _permissions;
    private readonly Func<string, CatalogProductTileVm?>? _catalogLookup;
    /// <summary>(productId, salePackageId) → максимум для ЭТОЙ строки в её собственных единицах.
    /// Второй параметр обязателен: при поштучной продаже из пачки количество строки считается в
    /// штуках, а остаток каталога — в пачках, и без конвертации 10 штук сравнивались с «3 пачки»
    /// и молча обрезались до 3 (покупателю пробивали 3 вместо 10).</summary>
    private readonly Func<string, string?, double?>? _stockLimitLookup;
    private readonly Func<CatalogProductTileVm, string?, Task>? _addProductFromCatalog;
    private readonly Func<Task>? _openDeferredCarts;
    private readonly Func<Task>? _applyOrderDiscount;
    private readonly Func<CartLineItemVm, Task>? _reweighCartLine;
    private readonly Func<CartLineItemVm, Task>? _applyLineDiscount;
    private readonly IClientsApiService? _clientsApi;
    private readonly Action? _openPayDebt;
    private readonly Func<CatalogProductTileVm, double, Task>? _addWeighedProductWithKnownWeight;
    private readonly Action? _onCheckoutSuccess;
    private readonly Func<Task>? _addCustomItem;
    private readonly Func<string, Task>? _onBarcodeNotFound;

    /// <summary>Пополнение склада прямо из строки чека: id товара, нужное количество,
    /// весовой ли товар. true — склад пополнен и количество можно ставить.</summary>
    private readonly Func<string, double, bool, Task<bool>>? _replenishStock;

    private string _barcodeInput = "";
    private string _manualQuantity = "1";
    private string _activeReceiptTitle = Tr.T("Основной чек", "Негизги чек", "Main receipt", "Ana fiş", "Asosiy chek");
    private double _subtotal;
    private double _discount;
    private double _total;
    private int _lineCount;
    private double _totalQuantity;
    private string _cartMessage = "";
    private bool _isBusy;
    private string _orderDiscountPercent = "";
    private string _orderDiscountSum = "";
    private string _activeSessionId = "";
    private string? _previousSessionId;
    private string? _paidSessionId;
    private string? _paidSessionPreviousId;
    private readonly List<OpenReceiptSession> _sessions = new();
    private bool _suppressTabRebuild;
    private bool _isMoreActionsVisible;
    private double? _lastCashReceivedForDisplay;
    private double? _lastChangeDueForDisplay;
    private readonly List<string> _pendingInsufficientStockNotes = new();

    public BasketPanelViewModel(
        ICartService cart,
        IUserPrompts prompts,
        IPosCheckoutService checkout,
        IDeferredCartService deferredCart,
        ICustomerDisplayService customerDisplay,
        IWindowService windowService,
        IDialogService dialogService,
        IDispatcher dispatcher,
        Func<string, CatalogProductTileVm?>? catalogLookup = null,
        Func<string, string?, double?>? stockLimitLookup = null,
        IPosCheckoutUiFlow? checkoutUiFlow = null,
        Func<CatalogProductTileVm, string?, Task>? addProductFromCatalog = null,
        Func<Task>? openDeferredCarts = null,
        Func<Task>? applyOrderDiscount = null,
        Func<CartLineItemVm, Task>? reweighCartLine = null,
        Func<CartLineItemVm, Task>? applyLineDiscount = null,
        IPermissionService? permissions = null,
        IClientsApiService? clientsApi = null,
        Action? openPayDebt = null,
        Func<CatalogProductTileVm, double, Task>? addWeighedProductWithKnownWeight = null,
        Action? onCheckoutSuccess = null,
        Func<Task>? addCustomItem = null,
        Func<string, Task>? onBarcodeNotFound = null,
        Func<string, double, bool, Task<bool>>? replenishStock = null)
    {
        _cart = cart;
        _prompts = prompts;
        _checkout = checkout;
        _deferredCart = deferredCart;
        _customerDisplay = customerDisplay;
        _windowService = windowService;
        _dialogService = dialogService;
        _dispatcher = dispatcher;
        _catalogLookup = catalogLookup;
        _stockLimitLookup = stockLimitLookup;
        _checkoutUiFlow = checkoutUiFlow;
        _addProductFromCatalog = addProductFromCatalog;
        _openDeferredCarts = openDeferredCarts;
        _applyOrderDiscount = applyOrderDiscount;
        _reweighCartLine = reweighCartLine;
        _applyLineDiscount = applyLineDiscount;
        _permissions = permissions;
        _clientsApi = clientsApi;
        _openPayDebt = openPayDebt;
        _addWeighedProductWithKnownWeight = addWeighedProductWithKnownWeight;
        _onCheckoutSuccess = onCheckoutSuccess;
        _addCustomItem = addCustomItem;
        _onBarcodeNotFound = onBarcodeNotFound;
        _replenishStock = replenishStock;

        AddByBarcodeCommand = new AsyncRelayCommand(AddByBarcodeAsync, CanAddByBarcode);
        PayCommand = new AsyncRelayCommand(PayAsync, () => HasItems && !IsBusy);
        DeferCartCommand = new AsyncRelayCommand(DeferCartAsync, () => HasItems && !IsBusy);
        HoldReceiptCommand = DeferCartCommand;
        DeleteReceiptCommand = new RelayCommand(DeleteActiveReceipt, CanDeleteActiveReceiptCore);
        ClearCartCommand = new AsyncRelayCommand(ClearCartAsync, () => HasItems && !IsBusy);
        NewReceiptCommand = new RelayCommand(CreateNewReceipt);
        SelectReceiptTabCommand = new RelayCommand<ReceiptTabVm>(SelectReceiptTab, tab => tab != null && !tab.IsActive);
        RemoveLineCommand = new AsyncRelayCommand<CartLineItemVm>(RemoveLineAsync, line => line != null && !string.IsNullOrEmpty(line.ItemId));
        IncreaseQuantityCommand = new RelayCommand<CartLineItemVm>(IncreaseQuantity, CanChangeLineQuantity);
        DecreaseQuantityCommand = new RelayCommand<CartLineItemVm>(DecreaseQuantity, CanDecreaseLineQuantity);
        SetQuantityCommand = new RelayCommand<CartLineItemVm>(SetLineQuantity, CanChangeLineQuantity);
        WeighLineCommand = new RelayCommand<CartLineItemVm>(WeighLine, line =>
            line is { IsWeight: true } && !string.IsNullOrEmpty(line.ItemId) && _reweighCartLine != null);
        LineDiscountCommand = new AsyncRelayCommand<CartLineItemVm>(ApplyLineDiscountAsync, line =>
            line != null && HasItems && !IsBusy && _applyLineDiscount != null);
        IncreaseManualQuantityCommand = new RelayCommand(IncreaseManualQuantity);
        DecreaseManualQuantityCommand = new RelayCommand(DecreaseManualQuantity);
        ToggleMoreActionsCommand = new RelayCommand(() => IsMoreActionsVisible = !IsMoreActionsVisible);
        OpenDeferredCartsCommand = new AsyncRelayCommand(OpenDeferredCartsAsync, () => !IsBusy);
        OpenPayDebtCommand = new RelayCommand(() => _openPayDebt?.Invoke());
        ApplyOrderDiscountCommand = new AsyncRelayCommand(ApplyOrderDiscountAsync, () => HasItems && !IsBusy);
        AddCustomItemCommand = new AsyncRelayCommand(AddCustomItemAsync);

        // Смена языка интерфейса: кнопка «Оплатить» и подписи строк чека собираются в коде (2026-09-07).
        Tr.LanguageChanged += () => _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(PayButtonText));
            SyncLinesFromCart();
        });
        ReturnPreviousReceiptCommand = new AsyncRelayCommand(RestoreLastHeldReceiptAsync, () => !IsBusy && HasHeldReceipts);
        RestoreLastHeldReceiptCommand = ReturnPreviousReceiptCommand;

        EnsureCartInitialized();
        EnsurePrimarySession();
        Lines.CollectionChanged += (_, _) => NotifyLineState();
        UpdateCartTotals();
        RebuildReceiptTabs();
    }

    public event EventHandler? StateChanged;

    /// <summary>Сервер отклонил оплату, сославшись на то, что смена не открыта — а локальный
    /// индикатор в шапке (простой флаг "есть ли сохранённый ID смены", без сверки с сервером)
    /// продолжает показывать её открытой. Смена могла открыться офлайн (получила временный ID,
    /// о котором сервер не знает) либо быть закрытой удалённо. MainWindow подписывается и
    /// поправляет локальное состояние/тулбар той же логикой, что и обычное закрытие смены.</summary>
    public event EventHandler? ShiftDesyncDetected;

    public ObservableCollection<CartLineItemVm> Lines { get; } = new();
    public ObservableCollection<ReceiptTabVm> ReceiptTabs { get; } = new();

    public string BarcodeInput
    {
        get => _barcodeInput;
        set
        {
            if (!SetProperty(ref _barcodeInput, value ?? ""))
                return;
            (AddByBarcodeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string ManualQuantity
    {
        get => _manualQuantity;
        set => SetProperty(ref _manualQuantity, value ?? "1");
    }

    public string ActiveReceiptTitle
    {
        get => _activeReceiptTitle;
        set => SetProperty(ref _activeReceiptTitle, value ?? "");
    }

    public double Subtotal
    {
        get => _subtotal;
        private set
        {
            if (!SetProperty(ref _subtotal, value))
                return;
            OnPropertyChanged(nameof(SubtotalDisplay));
        }
    }

    public double Discount
    {
        get => _discount;
        private set
        {
            if (!SetProperty(ref _discount, value))
                return;
            OnPropertyChanged(nameof(DiscountDisplay));
        }
    }

    public double Total
    {
        get => _total;
        private set
        {
            if (!SetProperty(ref _total, value))
                return;
            OnPropertyChanged(nameof(TotalDisplay));
            OnPropertyChanged(nameof(TotalAmount));
            OnPropertyChanged(nameof(PayButtonText));
        }
    }

    public int LineCount
    {
        get => _lineCount;
        private set => SetProperty(ref _lineCount, value);
    }

    public double TotalQuantity
    {
        get => _totalQuantity;
        private set => SetProperty(ref _totalQuantity, value);
    }

    public string SubtotalDisplay => $"{Subtotal.ToString("0.00", CultureInfo.InvariantCulture)} сом";
    public string DiscountDisplay => $"{Discount.ToString("0.00", CultureInfo.InvariantCulture)} сом";
    public string TotalDisplay => $"{Total.ToString("0.00", CultureInfo.InvariantCulture)} сом";
    public string TotalAmount => Total.ToString("0.00", CultureInfo.InvariantCulture);
    public string PayButtonText =>
        Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash");

    public string CartMessage
    {
        get => _cartMessage;
        set
        {
            if (!SetProperty(ref _cartMessage, value ?? ""))
                return;
            OnPropertyChanged(nameof(HasCartMessage));
        }
    }

    public bool HasCartMessage => !string.IsNullOrWhiteSpace(CartMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            (AddByBarcodeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (PayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeferCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (OpenDeferredCartsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyOrderDiscountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReturnPreviousReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeleteReceiptCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsMoreActionsVisible
    {
        get => _isMoreActionsVisible;
        set => SetProperty(ref _isMoreActionsVisible, value);
    }

    public bool HasItems => Lines.Count > 0;
    public bool HasLines => HasItems;
    public bool IsEmpty => Lines.Count == 0;
    public bool HasHeldReceipts => DeferredCartsStore.Count() > 0;
    public bool CanDeleteActiveReceipt => CanDeleteActiveReceiptCore();
    public bool IsAtReceiptLimit => _sessions.Count >= MaxOpenReceipts;

    public double GetQuantityInOtherOpenReceipts(string productId) =>
        _sessions
            .Where(session => session.Id != _activeSessionId)
            .Sum(session => OpenReceiptSnapshot.SumProductQuantity(session.CartJson, productId));

    public ICommand AddByBarcodeCommand { get; }
    public ICommand PayCommand { get; }
    public ICommand DeferCartCommand { get; }
    public ICommand HoldReceiptCommand { get; }
    public ICommand DeleteReceiptCommand { get; }
    public ICommand ClearCartCommand { get; }
    public ICommand NewReceiptCommand { get; }
    public ICommand SelectReceiptTabCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand IncreaseQuantityCommand { get; }
    public ICommand DecreaseQuantityCommand { get; }
    public ICommand SetQuantityCommand { get; }
    public ICommand WeighLineCommand { get; }
    public ICommand LineDiscountCommand { get; }
    public ICommand IncreaseManualQuantityCommand { get; }
    public ICommand DecreaseManualQuantityCommand { get; }
    public ICommand ToggleMoreActionsCommand { get; }
    public ICommand OpenDeferredCartsCommand { get; }
    public ICommand OpenPayDebtCommand { get; }
    public ICommand ApplyOrderDiscountCommand { get; }
    public ICommand AddCustomItemCommand { get; }
    public ICommand ReturnPreviousReceiptCommand { get; }
    public ICommand RestoreLastHeldReceiptCommand { get; }


    public BasketPanelState CaptureState()
    {
        PersistActiveSessionSnapshot();
        return new BasketPanelState
        {
            ActiveSessionId = _activeSessionId,
            Sessions = _sessions
                .Select(session => new OpenReceiptSessionState
                {
                    Id = session.Id,
                    CartJson = OpenReceiptSnapshot.CloneCartJson(session.CartJson),
                })
                .ToList(),
        };
    }

    public void RestoreState(BasketPanelState? state)
    {
        if (state?.Sessions is null || state.Sessions.Count == 0)
            return;

        _sessions.Clear();
        foreach (var savedSession in state.Sessions.Where(s => !string.IsNullOrWhiteSpace(s.Id)))
        {
            _sessions.Add(new OpenReceiptSession
            {
                Id = savedSession.Id,
                CartJson = OpenReceiptSnapshot.CloneCartJson(savedSession.CartJson),
            });
        }

        if (_sessions.Count == 0)
        {
            EnsurePrimarySession(resetCart: true);
            return;
        }

        RenameReceiptSessions();
        _activeSessionId = _sessions.Any(s => s.Id == state.ActiveSessionId)
            ? state.ActiveSessionId
            : _sessions[0].Id;

        var active = GetActiveSession();
        if (active is null)
            return;

        ActiveReceiptTitle = active.BaseName;
        ApplySessionToCart(active);
        SyncLinesFromCart();
        UpdateCartTotals();
        CartMessage = "";
        RaiseCartCommands();
    }
    public void RefreshFromCart()
    {
        SyncLinesFromCart();
        UpdateCartTotals();
    }

    public bool ApplyOrderDiscount(string? mode, string? value, bool clear = false)
    {
        if (!_cart.HasCart || _cart.LineCount == 0)
        {
            _prompts.ShowWarning(Tr.T("Добавьте товары перед применением скидки.", "Арзандатууну колдонуудан мурун товар кошуңуз.", "Add products before applying a discount.", "İndirim uygulamadan önce ürün ekleyin.", "Chegirma qo'llashdan oldin mahsulot qo'shing."));
            return false;
        }

        if (!clear)
        {
            var normalized = OrderDiscountHelper.NormalizeDecimal(value ?? "");
            var isPercent = string.Equals(mode, "percent", StringComparison.OrdinalIgnoreCase);
            var validationError = isPercent
                ? OrderDiscountHelper.ValidatePercent(normalized)
                : OrderDiscountHelper.ValidateSum(normalized);
            if (validationError != null)
            {
                _prompts.ShowWarning(validationError);
                return false;
            }

            // 2026-09-08: "Максимальная скидка" — реальное серверное ограничение (видно на
            // app.nurcrm.kg, Моя компания → Касса), владелец явно попросил, чтобы касса его
            // применяла. На владельца/админа (ViewSettings) не действует, как и на сайте.
            // 2026-09-23: проверка переехала в MaxDiscountGate — одна точка правды для корзины
            // и окна оплаты. Прежнее условие было fail-open: `!(_permissions?.HasPermission(...)
            // ?? true)` снимало потолок, когда сервис прав не подан, то есть отключало
            // ограничение ровно тогда, когда о пользователе ничего не известно.
            if (isPercent
                && MaxDiscountGate.Limit is { } limitPercent
                && double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var enteredPercent)
                && MaxDiscountGate.Exceeds(enteredPercent, isPercent: true, baseSum: 0))
            {
                _prompts.ShowWarning(Tr.T(
                    $"Скидка не может превышать {limitPercent:0.##}% — таково ограничение для сотрудников.",
                    $"Арзандатуу {limitPercent:0.##}%дан ашпашы керек — бул кызматкерлер үчүн чектөө.",
                    $"The discount can't exceed {limitPercent:0.##}% — that's the limit set for employees.",
                    $"İndirim %{limitPercent:0.##}'i geçemez — personel için belirlenen sınır budur.",
                    $"Chegirma {limitPercent:0.##}%dan oshmasligi kerak — bu xodimlar uchun belgilangan chegara."));
                return false;
            }

            if (!isPercent
                && double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var fixedAmount))
            {
                var totals = CartTotalsCalculator.Calculate(_cart.Root);
                var maximum = Math.Max(0, totals.Subtotal - totals.LineDiscounts);
                if (fixedAmount > maximum + 1e-6)
                {
                    _prompts.ShowWarning(Tr.T(
                        $"Скидка не может превышать сумму товаров: {maximum:0.00} сом.",
                        $"Арзандатуу товарлардын суммасынан ашпашы керек: {maximum:0.00} сом."));
                    return false;
                }

                // Тот же лимит «Максимальная скидка», что и для процентов. Без этого его обходили
                // одним переключением режима: сотруднику с лимитом 10% на чеке 1000 сом «10% → 50»
                // запрещалось, а «сумма → 900» проходило — то есть ограничение не работало вовсе.
                if (MaxDiscountGate.Limit is { } limitForSum
                    && !(_permissions?.HasPermission(PosPermissions.ViewSettings) ?? true)
                    && maximum > 1e-6)
                {
                    var effectivePercent = fixedAmount / maximum * 100.0;
                    if (effectivePercent > (double)limitForSum + 1e-6)
                    {
                        var allowedSum = maximum * (double)limitForSum / 100.0;
                        _prompts.ShowWarning(Tr.T(
                            $"Скидка не может превышать {limitForSum:0.##}% — это {allowedSum:0.00} сом для текущего чека.",
                            $"Арзандатуу {limitForSum:0.##}%дан ашпашы керек — учурдагы чек үчүн бул {allowedSum:0.00} сом.",
                            $"The discount can't exceed {limitForSum:0.##}% — that is {allowedSum:0.00} som for this receipt.",
                            $"İndirim %{limitForSum:0.##}'i geçemez — bu fiş için {allowedSum:0.00} som eder.",
                            $"Chegirma {limitForSum:0.##}%dan oshmasligi kerak — bu chek uchun {allowedSum:0.00} so'm."));
                        return false;
                    }
                }
            }
        }

        var percent = !clear && string.Equals(mode, "percent", StringComparison.OrdinalIgnoreCase) ? value : null;
        var total = !clear && !string.Equals(mode, "percent", StringComparison.OrdinalIgnoreCase) ? value : null;
        ReceiptSnapshotCartEditor.PatchOrderDiscount(_cart, percent, total);
        RefreshFromCart();
        return true;
    }

    public void AddProductFromCatalog(CatalogProductTileVm product, string? lineNameOverride = null)
    {
        if (product is null)
            return;

        try
        {
            EnsureCartInitialized();
            var qty = ParseQuantity(ManualQuantity, product.MustWeigh);
            if (string.IsNullOrWhiteSpace(lineNameOverride))
                _cart.AddItem(product, qty);
            else
                _cart.AddItem(product, qty, lineNameOverride);
            SyncLinesFromCart();
            UpdateCartTotals();
            // Количество всегда сбрасывается на 1 после добавления — иначе следующий товар
            // (в том числе штучный после весового или после упаковки) молча добавится с чужим
            // количеством вместо ожидаемой 1 штуки.
            ManualQuantity = "1";
            CartMessage = Tr.T("Товар добавлен.", "Товар кошулду.", "Product added.", "Ürün eklendi.", "Mahsulot qo'shildi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART add failed: {ex}", "CART");
            _prompts.ShowError(Tr.T("Не удалось добавить товар в чек.", "Товарды чекке кошуу мүмкүн болгон жок.", "Could not add the product to the receipt.", "Ürün fişe eklenemedi.", "Mahsulotni chekka qo'shib bo'lmadi."));
        }
    }

    /// <summary>Поштучная продажа из упаковки: количество и цена строки заданы явно вызывающим
    /// кодом (диалог выбора "Целая пачка / Поштучно"), а не полем ManualQuantity. salePackageId —
    /// ID упаковки на сервере (ProductPackageOption.Id): при оформлении уходит как
    /// "sale_package_id" вместо unit_price, как делает сайт — иначе сервер отклоняет цену за
    /// штуку проверкой "не ниже закупочной", даже если она настроена корректно.</summary>
    public void AddProductFromCatalogWithOverride(
        CatalogProductTileVm product, double quantity, double unitPriceOverride, string? salePackageId = null)
    {
        if (product is null || quantity <= 0)
            return;

        try
        {
            EnsureCartInitialized();
            _cart.AddItem(product, quantity, unitPriceOverride, salePackageId);
            SyncLinesFromCart();
            UpdateCartTotals();
            CartMessage = Tr.T("Товар добавлен.", "Товар кошулду.", "Product added.", "Ürün eklendi.", "Mahsulot qo'shildi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART add (piece) failed: {ex}", "CART");
            _prompts.ShowError(Tr.T("Не удалось добавить товар в чек.", "Товарды чекке кошуу мүмкүн болгон жок.", "Could not add the product to the receipt.", "Ürün fişe eklenemedi.", "Mahsulotni chekka qo'shib bo'lmadi."));
        }
    }

    /// <summary>Добавление товара по голосовой команде (VoiceControlService, 2026-09-04) — как
    /// AddProductFromCatalog, но количество приходит явным параметром (распознано из речи), а
    /// не из поля ManualQuantity, которое кассир руками не трогал.</summary>
    public void AddProductByVoice(CatalogProductTileVm product, double quantity)
    {
        if (product is null)
            return;

        try
        {
            EnsureCartInitialized();
            var qty = ParseQuantity(quantity.ToString(CultureInfo.InvariantCulture), product.MustWeigh);
            _cart.AddItem(product, qty);
            SyncLinesFromCart();
            UpdateCartTotals();
            CartMessage = Tr.T($"Голосом добавлено: {product.Title}.", $"Үн менен кошулду: {product.Title}.",
                $"Added by voice: {product.Title}.", $"Sesle eklendi: {product.Title}.", $"Ovoz orqali qo'shildi: {product.Title}.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART voice add failed: {ex}", "CART");
            _prompts.ShowError(Tr.T("Не удалось добавить товар голосом.", "Товарды үн менен кошуу мүмкүн болгон жок.", "Could not add the product by voice.", "Ürün sesle eklenemedi.", "Mahsulotni ovoz orqali qo'shib bo'lmadi."));
        }
    }

    public void CreateNewReceipt()
    {
        if (!TryEnsureReceiptSlotAvailable())
            return;

        PersistActiveSessionSnapshot();

        var session = new OpenReceiptSession
        {
            Id = Guid.NewGuid().ToString("N"),
            CartJson = "{}",
        };
        _sessions.Add(session);
        RenameReceiptSessions();
        _previousSessionId = _activeSessionId;
        _activeSessionId = session.Id;
        ActiveReceiptTitle = session.BaseName;

        _cart.ResetForNewReceipt();
        SyncLinesFromCart();
        UpdateCartTotals();
        CartMessage = Tr.T($"Открыт «{session.BaseName}».", $"«{session.BaseName}» ачылды.",
            $"Opened “{session.BaseName}”.", $"“{session.BaseName}” açıldı.", $"“{session.BaseName}” ochildi.");
        RaiseCartCommands();
    }

    public void ClearAfterShiftClose()
    {
        _cart.Clear();
        _sessions.Clear();
        EnsurePrimarySession();
        SyncLinesFromCart();
        UpdateCartTotals();
        CartMessage = "";
        PushCustomerDisplay();
        RaiseCartCommands();
    }

    private void SelectReceiptTab(ReceiptTabVm? tab)
    {
        if (tab is null || tab.IsActive || string.IsNullOrWhiteSpace(tab.Id))
            return;

        var target = _sessions.FirstOrDefault(s => s.Id == tab.Id);
        if (target is null)
            return;

        PersistActiveSessionSnapshot();
        _previousSessionId = _activeSessionId;
        _activeSessionId = target.Id;
        ActiveReceiptTitle = target.BaseName;
        ApplySessionToCart(target);
        SyncLinesFromCart();
        UpdateCartTotals();
        CartMessage = Tr.T($"Активен «{target.BaseName}».", $"«{target.BaseName}» активдүү.",
            $"Active: “{target.BaseName}”.", $"Etkin: “{target.BaseName}”.", $"Faol: “{target.BaseName}”.");
        RaiseCartCommands();
    }

    private bool CanAddByBarcode() =>
        !IsBusy && !string.IsNullOrWhiteSpace(BarcodeInput);

    private async Task AddByBarcodeAsync()
    {
        await RunOnUiThreadAsync(() => IsBusy = true).ConfigureAwait(false);
        try
        {
            var barcode = BarcodeInput.Trim();

            // 2026-09-15, диагностика живой жалобы ("штрих-М не читает") — снять после того как
            // разберёмся с реальным примером кода весов Штрих-М: без этой строки не видно, что
            // именно пришло со сканера и на каком именно шаге код не распознался.
            PosLogger.Log(
                $"[DEBUG] Barcode scanned: '{barcode}' (len={barcode.Length}), " +
                $"weightLayout={WeightBarcodeParser.Layout}, weightMode={WeightBarcodeParser.Mode}",
                "CART");

            // Прямое совпадение по каталогу проверяем ПЕРВЫМ: у любого настоящего EAN-13
            // штрих-кода, начинающегося с "2", контрольная сумма технически валидна (это
            // свойство всех корректно сгенерированных штрих-кодов, не только весовых), поэтому
            // если сначала пробовать распознать код как весовой, обычные товары с барcode,
            // начинающимся на "2", никогда не находились бы — попытка декодировать вес
            // "успешно" срабатывала бы раньше, чем прямой поиск. Весовые же коды с весов
            // никогда не совпадают ни с одним товаром напрямую (каждое взвешивание уникально),
            // так что они всё равно корректно попадут в ветку ниже.
            var product = _catalogLookup?.Invoke(barcode);

            if (product != null)
            {
                await AddFoundCatalogProductAsync(product, ResolveVariantLineName(product, barcode)).ConfigureAwait(true);
                return;
            }

            // Штрих-код весов (Штрих-М и совместимые) несёт вес прямо в самом коде — обычным
            // поиском по штрих-коду товар так не найти, код на каждое взвешивание уникален.
            if (WeightBarcodeParser.TryParse(barcode, out var weighted))
            {
                PosLogger.Log(
                    $"[DEBUG] Barcode parsed as weight/amount code: productCode={weighted.ProductCode}, kind={weighted.Kind}",
                    "CART");
                var weighedProduct = LocalCartService.FindByEmbeddedCode(weighted.ProductCode);
                if (weighedProduct is null)
                {
                    await RunOnUiThreadAsync(() =>
                        _prompts.ShowWarning(Tr.T(
                            $"Товар с кодом {weighted.ProductCode} не найден в каталоге.",
                            $"{weighted.ProductCode} коду менен товар каталогдон табылган жок."))).ConfigureAwait(false);
                    return;
                }

                var weightKg = weighted.ResolveWeightKg(LocalCartService.ParsePrice(weighedProduct.PriceLine));
                if (_addWeighedProductWithKnownWeight != null)
                    await _addWeighedProductWithKnownWeight(weighedProduct, weightKg).ConfigureAwait(true);
                await RunOnUiThreadAsync(() => BarcodeInput = "").ConfigureAwait(false);
                return;
            }

            PosLogger.Log("[DEBUG] Barcode not in catalog and not a valid weight/amount code.", "CART");

            // 2026-09-17: локальный кэш мог ещё не досинхронизироваться с сайтом (товар
            // добавили/поменяли на сайте только что) — прежде чем сказать кассиру "не найдено",
            // быстро спрашиваем сервер напрямую по этому штрих-коду.
            var serverProduct = await TryFindProductOnServerAsync(barcode).ConfigureAwait(true);
            if (serverProduct != null)
            {
                await AddFoundCatalogProductAsync(serverProduct, ResolveVariantLineName(serverProduct, barcode)).ConfigureAwait(true);
                return;
            }

            if (_onBarcodeNotFound != null)
            {
                // Неизвестный штрих-код (2026-09-07): вместо голого предупреждения — предложить
                // добавить товар на склад (карточка с подставленным кодом) или пропустить.
                await RunOnUiThreadAsync(() => BarcodeInput = "").ConfigureAwait(false);
                await _onBarcodeNotFound(barcode).ConfigureAwait(true);
                return;
            }

            await RunOnUiThreadAsync(() =>
                _prompts.ShowWarning(Tr.T("Товар не найден в каталоге.", "Товар каталогдон табылган жок.", "Product not found in the catalog.", "Ürün katalogda bulunamadı.", "Mahsulot katalogda topilmadi."))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART barcode add failed: {ex}", "CART");
            await RunOnUiThreadAsync(() =>
                _prompts.ShowError(Tr.T("Ошибка добавления товара.", "Товарды кошууда ката кетти.", "Error adding the product.", "Ürün eklenirken hata oluştu.", "Mahsulot qo'shishda xato."))).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    /// <summary>«Дополнительные штрихкоды» (2026-09-21): если отсканированный код — это доп.
    /// штрихкод варианта (не основной штрихкод товара) и у варианта задано название, строка
    /// чека получает комбинированное имя «Название товара + название варианта» (например,
    /// «Асу вода» + «Клубничный» → «Асу вода Клубничный»), как на сайте. Количество варианта
    /// ("Кол-во в упаковке") здесь намеренно не используется — это просто описательное поле
    /// сайта, а не множитель для количества строки чека.</summary>
    private static string? ResolveVariantLineName(CatalogProductTileVm product, string scannedBarcode)
    {
        var variant = product.AlternateBarcodeVariants?.FirstOrDefault(v =>
            string.Equals(v.Barcode?.Trim(), scannedBarcode, StringComparison.OrdinalIgnoreCase));
        if (variant is null || string.IsNullOrWhiteSpace(variant.Name))
            return null;

        return $"{product.Title} {variant.Name}".Trim();
    }

    private async Task AddFoundCatalogProductAsync(CatalogProductTileVm product, string? lineNameOverride = null)
    {
        if (_addProductFromCatalog != null)
            await _addProductFromCatalog(product, lineNameOverride).ConfigureAwait(true);
        else
            await RunOnUiThreadAsync(() => AddProductFromCatalog(product, lineNameOverride)).ConfigureAwait(false);

        await RunOnUiThreadAsync(() => BarcodeInput = "").ConfigureAwait(false);
    }

    /// <summary>2026-09-17: скан не нашёл товар в локальном кэше — прежде чем показать кассиру
    /// "не найдено", быстро спрашиваем сервер напрямую (поиск по штрих-коду). Локальный кэш
    /// синхронизируется периодически и может на минуту-другую отставать от сайта: товар уже
    /// добавлен/изменён в NurCRM, а кассир ещё не видит его при сканировании. Короткий таймаут
    /// и антидребезг промахов — чтобы повторные сканы несуществующего кода не долбили сервер и
    /// не подвешивали интерфейс, если сети нет вовсе.</summary>
    private async Task<CatalogProductTileVm?> TryFindProductOnServerAsync(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode) || PosApp.CatalogApi is null)
            return null;

        if (_serverBarcodeMissCache.TryGetValue(barcode, out var missedAt) &&
            DateTime.UtcNow - missedAt < ServerBarcodeMissTtl)
            return null;

        try
        {
            using var cts = new CancellationTokenSource(ServerBarcodeLookupTimeout);
            var results = await PosApp.CatalogApi.ProductsSearchAsync(barcode, limit: 5, cts.Token).ConfigureAwait(true);
            var dto = results.FirstOrDefault(d => string.Equals(d.Barcode?.Trim(), barcode, StringComparison.OrdinalIgnoreCase));
            if (dto == null)
            {
                _serverBarcodeMissCache[barcode] = DateTime.UtcNow;
                return null;
            }

            var tile = ProductCatalogMapper.TryTile(dto, PosApp.Settings?.ApiBaseUrl ?? "");
            if (tile == null)
            {
                _serverBarcodeMissCache[barcode] = DateTime.UtcNow;
                return null;
            }

            LocalProductRepository.Instance.UpsertFromTiles([tile]);
            var existingIndex = CatalogCacheService.Products.FindIndex(p => p.Id == tile.Id);
            if (existingIndex >= 0)
                CatalogCacheService.Products[existingIndex] = tile;
            else
                CatalogCacheService.Products.Add(tile);
            CatalogCacheService.NotifyCatalogChanged();

            PosLogger.Log($"[DEBUG] Barcode '{barcode}' missing from local cache but found on server: productId={tile.Id}.", "CART");
            return tile;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Server barcode fallback lookup failed for '{barcode}': {ex.GetType().Name}: {ex.Message}", "CART");
            return null;
        }
    }

    private async Task PayAsync()
    {
        if (Lines.Count == 0)
            return;

        // 2026-09-21, живой баг владельца: чек из одной «Доп. услуги» типа «Расход» (без единого
        // товара) реально уходит в минус, но экран показывал «Итог: 0.00» как бесплатную продажу —
        // после клика «Оплатить» сервер отвечал «Внутренняя ошибка сервера (500)» на нонсенсной для
        // него отрицательной продаже. Ловим это здесь, ДО похода на сервер, с понятным сообщением
        // вместо непонятной серверной ошибки (см. CartTotalsCalculator.RawTotal).
        var precheckTotals = CartTotalsCalculator.Calculate(_cart.Root);
        if (precheckTotals.RawTotal < -0.005)
        {
            CartMessage = Tr.T(
                "Итог чека отрицательный из-за «Расхода» — добавьте товар или уменьшите сумму расхода.",
                "Чек жыйынтыгы «Чыгаша» себептүү терс — товар кошуңуз же чыгаша суммасын азайтыңыз.",
                "The receipt total is negative because of an \"Expense\" line — add a product or reduce the expense amount.",
                "\"Gider\" satırı yüzünden fiş toplamı negatif — bir ürün ekleyin veya gider tutarını azaltın.",
                "Chek jami \"Xarajat\" tufayli manfiy — mahsulot qo'shing yoki xarajat summasini kamaytiring.");
            return;
        }

        PosLogger.Log("PAY start", "PAYMENT");

        // Захватываем ID сессии ДО сетевого запроса оплаты: пока он в полёте (может занять
        // секунду и больше), UI-поток свободен, и кассир вполне может успеть переключиться на
        // другую вкладку чека (обычное дело — параллельно обслуживает другого покупателя).
        // Если определять "какую вкладку только что оплатили" уже ПОСЛЕ ответа сервера через
        // "текущая активная сессия", можно по ошибке удалить не оплаченный, а тот чек, на
        // который кассир успел переключиться, — включая чужие ещё не оплаченные товары.
        _paidSessionId = GetActiveSession()?.Id;
        _paidSessionPreviousId = _previousSessionId;

        await RunOnUiThreadAsync(() =>
        {
            IsBusy = true;
            _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Processing, Tr.T("Идёт оплата...", "Төлөм жүрүп жатат...", "Processing payment...", "Ödeme işleniyor...", "To'lov amalga oshirilmoqda..."));
        }).ConfigureAwait(false);

        var paymentStatusActive = false;
        try
        {
            if (_checkoutUiFlow != null)
            {
                PosLogger.Log("PAY prepare checkout (stock check)", "PAYMENT");
                var prepared = await _checkoutUiFlow.PrepareCheckoutAsync().ConfigureAwait(false);
                if (!prepared)
                {
                    PosLogger.Log("PAY aborted: stock blocked", "PAYMENT");
                    await RunOnUiThreadAsync(() =>
                        _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Idle)).ConfigureAwait(false);
                    return;
                }
            }

            var totals = CartTotalsCalculator.Calculate(_cart.Root);
            PosLogger.Log(
                $"PAY dialog open: lines={_cart.LineCount}, total={totals.TotalDue:0.00}",
                "PAYMENT");

            // Диалог оплаты всегда открывается через AvaloniaWindowService на UI-потоке.
            var checkoutVm = new CheckoutViewModel(totals, _orderDiscountPercent, _orderDiscountSum, _clientsApi, _customerDisplay);
            var confirmed = await _windowService
                .ShowDialogAsync<CheckoutViewModel, bool?>(checkoutVm)
                .ConfigureAwait(false);

            if (confirmed != true)
            {
                PosLogger.Log("PAY canceled by cashier", "PAYMENT");
                await RunOnUiThreadAsync(() =>
                    _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Idle)).ConfigureAwait(false);
                return;
            }

            var cashReceived = checkoutVm.CashReceivedForApi;

            PosLogger.Log(
                $"PAY API checkout: method={checkoutVm.PaymentMethod}, cash={cashReceived}, print={checkoutVm.IsPrintReceiptEnabled}",
                "PAYMENT");

            if (_checkoutUiFlow != null)
            {
                await _checkoutUiFlow.ShowPaymentProcessingAsync(totals.TotalDue).ConfigureAwait(false);
                paymentStatusActive = true;
            }

            var result = await _checkout.CheckoutAsync(new PosCheckoutRequest
            {
                PaymentMethod = checkoutVm.PaymentMethod,
                CashReceived = cashReceived,
                PrintReceipt = checkoutVm.IsPrintReceiptEnabled,
                OrderDiscountBody = checkoutVm.PendingOrderDiscountBody,
                ClientId = checkoutVm.ClientId,
                NonCashReceived = checkoutVm.NonCashReceivedForApi,
            }).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var error = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? PaymentErrorMessages.GenericFailure
                    : result.ErrorMessage;
                PosLogger.Log($"PAY failed result: {error}", "PAYMENT");
                if (LooksLikeShiftNotOpenError(error))
                    ShiftDesyncDetected?.Invoke(this, EventArgs.Empty);
                if (_checkoutUiFlow != null)
                {
                    await _checkoutUiFlow.ShowPaymentResultAsync(false, error).ConfigureAwait(false);
                    paymentStatusActive = false;
                }
                await RunOnUiThreadAsync(() =>
                {
                    _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Failed, error);
                    if (_checkoutUiFlow == null)
                        _prompts.ShowError(error);
                }).ConfigureAwait(false);
                return;
            }

            PosLogger.Log(
                $"PAY success: offline={result.SavedOffline}, total={result.TotalAmount:0.00}",
                "PAYMENT");

            LogPendingInsufficientStockOverrideIfAny(result);
            RecordSoldLineItemsForHistory();
            CreditOrRedeemLoyaltyPoints(checkoutVm, result);
            // Каталог должен мгновенно отразить проданный остаток (та же логика, что и после
            // пополнения склада при нулевом остатке — RefreshCatalogCommand делает полную
            // синхронизацию с сервером, а не только точечный пересчёт проданных позиций).
            // ОБЯЗАТЕЛЬНО через диспетчер: к этому месту метод уже выполняется в продолжении
            // после ConfigureAwait(false) (см. комментарий ниже про OpenNextDeferredCartIfAnyAsync)
            // — прямой вызов ICommand.Execute трогает Avalonia UI не с UI-потока и рушит
            // приложение ("Call from invalid thread").
            if (_onCheckoutSuccess != null)
                await _dispatcher.InvokeAsync(_onCheckoutSuccess).ConfigureAwait(false);

            if (_checkoutUiFlow != null)
            {
                var successMessage = result.SavedOffline
                    ? Tr.T(
                        "Оплата сохранена. Данные будут отправлены при восстановлении связи.",
                        "Төлөм сакталды. Байланыш калыбына келгенде маалымат жиберилет.")
                    : Tr.T("Платёж принят. Открываем новый чек.", "Төлөм кабыл алынды. Жаңы чек ачылууда.", "Payment accepted. Opening a new receipt.", "Ödeme alındı. Yeni fiş açılıyor.", "To'lov qabul qilindi. Yangi chek ochilmoqda.");
                await _checkoutUiFlow.ShowPaymentResultAsync(true, successMessage).ConfigureAwait(false);
                paymentStatusActive = false;
            }

            await RunOnUiThreadAsync(() =>
            {
                if (checkoutVm.PaymentMethod == "cash" &&
                    double.TryParse(cashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var cashPaid))
                {
                    _lastCashReceivedForDisplay = cashPaid;
                    // 2026-09-13, живой баг: totals был посчитан ДО открытия диалога оплаты, а
                    // кассир мог внутри него применить скидку на чек или списать баллы лояльности
                    // (checkoutVm.EffectiveTotalDue меняется, totals.TotalDue — нет). С клиента
                    // списывается верная (пересчитанная) сумма, но экран покупателя показывал
                    // сдачу от СТАРОЙ суммы — например, скидка снизила сумму на 85 сом, а сдача на
                    // экране покупателя показывала на 85 сом меньше, чем реально нужно вернуть.
                    _lastChangeDueForDisplay = Math.Max(0, cashPaid - checkoutVm.EffectiveTotalDue);
                }
                else
                {
                    _lastCashReceivedForDisplay = null;
                    _lastChangeDueForDisplay = null;
                }

                _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Success, Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışverişiniz için teşekkürler!", "Xaridingiz uchun rahmat!"));
                SyncLinesFromCart();
                UpdateCartTotals();
                CartMessage = result.SavedOffline
                    ? result.InfoMessage ?? Tr.T("Оплата сохранена локально.", "Төлөм жергиликтүү сакталды.", "Payment saved locally.", "Ödeme yerel olarak kaydedildi.", "To'lov mahalliy saqlandi.")
                    : result.InfoMessage ?? Tr.T("Оплата выполнена. Новый чек открыт.", "Төлөм аткарылды. Жаңы чек ачылды.", "Payment completed. A new receipt has been opened.", "Ödeme tamamlandı. Yeni fiş açıldı.", "To'lov amalga oshirildi. Yangi chek ochildi.");
            }).ConfigureAwait(false);

            if (_checkoutUiFlow != null)
            {
                // Нажатие "Оплатить" в диалоге чекаута уже было подтверждением кассира —
                // чек уже напечатан выше (если PrintReceipt был включён), доп. модалка
                // "Оплата выполнена" с кнопками печати/закрытия только добавляла лишний клик.
                PosLogger.Log("PAY success, no extra confirmation dialog", "PAYMENT");

                // Если у кассира есть другие отложенные чеки (например, очередь ожидающих клиентов),
                // открываем самый старый вместо пустого нового чека.
                // Must run via the dispatcher: by this point we're on a thread-pool continuation
                // (every await above uses ConfigureAwait(false)), but everything this call chain
                // touches (RefreshFromCart -> SyncLinesFromCart -> RaiseCartCommands, window
                // creation) requires the UI thread and previously threw "Call from invalid thread".
                await _dispatcher.InvokeAsync(() => _checkoutUiFlow.OpenNextDeferredCartIfAnyAsync())
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(result.InfoMessage) && result.SavedOffline)
            {
                await _dialogService.ShowInfoAsync(result.InfoMessage).ConfigureAwait(false);
            }

            PosLogger.Log("PAY finished", "PAYMENT");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PAY failed: {ex}", "PAYMENT");
            var error = PaymentErrorMessages.ForCashier(ex);
            if (LooksLikeShiftNotOpenError(error))
                ShiftDesyncDetected?.Invoke(this, EventArgs.Empty);
            var errorShownInStatus = false;
            if (_checkoutUiFlow != null && paymentStatusActive)
            {
                try
                {
                    await _checkoutUiFlow.ShowPaymentResultAsync(false, error).ConfigureAwait(false);
                    errorShownInStatus = true;
                }
                catch (Exception statusEx)
                {
                    PosLogger.Log($"PAY error status dialog failed: {statusEx}", "PAYMENT");
                }
                finally
                {
                    paymentStatusActive = false;
                }
            }
            await RunOnUiThreadAsync(() =>
            {
                _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Failed, error);
                if (!errorShownInStatus)
                    _prompts.ShowError(error);
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
            _ = ResetCustomerDisplayStatusAfterDelayAsync();
        }
    }

    /// <summary>Отличает отказ сервера "смена не открыта" от прочих ошибок оплаты (нехватка
    /// наличных, сбой сети и т.п.) по тексту ответа — отдельного кода ошибки сервер не отдаёт.</summary>
    private static bool LooksLikeShiftNotOpenError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.Contains("смен", StringComparison.OrdinalIgnoreCase) &&
        (error.Contains("не открыт", StringComparison.OrdinalIgnoreCase)
         || error.Contains("откройте смену", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Вызывается из MainWindow.Dialogs.cs (TryReplenishStockForOverrideAsync) после того, как
    /// кассир нажал «Подтвердить и добавить» на предупреждении о нулевом остатке И реально
    /// пополнил склад через инвентаризационный акт — товар уже добавлен в чек, здесь только
    /// запоминается причина для журнала «Некорректные чеки» (см. LogPendingInsufficientStockOverrideIfAny,
    /// который запишет запись после того, как этот чек реально будет оплачен).
    /// </summary>
    public void RecordInsufficientStockOverride(string productName, double soldQuantity, double addedQuantity, string unit)
    {
        var soldText = FormatQty(soldQuantity);
        var addedText = FormatQty(addedQuantity);
        _pendingInsufficientStockNotes.Add(
            $"{productName} — продано {soldText} {unit} (остаток был равен нулю, пополнено на складе: {addedText} {unit})");
    }

    private static string FormatQty(double value) =>
        value.ToString(value % 1 < 1e-6 ? "0" : "0.###", CultureInfo.InvariantCulture);

    private void LogPendingInsufficientStockOverrideIfAny(PosCheckoutResult result)
    {
        if (_pendingInsufficientStockNotes.Count == 0)
            return;

        try
        {
            IrregularReceiptsStore.Append(new IrregularReceiptEntry
            {
                Tag = IrregularReceiptEntry.InsufficientStock,
                CashierName = PosApp.CurrentUserId,
                Note = string.Join("; ", _pendingInsufficientStockNotes),
                Total = result.TotalAmount,
                CartJson = result.CartJsonSnapshot ?? "{}",
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"IRREGULAR receipt log failed: {ex}", "IRREGULAR");
        }
        finally
        {
            _pendingInsufficientStockNotes.Clear();
        }
    }

    /// <summary>Пишет проданные позиции в локальную историю (AI-фичи 2026-09-03, п.1 — прогноз
    /// пополнения склада). ДОЛЖНО вызываться до SyncLinesFromCart() — тот заново наполняет Lines
    /// для следующего чека, стирая только что проданные позиции.</summary>
    private void RecordSoldLineItemsForHistory()
    {
        try
        {
            var soldAt = DateTime.UtcNow;

            // 2026-09-23. Раньше сюда писалась UnitPrice — цена ДО скидки на позицию, а сама
            // скидка строки не сохранялась нигде. Вся аналитика, которая считает по этой
            // таблице (выгрузка в Excel и Word, Telegram-сводки, ABC, сезонность), завышала
            // выручку ровно на сумму построчных скидок. Пишем фактическую цену за единицу:
            // LineTotal уже за вычетом скидки строки.
            //
            // Скидка на ВЕСЬ чек и оплата бонусами сюда не входят намеренно — они лежат
            // отдельно (ClientLoyaltyStore.RecordSaleAdjustment) и вычитаются на уровне отчёта,
            // иначе вычлись бы дважды.
            var lines = Lines
                .Where(l => !string.IsNullOrWhiteSpace(l.ProductId))
                .Select(l => (
                    l.ProductId,
                    l.Title,
                    l.Quantity,
                    EffectiveUnitPrice: l.Quantity > 0
                        ? Math.Max(0, l.LineTotal) / l.Quantity
                        : l.UnitPrice,
                    soldAt))
                .ToList();

            if (lines.Count > 0)
                SoldLineItemsStore.AppendSale(lines);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Sold line item history write failed: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Бонусная программа (AI-фичи 2026-09-04) — локальная, см. ClientLoyaltyStore.
    /// Начисляет заработанные баллы и списывает те, что кассир применил в диалоге оплаты, ОДНИМ
    /// изменением баланса (earned - redeemed), а не двумя отдельными записями.</summary>
    private void CreditOrRedeemLoyaltyPoints(CheckoutViewModel checkoutVm, PosCheckoutResult result)
    {
        // Скидку и списанные бонусы записываем ВСЕГДА, ещё до проверок бонусной программы:
        // скидка бывает и на чеке без клиента, а отчёту смены она нужна не меньше. Сервер
        // разделить эти две величины не может — он получает их одной суммой.
        RecordSaleAdjustmentForReports(checkoutVm, result);

        if (!UserPreferences.Instance.LoyaltyEnabled)
            return;

        var clientId = checkoutVm.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            return;

        try
        {
            var delta = checkoutVm.EarnedPointsPreview - checkoutVm.PointsRedeemed;
            ClientLoyaltyStore.AdjustBalance(clientId, delta);

            // 2026-09-13, живой баг: без привязки к ID продажи полный возврат чека не мог
            // найти и отменить эти же баллы — покупатель сохранял бонусы за возвращённый
            // товар навсегда. ID есть только у продажи, прошедшей ОНЛАЙН (сервер возвращает
            // его в ответе на чек) — офлайн-очередь получает серверный ID только после
            // реплея, к этому моменту здесь уже поздно; для офлайн-продаж запись просто не
            // создаётся, как и раньше.
            // 2026-09-21: здесь стоял PosSaleRowFormatter.TrySaleId — извлекатель для СТРОКИ
            // СПИСКА продаж. Он знает только id/sale_id/uuid/pk и не умеет ни вложенный
            // "sale": {...}, ни обёртку "data": {...}, которую checkout иногда возвращает.
            // На таком ответе id не находился, баллы при этом уже были начислены выше —
            // то есть возврат чека не мог их отменить НИКОГДА. CheckoutResponseHelper.TrySaleId
            // разбирает все эти формы и используется везде, где id продажи берут из checkout'а.
            var saleId = result.CheckoutResponse is { } response
                ? CheckoutResponseHelper.TrySaleId(response)
                : null;
            if (!string.IsNullOrEmpty(saleId))
            {
                ClientLoyaltyStore.RecordTransaction(saleId, clientId, delta);
            }
            else
            {
                // Баланс уже изменён (иначе списанные баллы остались бы у покупателя), но без
                // id продажи привязки нет и возврат эти баллы не отменит. Для офлайн-продажи это
                // известное ограничение, для онлайновой — признак, что ответ сервера изменился.
                PosLogger.Log(
                    $"Лояльность: баллы изменены на {delta:0.##}, но id продажи не найден — возврат их не отменит " +
                    $"(offline={result.SavedOffline}).",
                    "WARNING");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Loyalty balance update failed: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Складывает в локальную таблицу скидку чека и ту её часть, что оплачена
    /// бонусами — отсюда отчёты смены берут строки «Скидки» и «Оплачено бонусами».
    /// Для офлайн-продажи id ещё нет, и запись просто не создаётся (как и у бонусов).</summary>
    private void RecordSaleAdjustmentForReports(CheckoutViewModel checkoutVm, PosCheckoutResult result)
    {
        try
        {
            var saleId = result.CheckoutResponse is { } response
                ? CheckoutResponseHelper.TrySaleId(response)
                : null;
            if (string.IsNullOrEmpty(saleId))
                return;

            var discount = checkoutVm.OrderDiscountAmount + checkoutVm.PointsRedeemed;
            ClientLoyaltyStore.RecordSaleAdjustment(
                saleId, PosApp.ActiveShiftId, discount, checkoutVm.PointsRedeemed);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Shift adjustment write failed: {ex.Message}", "WARNING");
        }
    }

    private async Task ResetCustomerDisplayStatusAfterDelayAsync()
    {
        try
        {
            await Task.Delay(4000).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                _lastCashReceivedForDisplay = null;
                _lastChangeDueForDisplay = null;
                _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Idle);
                PushCustomerDisplay();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PAY status reset failed: {ex}", "PAYMENT");
        }
    }

    /// <summary>public (не private): нужен извне для автосохранения текущего чека при
    /// принудительном выходе — например, когда во время работы истекает подписка компании
    /// (см. MainWindow.HandleSubscriptionExpiredDuringSessionAsync, в другой сборке).</summary>
    public async Task DeferCartAsync()
    {
        await RunOnUiThreadAsync(() => IsBusy = true).ConfigureAwait(false);
        try
        {
            if (!HasItems)
            {
                await RunOnUiThreadAsync(() =>
                    _prompts.ShowWarning(Tr.T("Корзина пуста — нечего откладывать.", "Себет бош — калтырууга эч нерсе жок.", "The cart is empty — nothing to hold.", "Sepet boş — beklemeye alınacak bir şey yok.", "Savat bo'sh — kutish uchun hech narsa yo'q."))).ConfigureAwait(false);
                return;
            }

            PersistActiveSessionSnapshot();

            var result = await _deferredCart
                .DeferCurrentCartAsync(startNewSale: false)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                await RunOnUiThreadAsync(() =>
                    _prompts.ShowWarning(result.ErrorMessage ?? Tr.T("Не удалось отложить чек.", "Чекти калтыруу мүмкүн болгон жок.", "Could not hold the receipt.", "Fiş beklemeye alınamadı.", "Chekni kutishga qo'yib bo'lmadi."))).ConfigureAwait(false);
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                _cart.ResetForNewReceipt();
                PersistActiveSessionSnapshot();
                SyncLinesFromCart();
                UpdateCartTotals();
                CartMessage = Tr.T($"Отложено: «{result.Label}». Текущий чек очищен.", $"Калтырылды: «{result.Label}». Учурдагы чек тазаланды.",
                    $"Held: “{result.Label}”. Current receipt cleared.", $"Beklemeye alındı: “{result.Label}”. Geçerli fiş temizlendi.",
                    $"Kutishga qo'yildi: “{result.Label}”. Joriy chek tozalandi.");
                NotifyHeldReceiptsChanged();
                RaiseCartCommands();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"DEFER failed: {ex}", "DEFER");
            await RunOnUiThreadAsync(() =>
                _prompts.ShowError(Tr.T("Не удалось отложить чек.", "Чекти калтыруу мүмкүн болгон жок.", "Could not hold the receipt.", "Fiş beklemeye alınamadı.", "Chekni kutishga qo'yib bo'lmadi."))).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private async Task RestoreLastHeldReceiptAsync()
    {
        await RunOnUiThreadAsync(() => IsBusy = true).ConfigureAwait(false);
        try
        {
            var latest = DeferredCartsStore.TryGetLatest();
            if (latest is null)
            {
                await RunOnUiThreadAsync(() =>
                    _prompts.ShowWarning(Tr.T("Нет отложенных чеков для возврата.", "Кайтаруу үчүн калтырылган чектер жок.", "No held receipts to restore.", "Geri getirilecek bekletilen fiş yok.", "Qaytarish uchun kutilayotgan chek yo'q."))).ConfigureAwait(false);
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                PersistActiveSessionSnapshot();
                var currentHasItems = HasItems;
                var currentJson = OpenReceiptSnapshot.CloneCartJson(OpenReceiptSnapshot.Capture(_cart).CartJson);

                if (currentHasItems)
                {
                    // Вариант Б: SWAP — текущий уходит в архив.
                    DeferredCartsStore.Add(new DeferredCartEntry
                    {
                        Label = BuildHeldLabel(Tr.T("Обмен", "Алмашуу", "Swap", "Değişim", "Almashtirish")),
                        CartJson = currentJson,
                    });
                }

                ApplyCartJson(latest.CartJson);
                DeferredCartsStore.RemoveIds(new[] { latest.Id });

                SyncLinesFromCart();
                UpdateCartTotals();
                CartMessage = currentHasItems
                    ? Tr.T("Чеки обменяны с последним отложенным.", "Чектер акыркы калтырылган менен алмаштырылды.", "Receipts swapped with the last held one.", "Fişler son bekletilenle değiştirildi.", "Cheklar oxirgi kutilayotgan bilan almashtirildi.")
                    : Tr.T($"Возвращён отложенный чек «{latest.Label}».", $"«{latest.Label}» калтырылган чеги кайтарылды.",
                        $"Restored held receipt “{latest.Label}”.", $"Bekletilen fiş geri getirildi: “{latest.Label}”.",
                        $"Kutilayotgan chek qaytarildi: “{latest.Label}”.");
                NotifyHeldReceiptsChanged();
                RaiseCartCommands();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"RESTORE held failed: {ex}", "DEFER");
            await RunOnUiThreadAsync(() =>
                _prompts.ShowError(Tr.T("Не удалось вернуть отложенный чек.", "Калтырылган чекти кайтаруу мүмкүн болгон жок.", "Could not restore the held receipt.", "Bekletilen fiş geri getirilemedi.", "Kutilayotgan chekni qaytarib bo'lmadi."))).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private bool CanDeleteActiveReceiptCore() => _sessions.Count > 1 && !IsBusy;

    private void DeleteActiveReceipt()
    {
        var active = GetActiveSession();
        if (active is null)
            return;

        if (_sessions.Count <= 1)
        {
            _prompts.ShowWarning(Tr.T("«Основной чек» удалить нельзя.", "«Негизги чекти» өчүрүүгө болбойт.", "The “Main receipt” cannot be deleted.", "“Ana fiş” silinemez.", "“Asosiy chek”ni o'chirib bo'lmaydi."));
            return;
        }

        var index = _sessions.FindIndex(s => s.Id == active.Id);
        if (index < 0)
            return;

        _sessions.RemoveAt(index);
        RenameReceiptSessions();
        var nextIndex = Math.Clamp(index - 1, 0, _sessions.Count - 1);
        var next = _sessions[nextIndex];
        _activeSessionId = next.Id;
        ActiveReceiptTitle = next.BaseName;
        ApplySessionToCart(next);
        SyncLinesFromCart();
        UpdateCartTotals();
        CartMessage = Tr.T($"Чек удалён. Активен «{next.BaseName}».", $"Чек өчүрүлдү. «{next.BaseName}» активдүү.",
            $"Receipt deleted. Active: “{next.BaseName}”.", $"Fiş silindi. Etkin: “{next.BaseName}”.", $"Chek o'chirildi. Faol: “{next.BaseName}”.");
        RaiseCartCommands();
    }

    private void ClearReceipt()
    {
        _cart.ResetForNewReceipt();
        PersistActiveSessionSnapshot();
        SyncLinesFromCart();
        UpdateCartTotals();
        CartMessage = "";
        RaiseCartCommands();
    }

    private async Task ClearCartAsync()
    {
        if (!HasItems || IsBusy)
            return;

        if (!await _prompts.ConfirmAsync(Tr.T("Очистить текущую корзину?", "Учурдагы себетти тазалайсызбы?", "Clear the current cart?", "Mevcut sepeti temizle?", "Joriy savatni tozalaysizmi?")).ConfigureAwait(false))
            return;

        // 2026-09-16, живой баг аудита ("обход PIN на очистку корзины") — удаление ОДНОЙ позиции
        // (RemoveLineAsync ниже) требует код доступа CartDelete, а полная очистка корзины раньше
        // спрашивала только "точно?" без кода — кассир без права на удаление мог обнулить всю
        // корзину в обход защиты, хотя это как минимум не менее разрушительное действие.
        if (!DemandPermission(PosPermissions.DeleteCartItem))
            return;
        if (!await DemandEmployeeAccessCodeAsync(
                NurMarketKassa.Services.EmployeeAccessGate.CartDelete,
                Tr.T("Очистка корзины", "Себетти тазалоо", "Clearing the cart", "Sepeti temizleme", "Savatni tozalash"),
                Tr.T("Введите свой код доступа, чтобы очистить корзину.",
                    "Себетти тазалоо үчүн жеке кодуңузду киргизиңиз.",
                    "Enter your access code to clear the cart.",
                    "Sepeti temizlemek için erişim kodunuzu girin.",
                    "Savatni tozalash uchun kirish kodingizni kiriting."))
                .ConfigureAwait(false))
            return;

        await _dispatcher.InvokeAsync(ClearReceipt).ConfigureAwait(false);
        _prompts.ShowToast(Tr.T("Корзина очищена.", "Себет тазаланды.", "Cart cleared.", "Sepet temizlendi.", "Savat tozalandi."));
    }

    private async Task RemoveLineAsync(CartLineItemVm? line)
    {
        if (line is null || string.IsNullOrWhiteSpace(line.ItemId))
            return;
        if (!DemandPermission(PosPermissions.DeleteCartItem))
            return;
        if (!await DemandEmployeeAccessCodeAsync(
                NurMarketKassa.Services.EmployeeAccessGate.CartDelete,
                Tr.T("Удаление товара", "Товарды өчүрүү", "Deleting product", "Ürün silme", "Mahsulotni o'chirish"),
                Tr.T($"Введите свой код доступа, чтобы удалить «{line.Title}» из чека.",
                    $"«{line.Title}» товарын чектен өчүрүү үчүн жеке кодуңузду киргизиңиз.",
                    $"Enter your access code to remove \"{line.Title}\" from the receipt.",
                    $"\"{line.Title}\" ürününü fişten silmek için erişim kodunuzu girin.",
                    $"\"{line.Title}\" mahsulotini chekdan o'chirish uchun kirish kodingizni kiriting."))
                .ConfigureAwait(false))
            return;

        _cart.RemoveItem(line.ItemId);
        SyncLinesFromCart();
        UpdateCartTotals();
        RaiseCartCommands();
    }

    private static bool CanChangeLineQuantity(CartLineItemVm? line) =>
        line != null && !string.IsNullOrEmpty(line.ItemId);

    private static bool CanDecreaseLineQuantity(CartLineItemVm? line) =>
        CanChangeLineQuantity(line) && line!.Quantity > (line.IsWeight ? 0.1 : 1) + 1e-6;

    private void IncreaseQuantity(CartLineItemVm? line)
    {
        if (!CanChangeLineQuantity(line))
            return;

        if (!string.IsNullOrWhiteSpace(line!.ProductId) && _stockLimitLookup != null)
            line.MaxStockQuantity = _stockLimitLookup(line.ProductId, line.SalePackageId);

        var step = line.IsWeight ? 0.1 : 1;
        var next = Math.Round(line.Quantity + step, line.IsWeight ? 3 : 0);
        if (line.MaxStockQuantity is { } limit && next > limit + 1e-6)
        {
            // Не тупик: предлагаем пополнить склад — ровно так же, как при добавлении товара
            // из каталога. Фоновая задача, потому что команда кнопки «+» синхронная.
            _ = OfferReplenishThenSetQuantityAsync(line, next, limit);
            return;
        }

        _cart.UpdateQuantity(line.ItemId, next);
        RefreshChangedLine(line);
    }

    /// <summary>Упёрлись в остаток при изменении количества ПРЯМО В ЧЕКЕ. До 2026-09-22 здесь
    /// показывалось тупиковое «Достигнут лимит остатка» с единственной кнопкой «Закрыть», хотя
    /// пополнение склада в кассе есть и на пути «добавить из каталога» предлагается всегда.
    /// Теперь спрашиваем и, если кассир согласился и акт пополнения реально прошёл, ставим
    /// запрошенное количество.</summary>
    private async Task OfferReplenishThenSetQuantityAsync(CartLineItemVm line, double desired, double limit)
    {
        var formattedLimit = limit.ToString(line.IsWeight ? "0.###" : "0", CultureInfo.InvariantCulture);
        CartMessage = Tr.T($"Достигнут лимит остатка: {formattedLimit} {line.Unit}.", $"Калдык чеги жетти: {formattedLimit} {line.Unit}.",
            $"Stock limit reached: {formattedLimit} {line.Unit}.", $"Stok sınırına ulaşıldı: {formattedLimit} {line.Unit}.",
            $"Qoldiq chegarasiga yetildi: {formattedLimit} {line.Unit}.");

        if (_replenishStock == null || string.IsNullOrWhiteSpace(line.ProductId))
        {
            _prompts.ShowWarning(CartMessage);
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(Tr.T(
            $"На складе только {formattedLimit} {line.Unit}. Продолжить и пополнить склад?",
            $"Кампада бары {formattedLimit} {line.Unit}. Улантып, кампаны толуктайсызбы?",
            $"Only {formattedLimit} {line.Unit} in stock. Continue and replenish?",
            $"Stokta yalnızca {formattedLimit} {line.Unit} var. Devam edip stok eklensin mi?",
            $"Omborda faqat {formattedLimit} {line.Unit} bor. Davom etib, omborni to'ldirasizmi?"))
            .ConfigureAwait(false);
        if (!confirmed)
            return;

        if (!await _replenishStock(line.ProductId!, desired, line.IsWeight).ConfigureAwait(false))
            return;

        await RunOnUiThreadAsync(() =>
        {
            // Остаток перечитываем заново: акт пополнения уже прошёл, и прежний лимит устарел.
            if (_stockLimitLookup != null)
                line.MaxStockQuantity = _stockLimitLookup(line.ProductId!, line.SalePackageId);

            var allowed = line.MaxStockQuantity is { } fresh && desired > fresh + 1e-6 ? fresh : desired;
            _cart.UpdateQuantity(line.ItemId, allowed);
            RefreshChangedLine(line);
            CartMessage = string.Empty;
        }).ConfigureAwait(false);
    }

    private void DecreaseQuantity(CartLineItemVm? line)
    {
        if (!CanChangeLineQuantity(line))
            return;

        var minimum = line!.IsWeight ? 0.1 : 1;
        if (line.Quantity <= minimum + 1e-6)
        {
            CartMessage = line.IsWeight
                ? Tr.T("Минимальный вес — 0,1 кг.", "Минималдуу салмак — 0,1 кг.", "Minimum weight is 0.1 kg.", "Minimum ağırlık 0,1 kg'dır.", "Minimal og'irlik — 0,1 kg.")
                : Tr.T("Меньше 1 нельзя.", "1ден аз болбойт.", "Cannot be less than 1.", "1'den az olamaz.", "1 dan kam bo'lishi mumkin emas.");
            return;
        }

        var step = line.IsWeight ? 0.1 : 1;
        var next = Math.Round(line.Quantity - step, line.IsWeight ? 3 : 0);
        if (next < minimum)
            next = minimum;

        _cart.UpdateQuantity(line.ItemId, next);
        RefreshChangedLine(line);
    }

    private void SetLineQuantity(CartLineItemVm? line)
    {
        if (!CanChangeLineQuantity(line))
            return;

        var minimum = line!.IsWeight ? 0.1 : 1;
        if (!double.TryParse(line.QuantityInput.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var typed)
            || typed <= 0)
        {
            // Не удалось разобрать ввод — возвращаем поле к актуальному количеству.
            line.QuantityInput = line.QuantityDisplay;
            return;
        }

        if (!string.IsNullOrWhiteSpace(line.ProductId) && _stockLimitLookup != null)
            line.MaxStockQuantity = _stockLimitLookup(line.ProductId, line.SalePackageId);

        var next = Math.Round(typed, line.IsWeight ? 3 : 0);
        if (next < minimum)
            next = minimum;

        if (line.MaxStockQuantity is { } limit && next > limit + 1e-6)
        {
            // Как и у кнопки «+»: сначала предлагаем пополнить склад, и только если кассир
            // отказался — обрезаем количество до остатка.
            _ = OfferReplenishThenSetQuantityAsync(line, next, limit);
            return;
        }

        _cart.UpdateQuantity(line.ItemId, next);
        RefreshChangedLine(line);
    }

    private void RefreshChangedLine(CartLineItemVm line)
    {
        var updated = _cart.Items.FirstOrDefault(item =>
            string.Equals(item.Id, line.ItemId, StringComparison.Ordinal));
        if (updated is null)
        {
            SyncLinesFromCart();
            return;
        }

        // Keep the same row and RepeatButton alive while the pointer is held.
        // Recreating Lines here stops Avalonia's repeat sequence after one tick.
        line.Quantity = updated.Quantity;
        line.LineTotal = (double)updated.LineTotal;
        line.DiscountAmount = (double)updated.LineDiscount;
        line.DiscountPercent = updated.DiscountPercent is { } percent ? (double)percent : null;
        line.FixedDiscountAmount = updated.FixedDiscountAmount is { } fixedAmount ? (double)fixedAmount : null;
        UpdateCartTotals();
        RaiseCartCommands();
    }

    private void WeighLine(CartLineItemVm? line)
    {
        if (line is null || !line.IsWeight || string.IsNullOrWhiteSpace(line.ItemId))
            return;
        if (!DemandPermission(PosPermissions.ViewScales))
            return;

        if (_reweighCartLine != null)
            _ = _reweighCartLine(line);
    }

    private async Task ApplyLineDiscountAsync(CartLineItemVm? line)
    {
        if (line is null || string.IsNullOrWhiteSpace(line.ItemId) || _applyLineDiscount == null)
            return;
        if (!DemandPermission(PosPermissions.ApplyDiscount))
            return;
        if (!await DemandCashierPasswordAsync(
                Tr.T("Изменение позиции", "Позицияны өзгөртүү", "Editing line item", "Kalem düzenleme", "Pozitsiyani tahrirlash"),
                Tr.T($"Введите пароль кассы, чтобы изменить «{line.Title}» в чеке.",
                    $"«{line.Title}» товарын чекте өзгөртүү үчүн касса паролун киргизиңиз."))
                .ConfigureAwait(false))
            return;

        _ = _applyLineDiscount(line);
    }

    private bool DemandPermission(string permission)
    {
        if (_permissions is null || _permissions.HasPermission(permission))
            return true;
        PosLogger.Log($"Permission denied: {permission}", "WARNING");
        _prompts.ShowWarning(Tr.T("Недостаточно прав для выполнения этой операции.", "Бул операцияны аткарууга укук жетишсиз.", "Insufficient permissions to perform this operation.", "Bu işlemi gerçekleştirmek için yetkiniz yok.", "Bu amalni bajarish uchun huquq yetarli emas."));
        return false;
    }

    /// <summary>Второй фактор для удаления/изменения позиций чека — пароль кассы
    /// (Company.cashier_password) сверх обычной проверки прав. Если пароль не настроен
    /// на бэкенде (пусто/null), проверка отключена — не ломает существующие компании.</summary>
    private async Task<bool> DemandCashierPasswordAsync(string title, string message)
    {
        var password = CompanyInfoService.LastCompany?.CashierPassword;
        if (string.IsNullOrEmpty(password))
            return true;

        return await _prompts.ConfirmWithPasswordAsync(title, message, password).ConfigureAwait(false);
    }

    /// <summary>2026-09-08: личный код доступа сотрудника (EmployeeAccessGate) для действия
    /// action — не активен для владельца/админа (есть ViewSettings) и не активен вовсе, пока
    /// владелец не впишет хотя бы один код в Настройки → Сотрудники.</summary>
    private async Task<bool> DemandEmployeeAccessCodeAsync(string action, string title, string message)
    {
        if (!NurMarketKassa.Services.EmployeeAccessGate.IsActiveFor(action, _permissions))
            return true;

        return await _prompts.ConfirmWithCodeAsync(title, message,
            entered => NurMarketKassa.Services.EmployeeAccessGate.TryValidate(action, entered)).ConfigureAwait(false);
    }

    private void IncreaseManualQuantity()
    {
        if (!double.TryParse(ManualQuantity.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
            || qty < 1)
            qty = 1;
        // Округляем до 3 знаков (как вес), а не до целого — иначе введённый вручную дробный вес
        // (например "0.750" для весового товара) после клика "+" превращался бы в целое число.
        ManualQuantity = Math.Round(qty + 1, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void DecreaseManualQuantity()
    {
        if (!double.TryParse(ManualQuantity.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
            || qty <= 1)
        {
            ManualQuantity = "1";
            return;
        }

        ManualQuantity = Math.Round(qty - 1, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void RefreshReceiptTabs()
    {
        if (_suppressTabRebuild)
            return;

        PersistActiveSessionSnapshot();
        RebuildReceiptTabs();
    }

    private void RebuildReceiptTabs()
    {
        ReceiptTabs.Clear();
        foreach (var session in _sessions)
        {
            var isActive = session.Id == _activeSessionId;
            var (lines, total) = isActive
                ? (LineCount, Total)
                : GetSessionSummary(session.CartJson);
            ReceiptTabs.Add(new ReceiptTabVm(
                session.Id,
                FormatTabTitle(session.BaseName, lines, total),
                isActive,
                SelectReceiptTabCommand));
        }

        (SelectReceiptTabCommand as RelayCommand<ReceiptTabVm>)?.RaiseCanExecuteChanged();
    }

    private void EnsurePrimarySession(bool resetCart = false)
    {
        if (_sessions.Count == 0)
        {
            var primary = new OpenReceiptSession
            {
                Id = Guid.NewGuid().ToString("N"),
                BaseName = Tr.T("Основной чек", "Негизги чек", "Main receipt", "Ana fiş", "Asosiy chek"),
                CartJson = "{}",
            };
            _sessions.Add(primary);
            _activeSessionId = primary.Id;
            ActiveReceiptTitle = primary.BaseName;
        }

        RenameReceiptSessions();

        if (resetCart)
        {
            _cart.ResetForNewReceipt();
            var active = GetActiveSession();
            if (active != null)
                active.CartJson = "{}";
        }
    }

    private OpenReceiptSession? GetActiveSession() =>
        _sessions.FirstOrDefault(s => s.Id == _activeSessionId) ?? _sessions.FirstOrDefault();

    private void PersistActiveSessionSnapshot()
    {
        var active = GetActiveSession();
        if (active is null)
            return;

        active.CartJson = OpenReceiptSnapshot.Capture(_cart).CartJson;
    }

    private void RenameReceiptSessions()
    {
        for (var index = 0; index < _sessions.Count; index++)
            _sessions[index].BaseName = index == 0 ? Tr.T("Основной чек", "Негизги чек", "Main receipt", "Ana fiş", "Asosiy chek") : $"Чек {index + 1}";

        var active = GetActiveSession();
        if (active != null)
            ActiveReceiptTitle = active.BaseName;
    }

    private void ApplySessionToCart(OpenReceiptSession session) =>
        ApplyCartJson(session.CartJson);

    /// <summary>После успешной оплаты доп. вкладки ("Чек 2", "Чек 3"...) она свою роль
    /// выполнила — оставлять пустую вкладку висеть было неудобно. Возвращаемся на ТУ вкладку,
    /// с которой кассир переключился на оплаченную (а не всегда на "Негизги чек") — если он
    /// оплачивал Чек 2, придя туда с Чек 3, после оплаты должен снова оказаться на Чек 3.
    /// Если такой вкладки уже нет (тоже успели закрыть/оплатить) — откатываемся на первичную.
    /// Публичный: вызывается из MainWindow ПОСЛЕ проверки отложенных чеков
    /// (OpenNextDeferredCartIfAnyAsync) — если у кассира есть отложенный чек, восстановление
    /// отложенного в приоритете.
    ///
    /// Удаляет строго ID сессии, захваченный в PayAsync ДО сетевого запроса оплаты (см.
    /// _paidSessionId/_paidSessionPreviousId), а не "текущую активную сессию" на момент
    /// вызова — за время запроса (может занять больше секунды) кассир вполне мог успеть
    /// переключиться на другую вкладку, и тогда "текущая активная" была бы уже ЧУЖОЙ, ещё
    /// не оплаченной вкладкой с товарами. Если кассир к этому моменту уже смотрит на другую
    /// вкладку — просто убираем оплаченную пустую вкладку из списка, не трогая то, что у
    /// него сейчас открыто.</summary>
    public void ReturnToPrimaryReceiptIfSecondary()
    {
        var paidId = _paidSessionId;
        var previousId = _paidSessionPreviousId;
        _paidSessionId = null;
        _paidSessionPreviousId = null;
        if (string.IsNullOrEmpty(paidId))
            return;

        var index = _sessions.FindIndex(s => s.Id == paidId);
        if (index <= 0)
            return;

        var wasStillActive = _activeSessionId == paidId;

        _sessions.RemoveAt(index);
        RenameReceiptSessions();
        RebuildReceiptTabs();

        if (!wasStillActive)
            return;

        var target = (!string.IsNullOrEmpty(previousId) && previousId != paidId
            ? _sessions.FirstOrDefault(s => s.Id == previousId)
            : null) ?? _sessions[0];

        _activeSessionId = target.Id;
        ActiveReceiptTitle = target.BaseName;
        ApplySessionToCart(target);
        SyncLinesFromCart();
        UpdateCartTotals();
        RaiseCartCommands();
    }

    private void ApplyCartJson(string? cartJson)
    {
        if (string.IsNullOrWhiteSpace(cartJson) || cartJson == "{}")
        {
            _cart.ResetForNewReceipt();
            return;
        }

        new OpenReceiptSnapshot { CartJson = cartJson }.ApplyTo(_cart);
        if (!_cart.HasCart)
            _cart.ResetForNewReceipt();
    }

    private bool TryEnsureReceiptSlotAvailable()
    {
        if (_sessions.Count < MaxOpenReceipts)
            return true;

        ShowReceiptLimitReached();
        return false;
    }

    private void ShowReceiptLimitReached()
    {
        var message = Tr.T(
            "Достигнут лимит открытых чеков (максимум 10). Завершите или удалите существующие чеки.",
            "Ачык чектердин чеги жетти (максимум 10). Учурдагы чектерди бүтүрүңүз же өчүрүңүз.");
        _prompts.ShowWarning(message);
        _ = _dialogService.ShowInfoAsync(message);
    }

    private static string BuildHeldLabel(string prefix) =>
        $"{prefix} #{DeferredCartsStore.Count() + 1} ({DateTime.Now:HH:mm})";

    private void NotifyHeldReceiptsChanged()
    {
        OnPropertyChanged(nameof(HasHeldReceipts));
        (ReturnPreviousReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static (int Lines, double Total) GetSessionSummary(string cartJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            var root = doc.RootElement;
            var lines = OpenReceiptSnapshot.CountLines(cartJson);
            var total = root.ValueKind == System.Text.Json.JsonValueKind.Object
                ? CartTotalsCalculator.Calculate(root).TotalDue
                : 0;
            return (lines, total);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string FormatTabTitle(string baseName, int lineCount, double total) =>
        $"{baseName} • {lineCount} тов. • {total.ToString("0.00", CultureInfo.InvariantCulture)} сом";

    private void EnsureCartInitialized()
    {
        if (_cart.HasCart)
            return;

        _cart.ResetForNewReceipt();
    }

    private void SyncLinesFromCart()
    {
        _suppressTabRebuild = true;
        try
        {
            Lines.Clear();
            var itemNumber = 1;
            foreach (var item in _cart.Items)
            {
                // Insert at the top: the cashier scans items in sequence and wants the most
                // recently added one visible without scrolling, not buried at the bottom.
                // ItemNumber still follows scan order (#1 = first item), only the row position flips.
                Lines.Insert(0, new CartLineItemVm
                {
                    ItemNumber = Math.Min(itemNumber++, 99999),
                    ItemId = item.Id ?? "",
                    ProductId = item.ProductId ?? "",
                    Barcode = item.Barcode ?? "",
                    Code = string.IsNullOrWhiteSpace(item.Barcode)
                        ? ""
                        : _catalogLookup?.Invoke(item.Barcode)?.Article ?? "",
                    Title = item.Name,
                    Unit = item.MustWeigh ? "кг" : "шт",
                    UnitPrice = (double)item.UnitPrice,
                    IsWeight = item.MustWeigh,
                    SalePackageId = item.SalePackageId,
                    MaxStockQuantity = string.IsNullOrWhiteSpace(item.ProductId)
                        ? null
                        : _stockLimitLookup?.Invoke(item.ProductId, item.SalePackageId),
                    Quantity = item.Quantity,
                    LineTotal = (double)item.LineTotal,
                    DiscountAmount = (double)item.LineDiscount,
                    DiscountPercent = item.DiscountPercent is { } percent ? (double)percent : null,
                    FixedDiscountAmount = item.FixedDiscountAmount is { } fixedAmount ? (double)fixedAmount : null,
                    RemoveCommand = RemoveLineCommand,
                    IncreaseCommand = IncreaseQuantityCommand,
                    DecreaseCommand = DecreaseQuantityCommand,
                    WeighCommand = WeighLineCommand,
                    DiscountCommand = LineDiscountCommand,
                    SetQuantityCommand = SetQuantityCommand,
                });
            }
        }
        finally
        {
            _suppressTabRebuild = false;
        }

        RaiseCartCommands();
        PersistActiveSessionSnapshot();
        RebuildReceiptTabs();
        PushCustomerDisplay();
    }

    private void UpdateCartTotals()
    {
        if (!_cart.HasCart)
        {
            Subtotal = 0;
            Discount = 0;
            Total = 0;
            LineCount = 0;
            TotalQuantity = 0;
            PersistActiveSessionSnapshot();
            RebuildReceiptTabs();
            PushCustomerDisplay();
            return;
        }

        SyncOrderDiscountFromCart();
        var totals = CartTotalsCalculator.Calculate(_cart.Root);
        Subtotal = totals.Subtotal;
        Discount = totals.LineDiscounts + totals.OrderDiscount;
        Total = totals.TotalDue;
        LineCount = totals.LineCount;
        TotalQuantity = _cart.TotalQuantity;
        PersistActiveSessionSnapshot();
        RebuildReceiptTabs();
        PushCustomerDisplay();
    }

    private void SyncOrderDiscountFromCart()
    {
        _orderDiscountPercent = "";
        _orderDiscountSum = "";
        if (!_cart.HasCart || _cart.Root.ValueKind != JsonValueKind.Object)
            return;

        if (_cart.Root.TryGetProperty("order_discount_percent", out var percent)
            && JsonNumericReader.TryToDouble(percent, out var percentValue)
            && percentValue > 0)
        {
            _orderDiscountPercent = percentValue.ToString("0.##", CultureInfo.InvariantCulture);
            return;
        }

        if (_cart.Root.TryGetProperty("order_discount_total", out var total)
            && JsonNumericReader.TryToDouble(total, out var totalValue)
            && totalValue > 0)
        {
            _orderDiscountSum = totalValue.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    private void PushCustomerDisplay()
    {
        _customerDisplay.UpdateCart(new CustomerDisplayCartSnapshot
        {
            Lines = Lines.Select(line => new CustomerDisplayLine
            {
                Title = line.Title,
                Barcode = line.Barcode,
                Quantity = line.Quantity,
                Unit = line.Unit,
                LineTotal = line.LineTotal,
            }).ToList(),
            Subtotal = Subtotal,
            Discount = Discount,
            Total = Total,
            CashReceived = _lastCashReceivedForDisplay,
            ChangeDue = _lastChangeDueForDisplay,
        });

        // Отдельный дисплей цены на COM-порту (2026-09-04, не второй монитор) — тот же хук,
        // что обновляет второй монитор покупателя, чтобы оба всегда показывали одну сумму.
        PoleDisplayService.Instance.ShowTotal(Total, Lines.Count);
    }

    private static double ParseQuantity(string? raw, bool mustWeigh)
    {
        if (!double.TryParse(raw?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
            || qty <= 0)
            qty = 1;

        return mustWeigh ? Math.Round(qty, 3) : Math.Round(qty, 0, MidpointRounding.AwayFromZero);
    }

    private void RaiseCartCommands()
    {
        NotifyLineState();
        (PayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeferCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenDeferredCartsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyOrderDiscountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReturnPreviousReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteReceiptCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ClearCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (IncreaseQuantityCommand as RelayCommand<CartLineItemVm>)?.RaiseCanExecuteChanged();
        (DecreaseQuantityCommand as RelayCommand<CartLineItemVm>)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteActiveReceipt));
        OnPropertyChanged(nameof(IsAtReceiptLimit));
        OnPropertyChanged(nameof(HasHeldReceipts));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenDeferredCartsAsync()
    {
        if (_openDeferredCarts is null)
            return;

        await RunOnUiThreadAsync(() => IsMoreActionsVisible = false).ConfigureAwait(false);
        await _openDeferredCarts().ConfigureAwait(true);
        await RunOnUiThreadAsync(() =>
        {
            RefreshFromCart();
            NotifyHeldReceiptsChanged();
            RaiseCartCommands();
        }).ConfigureAwait(false);
    }

    private async Task ApplyOrderDiscountAsync()
    {
        if (_applyOrderDiscount is null)
            return;
        if (!DemandPermission(PosPermissions.ApplyDiscount))
            return;

        await RunOnUiThreadAsync(() => IsMoreActionsVisible = false).ConfigureAwait(false);
        await _applyOrderDiscount().ConfigureAwait(true);
    }

    private async Task AddCustomItemAsync()
    {
        if (_addCustomItem is null)
            return;

        await RunOnUiThreadAsync(() => IsMoreActionsVisible = false).ConfigureAwait(false);
        await _addCustomItem().ConfigureAwait(true);
    }

    /// <summary>Строка «Доп. услуга» в чеке (2026-09-07, как на сайте): без товара, по названию и
    /// сумме. isExpense («Расход») кладёт строку с отрицательной ценой — она вычитается из чека;
    /// «Доход» (доставка, услуга) прибавляется. На сервер строка уходит при оплате
    /// (StagingCartService → PosAddCustomItemAsync).</summary>
    public bool AddCustomItem(string name, double price, double quantity, bool isExpense)
    {
        var title = (name ?? "").Trim();
        if (title.Length == 0 || !double.IsFinite(price) || price <= 0 || !double.IsFinite(quantity) || quantity <= 0)
            return false;

        try
        {
            EnsureCartInitialized();
            _cart.AddCustomItem(title, isExpense ? -price : price, quantity);
            SyncLinesFromCart();
            UpdateCartTotals();
            CartMessage = isExpense
                ? Tr.T("Расход добавлен в чек.", "Чыгаша чекке кошулду.", "Expense added to the receipt.", "Gider fişe eklendi.", "Xarajat chekka qo'shildi.")
                : Tr.T("Услуга добавлена в чек.", "Кызмат чекке кошулду.", "Service added to the receipt.", "Hizmet fişe eklendi.", "Xizmat chekka qo'shildi.");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART custom item failed: {ex}", "CART");
            _prompts.ShowError(Tr.T("Не удалось добавить услугу в чек.", "Кызматты чекке кошуу мүмкүн болгон жок.", "Could not add the service to the receipt.", "Hizmet fişe eklenemedi.", "Xizmatni chekka qo'shib bo'lmadi."));
            return false;
        }
    }

    private Task RunOnUiThreadAsync(Action action) =>
        _dispatcher.InvokeAsync(action);

    private void NotifyLineState()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasLines));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Локальная сессия открытого чека (снимок корзины + имя вкладки).</summary>
    private sealed class OpenReceiptSession
    {
        public string Id { get; init; } = "";
        public string BaseName { get; set; } = Tr.T("Основной чек", "Негизги чек", "Main receipt", "Ana fiş", "Asosiy chek");
        public string CartJson { get; set; } = "{}";
    }
}

/// <summary>
/// Вкладка открытого чека в панели корзины.
/// </summary>
public sealed class ReceiptTabVm : ViewModelBase
{
    private string _title;
    private bool _isActive;

    public ReceiptTabVm(string id, string title, bool isActive = false, ICommand? selectCommand = null)
    {
        Id = id;
        _title = title;
        _isActive = isActive;
        SelectCommand = selectCommand;
    }

    public string Id { get; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value ?? "");
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public ICommand? SelectCommand { get; }
}
