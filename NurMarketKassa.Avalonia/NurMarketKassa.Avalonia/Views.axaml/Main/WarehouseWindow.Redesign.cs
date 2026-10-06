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
        StockFilterPanel.Children.Add(new TextBlock
        {
            Text = T("Наличие", "Бар-жогу", "Stock", "Stok", "Mavjudlik"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 0, 4, 0),
        });
        ((TextBlock)StockFilterPanel.Children[0]).Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        foreach (var (key, text) in new[]
                 {
                     ("all", T("Все", "Баары", "All", "Tümü", "Hammasi")),
                     ("low", T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda")),
                     ("out", T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas")),
                     ("expiry", T("Срок годности", "Жарактуулук мөөнөтү", "Expiry", "Son kullanma", "Yaroqlilik muddati")),
                 })
        {
            var radio = new RadioButton { GroupName = "WarehouseStockFilter", Classes = { "SaleUnitPill" }, Content = text, IsChecked = key == _viewModel.StockFilter };
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

    /// <summary>2026-10-06, владелец: «открывать товар на складе голосом». Вкладка «Товары», фильтры сброшены, в поиске — товар.</summary>
    public void ShowProduct(CatalogProductTileVm product)
    {
        WarehouseTabs.SelectedItem = ProductsTabItem;
        if (_viewModel.StockFilter != "all")
            SetStockFilter("all");
        ProductSearchBox.Text = product.Title;
        Activate();
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
        WarehouseKpiPanel.ColumnDefinitions.Clear();
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
        for (var i = 0; i < cards.Count; i++)
        {
            WarehouseKpiPanel.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            Grid.SetColumn(cards[i], i);
            cards[i].Margin = new Thickness(i == 0 ? 0 : 6, 0, i == cards.Count - 1 ? 0 : 6, 0);
            WarehouseKpiPanel.Children.Add(cards[i]);
        }

        Control Card(string label, string value, string sub, string? accent, string? filterKey)
        {
            var active = filterKey != null && filter == filterKey;
            var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), BorderThickness = new Thickness(active ? 2 : 1), MinHeight = 82 };
            border.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
            border.Bind(Border.BorderBrushProperty, this.GetResourceObservable(active ? "BrushAccent" : "BrushBorder"));
            var stack = new StackPanel { Spacing = 3 };
            var l = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            l.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            var v = new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            v.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(accent ?? "BrushText"));
            var s = new TextBlock { Text = sub, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            s.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            stack.Children.Add(l);
            stack.Children.Add(v);
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
