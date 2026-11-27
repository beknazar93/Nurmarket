using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Analytics;

/// <summary>Аналитика склада: показатели, категории, «Заканчивается», «Нет в наличии» и самые дорогие остатки.
///
/// 2026-09-27 вынесена из окна склада (раньше — WarehouseWindow.RefreshAnalytics и разметка его
/// вкладки «Аналитика») в отдельный элемент: в программе владельца вся аналитика собрана в разделе
/// «Аналитика», и там она стоит вкладкой «Склад», а в кассе осталась вкладкой склада, как была.
/// Одна копия кода на оба места — правка расчёта не разойдётся.
///
/// 2026-11-27 (владелец: «Аналитика товаров в десктоп тоже добавь! Общее количество товаров, категории, общее количество, заканчивается — подробнее»):
/// расширена — плитки (товаров, единиц, стоимость по закупке и продаже, наценка, нет в наличии, заканчивается, в минусе, без цены / штрихкода / категории),
/// фильтры (поиск, категория), таблица категорий с сортировкой и раскрытием в список товаров, списки «Заканчивается» и «Нет в наличии», топ-20 по стоимости,
/// выгрузка в Excel. Весь расчёт — в <see cref="StockAnalytics"/> (одни формулы с «Склад → Товары»); здесь только показ. Разметка собрана в коде (как CategoryAnalyticsView):
/// колонки и подписи на 5 языках пересобираются при каждом <see cref="Refresh"/>, поэтому смена языка подхватывается.
///
/// Функция платная (UserPreferences.WarehouseAnalyticsUnlocked) — проверяет это тот, кто её показывает.</summary>
public sealed class WarehouseStockAnalyticsView : UserControl
{
    private readonly TextBox _search = new() { MinHeight = 40 };
    private readonly ComboBox _category = new() { MinHeight = 40, Width = 240 };
    private readonly Button _export = new() { MinHeight = 40, Padding = new Thickness(16, 0) };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly KpiTileGrid _tiles = new() { MinTileWidth = 170, Spacing = 8 };
    private readonly StackPanel _categoryBars = new() { Spacing = 10 };
    private readonly DataGrid _categoryGrid = MakeGrid(320);
    private readonly TextBlock _categoryProductsTitle = Heading();
    private readonly DataGrid _categoryProductsGrid = MakeGrid(320);
    private readonly TextBlock _lowTitle = Heading();
    private readonly DataGrid _lowGrid = MakeGrid(320);
    private readonly TextBlock _outTitle = Heading();
    private readonly DataGrid _outGrid = MakeGrid(320);
    private readonly TextBlock _topTitle = Heading();
    private readonly StackPanel _topBars = new() { Spacing = 10 };
    private readonly DataGrid _topGrid = MakeGrid(320);
    private readonly TextBlock _categoriesTitle = Heading();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private IReadOnlyList<string?> _categoryKeys = [];
    private bool _updating;
    private StockAnalyticsReport? _report;
    private string? _selectedCategoryName;

    public WarehouseStockAnalyticsView()
    {
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Recalculate(); };
        _category.SelectionChanged += (_, _) => { if (!_updating) Recalculate(); };
        _categoryGrid.SelectionChanged += (_, _) => ShowCategoryProducts();
        _export.Classes.Add("sec-outline");
        _export.Click += async (_, _) => await ExportAsync();
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushTextSoft"));

        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        _search.Margin = new Thickness(0, 0, 8, 0);
        _category.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_search, 0);
        Grid.SetColumn(_category, 1);
        Grid.SetColumn(_export, 2);
        filters.Children.Add(_search);
        filters.Children.Add(_category);
        filters.Children.Add(_export);

        var root = new StackPanel { Spacing = 16, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(filters);
        root.Children.Add(_status);
        root.Children.Add(_tiles);
        root.Children.Add(Section(_categoriesTitle, Card(_categoryBars), _categoryGrid, _categoryProductsTitle, _categoryProductsGrid));
        root.Children.Add(Section(_lowTitle, _lowGrid));
        root.Children.Add(Section(_outTitle, _outGrid));
        root.Children.Add(Section(_topTitle, Card(_topBars), _topGrid));
        _categoryProductsTitle.IsVisible = false;
        _categoryProductsGrid.IsVisible = false;

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 12),
        };
    }

    // ------------------------------------------------------------------ разметка

    private static TextBlock Heading()
    {
        var t = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 14 };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
        return t;
    }

    private static DataGrid MakeGrid(double maxHeight)
    {
        var g = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            MaxHeight = maxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        g.Bind(DataGrid.BackgroundProperty, g.GetResourceObservable("BrushPanel"));
        g.Bind(DataGrid.BorderBrushProperty, g.GetResourceObservable("BrushBorder"));
        return g;
    }

    private static Border Card(Control child)
    {
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Child = child };
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable("BrushPanel"));
        b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("BrushBorder"));
        return b;
    }

    private static StackPanel Section(params Control[] parts)
    {
        var s = new StackPanel { Spacing = 8 };
        foreach (var p in parts)
            s.Children.Add(p);
        return s;
    }

    /// <summary>Колонка таблицы: текст с многоточием (длинное название не вылезает из ячейки, полный — во всплывающей подсказке); сортировка — по <paramref name="sortPath"/> (число), если задана.
    /// Наименьшая ширина — по длине заголовка (надпись на любом из 5 языков целиком, с местом под стрелку сортировки).</summary>
    private static DataGridTemplateColumn Col(string header, string path, string? sortPath = null, double minWidth = 90, string width = "*") =>
        new()
        {
            Header = header,
            SortMemberPath = sortPath ?? path,
            MinWidth = Math.Max(minWidth, header.Length * 8 + 48),
            Width = new DataGridLength(width.TrimEnd('*') is { Length: > 0 } k ? double.Parse(k, System.Globalization.CultureInfo.InvariantCulture) : 1, DataGridLengthUnitType.Star),
            CellTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((_, _) =>
            {
                var t = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
                t.Bind(TextBlock.TextProperty, new Binding(path));
                t.Bind(ToolTip.TipProperty, new Binding(path));
                return t;
            }),
        };

    // ------------------------------------------------------------------ строки таблиц (показ; числа — для сортировки)

    private sealed class CategoryRowVm
    {
        public string Name { get; init; } = "";
        public int Positions { get; init; }
        public double Units { get; init; }
        public double Value { get; init; }
        public double Purchase { get; init; }
        public int Low { get; init; }
        public int Out { get; init; }
        public double Share { get; init; }
        public string UnitsText => MoneyText.Qty(Units);
        public string ValueText => MoneyText.Som(Value);
        public string PurchaseText => MoneyText.Som(Purchase);
        public string ShareText => MoneyText.Weight(Share, "0.#") + " %";
        public StockCategoryRow Source { get; init; } = null!;
    }

    private sealed class ProductRowVm
    {
        public string Title { get; init; } = "";
        public string Category { get; init; } = "";
        public double Quantity { get; init; }
        public double Min { get; init; }
        public double Price { get; init; }
        public double Value { get; init; }
        public double Purchase { get; init; }
        public string StockText { get; init; } = "";
        public string MinText => MoneyText.Qty(Min);
        public string PriceText => MoneyText.PriceSom(Price);
        public string ValueText => MoneyText.Som(Value);
        public string PurchaseText => MoneyText.Som(Purchase);
    }

    private static ProductRowVm ToVm(StockProductRow r) => new()
    {
        Title = r.Title,
        Category = r.Category.Length == 0 ? StockAnalytics.NoCategoryLabel : r.Category,
        Quantity = r.Quantity,
        Min = r.Threshold,
        Price = r.SalePrice,
        Value = r.SaleValue,
        Purchase = r.PurchaseValue ?? 0,
        StockText = MoneyText.Qty(r.Quantity) + (r.Unit.Length > 0 ? " " + r.Unit : ""),
    };

    // ------------------------------------------------------------------ расчёт и показ

    /// <summary>Считается локально из уже загруженного каталога (CatalogCacheService.Products) —
    /// без отдельных запросов к серверу, поэтому мгновенно и без дополнительной нагрузки.</summary>
    public void Refresh()
    {
        ApplyTexts();
        FillCategoryOptions();
        Recalculate();
    }

    private void ApplyTexts()
    {
        _search.Watermark = Tr.T("Поиск товара…", "Товарды издөө…", "Search a product…", "Ürün ara…", "Mahsulot qidirish…");
        _export.Content = "📊 " + Tr.T("Excel", "Excel", "Excel", "Excel", "Excel");
        Avalonia.Automation.AutomationProperties.SetName(_export, Tr.T("Выгрузить аналитику склада в Excel", "Кампанын аналитикасын Excel'ге чыгаруу", "Export stock analytics to Excel", "Depo analizini Excel'e aktar", "Ombor tahlilini Excel'ga yuklash"));
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        Avalonia.Automation.AutomationProperties.SetName(_category, Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya"));
    }

    private void FillCategoryOptions()
    {
        var (names, hasNone) = StockAnalytics.CategoryOptions(CatalogCacheService.Products.ToArray());
        var keys = new List<string?> { null };
        var labels = new List<string> { Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar") };
        foreach (var n in names)
        {
            keys.Add(n);
            labels.Add(n);
        }
        if (hasNone)
        {
            keys.Add(StockAnalytics.NoCategoryKey);
            labels.Add(StockAnalytics.NoCategoryLabel);
        }

        var current = SelectedCategoryKey();
        _updating = true;
        try
        {
            _categoryKeys = keys;
            _category.ItemsSource = labels;
            var at = current is null ? 0 : keys.ToList().FindIndex(k => string.Equals(k, current, StringComparison.CurrentCultureIgnoreCase));
            _category.SelectedIndex = at < 0 ? 0 : at;
        }
        finally
        {
            _updating = false;
        }
    }

    private string? SelectedCategoryKey()
    {
        var i = _category.SelectedIndex;
        return i >= 0 && i < _categoryKeys.Count ? _categoryKeys[i] : null;
    }

    private void Recalculate()
    {
        var all = CatalogCacheService.Products.ToArray();
        var showPurchase = WarehouseWindow.RevisionShowPurchase;
        var report = StockAnalytics.Build(all, showPurchase, SelectedCategoryKey(), _search.Text);
        _report = report;
        Show(report, all.Length);
    }

    private void Show(StockAnalyticsReport r, int catalogTotal)
    {
        // ---- плитки
        _tiles.Children.Clear();
        _tiles.Children.Add(Tile(Tr.T("Всего товаров (позиций)", "Бардык товарлар (позициялар)", "Products (items) in total", "Toplam ürün (kalem)", "Jami mahsulotlar (pozitsiyalar)"), r.Positions.ToString("N0")));
        _tiles.Children.Add(Tile(Tr.T("Единиц на складе", "Кампадагы бирдиктер", "Units in stock", "Stoktaki adet", "Ombordagi birliklar"), r.Units.ToString("#,0.###"))); // 2026-11-27: с разрядами, как «Единиц на складе» в «Склад → Товары»
        if (r.PurchaseValue is { } buy)
            _tiles.Children.Add(Tile(Tr.T("Стоимость по закупке", "Сатып алуу баасы боюнча", "Value at purchase price", "Alış fiyatıyla değer", "Xarid narxidagi qiymat"), MoneyText.Som(buy)));
        _tiles.Children.Add(Tile(Tr.T("Стоимость по продаже", "Сатуу баасы боюнча", "Value at sale price", "Satış fiyatıyla değer", "Sotuv narxidagi qiymat"), MoneyText.Som(r.SaleValue)));
        if (r.Markup is { } markup)
            _tiles.Children.Add(Tile(Tr.T("Потенциальная наценка", "Мүмкүн болгон кошумча баа", "Potential markup", "Potansiyel kâr marjı", "Potensial ustama"), MoneyText.Som(markup),
                r.MarkupPercent is { } pct ? "+" + MoneyText.Weight(pct, "0.#") + " % " + Tr.T("к закупке", "сатып алууга карата", "on cost", "maliyete göre", "xaridga nisbatan") : null));
        _tiles.Children.Add(Tile(Tr.T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), r.OutOfStock.ToString("N0")));
        _tiles.Children.Add(Tile(Tr.T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda"), r.Low.ToString("N0"),
            Tr.T("ниже минимума карточки или меньше 10", "карточканын минимумунан төмөн же 10дон аз", "below the card minimum or under 10", "kart minimumunun altında veya 10'dan az", "karta minimumidan past yoki 10 dan kam")));
        _tiles.Children.Add(Tile(Tr.T("В минусе", "Минуста", "Negative stock", "Eksi stok", "Minusda"), r.Negative.ToString("N0")));
        _tiles.Children.Add(Tile(Tr.T("Без цены", "Баасыз", "No price", "Fiyatsız", "Narxsiz"), r.NoPrice.ToString("N0")));
        _tiles.Children.Add(Tile(Tr.T("Без штрихкода", "Штрихкодсуз", "No barcode", "Barkodsuz", "Shtrixkodsiz"), r.NoBarcode.ToString("N0")));
        _tiles.Children.Add(Tile(Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz"), r.NoCategory.ToString("N0")));

        // ---- строка состояния
        var parts = new List<string>();
        if (r.Positions != catalogTotal)
            parts.Add(Tr.T($"По фильтру: {r.Positions} из {catalogTotal} товаров", $"Чыпка боюнча: {r.Positions} / {catalogTotal} товар", $"Filtered: {r.Positions} of {catalogTotal} products", $"Filtreye göre: {r.Positions} / {catalogTotal} ürün", $"Filtr bo'yicha: {r.Positions} / {catalogTotal} mahsulot"));
        if (catalogTotal == 0)
            parts.Add(Tr.T("Каталог пуст — товары появятся после загрузки каталога.", "Каталог бош — товарлар каталог жүктөлгөндөн кийин чыгат.", "The catalog is empty — products appear once the catalog loads.", "Katalog boş — ürünler katalog yüklenince görünür.", "Katalog bo'sh — mahsulotlar katalog yuklangach paydo bo'ladi."));
        if (!r.ShowPurchase)
            parts.Add(Tr.T("Закупочные цены скрыты: нет права.", "Сатып алуу баалары жашырылган: уруксат жок.", "Purchase prices are hidden: no permission.", "Alış fiyatları gizli: yetki yok.", "Xarid narxlari yashirilgan: ruxsat yo'q."));
        if (CatalogCacheService.LastSyncTime is { } synced)
            parts.Add(Tr.T("Каталог обновлён ", "Каталог жаңыртылды ", "Catalog updated ", "Katalog güncellendi ", "Katalog yangilandi ") + (synced.Kind == DateTimeKind.Local ? synced : DateTime.SpecifyKind(synced, DateTimeKind.Utc).ToLocalTime()).ToString("dd.MM HH:mm")); // 2026-11-27: время синхронизации хранится в UTC — показываем местное
        SetStatus(string.Join("  ·  ", parts));

        // ---- категории
        _categoriesTitle.Text = Tr.T("По категориям", "Категориялар боюнча", "By category", "Kategorilere göre", "Kategoriyalar bo'yicha") + $" ({r.Categories.Count})";
        _categoryBars.Children.Clear();
        BarChartRenderer.Render(_categoryBars, r.Categories.Where(c => c.SaleValue > 0).Take(10)
            .Select(c => (c.Name, c.SaleValue, MoneyText.Som(c.SaleValue) + " · " + MoneyText.Weight(c.Share, "0.#") + " %")).ToList());

        _categoryGrid.Columns.Clear();
        _categoryGrid.Columns.Add(Col(Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya"), nameof(CategoryRowVm.Name), minWidth: 160, width: "2*"));
        _categoryGrid.Columns.Add(Col(Tr.T("Товаров", "Товарлар", "Products", "Ürün", "Mahsulotlar"), nameof(CategoryRowVm.Positions)));
        _categoryGrid.Columns.Add(Col(Tr.T("Единиц", "Бирдик", "Units", "Adet", "Birliklar"), nameof(CategoryRowVm.UnitsText), nameof(CategoryRowVm.Units)));
        if (r.ShowPurchase)
            _categoryGrid.Columns.Add(Col(Tr.T("По закупке", "Сатып алуу боюнча", "At purchase", "Alış", "Xarid bo'yicha"), nameof(CategoryRowVm.PurchaseText), nameof(CategoryRowVm.Purchase), minWidth: 120));
        _categoryGrid.Columns.Add(Col(Tr.T("Стоимость", "Наркы", "Value", "Değer", "Qiymat"), nameof(CategoryRowVm.ValueText), nameof(CategoryRowVm.Value), minWidth: 120));
        _categoryGrid.Columns.Add(Col(Tr.T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda"), nameof(CategoryRowVm.Low), minWidth: 130));
        _categoryGrid.Columns.Add(Col(Tr.T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas"), nameof(CategoryRowVm.Out), minWidth: 130));
        _categoryGrid.Columns.Add(Col(Tr.T("Доля", "Үлүшү", "Share", "Pay", "Ulush"), nameof(CategoryRowVm.ShareText), nameof(CategoryRowVm.Share), minWidth: 80));
        var categoryRows = r.Categories.Select(c => new CategoryRowVm
        {
            Name = c.Name, Positions = c.Positions, Units = c.Units, Value = c.SaleValue, Purchase = c.PurchaseValue ?? 0,
            Low = c.Low, Out = c.OutOfStock, Share = c.Share, Source = c,
        }).ToList();
        _categoryGrid.ItemsSource = categoryRows;
        var keep = categoryRows.FirstOrDefault(c => c.Name == _selectedCategoryName);
        if (keep is not null)
            _categoryGrid.SelectedItem = keep;
        ShowCategoryProducts();

        // ---- заканчивается / нет в наличии
        _lowTitle.Text = Tr.T("Заканчивается", "Түгөнүп баратат", "Running low", "Tükenmek üzere", "Tugab bormoqda") + $" ({r.LowItems.Count})";
        FillProductGrid(_lowGrid, r.LowItems, withMin: true, withValue: false, r.ShowPurchase);
        _outTitle.Text = Tr.T("Нет в наличии", "Калдыкта жок", "Out of stock", "Stokta yok", "Mavjud emas") + $" ({r.OutItems.Count})";
        FillProductGrid(_outGrid, r.OutItems, withMin: false, withValue: false, r.ShowPurchase);

        // ---- самые дорогие остатки
        _topTitle.Text = Tr.T($"Топ-{StockAnalytics.TopCount} по стоимости остатка", $"Калдыктын наркы боюнча топ-{StockAnalytics.TopCount}", $"Top {StockAnalytics.TopCount} by stock value", $"Stok değerine göre ilk {StockAnalytics.TopCount}", $"Qoldiq qiymati bo'yicha top-{StockAnalytics.TopCount}");
        BarChartRenderer.Render(_topBars, r.TopByValue.Take(10).Select(x => (x.Title, x.SaleValue, MoneyText.Som(x.SaleValue))).ToList());
        FillProductGrid(_topGrid, r.TopByValue, withMin: false, withValue: true, r.ShowPurchase);
    }

    private static void FillProductGrid(DataGrid grid, IReadOnlyList<StockProductRow> rows, bool withMin, bool withValue, bool showPurchase)
    {
        grid.Columns.Clear();
        grid.Columns.Add(Col(Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), nameof(ProductRowVm.Title), minWidth: 180, width: "3*"));
        grid.Columns.Add(Col(Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya"), nameof(ProductRowVm.Category), minWidth: 120, width: "2*"));
        grid.Columns.Add(Col(Tr.T("Остаток", "Калдык", "In stock", "Stok", "Qoldiq"), nameof(ProductRowVm.StockText), nameof(ProductRowVm.Quantity)));
        if (withMin)
            grid.Columns.Add(Col(Tr.T("Минимум", "Минимум", "Minimum", "Minimum", "Minimum"), nameof(ProductRowVm.MinText), nameof(ProductRowVm.Min)));
        if (withValue)
        {
            grid.Columns.Add(Col(Tr.T("Цена за единицу", "Бирдиктин баасы", "Unit price", "Birim fiyat", "Birlik narxi"), nameof(ProductRowVm.PriceText), nameof(ProductRowVm.Price), minWidth: 120));
            if (showPurchase)
                grid.Columns.Add(Col(Tr.T("По закупке", "Сатып алуу боюнча", "At purchase", "Alış", "Xarid bo'yicha"), nameof(ProductRowVm.PurchaseText), nameof(ProductRowVm.Purchase), minWidth: 120));
            grid.Columns.Add(Col(Tr.T("Стоимость", "Наркы", "Value", "Değer", "Qiymat"), nameof(ProductRowVm.ValueText), nameof(ProductRowVm.Value), minWidth: 120));
        }
        grid.ItemsSource = rows.Select(ToVm).ToList();
    }

    /// <summary>Раскрытие категории: выбранная строка таблицы — список её товаров ниже.</summary>
    private void ShowCategoryProducts()
    {
        if (_categoryGrid.SelectedItem is not CategoryRowVm row)
        {
            _selectedCategoryName = null;
            _categoryProductsTitle.IsVisible = false;
            _categoryProductsGrid.IsVisible = false;
            return;
        }

        _selectedCategoryName = row.Name;
        _categoryProductsTitle.Text = Tr.T("Товары категории", "Категориядагы товарлар", "Products in category", "Kategorideki ürünler", "Kategoriyadagi mahsulotlar") + $" «{row.Name}» ({row.Positions})";
        FillProductGrid(_categoryProductsGrid, row.Source.Products, withMin: true, withValue: true, _report?.ShowPurchase ?? false);
        _categoryProductsTitle.IsVisible = true;
        _categoryProductsGrid.IsVisible = true;
    }

    private static Border Tile(string label, string value, string? hint = null)
    {
        var l = new TextBlock { Text = label };
        l.Classes.Add("sec-kpi-label");
        var v = new TextBlock { Text = value };
        v.Classes.Add("sec-kpi-value");
        var fit = new Viewbox { Child = v };
        fit.Classes.Add("sec-kpi-fit");
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(l);
        stack.Children.Add(fit);
        if (!string.IsNullOrEmpty(hint))
        {
            var h = new TextBlock { Text = hint, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            h.Bind(TextBlock.ForegroundProperty, h.GetResourceObservable("BrushTextSoft"));
            stack.Children.Add(h);
        }
        var tile = new Border { Child = stack };
        tile.Classes.Add("sec-kpi");
        Avalonia.Automation.AutomationProperties.SetName(tile, label + ": " + value);
        return tile;
    }

    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
    }

    // ------------------------------------------------------------------ Excel

    private async System.Threading.Tasks.Task ExportAsync()
    {
        if (_report is null)
            return;
        if (!TariffGate.CanUseAnalyticsExport)
        {
            SetStatus(TariffGate.AnalyticsExportLockedMessage);
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;
        var shop = ReportShopName.Current;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Сохранить отчёт в Excel", "Отчётту Excel форматында сактоо", "Save the report to Excel", "Raporu Excel olarak kaydet", "Hisobotni Excel formatida saqlash"),
            SuggestedFileName = StockAnalyticsExport.SuggestFileName(shop),
            FileTypeChoices = [new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] }],
        });
        if (file is null)
            return;

        var path = ExportFileBridge.PathFor(file);
        try
        {
            var report = _report!;
            var note = FilterNote();
            await System.Threading.Tasks.Task.Run(() => StockAnalyticsExport.ExportToExcel(path!, report, shop, note)).ConfigureAwait(true);
            path = await ExportFileBridge.CompleteAsync(file, path);
            SetStatus(Tr.T("Отчёт сохранён", "Отчёт сакталды", "Report saved", "Rapor kaydedildi", "Hisobot saqlandi") + $": {path}");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выгрузка аналитики склада не удалась: {ex}", "WARNING");
            SetStatus(Tr.T("Не удалось сохранить отчёт", "Отчётту сактоо мүмкүн болгон жок", "Could not save the report", "Rapor kaydedilemedi", "Hisobotni saqlab bo'lmadi") + ": " + ex.Message);
        }
    }

    /// <summary>Подпись фильтра для шапки файла: что выбрано в списке категорий и что набрано в поиске.</summary>
    private string? FilterNote()
    {
        var parts = new List<string>();
        if (SelectedCategoryKey() is { } key)
            parts.Add(Tr.T("Категория: ", "Категория: ", "Category: ", "Kategori: ", "Kategoriya: ") + (key.Length == 0 ? StockAnalytics.NoCategoryLabel : key));
        if (!string.IsNullOrWhiteSpace(_search.Text))
            parts.Add(Tr.T("Поиск: ", "Издөө: ", "Search: ", "Arama: ", "Qidiruv: ") + _search.Text!.Trim());
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
