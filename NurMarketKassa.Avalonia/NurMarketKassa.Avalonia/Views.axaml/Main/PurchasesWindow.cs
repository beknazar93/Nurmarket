using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-22, владелец: «Закупки как у веба — изучи у веба закупку!» (docs/«Закупки в вебе NurCRM — исследование 2026-10-22.md»).
/// Раздел «Закупки» — как /crm/market/procurement сайта NurCRM, нашим дизайном:
///  • вкладки «История закупок» (приходы suppliers/receipts/ с фильтрами поставщик, период, поиск по поставщику и товару; «Вернуть» в строке),
///    «Заказы поставщикам» (наши заказы со статусами — у сервера их нет, журнал PurchasesStore) и «Возвраты поставщикам» (suppliers/returns/);
///  • наверху «Рекомендуемый заказ» (PurchasesWindow.Recommend.cs), «Возврат поставщику» и одна жёлтая «＋ Приём товара» — мастер
///    «1 Поставщик → 2 Товары → 3 Оплата» (PurchasesWindow.Receive.cs) с окном «Подтверждение оплаты» и «Скачать накладную».
/// Приход, склад и долг — те же, что у «Приёмки» склада (PurchaseReceivingService, SupplierDebtRecorder). Программа владельца — пункт меню
/// «Закупки» (группа «Товары»), касса — отдельным окном по праву «Склад».</summary>
public sealed partial class PurchasesWindow : Window, IOwnerSection, IOwnerSectionKeepAlive
{
    internal static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string TitleText => T("Закупки", "Сатып алуулар", "Purchases", "Satın alma", "Xaridlar");

    internal static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    internal static string Q(double v) => v.ToString("0.###", Ru);

    private readonly SupplierApiService _svc;
    private readonly Panel _host = new();
    private readonly Grid _main = new() { Margin = new Thickness(24, 16, 24, 20), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*") };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _hint = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid _kpi = new() { MinTileWidth = 170, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
    private readonly PurchasesSupplierPicker _filterSupplier;
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _periodLabel = new();
    private readonly TextBox _search;
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), IsVisible = false };
    // 2026-10-22: касса 1024×600 — плитки строкой текста (списку остаётся место), название окна — в строке кнопок.
    private readonly TextBlock _kpiLine = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), IsVisible = false };
    private bool _compact;
    private string _hintText = "";
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly Grid _head = new();
    private readonly Button _more;
    private readonly Grid _filters;
    private bool _narrow;
    private bool _embedded;
    private int _gen;

    private string _tab = "history";
    private string _period = "month";
    private DateTime? _customFrom;
    private DateTime? _customTo;

    // История закупок: загруженные страницы, всего по фильтру, подписи приходов этого ПК, долги, записанные по приходам.
    private List<SupplierReceipt> _receipts = new();
    private int _receiptCount;
    private double _receiptTotal;
    private int _page;
    private bool _hasNext;
    private bool _loaded;
    private Dictionary<string, PurchaseReceiptNote> _notes = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _debtKeys = new HashSet<string>();
    /// <summary>2026-10-27, С-2: суммы долгов, записанных журналом по меткам приходов (для плитки «В долг»).</summary>
    private IReadOnlyDictionary<string, double> _debtAmounts = new Dictionary<string, double>();
    private List<SupplierReturnInfo>? _returns;
    private const int PageSize = 50;

    // Открытая страница поверх списка: рекомендуемый заказ или приём товара.
    private Control? _sub;

    public PurchasesWindow()
    {
        _svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
        Title = TitleText;
        Width = 1180;
        Height = 780;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        AddRowStyles();

        _title.Text = Title;
        Use(_title, TextBlock.ForegroundProperty, "BrushText");
        _hint.Text = T("Приходы от поставщиков и возвраты — те же, что на сайте NurCRM. «＋ Приём товара» прибавит остаток на складе, запомнит цену закупки и долг поставщику.",
            "Жеткирүүчүлөрдөн кирүүлөр жана кайтаруулар — NurCRM сайтындагыдай. «＋ Товар кабыл алуу» кампадагы калдыкты кошот, сатып алуу баасын жана жеткирүүчүгө карызды эстейт.",
            "Deliveries from suppliers and returns — the same as on the NurCRM website. “＋ Receive goods” adds stock, remembers the purchase price and the debt to the supplier.",
            "Tedarikçi girişleri ve iadeler — NurCRM sitesindekiyle aynı. «＋ Mal kabul» stoğu artırır, alış fiyatını ve tedarikçi borcunu kaydeder.",
            "Yetkazib beruvchilardan kirimlar va qaytarishlar — NurCRM saytidagidek. «＋ Tovar qabul qilish» ombordagi qoldiqni oshiradi, xarid narxi va qarzni eslab qoladi.");
        Use(_hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        _hintText = _hint.Text;
        Use(_kpiLine, TextBlock.ForegroundProperty, "BrushText");

        // Наверху: «Рекомендуемый заказ», «Возврат поставщику» — обводкой, «＋ Приём товара» — единственная жёлтая кнопка экрана.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(_hint);
        var recommend = UiKit.Ghost(this, T("Рекомендуемый заказ", "Сунушталган буйрутма", "Recommended order", "Önerilen sipariş", "Tavsiya etilgan buyurtma"));
        Avalonia.Automation.AutomationProperties.SetName(recommend, recommend.Content as string);
        recommend.Click += (_, _) => OpenRecommend(null);
        Grid.SetColumn(recommend, 1);
        top.Children.Add(recommend);
        var giveBack = UiKit.Ghost(this, T("Возврат поставщику", "Жеткирүүчүгө кайтаруу", "Return to supplier", "Tedarikçiye iade", "Yetkazib beruvchiga qaytarish"));
        Avalonia.Automation.AutomationProperties.SetName(giveBack, giveBack.Content as string);
        giveBack.Margin = new Thickness(8, 0, 0, 0);
        giveBack.Click += async (_, _) => await ReturnAsync(null, null).ConfigureAwait(true);
        Grid.SetColumn(giveBack, 2);
        top.Children.Add(giveBack);
        var receive = UiKit.Primary(this, T("＋ Приём товара", "＋ Товар кабыл алуу", "＋ Receive goods", "＋ Mal kabul", "＋ Tovar qabul qilish"));
        Avalonia.Automation.AutomationProperties.SetName(receive, T("Приём товара от поставщика", "Жеткирүүчүдөн товар кабыл алуу", "Receive goods from a supplier", "Tedarikçiden mal kabul", "Yetkazib beruvchidan tovar qabul qilish"));
        receive.Margin = new Thickness(8, 0, 0, 0);
        receive.Click += (_, _) => OpenReceive(null);
        Grid.SetColumn(receive, 3);
        top.Children.Add(receive);
        top.Classes.Add("no-reflow");

        var tabsBox = UiKit.Segment(_tabs);
        tabsBox.HorizontalAlignment = HorizontalAlignment.Left;
        tabsBox.Margin = new Thickness(0, 0, 0, 12);

        // Фильтры: поставщик (поиск и «＋»), период, поиск по поставщику и товару, «Обновить».
        _filterSupplier = new PurchasesSupplierPicker(_svc) { AllowAll = true };
        _filterSupplier.Changed += async _ => await ReloadAsync().ConfigureAwait(true);
        _search = UiKit.Input(this, T("Поиск: поставщик или товар", "Издөө: жеткирүүчү же товар", "Search: supplier or product", "Ara: tedarikçi veya ürün", "Qidirish: yetkazib beruvchi yoki mahsulot"), 44);
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        _search.TextChanged += (_, _) => Render();
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Height = 44;
        Avalonia.Automation.AutomationProperties.SetName(refresh, refresh.Content as string);
        refresh.Click += async (_, _) => await ReloadAsync().ConfigureAwait(true);
        var periods = UiKit.WithPeriodLabel(UiKit.Segment(_periods), _periodLabel);
        _filters = new Grid { ColumnDefinitions = new ColumnDefinitions("1.2*,Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        _filters.Children.Add(_filterSupplier);
        Grid.SetColumn(periods, 1);
        periods.Margin = new Thickness(10, 0, 0, 0);
        _filters.Children.Add(periods);
        Grid.SetColumn(_search, 2);
        _search.Margin = new Thickness(10, 0, 0, 0);
        _filters.Children.Add(_search);
        Grid.SetColumn(refresh, 3);
        refresh.Margin = new Thickness(8, 0, 0, 0);
        _filters.Children.Add(refresh);
        _filters.Classes.Add("no-reflow");
        BuildPeriods();

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var headBox = new Border { Padding = new Thickness(16, 10, 4, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = _head };
        Use(headBox, Border.BackgroundProperty, "BrushPanelSoft");
        Use(headBox, Border.BorderBrushProperty, "BrushBorder");
        _more = UiKit.Ghost(this, T("Показать ещё", "Дагы көрсөтүү", "Show more", "Daha fazla", "Yana ko'rsatish"));
        _more.HorizontalAlignment = HorizontalAlignment.Center;
        _more.Margin = new Thickness(0, 12, 0, 12);
        _more.IsVisible = false;
        _more.Click += async (_, _) => await LoadPageAsync(_page + 1, _gen).ConfigureAwait(true);
        var listContent = new StackPanel { Children = { _rows, _more } };
        var scroll = new ScrollViewer { Content = listContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var listGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listGrid.Children.Add(headBox);
        Grid.SetRow(scroll, 1);
        listGrid.Children.Add(scroll);
        var listCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = listGrid };
        Use(listCard, Border.BackgroundProperty, "BrushPanel");
        Use(listCard, Border.BorderBrushProperty, "BrushBorder");

        Grid.SetRow(top, 1);
        Grid.SetRow(tabsBox, 2);
        Grid.SetRow(_kpi, 3);
        Grid.SetRow(_kpiLine, 3);
        Grid.SetRow(_filters, 4);
        Grid.SetRow(_status, 5);
        Grid.SetRow(listCard, 6);
        _main.Children.Add(_title);
        _main.Children.Add(top);
        _main.Children.Add(tabsBox);
        _main.Children.Add(_kpi);
        _main.Children.Add(_kpiLine);
        _main.Children.Add(_filters);
        _main.Children.Add(_status);
        _main.Children.Add(listCard);
        _host.Children.Add(_main);
        Content = _host;
        BuildTabs();
        RenderKpi();

        // Узко (раздел на 1024 — ~950 точек, телефон): подсказка прячется, фильтры — в две строки, в списке меньше колонок.
        _main.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 1060;
            var compact = e.NewSize.Height < 600;
            if (narrow == _narrow && compact == _compact && _head.ColumnDefinitions.Count > 0)
                return;
            _narrow = narrow;
            _compact = compact;
            // Низкий экран (касса 1024×600): плитки — одной строкой, название окна — слева от кнопок вместо подсказки.
            var titleInBar = compact && !_embedded;
            _title.IsVisible = !_embedded && !compact;
            _hint.IsVisible = !narrow || titleInBar;
            _hint.Text = titleInBar ? TitleText : _hintText;
            _hint.FontSize = titleInBar ? 20 : 12.5;
            _hint.FontWeight = titleInBar ? FontWeight.Bold : FontWeight.Normal;
            Use(_hint, TextBlock.ForegroundProperty, titleInBar ? "BrushText" : "BrushTextSoft");
            _kpi.IsVisible = !compact;
            _kpiLine.IsVisible = compact;
            _filters.ColumnDefinitions = narrow ? new ColumnDefinitions("*,Auto") : new ColumnDefinitions("1.2*,Auto,*,Auto");
            _filters.RowDefinitions = narrow ? new RowDefinitions("Auto,10,Auto") : new RowDefinitions();
            Grid.SetRow(periods, 0);
            Grid.SetColumn(periods, 1);
            Grid.SetRow(_search, narrow ? 2 : 0);
            Grid.SetColumn(_search, narrow ? 0 : 2);
            _search.Margin = new Thickness(narrow ? 0 : 10, 0, 0, 0);
            Grid.SetRow(refresh, narrow ? 2 : 0);
            Grid.SetColumn(refresh, narrow ? 1 : 3);
            Render();
        };

        SetStatus(T("Загружаю закупки…", "Сатып алуулар жүктөлүүдө…", "Loading purchases…", "Satın almalar yükleniyor…", "Xaridlar yuklanmoqda…"));
        Opened += async (_, _) =>
        {
            FitKassaScreen();
            ConsumePending();
            if (_loaded)
                return;
            _loaded = true;
            await Task.WhenAll(_filterSupplier.LoadAsync(), ReloadAsync()).ConfigureAwait(true);
        };
        Action onStore = () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _notes = PurchasesStore.ReceiptNotes();
            BuildTabs();
            Render();
        });
        PurchasesStore.Changed += onStore;
        Closed += (_, _) => PurchasesStore.Changed -= onStore;
        Action onPending = ConsumePending;
        PendingPosted += onPending;
        Closed += (_, _) => PendingPosted -= onPending;
        KeyDown += (_, e) =>
        {
            // Esc на открытой странице — назад к списку (сам раздел закрывает оболочка/окно). Набранный приём Esc не бросает —
            // выйти можно кнопкой «← К закупкам» (она спросит).
            if (e.Key == Avalonia.Input.Key.Escape && _sub is not null && !e.Handled)
            {
                e.Handled = true;
                if (!ReceiveHasWork)
                    CloseSub();
            }
        };
    }

    public void AsOwnerSection()
    {
        _embedded = true;
        _title.IsVisible = false;
        _main.Margin = OwnerSectionLayout.Margin;
    }

    /// <summary>Есть незаконченный приём (строки с количеством) — оболочка не закрывает спрятанный раздел и предупреждает.</summary>
    public bool? HasUnsavedChanges => ReceiveHasWork;
    public bool KeepAliveWhenHidden => ReceiveHasWork;

    /// <summary>Касса на экране 1024×600: окно больше экрана — разворачиваем (кнопки внизу всегда видны).</summary>
    private void FitKassaScreen()
    {
        if (_embedded)
            return;
        try
        {
            if (Screens.ScreenFromWindow(this)?.WorkingArea is { } area && (area.Height / (Screens.ScreenFromWindow(this)?.Scaling ?? 1) < Height + 40
                                                                            || area.Width / (Screens.ScreenFromWindow(this)?.Scaling ?? 1) < Width + 20))
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: размер экрана не получен ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Приём, который нужно открыть, когда раздел «Закупки» программы владельца покажется (переход из «Склада»/«Пополнения»).</summary>
    private static ReceivePrefill? _pendingPrefill;
    private static bool _pendingReceive;
    private static event Action? PendingPosted;

    /// <summary>Открыть «Закупки» (и сразу «Приём товара», если receive): в программе владельца — раздел меню (ProductActionPlan.OpenSection,
    /// тот же переход, что у Нур Советника), в кассе — отдельное окно. Право «Склад» проверяет вызывающий.</summary>
    internal static void Open(Window from, bool receive = false, ReceivePrefill? prefill = null)
    {
        if (AppMode.IsOwner && ProductActionPlan.OpenSection is { } open)
        {
            _pendingReceive = receive || prefill is not null;
            _pendingPrefill = prefill;
            open("purchases");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => PendingPosted?.Invoke(), Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        var w = new PurchasesWindow();
        w.Show(from);
        if (receive || prefill is not null)
            w.OpenReceive(prefill);
    }

    /// <summary>Касса: окно «Закупки» из меню (MainWindow.NavigatePurchases).</summary>
    public static void ShowFor(Window owner) => Open(owner);

    /// <summary>2026-10-27, жалоба «Нур Советник открывает не ту вкладку»: вкладка «Закупок» по ключу словаря (history, orders, returns, receive —
    /// «Приём товара»); true — вкладка действительно показана.</summary>
    public bool SelectNavTab(string tab)
    {
        if (tab == "receive")
        {
            OpenReceive(null);
            return _receive is not null;
        }
        if (tab is not ("history" or "orders" or "returns"))
            return false;
        if (_receive is not null || _recommend is not null)
            CloseSub();
        if (_tab != tab)
        {
            _tab = tab;
            BuildTabs();
            _ = ReloadAsync();
        }
        return _tab == tab;
    }

    private void ConsumePending()
    {
        if (!_pendingReceive)
            return;
        _pendingReceive = false;
        var prefill = _pendingPrefill;
        _pendingPrefill = null;
        OpenReceive(prefill);
    }

    // ------------------------------------------------------------------ вкладки и период

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        var open = PurchasesStore.Orders().Count(o => o.IsOpen);
        foreach (var (key, text) in new[]
                 {
                     ("history", T("История закупок", "Сатып алуулар тарыхы", "Purchase history", "Satın alma geçmişi", "Xaridlar tarixi")),
                     ("orders", T("Заказы поставщикам", "Жеткирүүчүлөргө буйрутмалар", "Supplier orders", "Tedarikçi siparişleri", "Buyurtmalar") + (open > 0 ? $" ({open})" : "")),
                     ("returns", T("Возвраты поставщикам", "Жеткирүүчүлөргө кайтаруулар", "Returns to suppliers", "Tedarikçiye iadeler", "Qaytarishlar")),
                 })
        {
            var seg = UiKit.Seg(text, key == _tab);
            seg.MinHeight = 44;
            seg.Click += async (_, _) =>
            {
                if (_tab == key)
                    return;
                _tab = key;
                BuildTabs();
                await ReloadAsync().ConfigureAwait(true);
            };
            _tabs.Children.Add(seg);
        }
    }

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("week", T("Неделя", "Апта", "Week", "Hafta", "Hafta")), ("month", T("Месяц", "Ай", "Month", "Ay", "Oy")),
                     ("quarter", T("3 месяца", "3 ай", "3 months", "3 ay", "3 oy")), ("all", T("Всё", "Баары", "All", "Tümü", "Barchasi")),
                     ("custom", T("Даты…", "Күндөр…", "Dates…", "Tarihler…", "Sanalar…")),
                 })
        {
            var seg = UiKit.Seg(text, key == _period);
            seg.MinHeight = 44;
            if (key == "custom")
                seg.Flyout = DatesFlyout();
            else
                seg.Click += async (_, _) =>
                {
                    _period = key;
                    BuildPeriods();
                    await ReloadAsync().ConfigureAwait(true);
                };
            _periods.Children.Add(seg);
        }
        var (from, to) = PeriodBounds();
        _periodLabel.Text = from is { } f ? PeriodRange.Label(f, to ?? DateTime.Today) : "";
    }

    /// <summary>«Даты…» — с и по (как «Дата от / Дата до» сайта), «Применить».</summary>
    private Flyout DatesFlyout()
    {
        var fromPicker = new CalendarDatePicker { SelectedDate = _customFrom ?? DateTime.Today.AddDays(-30), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42, SelectedDateFormat = CalendarDatePickerFormat.Short };
        var toPicker = new CalendarDatePicker { SelectedDate = _customTo ?? DateTime.Today, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42, SelectedDateFormat = CalendarDatePickerFormat.Short };
        var error = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(error, TextBlock.ForegroundProperty, "BrushDanger");
        var apply = UiKit.Ghost(this, T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"));
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        apply.Click += async (_, _) =>
        {
            if (fromPicker.SelectedDate is not { } f || toPicker.SelectedDate is not { } t || f.Date > t.Date)
            {
                error.Text = T("Дата «по» не может быть раньше даты «с».", "«Чейин» күнү «баштап» күнүнөн мурун боло албайт.", "The “to” date can't be earlier than the “from” date.",
                    "«Bitiş» tarihi «başlangıç» tarihinden önce olamaz.", "«Gacha» sanasi «dan» sanasidan oldin bo'lishi mumkin emas.");
                error.IsVisible = true;
                return;
            }
            _customFrom = f.Date;
            _customTo = t.Date;
            _period = "custom";
            flyout.Hide();
            BuildPeriods();
            await ReloadAsync().ConfigureAwait(true);
        };
        flyout.Content = new StackPanel
        {
            Spacing = 8, Width = 280,
            Children = { UiKit.Label(this, T("С", "Баштап", "From", "Başlangıç", "Dan")), fromPicker, UiKit.Label(this, T("По", "Чейин", "To", "Bitiş", "Gacha")), toPicker, error, apply },
        };
        return flyout;
    }

    private (DateTime? From, DateTime? To) PeriodBounds() => _period switch
    {
        "week" => (PeriodRange.WeekFrom(DateTime.Today), null),
        "month" => (DateTime.Today.AddDays(-29), null),
        "quarter" => (DateTime.Today.AddDays(-89), null),
        "custom" => (_customFrom, _customTo),
        _ => (null, null),
    };

    // ------------------------------------------------------------------ загрузка

    private async Task ReloadAsync()
    {
        var gen = ++_gen;
        _notes = PurchasesStore.ReceiptNotes();
        _debtKeys = SupplierDebtRecorder.DoneKeys();
        _debtAmounts = SupplierDebtRecorder.DoneAmounts();
        _rows.Children.Clear();
        _more.IsVisible = false;
        ApplyHead();
        if (_tab == "orders")
        {
            SetStatus(null);
            RenderKpi();
            Render();
            return;
        }
        if (_tab == "returns")
        {
            await LoadReturnsAsync(gen).ConfigureAwait(true);
            return;
        }
        _receipts = new List<SupplierReceipt>();
        _receiptCount = 0;
        _receiptTotal = 0;
        RenderKpi();
        await LoadPageAsync(1, gen).ConfigureAwait(true);
    }

    private async Task LoadPageAsync(int page, int gen)
    {
        SetStatus(page == 1 ? T("Загружаю закупки…", "Сатып алуулар жүктөлүүдө…", "Loading purchases…", "Satın almalar yükleniyor…", "Xaridlar yuklanmoqda…") : null);
        _more.IsEnabled = false;
        try
        {
            var (from, to) = PeriodBounds();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var (list, count, total, hasNext) = await _svc.GetReceiptsPageAsync(page, PageSize, _filterSupplier.Selected?.Id, from, to, cts.Token).ConfigureAwait(true);
            if (gen != _gen)
                return;
            ShowReceipts(page == 1 ? list : _receipts.Concat(list).ToList(), count, total, page, hasNext);
            SetStatus(null);
            PosLogger.Log($"Закупки: история, страница {page} — {list.Count} из {count}, сумма {total:0.00}.", "INFO");
        }
        catch (Exception ex)
        {
            if (gen != _gen)
                return;
            SetStatus(T("Закупки не загрузились: ", "Сатып алуулар жүктөлгөн жок: ", "Purchases didn't load: ", "Satın almalar yüklenmedi: ", "Xaridlar yuklanmadi: ")
                      + (ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi") : ServerTelegramBotApi.DescribeFields(ex))
                      + T(" — нажмите «Обновить».", " — «Жаңыртууну» басыңыз.", " — press “Refresh”.", " — «Yenile»ye basın.", " — «Yangilash»ni bosing."), error: true);
            PosLogger.Log($"Закупки: история не загружена ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            Render();
        }
        finally
        {
            _more.IsEnabled = true;
        }
    }

    /// <summary>Приходы на экран (и для стенда снимков без сети).</summary>
    public void ShowReceipts(List<SupplierReceipt> receipts, int count, double total, int page, bool hasNext)
    {
        _receipts = receipts;
        _receiptCount = Math.Max(count, receipts.Count);
        _receiptTotal = total;
        _page = page;
        _hasNext = hasNext;
        RenderKpi();
        Render();
    }

    private async Task LoadReturnsAsync(int gen)
    {
        SetStatus(T("Загружаю возвраты…", "Кайтаруулар жүктөлүүдө…", "Loading returns…", "İadeler yükleniyor…", "Qaytarishlar yuklanmoqda…"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var list = await _svc.GetReturnsAsync(_filterSupplier.Selected?.Id, cts.Token).ConfigureAwait(true);
            if (gen != _gen)
                return;
            ShowReturns(list);
            SetStatus(null);
        }
        catch (Exception ex)
        {
            if (gen != _gen)
                return;
            _returns = new List<SupplierReturnInfo>();
            SetStatus(T("Возвраты не загрузились: ", "Кайтаруулар жүктөлгөн жок: ", "Returns didn't load: ", "İadeler yüklenmedi: ", "Qaytarishlar yuklanmadi: ")
                      + ServerTelegramBotApi.DescribeFields(ex), error: true);
            RenderKpi();
            Render();
        }
    }

    public void ShowReturns(List<SupplierReturnInfo> list)
    {
        _returns = list;
        RenderKpi();
        Render();
    }

    private void SetStatus(string? text, bool error = false)
    {
        _status.IsVisible = !string.IsNullOrEmpty(text);
        _status.Text = text ?? "";
        Use(_status, TextBlock.ForegroundProperty, error ? "BrushDanger" : "BrushTextSoft");
    }

    // ------------------------------------------------------------------ плитки

    private void RenderKpi()
    {
        RenderKpiTiles();
        // Та же сводка строкой (низкий экран): «Всего закупок: 53 · Общая сумма: 37 892 772,00 сом · …».
        _kpiLine.Text = string.Join("   ·   ", _kpi.Children.OfType<Border>().Select(b => b.Child is StackPanel p && p.Children.Count == 2
            && p.Children[0] is TextBlock label && p.Children[1] is Viewbox { Child: TextBlock value } ? label.Text + ": " + value.Text : "").Where(x => x.Length > 0));
    }

    private void RenderKpiTiles()
    {
        _kpi.Children.Clear();
        if (_tab == "orders")
        {
            var orders = ShownOrders().ToList();
            var open = orders.Where(o => o.IsOpen).ToList();
            _kpi.Children.Add(UiKit.Kpi(T("Открытых заказов", "Ачык буйрутмалар", "Open orders", "Açık siparişler", "Ochiq buyurtmalar"), open.Count.ToString(CultureInfo.InvariantCulture)));
            _kpi.Children.Add(UiKit.Kpi(T("На сумму", "Суммасы", "Worth", "Tutar", "Summasi"), MoneyText.Som(open.Sum(o => o.Total))));
            _kpi.Children.Add(UiKit.Kpi(T("Ждут приёмки", "Кабыл алууну күтүүдө", "Awaiting receiving", "Kabul bekleyen", "Qabulni kutmoqda"),
                open.Count(o => o.State is "sent" or "partial").ToString(CultureInfo.InvariantCulture)));
            return;
        }
        if (_tab == "returns")
        {
            var list = ShownReturns().ToList();
            _kpi.Children.Add(UiKit.Kpi(T("Возвратов", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar"), list.Count.ToString(CultureInfo.InvariantCulture)));
            _kpi.Children.Add(UiKit.Kpi(T("На сумму", "Суммасы", "Worth", "Tutar", "Summasi"), MoneyText.Som(list.Sum(r => r.Total))));
            _kpi.Children.Add(UiKit.Kpi(T("В счёт долга", "Карыздын эсебине", "Against debt", "Borca sayılan", "Qarz hisobiga"),
                MoneyText.Som(list.Where(r => r.Compensation == "debt_offset").Sum(r => r.Total))));
            return;
        }
        _kpi.Children.Add(UiKit.Kpi(T("Всего закупок", "Бардык сатып алуулар", "Total purchases", "Toplam satın alma", "Jami xaridlar"), _receiptCount.ToString("N0", Ru)));
        _kpi.Children.Add(UiKit.Kpi(T("Общая сумма", "Жалпы сумма", "Total amount", "Toplam tutar", "Umumiy summa"), MoneyText.Som(_receiptTotal)));
        // 2026-10-27, аудит владельца С-2: плитка «В долг (по этому ПК)» показывала 0,00 при строке «В долг» в списке — она считала только
        // заметки закупок, а строка берёт ещё и записанные долги по приходам. Теперь та же двойная проверка, что у значка оплаты строки (PayPill).
        var debt = _receipts.Sum(r => _notes.TryGetValue(r.Id, out var n) ? n.Debt : _debtAmounts.GetValueOrDefault(SupplierDebtRecorder.MarkerForReceipt(r.Id)));
        var debtTile = UiKit.Kpi(T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), MoneyText.Som(debt), danger: debt > 0.005);
        ToolTip.SetTip(debtTile, T("Приходы, оплаченные в долг и записанные в программе. Способ оплаты прихода сервер NurCRM не хранит, поэтому закупки, проведённые не здесь, в плитке не видны.",
            "Карызга төлөнүп, программада жазылган кирешелер. NurCRM сервери кириштин төлөм ыкмасын сактабайт, ошондуктан бул жерде өткөрүлбөгөн сатып алуулар плиткада көрүнбөйт.",
            "Deliveries paid on credit and recorded in the program. The NurCRM server does not store the payment method of a delivery, so purchases made elsewhere are not shown in the tile.",
            "Veresiye ödenen ve programda kaydedilen girişler. NurCRM sunucusu girişin ödeme yöntemini saklamaz, bu yüzden başka yerde yapılan alımlar kutuda görünmez.",
            "Qarzga to'langan va dasturda yozilgan kirimlar. NurCRM serveri kirimning to'lov usulini saqlamaydi, shu sababli boshqa joyda o'tkazilgan xaridlar kartada ko'rinmaydi."));
        _kpi.Children.Add(debtTile);
        _kpi.Children.Add(UiKit.Kpi(T("Возвращено", "Кайтарылды", "Returned", "İade edilen", "Qaytarildi"), MoneyText.Som(_receipts.Sum(r => r.ReturnedAmount))));
    }

    // ------------------------------------------------------------------ список

    private string Query => (_search.Text ?? "").Trim();

    private IEnumerable<SupplierReceipt> ShownReceipts()
    {
        var q = Query;
        return _receipts.Where(r => q.Length == 0 || r.SupplierName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                    || r.CreatedBy.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                    || r.Lines.Any(l => l.ProductName.Contains(q, StringComparison.CurrentCultureIgnoreCase) || l.Code.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private IEnumerable<PurchaseOrder> ShownOrders()
    {
        var q = Query;
        var supplier = _filterSupplier.Selected?.Id;
        var (from, to) = PeriodBounds();
        return PurchasesStore.Orders().Where(o => (supplier is null || o.SupplierId == supplier)
                                                  && (from is null || o.CreatedAt.Date >= from.Value.Date) && (to is null || o.CreatedAt.Date <= to.Value.Date)
                                                  && (q.Length == 0 || o.SupplierName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                                      || o.Number.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                                      || o.Lines.Any(l => l.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
            .OrderBy(o => o.IsOpen ? 0 : 1).ThenByDescending(o => o.UpdatedAt);
    }

    private IEnumerable<SupplierReturnInfo> ShownReturns()
    {
        var q = Query;
        var (from, to) = PeriodBounds();
        return (_returns ?? new List<SupplierReturnInfo>()).Where(r => (from is null || r.CreatedAt?.Date >= from.Value.Date) && (to is null || r.CreatedAt?.Date <= to.Value.Date)
            && (q.Length == 0 || r.SupplierName.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Comment.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.Lines.Any(l => l.ProductName.Contains(q, StringComparison.CurrentCultureIgnoreCase))));
    }

    /// <summary>Колонки шапки и строк по вкладке и ширине.</summary>
    private string Columns => _tab switch
    {
        "orders" => _narrow ? "1.1*,1.8*,0,1.1*,1.4*,Auto" : "1.1*,2*,0.7*,1.1*,1.4*,Auto",
        "returns" => _narrow ? "1*,1.8*,0,1.1*,1.8*,0" : "1*,2*,0.7*,1.1*,1.8*,Auto",
        _ => _narrow ? "1*,1.9*,0,1.1*,1.6*,Auto" : "1*,2.2*,0.7*,1.1*,1.6*,Auto",
    };

    private void ApplyHead()
    {
        _head.Children.Clear();
        _head.ColumnDefinitions = new ColumnDefinitions(Columns);
        var heads = _tab switch
        {
            "orders" => new[] { T("Заказ", "Буйрутма", "Order", "Sipariş", "Buyurtma"), T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"),
                T("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), T("Статус", "Абалы", "Status", "Durum", "Holat"), "" },
            "returns" => new[] { T("Дата", "Күнү", "Date", "Tarih", "Sana"), T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"),
                T("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), T("Деньги и причина", "Акча жана себеп", "Money & reason", "Para ve neden", "Pul va sabab"), "" },
            _ => new[] { T("Дата", "Күнү", "Date", "Tarih", "Sana"), T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"),
                T("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), "" },
        };
        for (var i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock
            {
                Text = heads[i], FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0), TextAlignment = i is 2 or 3 ? TextAlignment.Right : TextAlignment.Left,
                IsVisible = !(_narrow && i == 2),
            };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            _head.Children.Add(t);
        }
        // Колонка кнопки строки — той же ширины, что кнопка (шапка ровно над столбцами).
        if (_head.Children.LastOrDefault() is TextBlock last)
            last.MinWidth = _tab == "returns" ? 0 : 112;
    }

    private void Render()
    {
        ApplyHead();
        _rows.Children.Clear();
        _more.IsVisible = false;
        if (_tab == "orders")
        {
            RenderKpi();
            var orders = ShownOrders().ToList();
            if (orders.Count == 0)
            {
                _rows.Children.Add(SectionCards.Empty(this, PurchasesStore.Orders().Count == 0
                    ? T("Заказов поставщикам пока нет. Откройте «Рекомендуемый заказ»: выберите поставщика, «Заполнить рекомендуемый заказ» и «Сохранить заказ» — заказ появится здесь, его можно отправить в WhatsApp и потом принять по нему товар.",
                        "Жеткирүүчүлөргө буйрутмалар азырынча жок. «Сунушталган буйрутманы» ачыңыз: жеткирүүчүнү тандап, «Сунушталган буйрутманы толтуруу» жана «Буйрутманы сактоо» — буйрутма бул жерде пайда болот, аны WhatsApp'ка жөнөтүп, кийин ал боюнча товар кабыл алсаңыз болот.",
                        "No supplier orders yet. Open “Recommended order”: choose a supplier, “Fill recommended order” and “Save order” — the order appears here; send it via WhatsApp and receive goods against it later.",
                        "Henüz tedarikçi siparişi yok. «Önerilen sipariş»i açın: tedarikçi seçin, «Önerilen siparişi doldur» ve «Siparişi kaydet» — sipariş burada görünür, WhatsApp'tan gönderip sonra ona göre mal kabul edebilirsiniz.",
                        "Hozircha buyurtmalar yo'q. «Tavsiya etilgan buyurtma»ni oching: yetkazib beruvchini tanlang, «Tavsiya etilgan buyurtmani to'ldirish» va «Buyurtmani saqlash» — buyurtma shu yerda paydo bo'ladi.")
                    : T("По этим фильтрам заказов нет.", "Бул чыпкалар боюнча буйрутма жок.", "No orders match these filters.", "Bu filtrelere uyan sipariş yok.", "Bu filtrlar bo'yicha buyurtma yo'q.")));
                return;
            }
            foreach (var o in orders)
                _rows.Children.Add(OrderRow(o));
            return;
        }
        if (_tab == "returns")
        {
            RenderKpi();
            if (_returns is null)
                return;
            var list = ShownReturns().ToList();
            if (list.Count == 0)
            {
                _rows.Children.Add(SectionCards.Empty(this, T("Возвратов поставщикам за этот период нет. «Возврат поставщику» наверху — брак, излишек, ошибка поставки: товар уйдёт со склада, деньги — в кассу или в счёт долга.",
                    "Бул мезгилде жеткирүүчүлөргө кайтаруулар жок. Жогорудагы «Жеткирүүчүгө кайтаруу» — бузук, ашыкча, ката: товар кампадан кетет, акча — кассага же карыздын эсебине.",
                    "No returns to suppliers in this period. “Return to supplier” at the top — defects, surplus, wrong delivery: goods leave the stock, money goes to the till or against the debt.",
                    "Bu dönemde tedarikçiye iade yok. Üstteki «Tedarikçiye iade» — kusur, fazlalık, yanlış teslimat: ürün stoktan çıkar, para kasaya ya da borca sayılır.",
                    "Bu davrda qaytarishlar yo'q. Yuqoridagi «Yetkazib beruvchiga qaytarish» — brak, ortiqcha, xato: mahsulot ombordan chiqadi, pul kassaga yoki qarz hisobiga.")));
                return;
            }
            foreach (var r in list)
                _rows.Children.Add(ReturnRow(r));
            return;
        }

        if (!_loaded || (_receipts.Count == 0 && _status.IsVisible))
            return;
        var shown = ShownReceipts().ToList();
        if (shown.Count == 0)
        {
            _rows.Children.Add(SectionCards.Empty(this, _receipts.Count == 0
                ? T("Закупок за этот период нет. Нажмите «＋ Приём товара»: выберите поставщика, отсканируйте товары, укажите количество и цену закупки — остаток на складе прибавится.",
                    "Бул мезгилде сатып алуулар жок. «＋ Товар кабыл алууну» басыңыз: жеткирүүчүнү тандап, товарларды сканерлеп, санын жана сатып алуу баасын жазыңыз — кампадагы калдык кошулат.",
                    "No purchases in this period. Press “＋ Receive goods”: choose a supplier, scan products, enter quantity and purchase price — the stock goes up.",
                    "Bu dönemde satın alma yok. «＋ Mal kabul»e basın: tedarikçi seçin, ürünleri okutun, miktar ve alış fiyatını girin — stok artar.",
                    "Bu davrda xaridlar yo'q. «＋ Tovar qabul qilish»ni bosing: yetkazib beruvchini tanlang, mahsulotlarni skanerlang, miqdor va xarid narxini kiriting — ombordagi qoldiq oshadi.")
                : T("По этому поиску закупок нет (ищем на загруженных страницах). Проверьте написание или очистите поиск.",
                    "Бул издөө боюнча сатып алуулар жок (жүктөлгөн барактардан издейбиз). Жазылышын текшериңиз же издөөнү тазалаңыз.",
                    "No purchases match this search (searching the loaded pages). Check the spelling or clear the search.",
                    "Bu aramaya uyan satın alma yok (yüklenen sayfalarda aranır). Yazımı kontrol edin veya aramayı temizleyin.",
                    "Bu qidiruv bo'yicha xaridlar yo'q (yuklangan sahifalarda qidiriladi). Yozilishini tekshiring yoki qidiruvni tozalang.")));
            _more.IsVisible = _hasNext;
            return;
        }
        foreach (var r in shown)
            _rows.Children.Add(ReceiptRow(r));
        _more.IsVisible = _hasNext;
    }

    // ------------------------------------------------------------------ строки

    /// <summary>Строка: данные — кнопкой во всю ширину (щелчок, Tab+Enter — подробности), справа отдельная кнопка действия.</summary>
    private Control RowShell(Grid data, string accessibleName, Action open, Button? action)
    {
        var row = new Button { Content = data };
        row.Classes.Add("pur-row");
        Avalonia.Automation.AutomationProperties.SetName(row, accessibleName);
        row.Click += (_, _) => open();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(row);
        if (action is not null)
        {
            action.Margin = new Thickness(0, 0, 8, 0);
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(action, 1);
            g.Children.Add(action);
        }
        else if (_tab != "returns")
        {
            // Строка без кнопки (заказ принят) — то же место справа, чтобы колонки не съезжали относительно шапки.
            var spacer = new Border { Width = 112 };
            Grid.SetColumn(spacer, 1);
            g.Children.Add(spacer);
        }
        var box = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        return box;
    }

    /// <summary>Сетка данных строки без последней колонки (кнопка — снаружи, ширина колонки шапки 112).</summary>
    private Grid DataGrid()
    {
        var cols = Columns.Split(',');
        return new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", cols.Take(cols.Length - 1))) };
    }

    private Button RowAction(string text, string name)
    {
        var b = new Button
        {
            Content = text, Height = 44, MinHeight = 44, MinWidth = 104, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            FontSize = 13.5, FontWeight = FontWeight.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("sec-outline");
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        return b;
    }

    private Control ReceiptRow(SupplierReceipt r)
    {
        var g = DataGrid();
        Put(g, 0, Two(r.CreatedAt?.ToString("dd.MM.yyyy", Ru) ?? "—", r.CreatedAt?.ToString("HH:mm", Ru) ?? ""));
        Put(g, 1, Two(r.SupplierName.Length > 0 ? r.SupplierName : "—",
            T("принял: ", "кабыл алды: ", "by: ", "kabul: ", "qabul qildi: ") + (r.CreatedBy.Length > 0 ? r.CreatedBy : "—"), bold: true));
        var items = Cell((r.ItemsCount > 0 ? r.ItemsCount : r.Lines.Count).ToString(CultureInfo.InvariantCulture), "BrushText", 13.5);
        items.TextAlignment = TextAlignment.Right;
        items.IsVisible = !_narrow;
        Put(g, 2, items);
        var sum = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var total = Cell(MoneyText.Som(r.Total), "BrushText", 14, FontWeight.SemiBold);
        total.TextAlignment = TextAlignment.Right;
        total.Margin = default;
        sum.Children.Add(total);
        if (r.ReturnedAmount > 0.005)
        {
            var back = Cell(T("возврат ", "кайтаруу ", "returned ", "iade ", "qaytarish ") + MoneyText.Som(r.ReturnedAmount), "BrushDanger", 12);
            back.TextAlignment = TextAlignment.Right;
            back.Margin = default;
            sum.Children.Add(back);
        }
        Put(g, 3, sum);
        Put(g, 4, PayPill(r));
        var action = RowAction(T("Вернуть", "Кайтаруу", "Return", "İade", "Qaytarish"), T("Вернуть товар поставщику по этому приходу", "Бул кириш боюнча товарды жеткирүүчүгө кайтаруу",
            "Return goods to the supplier from this delivery", "Bu girişten tedarikçiye iade et", "Bu kirim bo'yicha tovarni qaytarish"));
        action.IsEnabled = r.ReturnedAmount < r.Total - 0.005;
        action.Click += async (_, _) => await ReturnAsync(r.SupplierId, r.Id).ConfigureAwait(true);
        return RowShell(g, $"{r.SupplierName}, {r.CreatedAt:dd.MM.yyyy}, {MoneyText.Som(r.Total)}", () => _ = ShowReceiptAsync(r), action);
    }

    /// <summary>Оплата прихода: подпись «Закупок» этого ПК → долг, записанный журналом по приходу → «—» (сервер способ оплаты не отдаёт).</summary>
    private Control PayPill(SupplierReceipt r)
    {
        string text;
        string bg;
        string fg;
        string? sub = null;
        if (_notes.TryGetValue(r.Id, out var n))
        {
            (text, bg, fg) = n.PayKind switch
            {
                "cash" => (T("Оплачено", "Төлөндү", "Paid", "Ödendi", "To'landi"), "BrushSuccessSoft", "BrushSuccess"),
                "drawer" => (T("Оплачено из ящика", "Кассадан төлөндү", "Paid from drawer", "Kasadan ödendi", "Kassadan to'landi"), "BrushSuccessSoft", "BrushSuccess"),
                "partial" => (T("Частично", "Жарым-жартылай", "Partly paid", "Kısmen", "Qisman"), "BrushWarningSoft", "BrushText"),
                _ => (T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), "BrushWarningSoft", "BrushText"),
            };
            if (n.Debt > 0.005)
                sub = T("долг ", "карыз ", "debt ", "borç ", "qarz ") + MoneyText.Som(n.Debt) + (n.DueDate is { } d ? T($" до {d:dd.MM}", $" {d:dd.MM} чейин", $" by {d:dd.MM}", $" {d:dd.MM}'e kadar", $" {d:dd.MM} gacha") : "");
            if (n.MoneyProblem)
            {
                text = "⚠ " + text;
                bg = "BrushDangerSoft";
                fg = "BrushDangerOnSoft";
            }
        }
        else if (_debtKeys.Contains(SupplierDebtRecorder.MarkerForReceipt(r.Id)))
            (text, bg, fg) = (T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), "BrushWarningSoft", "BrushText");
        else
            (text, bg, fg) = ("—", "BrushPanelSoft", "BrushTextSoft");
        var box = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var pill = SectionCards.Pill(this, text, bg, fg);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        if (text == "—")
            ToolTip.SetTip(pill, T("Способ оплаты сервер NurCRM в приходе не хранит — он виден у закупок, проведённых здесь, и в карточке поставщика.",
                "NurCRM сервери төлөм ыкмасын киришке сактабайт — ал бул жерде өткөрүлгөн сатып алууларда жана жеткирүүчүнүн карточкасында көрүнөт.",
                "The NurCRM server doesn't store the payment method in a delivery — it shows for purchases made here and in the supplier card.",
                "NurCRM sunucusu ödeme yöntemini girişte saklamaz — burada yapılan alımlarda ve tedarikçi kartında görünür.",
                "NurCRM serveri to'lov usulini kirimda saqlamaydi — u shu yerda o'tkazilgan xaridlarda va yetkazib beruvchi kartasida ko'rinadi."));
        box.Children.Add(pill);
        if (sub is not null)
        {
            var s = Cell(sub, "BrushTextSoft", 12);
            s.Margin = default;
            box.Children.Add(s);
        }
        return box;
    }

    private Control OrderRow(PurchaseOrder o)
    {
        var g = DataGrid();
        Put(g, 0, Two(o.Number, o.CreatedAt.ToString("dd.MM.yyyy", Ru)));
        Put(g, 1, Two(o.SupplierName, o.Lines.Count(l => l.Qty > 0) + " " + T("поз.", "поз.", "items", "kalem", "poz."), bold: true));
        var items = Cell(o.Lines.Count(l => l.Qty > 0).ToString(CultureInfo.InvariantCulture), "BrushText", 13.5);
        items.TextAlignment = TextAlignment.Right;
        items.IsVisible = !_narrow;
        Put(g, 2, items);
        var total = Cell(MoneyText.Som(o.Total), "BrushText", 14, FontWeight.SemiBold);
        total.TextAlignment = TextAlignment.Right;
        Put(g, 3, total);
        var (text, bg, fg) = OrderState(o);
        var state = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var pill = SectionCards.Pill(this, text, bg, fg);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        state.Children.Add(pill);
        if (o.State == "partial")
        {
            var left = Cell(T("осталось принять ", "кабыл алуу калды ", "left to receive ", "kalan ", "qoldi ") + o.Lines.Count(l => l.Left > 0.0005) + " " + T("поз.", "поз.", "items", "kalem", "poz."), "BrushTextSoft", 12);
            left.Margin = default;
            state.Children.Add(left);
        }
        Put(g, 4, state);
        Button? action = null;
        if (o.IsOpen)
        {
            action = RowAction(T("Принять", "Кабыл алуу", "Receive", "Kabul et", "Qabul qilish"), T("Принять товар по заказу", "Буйрутма боюнча товар кабыл алуу", "Receive goods for this order", "Siparişe göre mal kabul et", "Buyurtma bo'yicha tovar qabul qilish"));
            action.Click += (_, _) => OpenReceive(ReceivePrefill.FromOrder(o));
        }
        return RowShell(g, $"{o.Number} {o.SupplierName} — {text}", () => _ = ShowOrderAsync(o), action);
    }

    internal static (string Text, string Bg, string Fg) OrderState(PurchaseOrder o) => o.State switch
    {
        "sent" => (T("Отправлен", "Жөнөтүлдү", "Sent", "Gönderildi", "Yuborildi"), "BrushPanelSoft", "BrushText"),
        "partial" => (T("Принят частично", "Жарым-жартылай кабыл алынды", "Partly received", "Kısmen kabul edildi", "Qisman qabul qilindi"), "BrushWarningSoft", "BrushText"),
        "received" => (T("Принят", "Кабыл алынды", "Received", "Kabul edildi", "Qabul qilindi"), "BrushSuccessSoft", "BrushSuccess"),
        "cancelled" => (T("Отменён", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"), "BrushPanelSoft", "BrushTextSoft"),
        _ => (T("Черновик", "Долбоор", "Draft", "Taslak", "Qoralama"), "BrushPanelSoft", "BrushTextSoft"),
    };

    private Control ReturnRow(SupplierReturnInfo r)
    {
        var g = DataGrid();
        Put(g, 0, Two(r.CreatedAt?.ToString("dd.MM.yyyy", Ru) ?? "—", r.CreatedAt?.ToString("HH:mm", Ru) ?? ""));
        Put(g, 1, Two(r.SupplierName, string.Join(", ", r.Lines.Take(2).Select(l => l.ProductName)) + (r.Lines.Count > 2 ? ", …" : ""), bold: true));
        var items = Cell(r.Lines.Count.ToString(CultureInfo.InvariantCulture), "BrushText", 13.5);
        items.TextAlignment = TextAlignment.Right;
        items.IsVisible = !_narrow;
        Put(g, 2, items);
        var total = Cell(MoneyText.Som(r.Total), "BrushText", 14, FontWeight.SemiBold);
        total.TextAlignment = TextAlignment.Right;
        Put(g, 3, total);
        var money = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var pill = SectionCards.Pill(this, SupplierReturnsWindow.CompensationName(r.Compensation),
            r.Compensation == "cash" ? "BrushSuccessSoft" : r.Compensation == "debt_offset" ? "BrushWarningSoft" : "BrushPanelSoft",
            r.Compensation == "cash" ? "BrushSuccess" : "BrushText");
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        money.Children.Add(pill);
        var reason = Cell(SupplierReturnsWindow.ReasonName(r.Reason) + (r.Comment.Length > 0 ? " · " + r.Comment : ""), "BrushTextSoft", 12);
        reason.Margin = default;
        reason.MaxLines = 1;
        reason.TextTrimming = TextTrimming.CharacterEllipsis;
        money.Children.Add(reason);
        Put(g, 4, money);
        return RowShell(g, $"{r.SupplierName}, {r.CreatedAt:dd.MM.yyyy}, {MoneyText.Som(r.Total)}", () => _ = ShowReturnAsync(r), null);
    }

    private StackPanel Two(string main, string sub, bool bold = false)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var a = Cell(main, "BrushText", bold ? 14 : 13.5, bold ? FontWeight.SemiBold : FontWeight.Normal);
        a.Margin = default;
        a.MaxLines = 2;
        a.TextTrimming = TextTrimming.CharacterEllipsis;
        p.Children.Add(a);
        if (sub.Length > 0)
        {
            var b = Cell(sub, "BrushTextSoft", 12);
            b.Margin = default;
            b.MaxLines = 1;
            b.TextTrimming = TextTrimming.CharacterEllipsis;
            p.Children.Add(b);
        }
        return p;
    }

    private static void Put(Grid g, int col, Control c)
    {
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    internal TextBlock Cell(string text, string brush, double size, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        t.Classes.Add("num");
        Use(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    // ------------------------------------------------------------------ подробности, возврат

    private async Task ShowReceiptAsync(SupplierReceipt r)
    {
        var detail = r;
        if (r.Lines.Count == 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                detail = await _svc.GetReceiptAsync(r.Id, cts.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Закупки: приход {r.Id} не загружен ({ex.Message}).", "WARNING");
            }
        }
        var dialog = new PurchaseReceiptDialog(detail, _notes.GetValueOrDefault(r.Id), _debtKeys.Contains(SupplierDebtRecorder.MarkerForReceipt(r.Id)), SupplierById(detail.SupplierId));
        await dialog.ShowDialog(this).ConfigureAwait(true);
        switch (dialog.Action)
        {
            case "return":
                await ReturnAsync(detail.SupplierId, detail.Id).ConfigureAwait(true);
                break;
            case "repeat":
                OpenReceive(ReceivePrefill.FromReceipt(detail));
                break;
        }
    }

    private async Task ShowOrderAsync(PurchaseOrder o)
    {
        var dialog = new PurchaseOrderDialog(o, SupplierById(o.SupplierId));
        await dialog.ShowDialog(this).ConfigureAwait(true);
        switch (dialog.Action)
        {
            case "receive":
                OpenReceive(ReceivePrefill.FromOrder(o));
                break;
            case "edit":
                OpenRecommend(o);
                break;
            case "cancel":
                PurchasesStore.SetOrderStatus(o.Id, "cancelled");
                PosLogger.Log($"Закупки: заказ {o.Number} отменён.", "INFO");
                break;
            case "delete":
                PurchasesStore.DeleteOrder(o.Id);
                PosLogger.Log($"Закупки: заказ {o.Number} удалён.", "INFO");
                break;
        }
        BuildTabs();
        Render();
    }

    private async Task ShowReturnAsync(SupplierReturnInfo r)
    {
        var lines = r.Lines.Select(l => (l.ProductName, Q(l.Qty) + " " + (l.Unit.Length > 0 ? l.Unit : "шт"), MoneyText.Som(l.PurchasePrice), MoneyText.Som(l.Total))).ToList();
        var info = new StackPanel { Spacing = 6 };
        info.Children.Add(Cell(SupplierReturnsWindow.CompensationName(r.Compensation) + " · " + SupplierReturnsWindow.ReasonName(r.Reason), "BrushText", 13.5));
        if (r.Comment.Length > 0)
            info.Children.Add(Cell(r.Comment, "BrushTextSoft", 13));
        info.Children.Add(SectionCards.LinesTable(this, lines, T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
            T("Цена", "Баасы", "Price", "Fiyat", "Narx"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa")));
        var w = new Window { Title = T("Возврат поставщику", "Жеткирүүчүгө кайтаруу", "Return to supplier", "Tedarikçiye iade", "Yetkazib beruvchiga qaytarish") };
        SectionCards.AsModal(w, 620);
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => w.Close();
        w.Content = SectionCards.Shell(w.Title, $"{r.SupplierName} · {r.CreatedAt:dd.MM.yyyy HH:mm} · {MoneyText.Som(r.Total)}", info, SectionCards.Buttons(close));
        await w.ShowDialog(this).ConfigureAwait(true);
    }

    private SupplierInfo? SupplierById(string? id) => string.IsNullOrEmpty(id) ? null : _filterSupplier.All.FirstOrDefault(s => s.Id == id);

    /// <summary>«Вернуть» в строке / «Возврат поставщику» наверху — окно возврата «Возвратов поставщикам» (с этим поставщиком и приходом).</summary>
    private async Task ReturnAsync(string? supplierId, string? receiptId)
    {
        var dialog = new SupplierReturnDialog(supplierId, receiptId);
        if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
        {
            PosLogger.Log($"Закупки: оформлен возврат поставщику {supplierId} (приход {receiptId ?? "—"}).", "INFO");
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ страницы поверх списка

    private void ShowSub(Control page)
    {
        // Состояние новой страницы уже задано (OpenReceive/OpenRecommend) — здесь только смена страницы.
        if (_sub is not null)
            _host.Children.Remove(_sub);
        _sub = page;
        _main.IsVisible = false;
        _host.Children.Add(page);
    }

    private void CloseSub()
    {
        if (_sub is not null)
            _host.Children.Remove(_sub);
        _sub = null;
        _receive = null;
        _recommend = null;
        _main.IsVisible = true;
        BuildTabs();
        Render();
    }

    /// <summary>Отступы страницы — как у списка (в программе владельца — поля раздела).</summary>
    private Thickness PageMargin => _main.Margin;

    // ------------------------------------------------------------------ стили строк

    /// <summary>Строка-кнопка во всю ширину — как строка «Поставщиков».</summary>
    private void AddRowStyles()
    {
        static Style S(Func<Selector?, Selector> sel, params (AvaloniaProperty P, object V)[] setters)
        {
            var st = new Style(sel);
            foreach (var (p, v) in setters)
                st.Setters.Add(new Setter(p, v));
            return st;
        }
        Styles.Add(S(x => x.OfType<Button>().Class("pur-row"),
            (Button.BackgroundProperty, Brushes.Transparent), (Button.BorderThicknessProperty, new Thickness(0)), (Button.CornerRadiusProperty, new CornerRadius(0)),
            (Button.PaddingProperty, new Thickness(16, 9, 4, 9)), (Button.MinHeightProperty, 56.0),
            (Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch), (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center), (Button.CursorProperty, new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))));
        Styles.Add(S(x => x.OfType<Button>().Class("pur-row").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, Brushes.Transparent)));
        Styles.Add(S(x => x.OfType<Button>().Class("pur-row").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelSoft"))));
        Styles.Add(S(x => x.OfType<Button>().Class("pur-row").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelPressed"))));
        Styles.Add(S(x => x.OfType<Button>().Class("pur-row").Class(":focus-visible").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("BrushText")), (ContentPresenter.BorderThicknessProperty, new Thickness(2))));
    }

    internal void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
