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
public sealed partial class BarcodePrintWindow : Window
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
    // 2026-11-06 (просьба клиента магазина одежды): «Формат кода» для этикеток РАЗМЕРОВ — как в шаблоне / штрихкод / QR. Только на время печати, в шаблон не пишется.
    private readonly ComboBox _codeModeCombo = new() { MinWidth = 160 };
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

    // 2026-11-01 (пакеты П-1…П-9 из веба)
    private readonly TextBlock _barDotsHint;
    private readonly Button _resetCopiesButton;
    private readonly ComboBox _copiesModeCombo;
    private readonly CheckBox _scanCheck;
    private HashSet<string>? _onlyIds;          // П-4: окно открыто с готовым выбором — пока поиск пуст, показываем только эти товары
    private string? _presetNote;
    private bool _restoring, _scanClearing;
    private int _filterVersion;
    private readonly Dictionary<string, string?> _problemCache = new(StringComparer.Ordinal);
    private readonly List<Action> _warnSync = new();
    private readonly List<Action> _codeSync = new();

    private static readonly (double W, double H)[] SizePresets = { (30, 20), (40, 30), (58, 40), (58, 30), (100, 70) };

    private static string CustomSizeLabel => T("Свой размер", "Жеке өлчөм", "Custom size", "Özel boyut", "Maxsus o'lcham");

    /// <summary>Открыть окно из Склада и Маркетплейса (прежняя точка входа BulkPriceTagPrintDialog.Open).</summary>
    public static async Task Open(Window? owner)
    {
        if (!BarcodePrintAccess.Ensure(owner)) // 2026-11-01 (П-10): право «Печать штрих-кодов»
            return;
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

    /// <summary>2026-11-01 (П-4): открыть окно с отмеченными товарами и готовыми копиями (например, «Этикетки на приход»).
    /// Платная проверка та же, что у Open. Запомненный поиск не применяется. Товары, которых нет в каталоге кассы, пропускаются с сообщением.</summary>
    public static async Task OpenWith(Window? owner, IReadOnlyList<(string ProductId, string? Barcode, int Copies)> items)
    {
        if (!BarcodePrintAccess.Ensure(owner)) // 2026-11-01 (П-10): право «Печать штрих-кодов»
            return;
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.BulkPriceTag, owner))
            return;
        var window = new BarcodePrintWindow(false);
        window.ApplyPreset(items);
        if (owner is null)
        {
            window.Show();
            return;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    public BarcodePrintWindow() : this(true)
    {
    }

    internal BarcodePrintWindow(bool restoreLast)
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
        _search.TextChanged += (_, _) => { if (_restoring || _scanClearing) return; _searchTimer.Stop(); _searchTimer.Start(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            _searchTimer.Stop();
            // 2026-11-01 (П-5): режим сканера — Enter (скан) добавляет товар в печать, а не ищет.
            if (_scanCheck.IsChecked == true && !string.IsNullOrWhiteSpace(_search.Text))
            {
                ScanToQueue(_search.Text!);
                return;
            }
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
                     (BarcodeFilter.WithProblems, T("С замечаниями", "Эскертүү менен", "With warnings", "Uyarılı", "Eslatmali")),
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

        _scanCheck = new CheckBox { Content = T("Сканер: добавлять в печать", "Сканер: басууга кошуу", "Scanner: add to print", "Tarayıcı: yazdırmaya ekle", "Skaner: chopga qo'shish"), IsChecked = _settings.ScanToQueue, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        ToolTip.SetTip(_scanCheck, T("Скан штрихкода отмечает товар и добавляет копию; затем «Печать выбранных».", "Штрих-кодду сканерлөө товарды белгилеп, көчүрмө кошот; андан соң «Тандалгандарды басуу».",
            "Scanning a barcode ticks the product and adds a copy; then “Print selected”.", "Barkod okutmak ürünü işaretler ve bir kopya ekler; sonra «Seçilenleri yazdır».", "Shtrix-kodni skanerlash mahsulotni belgilaydi va nusxa qo'shadi; so'ngra «Tanlanganlarni chop etish»."));
        _scanCheck.IsCheckedChanged += (_, _) =>
        {
            if (_init)
                return;
            _settings.ScanToQueue = _scanCheck.IsChecked == true;
            BarcodePrintSettingsStore.Save(_settings);
        };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetColumn(_scanCheck, 3);
        toolbar.Children.Add(_scanCheck);
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
        // 2026-11-01 (П-4): «Копии: по умолчанию / = остаток»; (П-1): «Сбросить копии позиций».
        _copiesModeCombo = new ComboBox { MinWidth = 150 };
        _copiesModeCombo.Items.Add(T("по умолчанию", "демейки", "default", "varsayılan", "standart"));
        _copiesModeCombo.Items.Add(T("= остаток", "= калдык", "= stock", "= stok", "= qoldiq"));
        _copiesModeCombo.SelectedIndex = _settings.CopiesMode == "stock" ? 1 : 0;
        _copiesBox.IsEnabled = _settings.CopiesMode != "stock";
        _resetCopiesButton = Ghost(T("Сбросить копии позиций", "Позиция көчүрмөлөрүн кайтаруу", "Reset item copies", "Kalem kopyalarını sıfırla", "Pozitsiya nusxalarini tiklash"));
        _resetCopiesButton.IsVisible = false;
        ToolTip.SetTip(_resetCopiesButton, T("Вернуть всем позициям количество копий по умолчанию", "Бардык позицияларга демейки көчүрмө санын кайтаруу", "Give every item the default number of copies again", "Tüm kalemlere varsayılan kopya sayısını geri ver", "Barcha pozitsiyalarga standart nusxa sonini qaytarish"));

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
        // 2026-11-01 (П-1): высота штрихкода — в миллиметрах (внутри по-прежнему пиксели 96 dpi шаблона), рядом «≈ N точек 203 dpi».
        _barHeightBox = new NumericUpDown { Minimum = 4, Maximum = 80, Value = BarMmFromPx(_barPx), Increment = 0.5m, FormatString = "0.0", Width = 118, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left };
        _barDotsHint = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        Use(_barDotsHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        _barHeightHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxWidth = 520 };
        Use(_barHeightHint, TextBlock.ForegroundProperty, "BrushWarning");
        _thicknessCombo = new ComboBox { MinWidth = 160 };
        foreach (var s in new[]
                 {
                     T("Авто", "Авто", "Auto", "Otomatik", "Avto"), "1 " + T("(тонкий)", "(ичке)", "(thin)", "(ince)", "(ingichka)"),
                     "2 " + T("(стандарт)", "(стандарт)", "(standard)", "(standart)", "(standart)"), "3 " + T("(толстый)", "(жоон)", "(thick)", "(kalın)", "(qalin)"),
                 })
            _thicknessCombo.Items.Add(s);
        _thicknessCombo.Items.Add("4 " + T("(очень толстый)", "(өтө жоон)", "(extra thick)", "(çok kalın)", "(juda qalin)")); // 2026-11-01 (П-1)
        _thicknessCombo.SelectedIndex = Math.Clamp(_template.BarcodeModuleDots, 0, 4);
        foreach (var s in new[] { T("Как в шаблоне", "Шаблондогудай", "As in template", "Şablondaki gibi", "Shablondagidek"), T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), "QR" })
            _codeModeCombo.Items.Add(s);
        _codeModeCombo.SelectedIndex = 0;
        ToolTip.SetTip(_codeModeCombo, T("Для этикеток размеров (одежда): печатать штрихкод или QR с кодом размера. Остальные товары — как в шаблоне.",
            "Өлчөм этикеткалары үчүн (кийим): штрихкод же өлчөмдүн коду менен QR басуу. Башка товарлар — шаблондогудай.",
            "For size labels (clothing): print a barcode or a QR with the size code. Other products stay as in the template.",
            "Beden etiketleri için (giyim): barkod veya beden koduyla QR basın. Diğer ürünler şablondaki gibi kalır.",
            "O'lcham yorliqlari uchun (kiyim): shtrix-kod yoki o'lcham kodi bilan QR chop etish. Boshqa mahsulotlar — shablondagidek."));

        var editorButton = Ghost(T("Редактор этикетки…", "Этикетка редактору…", "Label editor…", "Etiket düzenleyici…", "Yorliq muharriri…"));
        editorButton.Click += async (_, _) => await OpenEditorAsync().ConfigureAwait(true);
        var autoButton = Ghost(T("Расставить заново", "Кайра иретке келтирүү", "Re-arrange", "Yeniden diz", "Qayta joylash"));
        ToolTip.SetTip(autoButton, T("Расставить элементы этикетки автоматически по размеру и высоте штрих-кода", "Этикетканын элементтерин өлчөмү жана штрих-кодунун бийиктиги боюнча автоматтык түрдө иретке келтирүү",
            "Arrange the label elements automatically by size and barcode height", "Etiket öğelerini boyuta ve barkod yüksekliğine göre otomatik diz", "Yorliq elementlarini o'lcham va shtrix-kod balandligiga qarab avtomatik joylashtirish"));
        autoButton.Click += (_, _) => { if (_init) return; _barPx = BarPxFromBox(); Relayout(); };

        _previewImage = new Image { Stretch = Stretch.Uniform };
        _previewCaption = new TextBlock { FontSize = 12, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Use(_previewCaption, TextBlock.ForegroundProperty, "BrushTextSoft");

        // Подпись слева от поля (в строку) — настройки занимают меньше высоты, карточкам товаров остаётся место.
        var rowA = new WrapPanel { Orientation = Orientation.Horizontal };
        rowA.Children.Add(Inline(T("Размер", "Өлчөм", "Size", "Boyut", "O'lcham"), _sizeCombo));
        rowA.Children.Add(Inline("", _customSizePanel));
        rowA.Children.Add(Inline(T("Копии", "Көчүрмө", "Copies", "Kopya", "Nusxa"), _copiesModeCombo));
        rowA.Children.Add(Inline(T("Копий (по умолчанию)", "Көчүрмө (демейки)", "Copies (default)", "Kopya (varsayılan)", "Nusxa (standart)"), _copiesBox));
        _resetCopiesButton.Margin = new Thickness(0, 0, 14, 6);
        rowA.Children.Add(_resetCopiesButton);
        rowA.Children.Add(Inline(T("Высота штрих-кода, мм", "Штрих-код бийиктиги, мм", "Barcode height, mm", "Barkod yüksekliği, mm", "Shtrix-kod balandligi, mm"),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _barHeightBox, _barDotsHint } }));
        rowA.Children.Add(Inline(T("Толщина штриха", "Сызык калыңдыгы", "Bar thickness", "Çizgi kalınlığı", "Chiziq qalinligi"), _thicknessCombo));
        rowA.Children.Add(Inline(T("Формат кода (размеры)", "Код форматы (өлчөмдөр)", "Code format (sizes)", "Kod biçimi (bedenler)", "Kod formati (o'lchamlar)"), _codeModeCombo));

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
        _copiesModeCombo.SelectionChanged += (_, _) => CopiesMode_Changed();
        _resetCopiesButton.Click += (_, _) => ResetItemCopies();
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
            _barPx = BarPxFromBox();
            Relayout();
        };
        _thicknessCombo.SelectionChanged += (_, _) =>
        {
            if (_init || _thicknessCombo.SelectedIndex < 0)
                return;
            _template.BarcodeModuleDots = _thicknessCombo.SelectedIndex; // 0 — авто, 1…4 — точек на модуль
            TemplateChanged();
        };

        _codeModeCombo.SelectionChanged += (_, _) =>
        {
            if (_init || _codeModeCombo.SelectedIndex < 0)
                return;
            SchedulePreview();
            // 2026-11-06: QR на этой этикетке слишком мелкий — предупреждаем сразу, а не при печати.
            if (CodeMode == LabelCodeMode.Qr && LabelCodeModes.QrSizeProblem(_template, "4870145004807") is { } qrProblem)
                SetStatus(qrProblem);
        };

        _liveTimer.Tick += (_, _) => _ = RefreshLiveStatusAsync();
        Closed += (_, _) => _liveTimer.Stop();
        Opened += (_, _) =>
        {
            _liveTimer.Start();
            _ = LoadPrintersAsync();
            _status.Text = _presetNote ?? "";
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
        UpdateBarDots();
        if (restoreLast)
            RestoreLast();
        ApplyFilter();
        if (restoreLast && _filter != BarcodeFilter.WithProblems && _settings.LastPage > 0)
            GoToPage(_settings.LastPage);
        RefreshPreview();
    }

}
