using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
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
            {
                SyncProductsEmpty();
                LoadPageThumbnails();
                // 2026-11-06 (жалоба клиента «колонка Код / штрихкод пустая»): у видимых строк без единого кода читаем полную карточку (в базе их нет после сокращённой ленты).
                NurMarketKassa.Services.ProductCodeBackfill.ScheduleForRows(_viewModel.PagedProducts.ToList());
            }
        };
        LoadPageThumbnails();
        NurMarketKassa.Services.ProductCodeBackfill.ScheduleForRows(_viewModel.PagedProducts.ToList());
        // 2026-11-11: подсказка вместо голой шапки таблицы при 0 строк; пока идёт загрузка с сервера — не показывается.
        ProductCountLoading.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                SyncProductsEmpty();
        };
        SyncProductsEmpty();
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
        // 2026-11-02 (аудит №2, С3-12): «Архив» — окно архивных товаров с возвратом из архива (POST unarchive). Не фильтр наличия, а отдельное окно.
        var archiveButton = SectionCards.Secondary(T("Архив", "Архив", "Archive", "Arşiv", "Arxiv"));
        // 2026-11-05 (аудит №4, Д4-21): «Архив» был пилюлей ~22 px (MinHeight = 0) — цель касания ниже 24 px (WCAG 2.5.8); теперь 40 px и отступ от сегмента слева.
        archiveButton.Margin = new Thickness(12, 0, 0, 6);
        archiveButton.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        archiveButton.MinHeight = 40;
        archiveButton.Padding = new Thickness(16, 0);
        ToolTip.SetTip(archiveButton, T("Товары, отправленные в архив: посмотреть и вернуть", "Архивге жөнөтүлгөн товарлар: карап чыгуу жана кайтаруу", "Products sent to the archive: view and restore", "Arşive gönderilen ürünler: görüntüle ve geri al", "Arxivga yuborilgan mahsulotlar: ko'rish va qaytarish"));
        archiveButton.Click += async (_, _) =>
        {
            var archiveApi = App.AppHost?.Services.GetService(typeof(NurMarketKassa.Services.Api.ICatalogApiService)) as NurMarketKassa.Services.Api.ICatalogApiService;
            if (archiveApi is null)
                return;
            // 2026-11-03 (аудит №3, Т3-03): без права «Склад: редактирование» архив открывается только для просмотра (кнопка «Вернуть» скрыта)
            var canRestore = App.AppHost?.Services.GetService(typeof(NurMarketKassa.Core.Contracts.IPermissionService)) is not NurMarketKassa.Core.Contracts.IPermissionService perm
                             || perm.HasPermission(NurMarketKassa.Core.Contracts.PosPermissions.EditProducts);
            var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ArchivedProductsDialog(archiveApi, canRestore);
            if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
            {
                // 2026-11-07 («при каждом изменении товара весь склад загружается заново»): только вернувшиеся из архива товары, без полной загрузки каталога.
                await CatalogCacheService.RefreshProductsAsync(dialog.RestoredIds).ConfigureAwait(true);
                await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            }
        };
        // 2026-11-02 (живая проверка круга 3, Р3-1): кнопка лежит рядом с группами фильтров, а не внутри группы «Наличие» (её скрывает обычный режим окна) — видна всегда.
        WarehouseFiltersRow.Children.Add(archiveButton);
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

    /// <summary>2026-11-11 (жалоба «пустота»): 0 строк в списке — подсказка по центру («ничего не найдено» при поиске/фильтре, «товаров пока нет» при пустом складе).</summary>
    private void SyncProductsEmpty()
    {
        var empty = _viewModel.PagedProducts.Count == 0 && !ProductCountLoading.IsVisible;
        ProductsEmptyHint.IsVisible = empty;
        if (!empty)
            return;
        var filtered = CatalogCacheService.Products.Count > 0;
        ProductsEmptyHint.Text = filtered
            ? T("Ничего не найдено. Измените поиск или фильтры.", "Эчтеке табылган жок. Издөөнү же чыпкаларды өзгөртүңүз.", "Nothing found. Change the search or the filters.",
                "Hiçbir şey bulunamadı. Aramayı veya filtreleri değiştirin.", "Hech narsa topilmadi. Qidiruv yoki filtrlarni o'zgartiring.")
            : T("Товаров пока нет. Нажмите «Создать товар» или «Массовая загрузка».", "Товарлар азырынча жок. «Товар түзүү» же «Массалык жүктөө» баскычын басыңыз.",
                "No products yet. Press “Create product” or “Bulk import”.", "Henüz ürün yok. «Ürün oluştur» veya «Toplu yükleme» düğmesine basın.",
                "Hozircha mahsulot yo'q. «Mahsulot yaratish» yoki «Ommaviy yuklash» tugmasini bosing.");
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
            "barcodes" => _barcodesTab, // 2026-11-11: вкладка «Штрихкоды» (WarehouseWindow.Barcodes.cs)
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
            _changedBanner.Bind(Border.BackgroundProperty, _changedBanner.GetResourceObservable("BrushAccent"));
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
                // 2026-11-06 (Б2-Т, живой тест Этапа 2): правило — «Минимальный остаток» карточки, а без него общий порог; подпись раньше называла только порог.
                // 2026-11-11 (жалоба «по дизайну почему пустота»): в плитке — короткая суть, полная фраза и «нажмите, чтобы показать» — в подсказке (иначе плитка в 3 строки раздувала весь ряд).
                T($"ниже минимума (<{CatalogProductTileVm.LowStockQuantityThreshold:0} без него)", $"минимумдан төмөн (жок болсо <{CatalogProductTileVm.LowStockQuantityThreshold:0})",
                    $"below minimum (<{CatalogProductTileVm.LowStockQuantityThreshold:0} if none)", $"asgarinin altı (yoksa <{CatalogProductTileVm.LowStockQuantityThreshold:0})",
                    $"minimaldan kam (yo'q bo'lsa <{CatalogProductTileVm.LowStockQuantityThreshold:0})"),
                low > 0 ? "BrushWarning" : null, "low",
                T($"ниже минимального остатка (без него — меньше {CatalogProductTileVm.LowStockQuantityThreshold:0}) — нажмите, чтобы показать", $"минималдуу калдыктан төмөн (жок болсо — {CatalogProductTileVm.LowStockQuantityThreshold:0} кем) — көрсөтүү үчүн басыңыз",
                    $"below the minimum stock (without it — under {CatalogProductTileVm.LowStockQuantityThreshold:0}) — click to show", $"asgari stokun altı (yoksa — {CatalogProductTileVm.LowStockQuantityThreshold:0} altı) — göstermek için tıklayın",
                    $"minimal qoldiqdan kam (u bo'lmasa — {CatalogProductTileVm.LowStockQuantityThreshold:0} dan kam) — ko'rsatish uchun bosing")),
            Card(T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), outOfStock.ToString("N0", RuCulture),
                T("остаток 0", "калдык 0", "stock 0", "stok 0", "qoldiq 0"),
                outOfStock > 0 ? "BrushDanger" : null, "out",
                T("остаток 0 — нажмите, чтобы показать", "калдык 0 — көрсөтүү үчүн басыңыз", "stock 0 — click to show", "stok 0 — göstermek için tıklayın", "qoldiq 0 — ko'rsatish uchun bosing")),
            Card(T("Срок годности", "Жарактуулук мөөнөтү", "Expiry", "Son kullanma", "Yaroqlilik muddati"),
                ProductExpiryIndex.IsLoaded ? marks.Count.ToString("N0", RuCulture) : "…",
                ProductExpiryIndex.IsLoaded
                    ? T($"просрочено {expired} · скоро {expiring}", $"мөөнөтү өткөн {expired} · жакында {expiring}", $"expired {expired} · soon {expiring}",
                        $"süresi geçmiş {expired} · yakında {expiring}", $"muddati o'tgan {expired} · yaqinda {expiring}")
                    : T("загружаю с сервера…", "серверден жүктөлүүдө…", "loading from the server…", "sunucudan yükleniyor…", "serverdan yuklanmoqda…"),
                expired > 0 ? "BrushDanger" : expiring > 0 ? "BrushWarning" : null, "expiry",
                T("нажмите, чтобы показать", "көрсөтүү үчүн басыңыз", "click to show", "göstermek için tıklayın", "ko'rsatish uchun bosing")),
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

        Control Card(string label, string value, string sub, string? accent, string? filterKey, string? tip = null)
        {
            var active = filterKey != null && filter == filterKey;
            var border = new Border { Classes = { "sec-kpi", "compact" }, VerticalAlignment = VerticalAlignment.Stretch };
            if (active)
            {
                border.BorderThickness = new Thickness(2);
                border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("BrushText"));
            }
            var stack = new StackPanel { Spacing = 2 };
            var l = new TextBlock { Text = label, Classes = { "sec-kpi-label" }, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            var v = new TextBlock { Text = value, Classes = { "sec-kpi-value" }, FontSize = 18 };
            if (accent != null)
                v.Bind(TextBlock.ForegroundProperty, v.GetResourceObservable(accent));
            // 2026-11-11: пояснение — не больше 2 строк (многоточие), полный текст — в подсказке плитки.
            var s = new TextBlock { Text = sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "kpi-sub" }, IsVisible = sub.Length > 0 };
            s.Bind(TextBlock.ForegroundProperty, s.GetResourceObservable("BrushTextSoft"));
            // 2026-11-11: подпись и значок «Настройка» в уголке — значок поверх подписи, высоту строки не увеличивает.
            var head = new Grid();
            head.Children.Add(l);
            stack.Children.Add(head);
            var valueBox = new Viewbox { Classes = { "sec-kpi-fit" }, Child = v };
            stack.Children.Add(valueBox);
            stack.Children.Add(s);
            // 2026-11-05 (волна 2-А, функция 1): на плитке «Срок годности» — «Списать просроченное» (если есть просроченное с остатком) и «Настройка» (режим проверки при продаже).
            // 2026-11-11 (жалоба «пустота»): две кнопки столбиком раздували плитку до 165 px и весь ряд сводки. Теперь «Настройка» — значок-шестерёнка в углу,
            // «Списать» — одной строкой справа от суммы (плитка не растёт).
            if (filterKey == "expiry")
            {
                var settingsText = T("Настройка", "Жөндөө", "Settings", "Ayarlar", "Sozlama");
                var setup = new Button
                {
                    Content = new TextBlock { Text = "⚙", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    Classes = { "SecondaryButton" }, Padding = new Thickness(0), MinHeight = 0, MinWidth = 0, Width = 36, Height = 36, // 2026-11-12 (аудит Д-12): цель касания 24 → 36 px; отрицательные поля не раздувают строку подписи
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -6, -8),
                };
                ToolTip.SetTip(setup, settingsText);
                Avalonia.Automation.AutomationProperties.SetName(setup, settingsText);
                setup.Click += async (_, e) =>
                {
                    e.Handled = true;
                    await ShopAutomationWindow.ShowAsync(this).ConfigureAwait(true);
                };
                head.Children.Add(setup);
                l.Margin = new Thickness(0, 0, 22, 0);
                if (expired > 0)
                {
                    // На плитке — короткая подпись (в строке с суммой, одной строкой), полное название — в подсказке и в окне списания.
                    var writeOffText = T("Списать просроченное", "Мөөнөтү өткөндү эсептен чыгаруу", "Write off expired", "Süresi geçmişi düş", "Muddati o'tganni hisobdan chiqarish");
                    var writeOffShort = T("Списать", "Чыгаруу", "Write off", "Düş", "Chiqarish");
                    var writeOff = new Button
                    {
                        Content = new TextBlock { Text = writeOffShort, FontSize = 12 },
                        Classes = { "SecondaryButton" }, Padding = new Thickness(8, 1), MinHeight = 36, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 6, 0, -6), // 2026-11-12 (аудит Д-12): «Списать» ~20 → 36 px высотой
                    };
                    Avalonia.Automation.AutomationProperties.SetName(writeOff, writeOffText);
                    ToolTip.SetTip(writeOff, writeOffText);
                    writeOff.Click += async (_, e) =>
                    {
                        e.Handled = true;
                        if (!EnsureCanEditProducts())
                            return;
                        if (await ExpiredWriteOff.RunAsync(this).ConfigureAwait(true) > 0)
                        {
                            RefreshWarehouseTotals();
                            await RefreshExpiryAsync(true).ConfigureAwait(true);
                        }
                    };
                    // Строка «сумма · кнопка»: сумма слева (Viewbox сжимает её при нехватке места), кнопка справа в той же строке — плитка не растёт.
                    var valueRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    stack.Children.Remove(valueBox);
                    valueRow.Children.Add(valueBox);
                    Grid.SetColumn(writeOff, 1);
                    valueRow.Children.Add(writeOff);
                    stack.Children.Insert(1, valueRow);
                }
            }
            border.Child = stack;
            ToolTip.SetTip(border, label + ": " + value + "\n" + (tip ?? sub));
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
        if (!EnsureCanEditProducts()) // 2026-11-03 (аудит №3, Т3-03): остаток правит владелец, администратор или сотрудник с правом «Склад: редактирование»
            return;
        var dialog = new StockAdjustDialog(product);
        if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
        {
            RefreshWarehouseTotals();
            _viewModel.ReapplyProductFilter();
        }
    }
}
