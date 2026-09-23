using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class LabelTemplateEditorDialog : Window
{
    private const double PxPerMm = 8.0;

    /// <summary>Общеупотребимые системные шрифты — все точно есть в Windows, не требуют
    /// проверки на наличие (GDI+ и так тихо подставит запасной, если имя не найдено).</summary>
    private static readonly string[] AvailableFontFamilies =
        { "Arial", "Consolas", "Segoe UI", "Calibri", "Times New Roman", "Verdana", "Tahoma" };

    private static readonly (double WidthMm, double HeightMm, string Label)[] SizePresets =
    {
        (39, 29, "39×29 мм"),
        (58, 40, "58×40 мм"),
        (40, 30, "40×30 мм (по умолчанию)"),
    };

    private const string CustomSizeLabel = "Свой размер";

    private static readonly string[] CurrencyPresets = { "сом", "$", "₽", "so'm", "₸", "₺" };

    /// <summary>Какой из 6 элементов этикетки сейчас выбран в левой панели инструментов —
    /// определяет, что показывает панель свойств справа (2026-09-06, редизайн по макету).</summary>
    private enum LabelElementKind { Barcode, ProductName, Price, Sku, Unit, StoreName }

    private readonly LabelTemplate _template;
    private readonly string _sampleProductName;
    private readonly string _sampleBarcode;
    private readonly string? _samplePriceText;
    private readonly Action<LabelTemplate> _saveAction;

    private readonly Dictionary<LabelElementKind, Border> _canvasBoxes = new();
    private readonly Dictionary<LabelElementKind, Button> _toolButtons = new();

    private LabelElementKind _selectedKind = LabelElementKind.ProductName;
    private double _zoom = 1.0;

    private Border? _dragTarget;
    private LabelElementLayout? _dragLayout;
    private Point _dragStartPointerCanvas;
    private double _dragStartXMm;
    private double _dragStartYMm;
    private double _dragStartWidthMm;
    private double _dragStartHeightMm;
    private bool _isResizing;

    private double Scale => PxPerMm * _zoom;

    public bool Saved { get; private set; }

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public LabelTemplateEditorDialog()
        : this(LabelTemplate.CreateDefault(), "Образец товара", "4870145004807", "285 сом")
    {
    }

    public LabelTemplateEditorDialog(
        LabelTemplate template, string sampleProductName, string sampleBarcode, string? samplePriceText,
        Action<LabelTemplate>? saveAction = null, string? titleOverride = null)
    {
        _template = template.Clone();
        _sampleProductName = sampleProductName;
        _sampleBarcode = string.IsNullOrWhiteSpace(sampleBarcode) ? "4870145004807" : sampleBarcode;
        _samplePriceText = samplePriceText;
        _saveAction = saveAction ?? LabelTemplateStore.Save;

        InitializeComponent();
        this.FitToScreen();

        if (!string.IsNullOrWhiteSpace(titleOverride))
            Title = titleOverride;

        LabelWidthBox.Value = (decimal)_template.WidthMm;
        LabelHeightBox.Value = (decimal)_template.HeightMm;
        LabelWidthBox.ValueChanged += LabelSize_Changed;
        LabelHeightBox.ValueChanged += LabelSize_Changed;

        SizePresetCombo.ItemsSource = SizePresets.Select(p => p.Label).Append(CustomSizeLabel).ToArray();
        SizePresetCombo.SelectedItem = FindMatchingPresetLabel();
        SizePresetCombo.SelectionChanged += SizePresetCombo_SelectionChanged;

        BuildToolButtons();
        BuildCanvas();
        SelectElement(_selectedKind);
        RefreshPreview();
    }

    private string FindMatchingPresetLabel()
    {
        foreach (var preset in SizePresets)
        {
            if (Math.Abs(preset.WidthMm - _template.WidthMm) < 0.01 && Math.Abs(preset.HeightMm - _template.HeightMm) < 0.01)
                return preset.Label;
        }
        return CustomSizeLabel;
    }

    private void SizePresetCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SizePresetCombo.SelectedItem is not string label)
            return;
        var preset = SizePresets.FirstOrDefault(p => p.Label == label);
        if (preset.Label is null)
            return;

        LabelWidthBox.Value = (decimal)preset.WidthMm;
        LabelHeightBox.Value = (decimal)preset.HeightMm;
    }

    // ---------- Левая панель «Инструменты» ----------

    private void BuildToolButtons()
    {
        // 2026-09-07: подписи инструментов теперь на 5 языках (Tr.T), плюс маленькая иконка
        // слева от текста — раньше кнопки были голым текстом без визуальной опоры.
        var tools = new (LabelElementKind Kind, string Icon, string Label)[]
        {
            (LabelElementKind.ProductName, "\U0001F4DD",
                Tr.T("Название", "Аталышы", "Name", "Ad", "Nomi")),
            (LabelElementKind.Barcode, "▦",
                Tr.T("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod")),
            (LabelElementKind.Price, "\U0001F4B0",
                Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx")),
            (LabelElementKind.Unit, "⚖",
                Tr.T("Ед. изм.", "Өлч. бирд.", "Unit", "Birim", "O'lchov")),
            (LabelElementKind.StoreName, "\U0001F3EA",
                Tr.T("Магазин", "Дүкөн", "Store", "Mağaza", "Do'kon")),
            (LabelElementKind.Sku, "\U0001F522",
                Tr.T("Артикул", "Артикул", "SKU", "Ürün kodu", "Artikul")),
        };

        foreach (var (kind, icon, label) in tools)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new TextBlock { Text = icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Classes = { "tool-item" } };
            button.Click += (_, _) => SelectElement(kind);
            _toolButtons[kind] = button;
            ToolsPanel.Children.Add(button);
        }
    }

    private void SelectElement(LabelElementKind kind)
    {
        _selectedKind = kind;

        foreach (var (kind2, button) in _toolButtons)
            button.Classes.Set("selected", kind2 == kind);
        foreach (var (kind2, box) in _canvasBoxes)
            box.BorderBrush = kind2 == kind ? Brush.Parse("#2563EB") : Brush.Parse("#64748B");
        foreach (var (kind2, box) in _canvasBoxes)
            box.BorderThickness = new Thickness(kind2 == kind ? 2 : 1);

        BuildPropertiesPanel();
    }

    // ---------- Холст ----------

    private void BuildCanvas()
    {
        EditorCanvas.Children.Clear();
        _canvasBoxes.Clear();
        EditorCanvas.Width = _template.WidthMm * Scale;
        EditorCanvas.Height = _template.HeightMm * Scale;

        if (_template.Barcode.Enabled)
            AddElementBox(LabelElementKind.Barcode, _template.Barcode, "Штрих-код", "#FDE68A");
        AddElementBox(LabelElementKind.ProductName, _template.ProductName, "Название", "#BFDBFE");
        AddElementBox(LabelElementKind.Price, _template.Price, "Цена", "#BBF7D0");

        if (_template.Sku.Enabled)
            AddElementBox(LabelElementKind.Sku, _template.Sku, "Артикул", "#FBCFE8");
        if (_template.Unit.Enabled)
            AddElementBox(LabelElementKind.Unit, _template.Unit, "Ед. изм.", "#DDD6FE");
        if (_template.StoreName.Enabled)
            AddElementBox(LabelElementKind.StoreName, _template.StoreName, "Магазин", "#FED7AA");

        foreach (var (kind2, box) in _canvasBoxes)
        {
            box.BorderBrush = kind2 == _selectedKind ? Brush.Parse("#2563EB") : Brush.Parse("#64748B");
            box.BorderThickness = new Thickness(kind2 == _selectedKind ? 2 : 1);
        }
    }

    private void AddElementBox(LabelElementKind kind, LabelElementLayout layout, string label, string colorHex)
    {
        var box = CreateElementBox(layout, label, colorHex, kind);
        _canvasBoxes[kind] = box;
        EditorCanvas.Children.Add(box);
    }

    private Border CreateElementBox(LabelElementLayout layout, string label, string colorHex, LabelElementKind kind)
    {
        var grip = new Border
        {
            Width = 14,
            Height = 14,
            Background = Brush.Parse("#334155"),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -4, -4),
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
        };

        var text = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
        };

        var box = new Border
        {
            Background = Brush.Parse(colorHex),
            BorderBrush = Brush.Parse("#64748B"),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new Grid { Children = { text, grip } },
        };

        Canvas.SetLeft(box, layout.XMm * Scale);
        Canvas.SetTop(box, layout.YMm * Scale);
        box.Width = layout.WidthMm * Scale;
        box.Height = layout.HeightMm * Scale;

        box.PointerPressed += (_, e) =>
        {
            SelectElement(kind);
            if (e.Handled)
                return;
            StartDrag(box, layout, e, resize: false);
        };
        box.PointerMoved += (_, e) => OnPointerMoved(box, layout, e);
        box.PointerReleased += (_, _) => EndDrag();

        grip.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            SelectElement(kind);
            StartDrag(box, layout, e, resize: true);
        };
        grip.PointerMoved += (_, e) => OnPointerMoved(box, layout, e);
        grip.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            EndDrag();
        };

        return box;
    }

    private void StartDrag(Border box, LabelElementLayout layout, PointerPressedEventArgs e, bool resize)
    {
        _dragTarget = box;
        _dragLayout = layout;
        _isResizing = resize;
        _dragStartPointerCanvas = e.GetPosition(EditorCanvas);
        _dragStartXMm = layout.XMm;
        _dragStartYMm = layout.YMm;
        _dragStartWidthMm = layout.WidthMm;
        _dragStartHeightMm = layout.HeightMm;
        e.Pointer.Capture(box);
    }

    private void OnPointerMoved(Border box, LabelElementLayout layout, PointerEventArgs e)
    {
        if (!ReferenceEquals(_dragTarget, box) || !ReferenceEquals(_dragLayout, layout))
            return;
        if (!e.GetCurrentPoint(EditorCanvas).Properties.IsLeftButtonPressed)
            return;

        var pos = e.GetPosition(EditorCanvas);
        var deltaXMm = (pos.X - _dragStartPointerCanvas.X) / Scale;
        var deltaYMm = (pos.Y - _dragStartPointerCanvas.Y) / Scale;

        if (_isResizing)
        {
            var maxWidth = Math.Max(4, _template.WidthMm - layout.XMm);
            var maxHeight = Math.Max(3, _template.HeightMm - layout.YMm);
            layout.WidthMm = Math.Clamp(_dragStartWidthMm + deltaXMm, 4, maxWidth);
            layout.HeightMm = Math.Clamp(_dragStartHeightMm + deltaYMm, 3, maxHeight);
            box.Width = layout.WidthMm * Scale;
            box.Height = layout.HeightMm * Scale;
        }
        else
        {
            layout.XMm = Math.Clamp(_dragStartXMm + deltaXMm, 0, Math.Max(0, _template.WidthMm - layout.WidthMm));
            layout.YMm = Math.Clamp(_dragStartYMm + deltaYMm, 0, Math.Max(0, _template.HeightMm - layout.HeightMm));
            Canvas.SetLeft(box, layout.XMm * Scale);
            Canvas.SetTop(box, layout.YMm * Scale);
        }
    }

    private void EndDrag()
    {
        if (_dragTarget is null)
            return;
        _dragTarget = null;
        _dragLayout = null;
        RefreshPreview();
    }

    private void LabelSize_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        _template.WidthMm = Math.Clamp((double)(LabelWidthBox.Value ?? 40m), 15, 150);
        _template.HeightMm = Math.Clamp((double)(LabelHeightBox.Value ?? 30m), 10, 150);

        ClampLayout(_template.Barcode);
        ClampLayout(_template.ProductName);
        ClampLayout(_template.Price);
        ClampLayout(_template.Sku);
        ClampLayout(_template.Unit);
        ClampLayout(_template.StoreName);

        if (SizePresetCombo.SelectedItem is not string current || current != FindMatchingPresetLabel())
            SizePresetCombo.SelectedItem = FindMatchingPresetLabel();

        BuildCanvas();
        RefreshPreview();
    }

    private void ClampLayout(LabelElementLayout layout)
    {
        layout.WidthMm = Math.Min(layout.WidthMm, _template.WidthMm);
        layout.HeightMm = Math.Min(layout.HeightMm, _template.HeightMm);
        layout.XMm = Math.Clamp(layout.XMm, 0, Math.Max(0, _template.WidthMm - layout.WidthMm));
        layout.YMm = Math.Clamp(layout.YMm, 0, Math.Max(0, _template.HeightMm - layout.HeightMm));
    }

    // ---------- Правая панель «Свойства элемента» ----------

    private void BuildPropertiesPanel()
    {
        PropertiesPanel.Children.Clear();
        PropertiesHeader.Text = _selectedKind switch
        {
            LabelElementKind.Barcode => "Штрих-код",
            LabelElementKind.ProductName => "Название",
            LabelElementKind.Price => "Цена",
            LabelElementKind.Sku => "Артикул",
            LabelElementKind.Unit => "Ед. изм.",
            LabelElementKind.StoreName => "Магазин",
            _ => "",
        };

        switch (_selectedKind)
        {
            case LabelElementKind.Barcode:
                BuildBarcodeProperties();
                break;
            case LabelElementKind.ProductName:
                AddFontControls(_template.ProductName);
                break;
            case LabelElementKind.Price:
                AddFontControls(_template.Price);
                BuildPriceProperties();
                break;
            case LabelElementKind.Sku:
                AddVisibilityCheckbox(_template.Sku);
                AddFontControls(_template.Sku);
                BuildSkuProperties();
                break;
            case LabelElementKind.Unit:
                AddVisibilityCheckbox(_template.Unit);
                AddFontControls(_template.Unit);
                break;
            case LabelElementKind.StoreName:
                AddVisibilityCheckbox(_template.StoreName);
                AddFontControls(_template.StoreName);
                break;
        }
    }

    private void AddVisibilityCheckbox(LabelElementLayout element)
    {
        var check = new CheckBox { Content = "Показывать на этикетке", IsChecked = element.Enabled };
        check.Click += (_, _) =>
        {
            element.Enabled = check.IsChecked == true;
            BuildCanvas();
            RefreshPreview();
        };
        PropertiesPanel.Children.Add(check);
    }

    private void AddFontControls(LabelElementLayout element)
    {
        var fontHeader = new TextBlock { Text = "Шрифт", FontWeight = FontWeight.SemiBold, Foreground = Brushes.Black };
        PropertiesPanel.Children.Add(fontHeader);

        var fontCombo = new ComboBox { ItemsSource = AvailableFontFamilies, HorizontalAlignment = HorizontalAlignment.Stretch };
        fontCombo.SelectedItem = AvailableFontFamilies.Contains(element.FontFamily)
            ? element.FontFamily
            : (AvailableFontFamilies.Contains(_template.FontFamily) ? _template.FontFamily : AvailableFontFamilies[0]);
        fontCombo.SelectionChanged += (_, _) =>
        {
            if (fontCombo.SelectedItem is string family)
            {
                element.FontFamily = family;
                RefreshPreview();
            }
        };
        PropertiesPanel.Children.Add(fontCombo);

        var sizeLabel = new TextBlock { Text = "Размер шрифта, px (0 — автоматически)", FontSize = 11, Foreground = Brush.Parse("#64748B") };
        PropertiesPanel.Children.Add(sizeLabel);

        var sizeBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 96,
            Increment = 1,
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = (decimal)(element.FontSizePx ?? _template.FontSizePx),
        };
        sizeBox.ValueChanged += (_, _) =>
        {
            element.FontSizePx = (double)(sizeBox.Value ?? 0m);
            RefreshPreview();
        };
        PropertiesPanel.Children.Add(sizeBox);
    }

    private void BuildBarcodeProperties()
    {
        AddVisibilityCheckbox(_template.Barcode);

        var formatLabel = new TextBlock { Text = "Тип кодирования", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        PropertiesPanel.Children.Add(formatLabel);

        var formatOptions = new (LabelBarcodeFormat Format, string Label)[]
        {
            (LabelBarcodeFormat.Auto, "Авто (по длине кода)"),
            (LabelBarcodeFormat.Ean13, "EAN-13"),
            (LabelBarcodeFormat.Code128, "Code 128"),
            (LabelBarcodeFormat.QrCode, "QR-код"),
        };
        var formatCombo = new ComboBox { ItemsSource = formatOptions.Select(o => o.Label).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        formatCombo.SelectedIndex = Array.FindIndex(formatOptions, o => o.Format == _template.BarcodeFormat);

        var digitsCheck = new CheckBox
        {
            Content = "Показывать цифры кода",
            IsChecked = _template.BarcodeShowDigits,
            Margin = new Thickness(0, 6, 0, 0),
            IsVisible = _template.BarcodeFormat != LabelBarcodeFormat.QrCode,
        };
        digitsCheck.Click += (_, _) =>
        {
            _template.BarcodeShowDigits = digitsCheck.IsChecked == true;
            RefreshPreview();
        };

        formatCombo.SelectionChanged += (_, _) =>
        {
            if (formatCombo.SelectedIndex < 0)
                return;
            _template.BarcodeFormat = formatOptions[formatCombo.SelectedIndex].Format;
            digitsCheck.IsVisible = _template.BarcodeFormat != LabelBarcodeFormat.QrCode;
            RefreshPreview();
        };
        PropertiesPanel.Children.Add(formatCombo);
        PropertiesPanel.Children.Add(digitsCheck);

        var marginLabel = new TextBlock { Text = "Плотность (поля), модулей", FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        PropertiesPanel.Children.Add(marginLabel);
        var marginBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 10,
            Increment = 1,
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Value = _template.BarcodeMargin,
        };
        marginBox.ValueChanged += (_, _) =>
        {
            _template.BarcodeMargin = (int)(marginBox.Value ?? 2m);
            RefreshPreview();
        };
        PropertiesPanel.Children.Add(marginBox);
    }

    private void BuildPriceProperties()
    {
        var currencyLabel = new TextBlock { Text = "Валюта", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        PropertiesPanel.Children.Add(currencyLabel);

        var currencyBox = new AutoCompleteBox
        {
            ItemsSource = CurrencyPresets,
            Text = _template.PriceCurrencyText,
            FilterMode = AutoCompleteFilterMode.None,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        currencyBox.TextChanged += (_, _) =>
        {
            _template.PriceCurrencyText = currencyBox.Text ?? "";
            RefreshPreview();
        };
        PropertiesPanel.Children.Add(currencyBox);

        var hideDecimalsCheck = new CheckBox
        {
            Content = "Скрывать копейки/тыйын",
            IsChecked = _template.PriceHideDecimals,
            Margin = new Thickness(0, 6, 0, 0),
        };
        hideDecimalsCheck.Click += (_, _) =>
        {
            _template.PriceHideDecimals = hideDecimalsCheck.IsChecked == true;
            RefreshPreview();
        };
        PropertiesPanel.Children.Add(hideDecimalsCheck);
    }

    private void BuildSkuProperties()
    {
        var overrideLabel = new TextBlock { Text = "Заменить текст (пусто — реальный артикул товара)", FontSize = 11, Margin = new Thickness(0, 6, 0, 0), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        PropertiesPanel.Children.Add(overrideLabel);

        var overrideBox = new TextBox { Text = _template.SkuCustomText, HorizontalAlignment = HorizontalAlignment.Stretch };
        overrideBox.TextChanged += (_, _) =>
        {
            _template.SkuCustomText = string.IsNullOrWhiteSpace(overrideBox.Text) ? null : overrideBox.Text;
            RefreshPreview();
        };
        PropertiesPanel.Children.Add(overrideBox);
    }

    // ---------- Предпросмотр, масштаб, тестовая печать ----------

    private void RefreshPreview()
    {
        try
        {
            using var bmp = BarcodeLabelService.GenerateLabelBitmap(
                _sampleProductName, _sampleBarcode, _samplePriceText, _template,
                sku: "SKU-001", unit: "шт", storeName: UserPreferences.Instance.StoreName);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            PreviewImage.Source = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label template preview render failed: {ex}", "WARNING");
        }
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => ChangeZoom(0.1);

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => ChangeZoom(-0.1);

    private void ChangeZoom(double delta)
    {
        _zoom = Math.Clamp(Math.Round(_zoom + delta, 1), 0.5, 2.0);
        ZoomLabel.Text = $"{(int)(_zoom * 100)}%";
        BuildCanvas();
    }

    private async void TestPrintButton_Click(object? sender, RoutedEventArgs e)
    {
        var printerPath = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(printerPath))
        {
            StatusText.Text = "Принтер этикеток не настроен — выберите его в окне печати этикетки.";
            return;
        }

        TestPrintButton.IsEnabled = false;
        StatusText.Text = "Печать…";
        try
        {
            var request = new LabelPrintRequest(
                _sampleProductName, _sampleBarcode, _samplePriceText, Copies: 1,
                PrinterName: printerPath, Template: _template,
                Sku: "SKU-001", Unit: "шт", StoreName: UserPreferences.Instance.StoreName);

            var result = await System.Threading.Tasks.Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
            StatusText.Text = result switch
            {
                LabelPrintResult.Success => "Этикетка отправлена на печать.",
                LabelPrintResult.PrinterNotFound => "Принтер не найден — выберите его в окне печати этикетки.",
                _ => "Ошибка печати. Подробности в журнале приложения.",
            };
        }
        finally
        {
            TestPrintButton.IsEnabled = true;
        }
    }

    // ---------- Сброс, сохранение ----------

    private void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        var defaults = LabelTemplate.CreateDefault();
        _template.WidthMm = defaults.WidthMm;
        _template.HeightMm = defaults.HeightMm;
        _template.FontFamily = defaults.FontFamily;
        _template.FontSizePx = defaults.FontSizePx;
        _template.BarcodeFormat = defaults.BarcodeFormat;
        _template.BarcodeShowDigits = defaults.BarcodeShowDigits;
        _template.BarcodeMargin = defaults.BarcodeMargin;
        _template.PriceCurrencyText = defaults.PriceCurrencyText;
        _template.PriceHideDecimals = defaults.PriceHideDecimals;
        _template.SkuCustomText = defaults.SkuCustomText;
        CopyLayout(defaults.Barcode, _template.Barcode);
        CopyLayout(defaults.ProductName, _template.ProductName);
        CopyLayout(defaults.Price, _template.Price);
        CopyLayout(defaults.Sku, _template.Sku);
        CopyLayout(defaults.Unit, _template.Unit);
        CopyLayout(defaults.StoreName, _template.StoreName);

        LabelWidthBox.Value = (decimal)_template.WidthMm;
        LabelHeightBox.Value = (decimal)_template.HeightMm;
        SizePresetCombo.SelectedItem = FindMatchingPresetLabel();
        _zoom = 1.0;
        ZoomLabel.Text = "100%";

        BuildCanvas();
        SelectElement(_selectedKind);
        RefreshPreview();
    }

    private static void CopyLayout(LabelElementLayout from, LabelElementLayout to)
    {
        to.XMm = from.XMm;
        to.YMm = from.YMm;
        to.WidthMm = from.WidthMm;
        to.HeightMm = from.HeightMm;
        to.Enabled = from.Enabled;
        to.FontFamily = from.FontFamily;
        to.FontSizePx = from.FontSizePx;
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        _saveAction(_template);
        Saved = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();
}
