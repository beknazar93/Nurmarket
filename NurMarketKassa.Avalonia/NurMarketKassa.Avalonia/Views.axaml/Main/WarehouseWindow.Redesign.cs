using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, владелец (снимок «Склада»): «проведи полный редизайн склада, улучши его». Вкладка «Товары»:
/// • сводка плитками сверху (была строкой под таблицей): товары и единицы, склад по закупке, по продаже и наценка,
///   «Заканчивается», «Нет в наличии», «Срок годности» — нажатие на плитку включает фильтр (повторное — снимает);
/// • фильтр наличия рядом с «Единицей продажи»: Все / Заканчивается / Нет в наличии / Срок годности;
/// • в строке — «категория · бренд» и метка срока годности (сроки — с сервера, ProductExpiryIndex);
/// • кнопка «±» — приход, списание или точное количество прямо из строки (ProductActions — тот же документ ревизии,
///   что «Списание»; тем же пользуются ИИ-советник и бот).</summary>
public partial class WarehouseWindow
{
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Dictionary<string, RadioButton> _stockFilterButtons = new();
    private (int Count, double Units, double Purchase, double Sale, int Low, int Out) _lastTotals;
    private bool _redesignReady;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private void InitRedesign()
    {
        if (_redesignReady)
            return;
        _redesignReady = true;
        // 2026-10-06, владелец (снимок: значок-коробка вместо фото): «где фотки, которые поставил? на складе отображай фотки спереди, если есть».
        // Фото товаров качал только каталог кассы — в программе владельца склад их не загружал. Теперь — для видимой страницы.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WarehouseViewModel.PagedProducts))
                LoadPageThumbnails();
        };
        LoadPageThumbnails();
        // 2026-10-06: склад, открытый в те доли секунды, пока каталог дозагружается после запуска, оставался пустым («Товаров 0»)
        // до «Обновить». Каталог загрузился, а склад пуст — перестроить список.
        // Отписка при закрытии окна: в кассе окно склада создаётся заново при каждом открытии.
        _catalogUpdatedHandler = () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // 2026-10-27, аудит владельца С-3: плитки («Товаров 341») пересчитывались только при пустом списке и после действий в окне,
            // поэтому после смены каталога (синхронизация, приход с кассы) расходились с «Всего: 343» под поиском. Теперь итоги —
            // на каждое обновление каталога (один источник цифр — CatalogCacheService.Products).
            if (CatalogCacheService.Products.Count > 0)
                RefreshWarehouseTotals();
            if (_viewModel.FilteredProducts.Count > 0 || CatalogCacheService.Products.Count == 0)
                return;
            _ = _viewModel.EnsureCatalogLoadedAsync();
            RefreshWarehouseTotals();
        });
        CatalogCacheService.CacheUpdated += _catalogUpdatedHandler;
        Closed += (_, _) => CatalogCacheService.CacheUpdated -= _catalogUpdatedHandler;
        // Синхронизация «без изменений» список в памяти не заполняет (его могли очистить при входе) — склад берёт каталог
        // из базы этого компьютера сам: при открытии и при каждом показе окна (не чаще раза в 5 с).
        LoadCatalogIfEmpty();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
                LoadCatalogIfEmpty();
        };
        StockFilterPanel.Children.Clear();
        _stockFilterButtons.Clear();
        // 2026-10-19, О9: подпись — перед рамкой сегментов (в разметке), кнопки — сегменты sec-seg (выбранный тёмно-синий).
        StockFilterLabel.Text = T("Наличие", "Бар-жогу", "Stock", "Stok", "Mavjudlik");
        ToolTip.SetTip(StockFilterPanel.Parent as Control ?? StockFilterPanel, StockFilterLabel.Text); // 2026-10-22 (П5): подпись скрыта на низком окне — остаётся подсказкой
        foreach (var (key, text) in new[]
                 {
                     ("all", T("Все", "Баары", "All", "Tümü", "Hammasi")),
                     ("low", T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda")),
                     ("out", T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas")),
                     ("expiry", T("Срок годности", "Жарактуулук мөөнөтү", "Expiry", "Son kullanma", "Yaroqlilik muddati")),
                 })
        {
            var radio = new RadioButton { GroupName = "WarehouseStockFilter", Classes = { "sec-seg" }, Content = text, IsChecked = key == _viewModel.StockFilter };
            var k = key;
            // По смене отметки, а не по Click — так срабатывает и с клавиатуры.
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && _viewModel.StockFilter != k)
                    SetStockFilter(k);
            };
            _stockFilterButtons[key] = radio;
            StockFilterPanel.Children.Add(radio);
        }
        _ = RefreshExpiryAsync(force: false);
        // 2026-10-22 (аудит 1.17.59, П-И, Б10): на низком окне (1366×768) вкладка «Товары» показывала 3,5 строки — плитки,
        // поиск и два ряда фильтров занимали ~60 % высоты.
        SizeChanged += (_, _) => ApplyProductsCompact();
        ApplyProductsCompact();

        // 2026-10-06, владелец: «сделай редизайн кнопок, расположение, удобство» — «✕» в поиске и двойной щелчок по строке.
        ProductSearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                ProductSearchClearButton.IsVisible = !string.IsNullOrEmpty(ProductSearchBox.Text);
        };
        ProductSearchClearButton.IsVisible = !string.IsNullOrEmpty(ProductSearchBox.Text);
        ProductsGrid.DoubleTapped += (_, e) =>
        {
            // Двойной щелчок по кнопке в строке — это её нажатия, не открытие карточки.
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
                return;
            if (ProductsGrid.SelectedItem is CatalogProductTileVm product)
                EditProduct_Click(new Button { Tag = product }, new RoutedEventArgs());
        };
        ToolTip.SetTip(ProductsGrid, T("Двойной щелчок по строке — открыть товар", "Сапты эки жолу басуу — товарды ачуу", "Double-click a row to open the product",
            "Ürünü açmak için satıra çift tıklayın", "Mahsulotni ochish uchun qatorni ikki marta bosing"));
    }

    /// <summary>2026-10-22 (аудит 1.17.59, П-И, Б10): компактная вкладка «Товары» на низком окне (высота &lt; 800): плитки без
    /// пояснений (они в подсказке), промежутки уже, строка таблицы 44 (зона касания 44 сохранена; на высоком окне — прежние 52).
    /// Логика поиска, фильтров и таблицы не меняется — только размеры.</summary>
    private void ApplyProductsCompact()
    {
        var compact = ClientSize.Height > 0 && ClientSize.Height < 800;
        Classes.Set("wh-compact", compact);
        // 2026-10-22 (аудит новой сборки, П5): на 1366×768 в программе владельца было ~6 строк (нужно ≥ 8): строка 44 → 40 на совсем низком окне,
        // плитки сводки ниже (стиль wh-compact в разметке), подписи фильтров скрыты — два ряда фильтров стали одним, поля раздела меньше.
        var veryLow = compact && ClientSize.Height < 720;
        ProductsGrid.RowHeight = compact ? (veryLow ? 40 : 44) : 52;
        WarehouseKpiPanel.Margin = new Thickness(0, 0, 0, compact ? 6 : 14);
        ProductsSearchRow.Margin = new Thickness(0, 0, 0, compact ? 4 : 12);
        ProductsFilterRow.Margin = new Thickness(0, 0, 0, compact ? 0 : 12);
        ProductsTabBorder.Padding = compact ? new Thickness(12, 8, 12, 6) : new Thickness(16);
        // 2026-10-23 (аудит круг 3, Б-6): моноблок 1024×600 — список был ~3,5 строки. Ниже 660 px плитки сводки скрыты (те же фильтры есть
        // кнопками «Все / Заканчивается / …» ниже), шапка окна плотнее; строка таблицы 44 (зона касания сохранена).
        var tiny = ClientSize.Height > 0 && ClientSize.Height < 660;
        WarehouseKpiPanel.IsVisible = !tiny;
        // 2026-10-26, владелец: «дублирование фильтра идёт» — плитки «Заканчивается / Нет в наличии / Срок годности» уже фильтруют
        // (нажатие включает, повторное снимает), а ряд «Наличие» повторял то же самое. Ряд показывается только там, где плиток нет.
        if (StockFilterPanel.Parent?.Parent is Control stockFilterGroup)
            stockFilterGroup.IsVisible = tiny;
        if (!_isOwnerSection)
            WindowHeaderBorder.Padding = tiny ? new Thickness(12, 4) : new Thickness(16, 10);
        WindowHeaderBorder.Margin = new Thickness(0, 0, 0, tiny ? 6 : (_isOwnerSection ? 8 : 16));
        if (tiny)
            ProductsGrid.RowHeight = 44;
        if (_isOwnerSection)
            RootGrid.Margin = compact ? new Thickness(24, 6, 24, 10) : OwnerSectionLayout.Margin;
    }

    /// <summary>2026-10-06, владелец: «открывать товар на складе голосом». Вкладка «Товары», фильтры сброшены, в поиске — товар.</summary>
    public void ShowProduct(CatalogProductTileVm product)
    {
        WarehouseTabs.SelectedItem = ProductsTabItem;
        if (_viewModel.StockFilter != "all")
            SetStockFilter("all");
        ResetSaleUnitFilter();
        ProductSearchBox.Text = product.Title;
        Activate();
    }

    /// <summary>2026-10-27, жалоба «Нур Советник открывает не ту вкладку»: выбрать вкладку склада по ключу словаря (AdvisorNavRouter) и ПРОВЕРИТЬ,
    /// что она выбрана. false — вкладки нет (скрыта правами или тарифом) или выбрать не удалось.</summary>
    public bool SelectNavTab(string tab)
    {
        TabItem? target = tab switch
        {
            "products" => ProductsTabItem,
            "receiving" => ReceivingTabItem,
            "revision" => RevisionTabItem,
            "writeoff" => WriteOffTabItem,
            "movements" => MovementsTabItem,
            "labels" => LabelEditorTabItem,
            "bulk" => _bulkTab,
            _ => null,
        };
        if (target is null || !target.IsVisible || !WarehouseTabs.Items.Contains(target))
            return false;
        WarehouseTabs.SelectedItem = target;
        return ReferenceEquals(WarehouseTabs.SelectedItem, target);
    }

    /// <summary>2026-10-27: фильтр наличия на вкладке «Товары» (low — заканчивается, out — нет в наличии, expiry — срок годности); true — фильтр применён.</summary>
    public bool ApplyNavFilter(string filter)
    {
        if (!SelectNavTab("products") || !_stockFilterButtons.ContainsKey(filter))
            return false;
        ResetSaleUnitFilter();
        ProductSearchBox.Text = "";
        if (_viewModel.StockFilter != filter)
            SetStockFilter(filter);
        return _viewModel.StockFilter == filter;
    }

    private Border? _changedBanner;

    /// <summary>2026-10-06, владелец: «чтобы наш ИИ напрямую открывал программу и показывал наглядно изменения». После «Выполнить»
    /// у ИИ — вкладка «Товары» только с изменёнными товарами и плашка «Изменено ИИ: N ✕» (во всплывающей подсказке — что сделано).</summary>
    public async void ShowChanged(IReadOnlyList<string> keys, string summary)
    {
        // Сначала каталог заново с сервера — в таблице новые цены, остатки и только что созданные товары, а не кеш.
        try
        {
            var result = await CatalogCacheService.SyncCatalogFullAsync().ConfigureAwait(true);
            if (result.Success)
                CatalogCacheService.NotifyCatalogChanged();
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            RefreshWarehouseTotals();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: каталог перед показом изменений ИИ не обновлён ({ex.Message}).", "CATALOG");
        }
        WarehouseTabs.SelectedItem = ProductsTabItem;
        if (_viewModel.StockFilter != "all")
            SetStockFilter("all");
        ResetSaleUnitFilter();
        ProductSearchBox.Text = "";
        _viewModel.SetChangedFilter(keys);
        if (_changedBanner is null)
        {
            _changedBanner = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 12, 4), Cursor = new Cursor(StandardCursorType.Hand) };
            _changedBanner.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushAccent"));
            _changedBanner.PointerPressed += (_, _) => HideChanged();
            WarehouseFiltersRow.Children.Insert(0, _changedBanner);
        }
        _changedBanner.Child = new TextBlock
        {
            Text = T($"✨ Изменено ИИ: {keys.Count}  ✕", $"✨ ИИ өзгөрттү: {keys.Count}  ✕", $"✨ Changed by AI: {keys.Count}  ✕", $"✨ Yapay zekâ değiştirdi: {keys.Count}  ✕",
                $"✨ SI o'zgartirdi: {keys.Count}  ✕"),
            FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_changedBanner, summary + "\n\n" + T("Нажмите — показать все товары", "Басыңыз — бардык товарларды көрсөтүү", "Click to show all products",
            "Tüm ürünleri göstermek için tıklayın", "Bosing — barcha mahsulotlarni ko'rsatish"));
        _changedBanner.IsVisible = true;
        Activate();
    }

    /// <summary>2026-10-20: проверка фильтров — «открыть товар» (голос/ИИ) сбрасывал только «Наличие»: при выбранных «Весовых»
    /// штучный товар не находился. Теперь и «Единица продажи» — «Все» (отметка и фильтр вместе).</summary>
    private void ResetSaleUnitFilter()
    {
        SaleUnitAllRadio.IsChecked = true;
        _viewModel.SetSaleUnitFilter("all");
    }

    private void HideChanged()
    {
        _viewModel.SetChangedFilter(null);
        if (_changedBanner is not null)
            _changedBanner.IsVisible = false;
    }

    private DateTime _catalogLoadTried = DateTime.MinValue;
    private Action? _catalogUpdatedHandler;

    private void LoadCatalogIfEmpty()
    {
        if (CatalogCacheService.Products.Count > 0 || DateTime.UtcNow - _catalogLoadTried < TimeSpan.FromSeconds(5))
            return;
        _catalogLoadTried = DateTime.UtcNow;
        if (CatalogCacheService.LoadFromDatabase())
            PosLogger.Log("Склад: каталог в памяти был пуст — загружен из базы этого компьютера.", "CATALOG");
    }

    // Не больше 4 загрузок фото сразу — страница склада до 50 товаров, сервер не дёргаем разом.
    private static readonly SemaphoreSlim ThumbGate = new(4);

    private void LoadPageThumbnails()
    {
        var services = App.AppHost?.Services;
        var thumbs = services?.GetService(typeof(ProductThumbService)) as ProductThumbService;
        var authApi = services?.GetService(typeof(NurMarketKassa.Services.Api.IAuthApiService)) as NurMarketKassa.Services.Api.IAuthApiService;
        var apiBaseUrl = (services?.GetService(typeof(NurMarketKassa.Configuration.AppSettings)) as NurMarketKassa.Configuration.AppSettings)?.ApiBaseUrl;
        if (thumbs is null || authApi is null || string.IsNullOrWhiteSpace(apiBaseUrl))
            return;
        foreach (var product in _viewModel.PagedProducts.ToList())
        {
            if (string.IsNullOrWhiteSpace(product.ImageUrl) || !string.IsNullOrEmpty(product.ProductImagePath))
                continue;
            _ = LoadThumbAsync(thumbs, authApi, apiBaseUrl!, product);
        }
    }

    private static async Task LoadThumbAsync(ProductThumbService thumbs, NurMarketKassa.Services.Api.IAuthApiService authApi, string apiBaseUrl, CatalogProductTileVm product)
    {
        await ThumbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await thumbs.SetThumbAsync(Avalonia.Threading.Dispatcher.UIThread, authApi, apiBaseUrl, product.ImageUrl!, product, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: фото «{product.Title}» не загружено ({ex.Message}).", "WARNING");
        }
        finally
        {
            ThumbGate.Release();
        }
    }

    private void ProductSearchClear_Click(object? sender, RoutedEventArgs e)
    {
        ProductSearchBox.Text = "";
        ProductSearchBox.Focus();
    }

    private void SetStockFilter(string key)
    {
        _viewModel.SetStockFilter(key);
        foreach (var (k, radio) in _stockFilterButtons)
            radio.IsChecked = k == key;
        BuildKpiCards();
    }

    /// <summary>Сроки годности с сервера → метки в строках, плитка «Срок годности», фильтр.</summary>
    private async Task RefreshExpiryAsync(bool force)
    {
        await ProductExpiryIndex.RefreshAsync(force).ConfigureAwait(true);
        WarehouseViewModel.ApplyExpiryBadges();
        BuildKpiCards();
        if (_viewModel.StockFilter == "expiry")
            _viewModel.ReapplyProductFilter();
    }

    /// <summary>Плитки сводки по последним итогам (RefreshWarehouseTotals).</summary>
    private void BuildKpiCards(int count, double units, double purchase, double sale, int low, int outOfStock)
    {
        _lastTotals = (count, units, purchase, sale, low, outOfStock);
        BuildKpiCards();
    }

    private void BuildKpiCards()
    {
        if (WarehouseKpiPanel is null)
            return;
        var (count, units, purchase, sale, low, outOfStock) = _lastTotals;
        var som = T("сом", "сом", "som", "som", "so'm");
        var marks = ProductExpiryIndex.Marks.Values.ToList();
        var expired = marks.Count(m => m.Expired);
        var expiring = marks.Count - expired;
        var filter = _viewModel.StockFilter;

        WarehouseKpiPanel.Children.Clear();
        var cards = new List<Control>
        {
            Card(T("Товаров", "Товарлар", "Products", "Ürünler", "Mahsulotlar"), count.ToString("N0", RuCulture),
                T($"{units.ToString("N0", RuCulture)} ед. на складе", $"кампада {units.ToString("N0", RuCulture)} бирдик", $"{units.ToString("N0", RuCulture)} units in stock",
                    $"stokta {units.ToString("N0", RuCulture)} birim", $"omborda {units.ToString("N0", RuCulture)} birlik"), null, null),
            Card(T("Склад по закупке", "Сатып алуу баасы боюнча", "Stock at cost", "Alış fiyatıyla stok", "Xarid narxida ombor"), purchase.ToString("N0", RuCulture) + " " + som,
                T("себестоимость остатка", "калдыктын өздүк наркы", "cost of the stock", "stok maliyeti", "qoldiq tannarxi"), null, null),
            Card(T("Склад по продаже", "Сатуу баасы боюнча", "Stock at sale price", "Satış fiyatıyla stok", "Sotuv narxida ombor"), sale.ToString("N0", RuCulture) + " " + som,
                T($"наценка {(sale - purchase).ToString("N0", RuCulture)} {som}", $"үстөк {(sale - purchase).ToString("N0", RuCulture)} {som}", $"markup {(sale - purchase).ToString("N0", RuCulture)} {som}",
                    $"kâr payı {(sale - purchase).ToString("N0", RuCulture)} {som}", $"ustama {(sale - purchase).ToString("N0", RuCulture)} {som}"), null, null),
            Card(T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda"), low.ToString("N0", RuCulture),
                T($"меньше {CatalogProductTileVm.LowStockQuantityThreshold:0} — нажмите, чтобы показать", $"{CatalogProductTileVm.LowStockQuantityThreshold:0} кем — көрсөтүү үчүн басыңыз",
                    $"under {CatalogProductTileVm.LowStockQuantityThreshold:0} — click to show", $"{CatalogProductTileVm.LowStockQuantityThreshold:0} altı — göstermek için tıklayın",
                    $"{CatalogProductTileVm.LowStockQuantityThreshold:0} dan kam — ko'rsatish uchun bosing"),
                low > 0 ? "BrushWarning" : null, "low"),
            Card(T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), outOfStock.ToString("N0", RuCulture),
                T("остаток 0 — нажмите, чтобы показать", "калдык 0 — көрсөтүү үчүн басыңыз", "stock 0 — click to show", "stok 0 — göstermek için tıklayın", "qoldiq 0 — ko'rsatish uchun bosing"),
                outOfStock > 0 ? "BrushDanger" : null, "out"),
            Card(T("Срок годности", "Жарактуулук мөөнөтү", "Expiry", "Son kullanma", "Yaroqlilik muddati"),
                ProductExpiryIndex.IsLoaded ? marks.Count.ToString("N0", RuCulture) : "…",
                ProductExpiryIndex.IsLoaded
                    ? T($"просрочено {expired} · скоро {expiring}", $"мөөнөтү өткөн {expired} · жакында {expiring}", $"expired {expired} · soon {expiring}",
                        $"süresi geçmiş {expired} · yakında {expiring}", $"muddati o'tgan {expired} · yaqinda {expiring}")
                    : T("загружаю с сервера…", "серверден жүктөлүүдө…", "loading from the server…", "sunucudan yükleniyor…", "serverdan yuklanmoqda…"),
                expired > 0 ? "BrushDanger" : expiring > 0 ? "BrushWarning" : null, "expiry"),
        };
        // 2026-10-30 (приёмка 1.17.65, «Права»): без права видеть закупку нет плитки «Склад по закупке», а у «Склада по продаже» — подсказки с наценкой (по ней считается закупка).
        if (!RevisionShowPurchase)
        {
            cards.RemoveAt(1);
            cards[1] = Card(T("Склад по продаже", "Сатуу баасы боюнча", "Stock at sale price", "Satış fiyatıyla stok", "Sotuv narxida ombor"), sale.ToString("N0", RuCulture) + " " + som, "", null, null);
        }
        // 2026-10-19, этап О9 (аудит, проблема 4): плитки — общие sec-kpi в KpiTileGrid (лишние — вторым рядом, ряды ровные),
        // сумма уменьшается Viewbox'ом целиком, подпись и пояснение переносятся (было «65 087 с…», «нажмите, что…»).
        // Выбранный фильтр — тёмная рамка (была жёлтая); цвет числа — только «мало» (оранжевый) и «нет / просрочено» (красный).
        foreach (var card in cards)
            WarehouseKpiPanel.Children.Add(card);

        Control Card(string label, string value, string sub, string? accent, string? filterKey)
        {
            var active = filterKey != null && filter == filterKey;
            var border = new Border { Classes = { "sec-kpi", "compact" } };
            if (active)
            {
                border.BorderThickness = new Thickness(2);
                border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushText"));
            }
            var stack = new StackPanel { Spacing = 3 };
            var l = new TextBlock { Text = label, Classes = { "sec-kpi-label" } };
            var v = new TextBlock { Text = value, Classes = { "sec-kpi-value" }, FontSize = 20 };
            if (accent != null)
                v.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(accent));
            var s = new TextBlock { Text = sub, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Classes = { "kpi-sub" } };
            s.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            stack.Children.Add(l);
            stack.Children.Add(new Viewbox { Classes = { "sec-kpi-fit" }, Child = v });
            stack.Children.Add(s);
            border.Child = stack;
            ToolTip.SetTip(border, label + ": " + value + "\n" + sub);
            if (filterKey != null)
            {
                border.Cursor = new Cursor(StandardCursorType.Hand);
                border.PointerPressed += (_, _) => SetStockFilter(filter == filterKey ? "all" : filterKey);
            }
            return border;
        }
    }

    private async void StockAdjust_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not CatalogProductTileVm product)
            return;
        var dialog = new StockAdjustDialog(product);
        if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
        {
            RefreshWarehouseTotals();
            _viewModel.ReapplyProductFilter();
        }
    }
}
