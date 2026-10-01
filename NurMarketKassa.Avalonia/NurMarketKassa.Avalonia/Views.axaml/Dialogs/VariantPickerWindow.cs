using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
/// </summary>
public sealed class VariantPickerWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string[] SizeOrder = { "XXXS", "XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL", "4XL", "5XL" };

    private readonly CatalogProductTileVm _product;
    private readonly List<ProductVariantDto> _variants;
    private readonly double _basePrice;
    private readonly WrapPanel _sizes = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _colors = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _colorsHead = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _price = new() { FontSize = 26, FontWeight = FontWeight.Bold };
    private readonly TextBlock _oldPrice = new() { FontSize = 16, TextDecorations = TextDecorations.Strikethrough, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Border _badge = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stock = new() { FontSize = 13.5 };
    private readonly TextBlock _qtyText = new() { FontSize = 18, FontWeight = FontWeight.Bold, MinWidth = 44, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _add = new() { Padding = new Thickness(22, 10), MinWidth = 170, HorizontalContentAlignment = HorizontalAlignment.Center };

    private string? _size;
    private string? _color;
    private double _qty = 1;

    public ProductVariantDto? Result { get; private set; }
    public double Quantity => _qty;

    public VariantPickerWindow(CatalogProductTileVm product, IEnumerable<ProductVariantDto> variants)
    {
        _product = product;
        _variants = variants.Where(v => v.IsActive).ToList();
        _basePrice = LocalCartService.ParsePrice(product.PriceLine);
        Title = product.Title;
        Width = 620;
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

        var root = new StackPanel { Margin = new Thickness(28, 24), Spacing = 12 };
        var title = new TextBlock { Text = product.Title, FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(title);

        var sizes = Sizes();
        if (sizes.Count > 0)
        {
            root.Children.Add(Head(Tr.T("Размер", "Өлчөмү", "Size", "Beden", "O'lcham")));
            root.Children.Add(_sizes);
        }

        Use(_colorsHead, TextBlock.ForegroundProperty, "BrushTextSoft");
        _colorsHead.Text = Tr.T("Цвет", "Түсү", "Color", "Renk", "Rang");
        root.Children.Add(_colorsHead);
        root.Children.Add(_colors);

        var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Use(_price, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        Use(_oldPrice, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_badge, Border.BackgroundProperty, "BrushDanger");
        priceRow.Children.Add(_price);
        priceRow.Children.Add(_oldPrice);
        priceRow.Children.Add(_badge);
        root.Children.Add(priceRow);
        Use(_stock, TextBlock.ForegroundProperty, "BrushTextSoft");
        root.Children.Add(_stock);

        // Количество и кнопки.
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        var qtyPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var minus = new Button { Content = "−", Width = 44, Height = 44, FontSize = 20, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        var plus = new Button { Content = "+", Width = 44, Height = 44, FontSize = 20, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        minus.Click += (_, _) => { _qty = Math.Max(1, _qty - 1); Refresh(); };
        plus.Click += (_, _) => { _qty += 1; Refresh(); };
        Use(_qtyText, TextBlock.ForegroundProperty, "BrushText");
        qtyPanel.Children.Add(minus);
        qtyPanel.Children.Add(_qtyText);
        qtyPanel.Children.Add(plus);
        bottom.Children.Add(qtyPanel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var cancel = new Button { Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), Padding = new Thickness(20, 10), MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();
        _add.Classes.Add("btn-primary");
        _add.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_add);
        Grid.SetColumn(buttons, 2);
        bottom.Children.Add(buttons);
        root.Children.Add(bottom);

        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = root };
        Use(frame, Border.BorderBrushProperty, "BrushBorder");
        Content = frame;

        // Сразу выбираем первый размер, где что-то есть, и первый его цвет.
        _size = sizes.FirstOrDefault(s => _variants.Any(v => v.Size == s && v.Quantity > 0)) ?? sizes.FirstOrDefault();
        _color = ColorsFor(_size).FirstOrDefault(c => Find(_size, c)?.Quantity > 0) ?? ColorsFor(_size).FirstOrDefault();
        Refresh();
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

    private void Refresh()
    {
        _sizes.Children.Clear();
        foreach (var s in Sizes())
        {
            var any = _variants.Any(v => string.Equals(v.Size.Trim(), s, StringComparison.OrdinalIgnoreCase) && v.Quantity > 0);
            _sizes.Children.Add(Chip(s, s == _size, any, () =>
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
        _colorsHead.IsVisible = _colors.IsVisible = colorList.Count > 0;
        foreach (var c in colorList)
        {
            var v = Find(_size, c);
            _colors.Children.Add(Chip(c, string.Equals(c, _color, StringComparison.OrdinalIgnoreCase), v?.Quantity > 0, () => { _color = c; Refresh(); }));
        }

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

        _stock.Text = sel == null
            ? Tr.T("Выберите размер и цвет", "Өлчөмүн жана түсүн тандаңыз", "Choose a size and color", "Beden ve renk seçin", "O'lcham va rangni tanlang")
            : Tr.T($"В наличии: {sel.Quantity:0.###} шт.", $"Бар: {sel.Quantity:0.###} даана", $"In stock: {sel.Quantity:0.###} pcs", $"Stokta: {sel.Quantity:0.###} adet", $"Mavjud: {sel.Quantity:0.###} dona")
              + (sel.Quantity < _qty ? Tr.T(" — меньше, чем нужно", " — керектүүдөн аз", " — less than needed", " — gerekenden az", " — keragidan kam") : "");
        _qtyText.Text = _qty.ToString("0", CultureInfo.InvariantCulture);
        _add.IsEnabled = sel != null;
        _add.Content = Tr.T("Добавить в чек", "Чекке кошуу", "Add to receipt", "Fişe ekle", "Chekka qo'shish")
                       + $" · {(price * _qty).ToString("N2", Ru)}";
    }

    private Button Chip(string text, bool selected, bool available, Action onClick)
    {
        var b = new Button
        {
            Content = available ? text : text + " · " + Tr.T("нет", "жок", "none", "yok", "yo'q"),
            Padding = new Thickness(16, 9),
            Margin = new Thickness(0, 0, 8, 8),
            MinWidth = 56,
            FontSize = 15,
            FontWeight = selected ? FontWeight.Bold : FontWeight.Normal,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Opacity = available ? 1 : 0.55,
            BorderThickness = new Thickness(selected ? 2 : 1),
        };
        Use(b, Button.BorderBrushProperty, selected ? "BrushAccent" : "BrushBorder");
        if (selected)
            Use(b, Button.BackgroundProperty, "BrushAccentSoft");
        b.Click += (_, _) => onClick();
        return b;
    }

    private TextBlock Head(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
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
