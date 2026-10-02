using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Converters;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-01, владелец: «магазин одежды — при выборе нужно выбрать размер, цвет, возможно изменение
/// цены, если на какой-то размер или цвет есть скидка/акция». Окно выбора варианта товара NurCRM:
/// размеры (по порядку XS…XXL, числа — по возрастанию), цвета выбранного размера, цена варианта
/// (ниже обычной — зачёркнутая обычная и «Скидка −N%»), остаток и количество.
/// Result — выбранный вариант, Quantity — сколько добавить; null — отмена.
///
/// 2026-10-02, владелец: «сделай редизайн более красивым». Шапка с фото и крестиком, размеры —
/// плитки с остатком под размером, цвета — с кружком цвета, цена — на отдельной карточке со скидкой,
/// количество — единым переключателем, кнопки в цветах темы (раньше «Добавить» была синей).
/// </summary>
public sealed class VariantPickerWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] SizeOrder = { "XXXS", "XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL", "4XL", "5XL" };

    /// <summary>Кружок цвета по названию (как его пишут продавцы на сайте). Неизвестный цвет — без кружка.</summary>
    private static readonly (string Key, string Hex)[] ColorSwatches =
    {
        ("тёмно-син", "#1E3A8A"), ("темно-син", "#1E3A8A"), ("светло-сер", "#D1D5DB"), ("тёмно-сер", "#4B5563"), ("темно-сер", "#4B5563"),
        ("бел", "#FFFFFF"), ("чёрн", "#1F2937"), ("черн", "#1F2937"), ("сер", "#9CA3AF"), ("син", "#2563EB"), ("голуб", "#60A5FA"),
        ("красн", "#DC2626"), ("бордов", "#7F1D1D"), ("зел", "#16A34A"), ("хаки", "#6B7A3A"), ("олив", "#708238"), ("жёлт", "#FACC15"),
        ("желт", "#FACC15"), ("оранж", "#F97316"), ("розов", "#EC4899"), ("фиолет", "#7C3AED"), ("сирен", "#A78BFA"), ("коричн", "#8B5E3C"),
        ("беж", "#D6C3A3"), ("молоч", "#F5EFE0"), ("крем", "#F3E5C8"), ("золот", "#D4AF37"), ("серебр", "#C0C0C0"),
        ("white", "#FFFFFF"), ("black", "#1F2937"), ("grey", "#9CA3AF"), ("gray", "#9CA3AF"), ("navy", "#1E3A8A"), ("blue", "#2563EB"),
        ("red", "#DC2626"), ("green", "#16A34A"), ("beige", "#D6C3A3"), ("pink", "#EC4899"), ("brown", "#8B5E3C"),
    };

    private readonly CatalogProductTileVm _product;
    private readonly List<ProductVariantDto> _variants;
    private readonly double _basePrice;
    private readonly WrapPanel _sizes = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _colors = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _colorsHead = new();
    private readonly TextBlock _sizeValue = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _colorValue = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _price = new() { FontSize = 30, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _oldPrice = new() { FontSize = 16, TextDecorations = TextDecorations.Strikethrough, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 4, 0, 0) };
    private readonly Border _badge = new() { CornerRadius = new CornerRadius(20), Padding = new Thickness(10, 4), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _stockDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stock = new() { FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _qtyText = new() { FontSize = 18, FontWeight = FontWeight.Bold, MinWidth = 48, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _add = new() { Padding = new Thickness(22, 0), Height = 50, MinWidth = 220, CornerRadius = new CornerRadius(12), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 15, FontWeight = FontWeight.Bold };

    private string? _size;
    private string? _color;
    private double _qty = 1;

    public ProductVariantDto? Result { get; private set; }
    public double Quantity => _qty;

    /// <summary>Подпись кнопки подтверждения вместо «Добавить в чек · сумма» (прокат: «Выбрать»).</summary>
    private readonly string? _confirmText;

    public VariantPickerWindow(CatalogProductTileVm product, IEnumerable<ProductVariantDto> variants, string? confirmText = null)
    {
        _confirmText = confirmText;
        _product = product;
        _variants = variants.Where(v => v.IsActive).ToList();
        _basePrice = LocalCartService.ParsePrice(product.PriceLine);
        Title = product.Title;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.Enter && _add.IsEnabled)
                Confirm();
        };

        AddChipStyles();
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24), Spacing = 18 };
        root.Children.Add(BuildHeader(product));

        var sizes = Sizes();
        if (sizes.Count > 0)
        {
            var sizeBlock = new StackPanel { Spacing = 10 };
            sizeBlock.Children.Add(HeadRow(Tr.T("Размер", "Өлчөмү", "Size", "Beden", "O'lcham"), _sizeValue));
            sizeBlock.Children.Add(_sizes);
            root.Children.Add(sizeBlock);
        }

        var colorBlock = new StackPanel { Spacing = 10 };
        _colorsHead.Text = Tr.T("Цвет", "Түсү", "Color", "Renk", "Rang");
        colorBlock.Children.Add(HeadRow(_colorsHead, _colorValue));
        colorBlock.Children.Add(_colors);
        root.Children.Add(colorBlock);
        _colorsHead.Tag = colorBlock;   // скрываем весь блок, если цветов нет

        root.Children.Add(BuildPriceCard());
        root.Children.Add(BuildBottom());

        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Child = root, ClipToBounds = true };
        Use(frame, Border.BorderBrushProperty, "BrushBorderStrong");
        Use(frame, Border.BackgroundProperty, "BrushDialogPanel");
        Content = frame;

        // Сразу выбираем первый размер, где что-то есть, и первый его цвет.
        _size = sizes.FirstOrDefault(s => _variants.Any(v => v.Size == s && v.Quantity > 0)) ?? sizes.FirstOrDefault();
        _color = ColorsFor(_size).FirstOrDefault(c => Find(_size, c)?.Quantity > 0) ?? ColorsFor(_size).FirstOrDefault();
        Refresh();
    }

    /// <summary>2026-10-02: стиль Fluent при наведении красил выбранную плитку размера/цвета серым и
    /// снимал жёлтую рамку — кассир, не убравший мышь, не видел, что выбрано. Наведение теперь
    /// мягко подсвечивает невыбранные, а выбранная остаётся в цветах темы.</summary>
    private void AddChipStyles()
    {
        IBrush? Res(string key) =>
            Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b ? b : null;
        Style Hover(bool selected) => new(x => (selected ? x.OfType<Button>().Class("vchip").Class("sel") : x.OfType<Button>().Class("vchip"))
            .Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Res(selected ? "BrushAccentSoft" : "BrushPanelSoft")),
                new Setter(ContentPresenter.BorderBrushProperty, Res(selected ? "BrushAccentStrong" : "BrushBorderStrong")),
            },
        };
        Styles.Add(Hover(false));
        Styles.Add(Hover(true));
    }

    private Control BuildHeader(CatalogProductTileVm product)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        // Фото товара, если есть, — как на плитке каталога.
        if (ShowProductPhotoConverter.Instance.Convert(product.ProductImagePath, typeof(bool), null, CultureInfo.CurrentCulture) is true
            && AssetPathToBitmapConverter.Instance.Convert(product.ProductImagePath, typeof(IImage), "thumb", CultureInfo.CurrentCulture) is IImage img)
        {
            var photo = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(12), ClipToBounds = true, Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Image { Source = img, Stretch = Stretch.UniformToFill },
            };
            grid.Children.Add(photo);
        }

        var texts = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = product.Title, FontSize = 21, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        texts.Children.Add(title);
        var meta = string.Join(" · ", new[] { product.Brand, product.Category }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (meta.Length > 0)
        {
            var sub = new TextBlock { Text = meta, FontSize = 13 };
            Use(sub, TextBlock.ForegroundProperty, "BrushTextSoft");
            texts.Children.Add(sub);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var close = new Button
        {
            Width = 36, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(18), Focusable = false,
            VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(12, 0, 0, 0),
            Content = new TextBlock { Text = "✕", FontSize = 16 },
        };
        ToolTip.SetTip(close, Tr.T("Закрыть (Esc)", "Жабуу (Esc)", "Close (Esc)", "Kapat (Esc)", "Yopish (Esc)"));
        Use((TextBlock)close.Content, TextBlock.ForegroundProperty, "BrushTextSoft");
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        return grid;
    }

    private Control BuildPriceCard()
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14) };
        Use(card, Border.BackgroundProperty, "BrushPanelSoft");

        var stack = new StackPanel { Spacing = 6 };
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal };
        Use(_price, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        Use(_oldPrice, TextBlock.ForegroundProperty, "BrushTextMuted");
        Use(_badge, Border.BackgroundProperty, "BrushDanger");
        priceRow.Children.Add(_price);
        priceRow.Children.Add(_oldPrice);
        priceRow.Children.Add(_badge);
        stack.Children.Add(priceRow);

        var stockRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Use(_stock, TextBlock.ForegroundProperty, "BrushTextSoft");
        stockRow.Children.Add(_stockDot);
        stockRow.Children.Add(_stock);
        stack.Children.Add(stockRow);

        card.Child = stack;
        return card;
    }

    private Control BuildBottom()
    {
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

        // Количество: «− 1 +» в одной рамке.
        var stepper = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Height = 50, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        Use(stepper, Border.BorderBrushProperty, "BrushBorder");
        Use(stepper, Border.BackgroundProperty, "BrushInput");
        var qtyPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var minus = StepButton("−");
        var plus = StepButton("+");
        minus.Click += (_, _) => { _qty = Math.Max(1, _qty - 1); Refresh(); };
        plus.Click += (_, _) => { _qty += 1; Refresh(); };
        Use(_qtyText, TextBlock.ForegroundProperty, "BrushText");
        qtyPanel.Children.Add(minus);
        qtyPanel.Children.Add(_qtyText);
        qtyPanel.Children.Add(plus);
        stepper.Child = qtyPanel;
        bottom.Children.Add(stepper);

        var cancel = new Button
        {
            Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            Height = 50, Padding = new Thickness(20, 0), MinWidth = 110, CornerRadius = new CornerRadius(12), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 12, 0), FontSize = 14,
        };
        Use(cancel, Button.BackgroundProperty, "BrushDialogPanel");
        Use(cancel, Button.BorderBrushProperty, "BrushBorder");
        Use(cancel, Button.ForegroundProperty, "BrushText");
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 2);
        bottom.Children.Add(cancel);

        Use(_add, Button.BackgroundProperty, "BrushAccent");
        Use(_add, Button.ForegroundProperty, "BrushAccentForeground");
        _add.Focusable = false;
        _add.Click += (_, _) => Confirm();
        Grid.SetColumn(_add, 3);
        bottom.Children.Add(_add);
        return bottom;
    }

    private Button StepButton(string text)
    {
        var b = new Button
        {
            Content = text, Width = 42, Height = 40, FontSize = 20, Padding = new Thickness(0), CornerRadius = new CornerRadius(9), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        };
        Use(b, Button.ForegroundProperty, "BrushText");
        return b;
    }

    private List<string> Sizes() =>
        _variants.Select(v => v.Size.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(SizeRank).ThenBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    private static double SizeRank(string s)
    {
        var i = Array.FindIndex(SizeOrder, x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            return i;
        return double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? 100 + n : 10_000;
    }

    private List<string> ColorsFor(string? size) =>
        _variants.Where(v => size == null || string.Equals(v.Size.Trim(), size, StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Color.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private ProductVariantDto? Find(string? size, string? color) =>
        _variants.FirstOrDefault(v =>
            (size == null || string.Equals(v.Size.Trim(), size, StringComparison.OrdinalIgnoreCase))
            && (color == null || string.Equals(v.Color.Trim(), color, StringComparison.OrdinalIgnoreCase)));

    private ProductVariantDto? Selected
    {
        get
        {
            var colors = ColorsFor(_size);
            return Find(_size, colors.Count == 0 ? null : _color);
        }
    }

    private double SizeStock(string size) =>
        _variants.Where(v => string.Equals(v.Size.Trim(), size, StringComparison.OrdinalIgnoreCase)).Sum(v => Math.Max(0, v.Quantity));

    private void Refresh()
    {
        _sizes.Children.Clear();
        foreach (var s in Sizes())
        {
            var stock = SizeStock(s);
            _sizes.Children.Add(SizeChip(s, stock, s == _size, () =>
            {
                _size = s;
                var colors = ColorsFor(_size);
                if (_color == null || !colors.Contains(_color, StringComparer.OrdinalIgnoreCase))
                    _color = colors.FirstOrDefault(c => Find(_size, c)?.Quantity > 0) ?? colors.FirstOrDefault();
                Refresh();
            }));
        }

        _colors.Children.Clear();
        var colorList = ColorsFor(_size);
        if (_colorsHead.Tag is Control colorBlock)
            colorBlock.IsVisible = colorList.Count > 0;
        foreach (var c in colorList)
        {
            var v = Find(_size, c);
            _colors.Children.Add(ColorChip(c, string.Equals(c, _color, StringComparison.OrdinalIgnoreCase), v?.Quantity > 0, () => { _color = c; Refresh(); }));
        }

        _sizeValue.Text = _size ?? "";
        _colorValue.Text = _color ?? "";

        var sel = Selected;
        var price = sel?.Price ?? _basePrice;
        _price.Text = $"{price.ToString("N2", Ru)} " + Tr.T("сом", "сом", "som", "som", "so'm");
        var discount = _basePrice > 0 && price < _basePrice - 0.005;
        _oldPrice.IsVisible = _badge.IsVisible = discount;
        if (discount)
        {
            _oldPrice.Text = _basePrice.ToString("N2", Ru);
            var pct = (int)Math.Round((1 - price / _basePrice) * 100);
            _badge.Child = new TextBlock
            {
                Text = Tr.T($"Скидка −{pct}%", $"Арзандатуу −{pct}%", $"Sale −{pct}%", $"İndirim −{pct}%", $"Chegirma −{pct}%"),
                FontSize = 12.5,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
            };
        }

        var enough = sel != null && sel.Quantity >= _qty;
        Use(_stockDot, Shape.FillProperty, sel == null ? "BrushTextMuted" : enough ? "BrushSuccess" : sel.Quantity > 0 ? "BrushWarning" : "BrushDanger");
        _stock.Text = sel == null
            ? Tr.T("Выберите размер и цвет", "Өлчөмүн жана түсүн тандаңыз", "Choose a size and color", "Beden ve renk seçin", "O'lcham va rangni tanlang")
            : Tr.T($"В наличии: {sel.Quantity:0.###} шт.", $"Бар: {sel.Quantity:0.###} даана", $"In stock: {sel.Quantity:0.###} pcs", $"Stokta: {sel.Quantity:0.###} adet", $"Mavjud: {sel.Quantity:0.###} dona")
              + (sel.Quantity < _qty ? Tr.T(" — меньше, чем нужно", " — керектүүдөн аз", " — less than needed", " — gerekenden az", " — keragidan kam") : "");
        _qtyText.Text = _qty.ToString("0", CultureInfo.InvariantCulture);
        _add.IsEnabled = sel != null;
        _add.Content = _confirmText ?? Tr.T("Добавить в чек", "Чекке кошуу", "Add to receipt", "Fişe ekle", "Chekka qo'shish")
                       + $"  ·  {(price * _qty).ToString("N2", Ru)}";
    }

    /// <summary>Плитка размера: крупно размер, под ним остаток («8 шт» / «нет»).</summary>
    private Button SizeChip(string size, double stock, bool selected, Action onClick)
    {
        var content = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        var big = new TextBlock { Text = size, FontSize = 17, FontWeight = selected ? FontWeight.Bold : FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var small = new TextBlock
        {
            Text = stock > 0 ? Tr.T($"{stock:0} шт", $"{stock:0} даана", $"{stock:0} pcs", $"{stock:0} adet", $"{stock:0} dona") : Tr.T("нет", "жок", "none", "yok", "yo'q"),
            FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
        };
        Use(big, TextBlock.ForegroundProperty, "BrushText");
        Use(small, TextBlock.ForegroundProperty, stock > 0 ? "BrushTextSoft" : "BrushDanger");
        content.Children.Add(big);
        content.Children.Add(small);
        return ChipButton(content, selected, stock > 0, onClick, minWidth: 68, padding: new Thickness(14, 8));
    }

    /// <summary>Плитка цвета: кружок цвета и название.</summary>
    private Button ColorChip(string color, bool selected, bool available, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (SwatchFor(color) is { } fill)
        {
            var dot = new Ellipse { Width = 18, Height = 18, Fill = fill, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
            Use(dot, Shape.StrokeProperty, "BrushBorderStrong");
            content.Children.Add(dot);
        }
        var text = new TextBlock { Text = available ? color : color + " · " + Tr.T("нет", "жок", "none", "yok", "yo'q"), FontSize = 15, FontWeight = selected ? FontWeight.Bold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
        Use(text, TextBlock.ForegroundProperty, "BrushText");
        content.Children.Add(text);
        return ChipButton(content, selected, available, onClick, minWidth: 0, padding: new Thickness(14, 10));
    }

    private Button ChipButton(Control content, bool selected, bool available, Action onClick, double minWidth, Thickness padding)
    {
        var b = new Button
        {
            Content = content,
            Padding = padding,
            Margin = new Thickness(0, 0, 10, 10),
            MinWidth = minWidth,
            MinHeight = 48,
            CornerRadius = new CornerRadius(12),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Opacity = available ? 1 : 0.5,
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        b.Classes.Add("vchip");
        if (selected)
            b.Classes.Add("sel");
        Use(b, Button.BorderBrushProperty, selected ? "BrushAccentStrong" : "BrushBorder");
        Use(b, Button.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushInput");
        b.Click += (_, _) => onClick();
        return b;
    }

    private static IBrush? SwatchFor(string color)
    {
        var c = color.Trim().ToLowerInvariant();
        foreach (var (key, hex) in ColorSwatches)
        {
            if (c.Contains(key, StringComparison.Ordinal))
                return new SolidColorBrush(Color.Parse(hex));
        }
        return null;
    }

    private Grid HeadRow(string text, TextBlock value) => HeadRow(new TextBlock { Text = text }, value);

    private Grid HeadRow(TextBlock head, TextBlock value)
    {
        head.FontSize = 13;
        head.FontWeight = FontWeight.SemiBold;
        Use(head, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(value, TextBlock.ForegroundProperty, "BrushText");
        value.Margin = new Thickness(8, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(head);
        row.Children.Add(value);
        var g = new Grid();
        g.Children.Add(row);
        return g;
    }

    private void Confirm()
    {
        Result = Selected;
        if (Result != null)
            Close(true);
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
