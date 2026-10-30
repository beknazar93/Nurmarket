using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-26, требование владельца: «в десктоп добавь массовую печать ценников и этикеток как в вебе». Единое окно
/// «Печать штрихкодов и ценников» по образцу страницы «Печать штрих-кодов» NurCRM: поиск по названию / штрихкоду / артикулу
/// (на 15 000 товаров — страницами по 48 карточек), карточки с галочкой «В печать», выбором штрихкода (основной и дополнительные)
/// и копиями, настройки этикетки (размер, что показывать, высота и толщина штриха — в шаблоне этикетки LabelTemplateStore),
/// принтер этикеток, «Печать выбранных» на термопринтер с ходом и отменой и «Печать на A4» (<see cref="BarcodeA4SheetWindow"/>) с
/// предпросмотром листа. Печать — прежние BarcodeLabelService.Print и A4SheetService; отрисовку штрихкодов и цены окно не меняет.
/// Товарам без штрихкода предлагается «Создать штрих-код» (BarcodeGenerator, с подтверждением, через PATCH карточки товара).
/// </summary>
public sealed class BarcodePrintWindow : Window
{
    // ---- Подмена для проверочного стенда (в программе всегда null): печать — в память, принтеры — список, сохранение штрихкода — без сервера.
    public static Func<LabelPrintRequest, LabelPrintResult>? ThermalPrintOverride;
    public static Func<IReadOnlyList<DiscoveredPrinter>>? PrinterListOverride;
    public static Func<string, string, Task<string?>>? BarcodeSaveOverride;
    public static Func<IReadOnlyList<A4SheetItem>, A4SheetOptions, string, LabelPrintResult>? SheetPrintOverride;
    /// <summary>Стенд: подтверждение создания штрихкода без окна вопроса.</summary>
    public static Func<string, Task<bool>>? ConfirmOverride;

    internal const int PageSize = 48;

    private sealed class Sel
    {
        public bool Selected;
        public int? Copies;
        public int BarcodeIndex;
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly Dictionary<string, Sel> _sel = new(StringComparer.Ordinal);
    private int _selectedCount;
    private List<CatalogProductTileVm> _found = new();
    private int _page;
    private BarcodeFilter _filter = BarcodeFilter.All;
    private string? _category;
    private LabelTemplate _template;
    private readonly BarcodePrintSettings _settings;
    private bool _templateDirty;
    private bool _init = true;
    private int _barPx;
    private readonly List<Action> _pageSync = new();
    private readonly List<Action> _copiesSync = new();
    private bool _copiesGuard;
    private readonly List<Border> _cards = new();
    private CancellationTokenSource? _printCts;
    private Bitmap? _previewBitmap;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    // Управление
    private readonly ModalShell _shell;
    private readonly TextBox _search;
    private readonly Dictionary<BarcodeFilter, Button> _filterButtons = new();
    private readonly ComboBox _categoryCombo;
    private readonly Border _settingsCard;
    private readonly Border _previewCard;
    private bool _compact;
    private readonly StackPanel _settingsBody;
    private readonly Button _settingsToggle;
    private readonly TextBlock _settingsSummary;
    private readonly ComboBox _sizeCombo;
    private readonly NumericUpDown _widthBox;
    private readonly NumericUpDown _heightBox;
    private readonly StackPanel _customSizePanel;
    private readonly NumericUpDown _copiesBox;
    private readonly Border _printerChip;
    private readonly Ellipse _printerDot;
    private readonly TextBlock _printerChipText;
    private readonly ComboBox _printerCombo;
    private readonly CheckBox _showName, _showPrice, _showBarcode, _showDigits, _showSku, _showUnit, _showStore;
    private readonly NumericUpDown _barHeightBox;
    private readonly TextBlock _barHeightHint;
    private readonly ComboBox _thicknessCombo;
    private readonly Image _previewImage;
    private readonly TextBlock _previewCaption;
    private readonly TextBlock _selectionText;
    private readonly Button _selectPageButton, _selectFoundButton, _clearButton, _a4Button, _printButton;
    private readonly ScrollViewer _scroll;
    private readonly WrapPanel _cardsPanel;
    private readonly TextBlock _emptyText;
    private readonly Button _prevButton, _nextButton;
    private readonly TextBlock _pagerText;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _cancelPrintButton;
    private double _lastWidth;

    private static readonly (double W, double H)[] SizePresets = { (30, 20), (40, 30), (58, 40), (58, 30), (100, 70) };

    private static string CustomSizeLabel => T("Свой размер", "Жеке өлчөм", "Custom size", "Özel boyut", "Maxsus o'lcham");

    /// <summary>Открыть окно из Склада и Маркетплейса (прежняя точка входа BulkPriceTagPrintDialog.Open).</summary>
    public static async Task Open(Window? owner)
    {
        // 2026-10-27 (аудит редакторов, Б-2): массовая печать — платная; все входы (Склад, Маркетплейс, редактор этикеток, ценники) идут сюда.
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.BulkPriceTag, owner))
            return;
        var window = new BarcodePrintWindow();
        if (owner is null)
        {
            window.Show();
            return;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    public BarcodePrintWindow()
    {
        _template = LabelTemplateStore.Load();
        _settings = BarcodePrintSettingsStore.Load();
        _barPx = LabelAutoLayout.BarcodeHeightPx(_template);

        Title = T("Печать штрихкодов и ценников", "Штрих-коддорду жана баа белгилерин басып чыгаруу", "Print barcodes and price tags",
            "Barkod ve fiyat etiketi yazdırma", "Shtrix-kod va narx yorliqlarini chop etish");
        Width = 1264;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        FontFamily = new FontFamily("Segoe UI, Inter, Noto Sans");
        Use(this, BackgroundProperty, "DialogWindowBackground");
        Use(this, TransparencyLevelHintProperty, "DialogTransparencyHint");
        AddStyles();

        // ---- поиск, фильтры
        _search = new TextBox
        {
            Watermark = T("Поиск по названию, штрихкоду или артикулу", "Аталышы, штрих-коду же артикул боюнча издөө", "Search by name, barcode or SKU",
                "Ad, barkod veya stok koduna göre ara", "Nomi, shtrix-kodi yoki artikul bo'yicha qidirish"),
            MinHeight = 44, CornerRadius = new CornerRadius(10), FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center,
            InnerLeftContent = Icon("magnify", 16, 10, 0, 4, 0),
        };
        Avalonia.Automation.AutomationProperties.SetName(_search, (string)_search.Watermark!);
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            _searchTimer.Stop();
            ApplyFilter();
        };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };

        var filterPanel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (f, text) in new[]
                 {
                     (BarcodeFilter.All, T("Все", "Бардыгы", "All", "Tümü", "Hammasi")),
                     (BarcodeFilter.WithBarcode, T("Со штрихкодом", "Штрих-коду менен", "With barcode", "Barkodlu", "Shtrix-kodli")),
                     (BarcodeFilter.WithoutBarcode, T("Без штрихкода", "Штрих-коду жок", "No barcode", "Barkodsuz", "Shtrix-kodsiz")),
                 })
        {
            var b = UiKit.Seg(text, f == _filter);
            b.MinHeight = 44;
            b.Click += (_, _) => { _filter = f; foreach (var kv in _filterButtons) UiKit.SetActive(kv.Value, kv.Key == _filter); ApplyFilter(); };
            _filterButtons[f] = b;
            filterPanel.Children.Add(b);
        }
        var filterSegment = UiKit.Segment(filterPanel);

        _categoryCombo = new ComboBox { MinWidth = 190, MaxWidth = 280, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_categoryCombo, T("Категория", "Категория", "Category", "Kategori", "Kategoriya"));
        FillCategories();
        _categoryCombo.SelectionChanged += (_, _) =>
        {
            if (_init)
                return;
            _category = _categoryCombo.SelectedIndex <= 0 ? null : _categoryCombo.SelectedItem as string;
            ApplyFilter();
        };

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(_search);
        filterSegment.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(filterSegment, 1);
        toolbar.Children.Add(filterSegment);
        _categoryCombo.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_categoryCombo, 2);
        toolbar.Children.Add(_categoryCombo);

        // ---- настройки этикетки
        _settingsToggle = new Button { MinHeight = 44, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Focusable = false };
        _settingsToggle.Classes.Add("sec-outline");
        _settingsToggle.Click += (_, _) => { SetSettingsOpen(!_settingsBody.IsVisible); _settings.SettingsOpen = _settingsBody.IsVisible; };
        _settingsSummary = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 0, 0) };
        Use(_settingsSummary, TextBlock.ForegroundProperty, "BrushTextSoft");

        _sizeCombo = new ComboBox { MinWidth = 150, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var p in SizePresets)
            _sizeCombo.Items.Add($"{p.W:0.#}×{p.H:0.#} " + T("мм", "мм", "mm", "mm", "mm"));
        _sizeCombo.Items.Add(CustomSizeLabel);
        _widthBox = Num(15, 150, (decimal)_template.WidthMm, 118);
        _heightBox = Num(10, 150, (decimal)_template.HeightMm, 118);
        _customSizePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _customSizePanel.Children.Add(_widthBox);
        _customSizePanel.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center, FontSize = 15 });
        _customSizePanel.Children.Add(_heightBox);
        _customSizePanel.Children.Add(new TextBlock { Text = T("мм", "мм", "mm", "mm", "mm"), VerticalAlignment = VerticalAlignment.Center });
        _copiesBox = Num(1, 99, _settings.DefaultCopies, 118);

        _printerDot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
        _printerChipText = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 };
        Use(_printerChipText, TextBlock.ForegroundProperty, "BrushText");
        _printerChip = new Border
        {
            CornerRadius = new CornerRadius(15), Padding = new Thickness(12, 0), MinHeight = 44, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _printerDot, _printerChipText } },
        };
        Use(_printerChip, Border.BackgroundProperty, "BrushInputAlt");
        _printerCombo = new ComboBox { MinWidth = 210, MaxWidth = 300, PlaceholderText = T("Выбрать принтер…", "Принтерди тандоо…", "Choose a printer…", "Yazıcı seç…", "Printerni tanlash…") };
        _printerCombo.SelectionChanged += PrinterCombo_SelectionChanged;
        var refreshPrinters = Ghost("↻");
        refreshPrinters.MinWidth = 52;
        Avalonia.Automation.AutomationProperties.SetName(refreshPrinters, T("Обновить список принтеров", "Принтерлердин тизмесин жаңыртуу", "Refresh the printer list", "Yazıcı listesini yenile", "Printerlar ro'yxatini yangilash"));
        refreshPrinters.Click += (_, _) => _ = LoadPrintersAsync();
        var testPrint = Ghost(T("Тест-печать", "Сынак басуу", "Test print", "Test baskı", "Sinov chop etish"));
        testPrint.Click += async (_, _) => await PrintTestAsync().ConfigureAwait(true);

        _showName = Check(T("Название", "Аталышы", "Name", "Ad", "Nomi"), _template.ProductName.Enabled);
        _showPrice = Check(T("Цена", "Баасы", "Price", "Fiyat", "Narx"), _template.Price.Enabled);
        _showBarcode = Check(T("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), _template.Barcode.Enabled);
        _showDigits = Check(T("Цифры", "Цифралар", "Digits", "Rakamlar", "Raqamlar"), _template.BarcodeShowDigits);
        _showSku = Check(T("Артикул", "Артикул", "SKU", "Stok kodu", "Artikul"), _template.Sku.Enabled);
        _showUnit = Check(T("Ед. изм.", "Өлч. бирд.", "Unit", "Birim", "O'lchov birligi"), _template.Unit.Enabled);
        _showStore = Check(T("Магазин", "Дүкөн", "Store", "Mağaza", "Do'kon"), _template.StoreName.Enabled);
        _barHeightBox = Num(8, 300, _barPx, 118);
        _barHeightHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxWidth = 520 };
        Use(_barHeightHint, TextBlock.ForegroundProperty, "BrushWarning");
        _thicknessCombo = new ComboBox { MinWidth = 160 };
        foreach (var s in new[]
                 {
                     T("Авто", "Авто", "Auto", "Otomatik", "Avto"), "1 " + T("(тонкий)", "(ичке)", "(thin)", "(ince)", "(ingichka)"),
                     "2 " + T("(стандарт)", "(стандарт)", "(standard)", "(standart)", "(standart)"), "3 " + T("(толстый)", "(жоон)", "(thick)", "(kalın)", "(qalin)"),
                 })
            _thicknessCombo.Items.Add(s);
        _thicknessCombo.SelectedIndex = Math.Clamp(_template.BarcodeModuleDots, 0, 3);

        var editorButton = Ghost(T("Редактор этикетки…", "Этикетка редактору…", "Label editor…", "Etiket düzenleyici…", "Yorliq muharriri…"));
        editorButton.Click += async (_, _) => await OpenEditorAsync().ConfigureAwait(true);
        var autoButton = Ghost(T("Расставить заново", "Кайра иретке келтирүү", "Re-arrange", "Yeniden diz", "Qayta joylash"));
        ToolTip.SetTip(autoButton, T("Расставить элементы этикетки автоматически по размеру и высоте штрих-кода", "Этикетканын элементтерин өлчөмү жана штрих-кодунун бийиктиги боюнча автоматтык түрдө иретке келтирүү",
            "Arrange the label elements automatically by size and barcode height", "Etiket öğelerini boyuta ve barkod yüksekliğine göre otomatik diz", "Yorliq elementlarini o'lcham va shtrix-kod balandligiga qarab avtomatik joylashtirish"));
        autoButton.Click += (_, _) => { if (_init) return; _barPx = (int)(_barHeightBox.Value ?? _barPx); Relayout(); };

        _previewImage = new Image { Stretch = Stretch.Uniform };
        _previewCaption = new TextBlock { FontSize = 11.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(_previewCaption, TextBlock.ForegroundProperty, "BrushTextSoft");

        // Подпись слева от поля (в строку) — настройки занимают меньше высоты, карточкам товаров остаётся место.
        var rowA = new WrapPanel { Orientation = Orientation.Horizontal };
        rowA.Children.Add(Inline(T("Размер", "Өлчөм", "Size", "Boyut", "O'lcham"), _sizeCombo));
        rowA.Children.Add(Inline("", _customSizePanel));
        rowA.Children.Add(Inline(T("Копий (по умолчанию)", "Көчүрмө (демейки)", "Copies (default)", "Kopya (varsayılan)", "Nusxa (standart)"), _copiesBox));
        rowA.Children.Add(Inline(T("Высота штрих-кода, px", "Штрих-код бийиктиги, px", "Barcode height, px", "Barkod yüksekliği, px", "Shtrix-kod balandligi, px"), _barHeightBox));
        rowA.Children.Add(Inline(T("Толщина штриха", "Сызык калыңдыгы", "Bar thickness", "Çizgi kalınlığı", "Chiziq qalinligi"), _thicknessCombo));

        var rowB = new WrapPanel { Orientation = Orientation.Horizontal };
        var showCaption = new TextBlock { Text = T("Отображать на этикетке:", "Этикеткада көрсөтүү:", "Show on the label:", "Etikette göster:", "Yorliqda ko'rsatish:"), FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Use(showCaption, TextBlock.ForegroundProperty, "BrushTextSoft");
        rowB.Children.Add(showCaption);
        foreach (var c in new[] { _showName, _showPrice, _showBarcode, _showDigits, _showSku, _showUnit, _showStore })
        {
            c.Margin = new Thickness(0, 0, 12, 0);
            rowB.Children.Add(c);
        }

        var rowC = new WrapPanel { Orientation = Orientation.Horizontal };
        _printerChip.Margin = new Thickness(0, 0, 8, 6);
        _printerChip.VerticalAlignment = VerticalAlignment.Center;
        _printerCombo.Margin = new Thickness(0, 0, 8, 6);
        refreshPrinters.Margin = new Thickness(0, 0, 8, 6);
        testPrint.Margin = new Thickness(0, 0, 8, 6);
        editorButton.Margin = new Thickness(0, 0, 8, 6);
        autoButton.Margin = new Thickness(0, 0, 8, 6);
        foreach (var c in new Control[] { _printerChip, _printerCombo, refreshPrinters, testPrint, editorButton })
            rowC.Children.Add(c);

        _previewCard = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(10), Width = 170, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Use(_previewCard, Border.BackgroundProperty, "BrushInputAlt");
        _previewCard.Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Border { Background = Brushes.White, Height = 92, Child = _previewImage, BoxShadow = BoxShadows.Parse("0 2 8 0 #22000000") },
                _previewCaption,
            },
        };

        _settingsBody = new StackPanel { Spacing = 2, Children = { rowA, rowB, _barHeightHint, rowC } };
        var settingsLeft = new StackPanel { Children = { _settingsSummary, _settingsBody } };
        var settingsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        settingsGrid.Children.Add(settingsLeft);
        Grid.SetColumn(_previewCard, 1);
        settingsGrid.Children.Add(_previewCard);
        _settingsCard = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8), Child = settingsGrid };
        Use(_settingsCard, Border.BackgroundProperty, "BrushPanel");
        Use(_settingsCard, Border.BorderBrushProperty, "BrushBorder");

        // ---- полоса выбора
        _selectionText = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        Use(_selectionText, TextBlock.ForegroundProperty, "BrushText");
        _selectPageButton = Ghost("");
        _selectPageButton.Click += (_, _) => SelectPage();
        _selectFoundButton = Ghost("");
        _selectFoundButton.Click += (_, _) => SelectFound();
        _clearButton = Ghost("");
        _clearButton.Click += (_, _) => ClearSelection();
        _a4Button = Ghost("");
        _a4Button.Content = ButtonWithIcon("printer-outline", T("Печать на A4", "A4 басуу", "Print on A4", "A4'e yazdır", "A4 ga chop etish"));
        _a4Button.Click += async (_, _) => await OpenA4Async().ConfigureAwait(true);
        _printButton = new Button { Height = 46, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(12), FontWeight = FontWeight.Bold, FontSize = 14, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        _printButton.Classes.Add("sec-primary");
        _printButton.Click += async (_, _) => await PrintSelectedAsync().ConfigureAwait(true);
        var selButtons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { _selectPageButton, _selectFoundButton, _clearButton, _a4Button, _printButton })
        {
            b.Margin = new Thickness(8, 0, 0, 0);
            selButtons.Children.Add(b);
        }
        _settingsToggle.Margin = new Thickness(0, 0, 12, 0);
        var selGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        selGrid.Children.Add(_settingsToggle);
        Grid.SetColumn(_selectionText, 1);
        selGrid.Children.Add(_selectionText);
        Grid.SetColumn(selButtons, 2);
        selGrid.Children.Add(selButtons);
        var selBar = new Border { Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8), Child = selGrid };
        Use(selBar, Border.BackgroundProperty, "BrushPanel");
        Use(selBar, Border.BorderBrushProperty, "BrushBorder");

        // ---- сетка карточек и подвал страниц
        _cardsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        _scroll = new ScrollViewer { Content = _cardsPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _scroll.SizeChanged += (_, e) => LayoutCards(e.NewSize.Width);
        _emptyText = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 60, 24, 0), IsVisible = false, IsHitTestVisible = false };
        Use(_emptyText, TextBlock.ForegroundProperty, "BrushTextSoft");
        var gridHost = new Grid { Children = { _scroll, _emptyText } };

        _prevButton = Ghost("‹");
        _prevButton.MinWidth = 52;
        _prevButton.Click += (_, _) => GoToPage(_page - 1);
        Avalonia.Automation.AutomationProperties.SetName(_prevButton, T("Назад", "Артка", "Back", "Geri", "Orqaga"));
        _nextButton = Ghost("›");
        _nextButton.MinWidth = 52;
        _nextButton.Click += (_, _) => GoToPage(_page + 1);
        Avalonia.Automation.AutomationProperties.SetName(_nextButton, T("Вперёд", "Алга", "Next", "İleri", "Oldinga"));
        _pagerText = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
        Use(_pagerText, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _progress = new ProgressBar { Width = 140, Height = 8, IsVisible = false, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
        _cancelPrintButton = Ghost(T("Отмена печати", "Басууну токтотуу", "Cancel printing", "Yazdırmayı iptal et", "Chop etishni bekor qilish"));
        _cancelPrintButton.IsVisible = false;
        _cancelPrintButton.Click += (_, _) => { _printCts?.Cancel(); _cancelPrintButton.IsEnabled = false; };

        // Страницы и состояние — в подвале слева (на 1024×600 так больше места карточкам).
        var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, MinWidth = 150 };
        hint.Children.Add(_prevButton);
        hint.Children.Add(_pagerText);
        hint.Children.Add(_nextButton);
        hint.Children.Add(_progress);
        hint.Children.Add(_cancelPrintButton);
        hint.Children.Add(_status);
        var legacy = Ghost(T("Виды ценников…", "Баа белгилеринин түрлөрү…", "Price tag styles…", "Fiyat etiketi türleri…", "Narx yorliqlari turlari…"));
        ToolTip.SetTip(legacy, T("Прежнее окно: готовые виды ценников (простой, подробный, с QR)", "Мурунку терезе: даяр баа белгилеринин түрлөрү (жөнөкөй, толук, QR менен)",
            "The previous window: ready-made price tag styles (simple, detailed, with QR)", "Önceki pencere: hazır fiyat etiketi türleri (basit, ayrıntılı, QR'lı)", "Avvalgi oyna: tayyor narx yorliqlari turlari (oddiy, batafsil, QR bilan)"));
        legacy.Click += async (_, _) => await BulkPriceTagPrintDialog.OpenLegacy(this).ConfigureAwait(true);
        var close = new Button { IsCancel = true, MinWidth = 110, Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish") };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { legacy, close } };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
        Grid.SetRow(_settingsCard, 1);
        Grid.SetRow(selBar, 2);
        Grid.SetRow(gridHost, 3);
        root.Children.Add(toolbar);
        root.Children.Add(_settingsCard);
        root.Children.Add(selBar);
        root.Children.Add(gridHost);

        _shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = T("Печать штрихкодов и ценников", "Штрих-коддорду жана баа белгилерин басып чыгаруу", "Print barcodes and price tags", "Barkod ve fiyat etiketi yazdırma", "Shtrix-kod va narx yorliqlarini chop etish"),
            Subtitle = T("Отметьте товары, настройте этикетку и печатайте на термопринтер или на лист A4.", "Товарларды белгилеп, этикетканы тууралап, термопринтерге же A4 барагына басыңыз.",
                "Tick the products, set up the label and print on a thermal printer or an A4 sheet.", "Ürünleri işaretleyin, etiketi ayarlayın ve termal yazıcıya veya A4 sayfaya yazdırın.", "Mahsulotlarni belgilang, yorliqni sozlang va termoprinterda yoki A4 varaqqa chop eting."),
            ContentScroll = ScrollBarVisibility.Disabled,
            ScreenHeightShare = 1.0,
            InitialFocus = _search,
            Footer = footer,
            Hint = hint,
            Content = root,
        };
        Content = _shell;

        // ---- обработчики настроек (после сборки всех элементов)
        _sizeCombo.SelectedIndex = FindPresetIndex();
        _sizeCombo.SelectionChanged += SizeCombo_SelectionChanged;
        _widthBox.ValueChanged += (_, _) => CustomSize_Changed();
        _heightBox.ValueChanged += (_, _) => CustomSize_Changed();
        _copiesBox.ValueChanged += (_, _) => DefaultCopies_Changed();
        _showName.IsCheckedChanged += (_, _) => FlagChanged(() => _template.ProductName.Enabled = _showName.IsChecked == true, relayout: true);
        _showPrice.IsCheckedChanged += (_, _) => FlagChanged(() => _template.Price.Enabled = _showPrice.IsChecked == true, relayout: true);
        _showBarcode.IsCheckedChanged += (_, _) => FlagChanged(() => _template.Barcode.Enabled = _showBarcode.IsChecked == true, relayout: true, barcodeToggled: true);
        _showDigits.IsCheckedChanged += (_, _) => FlagChanged(() => _template.BarcodeShowDigits = _showDigits.IsChecked == true, relayout: false);
        _showSku.IsCheckedChanged += (_, _) => FlagChanged(() => _template.Sku.Enabled = _showSku.IsChecked == true, relayout: true);
        _showUnit.IsCheckedChanged += (_, _) => FlagChanged(() => _template.Unit.Enabled = _showUnit.IsChecked == true, relayout: true);
        _showStore.IsCheckedChanged += (_, _) => FlagChanged(() => _template.StoreName.Enabled = _showStore.IsChecked == true, relayout: true);
        _barHeightBox.ValueChanged += (_, _) =>
        {
            if (_init)
                return;
            _barPx = (int)(_barHeightBox.Value ?? _barPx);
            Relayout();
        };
        _thicknessCombo.SelectionChanged += (_, _) =>
        {
            if (_init || _thicknessCombo.SelectedIndex < 0)
                return;
            _template.BarcodeModuleDots = _thicknessCombo.SelectedIndex;
            TemplateChanged();
        };

        Opened += (_, _) =>
        {
            _ = LoadPrintersAsync();
            _status.Text = "";
            // На низком экране (1024×600) настройки свёрнуты в одну строку — карточкам остаётся место; высота известна после подгонки окна под экран.
            Dispatcher.UIThread.Post(() => SetSettingsOpen(_settings.SettingsOpen ?? ClientSize.Height >= 840));
        };
        SizeChanged += (_, e) =>
        {
            var c = e.NewSize.Width < 1400;
            if (c == _compact)
                return;
            _compact = c;
            ApplyTexts();
        };
        Closing += (_, _) => SaveState();
        _init = false;

        SetSettingsOpen(true, initial: true);
        UpdatePrinterChip();
        UpdateSummary();
        ApplyFilter();
        RefreshPreview();
    }

    // ------------------------------------------------------------------ стили и мелкие помощники

    private void AddStyles()
    {
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ComboBox>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(ComboBox.CornerRadiusProperty, new CornerRadius(8)),
                new Avalonia.Styling.Setter(MinHeightProperty, 44.0),
                new Avalonia.Styling.Setter(ComboBox.BorderThicknessProperty, new Thickness(1.5)),
            },
        });
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<NumericUpDown>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(NumericUpDown.CornerRadiusProperty, new CornerRadius(8)),
                new Avalonia.Styling.Setter(MinHeightProperty, 44.0),
                new Avalonia.Styling.Setter(NumericUpDown.BorderThicknessProperty, new Thickness(1.5)),
            },
        });
        Styles.Add(new Avalonia.Styling.Style(x => x.OfType<CheckBox>())
        {
            Setters = { new Avalonia.Styling.Setter(MinHeightProperty, 44.0) },
        });
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));

    private PathIcon Icon(string key, double size, double l = 0, double t = 0, double r = 0, double b = 0)
    {
        var icon = MarketplaceIcons.Icon(key, size);
        icon.Margin = new Thickness(l, t, r, b);
        Use(icon, TemplatedControl.ForegroundProperty, "BrushTextSoft");
        return icon;
    }

    private static Button Ghost(string text)
    {
        var b = new Button
        {
            Content = text, MinHeight = 44, Padding = new Thickness(14, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), FontSize = 13.5,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Classes.Add("sec-outline");
        return b;
    }

    private static object ButtonWithIcon(string iconKey, string text)
    {
        var icon = MarketplaceIcons.Icon(iconKey, 16);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, double width) => new()
    {
        Minimum = min, Maximum = max, Value = value, Increment = 1, FormatString = "0", Width = width, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left,
    };

    private CheckBox Check(string text, bool isChecked) => new() { Content = text, IsChecked = isChecked, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };

    private StackPanel Inline(string caption, Control control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 14, 6), VerticalAlignment = VerticalAlignment.Center };
        if (caption.Length > 0)
        {
            var label = new TextBlock { Text = caption, FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
            panel.Children.Add(label);
        }
        panel.Children.Add(control);
        return panel;
    }

    private StackPanel Field(string caption, Control control)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 14, 6) };
        var label = new TextBlock { Text = caption.Length > 0 ? caption : " ", FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    private void FillCategories()
    {
        _categoryCombo.Items.Clear();
        _categoryCombo.Items.Add(T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar"));
        foreach (var c in BarcodePrintCatalog.Categories(CatalogCacheService.Products))
            _categoryCombo.Items.Add(c);
        _categoryCombo.SelectedIndex = 0;
    }

    // ------------------------------------------------------------------ поиск и страницы

    /// <summary>Текст строки поиска (для стенда и вызова из кода).</summary>
    public string SearchText
    {
        get => _search.Text ?? "";
        set { _search.Text = value; _searchTimer.Stop(); ApplyFilter(); }
    }

    /// <summary>Сколько товаров найдено по текущему поиску и фильтрам.</summary>
    public int FoundCount => _found.Count;

    public int SelectedCount => _selectedCount;

    public int PageNumber => _page + 1;

    private int PageCount => Math.Max(1, (_found.Count + PageSize - 1) / PageSize);

    private void ApplyFilter()
    {
        _found = BarcodePrintCatalog.Search(CatalogCacheService.Products, _search.Text, _filter, _category);
        _page = 0;
        RenderPage();
        UpdateSelectionUi();
    }

    public void GoToPage(int page)
    {
        page = Math.Clamp(page, 0, PageCount - 1);
        if (page == _page)
            return;
        _page = page;
        RenderPage();
    }

    private void RenderPage()
    {
        _cardsPanel.Children.Clear();
        _cards.Clear();
        _pageSync.Clear();
        _copiesSync.Clear();
        var start = _page * PageSize;
        var end = Math.Min(start + PageSize, _found.Count);
        // 2026-10-31 (стресс-тест 1.17.68): 48 карточек разом — окно открывалось 0,6–0,8 с. Первые 12 строим сразу, остальные — пакетами по 12 между кадрами.
        var gen = ++_renderGen;
        var firstEnd = Math.Min(start + CardBatch, end);
        for (var i = start; i < firstEnd; i++)
        {
            var card = BuildCard(_found[i]);
            _cards.Add(card);
            _cardsPanel.Children.Add(card);
        }
        if (firstEnd < end)
            Dispatcher.UIThread.Post(() => AddCardBatch(gen, firstEnd, end), DispatcherPriority.Background);
        LayoutCards(_scroll.Bounds.Width > 0 ? _scroll.Bounds.Width : Width - 64);
        _scroll.Offset = default;

        var total = CatalogCacheService.Products.Count;
        _emptyText.IsVisible = _found.Count == 0;
        if (_found.Count == 0)
            _emptyText.Text = total == 0
                ? T("Каталог товаров ещё не загружен — откройте Склад и нажмите «Обновить».", "Товарлар каталогу али жүктөлө элек — Кампаны ачып, «Жаңыртуу» баскычын басыңыз.", "The product catalog is not loaded yet — open the Warehouse and press “Refresh”.", "Ürün kataloğu henüz yüklenmedi — Depoyu açıp «Yenile»ye basın.", "Mahsulotlar katalogi hali yuklanmagan — Omborni ochib, «Yangilash» tugmasini bosing.")
                : T("Ничего не найдено. Измените поиск или фильтр.", "Эч нерсе табылган жок. Издөөнү же чыпканы өзгөртүңүз.", "Nothing found. Change the search or the filter.", "Hiçbir şey bulunamadı. Aramayı veya filtreyi değiştirin.", "Hech narsa topilmadi. Qidiruv yoki filtrni o'zgartiring.");
        var from = _found.Count == 0 ? 0 : start + 1;
        _pagerText.Text = T($"Стр. {_page + 1} из {PageCount} · {from}–{end} из {_found.Count:N0}".Replace(',', ' '), $"{_page + 1}/{PageCount}-бет · {from}–{end} / {_found.Count:N0}".Replace(',', ' '),
            $"Page {_page + 1} of {PageCount} · {from}–{end} of {_found.Count:N0}", $"Sayfa {_page + 1}/{PageCount} · {from}–{end} / {_found.Count:N0}".Replace(',', ' '), $"{_page + 1}/{PageCount}-sahifa · {from}–{end} / {_found.Count:N0}".Replace(',', ' '));
        _prevButton.IsEnabled = _page > 0;
        _nextButton.IsEnabled = _page < PageCount - 1;
        ApplyTexts();
    }

    private const int CardBatch = 12;
    private int _renderGen;

    /// <summary>Следующий пакет карточек страницы; страница сменилась (или окно закрыто) — пакет бросается.</summary>
    private void AddCardBatch(int gen, int from, int end)
    {
        if (gen != _renderGen)
            return;
        var to = Math.Min(from + CardBatch, end);
        for (var i = from; i < to; i++)
        {
            var card = BuildCard(_found[i]);
            _cards.Add(card);
            _cardsPanel.Children.Add(card);
        }
        LayoutCards(_lastWidth > 0 ? _lastWidth : (_scroll.Bounds.Width > 0 ? _scroll.Bounds.Width : Width - 64));
        if (to < end)
            Dispatcher.UIThread.Post(() => AddCardBatch(gen, to, end), DispatcherPriority.Background);
    }

    private void LayoutCards(double width)
    {
        if (width <= 0)
            return;
        _lastWidth = width;
        const double gap = 10;
        var usable = Math.Max(width - 14, 200);
        var cols = Math.Max(1, (int)((usable + gap) / (250 + gap)));
        var cardWidth = Math.Floor((usable - gap * (cols - 1)) / cols) - 1;
        foreach (var c in _cards)
        {
            c.Width = Math.Max(cardWidth, 180);
            c.Margin = new Thickness(0, 0, gap, gap);
        }
    }

    // ------------------------------------------------------------------ карточка товара

    private Sel GetSel(string id)
    {
        if (!_sel.TryGetValue(id, out var s))
            _sel[id] = s = new Sel();
        return s;
    }

    private bool IsPrintable(CatalogProductTileVm p) => !_template.Barcode.Enabled || BarcodePrintCatalog.HasBarcode(p);

    private void SetSelected(Sel st, bool value)
    {
        if (st.Selected == value)
            return;
        st.Selected = value;
        _selectedCount += value ? 1 : -1;
    }

    private Border BuildCard(CatalogProductTileVm p)
    {
        var choices = BarcodePrintCatalog.Choices(p);
        var st = GetSel(p.Id);
        if (st.BarcodeIndex >= choices.Count)
            st.BarcodeIndex = 0;
        var printable = IsPrintable(p);
        if (!printable && st.Selected)
            SetSelected(st, false);

        var check = new CheckBox { Content = T("В печать", "Басууга", "Print", "Yazdır", "Chopga"), IsChecked = st.Selected, IsEnabled = printable, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        var copies = Num(1, 99, st.Copies ?? _settings.DefaultCopies, 118);
        copies.IsEnabled = printable;
        Avalonia.Automation.AutomationProperties.SetName(copies, T("Копий", "Көчүрмө", "Copies", "Kopya", "Nusxa"));
        var copiesLabel = new TextBlock { Text = T("Копий:", "Көчүрмө:", "Copies:", "Kopya:", "Nusxa:"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Use(copiesLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        top.Children.Add(check);
        Grid.SetColumn(copiesLabel, 1);
        top.Children.Add(copiesLabel);
        Grid.SetColumn(copies, 2);
        top.Children.Add(copies);

        var caption = new TextBlock { Text = T("ШТРИХ-КОД", "ШТРИХ-КОД", "BARCODE", "BARKOD", "SHTRIX-KOD"), FontSize = 10.5, FontWeight = FontWeight.SemiBold };
        Use(caption, TextBlock.ForegroundProperty, "BrushTextSoft");
        Control codeControl;
        if (choices.Count > 1)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), IsEnabled = printable };
            foreach (var c in choices)
                combo.Items.Add(c.Code + "  " + (c.IsMain ? T("(основной)", "(негизги)", "(main)", "(ana)", "(asosiy)") : T("(доп.)", "(кошумча)", "(extra)", "(ek)", "(qo'sh.)") + (c.VariantName != null ? " " + c.VariantName : "")));
            combo.SelectedIndex = st.BarcodeIndex;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                    st.BarcodeIndex = combo.SelectedIndex;
                if (st.Selected)
                    SchedulePreview();
            };
            codeControl = combo;
        }
        else
        {
            var codeText = new TextBlock
            {
                Text = choices.Count == 1 ? choices[0].Code : "—", FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"), FontSize = 15, FontWeight = FontWeight.SemiBold,
                MinHeight = 30, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Use(codeText, TextBlock.ForegroundProperty, choices.Count == 1 ? "BrushAccentStrong" : "BrushTextSoft");
            codeControl = codeText;
        }

        Button action;
        if (choices.Count == 0 && _template.Barcode.Enabled)
        {
            action = Ghost("＋ " + T("Создать штрих-код", "Штрих-код түзүү", "Create a barcode", "Barkod oluştur", "Shtrix-kod yaratish"));
            action.HorizontalAlignment = HorizontalAlignment.Stretch;
            ToolTip.SetTip(action, T("Сгенерировать EAN-13 и сохранить в карточку товара (после подтверждения)", "EAN-13 түзүп, товардын карточкасына сактоо (ырастоодон кийин)",
                "Generate an EAN-13 and save it to the product card (after confirmation)", "EAN-13 üretip ürün kartına kaydet (onaydan sonra)", "EAN-13 yaratib, mahsulot kartochkasiga saqlash (tasdiqlagandan so'ng)"));
            action.Click += async (_, _) => await CreateBarcodeAsync(p).ConfigureAwait(true);
        }
        else
        {
            action = new Button { Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(10), FontSize = 13.5, FontWeight = FontWeight.SemiBold, Focusable = false, IsEnabled = printable, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            action.Classes.Add("sec-primary");
            action.Content = ButtonWithIcon("printer-outline", T("Распечатать штрих-код", "Штрих-кодду басуу", "Print barcode", "Barkodu yazdır", "Shtrix-kodni chop etish"));
            action.Click += async (_, _) => await PrintOneAsync(p).ConfigureAwait(true);
        }

        var name = new TextBlock { Text = p.Title, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 38 };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        ToolTip.SetTip(name, p.Title);
        var price = new TextBlock { Text = p.PriceLine, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        Use(price, TextBlock.ForegroundProperty, "BrushTextSoft");

        ToolTip.SetTip(codeControl, caption.Text);
        var stack = new StackPanel { Spacing = 2, Children = { top, codeControl, action, name, price } };
        var border = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6, 12, 10), Child = stack };
        Use(border, Border.BackgroundProperty, "BrushPanel");

        void Style()
        {
            border.BorderThickness = new Thickness(st.Selected ? 2 : 1.5);
            Use(border, Border.BorderBrushProperty, st.Selected ? "BrushAccent" : "BrushBorder");
        }
        Style();

        check.IsCheckedChanged += (_, _) =>
        {
            SetSelected(st, check.IsChecked == true);
            Style();
            UpdateSelectionUi();
            SchedulePreview();
        };
        copies.ValueChanged += (_, _) =>
        {
            if (_copiesGuard)
                return;
            var v = (int)(copies.Value ?? _settings.DefaultCopies);
            st.Copies = v == _settings.DefaultCopies ? null : v;
            UpdateSelectionUi();
        };
        _copiesSync.Add(() => copies.Value = st.Copies ?? _settings.DefaultCopies);
        _pageSync.Add(() =>
        {
            if ((check.IsChecked == true) != st.Selected)
                check.IsChecked = st.Selected;
            Style();
        });
        return border;
    }

    // ------------------------------------------------------------------ выбор

    public void SelectPage()
    {
        var start = _page * PageSize;
        var end = Math.Min(start + PageSize, _found.Count);
        for (var i = start; i < end; i++)
            if (IsPrintable(_found[i]))
                SetSelected(GetSel(_found[i].Id), true);
        foreach (var s in _pageSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
    }

    public void SelectFound()
    {
        foreach (var p in _found)
            if (IsPrintable(p))
                SetSelected(GetSel(p.Id), true);
        foreach (var s in _pageSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
    }

    public void ClearSelection()
    {
        foreach (var s in _sel.Values)
            s.Selected = false;
        _selectedCount = 0;
        foreach (var s in _pageSync)
            s();
        UpdateSelectionUi();
        SchedulePreview();
    }

    private int TotalLabels()
    {
        var n = 0;
        foreach (var s in _sel.Values)
            if (s.Selected)
                n += s.Copies ?? _settings.DefaultCopies;
        return n;
    }

    private void UpdateSelectionUi()
    {
        var labels = TotalLabels();
        _selectionText.Text = T($"Выбрано: {_selectedCount}", $"Тандалды: {_selectedCount}", $"Selected: {_selectedCount}", $"Seçildi: {_selectedCount}", $"Tanlandi: {_selectedCount}")
            + (_selectedCount > 0 ? T($" · этикеток: {labels}", $" · этикетка: {labels}", $" · labels: {labels}", $" · etiket: {labels}", $" · yorliq: {labels}") : "");
        _printButton.Content = ButtonWithIcon("printer-outline", _compact
            ? T($"Печать ({_selectedCount})", $"Басуу ({_selectedCount})", $"Print ({_selectedCount})", $"Yazdır ({_selectedCount})", $"Chop etish ({_selectedCount})")
            : T($"Печать выбранных ({_selectedCount})", $"Тандалгандарды басуу ({_selectedCount})", $"Print selected ({_selectedCount})", $"Seçilenleri yazdır ({_selectedCount})", $"Tanlanganlarni chop etish ({_selectedCount})"));
        ToolTip.SetTip(_printButton, T("Печать выбранных на термопринтер этикеток", "Тандалгандарды этикетка термопринтерине басуу", "Print the selected on the label thermal printer", "Seçilenleri etiket termal yazıcısında yazdır", "Tanlanganlarni yorliq termoprinterida chop etish"));
        _printButton.IsEnabled = _selectedCount > 0;
        _a4Button.IsEnabled = _selectedCount > 0;
        _clearButton.IsEnabled = _selectedCount > 0;
    }

    /// <summary>Выбранные товары в порядке каталога: товар, штрихкод (выбранный на карточке) и число копий.</summary>
    public List<BarcodePrintItem> BuildItems()
    {
        var list = new List<BarcodePrintItem>();
        foreach (var p in CatalogCacheService.Products)
        {
            if (!_sel.TryGetValue(p.Id, out var st) || !st.Selected)
                continue;
            var choices = BarcodePrintCatalog.Choices(p);
            var code = choices.Count == 0 ? null : choices[Math.Clamp(st.BarcodeIndex, 0, choices.Count - 1)].Code;
            if (code == null && _template.Barcode.Enabled)
                continue;
            list.Add(new BarcodePrintItem(p, code, Math.Clamp(st.Copies ?? _settings.DefaultCopies, 1, 99)));
        }
        return list;
    }

    // ------------------------------------------------------------------ настройки этикетки

    private int FindPresetIndex()
    {
        for (var i = 0; i < SizePresets.Length; i++)
            if (Math.Abs(SizePresets[i].W - _template.WidthMm) < 0.05 && Math.Abs(SizePresets[i].H - _template.HeightMm) < 0.05)
                return i;
        return SizePresets.Length;
    }

    private void SetSettingsOpen(bool open, bool initial = false)
    {
        _settingsBody.IsVisible = open;
        _previewCard.IsVisible = open;
        _settingsSummary.IsVisible = !open;
        UpdateToggleText();
        _customSizePanel.IsVisible = _sizeCombo.SelectedIndex == SizePresets.Length;
    }

    private void UpdateToggleText()
    {
        var open = _settingsBody.IsVisible;
        _settingsToggle.Content = (open ? "▴  " : "▾  ") + (_compact ? T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar")
            : T("Настройки этикетки", "Этикетка жөндөөлөрү", "Label settings", "Etiket ayarları", "Yorliq sozlamalari"));
    }

    /// <summary>Подписи кнопок выбора: на узком окне (1024) — короткие, на широком — как на сайте.</summary>
    private void ApplyTexts()
    {
        UpdateToggleText();
        UpdateSelectionUi();
        var n = _found.Count.ToString("N0");
        _selectPageButton.Content = _compact ? T("На странице", "Баракта", "On page", "Sayfada", "Sahifada")
            : T("Выбрать все на странице", "Баракта бардыгын тандоо", "Select all on page", "Sayfadakilerin tümünü seç", "Sahifadagilarni tanlash");
        _selectFoundButton.Content = _compact ? T($"Все найденные ({n})", $"Баары ({n})", $"All found ({n})", $"Tümü ({n})", $"Hammasi ({n})")
            : T($"Выбрать все найденные ({n})", $"Табылгандардын бардыгын тандоо ({n})", $"Select all found ({n})", $"Bulunanların tümünü seç ({n})", $"Topilganlarning barchasini tanlash ({n})");
        _clearButton.Content = _compact ? T("Снять", "Алуу", "Clear", "Kaldır", "Bekor") : T("Снять выбор", "Тандоону алып салуу", "Clear selection", "Seçimi kaldır", "Tanlovni bekor qilish");
        ToolTip.SetTip(_selectPageButton, T("Выбрать все на странице", "Баракта бардыгын тандоо", "Select all on page", "Sayfadakilerin tümünü seç", "Sahifadagilarni tanlash"));
        ToolTip.SetTip(_selectFoundButton, T("Выбрать все найденные", "Табылгандардын бардыгын тандоо", "Select all found", "Bulunanların tümünü seç", "Topilganlarning barchasini tanlash"));
    }

    private void UpdateSummary()
    {
        var parts = new List<string> { $"{_template.WidthMm:0.#}×{_template.HeightMm:0.#} " + T("мм", "мм", "mm", "mm", "mm") };
        var shown = new List<string>();
        if (_template.Barcode.Enabled) shown.Add(T("штрих-код", "штрих-код", "barcode", "barkod", "shtrix-kod"));
        if (_template.ProductName.Enabled) shown.Add(T("название", "аталышы", "name", "ad", "nomi"));
        if (_template.Price.Enabled) shown.Add(T("цена", "баасы", "price", "fiyat", "narx"));
        parts.Add(string.Join(", ", shown));
        parts.Add(T($"копий {_settings.DefaultCopies}", $"көчүрмө {_settings.DefaultCopies}", $"copies {_settings.DefaultCopies}", $"kopya {_settings.DefaultCopies}", $"nusxa {_settings.DefaultCopies}"));
        parts.Add(_printerCombo.SelectedItem is DiscoveredPrinter pr ? pr.Label : T("принтер не подключён", "принтер туташкан эмес", "no printer", "yazıcı bağlı değil", "printer ulanmagan"));
        _settingsSummary.Text = string.Join(" · ", parts.Where(s => s.Length > 0));
    }

    private void SizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_init || _sizeCombo.SelectedIndex < 0)
            return;
        var custom = _sizeCombo.SelectedIndex == SizePresets.Length;
        _customSizePanel.IsVisible = custom;
        if (custom)
        {
            _widthBox.Value = (decimal)_template.WidthMm;
            _heightBox.Value = (decimal)_template.HeightMm;
            return;
        }
        var (w, h) = SizePresets[_sizeCombo.SelectedIndex];
        ApplySize(w, h);
    }

    private void CustomSize_Changed()
    {
        if (_init || _sizeCombo.SelectedIndex != SizePresets.Length)
            return;
        ApplySize((double)(_widthBox.Value ?? 40m), (double)(_heightBox.Value ?? 30m));
    }

    private void ApplySize(double w, double h)
    {
        w = Math.Clamp(w, 15, 150);
        h = Math.Clamp(h, 10, 150);
        if (Math.Abs(_template.WidthMm - w) < 0.01 && Math.Abs(_template.HeightMm - h) < 0.01)
            return;
        // Ручной размер шрифта (если задан в редакторе) растёт и убывает вместе с этикеткой — иначе на малой этикетке он не помещается.
        var k = Math.Min(w / Math.Max(_template.WidthMm, 1), h / Math.Max(_template.HeightMm, 1));
        _template.WidthMm = w;
        _template.HeightMm = h;
        foreach (var e in new[] { _template.ProductName, _template.Price, _template.Sku, _template.Unit, _template.StoreName, _template.Variant })
            if (e.FontSizePx is > 0)
                e.FontSizePx = Math.Max(4, Math.Round(e.FontSizePx.Value * k, 1));
        if (_template.FontSizePx > 0)
            _template.FontSizePx = Math.Max(4, Math.Round(_template.FontSizePx * k, 1));
        Relayout();
    }

    private void DefaultCopies_Changed()
    {
        if (_init)
            return;
        _settings.DefaultCopies = (int)(_copiesBox.Value ?? 1);
        BarcodePrintSettingsStore.Save(_settings);
        // Карточки без своего числа копий показывают новое общее значение.
        _copiesGuard = true;
        try { foreach (var s in _copiesSync) s(); }
        finally { _copiesGuard = false; }
        UpdateSelectionUi();
        UpdateSummary();
    }

    private void FlagChanged(Action apply, bool relayout, bool barcodeToggled = false)
    {
        if (_init)
            return;
        apply();
        if (relayout)
            Relayout(); // перерисует и страницу, и предпросмотр
        else
            TemplateChanged();
        if (barcodeToggled)
        {
            // Штрих-код включили: у выбранных товаров без штрихкода печатать нечего — выбор снимается; выключили — их карточки снова доступны.
            var dropped = 0;
            if (_template.Barcode.Enabled)
                foreach (var p in CatalogCacheService.Products)
                    if (_sel.TryGetValue(p.Id, out var st) && st.Selected && !BarcodePrintCatalog.HasBarcode(p)) { SetSelected(st, false); dropped++; }
            RenderPage();
            UpdateSelectionUi();
            if (dropped > 0)
                SetStatus(T($"Снят выбор у {dropped} товаров без штрихкода.", $"Штрих-коду жок {dropped} товардын тандоосу алынды.", $"Deselected {dropped} products without a barcode.", $"Barkodsuz {dropped} ürünün seçimi kaldırıldı.", $"Shtrix-kodsiz {dropped} ta mahsulot tanlovi bekor qilindi."));
        }
    }

    private void Relayout()
    {
        var eff = LabelAutoLayout.Apply(_template, _barPx);
        _barHeightHint.IsVisible = _template.Barcode.Enabled && eff < _barPx - 1;
        _barHeightHint.Text = T($"Фактически {eff} px: этикетка низкая — остальное заняло место.", $"Иш жүзүндө {eff} px: этикетка аз бийик.", $"Actually {eff} px: the label is short.", $"Gerçekte {eff} px: etiket alçak.", $"Aslida {eff} px: yorliq past.");
        TemplateChanged();
    }

    private void TemplateChanged()
    {
        _templateDirty = true;
        UpdateSummary();
        SchedulePreview();
    }

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshPreview()
    {
        try
        {
            CatalogProductTileVm? sample = null;
            foreach (var p in CatalogCacheService.Products)
                if (_sel.TryGetValue(p.Id, out var st) && st.Selected) { sample = p; break; }
            sample ??= _found.FirstOrDefault(BarcodePrintCatalog.HasBarcode) ?? _found.FirstOrDefault();
            var name = sample?.Title ?? T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi");
            var code = "4870145004807";
            if (sample != null)
            {
                var ch = BarcodePrintCatalog.Choices(sample);
                if (ch.Count > 0)
                    code = ch[Math.Clamp(GetSel(sample.Id).BarcodeIndex, 0, ch.Count - 1)].Code;
            }
            var price = sample != null ? BarcodePrintCatalog.LabelPrice(sample) : "85,00 сом";
            // 2026-10-29: Android/Linux — System.Drawing недоступен, картинку рисует Avalonia (LabelSceneRenderer).
            var next = LabelSceneRenderer.LabelPreview(name, code, price, _template, sample?.Article, sample?.Unit, UserPreferences.Instance.StoreName);
            _previewImage.Source = next;
            _previewBitmap?.Dispose();
            _previewBitmap = next;
            _previewCaption.Text = sample != null ? name : T("Образец", "Үлгү", "Sample", "Örnek", "Namuna");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: preview failed: {ex}", "WARNING");
        }
    }

    private async Task OpenEditorAsync()
    {
        // 2026-10-27 (аудит редакторов, Б-2): редактор этикеток — отдельная покупка, из массовой печати он открывался без проверки.
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.LabelEditor, this))
            return;
        SaveState();
        var sample = BuildItems().FirstOrDefault() ?? (_found.Count > 0 ? new BarcodePrintItem(_found[0], BarcodePrintCatalog.Choices(_found[0]).FirstOrDefault()?.Code, 1) : null);
        var editor = new LabelTemplateEditorDialog(_template, sample?.Product.Title ?? T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi"),
            sample?.Barcode ?? "4870145004807", sample != null ? BarcodePrintCatalog.LabelPrice(sample.Product) : "85,00 сом",
            LabelTemplateStore.Save); // со своим saveAction — кнопки «Печать штрихкодов…» в редакторе нет (мы уже в этом окне)
        await editor.ShowDialog(this).ConfigureAwait(true);
        if (!editor.Saved)
            return;
        _template = LabelTemplateStore.Load();
        _templateDirty = false;
        _init = true;
        _showName.IsChecked = _template.ProductName.Enabled;
        _showPrice.IsChecked = _template.Price.Enabled;
        _showBarcode.IsChecked = _template.Barcode.Enabled;
        _showDigits.IsChecked = _template.BarcodeShowDigits;
        _showSku.IsChecked = _template.Sku.Enabled;
        _showUnit.IsChecked = _template.Unit.Enabled;
        _showStore.IsChecked = _template.StoreName.Enabled;
        _barPx = LabelAutoLayout.BarcodeHeightPx(_template);
        _barHeightBox.Value = _barPx;
        _barHeightHint.IsVisible = false;
        _thicknessCombo.SelectedIndex = Math.Clamp(_template.BarcodeModuleDots, 0, 3);
        _sizeCombo.SelectedIndex = FindPresetIndex();
        _customSizePanel.IsVisible = _sizeCombo.SelectedIndex == SizePresets.Length;
        _widthBox.Value = (decimal)_template.WidthMm;
        _heightBox.Value = (decimal)_template.HeightMm;
        _init = false;
        UpdateSummary();
        RenderPage();
        RefreshPreview();
    }

    /// <summary>Шаблон этикетки и настройки — на диск, если менялись.</summary>
    private void SaveState()
    {
        if (_templateDirty)
        {
            LabelTemplateStore.Save(_template);
            _templateDirty = false;
        }
        BarcodePrintSettingsStore.Save(_settings);
    }

    // ------------------------------------------------------------------ принтер

    private void PrinterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 2026-10-27 (приёмка 26.10): PDF/XPS/OneNote/факс как принтер этикеток в настройках не запоминаем
        if (_printerCombo.SelectedItem is DiscoveredPrinter p && !_init && !p.IsVirtual)
        {
            UserPreferences.Instance.LabelPrinterDevicePath = p.DevicePath;
            UserPreferences.Instance.SaveToDisk();
        }
        UpdatePrinterChip();
        UpdateSummary();
    }

    private void UpdatePrinterChip()
    {
        var connected = _printerCombo.SelectedItem is DiscoveredPrinter;
        _printerChipText.Text = connected
            ? ((DiscoveredPrinter)_printerCombo.SelectedItem!).Label
            : T("Принтер не подключён", "Принтер туташкан эмес", "Printer not connected", "Yazıcı bağlı değil", "Printer ulanmagan");
        Use(_printerDot, Shape.FillProperty, connected ? "BrushSuccess" : "BrushDanger");
    }

    private async Task LoadPrintersAsync()
    {
        IReadOnlyList<DiscoveredPrinter> list;
        try
        {
            list = await Task.Run(() => PrinterListOverride?.Invoke() ?? BarcodeLabelService.GetAvailablePrinters()).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: printer enumeration failed: {ex.GetType().Name}", "WARNING");
            list = Array.Empty<DiscoveredPrinter>();
        }
        var savedPath = UserPreferences.Instance.LabelPrinterDevicePath;
        var keep = (_printerCombo.SelectedItem as DiscoveredPrinter)?.DevicePath ?? savedPath;
        var wasInit = _init;
        _init = true;
        _printerCombo.ItemsSource = list.ToList();
        _printerCombo.SelectedItem = list.FirstOrDefault(p => string.Equals(p.DevicePath, keep, StringComparison.OrdinalIgnoreCase));
        _init = wasInit;
        UpdatePrinterChip();
        UpdateSummary();
    }

    // ------------------------------------------------------------------ печать

    private void SetStatus(string text) => _status.Text = text;

    private async Task PrintTestAsync()
    {
        var sample = new BarcodePrintItem(new CatalogProductTileVm("test", T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi"), "85.00 сом", false) { Barcode = "4870145004807" }, "4870145004807", 1);
        await PrintLabelsAsync(new[] { sample }, test: true).ConfigureAwait(true);
    }

    private async Task PrintOneAsync(CatalogProductTileVm p)
    {
        var st = GetSel(p.Id);
        var choices = BarcodePrintCatalog.Choices(p);
        var code = choices.Count == 0 ? null : choices[Math.Clamp(st.BarcodeIndex, 0, choices.Count - 1)].Code;
        await PrintLabelsAsync(new[] { new BarcodePrintItem(p, code, Math.Clamp(st.Copies ?? _settings.DefaultCopies, 1, 99)) }, test: false).ConfigureAwait(true);
    }

    /// <summary>«Печать выбранных (N)» — на термопринтер этикеток.</summary>
    public async Task PrintSelectedAsync()
    {
        var items = BuildItems();
        if (items.Count == 0)
        {
            SetStatus(T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang."));
            return;
        }
        await PrintLabelsAsync(items, test: false).ConfigureAwait(true);
    }

    private async Task PrintLabelsAsync(IReadOnlyList<BarcodePrintItem> items, bool test)
    {
        if (_printerCombo.SelectedItem is not DiscoveredPrinter printer)
        {
            SetStatus(T("Принтер не подключён — выберите принтер в настройках этикетки.", "Принтер туташкан эмес — этикетка жөндөөлөрүнөн принтерди тандаңыз.", "No printer connected — choose a printer in the label settings.", "Yazıcı bağlı değil — etiket ayarlarından bir yazıcı seçin.", "Printer ulanmagan — yorliq sozlamalaridan printerni tanlang."));
            SetSettingsOpen(true);
            return;
        }
        if (!_shell.TryBeginBusy())
            return;
        SaveState();
        var template = _template.Clone();
        var store = UserPreferences.Instance.StoreName;
        var print = ThermalPrintOverride ?? BarcodeLabelService.Print;
        _printCts = new CancellationTokenSource();
        var ct = _printCts.Token;
        _progress.Maximum = Math.Max(items.Count, 1);
        _progress.Value = 0;
        _progress.IsVisible = items.Count > 1;
        _cancelPrintButton.IsVisible = items.Count > 1;
        _cancelPrintButton.IsEnabled = true;
        foreach (var b in new[] { _selectPageButton, _selectFoundButton, _clearButton, _a4Button, _printButton })
            b.IsEnabled = false;
        SetStatus(T($"Печать… (товаров: {items.Count})", $"Басып жатат… (товар: {items.Count})", $"Printing… ({items.Count} products)", $"Yazdırılıyor… ({items.Count} ürün)", $"Chop etilmoqda… ({items.Count} ta mahsulot)"));
        var done = 0;
        var labels = 0;
        var last = LabelPrintResult.Success;
        var cancelled = false;
        try
        {
            await Task.Run(() =>
            {
                foreach (var it in items)
                {
                    if (ct.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }
                    var request = new LabelPrintRequest(it.Product.Title, it.Barcode ?? "", BarcodePrintCatalog.LabelPrice(it.Product), it.Copies, printer.DevicePath,
                        template, it.Product.Article, it.Product.Unit, store);
                    last = print(request);
                    if (last != LabelPrintResult.Success)
                        break;
                    done++;
                    labels += it.Copies;
                    var d = done;
                    Dispatcher.UIThread.Post(() => _progress.Value = d);
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print window: print failed: {ex}", "ERROR");
            last = LabelPrintResult.Failed;
        }
        finally
        {
            _progress.IsVisible = false;
            _cancelPrintButton.IsVisible = false;
            _printCts?.Dispose();
            _printCts = null;
            _shell.EndBusy();
            _clearButton.IsEnabled = _selectPageButton.IsEnabled = _selectFoundButton.IsEnabled = true;
            UpdateSelectionUi();
        }

        var problems = test ? 0 : items.Count(i => BarcodeLabelService.DescribeBarcodeProblem(i.Barcode, template) is not null);
        var problemNote = problems == 0 ? "" : " " + T(
            $"Замечания по штрихкоду у {problems} товаров (неверный EAN — напечатан как Code 128, кириллица, слишком длинный или плотный код).",
            $"{problems} товардын штрих-кодунда эскертүү бар (туура эмес EAN — Code 128 катары басылды, кирилл, өтө узун же тыгыз код).",
            $"Barcode notes for {problems} products (invalid EAN printed as Code 128, Cyrillic, too long or too dense).",
            $"{problems} üründe barkod uyarısı var (geçersiz EAN Code 128 olarak basıldı, Kiril, çok uzun veya çok sık).",
            $"{problems} ta mahsulotda shtrix-kod bo'yicha eslatma bor (noto'g'ri EAN Code 128 sifatida chop etildi, kirill, juda uzun yoki zich).");
        SetStatus(last switch
        {
            LabelPrintResult.Success when cancelled => T($"Печать остановлена: отправлено {labels} этикеток ({done} из {items.Count} товаров).", $"Басуу токтотулду: {labels} этикетка жөнөтүлдү ({done}/{items.Count} товар).", $"Printing stopped: {labels} labels sent ({done} of {items.Count} products).", $"Yazdırma durduruldu: {labels} etiket gönderildi ({done}/{items.Count} ürün).", $"Chop etish to'xtatildi: {labels} ta yorliq yuborildi ({done}/{items.Count} mahsulot)."),
            LabelPrintResult.Success => T($"Готово — отправлено на печать {labels} этикеток.", $"Даяр — {labels} этикетка басууга жөнөтүлдү.", $"Done — {labels} labels sent to the printer.", $"Tamamlandı — {labels} etiket yazdırmaya gönderildi.", $"Tayyor — {labels} ta yorliq chop etishga yuborildi.") + problemNote,
            LabelPrintResult.PrinterNotFound => T("Принтер не найден — обновите список принтеров.", "Принтер табылган жок — принтерлердин тизмесин жаңыртыңыз.", "Printer not found — refresh the printer list.", "Yazıcı bulunamadı — yazıcı listesini yenileyin.", "Printer topilmadi — printerlar ro'yxatini yangilang."),
            _ => T($"Ошибка печати (напечатано {labels} этикеток). Подробности в журнале приложения.", $"Басуу катасы ({labels} этикетка басылды). Толук маалымат колдонмонун журналында.", $"Printing error ({labels} labels printed). See the app log.", $"Yazdırma hatası ({labels} etiket basıldı). Ayrıntılar uygulama günlüğünde.", $"Chop etishda xato ({labels} ta yorliq chop etildi). Batafsil ma'lumot ilova jurnalida."),
        });
    }

    // ------------------------------------------------------------------ A4

    public async Task OpenA4Async()
    {
        var items = BuildItems();
        if (items.Count == 0)
        {
            SetStatus(T("Выберите хотя бы один товар.", "Жок дегенде бир товарды тандаңыз.", "Select at least one product.", "En az bir ürün seçin.", "Kamida bitta mahsulotni tanlang."));
            return;
        }
        var overrides = items.Select(i => _sel.TryGetValue(i.Product.Id, out var s) ? s.Copies : null).ToList();
        var a4 = new BarcodeA4SheetWindow(items, overrides, _settings, UserPreferences.Instance.StoreName);
        await a4.ShowDialog(this).ConfigureAwait(true);
        if (!string.IsNullOrEmpty(a4.ResultMessage))
            SetStatus(a4.ResultMessage);
    }

    // ------------------------------------------------------------------ штрихкод для товара без кода

    private async Task CreateBarcodeAsync(CatalogProductTileVm p)
    {
        // Только для товара без штрихкода: ничего не стирается, у товара появляется один новый код.
        if (BarcodePrintCatalog.HasBarcode(p))
            return;
        var code = BarcodeGenerator.GenerateForCatalog();
        var question = T($"Сгенерировать штрихкод {code} и сохранить его в карточку товара «{p.Title}»? Изменение появится в NurCRM и на всех кассах.",
            $"{code} штрих-кодун түзүп, «{p.Title}» товарынын карточкасына сактайсызбы? Өзгөртүү NurCRM'де жана бардык кассаларда пайда болот.",
            $"Generate barcode {code} and save it to the card of “{p.Title}”? The change will appear in NurCRM and on all registers.",
            $"{code} barkodu üretilip «{p.Title}» ürün kartına kaydedilsin mi? Değişiklik NurCRM'de ve tüm kasalarda görünür.",
            $"{code} shtrix-kodini yaratib, «{p.Title}» mahsuloti kartochkasiga saqlaymizmi? O'zgarish NurCRM da va barcha kassalarda paydo bo'ladi.");
        var ok = ConfirmOverride != null
            ? await ConfirmOverride(question).ConfigureAwait(true)
            : await PosConfirmDialog.ShowModalAsync(this, T("Создать штрих-код", "Штрих-код түзүү", "Create a barcode", "Barkod oluştur", "Shtrix-kod yaratish"), question,
                T("Создать и сохранить", "Түзүп сактоо", "Create and save", "Oluştur ve kaydet", "Yaratib saqlash"), T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish")).ConfigureAwait(true);
        if (!ok)
            return;
        if (!_shell.TryBeginBusy())
            return;
        string? error;
        try
        {
            SetStatus(T("Сохраняю штрихкод в карточку товара…", "Штрих-кодду товардын карточкасына сактап жатам…", "Saving the barcode to the product card…", "Barkod ürün kartına kaydediliyor…", "Shtrix-kod mahsulot kartochkasiga saqlanmoqda…"));
            error = BarcodeSaveOverride != null
                ? await BarcodeSaveOverride(p.Id, code).ConfigureAwait(true)
                : await SaveBarcodeToServerAsync(p, code).ConfigureAwait(true);
        }
        finally
        {
            _shell.EndBusy();
        }
        if (error != null)
        {
            SetStatus(error);
            return;
        }
        p.Barcode = code;
        SetStatus(T($"Штрихкод {code} сохранён в карточке «{p.Title}».", $"{code} штрих-коду «{p.Title}» карточкасына сакталды.", $"Barcode {code} saved to the card of “{p.Title}”.", $"{code} barkodu «{p.Title}» kartına kaydedildi.", $"{code} shtrix-kodi «{p.Title}» kartochkasiga saqlandi."));
        RenderPage();
        UpdateSelectionUi();
    }

    private static async Task<string?> SaveBarcodeToServerAsync(CatalogProductTileVm p, string code)
    {
        var result = await ProductActions.UpdateFieldsAsync(p.Id, new Dictionary<string, object?> { ["barcode"] = code },
            T("штрихкод", "штрих-код", "barcode", "barkod", "shtrix-kod"), PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId).ConfigureAwait(true);
        return result.Ok ? null : result.Message;
    }
}
