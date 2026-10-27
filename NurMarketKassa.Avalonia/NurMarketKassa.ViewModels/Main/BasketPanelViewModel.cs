using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Core.Domain;
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
public sealed partial class BasketPanelViewModel : ViewModelBase
{
    public const int MaxOpenReceipts = 10;

    /// <summary>2026-09-17: скан промахнулся мимо локального кэша — прежде чем спросить сервер
    /// напрямую, ждём не дольше этого времени (короткий сетевой запрос не должен подвешивать
    /// сканирование на кассе при плохой связи).
    /// 2026-10-04: 4 → 2 с (требование владельца: действие кассира не ждёт сеть дольше 1–2 с;
    /// живой сервер находит товар по штрихкоду за доли секунды).</summary>
    private static readonly TimeSpan ServerBarcodeLookupTimeout = TimeSpan.FromSeconds(2);

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
    private double _orderDiscount; // 2026-10-22 (аудит 1.17.59, П-В): скидка на чек отдельной строкой над «Итого»
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
    // 2026-10-26, владелец (второй монитор): сколько оплачено (безнал, смешанная, предоплата долга), экран показывает итог оплаты,
    // и номер сброса — чтобы поздний сброс прошлой оплаты не стёр показ следующей.
    private double? _lastPaidAmountForDisplay;
    private bool _customerDisplayShowsPayment;
    private int _customerDisplayResetVersion;
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

        AddByBarcodeCommand = new RelayCommand(SubmitBarcodeInput, CanAddByBarcode);
        PayCommand = new AsyncRelayCommand(PayAsync, () => HasItems && !IsBusy);
        DeferCartCommand = new AsyncRelayCommand(DeferCartAsync, () => HasItems && !IsBusy);
        HoldReceiptCommand = DeferCartCommand;
        DeleteReceiptCommand = new AsyncRelayCommand(DeleteActiveReceiptAsync, CanDeleteActiveReceiptCore);
        ClearCartCommand = new AsyncRelayCommand(ClearCartAsync, () => HasItems && !IsBusy);
        NewReceiptCommand = new RelayCommand(CreateNewReceipt);
        SelectReceiptTabCommand = new RelayCommand<ReceiptTabVm>(SelectReceiptTab, tab => tab != null && !tab.IsActive);
        RemoveLineCommand = new AsyncRelayCommand<CartLineItemVm>(RemoveLineAsync, line => line != null && !string.IsNullOrEmpty(line.ItemId));
        IncreaseQuantityCommand = new RelayCommand<CartLineItemVm>(IncreaseQuantity, CanChangeLineQuantity);
        DecreaseQuantityCommand = new RelayCommand<CartLineItemVm>(DecreaseQuantity, CanDecreaseLineQuantity);
        SetQuantityCommand = new RelayCommand<CartLineItemVm>(SetLineQuantity, CanChangeLineQuantity);
        // 2026-10-06 (О-03): «Размер» на строке — окно выбора размера показывает главное окно (ChangeVariantLine).
        ChangeVariantLineCommand = new AsyncRelayCommand<CartLineItemVm>(
            line => line != null && ChangeVariantLine != null ? ChangeVariantLine(line) : Task.CompletedTask,
            line => line != null && !string.IsNullOrWhiteSpace(line.VariantId) && !IsBusy);
        WeighLineCommand = new RelayCommand<CartLineItemVm>(WeighLine, line =>
            line is { IsWeight: true } && !string.IsNullOrEmpty(line.ItemId) && _reweighCartLine != null);
        LineDiscountCommand = new AsyncRelayCommand<CartLineItemVm>(ApplyLineDiscountAsync, line =>
            line != null && HasItems && !IsBusy && _applyLineDiscount != null);
        // 2026-10-03, клиент: «опт не только на весь чек, но и на сам товар — переключатель».
        WholesaleLineCommand = new RelayCommand<CartLineItemVm>(line => ToggleWholesale(line), line => line is { CanWholesale: true } && !IsBusy);
        WholesaleAllCommand = new RelayCommand(ToggleWholesaleAll, () => HasItems && !IsBusy);
        IncreaseManualQuantityCommand = new RelayCommand(IncreaseManualQuantity);
        DecreaseManualQuantityCommand = new RelayCommand(DecreaseManualQuantity);
        ToggleMoreActionsCommand = new RelayCommand(() => IsMoreActionsVisible = !IsMoreActionsVisible);
        OpenDeferredCartsCommand = new AsyncRelayCommand(OpenDeferredCartsAsync, () => !IsBusy);
        OpenPayDebtCommand = new RelayCommand(() => _openPayDebt?.Invoke());
        ApplyOrderDiscountCommand = new AsyncRelayCommand(ApplyOrderDiscountAsync, () => HasItems && !IsBusy);
        AddCustomItemCommand = new AsyncRelayCommand(AddCustomItemAsync);
        AddQuickProductCommand = new RelayCommand<CatalogProductTileVm>(
            product => { if (product != null) AddProductFromCatalog(product); },
            product => product != null && !IsBusy);
        RefreshQuickProducts();
        // Каталог мог обновиться (синхронизация, отметка звёздочкой) — перечитываем.
        CatalogCacheService.CatalogChanged += () => _dispatcher.InvokeAsync(RefreshQuickProducts);
        // Быстрые товары можно отключить в Настройки → Экран (2026-09-28).
        UserPreferences.ShowQuickProductsChanged += () => _dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(HasQuickProducts)));

        // Смена языка интерфейса: кнопка «Оплатить» и подписи строк чека собираются в коде (2026-09-07).
        Tr.LanguageChanged += () => _dispatcher.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(PayButtonText));
            OnPropertyChanged(nameof(SubtotalDisplay));
            OnPropertyChanged(nameof(DiscountDisplay));
            OnPropertyChanged(nameof(TotalDisplay));
            // Названия вкладок чеков («Основной чек», «Чек 2») тоже собираются в коде.
            RenameReceiptSessions();
            SyncLinesFromCart();
        });
        ReturnPreviousReceiptCommand = new AsyncRelayCommand(RestoreLastHeldReceiptAsync, () => !IsBusy && HasHeldReceipts);
        RestoreLastHeldReceiptCommand = ReturnPreviousReceiptCommand;

        EnsureCartInitialized();
        EnsurePrimarySession();
        Lines.CollectionChanged += (_, _) => NotifyLineState();
        // 2026-10-01, «Умная допродажа»: чек изменился — подбираем подсказку «С этим часто берут».
        Lines.CollectionChanged += (_, _) => ScheduleUpsell();
        AcceptUpsellCommand = new AsyncRelayCommand(AcceptUpsellAsync, () => _upsell != null && !IsBusy);
        SkipUpsellCommand = new RelayCommand(SkipUpsell, () => _upsell != null);
        UserPreferences.UpsellEnabledChanged += () => _dispatcher.InvokeAsync(ScheduleUpsell);
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

    /// <summary>Вызывается перед оплатой (на UI-потоке). Если смены нет — окно кассы сверяется с
    /// сервером и при необходимости открывает смену; false — платить нельзя.</summary>
    public Func<Task<bool>>? EnsureShiftBeforePayment { get; set; }

    /// <summary>Оплата прошла (на UI-потоке). Окно кассы по нему подтягивает остаток смены.</summary>
    public event EventHandler? CheckoutSucceeded;

    /// <summary>2026-10-18, ревью этапа 6: последняя проведённая продажа (для плашки «Продажа прошла» и «Печать
    /// копии»). Заполняется на UI-потоке прямо перед <see cref="CheckoutSucceeded"/>.</summary>
    public LastSaleInfo? LastSale { get; private set; }

    /// <summary>2026-10-04: оплата проведена, корзина уже новая — состояние кассы нужно записать на диск
    /// немедленно (вызывается на UI-потоке, раньше окна «Платёж принят»).</summary>
    public event EventHandler? PaymentCommitted;

    public ObservableCollection<CartLineItemVm> Lines { get; } = new();
    public ObservableCollection<ReceiptTabVm> ReceiptTabs { get; } = new();

    /// <summary>Избранные товары — показываются в правой панели, пока чек пуст.
    ///
    /// До этого половина экрана при пустой корзине была занята рисунком тележки и подписью
    /// «Выберите товар слева». Кассир и так знает, что делать; а вот товары, которые он пробивает
    /// каждый день, стоило положить под руку — их отмечают звёздочкой в каталоге.</summary>
    public ObservableCollection<CatalogProductTileVm> QuickProducts { get; } = new();

    public bool HasQuickProducts => UserPreferences.Instance.ShowQuickProducts && QuickProducts.Count > 0;

    public ICommand AddQuickProductCommand { get; private set; } = null!;

    /// <summary>Перечитывает избранное из каталога. Вызывается при старте и когда каталог
    /// обновился — иначе отмеченный только что товар появился бы здесь лишь после перезапуска.</summary>
    public void RefreshQuickProducts()
    {
        try
        {
            var favourites = LocalProductRepository.Instance.LoadFavoriteTiles();

            QuickProducts.Clear();
            foreach (var product in favourites)
                QuickProducts.Add(product);

            OnPropertyChanged(nameof(HasQuickProducts));

        }
        catch (Exception ex)
        {
            PosLogger.Log($"Быстрые товары не обновились: {ex.Message}", "WARNING");
        }
    }

    public string BarcodeInput
    {
        get => _barcodeInput;
        set
        {
            if (!SetProperty(ref _barcodeInput, value ?? ""))
                return;
            (AddByBarcodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    /// <summary>2026-10-22 (аудит 1.17.59, П-В, Б9): скидка на весь чек — отдельной строкой «Скидка на чек −N» над «Итого»,
    /// чтобы кассир видел, откуда меньшая сумма (зелёное «Скидка 50% применена» гаснет). Только показ.</summary>
    public double OrderDiscountAmount
    {
        get => _orderDiscount;
        private set
        {
            if (!SetProperty(ref _orderDiscount, value))
                return;
            OnPropertyChanged(nameof(HasOrderDiscount));
            OnPropertyChanged(nameof(OrderDiscountText));
            OnPropertyChanged(nameof(OrderDiscountValue));
        }
    }

    public bool HasOrderDiscount => _orderDiscount > 0.004;

    public string OrderDiscountText =>
        Tr.T("Скидка на чек", "Чекке арзандатуу", "Receipt discount", "Fiş indirimi", "Chek chegirmasi");

    public string OrderDiscountValue => "−" + MoneyText.Som(_orderDiscount);

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

    // 2026-10-19, К1: суммы на экране — одним видом «1 234,50» (MoneyText), как у владельца; только показ.
    public string SubtotalDisplay => MoneyText.Som(Subtotal);
    public string DiscountDisplay => MoneyText.Som(Discount);
    public string TotalDisplay => MoneyText.Som(Total);
    public string TotalAmount => MoneyText.Amount(Total);
    public string PayButtonText =>
        Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash");

    public string CartMessage
    {
        get => _cartMessage;
        set
        {
            if (!SetProperty(ref _cartMessage, value ?? ""))
            {
                // 2026-10-21: то же сообщение ещё раз (второй «+ Новый чек» на пустом чеке) — снова показываем полный срок.
                ScheduleCartMessageClear(_cartMessage);
                return;
            }
            OnPropertyChanged(nameof(HasCartMessage));
            ScheduleCartMessageClear(_cartMessage);
        }
    }

    public bool HasCartMessage => !string.IsNullOrWhiteSpace(CartMessage);

    /// <summary>2026-10-21 (пакет 6, п. 15): строка под чеком гаснет сама — «Операция проведена, но чек не напечатался…»
    /// висела в панели часами. Короткие подсказки — 12 с, длинные (с объяснением, что делать) — 45 с.</summary>
    private CancellationTokenSource? _cartMessageCts;

    private void ScheduleCartMessageClear(string message)
    {
        _cartMessageCts?.Cancel();
        _cartMessageCts = null;
        if (string.IsNullOrWhiteSpace(message))
            return;
        var cts = _cartMessageCts = new CancellationTokenSource();
        var delay = TimeSpan.FromSeconds(message.Length > 70 ? 45 : 12);
        _ = Task.Delay(delay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;
            _dispatcher.Post(() =>
            {
                if (!cts.IsCancellationRequested && ReferenceEquals(_cartMessage, message))
                    CartMessage = "";
            });
        }, TaskScheduler.Default);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            (PayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeferCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (OpenDeferredCartsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyOrderDiscountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ReturnPreviousReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (DeleteReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            // 2026-10-20, живая проверка: в «1С» «Очистить» оставалась серой при товарах в чеке — после скана
            // (IsBusy → false) её доступность не пересчитывалась.
            (ClearCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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

    /// <summary>Способ оплаты, который окно оплаты выберет сразу, — для кнопок «Наличные» и
    /// «Безнал» в раскладках кассы (2026-09-28): "cash" или "transfer". null — как всегда
    /// (наличные). Сбрасывается в начале каждой оплаты, поэтому обычное «Оплатить» его не видит.</summary>
    public string? PreferredPaymentMethod { get; set; }

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

    /// <summary>2026-10-06 (О-03): «Размер» на строке чека.</summary>
    public ICommand ChangeVariantLineCommand { get; }

    /// <summary>Окно выбора другого размера/цвета для строки — ставит главное окно кассы.</summary>
    public Func<CartLineItemVm, Task>? ChangeVariantLine { get; set; }

    /// <summary>2026-10-17, этап 4 («Контроль удалений»): окно «Убрать из чека?» с причиной — ставит главное окно
    /// кассы. null-ответ — кассир нажал «Оставить», строка остаётся. Спрашивается только при включённой
    /// настройке и сумме строки от порога (UserPreferences.AskCartRemovalReason / CartRemovalReasonThreshold).</summary>
    public Func<CartRemovalReasonRequest, Task<CartRemovalReasonAnswer?>>? AskRemovalReason { get; set; }

    /// <summary>2026-10-06 (О-03): строка чека меняется на другой размер/цвет того же товара с тем же количеством.
    /// Скидка строки не переносится — цена у другого варианта может быть своя.</summary>
    public void ReplaceLineVariant(CartLineItemVm line, CatalogProductTileVm product, double unitPrice, string variantId, string label, string? size, string? color)
    {
        if (line is null || product is null || string.IsNullOrWhiteSpace(line.ItemId) || string.IsNullOrWhiteSpace(variantId))
            return;
        try
        {
            EnsureCartInitialized();
            var qty = line.Quantity > 0 ? line.Quantity : 1;
            _cart.RemoveItem(line.ItemId);
            _cart.AddVariantItem(product, qty, unitPrice, variantId, label, size, color);
            SyncLinesAndTotals();
            CartMessage = Tr.T("Размер заменён.", "Өлчөм алмаштырылды.", "Size changed.", "Beden değiştirildi.", "O'lcham almashtirildi.");
            PosLogger.Log($"CART: строка {line.ItemId} ({line.Title}) заменена на вариант {variantId} ({label}).", "CART");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART change variant failed: {ex}", "CART");
            _prompts.ShowError(Tr.T("Не удалось сменить размер.", "Өлчөмдү алмаштыруу мүмкүн болгон жок.", "Could not change the size.", "Beden değiştirilemedi.", "O'lchamni almashtirib bo'lmadi."));
        }
    }
    public ICommand LineDiscountCommand { get; }
    public ICommand WholesaleLineCommand { get; }
    public ICommand WholesaleAllCommand { get; }
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
                    DeferredAt = session.DeferredAt,
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
                DeferredAt = savedSession.DeferredAt,
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
        SyncLinesAndTotals();
        CartMessage = "";
        RaiseCartCommands();
    }
    public void RefreshFromCart()
    {
        SyncLinesAndTotals();
    }

    /// <param name="refresh">2026-10-20 (живой аудит кассы, К5): false — скидка записана в чек, но экран кассы и экран
    /// покупателя не обновлены (окно кассы сначала спросит «Продажа в убыток… применить?», потом RefreshFromCart).</param>
    public bool ApplyOrderDiscount(string? mode, string? value, bool clear = false, bool refresh = true)
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
                    $"İndirim en fazla %{limitPercent:0.##} olabilir — personel için belirlenen sınır budur.",
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
                        $"Скидка не может превышать сумму товаров: {MoneyText.Amount(maximum)} сом.",
                        $"Арзандатуу товарлардын суммасынан ашпашы керек: {MoneyText.Amount(maximum)} сом.", $"The discount can't exceed the total of the items: {MoneyText.Amount(maximum)} som.", $"İndirim, ürünlerin toplam tutarını aşamaz: {MoneyText.Amount(maximum)} som.", $"Chegirma mahsulotlar summasidan oshmasligi kerak: {MoneyText.Amount(maximum)} so'm."));
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
                            $"Скидка не может превышать {limitForSum:0.##}% — это {MoneyText.Amount(allowedSum)} сом для текущего чека.",
                            $"Арзандатуу {limitForSum:0.##}%дан ашпашы керек — учурдагы чек үчүн бул {MoneyText.Amount(allowedSum)} сом.",
                            $"The discount can't exceed {limitForSum:0.##}% — that is {MoneyText.Amount(allowedSum)} som for this receipt.",
                            $"İndirim en fazla %{limitForSum:0.##} olabilir — bu fiş için {MoneyText.Amount(allowedSum)} som.",
                            $"Chegirma {limitForSum:0.##}%dan oshmasligi kerak — bu chek uchun {MoneyText.Amount(allowedSum)} so'm."));
                        return false;
                    }
                }
            }
        }

        var percent = !clear && string.Equals(mode, "percent", StringComparison.OrdinalIgnoreCase) ? value : null;
        var total = !clear && !string.Equals(mode, "percent", StringComparison.OrdinalIgnoreCase) ? value : null;
        ReceiptSnapshotCartEditor.PatchOrderDiscount(_cart, percent, total);
        if (refresh)
            RefreshFromCart();
        // 2026-10-04: убыток проверяет окно кассы (LossWarningText) — подтверждение «Я знаю что делаю»
        // или откат к прежней скидке (RestoreOrderDiscount).
        return true;
    }

    /// <summary>Скидка на чек сейчас: (процент, сумма) строками — для отката, если кассир не подтвердил убыток.</summary>
    public (string? Percent, string? Total) ReadOrderDiscount()
    {
        if (!_cart.HasCart || _cart.Root.ValueKind != JsonValueKind.Object)
            return (null, null);
        static string? Num(JsonElement root, string name) =>
            root.TryGetProperty(name, out var v) && JsonNumericReader.TryToDouble(v, out var d) && d > 0
                ? d.ToString(CultureInfo.InvariantCulture)
                : null;
        return (Num(_cart.Root, "order_discount_percent"), Num(_cart.Root, "order_discount_total"));
    }

    /// <param name="refresh">2026-10-20 (К5): false — откат скидки, которую экран ещё не показывал (обновлять нечего).</param>
    public void RestoreOrderDiscount(string? percent, string? total, bool refresh = true)
    {
        ReceiptSnapshotCartEditor.PatchOrderDiscount(_cart, percent, total);
        if (refresh)
            RefreshFromCart();
    }

    // ------------------------------------------------------------------ продажа в убыток

    /// <summary>2026-10-03, владелец: «если в убыток даёт скидку — предупреждение на экране; рядом с товаром
    /// показывать, сколько убытка, и фиксировать в админке». Убыток строки = закупка × количество − сумма
    /// строки после скидки на позицию и её доли скидки на весь чек. Только если на чеке есть скидка и
    /// у товара указана закупочная цена: без скидки «цена ниже закупки» — это вопрос цен, не кассира.</summary>
    public void UpdateLossMarks()
    {
        try
        {
            double orderDiscount = 0;
            if (_cart.HasCart && _cart.Root.ValueKind == JsonValueKind.Object)
                orderDiscount = CartTotalsCalculator.Calculate(_cart.Root).OrderDiscount;
            var gross = Lines.Sum(l => Math.Max(0, l.LineTotal));
            var share = gross > 0 ? Math.Clamp(orderDiscount / gross, 0, 1) : 0;
            foreach (var line in Lines)
            {
                double loss = 0;
                var cost = UnitCostOf(line);
                line.UnitCost = cost;
                line.CanWholesale = line.IsWholesale || WholesalePriceOf(line) > 0;
                if (cost > 0 && (line.HasDiscount || share > 0))
                {
                    var net = Math.Max(0, line.LineTotal) * (1 - share);
                    loss = Math.Round(Math.Max(0, cost * line.Quantity - net), 2);
                }
                line.LossAmount = loss;
            }
            OnPropertyChanged(nameof(LossTotal));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Убыток по строкам не посчитан: {ex.Message}", "WARNING");
        }
    }

    public double LossTotal => Lines.Sum(l => l.LossAmount);

    // ------------------------------------------------------------------ опт

    /// <summary>Оптовая цена товара строки (за единицу строки); 0 — у товара её нет.</summary>
    private double WholesalePriceOf(CartLineItemVm line)
    {
        if (string.IsNullOrWhiteSpace(line.ProductId) || _catalogLookup?.Invoke(line.ProductId) is not { } tile || tile.WholesalePrice <= 0)
            return 0;
        if (!string.IsNullOrWhiteSpace(line.SalePackageId) && tile.PieceOption is { QuantityInPackage: > 0 } piece
            && string.Equals(piece.Id, line.SalePackageId, StringComparison.OrdinalIgnoreCase))
            return Math.Round(tile.WholesalePrice / piece.QuantityInPackage, 2);
        return tile.WholesalePrice;
    }

    /// <summary>Строка ↔ оптовая цена. Только у локального (ещё не отправленного) чека — серверную корзину
    /// касса так не правит.</summary>
    private void ToggleWholesale(CartLineItemVm? line, bool refresh = true)
    {
        if (line is null || string.IsNullOrWhiteSpace(line.ItemId) || !(_cart.IsStaging || _cart.IsLocalOffline))
            return;
        try
        {
            if (ReceiptSnapshotCartEditor.SetLineWholesale(_cart, line.ItemId, !line.IsWholesale, WholesalePriceOf(line)) && refresh)
            {
                RefreshFromCart();
                CartMessage = !line.IsWholesale
                    ? Tr.T($"«{line.Title}» — по оптовой цене.", $"«{line.Title}» — дүң баада.", $"“{line.Title}” — at the wholesale price.", $"«{line.Title}» — toptan fiyatla.", $"«{line.Title}» — ulgurji narxda.")
                    : Tr.T($"«{line.Title}» — снова по розничной цене.", $"«{line.Title}» — кайра чекене баада.", $"“{line.Title}” — back to the retail price.", $"«{line.Title}» — yeniden perakende fiyatla.", $"«{line.Title}» — yana chakana narxda.");
                PosLogger.Log($"Опт: строка «{line.Title}» → {(line.IsWholesale ? "розница" : "опт")}.", "CART");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Опт: строку не переключить ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>«Опт на весь чек»: если хоть одна строка с оптовой ценой ещё в рознице — все такие на опт,
    /// иначе все обратно в розницу.</summary>
    private void ToggleWholesaleAll()
    {
        var candidates = Lines.Where(l => l.CanWholesale).ToList();
        if (candidates.Count == 0)
        {
            _prompts.ShowWarning(Tr.T("У товаров этого чека не указана оптовая цена (карточка товара → «Оптовая цена»).",
                "Бул чектеги товарлардын дүң баасы көрсөтүлгөн эмес (товардын карточкасы → «Дүң баа»).",
                "The products in this receipt have no wholesale price (product card → “Wholesale price”).",
                "Bu fişteki ürünlerin toptan fiyatı yok (ürün kartı → «Toptan fiyat»).",
                "Bu chekdagi mahsulotlarning ulgurji narxi ko'rsatilmagan (mahsulot kartasi → «Ulgurji narx»)."));
            return;
        }
        var toWholesale = candidates.Any(l => !l.IsWholesale);
        foreach (var line in candidates.Where(l => l.IsWholesale != toWholesale).ToList())
            ToggleWholesale(line, refresh: false);
        RefreshFromCart();
        CartMessage = toWholesale
            ? Tr.T("Чек — по оптовым ценам.", "Чек — дүң бааларда.", "Receipt at wholesale prices.", "Fiş toptan fiyatlarla.", "Chek ulgurji narxlarda.")
            : Tr.T("Чек — снова по розничным ценам.", "Чек — кайра чекене бааларда.", "Receipt back at retail prices.", "Fiş yeniden perakende fiyatlarla.", "Chek yana chakana narxlarda.");
    }

    // 2026-10-04, клиент: «поставить тумблер сверху (Оптовый/Розничный) для продажи оптовой ценой или по
    // розничной; место, чтобы поставить галочку оптовой продажи определённого товара». Тумблер действует на
    // текущий чек: включили — все строки с оптовой ценой и всё, что отсканируют дальше, по опту; галочка «Опт»
    // у строки по-прежнему меняет один товар. Новый (пустой) или другой чек — снова «Розничный», чтобы
    // следующего покупателя случайно не пробить по оптовым ценам.
    private bool _isWholesaleMode;
    private bool _applyingWholesaleMode;
    private string _wholesaleModeSessionId = "";
    private readonly HashSet<string> _wholesaleModeSeenLines = new(StringComparer.Ordinal);

    public bool IsWholesaleMode
    {
        get => _isWholesaleMode;
        set
        {
            if (_isWholesaleMode == value)
                return;
            _isWholesaleMode = value;
            OnPropertyChanged();
            if (_applyingWholesaleMode)
                return;
            var candidates = Lines.Where(l => l.CanWholesale && l.IsWholesale != value).ToList();
            _applyingWholesaleMode = true;
            try
            {
                foreach (var line in candidates)
                    ToggleWholesale(line, refresh: false);
                if (candidates.Count > 0)
                    RefreshFromCart();
            }
            finally
            {
                _applyingWholesaleMode = false;
            }
            RememberWholesaleModeLines();
            CartMessage = value
                ? Tr.T("Оптовая продажа: товары с оптовой ценой — по опту.", "Дүң сатуу: дүң баасы бар товарлар — дүң баада.", "Wholesale sale: items with a wholesale price at wholesale.", "Toptan satış: toptan fiyatı olan ürünler toptan fiyatla.", "Ulgurji savdo: ulgurji narxi bor mahsulotlar ulgurji narxda.")
                : Tr.T("Розничная продажа.", "Чекене сатуу.", "Retail sale.", "Perakende satış.", "Chakana savdo.");
            PosLogger.Log($"Опт: тумблер чека → {(value ? "оптовый" : "розничный")}, строк переключено {candidates.Count}.", "CART");
        }
    }

    private void RememberWholesaleModeLines()
    {
        _wholesaleModeSessionId = _activeSessionId;
        _wholesaleModeSeenLines.Clear();
        foreach (var line in Lines)
            if (!string.IsNullOrEmpty(line.ItemId))
                _wholesaleModeSeenLines.Add(line.ItemId);
    }

    /// <summary>После каждого обновления строк: другой или пустой чек — тумблер в «Розничный»; в оптовом
    /// режиме новые строки с оптовой ценой сразу по опту (уже бывшие строки не трогаем — их могли
    /// снять галочкой вручную).</summary>
    private void ApplyWholesaleModeToNewLines()
    {
        if (_applyingWholesaleMode)
            return;
        // Пустой чек сбрасывает режим, только если в нём уже были строки (чек оплачен или очищен):
        // включить «Оптовый» до первого скана можно.
        if (_isWholesaleMode
            && ((Lines.Count == 0 && _wholesaleModeSeenLines.Count > 0) || _wholesaleModeSessionId != _activeSessionId))
        {
            _isWholesaleMode = false;
            OnPropertyChanged(nameof(IsWholesaleMode));
        }
        if (!_isWholesaleMode)
        {
            RememberWholesaleModeLines();
            return;
        }

        var fresh = Lines.Where(l => !string.IsNullOrEmpty(l.ItemId) && !_wholesaleModeSeenLines.Contains(l.ItemId)).ToList();
        foreach (var line in fresh)
            _wholesaleModeSeenLines.Add(line.ItemId);
        var toWholesale = fresh.Where(l => l.CanWholesale && !l.IsWholesale).ToList();
        if (toWholesale.Count == 0)
            return;
        _applyingWholesaleMode = true;
        try
        {
            foreach (var line in toWholesale)
                ToggleWholesale(line, refresh: false);
            RefreshFromCart();
        }
        finally
        {
            _applyingWholesaleMode = false;
        }
        RememberWholesaleModeLines();
    }

    private double UnitCostOf(CartLineItemVm line) => UnitCostOf(line.ProductId, line.SalePackageId);

    private double UnitCostOf(string? productId, string? salePackageId)
    {
        if (string.IsNullOrWhiteSpace(productId) || _catalogLookup?.Invoke(productId) is not { } tile || tile.PurchasePrice <= 0)
            return 0;
        if (!string.IsNullOrWhiteSpace(salePackageId) && tile.PieceOption is { QuantityInPackage: > 0 } piece
            && string.Equals(piece.Id, salePackageId, StringComparison.OrdinalIgnoreCase))
            return tile.PurchasePrice / piece.QuantityInPackage;
        return tile.PurchasePrice;
    }

    /// <summary>2026-10-20 (живой аудит кассы, К5): тот же расчёт убытка, что LossWarningText, но прямо по чеку (корзине), а не по
    /// строкам экрана. Окно кассы записывает скидку в чек БЕЗ обновления экрана и экрана покупателя, спрашивает «Продажа в
    /// убыток… применить?» по этому тексту и только после «Да» показывает скидку (раньше покупатель видел её до ответа).
    /// null — убытка нет.</summary>
    public string? LossWarningTextFromCart()
    {
        try
        {
            if (!_cart.HasCart || _cart.Root.ValueKind != JsonValueKind.Object)
                return null;
            var root = _cart.Root;
            var items = CartDisplayHelper.EnumerateItems(root).ToList();
            static double TotalOf(JsonElement it) =>
                double.TryParse(CartDisplayHelper.LineTotal(it), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var orderDiscount = CartTotalsCalculator.Calculate(root).OrderDiscount;
            var gross = items.Sum(it => Math.Max(0, TotalOf(it)));
            var share = gross > 0 ? Math.Clamp(orderDiscount / gross, 0, 1) : 0;
            var loss = new List<(string Title, double Loss)>();
            foreach (var it in items)
            {
                var cost = UnitCostOf(CartDisplayHelper.TryProductId(it), CartDisplayHelper.SalePackageId(it));
                var qty = CartDisplayHelper.LineQuantity(it);
                var lineTotal = TotalOf(it);
                var hasDiscount = CartDisplayHelper.EffectiveLineDiscount(it) > 1e-6
                                  || lineTotal + 1e-6 < CartDisplayHelper.UnitPrice(it) * qty;
                if (cost <= 0 || !(hasDiscount || share > 0))
                    continue;
                var net = Math.Max(0, lineTotal) * (1 - share);
                var value = Math.Round(Math.Max(0, cost * qty - net), 2);
                if (value > 0.005)
                    loss.Add((CartDisplayHelper.ItemName(it), value));
            }
            if (loss.Count == 0)
                return null;
            var total = loss.Sum(l => l.Loss);
            var lossWord = Tr.T("убыток", "зыян", "loss", "zarar", "zarar");
            var list = string.Join("\n", loss.Take(5).Select(l => $"• {l.Title}: {lossWord} {MoneyText.Som(l.Loss)}"));
            PosLogger.Log($"Скидка в убыток: {total:0.00} сом по {loss.Count} поз. (до применения).", "CART");
            return Tr.T(
                $"Со скидкой товар продаётся дешевле закупки — убыток {MoneyText.Amount(total)} сом.\n{list}",
                $"Арзандатуу менен товар сатып алуу баасынан арзан сатылат — зыян {MoneyText.Amount(total)} сом.\n{list}",
                $"With this discount the item sells below cost — loss {MoneyText.Amount(total)} som.\n{list}",
                $"Bu indirimle ürün alış fiyatının altında satılıyor — zarar {MoneyText.Amount(total)} som.\n{list}",
                $"Chegirma bilan mahsulot xarid narxidan arzon sotilmoqda — zarar {MoneyText.Amount(total)} so'm.\n{list}");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Убыток по чеку до применения скидки не посчитан: {ex.Message}", "WARNING");
            return null;
        }
    }

    /// <summary>Скидка увела чек в убыток — предупреждение кассиру (после скидки на чек или на позицию).</summary>
    public void WarnIfSellingAtLoss()
    {
        if (LossWarningText() is { } text)
            _prompts.ShowWarning(text);
    }

    /// <summary>2026-10-04, клиент: «если скидку случайно выдать в убыток — предупреждающий экран, и только
    /// после подтверждения (кнопка «Я знаю что делаю») добавить скидку». Текст предупреждения или null,
    /// если после скидки убытка нет.</summary>
    public string? LossWarningText()
    {
        UpdateLossMarks();
        var loss = Lines.Where(l => l.HasLoss).ToList();
        if (loss.Count == 0)
            return null;
        var total = loss.Sum(l => l.LossAmount);
        var list = string.Join("\n", loss.Take(5).Select(l => $"• {l.Title}: {l.LossDisplayText}"));
        PosLogger.Log($"Скидка в убыток: {total:0.00} сом по {loss.Count} поз.", "CART");
        return Tr.T(
            $"Со скидкой товар продаётся дешевле закупки — убыток {MoneyText.Amount(total)} сом.\n{list}",
            $"Арзандатуу менен товар сатып алуу баасынан арзан сатылат — зыян {MoneyText.Amount(total)} сом.\n{list}",
            $"With this discount the item sells below cost — loss {MoneyText.Amount(total)} som.\n{list}",
            $"Bu indirimle ürün alış fiyatının altında satılıyor — zarar {MoneyText.Amount(total)} som.\n{list}",
            $"Chegirma bilan mahsulot xarid narxidan arzon sotilmoqda — zarar {MoneyText.Amount(total)} so'm.\n{list}");
    }

    /// <summary>2026-10-15: снимок убыточных строк чека ДО оплаты. Раньше журнал убытков писался после
    /// CheckoutAsync(...).ConfigureAwait(false): UpdateLossMarks правил свойства строк не с UI-потока
    /// («Call from invalid thread» глотался try/catch — запись терялась), а _cart.Root к тому моменту
    /// уже новая пустая корзина — скидка на весь чек терялась, убыток занижался. Вызывать на UI-потоке;
    /// строки не меняет. <paramref name="orderDiscount"/> — скидка на чек из окна оплаты (с бонусами).</summary>
    private List<LossSaleLine> CaptureLossLines(double orderDiscount)
    {
        var result = new List<LossSaleLine>();
        try
        {
            var gross = Lines.Sum(l => Math.Max(0, l.LineTotal));
            var share = gross > 0 ? Math.Clamp(orderDiscount / gross, 0, 1) : 0;
            foreach (var line in Lines)
            {
                var cost = UnitCostOf(line);
                if (cost <= 0 || !(line.HasDiscount || share > 0))
                    continue;
                var net = Math.Max(0, line.LineTotal) * (1 - share);
                var loss = Math.Round(Math.Max(0, cost * line.Quantity - net), 2);
                if (loss <= 0.005)
                    continue;
                result.Add(new LossSaleLine
                {
                    Title = line.Title,
                    Quantity = line.Quantity,
                    Unit = line.Unit,
                    UnitPrice = line.UnitPrice,
                    UnitCost = cost,
                    Net = Math.Round(cost * line.Quantity - loss, 2),
                    Loss = loss,
                });
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Убыток по чеку не посчитан: {ex.Message}", "WARNING");
        }
        return result;
    }

    /// <summary>Оплаченный чек с убытком — в журнал для программы владельца (LossSalesStore).
    /// 2026-10-15: пишет из снимка <see cref="CaptureLossLines"/>, снятого до оплаты, — UI не трогает,
    /// можно вызывать с любого потока.</summary>
    private static void RecordLossSale(string? saleId, IReadOnlyList<LossSaleLine>? lossLines)
    {
        try
        {
            if (lossLines is not { Count: > 0 })
                return;
            LossSalesStore.Append(new LossSaleRecord
            {
                At = DateTimeOffset.Now,
                SaleId = saleId,
                Cashier = PosApp.CurrentUserDisplayName ?? "",
                Lines = lossLines.ToList(),
            });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Продажа в убыток не записана: {ex.Message}", "WARNING");
        }
    }

    public void AddProductFromCatalog(CatalogProductTileVm product, string? lineNameOverride = null)
    {
        if (product is null)
            return;

        try
        {
            EnsureCartInitialized();
            var qty = ParseQuantity(ManualQuantity, product.MustWeigh, QuantityRules.ForTile(product)); // 2026-10-26 (ХМ2)
            if (string.IsNullOrWhiteSpace(lineNameOverride))
                _cart.AddItem(product, qty);
            else
                _cart.AddItem(product, qty, lineNameOverride);
            SyncLinesAndTotals();
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
            SyncLinesAndTotals();
            CartMessage = Tr.T("Товар добавлен.", "Товар кошулду.", "Product added.", "Ürün eklendi.", "Mahsulot qo'shildi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART add (piece) failed: {ex}", "CART");
            _prompts.ShowError(Tr.T("Не удалось добавить товар в чек.", "Товарды чекке кошуу мүмкүн болгон жок.", "Could not add the product to the receipt.", "Ürün fişe eklenemedi.", "Mahsulotni chekka qo'shib bo'lmadi."));
        }
    }

    /// <summary>2026-10-01, магазин одежды: вариант товара (размер/цвет) по цене варианта.</summary>
    public void AddVariantFromCatalog(
        CatalogProductTileVm product, double quantity, double unitPrice, string variantId, string label, string? size, string? color)
    {
        if (product is null || quantity <= 0 || string.IsNullOrWhiteSpace(variantId))
            return;

        try
        {
            EnsureCartInitialized();
            _cart.AddVariantItem(product, quantity, unitPrice, variantId, label, size, color);
            SyncLinesAndTotals();
            CartMessage = Tr.T("Товар добавлен.", "Товар кошулду.", "Product added.", "Ürün eklendi.", "Mahsulot qo'shildi.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CART add (variant) failed: {ex}", "CART");
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
            var qty = ParseQuantity(quantity.ToString(CultureInfo.InvariantCulture), product.MustWeigh, QuantityRules.ForTile(product)); // 2026-10-26 (ХМ2)
            _cart.AddItem(product, qty);
            SyncLinesAndTotals();
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
        // 2026-10-02: на пустом чеке «+ Новый чек» не открывает ещё одну пустую вкладку.
        if (LineCount == 0 && GetActiveSession() is { DeferredAt: null } current)
        {
            CartMessage = Tr.T($"«{current.BaseName}» и так пустой — добавляйте товары.", $"«{current.BaseName}» бош — товарларды кошуңуз.",
                $"“{current.BaseName}” is already empty — add products.", $"«{current.BaseName}» zaten boş — ürün ekleyin.", $"«{current.BaseName}» bo'sh — mahsulot qo'shing.");
            return;
        }

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
        SyncLinesAndTotals();
        CartMessage = Tr.T($"Открыт «{session.BaseName}».", $"«{session.BaseName}» ачылды.",
            $"Opened “{session.BaseName}”.", $"«{session.BaseName}» açıldı.", $"«{session.BaseName}» ochildi.");
        RaiseCartCommands();
    }

    /// <param name="closedShiftId">2026-10-17, этап 4: id закрытой смены — для журнала «Удалено из чека»
    /// (к этому моменту PosApp.ActiveShiftId уже пуст).</param>
    public void ClearAfterShiftClose(string? closedShiftId = null)
    {
        // 2026-10-06, стресс-тест перед 1.17.54: «Отложить чек» с 27.09 оставляет чек вкладкой «Отложен ЧЧ:ММ», а закрытие
        // смены стирало все вкладки — отложенный чек пропадал молча (предупреждение спрашивает только про текущий чек).
        // Теперь стирается только текущий чек; другие вкладки с товарами остаются, как раньше оставался список «Отложенные».
        var kept = _sessions
            .Where(s => s.Id != _activeSessionId && GetSessionSummary(s.CartJson).Lines > 0)
            .ToList();
        // 2026-10-17, этап 4: неоплаченные строки текущего чека, стёртые закрытием смены, — в журнал удалений.
        LogCartRemovals(CurrentCartItems(), CartRemovalLogStore.KindShiftClose, shiftId: closedShiftId);
        _cart.Clear();
        _sessions.Clear();
        EnsurePrimarySession();
        if (kept.Count > 0)
        {
            _sessions.AddRange(kept);
            RenameReceiptSessions();
            PosLogger.Log($"SHIFT: после закрытия смены сохранены вкладки с товарами: {kept.Count}.", "SHIFT");
        }
        SyncLinesAndTotals();
        CartMessage = kept.Count > 0
            ? Tr.T($"Отложенные чеки сохранены: {kept.Count}.", $"Калтырылган чектер сакталды: {kept.Count}.", $"Held receipts kept: {kept.Count}.",
                $"Bekleyen fişler korundu: {kept.Count}.", $"Kutishdagi cheklar saqlandi: {kept.Count}.")
            : "";
        RebuildReceiptTabs();
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

        // 2026-10-02, владелец: «если чек пустой, почему автоматически он не закрывается?». Пустая
        // вкладка закрывалась только после оплаты; при переключении на другую она оставалась висеть
        // («Чек 2 • 0 тов.»). Теперь пустую обычную (не отложенную) вкладку, с которой уходят, убираем.
        var leaving = GetActiveSession();
        var dropLeaving = leaving != null && leaving.Id != target.Id && leaving.DeferredAt == null
                          && LineCount == 0 && _sessions.Count > 1;

        PersistActiveSessionSnapshot();
        _previousSessionId = dropLeaving ? null : _activeSessionId;
        _activeSessionId = target.Id;
        if (dropLeaving)
        {
            _sessions.Remove(leaving!);
            PosLogger.Log("Пустая вкладка чека закрыта при переходе на другую.", "CART");
        }
        // Открыли отложенный чек — покупатель вернулся, дальше это обычный чек.
        var wasHeld = target.DeferredAt != null;
        if (wasHeld)
        {
            target.DeferredAt = null;
            RenameReceiptSessions();
        }
        ActiveReceiptTitle = target.BaseName;
        ApplySessionToCart(target);
        SyncLinesAndTotals();
        CartMessage = wasHeld
            ? Tr.T("Отложенный чек снова открыт.", "Калтырылган чек кайра ачылды.", "The held receipt is open again.",
                "Bekleyen fiş yeniden açıldı.", "Kutishdagi chek qayta ochildi.")
            : Tr.T($"Активен «{target.BaseName}».", $"«{target.BaseName}» активдүү.",
                $"Active: “{target.BaseName}”.", $"Etkin: «{target.BaseName}».", $"Faol: «{target.BaseName}».");
        RaiseCartCommands();
    }

    /// <summary>2026-09-26, стресс-тест (149 сканов подряд с паузой 150 мс — в чек попал каждый
    /// второй): пока касса добавляла предыдущий товар, команда была занята (IsBusy и сама
    /// AsyncRelayCommand не пускают повторный запуск), Enter следующего скана отбрасывался, а
    /// очистка поля после добавления стирала уже набранный следующий код. Со сканером это то
    /// же самое: сервис сканера кладёт код в это же поле и зовёт эту же команду. Теперь код
    /// забирается из поля сразу и встаёт в очередь — сканы обрабатываются по порядку, ни один
    /// не теряется, и поле свободно для следующего скана.</summary>
    private readonly Queue<string> _pendingScans = new();
    private readonly object _scanLock = new();
    private bool _scanLoopRunning;

    private bool CanAddByBarcode() => !string.IsNullOrWhiteSpace(BarcodeInput);

    private void SubmitBarcodeInput()
    {
        var text = BarcodeInput;
        BarcodeInput = "";
        EnqueueBarcode(text);
    }

    /// <summary>Скан в очередь добавления (поле ввода, «Добавить», сервис сканера).</summary>
    public void EnqueueBarcode(string? barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return;

        lock (_scanLock)
        {
            _pendingScans.Enqueue(barcode.Trim());
            if (_scanLoopRunning)
                return;
            _scanLoopRunning = true;
        }

        _ = ProcessScanQueueAsync();
    }

    private async Task ProcessScanQueueAsync()
    {
        while (true)
        {
            string next;
            lock (_scanLock)
            {
                if (_pendingScans.Count == 0)
                {
                    _scanLoopRunning = false;
                    return;
                }
                next = _pendingScans.Dequeue();
            }

            await AddByBarcodeAsync(next).ConfigureAwait(true);
        }
    }

    /// <summary>2026-09-28, срочно (живая проверка владельца): весы TM-30F печатают этикетку с
    /// префиксом 21 и СУММОЙ в штрихкоде (2101003013207 = «Алма» 0,220 кг × 60 = 13,20), а режим
    /// компании с сервера «по весу» — касса читала 01320 как 1,320 кг и брала 79,20. Ручная
    /// настройка в окне TM-30F оказалась незаметной. Теперь при ПЕРВОМ скане этикетки с
    /// нестандартным префиксом (не 20 и не 25) касса один раз спрашивает, что напечатано, показывает
    /// оба варианта в деньгах и запоминает ответ для этого префикса на этом компьютере.</summary>
    private async Task<WeightBarcodeParseResult> ConfirmWeightBarcodeKindAsync(
        string barcode, WeightBarcodeParseResult weighted, CatalogProductTileVm product)
    {
        var code = barcode.Trim();
        var prefix = code[..2];
        if (prefix is "20" or "25")
            return weighted;
        var prefs = UserPreferences.Instance;
        var amountSet = UserPreferences.ParseAmountPrefixes(prefs.ScaleAmountPrefixes);
        var weightSet = UserPreferences.ParseAmountPrefixes(prefs.ScaleWeightPrefixes);
        if (amountSet.Contains(prefix) || weightSet.Contains(prefix))
            return weighted;

        var useCodeLayout = string.Equals(WeightBarcodeParser.Layout, "code", StringComparison.OrdinalIgnoreCase);
        if (!int.TryParse(useCodeLayout ? code.Substring(8, 4) : code.Substring(7, 5), out var raw) || raw <= 0)
            return weighted;
        var price = LocalCartService.ParsePrice(product.PriceLine);
        var asWeight = new WeightBarcodeParseResult(weighted.ProductCode, raw / 1000.0, WeightBarcodeValueKind.Weight);
        var amountValue = string.Equals(WeightBarcodeParser.AmountUnit, "som", StringComparison.OrdinalIgnoreCase) ? raw : raw / 100.0;
        var asAmount = new WeightBarcodeParseResult(weighted.ProductCode, amountValue, WeightBarcodeValueKind.Amount);
        var inv = CultureInfo.InvariantCulture;
        var wKg = asWeight.Value.ToString("0.000", inv);
        var wSum = (asWeight.Value * price).ToString("0.00", inv);
        var aSum = asAmount.Value.ToString("0.00", inv);
        var aKg = price > 0 ? asAmount.ResolveWeightKg(price).ToString("0.000", inv) : "?";
        var name = product.Title;

        // 2026-10-22 (модалки, пакет 1): кнопки — сами ответы: «Сумма … сом» (главная) и «Вес … кг» (вторая; Esc и ✕ — тоже
        // «вес», как прежнее «Нет»). Текст больше не называет кнопки «Да» / «Нет». Смысл ответов и запоминание — прежние.
        var isAmount = await _prompts.ConfirmAsync(Tr.T(
            $"Этикетка весов с префиксом {prefix} ({name}). Что напечатано на этикетке — сумма или вес?\n\nСумма: {aSum} сом (вес {aKg} кг)\nВес: {wKg} кг (сумма {wSum} сом)\n\nСверьте с этикеткой. Ответ запомнится для префикса {prefix}; поменять — Настройки → Весы.",
            $"{prefix} префикстүү тараза этикеткасы ({name}). Этикеткада эмне басылган — сумма же салмак?\n\nСумма: {aSum} сом (салмагы {aKg} кг)\nСалмак: {wKg} кг (суммасы {wSum} сом)\n\nЭтикетка менен салыштырыңыз. Жооп {prefix} префикси үчүн эсте калат; өзгөртүү — Жөндөөлөр → Таразалар.",
            $"Scale label with prefix {prefix} ({name}). What is printed on the label — total or weight?\n\nTotal: {aSum} som (weight {aKg} kg)\nWeight: {wKg} kg (total {wSum} som)\n\nCompare with the label. The answer is remembered for prefix {prefix}; change it in Settings → Scales.",
            $"{prefix} önekli terazi etiketi ({name}). Etikette ne basılı — tutar mı ağırlık mı?\n\nTutar: {aSum} som (ağırlık {aKg} kg)\nAğırlık: {wKg} kg (tutar {wSum} som)\n\nEtiketle karşılaştırın. Cevap {prefix} öneki için hatırlanır; değiştirmek için Ayarlar → Teraziler.",
            $"{prefix} prefiksli tarozi yorlig'i ({name}). Yorliqda nima bosilgan — summa yoki vazn?\n\nSumma: {aSum} so'm (vazn {aKg} kg)\nVazn: {wKg} kg (summa {wSum} so'm)\n\nYorliq bilan solishtiring. Javob {prefix} prefiksi uchun eslab qolinadi; o'zgartirish — Sozlamalar → Tarozilar."),
            Tr.T($"Сумма {aSum} сом", $"Суммасы {aSum} сом", $"Total {aSum} som", $"Tutar {aSum} som", $"Summa {aSum} so'm"),
            Tr.T($"Вес {wKg} кг", $"Салмак {wKg} кг", $"Weight {wKg} kg", $"Ağırlık {wKg} kg", $"Vazn {wKg} kg")).ConfigureAwait(true);

        if (isAmount)
        {
            amountSet.Add(prefix);
            weightSet.Remove(prefix);
        }
        else
        {
            weightSet.Add(prefix);
            amountSet.Remove(prefix);
        }
        prefs.ScaleAmountPrefixes = string.Join(",", amountSet.OrderBy(p => p, StringComparer.Ordinal));
        prefs.ScaleWeightPrefixes = string.Join(",", weightSet.OrderBy(p => p, StringComparer.Ordinal));
        WeightBarcodeParser.AmountPrefixes = amountSet;
        // 2026-09-28: и правило «вес» — парсер теперь учитывает его сам (WeightPrefixes), даже
        // если режим компании прочёл бы этот префикс как сумму (Настройки → Весы → «Штрих-код»).
        WeightBarcodeParser.WeightPrefixes = weightSet;
        prefs.SaveToDisk();
        PosLogger.Log($"[SCALE] prefix {prefix} confirmed as {(isAmount ? "amount" : "weight")} by cashier", "CART");
        return isAmount ? asAmount : asWeight;
    }

    private async Task AddByBarcodeAsync(string barcode)
    {
        await RunOnUiThreadAsync(() => IsBusy = true).ConfigureAwait(false);
        try
        {
            // 2026-10-04, ТЗ разработчика NurCRM: QR клиента «NURCRM» + 12 цифр телефона проверяем ПЕРВЫМ,
            // до товара и весового кода. Только строки с префиксом NURCRM — обычные штрихкоды идут дальше как
            // были. Раньше строки журнала ниже: полный номер клиента в журнал не пишем (там будет маска).
            if (ClientQrCode.TryParse(barcode, out var clientQr))
            {
                await HandleClientQrAsync(clientQr!).ConfigureAwait(true);
                return;
            }

            // 2026-09-15, диагностика живой жалобы ("штрих-М не читает") — снять после того как
            // разберёмся с реальным примером кода весов Штрих-М: без этой строки не видно, что
            // именно пришло со сканера и на каком именно шаге код не распознался.
            PosLogger.Log(
                $"[DEBUG] Barcode scanned: '{barcode}' (len={barcode.Length}), " +
                $"weightLayout={WeightBarcodeParser.Layout}, weightMode={WeightBarcodeParser.Mode}",
                "CART");
            // 2026-09-30: префиксы весов с прямой отправкой (Rongta — сумма в этикетке), в т.ч. записанные
            // программой владельца на этом компьютере.
            ScaleLabelCodeRegistry.ApplySharedAmountPrefixes();

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

            // 2026-10-06, исследование «Кассы для одежды» (О-01): штрихкод размера (этикетка «Платье — 44, Красный»)
            // — сразу этот размер, без окна выбора. Справочник ведёт касса (VariantBarcodeIndex): сервер такой
            // штрихкод пока не находит. Проверяется до весового кода: внутренние штрихкоды размеров тоже бывают на «2».
            if (MarketSpheres.IsClothing && VariantBarcodeIndex.Find(barcode) is { } variantHit)
            {
                var variantProduct = CatalogCacheService.Products.FirstOrDefault(p =>
                    string.Equals(p.Id, variantHit.ProductId, StringComparison.OrdinalIgnoreCase));
                if (variantProduct != null)
                {
                    PosLogger.Log($"[DEBUG] Barcode is a size/color label: product {variantHit.ProductId}, variant {variantHit.Variant.Id}.", "CART");
                    _scannedVariant = (variantHit.ProductId, variantHit.Variant);
                    await AddFoundCatalogProductAsync(variantProduct).ConfigureAwait(true);
                    _scannedVariant = null;
                    return;
                }
            }

            // Штрих-код весов (Штрих-М и совместимые) несёт вес прямо в самом коде — обычным
            // поиском по штрих-коду товар так не найти, код на каждое взвешивание уникален.
            // 2026-10-06 (Р-08, магазин одежды): в сфере «Одежда» весовых этикеток обычно нет, а внутренние
            // штрихкоды одежды начинаются на «2» — незнакомый код на «2» открывал «сумма или вес?». Там весовой
            // код разбираем, только если кассир явно настроил этот префикс (Настройки → Весы → «Штрих-код»).
            if (WeightBarcodeAllowedHere(barcode) && WeightBarcodeParser.TryParse(barcode, out var weighted))
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
                            $"{weighted.ProductCode} коддуу товар каталогдон табылган жок.", $"Product with code {weighted.ProductCode} was not found in the catalog.", $"Kodu {weighted.ProductCode} olan ürün katalogda bulunamadı.", $"{weighted.ProductCode} kodli mahsulot katalogda topilmadi."))).ConfigureAwait(false);
                    return;
                }

                weighted = await ConfirmWeightBarcodeKindAsync(barcode, weighted, weighedProduct).ConfigureAwait(true);
                var weightKg = weighted.ResolveWeightKg(LocalCartService.ParsePrice(weighedProduct.PriceLine));
                if (_addWeighedProductWithKnownWeight != null)
                    await _addWeighedProductWithKnownWeight(weighedProduct, weightKg).ConfigureAwait(true);
                return;
            }

            PosLogger.Log("[DEBUG] Barcode not in catalog and not a valid weight/amount code.", "CART");

            // 2026-09-17: локальный кэш мог ещё не досинхронизироваться с сайтом (товар
            // добавили/поменяли на сайте только что) — прежде чем сказать кассиру "не найдено",
            // быстро спрашиваем сервер напрямую по этому штрих-коду.
            var serverLookup = await TryFindProductOnServerAsync(barcode).ConfigureAwait(true);
            if (serverLookup.Product is { } serverProduct)
            {
                await AddFoundCatalogProductAsync(serverProduct, ResolveVariantLineName(serverProduct, barcode)).ConfigureAwait(true);
                return;
            }

            if (serverLookup.Unavailable)
            {
                await RunOnUiThreadAsync(() => _prompts.ShowWarning(Tr.T(
                    "Не удалось проверить штрихкод на сервере. Проверьте связь и повторите сканирование; создание нового товара пока недоступно.",
                    "Штрихкодду серверден текшерүү мүмкүн болгон жок. Байланышты текшерип, кайра сканерлеңиз; азырынча жаңы товар түзүү жеткиликсиз.",
                    "The barcode could not be verified with the server. Check the connection and scan again; creating a new product is unavailable for now.",
                    "Barkod sunucuda doğrulanamadı. Bağlantıyı kontrol edip tekrar tarayın; şu anda yeni ürün oluşturma kullanılamıyor.",
                    "Shtrix-kodni serverda tekshirib bo'lmadi. Ulanishni tekshirib qayta skanerlang; hozircha yangi mahsulot yaratib bo'lmaydi."))).ConfigureAwait(false);
                return;
            }

            if (_onBarcodeNotFound != null)
            {
                // Неизвестный штрих-код (2026-09-07): вместо голого предупреждения — предложить
                // добавить товар на склад (карточка с подставленным кодом) или пропустить.
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
                _prompts.ShowError(Tr.T("Ошибка добавления товара.", "Товарды кошууда ката кетти.", "Error adding the product.", "Ürün eklenirken hata oluştu.", "Mahsulotni qo'shishda xato."))).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    /// <summary>2026-10-06 (О-01): отсканирован штрихкод размера — вариант уже известен; окно добавления товара
    /// берёт его через <see cref="TakeScannedVariant"/> и не открывает выбор размера.</summary>
    private (string ProductId, ProductVariantDto Variant)? _scannedVariant;

    /// <summary>Вариант из скана штрихкода размера для этого товара (один раз); null — скана размера не было.</summary>
    public ProductVariantDto? TakeScannedVariant(string productId)
    {
        if (_scannedVariant is not { } scanned || !string.Equals(scanned.ProductId, productId, StringComparison.OrdinalIgnoreCase))
            return null;
        _scannedVariant = null;
        return scanned.Variant;
    }

    /// <summary>2026-10-06 (Р-08): в сфере «Одежда» весовой код — только с явно настроенным префиксом.</summary>
    private static bool WeightBarcodeAllowedHere(string barcode)
    {
        if (!MarketSpheres.IsClothing)
            return true;
        var code = barcode.Trim();
        if (code.Length < 2)
            return false;
        var prefix = code[..2];
        var allowed = WeightBarcodeParser.AmountPrefixes.Contains(prefix) || WeightBarcodeParser.WeightPrefixes.Contains(prefix);
        if (!allowed)
            PosLogger.Log($"[DEBUG] Clothing mode: prefix {prefix} is not set up as a scale label — not parsed as weight.", "CART");
        return allowed;
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
    }

    /// <summary>2026-09-17: скан не нашёл товар в локальном кэше — прежде чем показать кассиру
    /// "не найдено", быстро спрашиваем сервер напрямую (поиск по штрих-коду). Локальный кэш
    /// синхронизируется периодически и может на минуту-другую отставать от сайта: товар уже
    /// добавлен/изменён в NurCRM, а кассир ещё не видит его при сканировании. Короткий таймаут
    /// и антидребезг промахов — чтобы повторные сканы несуществующего кода не долбили сервер и
    /// не подвешивали интерфейс, если сети нет вовсе.</summary>
    private sealed record BarcodeServerLookup(CatalogProductTileVm? Product, bool Unavailable);

    private async Task<BarcodeServerLookup> TryFindProductOnServerAsync(string barcode)
    {
        // 2026-10-15: намеренный автономный режим (UseLocalOperations) — товары и так создаются
        // локально (ProductEditDialog), сервер не спрашиваем: сразу «не найдено» → предложение
        // добавить товар, как было до 1.17.58. Проверяем раньше CatalogApi: в автономном
        // запуске API может быть не создан, и это не повод блокировать добавление товара.
        // 2026-10-16: и раньше IsServerOutage — при автономном входе без интернета монитор аварии
        // тоже уходит в Outage, и скан незнакомого кода говорил «сервер недоступен» вместо
        // предложения добавить товар локально.
        if (OfflineModeHelper.UseLocalOperations)
            return new(null, false);

        // 2026-10-15: авария сервера (временная) — «сервер недоступен», новый товар не создаём:
        // иначе после восстановления связи появится дубль товара, который уже есть на сайте.
        if (OfflineModeHelper.IsServerOutage)
            return new(null, true);

        if (string.IsNullOrWhiteSpace(barcode) || PosApp.CatalogApi is null)
            return new(null, true);

        if (_serverBarcodeMissCache.TryGetValue(barcode, out var missedAt) &&
            DateTime.UtcNow - missedAt < ServerBarcodeMissTtl)
            return new(null, false);

        try
        {
            using var cts = new CancellationTokenSource(ServerBarcodeLookupTimeout);
            var results = await PosApp.CatalogApi.ProductsSearchAsync(barcode, limit: 5, cts.Token).ConfigureAwait(true);
            var dto = results.FirstOrDefault(d => string.Equals(d.Barcode?.Trim(), barcode, StringComparison.OrdinalIgnoreCase));
            if (dto == null)
            {
                _serverBarcodeMissCache[barcode] = DateTime.UtcNow;
                return new(null, false);
            }

            var tile = ProductCatalogMapper.TryTile(dto, PosApp.Settings?.ApiBaseUrl ?? "");
            if (tile == null)
            {
                return new(null, true);
            }

            LocalProductRepository.Instance.UpsertFromTiles([tile]);
            var existingIndex = CatalogCacheService.Products.FindIndex(p => p.Id == tile.Id);
            if (existingIndex >= 0)
                CatalogCacheService.Products[existingIndex] = tile;
            else
                CatalogCacheService.Products.Add(tile);
            CatalogCacheService.NotifyCatalogChanged();

            PosLogger.Log($"[DEBUG] Barcode '{barcode}' missing from local cache but found on server: productId={tile.Id}.", "CART");
            return new(tile, false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Server barcode fallback lookup failed for '{barcode}': {ex.GetType().Name}: {ex.Message}", "CART");
            return new(null, true);
        }
    }

    /// <summary>2026-10-04, ТЗ 1.17.48 P0-5: пока открыт вопрос «пополнить склад?» по строке — оплату не начинаем
    /// (кассир ввёл количество и сразу нажал «Оплатить»: поле применилось по уходу фокуса, а чек ушёл бы со
    /// старым количеством). Во время оплаты поздние правки количества про склад не спрашивают.</summary>
    private async Task PayAsync()
    {
        // 2026-10-18, аудит доступов: продажа/оплата — только с доступом «Продажа» (владельца/админа и компании без
        // выставленных доступов не запирает — см. PermissionService). Сам чек не трогаем.
        if (_permissions is not null && !_permissions.HasPermission(PosPermissions.Sale))
        {
            PosLogger.Log($"Permission denied: {PosPermissions.Sale}", "WARNING");
            _prompts.ShowWarning(NurMarketKassa.Services.PermissionService.CashierWorkDeniedMessage(PosPermissions.Sale));
            return;
        }

        if (!_stockOffersOpen.IsEmpty)
        {
            CartMessage = Tr.T(
                "Сначала ответьте на вопрос о пополнении склада.",
                "Адегенде кампаны толуктоо суроосуна жооп бериңиз.",
                "Answer the restock question first.",
                "Önce stok ekleme sorusunu yanıtlayın.",
                "Avval omborni to'ldirish savoliga javob bering.");
            return;
        }

        _paymentInProgress = true;
        try
        {
            await PayCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _paymentInProgress = false;
        }
    }

    private async Task PayCoreAsync()
    {
        // Способ оплаты от кнопок «Наличные»/«Безнал» раскладки (2026-09-28) — одноразовый:
        // забираем сразу, чтобы следующее обычное «Оплатить» открылось как всегда.
        var preferredMethod = PreferredPaymentMethod;
        PreferredPaymentMethod = null;

        if (Lines.Count == 0)
            return;

        // 2026-09-28, денежный баг продажи №1136: акции товаров NurCRM — по последнему каталогу,
        // ДО того как посчитан итог окна оплаты (строка могла попасть в чек раньше, чем касса
        // узнала об акции). Иначе окно показало бы полную цену, а сервер провёл бы со скидкой.
        if (ReceiptSnapshotCartEditor.RefreshPromotionRules(_cart))
        {
            PosLogger.Log("PAY: акции товаров в чеке обновлены по каталогу перед оплатой.", "PAYMENT");
            SyncLinesAndTotals();
        }

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
                "«Чыгаша» себебинен чектин жыйынтыгы терс болуп калды — товар кошуңуз же чыгаша суммасын азайтыңыз.",
                "The receipt total is negative because of an “Expense” line — add a product or reduce the expense amount.",
                "«Gider» satırı yüzünden fiş toplamı negatif — bir ürün ekleyin veya gider tutarını azaltın.",
                "Chek summasi «Xarajat» sababli manfiy — mahsulot qo'shing yoki xarajat summasini kamaytiring.");
            return;
        }

        PosLogger.Log("PAY start", "PAYMENT");

        // 2026-09-26, лог магазина: после ответа «Смена не открыта» касса сбрасывала смену, но
        // следующее «Оплатить» снова шло без смены — и так раз за разом, пока кассир сам не
        // нажмёт «Открыть смену» или не отсканирует новый товар. Теперь без смены оплата сначала
        // сверяется с сервером и при необходимости предлагает открыть смену.
        if (EnsureShiftBeforePayment is { } ensureShift && !await ensureShift().ConfigureAwait(true))
        {
            PosLogger.Log("PAY aborted: shift not open", "PAYMENT");
            return;
        }

        // Захватываем ID сессии ДО сетевого запроса оплаты: пока он в полёте (может занять
        // секунду и больше), UI-поток свободен, и кассир вполне может успеть переключиться на
        // другую вкладку чека (обычное дело — параллельно обслуживает другого покупателя).
        // Если определять "какую вкладку только что оплатили" уже ПОСЛЕ ответа сервера через
        // "текущая активная сессия", можно по ошибке удалить не оплаченный, а тот чек, на
        // который кассир успел переключиться, — включая чужие ещё не оплаченные товары.
        _paidSessionId = GetActiveSession()?.Id;
        _paidSessionPreviousId = _previousSessionId;
        // 2026-10-04: клиент, выбранный в этом чеке сканом QR клиента NurCRM, и сам чек — чтобы
        // после оплаты снять клиента именно с него (_paidSessionId обнуляется позже).
        var receiptClient = GetActiveSession()?.Client;
        var paidSessionId = _paidSessionId;

        await RunOnUiThreadAsync(() =>
        {
            IsBusy = true;
            // 2026-10-26: новая оплата — итог прошлой на экране покупателя больше не нужен, а его отложенный сброс не должен задеть эту.
            ForgetPaymentOnCustomerDisplay();
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
            if (preferredMethod == "transfer")
                checkoutVm.IsTransfer = true;
            if (receiptClient != null)
                checkoutVm.PreselectClient(receiptClient);
            // 2026-10-02: после «Оформить как прокат» в чеке только строка проката — кнопку больше не показываем.
            checkoutVm.HasRentableItems = CartDisplayHelper.EnumerateItems(_cart.Root).Any(it => !CartDisplayHelper.IsCustomLine(it));
            var confirmed = await _windowService
                .ShowDialogAsync<CheckoutViewModel, bool?>(checkoutVm)
                .ConfigureAwait(false);

            if (confirmed != true && checkoutVm.RentalRequested)
            {
                PosLogger.Log("PAY: кассир выбрал «Оформить как прокат».", "PAYMENT");
                await RunOnUiThreadAsync(() =>
                    _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Idle)).ConfigureAwait(false);
                var clientId = checkoutVm.ClientId;
                var clientName = checkoutVm.HasSelectedClient ? checkoutVm.SelectedClientName : null;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300).ConfigureAwait(false);   // окно оплаты закрылось, IsBusy снят
                    await _dispatcher.InvokeAsync(() => ConvertCartToRentalAsync(clientId, clientName)).ConfigureAwait(false);
                });
                return;
            }

            if (confirmed != true)
            {
                PosLogger.Log("PAY canceled by cashier", "PAYMENT");
                await RunOnUiThreadAsync(() =>
                    _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Idle)).ConfigureAwait(false);
                return;
            }

            var cashReceived = checkoutVm.CashReceivedForApi;

            PosLogger.Log(
                // 2026-09-28: итог окна оплаты в журнале — по нему кассир взял деньги (см. продажу №1136).
                $"PAY API checkout: method={checkoutVm.PaymentMethod}, window_total={checkoutVm.EffectiveTotalDue:0.00}, cash={cashReceived}, print={checkoutVm.IsPrintReceiptEnabled}, " +
                $"consultant={checkoutVm.ConsultantIdForApi ?? "-"}",
                "PAYMENT");

            if (_checkoutUiFlow != null)
            {
                await _checkoutUiFlow.ShowPaymentProcessingAsync(totals.TotalDue).ConfigureAwait(false);
                paymentStatusActive = true;
            }

            // 2026-10-15: убыточные строки — снимком ДО оплаты и на UI-потоке: после CheckoutAsync корзина
            // уже новая (скидка на чек потеряна), а мы — на потоке пула. Скидка на чек — из окна оплаты.
            List<LossSaleLine>? lossLines = null;
            var lossOrderDiscount = checkoutVm.OrderDiscountAmount + checkoutVm.PointsRedeemed;
            await RunOnUiThreadAsync(() => lossLines = CaptureLossLines(lossOrderDiscount)).ConfigureAwait(false);

            var result = await _checkout.CheckoutAsync(new PosCheckoutRequest
            {
                PaymentMethod = checkoutVm.PaymentMethod,
                CashReceived = cashReceived,
                PrintReceipt = checkoutVm.IsPrintReceiptEnabled,
                OrderDiscountBody = checkoutVm.PendingOrderDiscountBody,
                ClientId = checkoutVm.ClientId,
                NonCashReceived = checkoutVm.NonCashReceivedForApi,
                ConsultantId = checkoutVm.ConsultantIdForApi,
                ConsultantCommissionEnabled = checkoutVm.ConsultantCommissionEnabledForApi,
                ConsultantCommissionPercent = checkoutVm.ConsultantCommissionPercentForApi,
                ConsultantName = checkoutVm.ConsultantNameForReceipt,
                // 2026-09-28, продажа №1136: сумма, которую кассир видел и взял, — с ней сервис
                // оплаты сверяет итог запроса/серверной корзины перед отправкой.
                ExpectedTotal = checkoutVm.EffectiveTotalDue,
                DebtSchedule = checkoutVm.DebtScheduleForApi,
                // 2026-10-20, владелец (долг Д5): клиент — в чек продажи «в долг» (имя и телефон маской).
                ClientNameForReceipt = checkoutVm.IsDebtMode ? checkoutVm.SelectedClient?.Name : null,
                ClientPhoneForReceipt = checkoutVm.IsDebtMode ? checkoutVm.SelectedClient?.Phone : null,
            }).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var error = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? PaymentErrorMessages.GenericFailure
                    : result.ErrorMessage;
                PosLogger.Log($"PAY failed result: {error}", "PAYMENT");
                // 2026-09-28: после отказа (в том числе сверки суммы с сервером) чек мог
                // обновиться данными серверной корзины — экран показывает то, что реально в чеке.
                await RunOnUiThreadAsync(() =>
                {
                    SyncLinesAndTotals();
                }).ConfigureAwait(false);
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
            RecordSoldLineItemsForHistory(TryReadSaleId(result));
            RecordLossSale(TryReadSaleId(result), lossLines);
            CreditOrRedeemLoyaltyPoints(checkoutVm, result);
            // 2026-10-04, стресс-тест на Android: касса, закрытая сразу после оплаты (сбой, разряд, смахнули из
            // недавних), при запуске восстанавливала ОПЛАЧЕННЫЙ чек с товарами — риск продать их второй раз.
            // Корзина к этому месту уже новая (PosCheckoutService), а на диск состояние попадало только после окна
            // «Платёж принят» и ещё 0,4 с. Теперь окно кассы пишет его сразу (MainWindow, PaymentCommitted).
            await RunOnUiThreadAsync(() => PaymentCommitted?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
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
                    ? OfflineModeHelper.IsServerOutage
                        // 2026-09-29: авария сервера — кассиру коротко, что с чеком, без причин сбоя.
                        ? Tr.T("Чек сохранён, отправится автоматически.", "Чек сакталды, автоматтык түрдө жөнөтүлөт.",
                            "Receipt saved, it will be sent automatically.", "Fiş kaydedildi, otomatik olarak gönderilecek.",
                            "Chek saqlandi, avtomatik ravishda yuboriladi.")
                        : Tr.T(
                        "Оплата сохранена. Данные будут отправлены при восстановлении связи.",
                        "Төлөм сакталды. Байланыш калыбына келгенде маалымат жиберилет.", "Payment saved. The data will be sent once the connection is restored.", "Ödeme kaydedildi. Veriler bağlantı yeniden kurulunca gönderilecek.", "To'lov saqlandi. Ma'lumotlar aloqa tiklanganda yuboriladi.")
                    : Tr.T("Платёж принят. Открываем новый чек.", "Төлөм кабыл алынды. Жаңы чек ачылууда.", "Payment accepted. Opening a new receipt.", "Ödeme alındı. Yeni fiş açılıyor.", "To'lov qabul qilindi. Yangi chek ochilmoqda.");
                await _checkoutUiFlow.ShowPaymentResultAsync(true, successMessage).ConfigureAwait(false);
                paymentStatusActive = false;
            }

            // 2026-09-28, продажа №1136: сервер провёл продажу не на ту сумму, что была в окне
            // оплаты, — кассир должен увидеть это сразу и вернуть/добрать разницу, а не узнать
            // из Z-отчёта. Окно модальное (после окна «Платёж принят»): пропустить его нельзя.
            if (!string.IsNullOrWhiteSpace(result.TotalMismatchWarning))
            {
                PosLogger.Log(
                    $"PAY total mismatch shown to cashier: window={checkoutVm.EffectiveTotalDue:0.00}, server={result.TotalAmount:0.00}",
                    "WARNING");
                await _dialogService.ShowErrorAsync(result.TotalMismatchWarning).ConfigureAwait(false);
            }

            await RunOnUiThreadAsync(() =>
            {
                // 2026-10-20, владелец (долг Д5): продажа «в долг» — покупатель видит, сколько в долг и до какого числа.
                var thanks = Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışverişiniz için teşekkürler!", "Xaridingiz uchun rahmat!");
                if (checkoutVm.IsDebtMode && checkoutVm.DebtScheduleForApi is { } debtPlan)
                {
                    var debtSum = MoneyText.Som(debtPlan.Payments.Sum(p => p.Amount));
                    var dueText = debtPlan.LastDueDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                    thanks += "\n" + Tr.T($"В долг {debtSum} — до {dueText}", $"Карызга {debtSum} — {dueText} чейин", $"On credit {debtSum} — until {dueText}",
                        $"Veresiye {debtSum} — {dueText} tarihine kadar", $"Qarzga {debtSum} — {dueText} gacha");
                }
                // 2026-10-26: внесено / сдача / оплачено и «Оплата принята» — одним вызовом (раньше здесь стояли суммы для наличных и статус).
                ShowPaymentResultOnCustomerDisplay(checkoutVm.PaymentMethod, cashReceived,
                    result.TotalMismatchWarning != null ? result.TotalAmount : checkoutVm.EffectiveTotalDue, thanks);
                // 2026-10-04: чек оплачен — клиент QR остаётся у этого покупателя, а не переходит к следующему.
                ForgetReceiptClient(paidSessionId);
                SyncLinesAndTotals();
                // 2026-10-18, ревью этапа 6: что реально проведено — для плашки «Продажа прошла» и копии именно этого чека.
                LastSale = new LastSaleInfo(
                    result.TotalAmount,
                    checkoutVm.PaymentMethod,
                    _lastCashReceivedForDisplay,
                    cashReceived,
                    _lastChangeDueForDisplay,
                    result.SavedOffline,
                    TryReadSaleId(result),
                    result.CartJsonSnapshot,
                    DateTime.Now,
                    // 2026-10-21 (ревью денег, п. 1): безналичная часть — для журнала возвратов.
                    checkoutVm.NonCashReceivedForApi);
                CheckoutSucceeded?.Invoke(this, EventArgs.Empty);
                CartMessage = result.SavedOffline
                    ? result.InfoMessage ?? Tr.T("Оплата сохранена локально.", "Төлөм локалдык түрдө сакталды.", "Payment saved locally.", "Ödeme yerel olarak kaydedildi.", "To'lov shu kompyuterda saqlandi.")
                    : result.InfoMessage ?? Tr.T("Оплата выполнена. Новый чек открыт.", "Төлөм аткарылды. Жаңы чек ачылды.", "Payment completed. A new receipt has been opened.", "Ödeme tamamlandı. Yeni fiş açıldı.", "To'lov amalga oshirildi. Yangi chek ochildi.");
                if (_rentalAwaitingPayment is { } rentalNo)
                {
                    _rentalAwaitingPayment = null;
                    CartMessage = Tr.T($"Прокат №{rentalNo} оплачен. Он закроется, когда клиент вернёт вещь: «Прокат» → номер с чека → «Принять возврат».",
                        $"Прокат №{rentalNo} төлөндү. Кардар буюмду кайтарганда жабылат: «Прокат» → чектеги номер → «Кайтарууну кабыл алуу».",
                        $"Rental #{rentalNo} paid. It closes when the client returns the item: “Rentals” → number from the receipt → “Take back”.",
                        $"Kiralama №{rentalNo} ödendi. Müşteri ürünü iade edince kapanır: «Kiralama» → fişteki numara → «İadeyi al».",
                        $"Prokat №{rentalNo} to'landi. Mijoz buyumni qaytarganda yopiladi: «Prokat» → chekdagi raqam → «Qaytarishni qabul qilish».");
                }
            }).ConfigureAwait(false);

            // 2026-10-04, отчёт о производительности (п. 9): чек печатается в фоне, уже после сброса чека —
            // не напечатался, кассир видит то же сообщение, что раньше показывалось сразу после оплаты.
            if (result.ReceiptPrintTask is { } printTask)
                _ = ReportReceiptNotPrintedAsync(printTask, result.SavedOffline);

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

    /// <summary>2026-10-04, п. 9: дождаться фоновой печати чека и, если чек не напечатан, сказать кассиру
    /// теми же словами, что и раньше (тогда — сразу после оплаты, до сброса чека).</summary>
    private async Task ReportReceiptNotPrintedAsync(Task<bool> printTask, bool savedOffline)
    {
        bool printed;
        try
        {
            printed = await printTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PAY: фоновая печать чека упала: {ex.Message}", "PRINTER");
            printed = false;
        }

        if (printed)
            return;

        PosLogger.Log("PAY: чек не напечатан (печать после оплаты, в фоне) — кассиру показано сообщение.", "PRINTER");
        var message = savedOffline
            ? Tr.T("Продажа сохранена, но чек не напечатан; используйте повторную печать.",
                "Сатуу сакталды, бирок чек басылган жок; чекти кайра басып чыгарыңыз.",
                "The sale is saved, but the receipt wasn't printed; use reprint.",
                "Satış kaydedildi ancak fiş yazdırılmadı; yeniden yazdırmayı kullanın.",
                "Sotuv saqlandi, lekin chek chop etilmadi; qayta chop etishdan foydalaning.")
            : Tr.T("Оплата выполнена, но чек не напечатан; используйте повторную печать.",
                "Төлөм аткарылды, бирок чек басылган жок; чекти кайра басып чыгарыңыз.",
                "Payment completed, but the receipt wasn't printed; use reprint.",
                "Ödeme tamamlandı ancak fiş yazdırılmadı; yeniden yazdırmayı kullanın.",
                "To'lov amalga oshirildi, lekin chek chop etilmadi; qayta chop etishdan foydalaning.");
        try
        {
            await RunOnUiThreadAsync(() => CartMessage = message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PAY: сообщение «чек не напечатан» не показано: {ex.Message}", "PRINTER");
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
                // 2026-10-23 (аудит, круг 8, В-1): в журнале некорректных чеков — имя кассира, id только если имени нет.
                CashierName = !string.IsNullOrWhiteSpace(PosApp.CurrentUserDisplayName) ? PosApp.CurrentUserDisplayName : PosApp.CurrentUserId,
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
    /// <summary>Номер продажи из ответа сервера на оплату. У офлайн-продажи его нет — тогда
    /// строка истории останется без номера, и бэкфилл на другой кассе подтянет этот чек как
    /// чужой. Задвоения не будет: свою запись касса делает только у себя, а после выгрузки
    /// офлайн-чека сервер вернёт тот же чек уже с номером.</summary>
    private static string? TryReadSaleId(PosCheckoutResult result)
    {
        if (result.CheckoutResponse is not { } response || response.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "id", "sale_id", "sale" })
        {
            if (!response.TryGetProperty(key, out var value))
                continue;

            var text = value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private void RecordSoldLineItemsForHistory(string? saleId)
    {
        // 2026-10-01: подсказки допродажи этого чека получают номер продажи (выручка допродажи).
        try { UpsellService.LinkSale(_upsellCartKey, saleId); }
        catch (Exception ex) { PosLogger.Log($"Допродажа: продажа не привязана ({ex.Message}).", "WARNING"); }

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
                SoldLineItemsStore.AppendSale(lines, saleId);
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
            // 2026-10-05, запрос NurCRM: бонусы на сервере — списание и начисление отдельными операциями с номером чека
            // (сначала списание, чтобы хватило баланса). Без связи — очередь ServerLoyalty.
            var redeemed = checkoutVm.PointsRedeemed;
            var earned = checkoutVm.EarnedPointsPreview;
            _ = Task.Run(async () =>
            {
                if (redeemed >= 0.005)
                    await ServerLoyalty.PostAsync(clientId, -redeemed, "redeem", saleId, null).ConfigureAwait(false);
                if (earned >= 0.005)
                    await ServerLoyalty.PostAsync(clientId, earned, "earn", saleId, null).ConfigureAwait(false);
            });

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
            // 2026-10-26, владелец (второй монитор): итог оплаты («внесено», «сдача») держался ровно 4 с, хотя в настройках
            // «Очистить экран через, сек.» — например, 10. Теперь после удачной оплаты ждём не меньше этой настройки.
            var version = Interlocked.Increment(ref _customerDisplayResetVersion);
            var delayMs = 4000;
            if (_customerDisplayShowsPayment)
                delayMs = Math.Max(delayMs, UserPreferences.Instance.CustomerDisplay.ClearDelaySeconds * 1000);
            await Task.Delay(delayMs).ConfigureAwait(false);
            await RunOnUiThreadAsync(() =>
            {
                // началась новая оплата или следующий покупатель — экран уже очищен, этот сброс устарел
                if (version != Volatile.Read(ref _customerDisplayResetVersion))
                    return;
                _lastCashReceivedForDisplay = null;
                _lastChangeDueForDisplay = null;
                _lastPaidAmountForDisplay = null;
                _customerDisplayShowsPayment = false;
                _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Idle);
                PushCustomerDisplay();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PAY status reset failed: {ex}", "PAYMENT");
        }
    }

    /// <summary>2026-10-26, владелец (второй монитор): итог удачной оплаты на экране покупателя — «Оплата принята», сколько
    /// внесено и сдача (наличные), для безнала, смешанной оплаты и предоплаты долга — сколько оплачено, без выдуманной сдачи.
    /// Только показ: деньги и чек не меняются. Раньше это стояло прямо в PayCoreAsync и умело только наличные.</summary>
    private void ShowPaymentResultOnCustomerDisplay(string paymentMethod, string cashReceived, double effectiveTotal, string message)
    {
        var parsed = double.TryParse(cashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var cashPaid);
        if (paymentMethod == "cash" && parsed)
        {
            _lastCashReceivedForDisplay = cashPaid;
            // 2026-09-13, живой баг: totals был посчитан ДО открытия диалога оплаты, а
            // кассир мог внутри него применить скидку на чек или списать баллы лояльности
            // (checkoutVm.EffectiveTotalDue меняется, totals.TotalDue — нет). С клиента
            // списывается верная (пересчитанная) сумма, но экран покупателя показывал
            // сдачу от СТАРОЙ суммы — например, скидка снизила сумму на 85 сом, а сдача на
            // экране покупателя показывала на 85 сом меньше, чем реально нужно вернуть.
            // 2026-09-28: если сервер провёл продажу на другую сумму (TotalMismatchWarning),
            // сдача на экране покупателя — от суммы сервера (effectiveTotal уже учитывает это).
            _lastChangeDueForDisplay = Math.Max(0, cashPaid - effectiveTotal);
        }
        else
        {
            _lastCashReceivedForDisplay = null;
            _lastChangeDueForDisplay = null;
        }
        // оплачено: наличные, безнал и смешанная — весь чек; «в долг» — только предоплата, если она была
        _lastPaidAmountForDisplay = paymentMethod == "debt"
            ? (parsed && cashPaid > 0.004 ? cashPaid : null)
            : (effectiveTotal > 0.004 ? effectiveTotal : null);
        _customerDisplayShowsPayment = true;
        _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Success, message);
    }

    /// <summary>2026-10-26: убрать итог прошлой оплаты (суммы и пометку) и отменить её отложенный сброс экрана.</summary>
    private void ForgetPaymentOnCustomerDisplay()
    {
        Interlocked.Increment(ref _customerDisplayResetVersion);
        _lastCashReceivedForDisplay = null;
        _lastChangeDueForDisplay = null;
        _lastPaidAmountForDisplay = null;
        _customerDisplayShowsPayment = false;
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
                    _prompts.ShowWarning(Tr.T("Корзина пуста — нечего откладывать.", "Себет бош — калтырууга эч нерсе жок.", "The cart is empty — nothing to hold.", "Sepet boş — beklemeye alınacak bir şey yok.", "Savat bo'sh — kutishga qo'yadigan narsa yo'q."))).ConfigureAwait(false);
                return;
            }

            PersistActiveSessionSnapshot();

            // 2026-09-27, владелец: «при нажатии "Отложить чек" новый чек же должен выходить» —
            // раньше чек уходил в скрытый список «Отложенные» (⋮ Ещё), корзина пустела, и казалось,
            // что чек пропал. Теперь он остаётся вкладкой «Отложен ЧЧ:ММ» рядом, а кассиру
            // открывается новый пустой чек. Прежний путь — только когда вкладок уже максимум.
            if (_sessions.Count < MaxOpenReceipts)
            {
                await RunOnUiThreadAsync(() =>
                {
                    var held = GetActiveSession();
                    if (held != null)
                        held.DeferredAt = DateTime.Now;
                    CreateNewReceipt();
                    var heldName = held?.BaseName ?? "";
                    CartMessage = Tr.T($"Чек отложен — он во вкладке «{heldName}». Открыт новый чек.",
                        $"Чек калтырылды — ал «{heldName}» өтмөгүндө. Жаңы чек ачылды.",
                        $"Receipt held — it's in the “{heldName}” tab. A new receipt is open.",
                        $"Fiş beklemeye alındı — «{heldName}» sekmesinde. Yeni fiş açıldı.",
                        $"Chek kutishga qo'yildi — u «{heldName}» yorlig'ida. Yangi chek ochildi.");
                    PushCustomerDisplay();
                    NotifyHeldReceiptsChanged();
                    RaiseCartCommands();
                }).ConfigureAwait(false);
                return;
            }

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
                // 2026-10-04: чек ушёл в «Отложенные» без клиента QR — вкладка теперь для нового покупателя.
                ForgetReceiptClient(_activeSessionId);
                PersistActiveSessionSnapshot();
                SyncLinesAndTotals();
                CartMessage = Tr.T($"Отложено: «{result.Label}». Текущий чек очищен.", $"Калтырылды: «{result.Label}». Учурдагы чек тазаланды.",
                    $"Held: “{result.Label}”. Current receipt cleared.", $"Beklemeye alındı: «{result.Label}». Mevcut fiş temizlendi.",
                    $"Kutishga qo'yildi: «{result.Label}». Joriy chek tozalandi.");
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
                    _prompts.ShowWarning(Tr.T("Нет отложенных чеков для возврата.", "Кайтаруу үчүн калтырылган чектер жок.", "No held receipts to restore.", "Geri getirilecek bekletilen fiş yok.", "Qayta ochish uchun kutishdagi cheklar yo'q."))).ConfigureAwait(false);
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
                // 2026-10-04: во вкладке теперь чек другого покупателя — клиента QR с неё снимаем.
                ForgetReceiptClient(_activeSessionId);

                SyncLinesAndTotals();
                CartMessage = currentHasItems
                    ? Tr.T("Чеки обменяны с последним отложенным.", "Чектер акыркы калтырылган менен алмаштырылды.", "Receipts swapped with the last held one.", "Mevcut fiş, son bekletilen fişle yer değiştirdi.", "Cheklar oxirgi kutishdagi chek bilan almashtirildi.")
                    : Tr.T($"Возвращён отложенный чек «{latest.Label}».", $"Калтырылган «{latest.Label}» чеги кайтарылды.",
                        $"Restored held receipt “{latest.Label}”.", $"Bekletilen fiş geri getirildi: «{latest.Label}».",
                        $"Kutishdagi chek qayta ochildi: «{latest.Label}».");
                NotifyHeldReceiptsChanged();
                RaiseCartCommands();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"RESTORE held failed: {ex}", "DEFER");
            await RunOnUiThreadAsync(() =>
                _prompts.ShowError(Tr.T("Не удалось вернуть отложенный чек.", "Калтырылган чекти кайтаруу мүмкүн болгон жок.", "Could not restore the held receipt.", "Bekletilen fiş geri getirilemedi.", "Kutishdagi chekni qayta ochib bo'lmadi."))).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    // 2026-10-22 (модалки, пакет 1, живой аудит): при одном чеке «Удалить чек» была серой — теперь она очищает
    // единственный чек (с подтверждением «Очистить чек»), если в нём есть товары.
    private bool CanDeleteActiveReceiptCore() => (_sessions.Count > 1 || HasItems) && !IsBusy;

    /// <summary>2026-10-18, владелец: «Удалить чек» (вкладку с товарами) — тот же личный код доступа CartDelete, что у
    /// «Очистить чек», если код задан в настройках (Сотрудники / сайт); без кода — как раньше, сразу.</summary>
    private async Task DeleteActiveReceiptAsync()
    {
        var active = GetActiveSession();
        if (active is null)
            return;
        // 2026-10-22 (модалки, пакет 1): единственный чек не удаляется, а очищается — тот же путь, что «Очистить чек»
        // (вопрос, право DeleteCartItem, код CartDelete, журнал удалённого).
        if (_sessions.Count <= 1)
        {
            await ClearCartAsync().ConfigureAwait(false);
            return;
        }
        string? confirmedBy = null;
        if (_sessions.Count > 1 && CurrentCartItems().Any())
        {
            // 2026-10-20 (живой аудит кассы, К11): вкладка с товарами удалялась одним нажатием (без кода CartDelete — вовсе без
            // вопроса). Теперь — как «Очистить корзину»: сначала вопрос, потом (если задан) код.
            var count = CurrentCartItems().Count();
            if (!await _prompts.ConfirmAsync(Tr.T(
                    $"Удалить «{active.BaseName}» вместе с товарами (позиций: {count})?",
                    $"«{active.BaseName}» товарлары менен өчүрүлсүнбү (позиция: {count})?",
                    $"Delete “{active.BaseName}” with its items ({count})?",
                    $"«{active.BaseName}» ürünleriyle birlikte silinsin mi ({count} kalem)?",
                    $"«{active.BaseName}» mahsulotlari bilan o'chirilsinmi (pozitsiyalar: {count})?"),
                    // 2026-10-22 (модалки, пакет 1): глагол и красная кнопка — удаление с товарами необратимо.
                    Tr.T("Удалить чек", "Чекти өчүрүү", "Delete receipt", "Fişi sil", "Chekni o'chirish"),
                    danger: true).ConfigureAwait(false))
                return;
            // 2026-10-18, ревью: как у «Очистить чек» (аудит 16.09) — без права удалять позиции вкладку с товарами не удалить.
            if (!DemandPermission(PosPermissions.DeleteCartItem))
                return;
            if (!await DemandEmployeeAccessCodeAsync(
                    NurMarketKassa.Services.EmployeeAccessGate.CartDelete,
                    Tr.T("Удаление чека", "Чекти өчүрүү", "Deleting the receipt", "Fiş silme", "Chekni o'chirish"),
                    Tr.T($"Введите свой код доступа, чтобы удалить «{active.BaseName}» с товарами.",
                        $"«{active.BaseName}» товарлары менен өчүрүү үчүн жеке кодуңузду киргизиңиз.",
                        $"Enter your access code to delete “{active.BaseName}” with its items.",
                        $"«{active.BaseName}» fişini ürünleriyle silmek için erişim kodunuzu girin.",
                        $"«{active.BaseName}»ni mahsulotlari bilan o'chirish uchun kirish kodingizni kiriting."))
                    .ConfigureAwait(false))
                return;
            confirmedBy = _lastAccessCodeOwner;
        }
        await _dispatcher.InvokeAsync(() => DeleteActiveReceipt(active.Id, confirmedBy));
    }

    private void DeleteActiveReceipt(string expectedSessionId, string? confirmedBy)
    {
        var active = GetActiveSession();
        if (active is null)
            return;
        // 2026-10-18: пока спрашивали код, активной стала другая вкладка — её не удаляем (и говорим об этом).
        if (!string.Equals(active.Id, expectedSessionId, StringComparison.Ordinal))
        {
            CartMessage = Tr.T("Чек не удалён — открыта другая вкладка. Откройте нужный чек и удалите снова.",
                "Чек өчүрүлгөн жок — башка өтмөк ачык. Керектүү чекти ачып, кайра өчүрүңүз.",
                "Receipt not deleted — another tab is open. Open the receipt and delete it again.",
                "Fiş silinmedi — başka bir sekme açık. Fişi açıp yeniden silin.",
                "Chek o'chirilmadi — boshqa varaq ochiq. Kerakli chekni ochib, qayta o'chiring.");
            return;
        }

        if (_sessions.Count <= 1)
        {
            _prompts.ShowWarning(Tr.T("«Основной чек» удалить нельзя.", "«Негизги чек» өчүрүлбөйт.", "The “Main receipt” cannot be deleted.", "«Ana fiş» silinemez.", "«Asosiy chek»ni o'chirib bo'lmaydi."));
            return;
        }

        var index = _sessions.FindIndex(s => s.Id == active.Id);
        if (index < 0)
            return;

        // 2026-10-17, этап 4: строки удаляемой вкладки (это текущий чек) — в журнал «Удалено из чека».
        LogCartRemovals(CurrentCartItems(), CartRemovalLogStore.KindReceipt, confirmedBy: confirmedBy);
        _sessions.RemoveAt(index);
        RenameReceiptSessions();
        var nextIndex = Math.Clamp(index - 1, 0, _sessions.Count - 1);
        var next = _sessions[nextIndex];
        _activeSessionId = next.Id;
        ActiveReceiptTitle = next.BaseName;
        ApplySessionToCart(next);
        SyncLinesAndTotals();
        CartMessage = Tr.T($"Чек удалён. Активен «{next.BaseName}».", $"Чек өчүрүлдү. «{next.BaseName}» активдүү.",
            $"Receipt deleted. Active: “{next.BaseName}”.", $"Fiş silindi. Etkin: «{next.BaseName}».", $"Chek o'chirildi. Faol: «{next.BaseName}».");
        RaiseCartCommands();
    }

    private void ClearReceipt()
    {
        _cart.ResetForNewReceipt();
        // 2026-10-04: чек очищен — и клиент QR с него снимается.
        ForgetReceiptClient(_activeSessionId);
        PersistActiveSessionSnapshot();
        SyncLinesAndTotals();
        CartMessage = "";
        RaiseCartCommands();
    }

    private async Task ClearCartAsync()
    {
        if (!HasItems || IsBusy)
            return;

        // 2026-10-22 (модалки, пакет 1): «Очистить чек» — красная, рядом «Отмена» (было «Продолжить»).
        if (!await _prompts.ConfirmAsync(Tr.T("Очистить текущий чек? Все товары будут убраны из чека.", "Учурдагы чекти тазалайсызбы? Бардык товарлар чектен алынат.",
                    "Clear the current receipt? All items will be removed.", "Mevcut fiş temizlensin mi? Tüm ürünler fişten çıkarılacak.",
                    "Joriy chekni tozalaysizmi? Barcha mahsulotlar chekdan olib tashlanadi."),
                Tr.T("Очистить чек", "Чекти тазалоо", "Clear receipt", "Fişi temizle", "Chekni tozalash"),
                danger: true).ConfigureAwait(false))
            return;

        // 2026-09-16, живой баг аудита ("обход PIN на очистку корзины") — удаление ОДНОЙ позиции
        // (RemoveLineAsync ниже) требует код доступа CartDelete, а полная очистка корзины раньше
        // спрашивала только "точно?" без кода — кассир без права на удаление мог обнулить всю
        // корзину в обход защиты, хотя это как минимум не менее разрушительное действие.
        if (!DemandPermission(PosPermissions.DeleteCartItem))
            return;
        if (!await DemandEmployeeAccessCodeAsync(
                NurMarketKassa.Services.EmployeeAccessGate.CartDelete,
                Tr.T("Очистка корзины", "Себетти тазалоо", "Clearing the cart", "Sepet temizleme", "Savatni tozalash"),
                Tr.T("Введите свой код доступа, чтобы очистить корзину.",
                    "Себетти тазалоо үчүн жеке кодуңузду киргизиңиз.",
                    "Enter your access code to clear the cart.",
                    "Sepeti temizlemek için erişim kodunuzu girin.",
                    "Savatni tozalash uchun kirish kodingizni kiriting."))
                .ConfigureAwait(false))
            return;

        // 2026-10-17, этап 4: кто подтвердил кодом — снимаем сразу, до следующего вопроса о коде.
        var confirmedBy = _lastAccessCodeOwner;
        await _dispatcher.InvokeAsync(() =>
        {
            // 2026-10-17, этап 4: каждая строка очищенного чека — в журнал «Удалено из чека» (одно действие — один BatchId).
            LogCartRemovals(CurrentCartItems(), CartRemovalLogStore.KindClear, confirmedBy: confirmedBy);
            ClearReceipt();
        }).ConfigureAwait(false);
        // 2026-10-23 (аудит П-9): вместо окна «Сообщение» — лёгкая плашка под шапкой; окно — только если плашка недоступна.
        var clearedText = Tr.T("Корзина очищена.", "Себет тазаланды.", "Cart cleared.", "Sepet temizlendi.", "Savat tozalandi.");
        if (!MainStatusViewModel.TryShowInfoToast(clearedText))
            _prompts.ShowToast(clearedText);
    }

    private async Task RemoveLineAsync(CartLineItemVm? line)
    {
        if (line is null || string.IsNullOrWhiteSpace(line.ItemId))
            return;
        if (!DemandPermission(PosPermissions.DeleteCartItem))
            return;

        // 2026-10-17, этап 4 («Контроль удалений», макет MCancelled): при включённой настройке и строке от порога —
        // окно «Убрать из чека?» с причиной; «Оставить» (Esc) — строка остаётся. По умолчанию выключено.
        CartRemovalReasonAnswer? reason = null;
        if (NeedsRemovalReason(line))
        {
            var request = new CartRemovalReasonRequest(line.Title, line.Quantity, line.Unit, line.LineTotal,
                CanRemoveOne: !line.IsWeight && line.Quantity > 1 + 1e-6 && FractionalRule(line) == null); // 2026-10-26 (ХМ2): «Только 1» не для метров
            reason = await AskRemovalReason!(request).ConfigureAwait(true);
            if (reason is null || !IsLineInCart(line))
                return;
        }

        if (!await DemandEmployeeAccessCodeAsync(
                NurMarketKassa.Services.EmployeeAccessGate.CartDelete,
                Tr.T("Удаление товара", "Товарды өчүрүү", "Removing an item", "Ürün silme", "Mahsulotni o'chirish"),
                Tr.T($"Введите свой код доступа, чтобы удалить «{line.Title}» из чека.",
                    $"«{line.Title}» товарын чектен өчүрүү үчүн жеке кодуңузду киргизиңиз.",
                    $"Enter your access code to remove \"{line.Title}\" from the receipt.",
                    $"«{line.Title}» ürününü fişten silmek için erişim kodunuzu girin.",
                    $"«{line.Title}» mahsulotini chekdan o'chirish uchun kirish kodingizni kiriting."))
                .ConfigureAwait(false))
            return;

        // 2026-10-17, этап 4: снимок строки до удаления — для журнала «Удалено из чека».
        var removed = CurrentCartItems().FirstOrDefault(item => string.Equals(item.Id, line.ItemId, StringComparison.Ordinal));
        if (reason is { OnlyOne: true } && removed is { MustWeigh: false, Quantity: > 1 })
        {
            // «Только 1 шт» — то же, что «−» у строки (DecreaseQuantity): количество на одну меньше.
            // 2026-10-17, ревью этапа 4: в журнал — на сколько реально уменьшилась сумма строки (при скидке строки
            // суммой это не средняя цена со скидкой).
            var totalBefore = (double)removed.LineTotal;
            _cart.UpdateQuantity(line.ItemId, Math.Round(removed.Quantity - 1, 0));
            var after = CurrentCartItems().FirstOrDefault(item => string.Equals(item.Id, line.ItemId, StringComparison.Ordinal));
            double? oneTotal = after is null ? null : Math.Round(totalBefore - (double)after.LineTotal, 2);
            LogCartRemovals(new[] { removed }, CartRemovalLogStore.KindOne, reason, _lastAccessCodeOwner, onlyOne: true,
                oneLineTotal: oneTotal);
            RefreshChangedLine(line);
            return;
        }

        _cart.RemoveItem(line.ItemId);
        if (removed is not null)
            LogCartRemovals(new[] { removed }, CartRemovalLogStore.KindLine, reason, _lastAccessCodeOwner);
        SyncLinesAndTotals();
        RaiseCartCommands();
    }

    /// <summary>2026-10-17, этап 4: спрашивать ли причину удаления этой строки. Понятия «пречек» (печать чека до
    /// оплаты) в кассе нет, поэтому условие — только сумма строки от порога.</summary>
    private bool NeedsRemovalReason(CartLineItemVm line)
    {
        var prefs = UserPreferences.Instance;
        return AskRemovalReason != null
               && prefs.AskCartRemovalReason
               && line.LineTotal + 0.005 >= Math.Max(0, prefs.CartRemovalReasonThreshold);
    }

    /// <summary>2026-10-17, этап 4: строки текущего чека (снимок) — пустой список, если корзины нет.</summary>
    private IReadOnlyList<CartItem> CurrentCartItems()
    {
        try
        {
            return _cart.HasCart ? _cart.Items.ToList() : Array.Empty<CartItem>();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал удалений: строки чека не прочитаны: {ex.Message}", "WARNING");
            return Array.Empty<CartItem>();
        }
    }

    /// <summary>2026-10-17, этап 4 («Контроль удалений»): убранные из текущего чека строки — в журнал
    /// «Удалено из чека» (CartRemovalLogStore, запись в фоне). Ошибка здесь удаление не отменяет.</summary>
    private void LogCartRemovals(IReadOnlyList<CartItem> items, string kind, CartRemovalReasonAnswer? reason = null,
        string? confirmedBy = null, bool onlyOne = false, string? shiftId = null, double? oneLineTotal = null)
    {
        try
        {
            if (items.Count == 0)
                return;
            var at = DateTimeOffset.Now;
            var batchId = items.Count > 1 ? Guid.NewGuid().ToString("N") : null;
            var receipt = GetActiveSession()?.BaseName;
            var records = items.Select(item =>
            {
                var quantity = onlyOne ? 1 : item.Quantity;
                var lineTotal = onlyOne && oneLineTotal is { } one && one >= 0
                    ? one
                    : onlyOne && item.Quantity > 0
                    ? Math.Round((double)item.LineTotal / item.Quantity, 2)
                    : (double)item.LineTotal;
                return new CartRemovalRecord
                {
                    At = at,
                    Kind = kind,
                    BatchId = batchId,
                    ShiftId = shiftId ?? PosApp.ActiveShiftId,
                    CashboxId = PosApp.PosCashboxId,
                    CompanyId = CompanyInfoService.LastCompany?.Id,
                    // 2026-10-23 (аудит, круг 8, В-1): «??» не ловит пустую строку — берём первое непустое.
                    Cashier = !string.IsNullOrWhiteSpace(PosApp.CurrentUserDisplayName) ? PosApp.CurrentUserDisplayName!
                        : !string.IsNullOrWhiteSpace(PosApp.CurrentUserId) ? PosApp.CurrentUserId! : "",
                    CartId = _cart.CartId,
                    Receipt = receipt,
                    ProductId = item.ProductId,
                    Title = item.Name,
                    Quantity = quantity,
                    Unit = Lines.FirstOrDefault(l => string.Equals(l.ItemId, item.Id, StringComparison.Ordinal))?.Unit,
                    UnitPrice = (double)item.UnitPrice,
                    LineTotal = lineTotal,
                    Reason = reason?.Reason,
                    Note = string.IsNullOrWhiteSpace(reason?.Note) ? null : reason!.Note!.Trim(),
                    ConfirmedBy = confirmedBy,
                };
            }).ToList();
            CartRemovalLogStore.Append(records);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал удалений: запись не поставлена: {ex.Message}", "WARNING");
        }
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

        var rule = FractionalRule(line); // 2026-10-26 (ХМ2): метры/м²/литры — шаг единицы, не 1
        var step = line.IsWeight ? 0.1 : rule != null ? (double)rule.Step : 1;
        var next = rule != null ? QuantityRules.Normalize(line.Quantity + step, rule) : Math.Round(line.Quantity + step, line.IsWeight ? 3 : 0);
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
        // 2026-10-04, ТЗ 1.17.48 P0-1: окно вопроса забирало фокус у поля количества, LostFocus применял то же
        // число ещё раз и открывал второе окно, третье… — «Нет» и Esc будто не работали, выйти можно было только
        // «Да» (менял склад). Теперь один вопрос на строку за раз. P0-5: строки уже нет в чеке (чек оплачен,
        // строка удалена) или идёт оплата — не спрашиваем, поле возвращаем к количеству строки.
        if (_paymentInProgress || !IsLineInCart(line) || !_stockOffersOpen.TryAdd(line.ItemId, 0))
        {
            if (!_stockOffersOpen.ContainsKey(line.ItemId))
                ResetQuantityInput(line);
            return;
        }

        try
        {
            await OfferReplenishThenSetQuantityCoreAsync(line, desired, limit).ConfigureAwait(false);
        }
        finally
        {
            _stockOffersOpen.TryRemove(line.ItemId, out _);
        }
    }

    /// <summary>2026-10-04, ТЗ 1.17.48 P0-1/P0-5: вопрос «пополнить склад?» по строке уже открыт (ключ — id строки).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _stockOffersOpen = new(StringComparer.Ordinal);

    /// <summary>2026-10-04, ТЗ P0-5: идёт оплата — поздние правки количества старых строк не спрашивают про склад.</summary>
    private volatile bool _paymentInProgress;

    private bool IsLineInCart(CartLineItemVm line) =>
        !string.IsNullOrEmpty(line.ItemId)
        && _cart.Items.Any(item => string.Equals(item.Id, line.ItemId, StringComparison.Ordinal));

    /// <summary>Поле количества — обратно к фактическому количеству строки (отказ, ошибка пополнения).</summary>
    private static void ResetQuantityInput(CartLineItemVm line) => line.QuantityInput = line.QuantityDisplay;

    private async Task OfferReplenishThenSetQuantityCoreAsync(CartLineItemVm line, double desired, double limit)
    {
        var formattedLimit = limit.ToString(line.IsWeight ? "0.###" : "0", CultureInfo.InvariantCulture);
        CartMessage = Tr.T($"Достигнут лимит остатка: {formattedLimit} {line.Unit}.", $"Калдык чегине жетти: {formattedLimit} {line.Unit}.",
            $"Stock limit reached: {formattedLimit} {line.Unit}.", $"Stok sınırına ulaşıldı: {formattedLimit} {line.Unit}.",
            $"Qoldiq chegarasiga yetildi: {formattedLimit} {line.Unit}.");

        if (_replenishStock == null || string.IsNullOrWhiteSpace(line.ProductId))
        {
            ResetQuantityInput(line);
            _prompts.ShowWarning(CartMessage);
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(Tr.T(
            $"На складе только {formattedLimit} {line.Unit}. Продолжить и пополнить склад?",
            $"Кампада болгону {formattedLimit} {line.Unit} бар. Улантып, кампаны толуктайсызбы?",
            $"Only {formattedLimit} {line.Unit} in stock. Continue and restock?",
            $"Stokta yalnızca {formattedLimit} {line.Unit} var. Devam edip depoya stok eklemek ister misiniz?",
            $"Omborda faqat {formattedLimit} {line.Unit} bor. Davom etib, omborni to'ldirasizmi?"),
            // 2026-10-22 (модалки, пакет 1): глагол вместо «Продолжить»; «Отмена» (Esc) — как прежнее «Нет».
            Tr.T("Пополнить склад", "Кампаны толуктоо", "Restock", "Stok ekle", "Omborni to'ldirish"))
            .ConfigureAwait(false);
        // 2026-10-04, ТЗ P0-1/P0-5: «Нет» (Esc) — поле к количеству строки, склад не трогаем; строки уже нет
        // в чеке (оплатили, пока висел вопрос) — тоже ничего не делаем.
        var stillInCart = false;
        await RunOnUiThreadAsync(() =>
        {
            stillInCart = IsLineInCart(line);
            if (!confirmed || !stillInCart || _paymentInProgress)
                ResetQuantityInput(line);
        }).ConfigureAwait(false);
        if (!confirmed || !stillInCart || _paymentInProgress)
            return;

        if (!await _replenishStock(line.ProductId!, desired, line.IsWeight).ConfigureAwait(false))
        {
            await RunOnUiThreadAsync(() => ResetQuantityInput(line)).ConfigureAwait(false);
            return;
        }

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

        var rule = FractionalRule(line!); // 2026-10-26 (ХМ2)
        var minimum = line!.IsWeight ? 0.1 : rule != null ? (double)rule.Min : 1;
        if (line.Quantity <= minimum + 1e-6)
        {
            CartMessage = rule != null
                ? QuantityRules.MinimumMessage(rule, line.Unit)
                : line.IsWeight
                ? Tr.T("Минимальный вес — 0,1 кг.", "Минималдуу салмак — 0,1 кг.", "Minimum weight is 0.1 kg.", "Minimum ağırlık 0,1 kg'dır.", "Minimal og'irlik — 0,1 kg.")
                : Tr.T("Меньше 1 нельзя.", "1ден аз болушу мүмкүн эмес.", "Cannot be less than 1.", "1'den az olamaz.", "1 dan kam bo'lishi mumkin emas.");
            return;
        }

        var step = line.IsWeight ? 0.1 : rule != null ? (double)rule.Step : 1;
        var next = rule != null ? QuantityRules.Normalize(line.Quantity - step, rule) : Math.Round(line.Quantity - step, line.IsWeight ? 3 : 0);
        if (next < minimum)
            next = minimum;

        _cart.UpdateQuantity(line.ItemId, next);
        RefreshChangedLine(line);
    }

    private void SetLineQuantity(CartLineItemVm? line)
    {
        if (!CanChangeLineQuantity(line))
            return;
        // 2026-10-04, ТЗ P0-5: поле старой строки теряет фокус уже после оплаты — строки в чеке нет, применять нечего.
        if (!IsLineInCart(line!))
            return;

        var rule = FractionalRule(line!); // 2026-10-26 (ХМ2)
        var minimum = line!.IsWeight ? 0.1 : rule != null ? (double)rule.Min : 1;
        if (!double.TryParse(line.QuantityInput.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var typed)
            || typed <= 0)
        {
            // Не удалось разобрать ввод — возвращаем поле к актуальному количеству.
            line.QuantityInput = line.QuantityDisplay;
            return;
        }

        if (!string.IsNullOrWhiteSpace(line.ProductId) && _stockLimitLookup != null)
            line.MaxStockQuantity = _stockLimitLookup(line.ProductId, line.SalePackageId);

        var next = rule != null ? QuantityRules.Normalize(typed, rule) : Math.Round(typed, line.IsWeight ? 3 : 0);
        if (rule != null && typed < minimum - 1e-6)
            CartMessage = QuantityRules.MinimumMessage(rule, line.Unit);
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

        // 2026-09-28, проверено на сервере (продажи 1143, 1144): у товара с акцией NurCRM сервер
        // сам назначает скидку строки по акции, а скидку кассира на эту строку не принимает —
        // касса показала бы одну сумму, а продажа прошла бы на другую. Предлагаем скидку на чек.
        if (ReceiptSnapshotCartEditor.LineHasPromotion(_cart, line.ItemId))
        {
            _prompts.ShowWarning(Tr.T(
                $"На «{line.Title}» действует акция NurCRM — скидку этой строки назначает сервер по акции, свою скидку на неё поставить нельзя. Используйте скидку на весь чек.",
                $"«{line.Title}» товарына NurCRM акциясы колдонулат — бул саптын арзандатуусун сервер акция боюнча коёт, өз арзандатууңузду коюуга болбойт. Бүт чекке арзандатууну колдонуңуз.",
                $"A NurCRM promotion applies to “{line.Title}” — the server sets this line's discount from the promotion, so you can't add your own. Use a discount on the whole receipt.",
                $"«{line.Title}» için NurCRM kampanyası geçerli — bu satırın indirimini sunucu kampanyaya göre belirler, kendi indiriminizi ekleyemezsiniz. Tüm fişe indirim uygulayın.",
                $"«{line.Title}» uchun NurCRM aksiyasi amal qiladi — bu qator chegirmasini server aksiya bo'yicha belgilaydi, o'z chegirmangizni qo'yib bo'lmaydi. Butun chekka chegirma qo'llang."));
            return;
        }

        if (!await DemandCashierPasswordAsync(
                Tr.T("Изменение позиции", "Позицияны өзгөртүү", "Editing line item", "Kalem düzenleme", "Pozitsiyani o'zgartirish"),
                Tr.T($"Введите пароль кассы, чтобы изменить «{line.Title}» в чеке.",
                    $"«{line.Title}» товарын чекте өзгөртүү үчүн касса сырсөзүн киргизиңиз.", $"Enter the till password to change “{line.Title}” in the receipt.", $"«{line.Title}» ürününü fişte değiştirmek için kasa şifresini girin.", $"Chekdagi «{line.Title}» pozitsiyasini o'zgartirish uchun kassa parolini kiriting."))
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
        // 2026-10-17, этап 4: чей код подошёл — для журнала «Удалено из чека» (null — код не спрашивали).
        _lastAccessCodeOwner = null;
        if (!NurMarketKassa.Services.EmployeeAccessGate.IsActiveFor(action, _permissions))
            return true;

        return await _prompts.ConfirmWithCodeAsync(title, message,
            entered =>
            {
                // 2026-10-18, ревью: проверка и «чей код» — один расчёт PBKDF2.
                if (!NurMarketKassa.Services.EmployeeAccessGate.TryValidate(action, entered, out var matchedBy))
                    return false;
                _lastAccessCodeOwner = matchedBy;
                return true;
            }).ConfigureAwait(false);
    }

    /// <summary>2026-10-17, этап 4: последний подошедший код доступа (имя сотрудника или «код с сайта»).</summary>
    private string? _lastAccessCodeOwner;

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
        // Отложенный чек без товаров (например, после восстановления состояния) — обычная вкладка.
        // Активную вкладку не проверяем: отложенный чек перестаёт быть отложенным, когда его
        // открывают (SelectReceiptTab), а пока кассир переключает вкладки, строки ещё старые.
        var cleared = false;
        foreach (var session in _sessions.Where(s => s.DeferredAt != null && s.Id != _activeSessionId))
        {
            if (GetSessionSummary(session.CartJson).Lines == 0)
            {
                session.DeferredAt = null;
                cleared = true;
            }
        }
        if (cleared)
            RenameReceiptSessions();

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
            _sessions[index].BaseName = _sessions[index].DeferredAt is { } heldAt
                ? Tr.T($"Отложен {heldAt:HH:mm}", $"Калтырылган {heldAt:HH:mm}", $"Held {heldAt:HH:mm}", $"Bekleyen {heldAt:HH:mm}", $"Kutishda {heldAt:HH:mm}")
                : index == 0
                    ? Tr.T("Основной чек", "Негизги чек", "Main receipt", "Ana fiş", "Asosiy chek")
                    : Tr.T($"Чек {index + 1}", $"Чек {index + 1}", $"Receipt {index + 1}", $"Fiş {index + 1}", $"Chek {index + 1}");

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
        if (index < 0)
            return;

        var wasStillActive = _activeSessionId == paidId;

        // 2026-09-27, «отложенный чек не удаляется после оплаты»: кнопка «Отложить чек» оставляет
        // отложенный чек вкладкой и открывает новую. Правила после оплаты:
        //  • отложенный чек касса сама не открывает — его покупатель ещё не вернулся;
        //  • две пустые вкладки ни к чему: если рядом есть пустая обычная вкладка, оплаченная
        //    убирается (так и для «Основного чека», который оплатили, вернувшись к отложенному);
        //  • если кроме оплаченной остались только отложенные — оплаченная (уже пустая) остаётся
        //    новым чеком для следующего покупателя.
        var emptyOther = _sessions.FirstOrDefault(s => s.Id != paidId && s.DeferredAt == null && IsEmptySession(s));
        OpenReceiptSession? target;
        if (index == 0)
        {
            if (emptyOther is null || !wasStillActive)
                return;
            target = emptyOther;
        }
        else
        {
            target = (!string.IsNullOrEmpty(previousId) && previousId != paidId
                ? _sessions.FirstOrDefault(s => s.Id == previousId)
                : null) ?? _sessions[0];
            if (target.DeferredAt != null)
            {
                target = emptyOther;
                if (target is null && wasStillActive)
                    return;
            }
        }

        _sessions.RemoveAt(index);
        RenameReceiptSessions();
        RebuildReceiptTabs();

        if (!wasStillActive || target is null)
            return;

        _activeSessionId = target.Id;
        ActiveReceiptTitle = target.BaseName;
        ApplySessionToCart(target);
        SyncLinesAndTotals();
        RaiseCartCommands();
    }

    private bool IsEmptySession(OpenReceiptSession session) =>
        session.Id == _activeSessionId ? LineCount == 0 : GetSessionSummary(session.CartJson).Lines == 0;

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
            "Ачык чектердин саны чегине жетти (эң көбү 10). Учурдагы чектерди аяктаңыз же өчүрүңүз.", "Open receipt limit reached (maximum 10). Complete or delete existing receipts.", "Açık fiş sınırına ulaşıldı (en fazla 10). Mevcut fişleri tamamlayın veya silin.", "Ochiq cheklar chegarasiga yetildi (ko'pi bilan 10 ta). Mavjud cheklarni yakunlang yoki o'chiring.");
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

    // 2026-10-19, К1 (аудит 19.10, проблема 8: «Основной чек • 6 тов. • 1517....» обрезалось): вкладка — «Основной чек ·
    // 1 517,96» (MoneyText, без «сом» и без числа позиций — оно видно в самом чеке), чтобы сумма была видна целиком во
    // всех раскладках и на 1024. lineCount оставлен в подписи метода — его передают прежние вызовы.
    private static string FormatTabTitle(string baseName, int lineCount, double total) =>
        $"{baseName} · {MoneyText.Amount(total)}";

    private void EnsureCartInitialized()
    {
        if (_cart.HasCart)
            return;

        _cart.ResetForNewReceipt();
    }

    /// <summary>Единица штучной строки чека — из карточки товара. 2026-09-24: раньше всегда
    /// «шт», и кабель, проданный метрами, печатался как «1 шт».</summary>
    private string LineUnit(string? productId, string? barcode)
    {
        var tile = string.IsNullOrWhiteSpace(productId)
            ? null
            : LocalProductRepository.Instance.TryGetTileById(productId);
        if (tile is null && !string.IsNullOrWhiteSpace(barcode))
            tile = _catalogLookup?.Invoke(barcode);

        var unit = tile?.Unit?.Trim();
        // «шт» — служебное значение кассы (см. ProductUnitNormalizer), на экран — на языке интерфейса;
        // единицы с сервера («м», «л», «уп») показываются как есть.
        return string.IsNullOrEmpty(unit) || unit is "кг" or "kg" or "шт"
            ? Tr.T("шт", "даана", "pcs", "adet", "dona")
            : unit;
    }

    /// <summary>2026-10-20, живая проверка (большой чек медленный): строки и итог одним вызовом. Строки больше не
    /// сохраняют снимок, не перестраивают вкладки и не обновляют экран покупателя — сразу за ними это делает
    /// UpdateCartTotals уже с новым итогом (раньше всё делалось дважды, первый раз — со старым итогом).</summary>
    private void SyncLinesAndTotals()
    {
        _totalsFollowLines = true;
        try
        {
            SyncLinesFromCart();
        }
        finally
        {
            _totalsFollowLines = false;
        }
        UpdateCartTotals();
    }

    private bool _totalsFollowLines;

    private void SyncLinesFromCart()
    {
        _suppressTabRebuild = true;
        try
        {
            // 2026-10-20, живая проверка: строки стирались и создавались заново на каждый скан (при 275 строках —
            // сотни событий списка и пересоздание всех строк на экране, ~1 с до нового итога). Теперь новые строки
            // собираются отдельно, а в Lines меняется только то, что отличается (ObservableListPatch): содержимое
            // то же, что раньше. Лимиты остатка в пакете LookupBatch считаются один раз на товар.
            using var lookups = LookupBatch.Begin();
            var fresh = new List<CartLineItemVm>();
            var itemNumber = 1;
            // 2026-10-03: строки по оптовой цене (is_wholesale в строке чека).
            var wholesaleIds = new HashSet<string>(StringComparer.Ordinal);
            if (_cart.HasCart && _cart.Root.ValueKind == JsonValueKind.Object)
                foreach (var it in CartDisplayHelper.EnumerateItems(_cart.Root))
                    if (it.TryGetProperty("is_wholesale", out var w) && w.ValueKind == JsonValueKind.True && CartDisplayHelper.TryItemId(it) is { } wid)
                        wholesaleIds.Add(wid);
            foreach (var item in _cart.Items)
            {
                // Insert at the top: the cashier scans items in sequence and wants the most
                // recently added one visible without scrolling, not buried at the bottom.
                // ItemNumber still follows scan order (#1 = first item), only the row position flips.
                fresh.Insert(0, new CartLineItemVm
                {
                    ItemNumber = Math.Min(itemNumber++, 99999),
                    ItemId = item.Id ?? "",
                    ProductId = item.ProductId ?? "",
                    Barcode = item.Barcode ?? "",
                    Code = string.IsNullOrWhiteSpace(item.Barcode)
                        ? ""
                        : _catalogLookup?.Invoke(item.Barcode)?.Article ?? "",
                    Title = item.Name,
                    Unit = item.MustWeigh ? Tr.T("кг", "кг", "kg", "kg", "kg") : LineUnit(item.ProductId, item.Barcode),
                    UnitPrice = (double)item.UnitPrice,
                    IsWeight = item.MustWeigh,
                    // 2026-10-26 (ХМ2): шаг дробной единицы «Хозтоваров» для кнопки «+» (null — как раньше, 1 или 0.1 кг).
                    FractionalStep = QuantityRules.Active && !item.MustWeigh && string.IsNullOrEmpty(item.SalePackageId)
                        ? (double?)QuantityRules.ForUnit(LineUnit(item.ProductId, item.Barcode))?.Step
                        : null,
                    SalePackageId = item.SalePackageId,
                    MaxStockQuantity = string.IsNullOrWhiteSpace(item.ProductId)
                        ? null
                        : _stockLimitLookup?.Invoke(item.ProductId, item.SalePackageId),
                    Quantity = item.Quantity,
                    LineTotal = (double)item.LineTotal,
                    DiscountAmount = (double)item.LineDiscount,
                    DiscountPercent = item.DiscountPercent is { } percent ? (double)percent : null,
                    FixedDiscountAmount = item.FixedDiscountAmount is { } fixedAmount ? (double)fixedAmount : null,
                    PromoBasePrice = item.PromoBasePrice,
                    IsWholesale = wholesaleIds.Contains(item.Id ?? ""),
                    WholesaleCommand = WholesaleLineCommand,
                    RemoveCommand = RemoveLineCommand,
                    IncreaseCommand = IncreaseQuantityCommand,
                    DecreaseCommand = DecreaseQuantityCommand,
                    WeighCommand = WeighLineCommand,
                    VariantId = item.VariantId,
                    ChangeVariantCommand = ChangeVariantLineCommand,
                    DiscountCommand = LineDiscountCommand,
                    SetQuantityCommand = SetQuantityCommand,
                });
            }
            ObservableListPatch.Apply(Lines, fresh, SameLine);
        }
        finally
        {
            _suppressTabRebuild = false;
        }

        UpdateLossMarks();
        ApplyWholesaleModeToNewLines();
        RaiseCartCommands();
        if (_totalsFollowLines)
            return;
        PersistActiveSessionSnapshot();
        RebuildReceiptTabs();
        PushCustomerDisplay();
    }

    /// <summary>2026-10-20: строка чека не изменилась — все данные, из которых она собрана, те же (в списке остаётся
    /// старый объект). Убыток/опт строки пересчитывает UpdateLossMarks после каждой перестройки.</summary>
    private static bool SameLine(CartLineItemVm a, CartLineItemVm b) =>
        a.ItemNumber == b.ItemNumber
        && a.ItemId == b.ItemId
        && a.ProductId == b.ProductId
        && a.Barcode == b.Barcode
        && a.Code == b.Code
        && a.Title == b.Title
        && a.Unit == b.Unit
        && a.UnitPrice.Equals(b.UnitPrice)
        && a.IsWeight == b.IsWeight
        && a.SalePackageId == b.SalePackageId
        && Nullable.Equals(a.MaxStockQuantity, b.MaxStockQuantity)
        && a.Quantity.Equals(b.Quantity)
        && a.LineTotal.Equals(b.LineTotal)
        && a.DiscountAmount.Equals(b.DiscountAmount)
        && Nullable.Equals(a.DiscountPercent, b.DiscountPercent)
        && Nullable.Equals(a.FixedDiscountAmount, b.FixedDiscountAmount)
        && Nullable.Equals(a.PromoBasePrice, b.PromoBasePrice)
        && a.IsWholesale == b.IsWholesale
        && a.VariantId == b.VariantId
        && a.QuantityInput == b.QuantityInput;

    private void UpdateCartTotals()
    {
        if (!_cart.HasCart)
        {
            Subtotal = 0;
            Discount = 0;
            OrderDiscountAmount = 0;
            Total = 0;
            LineCount = 0;
            TotalQuantity = 0;
            PersistActiveSessionSnapshot();
            RebuildReceiptTabs();
            PushCustomerDisplay();
            return;
        }

        SyncOrderDiscountFromCart();
        UpdateLossMarks();
        var totals = CartTotalsCalculator.Calculate(_cart.Root);
        Subtotal = totals.Subtotal;
        Discount = totals.LineDiscounts + totals.OrderDiscount;
        OrderDiscountAmount = totals.OrderDiscount;
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
        // 2026-10-26: итог оплаты теперь держится дольше (настройка «Очистить экран через»), а следующий покупатель может начать
        // сканировать раньше — как только в чеке появились товары, итог прошлой оплаты убираем сразу.
        if (_customerDisplayShowsPayment && Lines.Count > 0)
        {
            ForgetPaymentOnCustomerDisplay();
            _customerDisplay.SetPaymentStatus(CustomerDisplayPaymentStatus.Idle);
        }

        _customerDisplay.UpdateCart(new CustomerDisplayCartSnapshot
        {
            Lines = Lines.Select(line => new CustomerDisplayLine
            {
                Title = line.Title,
                Barcode = line.Barcode,
                ImageUrl = CustomerDisplayImageUrl(line.ProductId),
                Quantity = line.Quantity,
                Unit = line.Unit,
                LineTotal = line.LineTotal,
            }).ToList(),
            Subtotal = Subtotal,
            Discount = Discount,
            Total = Total,
            CashReceived = _lastCashReceivedForDisplay,
            ChangeDue = _lastChangeDueForDisplay,
            PaidAmount = _lastPaidAmountForDisplay,
        });

        // Отдельный дисплей цены на COM-порту (2026-09-04, не второй монитор) — тот же хук,
        // что обновляет второй монитор покупателя, чтобы оба всегда показывали одну сумму.
        PoleDisplayService.Instance.ShowTotal(Total, Lines.Count);
    }

    /// <summary>Фото позиции для экрана покупателя (2026-09-28, виды «Карточки» и «Профи» показывают
    /// позиции с фото): локальный файл, если каталог его уже скачал, иначе адрес картинки — экран
    /// сам найдёт её в кэше миниатюр. Ошибка здесь не должна мешать продаже — тогда просто без фото.</summary>
    private static string? CustomerDisplayImageUrl(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        try
        {
            var tile = LocalProductRepository.Instance.TryGetTileById(productId);
            return string.IsNullOrWhiteSpace(tile?.ProductImagePath) ? tile?.ImageUrl : tile!.ProductImagePath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <param name="rule">2026-10-26 (ХМ2): правило дробной единицы «Хозтоваров» (м, м², л) — шаг и минимум вместо целого; null — как раньше.</param>
    private static double ParseQuantity(string? raw, bool mustWeigh, QuantityRule? rule = null)
    {
        if (!double.TryParse(raw?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var qty)
            || qty <= 0)
            qty = 1;

        if (!mustWeigh && rule != null)
            return QuantityRules.Normalize(qty, rule);
        return mustWeigh ? Math.Round(qty, 3) : Math.Round(qty, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>2026-10-26 (ХМ2): правило дробного количества для строки чека; null — весовая, поштучная из упаковки,
    /// не «Хозтовары» или единица целая (всё как раньше).</summary>
    private static QuantityRule? FractionalRule(CartLineItemVm line) =>
        line.IsWeight || !string.IsNullOrEmpty(line.SalePackageId) ? null : QuantityRules.ForUnit(line.Unit);

    private void RaiseCartCommands()
    {
        NotifyLineState();
        (PayCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeferCartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenDeferredCartsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ApplyOrderDiscountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (ReturnPreviousReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeleteReceiptCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
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
            SyncLinesAndTotals();
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

    // ── Умная допродажа (2026-10-01) ─────────────────────────────────────────────────────────
    // Одна подсказка над итогом чека: товар, который часто покупают вместе с товарами чека
    // (UpsellService, считает по истории чеков на этой кассе). «Добавить» идёт тем же путём, что
    // нажатие на товар в каталоге (размер/цвет, пачка/штука, весы, остаток). «Пропустить» — этот
    // товар в этом чеке больше не предлагается. Показы и ответы пишутся в UpsellEvents.

    private UpsellSuggestion? _upsell;
    private int _upsellVersion;
    private string _upsellCartKey = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> _upsellSkipped = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _upsellShown = new(StringComparer.OrdinalIgnoreCase);

    public ICommand AcceptUpsellCommand { get; private set; } = null!;
    public ICommand SkipUpsellCommand { get; private set; } = null!;

    public bool HasUpsell => _upsell != null;

    /// <summary>2026-10-02, владелец: «прокат — в кассе тоже внутри добавь». Кнопка «Прокат» в меню
    /// «Ещё» у чека — в сферах «Одежда» и «Услуги».</summary>
    // 2026-10-20: прокат во всех сферах (было «Одежда»/«Услуги»); скрыт в «Показывать разделы» — пункта нет.
    public bool ShowRental => MarketSpheres.HasRentals && !SectionVisibility.IsHidden("kassa.rental");

    /// <summary>2026-10-02: «Оформить как прокат» из окна оплаты — главное окно открывает «Новый прокат» с
    /// вещами чека и клиентом; возвращает созданный прокат и стоимость проката (null — отменили).</summary>
    public Func<IReadOnlyList<RentalItem>, string?, string?, Task<(RentalDto Rental, double Total)?>>? RentalFromCart { get; set; }

    /// <summary>Вещи чека → «Новый прокат»; после оформления товары заменяются строкой
    /// «Прокат №N: вещи, до ДД.ММ, залог …» на стоимость проката и снова открывается оплата. На бумажном
    /// чеке — номер проката и сумма; по этому номеру прокат закрывают при возврате.</summary>
    private async Task ConvertCartToRentalAsync(string? clientId, string? clientName)
    {
        if (RentalFromCart is null)
            return;
        var items = new List<RentalItem>();
        var customs = new List<(string Name, double Price, double Qty)>();
        foreach (var it in CartDisplayHelper.EnumerateItems(_cart.Root))
        {
            var qty = CartDisplayHelper.LineQuantity(it);
            if (CartDisplayHelper.IsCustomLine(it))
            {
                customs.Add((CartDisplayHelper.ItemName(it), CartDisplayHelper.UnitPrice(it), qty));
                continue;
            }
            var productId = CartDisplayHelper.TryProductId(it);
            if (string.IsNullOrWhiteSpace(productId))
                continue;
            string? Field(string name) => it.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var title = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase))?.Title
                        ?? CartDisplayHelper.ItemName(it);
            items.Add(new RentalItem(productId, CartDisplayHelper.ServerVariantId(it), title, Field("variant_size") ?? "", Field("variant_color") ?? "", qty));
        }
        if (items.Count == 0)
        {
            CartMessage = Tr.T("В чеке нет товаров для проката.", "Чекте прокат үчүн товар жок.", "No products in the receipt to rent.", "Fişte kiralanacak ürün yok.", "Chekda prokat uchun mahsulot yo'q.");
            return;
        }

        (RentalDto Rental, double Total)? result;
        try
        {
            result = await RentalFromCart(items, clientId, clientName).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат из чека: окно не открылось ({ex.Message}).", "WARNING");
            return;
        }
        if (result is not { } done)
            return;

        var r = done.Rental;
        var deposit = r.IsDocumentDeposit
            ? Tr.T("залог: документ", "күрөө: документ", "deposit: document", "depozito: belge", "garov: hujjat")
            : r.DepositAmount > 0
                ? Tr.T($"залог {r.DepositAmount:0.##} сом", $"күрөө {r.DepositAmount:0.##} сом", $"deposit {r.DepositAmount:0.##} som", $"depozito {r.DepositAmount:0.##} som", $"garov {r.DepositAmount:0.##} so'm")
                : "";
        var line = $"Прокат №{r.Number}: {string.Join(", ", r.Items.Select(i => i.Label))}, до {r.DateTo:dd.MM}" + (deposit.Length > 0 ? ", " + deposit : "");

        // Товары уходят в прокат (склад списывает документ проката), а в чеке остаётся только стоимость проката.
        _cart.Clear();
        foreach (var c in customs)
            AddCustomItem(c.Name, Math.Abs(c.Price), c.Qty, c.Price < 0);
        if (done.Total > 0)
            AddCustomItem(line, done.Total, 1, false);
        SyncLinesAndTotals();
        PosLogger.Log($"Прокат №{r.Number} из чека: {items.Count} вещ., стоимость {done.Total:0.00}.", "RENTAL");
        CartMessage = Tr.T($"Прокат №{r.Number} оформлен. Номер — на чеке, по нему закроете прокат при возврате.",
            $"Прокат №{r.Number} түзүлдү. Номери — чекте, кайтарганда ушул номер боюнча жабасыз.",
            $"Rental #{r.Number} created. The number is on the receipt — use it to close the rental on return.",
            $"Kiralama №{r.Number} oluşturuldu. Numara fişte — iadede bununla kapatın.",
            $"Prokat №{r.Number} rasmiylashtirildi. Raqami chekda — qaytarishda shu raqam bilan yopasiz.");
        _rentalAwaitingPayment = r.Number;
        if (Lines.Count > 0 && PayCommand.CanExecute(null))
            PayCommand.Execute(null);
    }

    /// <summary>2026-10-02, владелец: «почему прокат не закрывается после оплаты». Оплата — это деньги за
    /// аренду; прокат закрывается при возврате вещи. После оплаты касса говорит это прямо.</summary>
    private int? _rentalAwaitingPayment;

    public string UpsellProductText => _upsell is null
        ? ""
        : $"{_upsell.Product.Title} — {_upsell.Price.ToString("0.##", CultureInfo.InvariantCulture)} {Tr.T("сом", "сом", "som", "som", "so'm")}";

    public string UpsellReasonText => _upsell is null
        ? ""
        : Tr.T($"Берут вместе с «{_upsell.TriggerTitle}» ({_upsell.Together} чек.)",
            $"«{_upsell.TriggerTitle}» менен бирге алышат ({_upsell.Together} чек)",
            $"Bought together with “{_upsell.TriggerTitle}” ({_upsell.Together} receipts)",
            $"«{_upsell.TriggerTitle}» ile birlikte alınıyor ({_upsell.Together} fiş)",
            $"«{_upsell.TriggerTitle}» bilan birga olinadi ({_upsell.Together} chek)");

    private void SetUpsell(UpsellSuggestion? suggestion)
    {
        if (suggestion != null && _upsellShown.Add(suggestion.Product.Id))
        {
            var key = _upsellCartKey;
            _ = Task.Run(() => UpsellService.Record("shown", suggestion, key));
        }

        _upsell = suggestion;
        OnPropertyChanged(nameof(HasUpsell));
        OnPropertyChanged(nameof(UpsellProductText));
        OnPropertyChanged(nameof(UpsellReasonText));
        (AcceptUpsellCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (SkipUpsellCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private async void ScheduleUpsell()
    {
        var version = Interlocked.Increment(ref _upsellVersion);
        try
        {
            // Строки чека перестраиваются пачкой событий (очистка + добавление заново), и на миг
            // чек выглядит пустым. Решаем только после паузы, иначе подсказка «начинала новый чек»
            // посреди текущего и снова предлагала пропущенный товар (найдено проверкой 01.10).
            await Task.Delay(300).ConfigureAwait(false);
            if (version != Volatile.Read(ref _upsellVersion))
                return;

            List<(string ProductId, string Title)>? cart = null;
            HashSet<string>? skipped = null;
            await _dispatcher.InvokeAsync(() =>
            {
                if (version != Volatile.Read(ref _upsellVersion))
                    return;
                if (Lines.Count == 0)
                {
                    // Чек оплачен или очищен — следующий чек начинается с чистого листа.
                    _upsellCartKey = Guid.NewGuid().ToString("N");
                    _upsellSkipped.Clear();
                    _upsellShown.Clear();
                    SetUpsell(null);
                    return;
                }

                if (!UpsellService.Enabled)
                {
                    SetUpsell(null);
                    return;
                }

                cart = Lines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId))
                    .Select(l => (l.ProductId, l.Title)).ToList();
                skipped = new HashSet<string>(_upsellSkipped, StringComparer.OrdinalIgnoreCase);
            }).ConfigureAwait(false);
            if (cart is null || skipped is null)
                return;

            var suggestion = await Task.Run(() => UpsellService.Suggest(cart, skipped)).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (version == Volatile.Read(ref _upsellVersion))
                    SetUpsell(suggestion);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Допродажа: подсказка не подобрана ({ex.Message}).", "WARNING");
        }
    }

    private async Task AcceptUpsellAsync()
    {
        var suggestion = _upsell;
        if (suggestion is null)
            return;
        var key = _upsellCartKey;
        _ = Task.Run(() => UpsellService.Record("accepted", suggestion, key));
        _upsellSkipped.Add(suggestion.Product.Id);   // удалят из чека — снова не предлагать
        SetUpsell(null);
        ManualQuantity = "1";
        await AddFoundCatalogProductAsync(suggestion.Product).ConfigureAwait(true);
    }

    private void SkipUpsell()
    {
        var suggestion = _upsell;
        if (suggestion is null)
            return;
        var key = _upsellCartKey;
        _ = Task.Run(() => UpsellService.Record("skipped", suggestion, key));
        _upsellSkipped.Add(suggestion.Product.Id);
        // Следующую подсказку покажем, когда кассир добавит ещё товар, — не навязываемся.
        SetUpsell(null);
    }

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
        public DateTime? DeferredAt { get; set; }

        /// <summary>2026-10-04: клиент, выбранный в этом чеке сканом QR клиента NurCRM (см.
        /// BasketPanelViewModel.ClientQr.cs). На диск не сохраняется — после перезапуска кассы QR
        /// сканируют заново.</summary>
        public ClientOption? Client { get; set; }
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
