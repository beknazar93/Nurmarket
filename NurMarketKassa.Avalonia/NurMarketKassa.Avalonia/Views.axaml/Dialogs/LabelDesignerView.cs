using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using NurMarketKassa.AvaloniaHost.Services;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-21, владелец: «а где редактор ценников как на вебе?». Визуальный редактор этикетки/ценника — режим «Редактор» вкладки
/// «Ценники и штрихкоды» Склада: холст в масштабе поверх настоящего предпросмотра выбранного товара, элементы (встроенные и свои: текст,
/// старая цена, скидка, бренд, дата, ячейка, линия, логотип) перетаскиваются мышью и стрелками, меняют размер за угол, выравниваются
/// по краям, привязываются к сетке; справа — свойства выбранного элемента (шрифт, размер, жирность, курсив, выравнивание, поворот).
/// Внизу — товар для проверки (поиск по каталогу), тест-печать, PDF, «Мои шаблоны». Шаблон — тот же <see cref="LabelTemplate"/>, что печатает
/// режим «Печать» (<see cref="LabelTemplateStore"/>); рисуют его те же функции, что печатают (BarcodeLabelService), — на экране то же, что на этикетке.
/// Расчёты (сетка, границы, выравнивание) — <see cref="LabelDesignerMath"/> (юнит-тесты). Перетаскивание и свойства — LabelDesignerView.Props.cs.</summary>
public sealed partial class LabelDesignerView : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private sealed record DElement(string Key, string Title, LabelElementLayout Layout, LabelCustomElement? Extra);

    private static readonly string[] Palette = { "#2563EB", "#16A34A", "#DB2777", "#EA580C", "#7C3AED", "#0891B2", "#CA8A04", "#475569" };

    private LabelTemplate _t;
    private string _savedJson;
    private string _selectedKey = "price";
    private double _grid = 0.5;
    private double _zoom = 1;
    private double _scale = 8; // пикселей экрана на мм
    private bool _building;
    private readonly List<string> _undo = new();

    // Товар для проверки.
    private CatalogProductTileVm? _product;
    private string _sampleName = T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi");
    private string _sampleCode = "4870145004807";
    private string _samplePrice = "285,00 сом";
    private LabelItemData _sampleData = LabelExtras.Sample;
    private string? _sampleSku = "SKU-001", _sampleUnit = "шт";

    private readonly Grid _canvasHost = new();
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _canvasFrame = new() { BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Image _previewImage = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Canvas _boxes = new();
    private readonly ScrollViewer _canvasScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _props = new() { Spacing = 6 };
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _warning = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 44, TextAlignment = TextAlignment.Center, FontSize = 12.5 };
    private NumericUpDown _wBox = null!, _hBox = null!;
    private ComboBox _sizeCombo = null!, _presetCombo = null!, _gridCombo = null!;
    private AutoCompleteBox _search = null!;
    private NumericUpDown _copiesBox = null!;
    private Button _undoButton = null!, _saveButton = null!;
    private bool _previewQueued;
    private Bitmap? _previewBitmap;

    /// <summary>Шаблон сохранён как рабочий (режим «Печать» подхватывает его).</summary>
    public event Action? TemplateSaved;

    /// <summary>«Печать товаров…» — перейти в режим «Печать».</summary>
    public event Action? PrintRequested;

    public LabelDesignerView()
    {
        _t = LabelTemplateStore.Load();
        EnsureSane(_t);
        _savedJson = Serialize(_t);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Focusable = true;
        Content = BuildLayout();
        KeyDown += OnKeyDown;
        AttachedToVisualTree += (_, _) => Reload();
        _canvasScroll.SizeChanged += (_, _) => { if (RefitScale()) RebuildCanvas(); };
        UpdateSelectionUi();
    }

    /// <summary>Рабочий шаблон редактора (для проверок).</summary>
    public LabelTemplate WorkingTemplate => _t;

    public string SelectedKey => _selectedKey;

    public bool IsDirty => Serialize(_t) != _savedJson;

    /// <summary>Добавить свой элемент (то же, что «+ Добавить элемент»); для проверок.</summary>
    public void AddElement(LabelExtraKind kind) => AddExtra(kind);

    /// <summary>Выбрать элемент по ключу (barcode, name, price, sku, unit, store, variant, x:&lt;id&gt;); для проверок.</summary>
    public void SelectElement(string key) => SelectKey(key);

    /// <summary>Отменить последнюю правку.</summary>
    public void Undo() => DoUndo();

    /// <summary>Сохранить рабочий шаблон (то же, что «Сохранить»).</summary>
    public void Save() => SaveAsWorking();

    public Canvas BoxesCanvas => _boxes;

    public Canvas CanvasRoot => _canvas;

    /// <summary>Масштаб холста: пикселей экрана на миллиметр.</summary>
    public double ScalePxPerMm => _scale;

    private static string Serialize(LabelTemplate t) => JsonSerializer.Serialize(t);

    private static void EnsureSane(LabelTemplate t)
    {
        t.Extras ??= new();
        t.Variant ??= new LabelElementLayout { XMm = 2, YMm = 0, WidthMm = 36, HeightMm = 4, Enabled = false };
        t.WidthMm = LabelDesignerMath.ClampSize(t.WidthMm);
        t.HeightMm = LabelDesignerMath.ClampSize(t.HeightMm);
    }

    /// <summary>Подхватить шаблон, сохранённый в других режимах («Печать», «Шаблоны»), если здесь нет несохранённых правок.</summary>
    public void Reload()
    {
        if (IsDirty)
        {
            FillPresets();
            return;
        }
        _t = LabelTemplateStore.Load();
        EnsureSane(_t);
        _savedJson = Serialize(_t);
        _undo.Clear();
        SyncAll();
    }

    /// <summary>Загрузить в редактор готовый шаблон («Шаблоны» → «Править»): считается несохранённой правкой рабочего шаблона.</summary>
    public void LoadTemplate(LabelTemplate template, string? hint = null)
    {
        Push();
        _t = template.Clone();
        _t.Item = null;
        EnsureSane(_t);
        SyncAll();
        if (hint != null)
            SetStatus(hint);
    }

    private void SyncAll()
    {
        _building = true;
        try
        {
            _wBox.Value = (decimal)_t.WidthMm;
            _hBox.Value = (decimal)_t.HeightMm;
            _sizeCombo.SelectedIndex = SizeIndex();
            _gridCombo.SelectedIndex = Math.Max(0, Array.FindIndex(LabelDesignerMath.GridSteps, s => Math.Abs(s - _grid) < 0.001));
        }
        finally
        {
            _building = false;
        }
        FillPresets();
        if (!AllElements().Any(e => e.Key == _selectedKey))
            _selectedKey = "price";
        RebuildChips();
        RefitScale();
        RebuildCanvas();
        BuildProps();
        QueuePreview();
        UpdateSelectionUi();
    }

    // ------------------------------------------------------------------ раскладка

    private static string ElementTitle(string key) => key switch
    {
        "barcode" => T("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"),
        "name" => T("Название", "Аталышы", "Name", "Ad", "Nomi"),
        "price" => T("Цена", "Баасы", "Price", "Fiyat", "Narx"),
        "sku" => T("Артикул", "Артикул", "SKU", "Stok kodu", "Artikul"),
        "unit" => T("Ед. изм.", "Өлч. бирд.", "Unit", "Birim", "O'lchov birligi"),
        "store" => T("Магазин", "Дүкөн", "Store", "Mağaza", "Do'kon"),
        "variant" => T("Размер и цвет", "Өлчөм жана түс", "Size and colour", "Beden ve renk", "O'lcham va rang"),
        _ => key,
    };

    private static string ExtraTitle(LabelExtraKind kind) => kind switch
    {
        LabelExtraKind.FreeText => T("Текст", "Текст", "Text", "Metin", "Matn"),
        LabelExtraKind.OldPrice => T("Старая цена", "Эски баа", "Old price", "Eski fiyat", "Eski narx"),
        LabelExtraKind.Discount => T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"),
        LabelExtraKind.Brand => T("Бренд", "Бренд", "Brand", "Marka", "Brend"),
        LabelExtraKind.Date => T("Дата печати", "Басып чыгаруу күнү", "Print date", "Yazdırma tarihi", "Chop etish sanasi"),
        LabelExtraKind.Cell => T("Ячейка", "Уяча", "Cell", "Hücre", "Katak"),
        LabelExtraKind.Line => T("Линия", "Сызык", "Line", "Çizgi", "Chiziq"),
        LabelExtraKind.Logo => T("Логотип", "Логотип", "Logo", "Logo", "Logotip"),
        LabelExtraKind.ShopQr => T("QR магазина", "Дүкөндүн QR'ы", "Shop QR", "Mağaza QR'ı", "Do'kon QR'i"),
        _ => kind.ToString(),
    };

    private List<DElement> AllElements()
    {
        var list = LabelDesignerMath.BuiltIns(_t).Select(b => new DElement(b.Key, ElementTitle(b.Key), b.Layout, null)).ToList();
        var counts = new Dictionary<LabelExtraKind, int>();
        foreach (var e in _t.Extras)
        {
            counts[e.Kind] = counts.GetValueOrDefault(e.Kind) + 1;
            var title = ExtraTitle(e.Kind) + (_t.Extras.Count(x => x.Kind == e.Kind) > 1 ? " " + counts[e.Kind] : "");
            list.Add(new DElement("x:" + e.Id, title, e, e));
        }
        return list;
    }

    private DElement? Selected() => AllElements().FirstOrDefault(e => e.Key == _selectedKey);

    private Control BuildLayout()
    {
        // Верхняя панель: шаблон, размер, сетка, масштаб, отмена.
        _presetCombo = new ComboBox { MinWidth = 200, MaxWidth = 280, MinHeight = 36, PlaceholderText = T("Шаблон…", "Шаблон…", "Template…", "Şablon…", "Shablon…") };
        _presetCombo.SelectionChanged += OnPresetPicked;
        _sizeCombo = new ComboBox { MinWidth = 110, MinHeight = 36 };
        foreach (var (w, h) in LabelDesignerMath.Sizes)
            _sizeCombo.Items.Add($"{MoneyText.Weight(w, "0.#")}×{MoneyText.Weight(h, "0.#")}"); // 2026-11-27: запятая одним форматтером (NumberFormatRatchet)
        _sizeCombo.Items.Add(T("Свой размер", "Жеке өлчөм", "Custom size", "Özel boyut", "Maxsus o'lcham"));
        _sizeCombo.SelectionChanged += (_, _) =>
        {
            if (_building || _sizeCombo.SelectedIndex < 0 || _sizeCombo.SelectedIndex >= LabelDesignerMath.Sizes.Length)
                return;
            var (w, h) = LabelDesignerMath.Sizes[_sizeCombo.SelectedIndex];
            ApplySize(w, h);
        };
        _wBox = Num(15, 150, 1, 1);
        _hBox = Num(15, 150, 1, 1);
        _wBox.ValueChanged += (_, _) => { if (!_building) ApplySize((double)(_wBox.Value ?? 40), _t.HeightMm); };
        _hBox.ValueChanged += (_, _) => { if (!_building) ApplySize(_t.WidthMm, (double)(_hBox.Value ?? 30)); };
        _gridCombo = new ComboBox { MinWidth = 100, MinHeight = 36 };
        foreach (var s in LabelDesignerMath.GridSteps)
            _gridCombo.Items.Add(s <= 0 ? T("Без сетки", "Тордун жок", "No grid", "Izgara yok", "To'rsiz") : MoneyText.Weight(s, "0.#") + " " + T("мм", "мм", "mm", "mm", "mm"));
        _gridCombo.SelectionChanged += (_, _) =>
        {
            if (_building || _gridCombo.SelectedIndex < 0)
                return;
            _grid = LabelDesignerMath.GridSteps[_gridCombo.SelectedIndex];
            RebuildCanvas();
        };
        _undoButton = Tool(T("Отменить", "Артка кайтаруу", "Undo", "Geri al", "Bekor qilish"), (_, _) => DoUndo());
        var zoomOut = Tool("−", (_, _) => Zoom(-0.25));
        var zoomIn = Tool("+", (_, _) => Zoom(0.25));
        var top = new WrapPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(Cap(T("Шаблон", "Шаблон", "Template", "Şablon", "Shablon"), _presetCombo));
        top.Children.Add(Cap(T("Размер, мм", "Өлчөм, мм", "Size, mm", "Boyut, mm", "O'lcham, mm"), _sizeCombo));
        top.Children.Add(Cap(T("Ширина", "Туурасы", "Width", "Genişlik", "Kengligi"), _wBox));
        top.Children.Add(Cap(T("Высота", "Бийиктиги", "Height", "Yükseklik", "Balandligi"), _hBox));
        top.Children.Add(Cap(T("Сетка", "Тор", "Grid", "Izgara", "To'r"), _gridCombo));
        top.Children.Add(Cap(T("Масштаб", "Масштаб", "Zoom", "Yakınlaştırma", "Masshtab"), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { zoomOut, _zoomText, zoomIn } }));
        top.Children.Add(Cap("", _undoButton));

        // Полоса элементов: переключатели «что есть на этикетке» и «Добавить».
        var addButton = Tool(T("+ Добавить элемент", "+ Элемент кошуу", "+ Add element", "+ Öğe ekle", "+ Element qo'shish"), null);
        addButton.Classes.Add("sec-primary");
        var flyout = new MenuFlyout();
        foreach (var kind in LabelExtras.AllKinds)
        {
            var k = kind;
            var item = new MenuItem { Header = ExtraTitle(k) };
            item.Click += (_, _) => AddExtra(k);
            flyout.Items.Add(item);
        }
        addButton.Flyout = flyout;
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var chipScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _chips };
        var chipsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        chipsGrid.Children.Add(chipScroll);
        Grid.SetColumn(addButton, 1);
        addButton.VerticalAlignment = VerticalAlignment.Top;
        chipsGrid.Children.Add(addButton);

        // Холст.
        _canvasFrame.Child = _canvas;
        _canvas.Children.Add(_previewImage);
        _canvas.Children.Add(_boxes);
        Use(_canvasFrame, Border.BorderBrushProperty, "BrushBorderStrong");
        _canvasFrame.Background = Brushes.White;
        _canvasScroll.Content = new Grid { Children = { _canvasFrame }, Margin = new Thickness(12) };
        var canvasBack = new Border { CornerRadius = new CornerRadius(10), Child = _canvasScroll, ClipToBounds = true, MinHeight = 150 };
        Use(canvasBack, Border.BackgroundProperty, "BrushSurfaceSubtle");
        _canvas.PointerPressed += (_, _) => Focus();

        var propsScroll = new ScrollViewer { Content = _props, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var propsCard = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = propsScroll, Width = 290, Margin = new Thickness(10, 0, 0, 0) };
        Use(propsCard, Border.BackgroundProperty, "BrushSurfaceSubtle");

        var mid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        mid.Children.Add(canvasBack);
        Grid.SetColumn(propsCard, 1);
        mid.Children.Add(propsCard);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        root.Children.Add(top);
        Grid.SetRow(chipsGrid, 1);
        root.Children.Add(chipsGrid);
        Grid.SetRow(mid, 2);
        root.Children.Add(mid);
        Use(_warning, TextBlock.ForegroundProperty, "BrushWarning");
        Grid.SetRow(_warning, 3);
        root.Children.Add(_warning);
        var bottom = BuildBottom();
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);
        return root;
    }

    private void Zoom(double delta)
    {
        _zoom = Math.Clamp(Math.Round(_zoom + delta, 2), 0.5, 3);
        RefitScale();
        RebuildCanvas();
    }

    private bool RefitScale()
    {
        var vp = _canvasScroll.Bounds.Size;
        var availW = Math.Max(vp.Width - 40, 120);
        var availH = Math.Max(vp.Height - 40, 80);
        var fit = Math.Min(availW / _t.WidthMm, availH / _t.HeightMm);
        var next = Math.Clamp(fit, 3, 14) * _zoom;
        _zoomText.Text = $"{(int)Math.Round(_zoom * 100)}%";
        if (Math.Abs(next - _scale) < 0.01)
            return false;
        _scale = next;
        return true;
    }

    // ------------------------------------------------------------------ холст

    private void RebuildCanvas()
    {
        var w = _t.WidthMm * _scale;
        var h = _t.HeightMm * _scale;
        _canvas.Width = w;
        _canvas.Height = h;
        _previewImage.Width = w;
        _previewImage.Height = h;
        _boxes.Width = w;
        _boxes.Height = h;
        _boxes.Children.Clear();
        var all = AllElements();
        var i = 0;
        foreach (var el in all)
        {
            var color = Color.Parse(Palette[i++ % Palette.Length]);
            if (!el.Layout.Enabled)
                continue;
            _boxes.Children.Add(MakeBox(el, color, el.Key == _selectedKey));
        }
        if (_grid > 0)
            _boxes.Children.Insert(0, new GridDots(_t.WidthMm, _t.HeightMm, _grid, _scale));
    }

    private Control MakeBox(DElement el, Color color, bool selected)
    {
        var l = el.Layout;
        var rect = new Rectangle
        {
            Width = Math.Max(l.WidthMm * _scale, 6),
            Height = Math.Max(l.HeightMm * _scale, 6),
            Stroke = new SolidColorBrush(color, selected ? 1 : 0.7),
            StrokeThickness = selected ? 2 : 1,
            StrokeDashArray = selected ? null : new AvaloniaList<double> { 4, 3 },
            Fill = new SolidColorBrush(color, selected ? 0.10 : 0.04),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Tag = el.Key,
        };
        AutomationProperties.SetName(rect, el.Title);
        Canvas.SetLeft(rect, l.XMm * _scale);
        Canvas.SetTop(rect, l.YMm * _scale);
        WireDrag(rect, el);
        if (!selected)
        {
            ToolTip.SetTip(rect, el.Title);
            return rect;
        }
        // Выбранный: рамка + подпись + угол изменения размера.
        var host = new Canvas { Width = rect.Width, Height = rect.Height, Tag = el.Key };
        Canvas.SetLeft(host, l.XMm * _scale);
        Canvas.SetTop(host, l.YMm * _scale);
        Canvas.SetLeft(rect, 0);
        Canvas.SetTop(rect, 0);
        host.Children.Add(rect);
        var grip = new Border
        {
            Width = 12, Height = 12, Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2),
            Cursor = new Cursor(StandardCursorType.BottomRightCorner), Tag = "grip",
        };
        AutomationProperties.SetName(grip, T("Изменить размер", "Өлчөмүн өзгөртүү", "Resize", "Yeniden boyutlandır", "O'lchamini o'zgartirish"));
        Canvas.SetLeft(grip, rect.Width - 6);
        Canvas.SetTop(grip, rect.Height - 6);
        WireResize(grip, el);
        host.Children.Add(grip);
        return host;
    }

    /// <summary>Сетка точек поверх этикетки (только при включённой привязке).</summary>
    private sealed class GridDots : Control
    {
        private readonly double _w, _h, _step, _scale;

        public GridDots(double w, double h, double step, double scale)
        {
            _w = w; _h = h; _step = step; _scale = scale;
            IsHitTestVisible = false;
            Width = w * scale;
            Height = h * scale;
        }

        public override void Render(DrawingContext context)
        {
            var brush = new SolidColorBrush(Color.FromArgb(70, 80, 80, 80));
            var stepPx = _step * _scale;
            if (stepPx < 4) // слишком густо — не рисуем каждую
                stepPx *= Math.Ceiling(4 / stepPx);
            for (double x = 0; x <= _w * _scale; x += stepPx)
                for (double y = 0; y <= _h * _scale; y += stepPx)
                    context.DrawEllipse(brush, null, new Point(x, y), 0.8, 0.8);
        }
    }

    // ------------------------------------------------------------------ предпросмотр

    private void QueuePreview()
    {
        if (_previewQueued)
            return;
        _previewQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _previewQueued = false;
            RenderPreview();
        }, DispatcherPriority.Background);
    }

    /// <summary>Шаблон с данными товара для проверки — то, что уйдёт в рисование.</summary>
    public LabelTemplate PreviewTemplate()
    {
        RefreshSampleFromProduct();
        var t = _t.Clone();
        t.Item = _sampleData;
        return t;
    }

    private void RenderPreview()
    {
        try
        {
            var t = PreviewTemplate();
            var next = LabelSceneRenderer.LabelPreview(_sampleName, _sampleCode, _samplePrice, t, _sampleSku, _sampleUnit,
                UserPreferences.Instance.StoreName, _t.Variant is { Enabled: true } ? "44, " + T("Красный", "Кызыл", "Red", "Kırmızı", "Qizil") : null);
            _previewImage.Source = next;
            _previewBitmap?.Dispose();
            _previewBitmap = next;
            var warn = BarcodeLabelService.DescribeBarcodeProblem(_sampleCode, _t);
            if (BarcodeLabelService.DescribeThinBarsLimit(_sampleCode, _t) is { } thin) // 2026-11-27: «Толщина штриха» 1 держит штрихкод узким
                warn = string.IsNullOrEmpty(warn) ? thin : warn + "\n" + thin;
            // 2026-11-27: рамка включена, а элемент заходит на неё — рамка заденет текст или штрихкод.
            var onFrame = AllElements().Where(e => e.Layout.Enabled && !(e.Extra is { Kind: LabelExtraKind.Line }) && LabelDesignerMath.TouchesFrame(e.Layout, _t)).Select(e => e.Title).Take(3).ToList();
            if (onFrame.Count > 0)
                warn = (string.IsNullOrEmpty(warn) ? "" : warn + "\n") + T("Рамка задевает: ", "Алкак тийип турат: ", "The frame touches: ", "Çerçeve değiyor: ", "Ramka tegib turibdi: ") + string.Join(", ", onFrame)
                    + T(" — отодвиньте от края.", " — четтен алыстатыңыз.", " — move it away from the edge.", " — kenardan uzaklaştırın.", " — chetdan uzoqlashtiring.");
            var overlaps = FindOverlaps();
            if (overlaps.Count > 0)
                warn = (string.IsNullOrEmpty(warn) ? "" : warn + "\n") + T("Элементы наезжают друг на друга: ", "Элементтер бири-бирине тийип турат: ", "Elements overlap: ", "Öğeler üst üste biniyor: ", "Elementlar bir-birining ustiga chiqqan: ") + string.Join("; ", overlaps);
            _warning.Text = warn ?? "";
            _warning.IsVisible = !string.IsNullOrEmpty(warn);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label designer preview failed: {ex}", "WARNING");
        }
    }

    private List<string> FindOverlaps()
    {
        var els = AllElements().Where(e => e.Layout.Enabled && !(e.Extra is { Kind: LabelExtraKind.Line })).ToList();
        var res = new List<string>();
        for (var i = 0; i < els.Count; i++)
            for (var j = i + 1; j < els.Count; j++)
                if (LabelDesignerMath.Overlaps(els[i].Layout, els[j].Layout))
                    res.Add(els[i].Title + " × " + els[j].Title);
        return res.Take(3).ToList();
    }

    // ------------------------------------------------------------------ правки

    /// <summary>Запомнить состояние для «Отменить» — перед каждым изменением.</summary>
    private void Push()
    {
        var json = Serialize(_t);
        if (_undo.Count > 0 && _undo[^1] == json)
            return;
        _undo.Add(json);
        if (_undo.Count > 60)
            _undo.RemoveAt(0);
        _undoButton.IsEnabled = true;
    }

    private void DoUndo()
    {
        if (_undo.Count == 0)
            return;
        var json = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _t = JsonSerializer.Deserialize<LabelTemplate>(json) ?? _t;
        EnsureSane(_t);
        SyncAll();
    }

    /// <summary>Шаблон изменён: холст, предпросмотр, кнопки.</summary>
    private void Changed(bool props = false)
    {
        RebuildCanvas();
        if (props)
            BuildProps();
        QueuePreview();
        UpdateSelectionUi();
    }

    private void ApplySize(double w, double h)
    {
        w = LabelDesignerMath.ClampSize(w);
        h = LabelDesignerMath.ClampSize(h);
        if (Math.Abs(w - _t.WidthMm) < 0.001 && Math.Abs(h - _t.HeightMm) < 0.001)
            return;
        Push();
        // Раскладка растягивается пропорционально новому размеру (как в прежнем редакторе); свои элементы — тоже
        // (2026-11-27: их растягивает сам ScaleToSize — одно правило для редактора, «Печати» и мастера принтера).
        LabelAutoLayout.ScaleToSize(_t, w, h);
        _building = true;
        try
        {
            _wBox.Value = (decimal)w;
            _hBox.Value = (decimal)h;
            _sizeCombo.SelectedIndex = SizeIndex();
        }
        finally { _building = false; }
        RefitScale();
        Changed(true);
    }

    private int SizeIndex()
    {
        for (var i = 0; i < LabelDesignerMath.Sizes.Length; i++)
            if (Math.Abs(LabelDesignerMath.Sizes[i].W - _t.WidthMm) < 0.05 && Math.Abs(LabelDesignerMath.Sizes[i].H - _t.HeightMm) < 0.05)
                return i;
        return LabelDesignerMath.Sizes.Length;
    }

    private void AddExtra(LabelExtraKind kind)
    {
        if (_t.Extras.Count >= 20)
        {
            SetStatus(T("Не больше 20 своих элементов на этикетке.", "Этикеткада өз элементтери 20дан ашпасын.", "At most 20 custom elements per label.", "Etikette en fazla 20 özel öğe.", "Yorliqda 20 tadan ko'p o'z elementi bo'lmasin."));
            return;
        }
        Push();
        var e = LabelExtras.Create(kind, _t);
        _t.Extras.Add(e);
        _selectedKey = "x:" + e.Id;
        RebuildChips();
        Changed(true);
    }

    private void SelectKey(string key)
    {
        if (_selectedKey == key)
            return;
        _selectedKey = key;
        RebuildCanvas();
        BuildProps();
        RebuildChips();
    }

    /// <summary>Переключатели элементов над холстом: галочка — элемент на этикетке; нажатие выбирает его.</summary>
    private void RebuildChips()
    {
        _chips.Children.Clear();
        foreach (var el in AllElements())
        {
            var chip = new ToggleButton
            {
                Content = el.Title, IsChecked = el.Layout.Enabled, MinHeight = 32, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 6, 4),
                CornerRadius = new CornerRadius(16), FontSize = 12.5, Focusable = false, Tag = el.Key,
            };
            chip.Classes.Add("sec-outline");
            if (el.Key == _selectedKey)
                chip.FontWeight = FontWeight.Bold;
            var key = el.Key;
            chip.Click += (_, _) =>
            {
                var cur = AllElements().FirstOrDefault(x => x.Key == key);
                if (cur is null)
                    return;
                // 2026-11-27 («Ошибка в редактировании штрих кода ценников»): нажатие по «Штрих-код», чтобы его править, выключало штрихкод — он пропадал
                // с этикетки. Элемент на этикетке, но не выбран — нажатие только выбирает его; выключает — повторное нажатие по уже выбранному.
                if (cur.Layout.Enabled && _selectedKey != key)
                {
                    chip.IsChecked = true;
                    SelectKey(key);
                    return;
                }
                Push();
                cur.Layout.Enabled = chip.IsChecked == true;
                if (cur.Layout.Enabled)
                    _selectedKey = key;
                else if (_selectedKey == key)
                    _selectedKey = AllElements().FirstOrDefault(x => x.Layout.Enabled)?.Key ?? "price";
                RebuildChips();
                Changed(true);
            };
            _chips.Children.Add(chip);
        }
    }

    private void UpdateSelectionUi()
    {
        _undoButton.IsEnabled = _undo.Count > 0;
        if (_saveButton != null)
            _saveButton.Content = IsDirty ? T("Сохранить*", "Сактоо*", "Save*", "Kaydet*", "Saqlash*") : T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");
    }

    // ------------------------------------------------------------------ клавиатура

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox or NumericUpDown or ComboBox or AutoCompleteBox)
            return;
        var sel = Selected();
        if (sel is null)
            return;
        var step = _grid > 0 ? _grid : 0.5;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            step *= 4;
        double dx = 0, dy = 0;
        switch (e.Key)
        {
            case Key.Left: dx = -step; break;
            case Key.Right: dx = step; break;
            case Key.Up: dy = -step; break;
            case Key.Down: dy = step; break;
            case Key.Delete:
                RemoveSelected();
                e.Handled = true;
                return;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                DoUndo();
                e.Handled = true;
                return;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                DuplicateSelected();
                e.Handled = true;
                return;
            default:
                return;
        }
        Push();
        LabelDesignerMath.Nudge(sel.Layout, _t, dx, dy, _grid);
        Changed(true);
        e.Handled = true;
    }

    private void RemoveSelected()
    {
        var sel = Selected();
        if (sel is null)
            return;
        Push();
        if (sel.Extra is { } x)
            _t.Extras.Remove(x);
        else
            sel.Layout.Enabled = false;
        _selectedKey = AllElements().FirstOrDefault(e => e.Layout.Enabled)?.Key ?? "price";
        RebuildChips();
        Changed(true);
    }

    private void DuplicateSelected()
    {
        if (Selected() is not { Extra: { } x } || _t.Extras.Count >= 20)
            return;
        Push();
        var copy = x.CloneExtra();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.XMm += 2;
        copy.YMm += 2;
        LabelDesignerMath.ClampInside(copy, _t);
        _t.Extras.Add(copy);
        _selectedKey = "x:" + copy.Id;
        RebuildChips();
        Changed(true);
    }

    // ------------------------------------------------------------------ мелочи построения

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));

    private NumericUpDown Num(double min, double max, double step, int decimals = 1, string? format = null) => new()
    {
        Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, MinWidth = 92, MinHeight = 36,
        FormatString = format ?? (decimals == 0 ? "0" : "0.##"), ParsingNumberStyle = System.Globalization.NumberStyles.Float,
    };

    private Button Tool(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs>? click)
    {
        var b = new Button
        {
            Content = text, MinHeight = 36, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Classes.Add("sec-outline");
        if (click != null)
            b.Click += click;
        return b;
    }

    private StackPanel Cap(string caption, Control control)
    {
        var p = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 10, 4) };
        if (caption.Length > 0)
        {
            var l = new TextBlock { Text = caption, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
            Use(l, TextBlock.ForegroundProperty, "BrushTextSoft");
            p.Children.Add(l);
        }
        else
            p.Children.Add(new TextBlock { Text = " ", FontSize = 11.5 });
        p.Children.Add(control);
        return p;
    }

    private void SetStatus(string text) => _status.Text = text;
}
