using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-22 (владелец: «в штрих-кодах добавь отображение товаров в виде списка тоже»): часть BarcodePrintView — вид «Список» рядом с карточками.
/// Список — компактная таблица строк (галочка «В печать», название, штрихкод, артикул, цена, остаток, копии, кнопка печати) и показывает ВЕСЬ найденный список сразу, без страниц:
/// строки виртуализированы (VirtualizingStackPanel + переиспользование строк), поэтому на 15 000 товаров первый показ и прокрутка быстрые. Выбор, копии и выбранный штрихкод лежат в тех же
/// <c>_sel</c>, что и у карточек, — при переключении видов ничего не теряется. Выбранный вид запоминается (UserPreferences.BarcodePrintListView). Цены и остатки — готовые строки каталога, во view не считаются.</summary>
public partial class BarcodePrintView
{
    private const double ListRowHeight = 48;

    private bool _listMode;
    private Button _viewListButton = null!, _viewTilesButton = null!;
    private Border? _listHost;
    private ItemsControl? _listItems;
    private ScrollViewer? _listScroll;
    private Grid? _listHeader;
    private int _listLevel;
    private readonly HashSet<ListRow> _liveRows = new();

    /// <summary>Сейчас показан список строк (а не карточки).</summary>
    public bool ListMode => _listMode;

    /// <summary>Сколько строк списка сейчас создано и стоит на экране (виртуализация: на 15 000 товаров — десятки, а не тысячи).</summary>
    public int ListRealizedRows => _liveRows.Count;

    /// <summary>Переключатель «Список / Плитки» для панели инструментов.</summary>
    private Control BuildViewToggle()
    {
        _listMode = UserPreferences.Instance.BarcodePrintListView;
        _viewListButton = UiKit.Seg(T("Список", "Тизме", "List", "Liste", "Ro'yxat"), _listMode);
        _viewTilesButton = UiKit.Seg(T("Плитки", "Плиткалар", "Tiles", "Kutucuklar", "Plitkalar"), !_listMode);
        foreach (var b in new[] { _viewListButton, _viewTilesButton })
        {
            b.MinHeight = 44;
            b.Padding = new Thickness(12, 0);
        }
        ToolTip.SetTip(_viewListButton, T("Товары строками — компактно, удобно на большом каталоге", "Товарлар сап менен — тыкан, чоң каталогго ыңгайлуу", "Products as rows — compact, handy for a large catalog", "Ürünler satır satır — derli toplu, büyük katalogda kullanışlı", "Mahsulotlar qatorlar bilan — ixcham, katta katalogda qulay"));
        ToolTip.SetTip(_viewTilesButton, T("Товары карточками", "Товарлар карточкалар менен", "Products as cards", "Ürünler kartlar halinde", "Mahsulotlar kartochkalar bilan"));
        Avalonia.Automation.AutomationProperties.SetName(_viewListButton, T("Список", "Тизме", "List", "Liste", "Ro'yxat"));
        Avalonia.Automation.AutomationProperties.SetName(_viewTilesButton, T("Плитки", "Плиткалар", "Tiles", "Kutucuklar", "Plitkalar"));
        _viewListButton.Click += (_, _) => SetListMode(true);
        _viewTilesButton.Click += (_, _) => SetListMode(false);
        return UiKit.Segment(new StackPanel { Orientation = Orientation.Horizontal, Children = { _viewListButton, _viewTilesButton } });
    }


    /// <summary>2026-11-22: на низком окне переключатель — два значка (☰ список, ▦ плитки) по 44 px: названия остаются в подсказке и в имени для диктора.</summary>
    private void SetViewToggleCompact(bool compact)
    {
        if (_viewListButton is null)
            return;
        _viewListButton.Content = compact ? "☰" : T("Список", "Тизме", "List", "Liste", "Ro'yxat");
        _viewTilesButton.Content = compact ? "▦" : T("Плитки", "Плиткалар", "Tiles", "Kutucuklar", "Plitkalar");
        foreach (var b in new[] { _viewListButton, _viewTilesButton })
            b.MinWidth = compact ? 44 : 0;
    }
    /// <summary>Включить вид «Список» (true) или «Плитки» (false); выбор запоминается. Поиск, фильтры и выбор товаров остаются как были.</summary>
    public void SetListMode(bool list)
    {
        if (list == _listMode && (_listHost?.IsVisible ?? !list))
            return;
        _listMode = list;
        UiKit.SetActive(_viewListButton, list);
        UiKit.SetActive(_viewTilesButton, !list);
        if (UserPreferences.Instance.BarcodePrintListView != list)
        {
            UserPreferences.Instance.BarcodePrintListView = list;
            UserPreferences.Instance.SaveToDisk();
        }
        RenderPage();
    }

    /// <summary>Вызывается в начале RenderPage: готовит место под вид (список или карточки). true — показываем список (страниц нет, все найденные строки).</summary>
    private bool PrepareView()
    {
        if (_listMode)
            EnsureListHost();
        if (_listHost != null)
        {
            _listHost.IsVisible = _listMode;
            _scroll.IsVisible = !_listMode;
            _prevButton.IsVisible = _nextButton.IsVisible = !_listMode; // в списке листать нечего
        }
        return _listMode;
    }

    private void EnsureListHost()
    {
        if (_listHost != null)
            return;
        var items = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<CatalogProductTileVm>((p, _) => new ListRow(this), supportsRecycling: true),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
        };
        // Прокрутка — внутри шаблона самого списка (как в «Приёмке»): создаются только видимые строки.
        items.Template = new FuncControlTemplate<ItemsControl>((_, scope) =>
        {
            var presenter = new ItemsPresenter { Name = "PART_ItemsPresenter", ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) }.RegisterInNameScope(scope);
            _listScroll = new ScrollViewer { Content = presenter, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            return _listScroll;
        });
        _listItems = items;
        _listHeader = BuildListHeader();
        var dock = new DockPanel { LastChildFill = true };
        var headerBox = new Border { Child = _listHeader, BorderThickness = new Thickness(0, 0, 0, 1) }; // шапка отделена линией от строк
        Use(headerBox, Border.BorderBrushProperty, "BrushBorder");
        DockPanel.SetDock(headerBox, Dock.Top);
        dock.Children.Add(headerBox);
        dock.Children.Add(items);
        _listHost = new Border
        {
            Child = dock, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, IsVisible = false,
        };
        Use(_listHost, Border.BackgroundProperty, "BrushPanel");
        Use(_listHost, Border.BorderBrushProperty, "BrushBorder");
        _listHost.SizeChanged += (_, e) => ApplyListLevel(e.NewSize.Width);
        if (_scroll.Parent is Panel host)
            host.Children.Insert(host.Children.IndexOf(_scroll) + 1, _listHost); // над карточками, под надписью «ничего не найдено»
    }

    /// <summary>Ширина списка → набор колонок (функция только ширины, без «памяти»): 0 — все, 1 — без артикула и остатка, 2 — ещё уже.</summary>
    private void ApplyListLevel(double width)
    {
        if (width <= 0)
            return;
        var level = width >= 910 ? 0 : width >= 700 ? 1 : 2; // 2026-11-27: пороги +70/+60 px — колонка штрихкода 200 → 246 px
        if (level == _listLevel)
            return;
        _listLevel = level;
        if (_listHeader != null)
            ApplyColumns(_listHeader, level);
        foreach (var row in _liveRows)
            row.ApplyLevel();
    }

    // колонки: 0 галочка · 1 название · 2 штрихкод · 3 артикул · 4 цена · 5 остаток · 6 копии · 7 печать
    // 2026-11-27: штрихкод 200 → 246 px — «4600000000028  (основной)» в списке выбора не влезал (нужно 206–220 px, было 180; TextNotClipped «barcode-print»).
    private static ColumnDefinitions ListColumns(int level)
    {
        var cols = new ColumnDefinitions();
        double[] w = level switch { 0 => new double[] { 44, -1, 246, 100, 96, 72, 126, 52 }, 1 => new double[] { 44, -1, 246, 0, 96, 0, 126, 52 }, _ => new double[] { 44, -1, 150, 0, 80, 0, 118, 52 } };
        foreach (var x in w)
            cols.Add(x < 0 ? new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 120 } : new ColumnDefinition(x, GridUnitType.Pixel));
        return cols;
    }

    private static void ApplyColumns(Grid grid, int level)
    {
        grid.ColumnDefinitions = ListColumns(level);
        foreach (var c in grid.Children.OfType<Control>())
            if (Grid.GetColumn(c) is 3 or 5)
                c.IsVisible = level == 0;
    }

    private Grid BuildListHeader()
    {
        var grid = new Grid { Height = 36, Margin = new Thickness(10, 0, 18, 0) };
        void Cell(int col, string text, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        Cell(1, T("Название", "Аталышы", "Name", "Ad", "Nomi"));
        Cell(2, T("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"));
        Cell(3, T("Артикул", "Артикул", "SKU", "Stok kodu", "Artikul"));
        Cell(4, T("Цена", "Баасы", "Price", "Fiyat", "Narx"));
        Cell(5, T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"));
        Cell(6, T("Копий", "Көчүрмө", "Copies", "Kopya", "Nusxa"));
        // колонка 0 — «В печать» (подпись в подсказке: колонка 44 px слишком узка для слова)
        var print = new TextBlock { Text = "✓", FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Use(print, TextBlock.ForegroundProperty, "BrushTextSoft");
        ToolTip.SetTip(print, T("В печать", "Басууга", "Print", "Yazdır", "Chopga"));
        grid.Children.Add(print);
        ApplyColumns(grid, _listLevel);
        return grid;
    }

    /// <summary>Строки списка: каждая рисуется один раз на строку-контейнер и при прокрутке только перепривязывается к другому товару.</summary>
    private void RenderListRows()
    {
        var items = _listItems!;
        if (!ReferenceEquals(items.ItemsSource, _found))
        {
            items.ItemsSource = _found;
            if (_listScroll != null)
                _listScroll.Offset = default; // новый поиск или фильтр — список с начала
        }
        else
            foreach (var row in _liveRows.ToList())
                row.Rebind(); // тот же список, но данные могли поменяться (шаблон этикетки, новый штрихкод)
        _pageSync.Add(SyncListRows);
        _copiesSync.Add(SyncListRows);
        _codeSync.Add(SyncListRows);
        _warnSync.Add(SyncListRows);
    }

    /// <summary>Подвал в списке: страниц нет — сколько товаров в списке.</summary>
    private void ShowListPager()
    {
        var n = MoneyText.Weight((decimal)_found.Count, "N0").Replace(',', ' '); // 2026-11-27: число через общий форматтер (NumberFormatRatchetTests)
        _pagerText.Text = T($"В списке: {n}", $"Тизмеде: {n}", $"In list: {n}", $"Listede: {n}", $"Ro'yxatda: {n}");
        _prevButton.IsEnabled = _nextButton.IsEnabled = false;
    }

    private void SyncListRows()
    {
        foreach (var row in _liveRows)
            row.Sync();
    }

    private static string StockText(double quantity) =>
        quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).Replace(".", BarcodePrintCatalog.LabelDecimalSeparator);

    // ------------------------------------------------------------------ строка списка

    private sealed class ListRow : Border
    {
        private readonly BarcodePrintView _v;
        private CatalogProductTileVm? _p;
        private bool _syncing;
        private int _level = -1;
        private readonly Grid _grid = new();
        private readonly CheckBox _check;
        private readonly TextBlock _name, _article, _price, _stock, _codeText, _warn, _copiesText;
        private readonly Grid _codeHost = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        private ComboBox? _codeCombo;
        private readonly Border _copiesBox, _copiesHost;
        private NumericUpDown? _copies;
        private readonly Button _printBtn, _createBtn;

        public ListRow(BarcodePrintView v)
        {
            _v = v;
            Height = ListRowHeight;
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(10, 0, 18, 0);
            v.Use(this, BorderBrushProperty, "BrushBorder");

            _check = new CheckBox { MinHeight = 44, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(_check, T("В печать", "Басууга", "Print", "Yazdır", "Chopga"));
            Place(_check, 0);

            _name = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new Thickness(0, 0, 8, 0) };
            v.Use(_name, TextBlock.ForegroundProperty, "BrushText");
            Place(_name, 1);

            _codeText = new TextBlock { FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _warn = new TextBlock { Text = "⚠", FontSize = 17, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), IsVisible = false };
            v.Use(_warn, TextBlock.ForegroundProperty, "BrushWarning");
            _codeHost.Children.Add(_codeText);
            Grid.SetColumn(_warn, 1);
            _codeHost.Children.Add(_warn);
            _codeHost.Margin = new Thickness(0, 0, 8, 0);
            Place(_codeHost, 2);

            _article = Soft(); Place(_article, 3);
            _price = Soft(); _price.FontWeight = FontWeight.SemiBold; v.Use(_price, TextBlock.ForegroundProperty, "BrushText"); Place(_price, 4);
            _stock = Soft(); Place(_stock, 5);

            // Поле «Копий»: до выбора строки — лёгкая «коробка» с числом (NumericUpDown тяжёлый), настоящее поле — при выборе, наведении или нажатии (как в карточке).
            _copiesText = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _copiesBox = new Border { MinHeight = 44, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5), Child = _copiesText, Focusable = true, IsTabStop = true };
            v.Use(_copiesBox, BackgroundProperty, "BrushPanel");
            v.Use(_copiesBox, BorderBrushProperty, "BrushInputBorder");
            v.Use(_copiesText, TextBlock.ForegroundProperty, "BrushText");
            AutomationProperties.SetName(_copiesBox, T("Копий", "Көчүрмө", "Copies", "Kopya", "Nusxa"));
            _copiesHost = new Border { Child = _copiesBox, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Place(_copiesHost, 6);
            _copiesBox.PointerEntered += (_, _) => TryRealize();
            _copiesBox.PointerPressed += (_, _) => TryRealize();
            _copiesBox.GotFocus += (_, _) => TryRealize()?.Focus();

            _printBtn = new Button { MinHeight = 44, MinWidth = 44, CornerRadius = new CornerRadius(10), Focusable = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0) };
            _printBtn.Classes.Add("sec-primary");
            _printBtn.Content = MarketplaceIcons.Icon("printer-outline", 18);
            var printText = T("Распечатать штрих-код", "Штрих-кодду басуу", "Print barcode", "Barkodu yazdır", "Shtrix-kodni chop etish");
            ToolTip.SetTip(_printBtn, printText);
            AutomationProperties.SetName(_printBtn, printText);
            _printBtn.Click += async (_, _) => { if (_p != null) await _v.PrintOneAsync(_p).ConfigureAwait(true); };
            _createBtn = new Button { MinHeight = 44, MinWidth = 44, CornerRadius = new CornerRadius(10), Focusable = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Content = "＋", FontSize = 18, IsVisible = false };
            _createBtn.Classes.Add("sec-outline");
            var createText = T("Создать штрих-код", "Штрих-код түзүү", "Create a barcode", "Barkod oluştur", "Shtrix-kod yaratish");
            ToolTip.SetTip(_createBtn, createText);
            AutomationProperties.SetName(_createBtn, createText);
            _createBtn.Click += async (_, _) => { if (_p != null) await _v.CreateBarcodeAsync(_p).ConfigureAwait(true); };
            Place(new Grid { Children = { _printBtn, _createBtn } }, 7);

            Child = _grid;
            // 2026-11-22: как у карточки — «В печать» помечает найденный товар, поле поиска очищается и фокус возвращается в него (Click приходит только от человека).
            _check.Click += (_, _) =>
            {
                if (_check.IsChecked == true && _p != null && _v.IsPrintable(_p))
                    _v.ClearSearchAfterPick();
            };
            _check.IsCheckedChanged += (_, _) =>
            {
                if (_syncing || _p is null)
                    return;
                var st = _v.GetSel(_p.Id);
                _v.SetSelected(st, _check.IsChecked == true);
                if (st.Selected && _v.IsPrintable(_p))
                    TryRealize();
                ApplyStyle(st);
                _v.UpdateSelectionUi();
                _v.SchedulePreview();
            };
            AttachedToVisualTree += (_, _) => _v._liveRows.Add(this);
            DetachedFromVisualTree += (_, _) => _v._liveRows.Remove(this);
            DataContextChanged += (_, _) =>
            {
                if (DataContext is CatalogProductTileVm pp && !ReferenceEquals(pp, _p))
                    BindProduct(pp);
            };
            ApplyLevel();
        }

        private TextBlock Soft()
        {
            var t = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            _v.Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            return t;
        }

        private void Place(Control c, int col)
        {
            Grid.SetColumn(c, col);
            _grid.Children.Add(c);
        }

        public void ApplyLevel()
        {
            if (_level == _v._listLevel)
                return;
            _level = _v._listLevel;
            ApplyColumns(_grid, _level);
        }

        public void Rebind()
        {
            if (_p != null)
                BindProduct(_p);
        }

        public void BindProduct(CatalogProductTileVm p)
        {
            _p = p;
            ApplyLevel();
            var choices = BarcodePrintCatalog.Choices(p);
            var st = _v.GetSel(p.Id);
            if (st.BarcodeIndex >= choices.Count)
                st.BarcodeIndex = 0;
            var printable = _v.IsPrintable(p);
            if (!printable && st.Selected)
                _v.SetSelected(st, false);
            _syncing = true;
            try
            {
                _check.IsEnabled = printable;
                _check.IsChecked = st.Selected;
                _name.Text = p.Title;
                ToolTip.SetTip(_name, p.Title);
                _article.Text = p.Article ?? "";
                _price.Text = BarcodePrintCatalog.LabelPriceText(p.PriceLine);
                _stock.Text = StockText(p.Quantity);
                // штрихкод: несколько кодов — выбор из списка, один — текстом, нет — тире
                if (choices.Count > 1)
                {
                    var combo = EnsureCombo();
                    combo.Items.Clear();
                    foreach (var c in choices)
                        combo.Items.Add(c.Code + "  " + (c.IsMain ? T("(основной)", "(негизги)", "(main)", "(ana)", "(asosiy)")
                            : c.IsSize ? T("(размер)", "(өлчөм)", "(size)", "(beden)", "(o'lcham)") + " " + c.VariantName
                            : c.Quantity > 1 ? T($"(упаковка ×{MoneyText.Qty(c.Quantity)})", $"(таңгак ×{MoneyText.Qty(c.Quantity)})", $"(pack ×{MoneyText.Qty(c.Quantity)})", $"(paket ×{MoneyText.Qty(c.Quantity)})", $"(qadoq ×{MoneyText.Qty(c.Quantity)})") + (c.VariantName != null ? " " + c.VariantName : "")
                            : T("(доп.)", "(кошумча)", "(extra)", "(ek)", "(qo'sh.)") + (c.VariantName != null ? " " + c.VariantName : "")));
                    combo.SelectedIndex = st.BarcodeIndex;
                    combo.IsEnabled = printable;
                    combo.IsVisible = true;
                    _codeText.IsVisible = false;
                }
                else
                {
                    if (_codeCombo != null)
                        _codeCombo.IsVisible = false;
                    _codeText.IsVisible = true;
                    _codeText.Text = choices.Count == 1 ? choices[0].Code : "—";
                    _v.Use(_codeText, TextBlock.ForegroundProperty, choices.Count == 1 ? "BrushAccentText" : "BrushTextSoft");
                }
                var create = choices.Count == 0 && _v._template.Barcode.Enabled;
                _createBtn.IsVisible = create;
                _printBtn.IsVisible = !create;
                _printBtn.IsEnabled = printable;
                _copiesBox.Opacity = printable ? 1 : 0.5;
                _copiesBox.IsHitTestVisible = printable;
                SyncCopies(p, st, printable);
                UpdateWarn(p, choices, st);
                ApplyStyle(st);
            }
            finally { _syncing = false; }
        }

        /// <summary>Привести состояние строки к общему выбору (после «На странице», «Весь список», «Снять», скана, печати).</summary>
        public void Sync()
        {
            if (_p is not { } p)
                return;
            var st = _v.GetSel(p.Id);
            var choices = BarcodePrintCatalog.Choices(p);
            _syncing = true;
            try
            {
                if ((_check.IsChecked == true) != st.Selected)
                    _check.IsChecked = st.Selected;
                if (_codeCombo is { IsVisible: true } combo && combo.SelectedIndex != st.BarcodeIndex && st.BarcodeIndex < combo.Items.Count)
                    combo.SelectedIndex = st.BarcodeIndex;
                SyncCopies(p, st, _v.IsPrintable(p));
                UpdateWarn(p, choices, st);
                ApplyStyle(st);
            }
            finally { _syncing = false; }
        }

        private void SyncCopies(CatalogProductTileVm p, Sel st, bool printable)
        {
            var n = _v.CopiesOf(p, st);
            _copiesText.Text = n.ToString();
            if (_copies != null)
            {
                _copies.Value = n;
                _copies.IsEnabled = printable;
            }
            else if (st.Selected && printable)
                TryRealize();
        }

        private ComboBox EnsureCombo()
        {
            if (_codeCombo != null)
                return _codeCombo;
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace") };
            combo.SelectionChanged += (_, _) =>
            {
                if (_syncing || _p is null || combo.SelectedIndex < 0)
                    return;
                var st = _v.GetSel(_p.Id);
                st.BarcodeIndex = combo.SelectedIndex;
                UpdateWarn(_p, BarcodePrintCatalog.Choices(_p), st);
                if (st.Selected)
                    _v.SchedulePreview();
            };
            _codeCombo = combo;
            _codeHost.Children.Insert(0, combo);
            return combo;
        }

        /// <summary>Заменить лёгкую «коробку» с числом настоящим полем «Копий» (один раз на строку).</summary>
        private NumericUpDown? TryRealize()
        {
            if (_p is null || !_v.IsPrintable(_p))
                return null;
            if (_copies != null)
                return _copies;
            var copies = Num(1, 99, _v.CopiesOf(_p, _v.GetSel(_p.Id)), 118);
            copies.Width = double.NaN; // ширину даёт колонка (на узком списке — 118 − поля)
            copies.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(copies, T("Копий", "Көчүрмө", "Copies", "Kopya", "Nusxa"));
            copies.ValueChanged += (_, _) =>
            {
                if (_v._copiesGuard || _syncing || _p is null)
                    return;
                var value = (int)(copies.Value ?? _v.BaseCopies(_p));
                _v.GetSel(_p.Id).Copies = value == _v.BaseCopies(_p) ? null : value;
                _v.UpdateSelectionUi();
            };
            _copies = copies;
            _copiesHost.Child = copies;
            return copies;
        }

        private void UpdateWarn(CatalogProductTileVm p, IReadOnlyList<BarcodeChoice> choices, Sel st)
        {
            var problem = choices.Count == 0 || !_v._template.Barcode.Enabled ? null : _v.ProblemOf(choices[Math.Clamp(st.BarcodeIndex, 0, choices.Count - 1)].Code, _v._template);
            _warn.IsVisible = problem != null;
            ToolTip.SetTip(_warn, problem);
        }

        private void ApplyStyle(Sel st)
        {
            if (st.Selected)
                _v.Use(this, BackgroundProperty, "BrushAccentSoft");
            else
                ClearValue(BackgroundProperty);
        }
    }
}
