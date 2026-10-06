using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-06, исследование «Кассы для одежды» (О-70), владелец: «делай всё по этапно». Приёмка сеткой: строки — цвета,
/// столбцы — размеры, в ячейке вписывается, сколько пришло этого размера и цвета (под ней — сколько уже есть). Сочетания,
/// которого нет у товара, — прочерк (его заводят в «Размеры и цвета»). Result — id варианта → штук; Labels — подписи.
/// </summary>
public sealed class VariantReceiveWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, TextBox> _cells = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _total = new() { FontSize = 15, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };

    public Dictionary<string, double>? Result { get; private set; }
    public Dictionary<string, string> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VariantReceiveWindow(string productTitle, IReadOnlyList<ProductVariantDto> variants, IReadOnlyDictionary<string, double>? current)
    {
        Title = Tr.T("Приёмка по размерам", "Өлчөмдөр боюнча кабыл алуу", "Receive by size", "Bedene göre kabul", "O'lchamlar bo'yicha qabul");
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 420;
        MaxHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushDialogPanel");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var list = variants.Where(v => !string.IsNullOrWhiteSpace(v.Id)).ToList();
        var sizes = list.Select(v => (v.Size ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(SizeRank).ThenBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var colors = list.Select(v => (v.Color ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();

        var root = new StackPanel { Margin = new Thickness(22, 18), Spacing = 12 };
        var head = new TextBlock { Text = productTitle, FontSize = 19, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, MaxWidth = 900 };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        root.Children.Add(head);
        root.Children.Add(Soft(Tr.T("Сколько пришло каждого размера и цвета. Под ячейкой — сколько уже есть на складе.",
            "Ар бир өлчөм жана түс канча келди. Уячанын астында — кампада канча бар.",
            "How many of each size and colour arrived. Below each cell — current stock.",
            "Her beden ve renkten kaç adet geldi. Hücrenin altında — mevcut stok.",
            "Har bir o'lcham va rangdan nechta keldi. Katakcha ostida — omborda qancha bor.")));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto" + string.Concat(Enumerable.Repeat(",Auto", sizes.Count))) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < sizes.Count; c++)
        {
            var h = new TextBlock { Text = sizes[c].Length == 0 ? "—" : sizes[c], FontWeight = FontWeight.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 4, 6) };
            Use(h, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(h, c + 1);
            grid.Children.Add(h);
        }
        for (var r = 0; r < colors.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new TextBlock { Text = colors[r].Length == 0 ? "—" : colors[r], FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetRow(name, r + 1);
            grid.Children.Add(name);
            for (var c = 0; c < sizes.Count; c++)
            {
                var v = list.FirstOrDefault(x => string.Equals((x.Size ?? "").Trim(), sizes[c], StringComparison.OrdinalIgnoreCase)
                                                 && string.Equals((x.Color ?? "").Trim(), colors[r], StringComparison.OrdinalIgnoreCase));
                Control cell;
                if (v is null)
                {
                    cell = Soft("—", center: true);
                    ((TextBlock)cell).HorizontalAlignment = HorizontalAlignment.Center;
                }
                else
                {
                    var box = new TextBox { Width = 64, Height = 38, Watermark = "0", HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 15 };
                    if (current != null && current.TryGetValue(v.Id!, out var have) && have > 0)
                        box.Text = have.ToString("0.###", Inv);
                    box.TextChanged += (_, _) => UpdateTotal();
                    _cells[v.Id!] = box;
                    Labels[v.Id!] = string.Join(", ", new[] { v.Size, v.Color }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
                    var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
                    stack.Children.Add(box);
                    var stock = Soft(Tr.T($"есть {v.Quantity:0.###}", $"бар {v.Quantity:0.###}", $"has {v.Quantity:0.###}", $"var {v.Quantity:0.###}", $"bor {v.Quantity:0.###}"), center: true);
                    stock.HorizontalAlignment = HorizontalAlignment.Center;
                    stock.FontSize = 11;
                    stack.Children.Add(stock);
                    cell = stack;
                }
                cell.Margin = new Thickness(4, 2, 4, 6);
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c + 1);
                grid.Children.Add(cell);
            }
        }
        root.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, MaxHeight = 520 });

        Use(_total, TextBlock.ForegroundProperty, "BrushCatalogPrice");
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        buttons.Children.Add(_total);
        var cancel = UiKit.Ghost(this, Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Margin = new Thickness(12, 0, 10, 0);
        cancel.Click += (_, _) => Close();
        Grid.SetColumn(cancel, 1);
        buttons.Children.Add(cancel);
        var ok = UiKit.Primary(this, Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"));
        ok.MinWidth = 140;
        ok.Click += (_, _) =>
        {
            Result = _cells.ToDictionary(kv => kv.Key, kv => Parse(kv.Value.Text), StringComparer.OrdinalIgnoreCase)
                .Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            Close();
        };
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        Content = root;
        UpdateTotal();
        Opened += (_, _) => _cells.Values.FirstOrDefault()?.Focus();
    }

    private static double Parse(string? text) =>
        double.TryParse((text ?? "").Replace(',', '.').Trim(), NumberStyles.Any, Inv, out var v) && v > 0 ? Math.Round(v, 3) : 0;

    private void UpdateTotal()
    {
        var sum = _cells.Values.Sum(b => Parse(b.Text));
        _total.Text = Tr.T($"Итого: {sum:0.###} шт.", $"Бардыгы: {sum:0.###} даана", $"Total: {sum:0.###} pcs", $"Toplam: {sum:0.###} adet", $"Jami: {sum:0.###} dona");
    }

    /// <summary>Порядок размеров как в окне выбора: XS…XXL, потом числа по возрастанию, потом остальное.</summary>
    internal static double SizeRank(string? size)
    {
        string[] order = { "XXXS", "XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL", "4XL", "5XL" };
        var s = (size ?? "").Trim();
        var i = Array.FindIndex(order, x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            return i;
        var digits = new string(s.Where(ch => char.IsDigit(ch) || ch is '.' or ',').ToArray()).Replace(',', '.');
        return double.TryParse(digits, NumberStyles.Any, Inv, out var n) ? 100 + n : 10_000;
    }

    private TextBlock Soft(string text, bool center = false)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Top, MaxWidth = 900 };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
