using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, просьба владельца по примеру чужой кассы (вкладка «Покупки»: строки по ВСЕМ документам, фильтры «период / поставщик», «Обновить», «Экспорт»,
/// внизу итог) и по макету design/Invoice-Doc («Реестр», Документы | Позиции). Вкладка «Покупки» раздела «Закупки»:
///  • «Документы» — приходы: № документа и дата, поставщик, № накладной поставщика, принял, позиций, штук, сумма, средняя наценка, оплата, статус («Исправлен vN», «С возвратом»);
///  • «Позиции» — строки всех документов: код, штрихкод, наименование, ед., количество, цена покупки (↑ — выше прошлой закупки), сумма, наценка %, цена продажи, документ, дата, поставщик, пользователь;
///  • отбор: период (Неделя/Месяц/3 месяца/Всё/Даты…), поставщик, склад (когда сервер его отдаёт), статус, поиск (номер, накладная, поставщик, товар; ё=е, раскладка, транслит);
///  • итог по фильтру закреплён внизу, шапка — над списком; список виртуализирован (10 000 строк без задержек), данные грузятся страницами по 100 с индикатором
///    «Загружаем… N из M», копия в purchases\registry-*.json — без связи показываются «данные от ЧЧ:ММ»;
///  • двойной щелчок по документу/позиции открывает накладную (<see cref="InvoiceHook"/> — форма накладной; пока её нет, окно «Позиции прихода» с подсвеченной строкой);
///  • экспорт Excel/CSV (с итогом) и печать списка; цены, закупка и поставщик — только при праве RevisionShowPurchase.
/// Загрузчики подменяются в стенде проверки.</summary>
internal sealed class PurchasesRegistryView : UserControl
{
    public delegate Task<RegistryLoad> Loader(DateTime? from, DateTime? to, Action<IReadOnlyList<SupplierReceipt>, int, int> onPage, CancellationToken ct);

    public delegate Task<Dictionary<string, SupplierReceipt>> LinesLoader(IReadOnlyList<SupplierReceipt> receipts, Action<int, int> progress, CancellationToken ct);

    /// <summary>Точка подключения формы накладной (InvoiceDocumentWindow): (окно-владелец, id прихода, id товара для подсветки) → true, если форма открыта.
    /// Пока null — реестр открывает существующее окно «Позиции прихода».</summary>
    public static Func<Window, string, string?, Task<bool>>? InvoiceHook { get; set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly Loader _loader;
    private readonly LinesLoader _linesLoader;
    private readonly bool _money;

    private readonly StackPanel _modeSeg = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _periodSeg = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly PurchasesSupplierPicker _supplier;
    private readonly ComboBox _warehouse;
    private readonly TextBox _search;
    private readonly WrapPanel _toolbar = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _info = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _progress = new() { Height = 3, MinHeight = 3, IsVisible = false, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Grid _head = new();
    private readonly ListBox _list = new() { Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly StackPanel _emptyHost = new() { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 40, 24, 0), IsVisible = false };
    private readonly TextBlock _empty = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _showAll;
    private readonly Border _totalsBar = new();
    private readonly Grid _totalsGrid = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private string _mode = "docs";
    private string _period = "month";
    private DateTime? _customFrom;
    private DateTime? _customTo;
    private RegistryStatus? _status;
    private string? _supplierId;
    private string? _warehouseName;

    private readonly List<SupplierReceipt> _receipts = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private List<RegistryDoc> _docs = new();
    private List<RegistryDoc> _shownDocs = new();
    private List<RegistryLine> _shownLines = new();
    private RegistryTotals _totals;
    private RegistryContext _ctx = new();
    private bool _hasWarehouse;
    private bool _dirty;
    private bool _loaded;
    private bool _loading;
    private bool _ensuring;
    private int _ensureDone;
    private int _ensureTotal;
    private int _loadedCount;
    private int _serverTotal;
    private bool _offline;
    private bool _loadFailed;
    private readonly Button _retry;
    private bool _truncated;
    private DateTime _loadedAt;
    private string? _note;
    private CancellationTokenSource? _cts;
    private double _width;
    private double _height = 600;
    private List<ColSpec> _visible = new();
    private string _visibleKey = "";

    private sealed record ColSpec(string Key, string Title, double Width, int Priority, bool Right = false, bool Star = false, double Min = 0);

    /// <summary>Открыть документ (и строку, если щёлкнули по позиции). Подменяется окном «Закупки» и стендом.</summary>
    public Func<RegistryDoc, RegistryLine?, Task> OpenDocument { get; set; } = (_, _) => Task.CompletedTask;

    public PurchasesRegistryView(SupplierApiService svc, bool showMoney, Loader loader, LinesLoader linesLoader)
    {
        _money = showMoney;
        _loader = loader;
        _linesLoader = linesLoader;

        Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class("rg-item"))
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, 0.0),
                new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent),
            },
        });
        foreach (var state in new[] { ":selected", ":pointerover" })
            Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class("rg-item").Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelSoft")) },
            });

        Use(_info, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_empty, TextBlock.ForegroundProperty, "BrushTextSoft");

        _supplier = new PurchasesSupplierPicker(svc) { AllowAll = true, ShowAdd = false, MinWidth = 210, Width = 230, Margin = new Thickness(0, 0, 8, 8) };
        _supplier.Changed += s =>
        {
            _supplierId = s?.Id;
            Refresh();
        };
        _supplier.IsVisible = _money; // 2026-10-30: поставщик — по праву на закупочные цены (RevisionShowPurchase)

        _warehouse = new ComboBox { Height = 44, MinWidth = 150, Margin = new Thickness(0, 0, 8, 8), IsVisible = false, CornerRadius = new CornerRadius(10) };
        Avalonia.Automation.AutomationProperties.SetName(_warehouse, T("Склад", "Склад", "Warehouse", "Depo", "Ombor"));
        _warehouse.SelectionChanged += (_, _) =>
        {
            if (_warehouse.SelectedIndex < 0)
                return;
            _warehouseName = _warehouse.SelectedIndex == 0 ? null : _warehouse.SelectedItem as string;
            Refresh();
        };

        _search = UiKit.Search(this, T("Номер, поставщик, накладная, товар…", "Номер, жеткирүүчү, накладной, товар…", "Number, supplier, invoice, product…", "Numara, tedarikçi, fatura, ürün…", "Raqam, yetkazib beruvchi, yuk xati, mahsulot…"), 44);
        _search.MinWidth = 240;
        _search.Width = 300;
        _search.Margin = new Thickness(0, 0, 8, 8);
        Avalonia.Automation.AutomationProperties.SetName(_search, T("Поиск по реестру закупок (Ctrl+F)", "Сатып алуулар реестринен издөө (Ctrl+F)", "Search the purchases registry (Ctrl+F)", "Alış kaydında ara (Ctrl+F)", "Xaridlar reestridan qidirish (Ctrl+F)"));
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Refresh();
        };
        _search.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start(); // 2026-10-30: 10 000 строк — ищем после паузы в наборе, а не на каждую букву
        };

        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Height = 44;
        refresh.Margin = new Thickness(0, 0, 8, 8);
        Avalonia.Automation.AutomationProperties.SetName(refresh, T("Обновить реестр закупок", "Сатып алуулар реестрин жаңыртуу", "Refresh the purchases registry", "Alış kaydını yenile", "Xaridlar reestrini yangilash"));
        refresh.Click += (_, _) => Reload();

        var export = UiKit.Ghost(this, T("Экспорт ▾", "Экспорт ▾", "Export ▾", "Dışa aktar ▾", "Eksport ▾"));
        export.Height = 44;
        export.Margin = new Thickness(0, 0, 8, 8);
        Avalonia.Automation.AutomationProperties.SetName(export, T("Экспорт реестра закупок", "Сатып алуулар реестрин экспорттоо", "Export the purchases registry", "Alış kaydını dışa aktar", "Xaridlar reestrini eksport qilish"));
        var excelItem = new MenuItem { Header = T("Excel (.xlsx) с итогом", "Excel (.xlsx) жыйынтыгы менен", "Excel (.xlsx) with the total", "Toplamlı Excel (.xlsx)", "Jami bilan Excel (.xlsx)") };
        excelItem.Click += async (_, _) => await ExportAsync(csv: false).ConfigureAwait(true);
        var csvItem = new MenuItem { Header = T("CSV (.csv) с итогом", "CSV (.csv) жыйынтыгы менен", "CSV (.csv) with the total", "Toplamlı CSV (.csv)", "Jami bilan CSV (.csv)") };
        csvItem.Click += async (_, _) => await ExportAsync(csv: true).ConfigureAwait(true);
        export.Flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ItemsSource = new[] { excelItem, csvItem } };

        var print = UiKit.Ghost(this, T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"));
        print.Height = 44;
        print.Margin = new Thickness(0, 0, 8, 8);
        Avalonia.Automation.AutomationProperties.SetName(print, T("Печать списка закупок", "Сатып алуулар тизмесин басып чыгаруу", "Print the purchases list", "Alış listesini yazdır", "Xaridlar ro'yxatini chop etish"));
        print.Click += async (_, _) => await PrintAsync().ConfigureAwait(true);

        var modeBox = UiKit.Segment(_modeSeg);
        modeBox.Margin = new Thickness(0, 0, 8, 8);
        modeBox.VerticalAlignment = VerticalAlignment.Top;
        var periodBox = UiKit.Segment(_periodSeg);
        periodBox.Margin = new Thickness(0, 0, 8, 8);
        periodBox.VerticalAlignment = VerticalAlignment.Top;
        _chips.Margin = new Thickness(0, 0, 8, 8);
        _chips.Spacing = 6;
        BuildMode();
        BuildPeriods();

        _toolbar.Children.Add(modeBox);
        _toolbar.Children.Add(periodBox);
        _toolbar.Children.Add(_supplier);
        _toolbar.Children.Add(_warehouse);
        _toolbar.Children.Add(_search);
        _toolbar.Children.Add(_chips);
        _toolbar.Children.Add(refresh);
        _toolbar.Children.Add(export);
        _toolbar.Children.Add(print);

        _showAll = UiKit.Ghost(this, T("Показать за всё время", "Бардык убакытка көрсөтүү", "Show all time", "Tüm zamanları göster", "Butun davr uchun ko'rsatish"));
        _showAll.Height = 40;
        _showAll.HorizontalAlignment = HorizontalAlignment.Center;
        _showAll.IsVisible = false;
        // 2026-10-31: «Повторить» для пустого списка после ошибки загрузки (раньше писали «Закупок за этот период нет»).
        _retry = UiKit.Ghost(this, T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"));
        _retry.Height = 40;
        _retry.HorizontalAlignment = HorizontalAlignment.Center;
        _retry.IsVisible = false;
        _retry.Click += (_, _) => Reload();
        _showAll.Click += (_, _) =>
        {
            _period = "all";
            BuildPeriods();
            Reload();
        };
        _emptyHost.Children.Add(_empty);
        _emptyHost.Children.Add(_showAll);
        _emptyHost.Children.Add(_retry);

        _list.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            RegistryDoc d => DocRow(d),
            RegistryLine l => LineRow(l),
            _ => new Panel(),
        });
        _list.ContainerPrepared += (_, e) => e.Container.Classes.Add("rg-item");
        _list.SelectionMode = SelectionMode.Single;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is { } sel)
            {
                e.Handled = true;
                _ = OpenItemAsync(sel);
            }
        };

        var headBox = new Border { Padding = new Thickness(10, 9, 14, 9), BorderThickness = new Thickness(0, 0, 0, 1), Child = _head };
        Use(headBox, Border.BackgroundProperty, "BrushPanelSoft");
        Use(headBox, Border.BorderBrushProperty, "BrushBorder");
        var listHost = new Grid();
        listHost.Children.Add(_list);
        listHost.Children.Add(_emptyHost);
        var listGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listGrid.Children.Add(headBox);
        Grid.SetRow(listHost, 1);
        listGrid.Children.Add(listHost);
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = listGrid, Margin = new Thickness(0, 0, 0, 8) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");

        _totalsBar.BorderThickness = new Thickness(1);
        _totalsBar.CornerRadius = new CornerRadius(12);
        _totalsBar.Padding = new Thickness(14, 8);
        _totalsBar.Child = _totalsGrid;
        Use(_totalsBar, Border.BackgroundProperty, "BrushWarningSoft");
        Use(_totalsBar, Border.BorderBrushProperty, "BrushWarning");

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        var infoRow = new StackPanel { Children = { _progress, _info }, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(_toolbar, 0);
        Grid.SetRow(infoRow, 1);
        Grid.SetRow(card, 3);
        Grid.SetRow(_totalsBar, 4);
        root.Children.Add(_toolbar);
        root.Children.Add(infoRow);
        root.Children.Add(card);
        root.Children.Add(_totalsBar);
        Content = root;

        listHost.SizeChanged += (_, e) =>
        {
            _width = e.NewSize.Width;
            _height = e.NewSize.Height;
            if (LayoutColumns())
                RebuildList(true);
        };
        SizeChanged += (_, e) => ApplyDensity(e.NewSize.Height);
        _tick.Tick += (_, _) =>
        {
            if (!_dirty)
                return;
            _dirty = false;
            Refresh();
        };
        AttachedToVisualTree += (_, _) => _tick.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _tick.Stop();
            _cts?.Cancel();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                _search.Focus();
                _search.SelectAll();
            }
        };
        RenderTotals();
    }

    // 2026-10-31: источник ресурса — сам элемент, а не реестр: подписка на реестре держала каждую построенную строку списка (утечка при прокрутке и перезагрузке).
    private static void Use(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is StyledElement el)
            target.Bind(property, el.GetResourceObservable(key));
    }

    // ------------------------------------------------------------------ жизненный цикл

    /// <summary>Первый показ вкладки — загрузить; повторные — ничего (кнопка «Обновить» грузит заново).</summary>
    public void Activate()
    {
        if (_loaded)
            return;
        _loaded = true;
        StartLoad();
    }

    public void Reload() => StartLoad();

    /// <summary>Индекс номеров документов сверился — переписать номера в списке.</summary>
    public void RefreshNumbers()
    {
        PurchasesRegistry.RefreshNumbers(_docs);
        Refresh();
    }

    /// <summary>Стенд проверки: показать готовый набор приходов без загрузки.</summary>
    public void ShowReceipts(IEnumerable<SupplierReceipt> receipts, bool offline = false, DateTime? loadedAt = null)
    {
        _loaded = true;
        _cts?.Cancel();
        PrepareContext();
        _receipts.Clear();
        _seen.Clear();
        var list = receipts.ToList();
        foreach (var r in list)
            _seen.Add(r.Id);
        _receipts.AddRange(list);
        _docs = PurchasesRegistry.Build(list, _ctx);
        _loading = false;
        _offline = offline;
        _loadedAt = loadedAt ?? DateTime.Now;
        _loadedCount = _serverTotal = list.Count;
        Refresh();
    }

    private void PrepareContext()
    {
        IReadOnlyDictionary<string, PurchaseReceiptNote> notes = new Dictionary<string, PurchaseReceiptNote>();
        IReadOnlySet<string> debts = new HashSet<string>();
        IReadOnlyDictionary<string, ReceiptMarkupDoc> markups = new Dictionary<string, ReceiptMarkupDoc>();
        try
        {
            notes = PurchasesStore.ReceiptNotes();
            debts = SupplierDebtRecorder.DoneKeys();
            markups = ReceiptMarkupLog.Snapshot();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: журналы закупок не прочитаны ({ex.Message}).", "WARNING");
        }
        var catalog = new Dictionary<string, RegistryCatalogInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in CatalogCacheService.Products.ToArray())
                catalog[p.Id] = new RegistryCatalogInfo(p.Barcode ?? "", p.ProductCode ?? p.Article ?? "", p.PriceValue);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // каталог обновлялся в этот момент — штрихкоды возьмём из прихода
        }
        _ctx = new RegistryContext
        {
            Markups = markups,
            Catalog = id => catalog.TryGetValue(id, out var c) ? c : null,
            Pay = r => PayOf(r, notes, debts),
        };
    }

    /// <summary>Оплата прихода — как значок «Оплата» истории: подпись «Закупок» этого ПК → долг по журналу → «—» (способ оплаты сервер не отдаёт).</summary>
    private static (string Text, string Kind) PayOf(SupplierReceipt r, IReadOnlyDictionary<string, PurchaseReceiptNote> notes, IReadOnlySet<string> debts)
    {
        if (notes.TryGetValue(r.Id, out var n))
        {
            var (text, kind) = n.PayKind switch
            {
                "cash" => (T("Оплачено", "Төлөндү", "Paid", "Ödendi", "To'landi"), "cash"),
                "drawer" => (T("Из ящика", "Кассадан", "From drawer", "Kasadan", "Kassadan"), "drawer"),
                "partial" => (T("Частично", "Жарым-жартылай", "Partly", "Kısmen", "Qisman"), "partial"),
                _ => (T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), "debt"),
            };
            return (n.MoneyProblem ? "⚠ " + text : text, kind);
        }
        return debts.Contains(SupplierDebtRecorder.MarkerForReceipt(r.Id)) ? (T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), "debt") : ("—", "");
    }

    // ------------------------------------------------------------------ загрузка

    private (DateTime? From, DateTime? To) PeriodBounds() => _period switch
    {
        "week" => (PeriodRange.WeekFrom(DateTime.Today), null),
        "month" => (DateTime.Today.AddDays(-29), null),
        "quarter" => (DateTime.Today.AddDays(-89), null),
        "custom" => (_customFrom, _customTo),
        _ => (null, null),
    };

    private void StartLoad()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        PrepareContext();
        _receipts.Clear();
        _seen.Clear();
        _docs = new List<RegistryDoc>();
        _loading = true;
        _ensuring = false;
        _offline = false;
        _truncated = false;
        _loadFailed = false;
        _note = null;
        _loadedCount = 0;
        _serverTotal = 0;
        Refresh();
        var (from, to) = PeriodBounds();
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _loader(from, to, (fresh, n, total) => Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested)
                        return;
                    Append(fresh);
                    _loadedCount = n;
                    _serverTotal = total;
                    _dirty = true;
                }), cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested)
                        FinishLoad(result);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Реестр закупок: загрузка не удалась ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested)
                        return;
                    _loading = false;
                    _loadFailed = true;
                    _note = T("Реестр не загрузился: ", "Реестр жүктөлгөн жок: ", "The registry didn't load: ", "Kayıt yüklenemedi: ", "Reestr yuklanmadi: ") + ex.Message;
                    Refresh();
                });
            }
        });
    }

    private void Append(IReadOnlyList<SupplierReceipt> fresh)
    {
        var add = fresh.Where(r => _seen.Add(r.Id)).ToList();
        if (add.Count == 0)
            return;
        _receipts.AddRange(add);
        _docs.AddRange(PurchasesRegistry.Build(add, _ctx));
    }

    private void FinishLoad(RegistryLoad result)
    {
        _loading = false;
        _offline = result.Offline;
        _truncated = result.Truncated;
        _loadedAt = result.LoadedAt;
        _serverTotal = Math.Max(result.Total, _loadedCount);
        if (result.Offline)
        {
            // Копия прошлой загрузки заменяет то, что успело прийти до обрыва связи.
            _receipts.Clear();
            _seen.Clear();
            _docs = new List<RegistryDoc>();
            Append(result.Receipts);
            _loadedCount = _receipts.Count;
        }
        Refresh();
        if (_mode == "lines")
            _ = EnsureLinesAsync();
    }

    /// <summary>У части приходов список не отдал строки — догружаем их по одному (4 запроса одновременно) с индикатором «Загружаем позиции… N из M».</summary>
    private async Task EnsureLinesAsync()
    {
        if (_ensuring || _loading || _offline)
            return;
        var missing = _receipts.Where(r => r.Lines.Count == 0 && r.ItemsCount > 0).ToList();
        if (missing.Count == 0)
            return;
        _ensuring = true;
        _ensureDone = 0;
        _ensureTotal = missing.Count;
        var cts = _cts ??= new CancellationTokenSource();
        UpdateInfo();
        try
        {
            var loaded = await Task.Run(() => _linesLoader(missing, (d, t) => Dispatcher.UIThread.Post(() =>
            {
                _ensureDone = d;
                _ensureTotal = t;
                UpdateInfo();
            }), cts.Token)).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;
            var byId = _docs.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, detail) in loaded)
            {
                var i = _receipts.FindIndex(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (i >= 0)
                    _receipts[i] = detail;
                if (byId.TryGetValue(id, out var doc))
                    PurchasesRegistry.Fill(doc, detail, _ctx);
            }
            if (_money && !_offline && !_truncated)
            {
                var snapshot = _receipts.ToList();
                var (from, to) = PeriodBounds();
                var total = _serverTotal;
                _ = Task.Run(() => PurchasesRegistryLoader.Save(snapshot, total, from, to, null));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: строки документов не догружены ({ex.Message}).", "WARNING");
        }
        finally
        {
            _ensuring = false;
            Refresh();
        }
    }

    // ------------------------------------------------------------------ отбор и показ

    private RegistryFilter BuildFilter(bool withStatus = true)
    {
        var (from, to) = PeriodBounds();
        return new RegistryFilter
        {
            From = from, To = to, SupplierId = _supplierId, Warehouse = _warehouseName, Status = withStatus ? _status : null,
            Query = (_search.Text ?? "").Trim(),
        };
    }

    private void Refresh()
    {
        _docs = _docs.OrderByDescending(d => d.At ?? DateTime.MinValue).ToList();
        PurchasesRegistry.MarkRaised(_docs);
        _hasWarehouse = _docs.Any(d => d.Warehouse.Length > 0);
        SyncPickers();
        var f = BuildFilter();
        if (_mode == "docs")
        {
            _shownDocs = PurchasesRegistry.FilterDocs(_docs, f);
            _shownLines = new List<RegistryLine>();
            _totals = PurchasesRegistry.TotalsOfDocs(_shownDocs);
        }
        else
        {
            _shownLines = PurchasesRegistry.FilterLines(_docs, f);
            _shownDocs = _shownLines.Select(l => l.Doc).Distinct().ToList();
            _totals = PurchasesRegistry.TotalsOfLines(_shownLines);
        }
        BuildChips();
        RebuildList(LayoutColumns());
        RenderTotals();
        UpdateInfo();
    }

    private void SyncPickers()
    {
        var suppliers = _docs.Where(d => d.SupplierId.Length > 0).GroupBy(d => d.SupplierId)
            .Select(g => new SupplierInfo(g.Key, g.First().SupplierName, "", "", "", "", "", "", "", "", null)).ToList();
        if (_supplier.All.Count != suppliers.Count)
            _supplier.SetSuppliers(suppliers);
        _warehouse.IsVisible = _hasWarehouse;
        if (_hasWarehouse)
        {
            var names = new List<string> { T("Все склады", "Бардык склад", "All warehouses", "Tüm depolar", "Barcha omborlar") };
            names.AddRange(_docs.Select(d => d.Warehouse).Where(w => w.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase));
            var current = _warehouse.ItemsSource as List<string>;
            if (current is null || !current.SequenceEqual(names))
            {
                _warehouse.ItemsSource = names;
                _warehouse.SelectedIndex = _warehouseName is null ? 0 : Math.Max(0, names.IndexOf(_warehouseName));
            }
        }
    }

    private void RebuildList(bool columnsChanged = false)
    {
        var items = _mode == "docs" ? _shownDocs.Cast<object>().ToList() : _shownLines.Cast<object>().ToList();
        BuildHead();
        // 2026-10-30: данные приходят страницами и список обновляется на ходу — прокрутка не сбрасывается; колонки сменились (ширина, режим) — строки строим заново.
        var scroll = _list.Scroll as ScrollViewer;
        var offset = scroll?.Offset ?? default;
        if (columnsChanged)
            _list.ItemsSource = null;
        _list.ItemsSource = items;
        if (!columnsChanged && scroll is not null && offset.Y > 0)
            Dispatcher.UIThread.Post(() => scroll.Offset = offset, DispatcherPriority.Loaded);
        var allLoaded = !_loading && !_ensuring;
        _retry.IsVisible = false;
        // 2026-10-31: пока первая страница не пришла, вместо пустого белого поля — «Загружаем закупки…» (в живом обходе вкладка выглядела пустой).
        _emptyHost.IsVisible = items.Count == 0 && (allLoaded || (_loading && _docs.Count == 0));
        if (items.Count == 0 && !allLoaded && _loading && _docs.Count == 0)
        {
            _empty.Text = T("Загружаем закупки…", "Сатып алуулар жүктөлүүдө…", "Loading purchases…", "Alışlar yükleniyor…", "Xaridlar yuklanmoqda…");
            _showAll.IsVisible = false;
        }
        else if (items.Count == 0 && allLoaded)
        {
            var (from, _) = PeriodBounds();
            var q = _search.Text?.Trim() ?? "";
            if (_docs.Count == 0 && (_loadFailed || _offline))
            {
                _empty.Text = T("Закупки не загрузились: нет связи с сервером. Нажмите «Повторить».", "Сатып алуулар жүктөлгөн жок: сервер менен байланыш жок. «Кайталоо» баскычын басыңыз.",
                    "Purchases didn't load: no connection to the server. Press “Retry”.", "Alışlar yüklenemedi: sunucuyla bağlantı yok. «Tekrar dene»ye basın.", "Xaridlar yuklanmadi: server bilan aloqa yo'q. «Qayta urinish»ni bosing.");
                _retry.IsVisible = true;
            }
            else if (_docs.Count == 0)
                _empty.Text = T("Закупок за этот период нет. Приход появится здесь после «＋ Приём товара» или загрузки накладной.",
                    "Бул мезгилде сатып алуулар жок. Кирүү «＋ Товар кабыл алуудан» же накладной жүктөгөндөн кийин ушул жерде пайда болот.",
                    "No purchases in this period. A delivery appears here after “＋ Receive goods” or loading an invoice.",
                    "Bu dönemde alış yok. Giriş, «＋ Mal kabul» veya fatura yüklemesinden sonra burada görünür.",
                    "Bu davrda xaridlar yo'q. Kirim «＋ Tovar qabul qilish» yoki yuk xati yuklangandan keyin shu yerda paydo bo'ladi.");
            else if (_mode == "lines" && _docs.All(d => d.Lines.Count == 0))
                _empty.Text = T("У документов нет строк (сервер их не отдал) — откройте документ двойным щелчком во вкладке «Документы».",
                    "Документтерде саптар жок (сервер бербеди) — «Документтер» өтмөгүндө эки жолу басып ачыңыз.",
                    "The documents have no lines (the server did not return them) — open a document by double click in the “Documents” tab.",
                    "Belgelerde satır yok (sunucu vermedi) — «Belgeler» sekmesinde çift tıklayarak açın.",
                    "Hujjatlarda qatorlar yo'q (server bermadi) — «Hujjatlar» bo'limida ikki marta bosib oching.");
            else
                _empty.Text = q.Length > 0
                    ? T($"Ничего не найдено по «{q}». Поиск работает вместе с периодом, поставщиком и статусом.", $"«{q}» боюнча эч нерсе табылган жок. Издөө мөөнөт, жеткирүүчү жана абал менен бирге иштейт.",
                        $"Nothing found for “{q}”. The search works together with the period, supplier and status.", $"«{q}» için sonuç yok. Arama dönem, tedarikçi ve durumla birlikte çalışır.",
                        $"«{q}» bo'yicha hech narsa topilmadi. Qidiruv davr, yetkazib beruvchi va holat bilan birga ishlaydi.")
                    : T("По этим фильтрам документов нет.", "Бул чыпкалар боюнча документтер жок.", "No documents match these filters.", "Bu filtrelere uyan belge yok.", "Bu filtrlar bo'yicha hujjat yo'q.");
            _showAll.IsVisible = from is not null && _docs.Count == 0 && !_retry.IsVisible;
        }
        else
            _showAll.IsVisible = false;
    }

    private void UpdateInfo()
    {
        var parts = new List<string>();
        var (from, to) = PeriodBounds();
        if (from is { } f)
            parts.Add(PeriodRange.Label(f, to ?? DateTime.Today));
        var all = _mode == "docs" ? _docs.Count : _docs.Sum(d => d.Lines.Count);
        var shown = _mode == "docs" ? _shownDocs.Count : _shownLines.Count;
        parts.Add(T($"Показано {N(shown)} из {N(all)}", $"Көрсөтүлдү {N(shown)} / {N(all)}", $"Showing {N(shown)} of {N(all)}", $"Gösterilen {N(shown)} / {N(all)}", $"Ko'rsatildi {N(shown)} / {N(all)}"));
        if (_loading)
            parts.Insert(0, T($"Загружаем… {N(_loadedCount)} из {N(_serverTotal)}", $"Жүктөлүүдө… {N(_loadedCount)} / {N(_serverTotal)}", $"Loading… {N(_loadedCount)} of {N(_serverTotal)}",
                $"Yükleniyor… {N(_loadedCount)} / {N(_serverTotal)}", $"Yuklanmoqda… {N(_loadedCount)} / {N(_serverTotal)}"));
        if (_ensuring)
            parts.Insert(0, T($"Загружаем позиции… {N(_ensureDone)} из {N(_ensureTotal)} документов", $"Позициялар жүктөлүүдө… {N(_ensureDone)} / {N(_ensureTotal)} документ",
                $"Loading items… {N(_ensureDone)} of {N(_ensureTotal)} documents", $"Kalemler yükleniyor… {N(_ensureDone)} / {N(_ensureTotal)} belge", $"Pozitsiyalar yuklanmoqda… {N(_ensureDone)} / {N(_ensureTotal)} hujjat"));
        if (_offline)
            parts.Add(_loadedAt > DateTime.MinValue
                ? T($"Нет связи с сервером — данные от {_loadedAt:HH:mm}", $"Сервер менен байланыш жок — маалымат {_loadedAt:HH:mm}", $"No connection — data from {_loadedAt:HH:mm}", $"Bağlantı yok — veriler {_loadedAt:HH:mm}", $"Aloqa yo'q — ma'lumot {_loadedAt:HH:mm}")
                : T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q"));
        if (_truncated)
            parts.Add(T($"просмотрены первые {PurchasesRegistryLoader.MaxPages * PurchasesRegistryLoader.PageSize:N0} документов — сузьте период",
                $"алгачкы {PurchasesRegistryLoader.MaxPages * PurchasesRegistryLoader.PageSize:N0} документ каралды — мөөнөттү кыскартыңыз",
                $"first {PurchasesRegistryLoader.MaxPages * PurchasesRegistryLoader.PageSize:N0} documents scanned — narrow the period",
                $"ilk {PurchasesRegistryLoader.MaxPages * PurchasesRegistryLoader.PageSize:N0} belge tarandı — dönemi daraltın",
                $"dastlabki {PurchasesRegistryLoader.MaxPages * PurchasesRegistryLoader.PageSize:N0} hujjat ko'rildi — davrni toraytiring"));
        var noLines = _docs.Count(d => d.LinesMissing);
        if (_mode == "lines" && !_ensuring && !_loading && noLines > 0)
            parts.Add(T($"у {noLines} документов нет загруженных позиций (нет связи) — они не вошли в список", $"{noLines} документтин позициялары жүктөлгөн эмес (байланыш жок)",
                $"{noLines} documents have no loaded items (offline)", $"{noLines} belgenin kalemleri yüklenmedi (bağlantı yok)", $"{noLines} hujjatning pozitsiyalari yuklanmagan (aloqa yo'q)"));
        if (_note is not null)
            parts.Add(_note);
        _info.Text = string.Join("  ·  ", parts);
        _progress.IsVisible = _loading || _ensuring;
        _progress.Value = _loading ? (_serverTotal > 0 ? (double)_loadedCount / _serverTotal : 0) : (_ensureTotal > 0 ? (double)_ensureDone / _ensureTotal : 0);
    }

    // ------------------------------------------------------------------ панель: режим, период, чипы

    private void BuildMode()
    {
        _modeSeg.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("docs", T("Документы", "Документтер", "Documents", "Belgeler", "Hujjatlar")),
                     ("lines", T("Позиции", "Позициялар", "Items", "Kalemler", "Pozitsiyalar")),
                 })
        {
            var seg = UiKit.Seg(text, key == _mode);
            seg.MinHeight = 44;
            seg.Click += (_, _) =>
            {
                if (_mode == key)
                    return;
                _mode = key;
                _visibleKey = "";
                BuildMode();
                Refresh();
                if (key == "lines")
                    _ = EnsureLinesAsync();
            };
            _modeSeg.Children.Add(seg);
        }
    }

    private void BuildPeriods()
    {
        _periodSeg.Children.Clear();
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
                seg.Click += (_, _) =>
                {
                    _period = key;
                    BuildPeriods();
                    Reload();
                };
            _periodSeg.Children.Add(seg);
        }
    }

    private Flyout DatesFlyout()
    {
        var fromPicker = new CalendarDatePicker { SelectedDate = _customFrom ?? DateTime.Today.AddDays(-30), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42, SelectedDateFormat = CalendarDatePickerFormat.Short };
        var toPicker = new CalendarDatePicker { SelectedDate = _customTo ?? DateTime.Today, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42, SelectedDateFormat = CalendarDatePickerFormat.Short };
        var error = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(error, TextBlock.ForegroundProperty, "BrushDanger");
        var apply = UiKit.Ghost(this, T("Применить", "Колдонуу", "Apply", "Uygula", "Qo'llash"));
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        apply.Click += (_, _) =>
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
            Reload();
        };
        flyout.Content = new StackPanel
        {
            Spacing = 8, Width = 280,
            Children = { UiKit.Label(this, T("С", "Баштап", "From", "Başlangıç", "Dan")), fromPicker, UiKit.Label(this, T("По", "Чейин", "To", "Bitiş", "Gacha")), toPicker, error, apply },
        };
        return flyout;
    }

    /// <summary>Чипы статуса с количеством («Проведены 24»): считаются по документам, прошедшим период, поставщика и склад (без поиска и статуса).</summary>
    private void BuildChips()
    {
        var f = BuildFilter(withStatus: false);
        f.Query = "";
        var basis = PurchasesRegistry.FilterDocs(_docs, f);
        _chips.Children.Clear();
        void Chip(RegistryStatus? status, string text, int count, bool always)
        {
            if (!always && count == 0 && _status != status)
                return;
            var chip = UiKit.Chip(this, text + " " + count.ToString("N0", Ru), _status == status);
            chip.Click += (_, _) =>
            {
                _status = _status == status ? null : status;
                Refresh();
            };
            Avalonia.Automation.AutomationProperties.SetName(chip, text + " " + count);
            _chips.Children.Add(chip);
        }
        Chip(RegistryStatus.Posted, T("Проведены", "Өткөрүлгөн", "Posted", "İşlenen", "O'tkazilgan"), basis.Count(d => d.Status == RegistryStatus.Posted), true);
        Chip(RegistryStatus.Edited, T("Исправлены", "Оңдолгон", "Edited", "Düzeltilen", "Tuzatilgan"), basis.Count(d => d.Status == RegistryStatus.Edited), false);
        Chip(RegistryStatus.Returned, T("С возвратом", "Кайтаруу менен", "With return", "İadeli", "Qaytarish bilan"), basis.Count(d => d.Status == RegistryStatus.Returned), true);
    }

    private void ApplyDensity(double height)
    {
        // Низкий экран кассы (1024×600): ниже поля у панели, чтобы списку осталось место.
        var low = height < 430;
        _toolbar.Margin = new Thickness(0, 0, 0, low ? -2 : 0);
    }

    // ------------------------------------------------------------------ колонки

    private IEnumerable<ColSpec> Specs()
    {
        var no = T("№", "№", "№", "№", "№");
        if (_mode == "lines")
        {
            yield return new ColSpec("no", no, 40, 5, Right: true);
            yield return new ColSpec("code", T("Код", "Код", "Code", "Kod", "Kod"), 78, 8);
            yield return new ColSpec("barcode", T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), 132, 4);
            yield return new ColSpec("name", T("Наименование", "Аталышы", "Name", "Ad", "Nomi"), 0, 1, Star: true, Min: 170);
            yield return new ColSpec("unit", T("Ед.", "Бирд.", "Unit", "Birim", "Birl."), 52, 7);
            yield return new ColSpec("qty", T("Кол-во", "Саны", "Qty", "Adet", "Soni"), 76, 2, Right: true);
            if (_money)
            {
                yield return new ColSpec("purchase", T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"), 92, 2, Right: true);
                yield return new ColSpec("sum", T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), 100, 2, Right: true);
                yield return new ColSpec("markup", T("Нац. %", "Үстөк %", "Markup %", "Kâr %", "Ust. %"), 70, 5, Right: true);
                yield return new ColSpec("sale", T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"), 90, 6, Right: true);
            }
            yield return new ColSpec("doc", T("Документ", "Документ", "Document", "Belge", "Hujjat"), 128, 3);
            yield return new ColSpec("date", T("Дата", "Күнү", "Date", "Tarih", "Sana"), 138, 4);
            if (_money)
                yield return new ColSpec("supplier", T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), 150, 5);
            if (_hasWarehouse)
                yield return new ColSpec("wh", T("Склад", "Склад", "Warehouse", "Depo", "Ombor"), 92, 9);
            yield return new ColSpec("by", T("Пользователь", "Колдонуучу", "User", "Kullanıcı", "Foydalanuvchi"), 104, 8);
            yield break;
        }
        yield return new ColSpec("numdate", T("№ / Дата", "№ / Күнү", "№ / Date", "№ / Tarih", "№ / Sana"), 150, 1);
        if (_money)
            yield return new ColSpec("supplier", T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), 0, 1, Star: true, Min: 150);
        yield return new ColSpec("supdoc", T("№ накл. пост.", "Жеткирүүчүнүн №", "Supplier inv. №", "Tedarikçi fat. №", "Yetk. yuk xati №"), 112, 4, Star: !_money, Min: !_money ? 120 : 0);
        if (_hasWarehouse)
            yield return new ColSpec("wh", T("Склад", "Склад", "Warehouse", "Depo", "Ombor"), 92, 9);
        yield return new ColSpec("by", T("Принял", "Кабыл алды", "Received by", "Kabul eden", "Qabul qildi"), 104, 6);
        yield return new ColSpec("items", T("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), 74, 3, Right: true);
        yield return new ColSpec("qty", T("Штук", "Даана", "Pcs", "Adet", "Dona"), 82, 5, Right: true);
        if (_money)
        {
            yield return new ColSpec("sum", T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), 140, 2, Right: true);
            yield return new ColSpec("markup", T("Ср. нац.", "Орт. үстөк", "Avg mk.", "Ort. kâr", "O'rt. ust."), 80, 5, Right: true);
            yield return new ColSpec("pay", T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), 104, 4);
        }
        yield return new ColSpec("status", T("Статус", "Абалы", "Status", "Durum", "Holat"), 140, 2);
    }

    /// <summary>Подобрать колонки под ширину: убираем наименее важные (по приоритету) — на 1024×600 остаются главные. true — набор изменился.</summary>
    private bool LayoutColumns()
    {
        var all = Specs().ToList();
        var avail = Math.Max(300, (_width > 0 ? _width : 1100) - 24);
        var visible = all.ToList();
        double Need(IEnumerable<ColSpec> cols) => cols.Sum(c => c.Star ? c.Min : c.Width);
        while (Need(visible) > avail && visible.Count > 3)
        {
            var drop = visible.Where(c => !c.Star && c.Priority > 1).OrderByDescending(c => c.Priority).ThenByDescending(c => visible.IndexOf(c)).FirstOrDefault();
            if (drop is null)
                break;
            visible.Remove(drop);
        }
        var key = _mode + ":" + string.Join(",", visible.Select(c => c.Key));
        if (key == _visibleKey)
            return false;
        _visibleKey = key;
        _visible = visible;
        return true;
    }

    private ColumnDefinitions ColumnDefs()
    {
        var defs = new ColumnDefinitions();
        foreach (var c in _visible)
            defs.Add(c.Star ? new ColumnDefinition(1, GridUnitType.Star) { MinWidth = c.Min } : new ColumnDefinition(c.Width, GridUnitType.Pixel));
        return defs;
    }

    private void BuildHead()
    {
        _head.Children.Clear();
        _head.ColumnDefinitions = ColumnDefs();
        for (var i = 0; i < _visible.Count; i++)
        {
            var t = new TextBlock
            {
                Text = _visible[i].Title, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = _visible[i].Right ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(0, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            _head.Children.Add(t);
        }
    }

    // ------------------------------------------------------------------ строки

    private TextBlock Txt(string text, string brush = "BrushText", double size = 13, FontWeight? weight = null, bool right = false, int lines = 1)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxLines = lines, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        t.Classes.Add("num");
        Use(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    private StackPanel Two(string main, string sub, bool bold = false, string subBrush = "BrushTextSoft")
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        p.Children.Add(Txt(main, "BrushText", 13, bold ? FontWeight.SemiBold : FontWeight.Normal));
        if (sub.Length > 0)
            p.Children.Add(Txt(sub, subBrush, 11.5));
        return p;
    }

    private Control Shell(Grid g, string number, Action open)
    {
        var box = new Border { Padding = new Thickness(10, 7, 14, 7), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, Child = g, MinHeight = 40 };
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        box.DoubleTapped += (_, _) => open();
        PurchaseNumberUi.AttachCopyMenu(box, number);
        return box;
    }

    private static string Date(DateTime? d) => d?.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—";

    private static string Money(double v) => MoneyText.Amount(v);

    private static string N(int v) => v.ToString("N0", Ru);

    private Control LineRow(RegistryLine l)
    {
        var g = new Grid { ColumnDefinitions = ColumnDefs() };
        for (var i = 0; i < _visible.Count; i++)
        {
            Control? c = _visible[i].Key switch
            {
                "no" => Txt(l.No.ToString(CultureInfo.InvariantCulture), "BrushTextSoft", 12.5, right: true),
                "code" => Txt(l.Code, "BrushTextSoft", 12.5),
                "barcode" => Txt(l.Barcode, "BrushTextSoft", 12.5),
                "name" => Txt(l.Name.Length > 0 ? l.Name : "—", "BrushText", 13.5, FontWeight.SemiBold, lines: 2),
                "unit" => Txt(l.Unit.Length > 0 ? l.Unit : T("шт", "даана", "pcs", "adet", "dona"), "BrushTextSoft", 12.5),
                "qty" => Txt(PurchasesRegistry.QtyText(l.Qty), "BrushText", 13, right: true),
                "purchase" => PurchaseCell(l),
                "sum" => Txt(Money(l.Sum), "BrushText", 13.5, FontWeight.Bold, right: true),
                "markup" => Txt(l.View.Percent is null ? "—" : PurchasesRegistry.MarkupText(l.View.Percent, l.View.Estimated), l.View.Below ? "BrushDanger" : l.View.Low ? "BrushWarning" : "BrushText", 13, right: true),
                "sale" => Txt(l.View.Sale > 0 ? Money(l.View.Sale) : "—", "BrushText", 13, right: true),
                "doc" => Txt(l.Doc.Number, "BrushText", 12.5, FontWeight.SemiBold),
                "date" => Txt(Date(l.Doc.At), "BrushTextSoft", 12.5),
                "supplier" => Txt(l.Doc.SupplierName.Length > 0 ? l.Doc.SupplierName : "—", "BrushTextSoft", 13),
                "wh" => Txt(l.Doc.Warehouse, "BrushTextSoft", 12.5),
                "by" => Txt(l.Doc.CreatedBy, "BrushTextSoft", 12.5),
                _ => null,
            };
            if (c is null)
                continue;
            Grid.SetColumn(c, i);
            g.Children.Add(c);
        }
        var shell = Shell(g, l.Doc.Number, () => _ = OpenItemAsync(l));
        return shell;
    }

    /// <summary>Закупка: число и значок «↑», если выше прошлой закупки этого товара (в подсказке — прошлая цена и рост в %).</summary>
    private Control PurchaseCell(RegistryLine l)
    {
        var text = Txt(Money(l.Purchase), "BrushText", 13, right: true);
        if (!l.Raised)
            return text;
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Child = new TextBlock { Text = "↑", FontSize = 11, FontWeight = FontWeight.Bold } };
        Use(badge, Border.BackgroundProperty, "BrushWarningSoft");
        Use((TextBlock)badge.Child, TextBlock.ForegroundProperty, "BrushWarning");
        var up = ReceivingPricing.PurchaseRaisePercent(l.PrevPurchase, l.Purchase);
        ToolTip.SetTip(badge, T($"Закупка выше прошлой: было {Money(l.PrevPurchase)}, сейчас {Money(l.Purchase)} (+{up:0.#} %)", $"Сатып алуу мурункудан жогору: болгон {Money(l.PrevPurchase)}, азыр {Money(l.Purchase)} (+{up:0.#} %)",
            $"Purchase is above the previous one: was {Money(l.PrevPurchase)}, now {Money(l.Purchase)} (+{up:0.#} %)", $"Alış öncekinden yüksek: önceki {Money(l.PrevPurchase)}, şimdi {Money(l.Purchase)} (+{up:0.#} %)",
            $"Xarid avvalgisidan yuqori: edi {Money(l.PrevPurchase)}, hozir {Money(l.Purchase)} (+{up:0.#} %)"));
        text.Margin = new Thickness(0, 0, 4, 0);
        text.HorizontalAlignment = HorizontalAlignment.Right;
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Children = { badge, text } };
    }

    private Control DocRow(RegistryDoc d)
    {
        var g = new Grid { ColumnDefinitions = ColumnDefs() };
        for (var i = 0; i < _visible.Count; i++)
        {
            Control? c = _visible[i].Key switch
            {
                "numdate" => Two(d.Number, Date(d.At), bold: true),
                "supplier" => Txt(d.SupplierName.Length > 0 ? d.SupplierName : "—", "BrushText", 13.5, FontWeight.SemiBold, lines: 2),
                "supdoc" => d.SupplierDoc.Length > 0 ? Two(d.SupplierDoc, d.SupplierDocDate?.ToString("dd.MM.yyyy") ?? "") : Txt("—", "BrushTextSoft"),
                "wh" => Txt(d.Warehouse, "BrushTextSoft", 12.5),
                "by" => Txt(d.CreatedBy, "BrushTextSoft", 12.5),
                "items" => Txt(d.Items.ToString("N0", Ru), "BrushText", 13, right: true),
                "qty" => Txt(d.LinesMissing ? "…" : PurchasesRegistry.QtyText(d.Qty), "BrushText", 13, right: true),
                "sum" => SumCell(d),
                "markup" => Txt(d.LinesMissing ? "…" : PurchasesRegistry.MarkupText(d.AvgMarkup, d.MarkupEstimated), "BrushText", 13, right: true),
                "pay" => Txt(d.Pay, d.PayKind == "debt" ? "BrushWarning" : "BrushTextSoft", 12.5),
                "status" => StatusCell(d),
                _ => null,
            };
            if (c is null)
                continue;
            Grid.SetColumn(c, i);
            g.Children.Add(c);
        }
        return Shell(g, d.Number, () => _ = OpenItemAsync(d));
    }

    private Control SumCell(RegistryDoc d)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        p.Children.Add(Txt(Money(d.Total), "BrushText", 13.5, FontWeight.Bold, right: true));
        if (d.Items == 0 && d.Total < 0.005 && !d.LinesMissing)
            p.Children.Add(Txt(T("пустой приход", "бош кириш", "empty delivery", "boş giriş", "bo'sh kirim"), "BrushTextSoft", 11.5, right: true)); // 2026-10-31: приход без строк (тестовый) — подпись вместо немой «0,00»
        if (d.Returned > 0.005)
            p.Children.Add(Txt(T("возврат ", "кайтаруу ", "returned ", "iade ", "qaytarma ") + Money(d.Returned), "BrushDanger", 11.5, right: true));
        return p;
    }

    private Control StatusCell(RegistryDoc d)
    {
        var (bg, fg) = d.Status switch
        {
            RegistryStatus.Edited => ("BrushWarningSoft", "BrushText"),
            RegistryStatus.Returned => ("BrushWarningSoft", "BrushText"),
            _ => ("BrushSuccessSoft", "BrushSuccess"),
        };
        var pill = SectionCards.Pill(this, PurchasesRegistry.StatusText(d), bg, fg);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        return pill;
    }

    private async Task OpenItemAsync(object item)
    {
        try
        {
            switch (item)
            {
                case RegistryDoc d:
                    await OpenDocument(d, null).ConfigureAwait(true);
                    break;
                case RegistryLine l:
                    await OpenDocument(l.Doc, l).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: документ не открылся ({ex.Message}).", "WARNING");
        }
    }

    // ------------------------------------------------------------------ итог по фильтру

    private void RenderTotals()
    {
        _totalsGrid.Children.Clear();
        var cells = new List<(string Caption, string Value, string? Sub)>
        {
            (T("По фильтру · документов", "Чыпка боюнча · документтер", "Filtered · documents", "Filtre · belge", "Filtr bo'yicha · hujjatlar"), _totals.Docs.ToString("N0", Ru), null),
            (T("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar"), _totals.Lines.ToString("N0", Ru), null),
            (T("Штук", "Даана", "Pieces", "Adet", "Dona"), PurchasesRegistry.QtyText(_totals.Qty), null),
        };
        if (_money)
        {
            cells.Add((T("Сумма закупки", "Сатып алуу суммасы", "Purchase amount", "Alış tutarı", "Xarid summasi"), Money(_totals.Purchase), null));
            cells.Add((T("Ср. наценка · прибыль", "Орт. үстөк · пайда", "Avg markup · profit", "Ort. kâr · kazanç", "O'rt. ustama · foyda"),
                _totals.AvgMarkup is { } a ? (_totals.Estimated ? "≈ " : "") + a.ToString("0.0", Ru) + " %" : "—", _totals.AvgMarkup is null ? null : Money(_totals.Profit)));
        }
        var narrow = _width > 0 && _width < 760;
        _totalsGrid.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", narrow ? Math.Min(3, cells.Count) : cells.Count)));
        _totalsGrid.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", narrow ? (cells.Count + 2) / 3 : 1)));
        for (var i = 0; i < cells.Count; i++)
        {
            var (caption, value, sub) = cells[i];
            var cap = new TextBlock { Text = caption.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(cap, TextBlock.ForegroundProperty, "BrushTextSoft");
            var val = new TextBlock { Text = value, FontSize = narrow ? 15 : 18, FontWeight = FontWeight.Bold };
            val.Classes.Add("num");
            Use(val, TextBlock.ForegroundProperty, "BrushText");
            var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { val } };
            if (sub is not null)
            {
                var s = new TextBlock { Text = sub, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
                s.Classes.Add("num");
                Use(s, TextBlock.ForegroundProperty, "BrushTextSoft");
                valueRow.Children.Add(s);
            }
            var cell = new StackPanel { Margin = new Thickness(0, 0, 10, narrow ? 4 : 0), Children = { cap, valueRow } };
            Grid.SetColumn(cell, narrow ? i % 3 : i);
            Grid.SetRow(cell, narrow ? i / 3 : 0);
            _totalsGrid.Children.Add(cell);
        }
        ToolTip.SetTip(_totalsBar, T("Итог — по текущему фильтру (период, поставщик, статус, поиск), а не только по видимым строкам.", "Жыйынтык — азыркы чыпка боюнча (мөөнөт, жеткирүүчү, абал, издөө), көрүнгөн саптар боюнча гана эмес.",
            "The total follows the current filter (period, supplier, status, search), not only the visible rows.", "Toplam geçerli filtreye göre (dönem, tedarikçi, durum, arama); yalnızca görünen satırlara göre değil.",
            "Jami joriy filtr bo'yicha (davr, yetkazib beruvchi, holat, qidiruv), faqat ko'rinadigan qatorlar bo'yicha emas."));
    }

    // ------------------------------------------------------------------ экспорт и печать

    private RegistryExportMeta Meta()
    {
        var (from, to) = PeriodBounds();
        var filters = new List<string>();
        if (_supplierId is not null && _money)
            filters.Add(T("поставщик: ", "жеткирүүчү: ", "supplier: ", "tedarikçi: ", "yetkazib beruvchi: ") + (_supplier.Selected?.Name ?? ""));
        if (_warehouseName is not null)
            filters.Add(T("склад: ", "склад: ", "warehouse: ", "depo: ", "ombor: ") + _warehouseName);
        if (_status is { } st)
            filters.Add(T("статус: ", "абалы: ", "status: ", "durum: ", "holat: ") + st switch
            {
                RegistryStatus.Edited => T("исправлены", "оңдолгон", "edited", "düzeltilen", "tuzatilgan"),
                RegistryStatus.Returned => T("с возвратом", "кайтаруу менен", "with return", "iadeli", "qaytarish bilan"),
                _ => T("проведены", "өткөрүлгөн", "posted", "işlenen", "o'tkazilgan"),
            });
        if (_search.Text?.Trim() is { Length: > 0 } q)
            filters.Add(T("поиск: ", "издөө: ", "search: ", "arama: ", "qidiruv: ") + q);
        var title = _mode == "lines" ? T("Покупки — позиции", "Сатып алуулар — позициялар", "Purchases — items", "Alışlar — kalemler", "Xaridlar — pozitsiyalar")
            : T("Покупки — документы", "Сатып алуулар — документтер", "Purchases — documents", "Alışlar — belgeler", "Xaridlar — hujjatlar");
        return new RegistryExportMeta(title, CompanyInfoService.LastCompany?.Name, from is { } f ? PeriodRange.Label(f, to ?? DateTime.Today) : "", string.Join("; ", filters));
    }

    private async Task ExportAsync(bool csv)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        if (_totals.Docs == 0 && _totals.Lines == 0)
        {
            _note = T("Нечего выгружать — список пуст.", "Чыгара турган эч нерсе жок — тизме бош.", "Nothing to export — the list is empty.", "Dışa aktarılacak bir şey yok — liste boş.", "Eksport qiladigan narsa yo'q — ro'yxat bo'sh.");
            UpdateInfo();
            return;
        }
        var meta = Meta();
        var lines = _mode == "lines";
        var docs = _shownDocs;
        var ls = _shownLines;
        var totals = _totals;
        var warehouse = _hasWarehouse;
        try
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Сохранить реестр закупок", "Сатып алуулар реестрин сактоо", "Save the purchases registry", "Alış kaydını kaydet", "Xaridlar reestrini saqlash"),
                SuggestedFileName = PurchasesRegistryExport.SuggestFileName(meta, csv, lines),
                DefaultExtension = PurchasesRegistryExport.Extension(csv),
                FileTypeChoices = [csv ? new FilePickerFileType("CSV") { Patterns = ["*.csv"] } : new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] }],
            }).ConfigureAwait(true);
            if (file is null)
                return;
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                _note = T("Не удалось определить путь файла — выберите папку на этом компьютере.", "Файлдын жолун аныктоо мүмкүн болгон жок — ушул компьютердеги папканы тандаңыз.", "Could not determine the file path — choose a folder on this computer.",
                    "Dosya yolu belirlenemedi — bu bilgisayarda bir klasör seçin.", "Fayl yo'lini aniqlab bo'lmadi — shu kompyuterdagi papkani tanlang.");
                UpdateInfo();
                return;
            }
            _note = T("Сохраняю…", "Сакталууда…", "Saving…", "Kaydediliyor…", "Saqlanmoqda…");
            UpdateInfo();
            await Task.Run(() => PurchasesRegistryExport.Save(path!, csv, lines, docs, ls, totals, meta, _money, warehouse)).ConfigureAwait(true);
            _note = T("Реестр сохранён: ", "Реестр сакталды: ", "Registry saved: ", "Kayıt kaydedildi: ", "Reestr saqlandi: ") + path;
            PosLogger.Log($"Реестр закупок: выгрузка {(csv ? "CSV" : "Excel")} — {(lines ? ls.Count + " позиций" : docs.Count + " документов")} ({path}).", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Реестр закупок: выгрузка не удалась ({ex}).", "WARNING");
            _note = T("Не удалось сохранить реестр: ", "Реестрди сактоо мүмкүн болгон жок: ", "Could not save the registry: ", "Kayıt kaydedilemedi: ", "Reestrni saqlab bo'lmadi: ") + ex.Message;
        }
        UpdateInfo();
    }

    /// <summary>Печать списка: на принтер по умолчанию (A4 альбомная, шапка на каждой странице, внизу итог); нет принтера или не Windows — PDF в «Документы\NurMarket\Закупки» и открыть.</summary>
    private async Task PrintAsync()
    {
        if (_totals.Docs == 0 && _totals.Lines == 0)
        {
            _note = T("Нечего печатать — список пуст.", "Басып чыгара турган эч нерсе жок — тизме бош.", "Nothing to print — the list is empty.", "Yazdırılacak bir şey yok — liste boş.", "Chop etadigan narsa yo'q — ro'yxat bo'sh.");
            UpdateInfo();
            return;
        }
        var meta = Meta();
        var lines = _mode == "lines";
        var docs = _shownDocs;
        var ls = _shownLines;
        var totals = _totals;
        var warehouse = _hasWarehouse;
        _note = T("Готовлю печать…", "Басып чыгарууга даярдалууда…", "Preparing to print…", "Yazdırma hazırlanıyor…", "Chop etishga tayyorlanmoqda…");
        UpdateInfo();
        string? error = null;
        try
        {
            string? printed = null;
            if (OperatingSystem.IsWindows())
            {
                printed = await Task.Run(() =>
                {
                    var printer = ClientDocPrint.DefaultPrinter();
                    return string.IsNullOrWhiteSpace(printer) ? "-" : PurchasesRegistryExport.PrintLandscape(printer, meta.Title, PurchasesRegistryExport.PageImages(lines, docs, ls, totals, meta, _money, warehouse));
                }).ConfigureAwait(true);
            }
            if (OperatingSystem.IsWindows() && printed is null)
            {
                _note = T("Список отправлен на печать.", "Тизме басып чыгарууга жөнөтүлдү.", "The list was sent to the printer.", "Liste yazıcıya gönderildi.", "Ro'yxat chop etishga yuborildi.");
                PosLogger.Log($"Реестр закупок: печать списка ({(lines ? ls.Count + " позиций" : docs.Count + " документов")}).", "INFO");
                UpdateInfo();
                return;
            }
            // Принтера нет (или печать не Windows): PDF и открыть — печатать можно из просмотрщика.
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NurMarket", "Закупки");
            var path = Path.Combine(folder, Path.ChangeExtension(PurchasesRegistryExport.SuggestFileName(meta, csv: false, lines), ".pdf"));
            await Task.Run(() => PurchasesRegistryExport.SavePdf(path, lines, docs, ls, totals, meta, _money, warehouse)).ConfigureAwait(true);
            PurchaseDocs.OpenFile(path);
            _note = T("Принтер не найден — список сохранён в PDF: ", "Принтер табылган жок — тизме PDF'ке сакталды: ", "No printer found — the list was saved as PDF: ", "Yazıcı bulunamadı — liste PDF olarak kaydedildi: ", "Printer topilmadi — ro'yxat PDF qilib saqlandi: ") + path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            PosLogger.Log($"Реестр закупок: печать не удалась ({ex}).", "WARNING");
        }
        if (error is not null)
            _note = T("Не удалось напечатать: ", "Басып чыгаруу мүмкүн болгон жок: ", "Could not print: ", "Yazdırılamadı: ", "Chop etib bo'lmadi: ") + error;
        UpdateInfo();
    }
}
