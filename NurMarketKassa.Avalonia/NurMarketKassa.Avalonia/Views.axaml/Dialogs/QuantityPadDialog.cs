using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-12 (ХМ6, ТЗ хозмаг 4.1 п. 2): окно «Количество / длина / площадь» для товара с дробной единицей («Хозтовары», дробное количество
/// включено): поле с единицей, кнопки шагов (м — 0,5…100; м² — 1…20; л — 0,5…10), «Д × Ш» для м² (результат вверх до шага), сумма строки,
/// ошибка подписью («Минимум — 1 м.»). Enter — «Готово», Esc — «Отмена». Цифры — своя цифровая панель окна (крупные кнопки ≥ 56 px, для сенсора
/// 1024×600); полная экранная клавиатура запрещена — текст вводится только физической клавиатурой или osk.exe. Формулы — <see cref="QuantityPadMath"/>.
/// Результат ShowDialog — true; количество — <see cref="Quantity"/> (уже по шагу и минимуму).
/// </summary>
internal sealed class QuantityPadDialog : Window
{
    private readonly QuantityRule _rule;
    private readonly string _unitText;
    private readonly decimal _price;
    private readonly TextBox _qty = new();
    private readonly TextBox _length = new();
    private readonly TextBox _width = new();
    private readonly TextBlock _total = new() { FontSize = 20, FontWeight = FontWeight.Bold };
    private readonly TextBlock _error = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _ok;
    private bool _suspend;

    /// <summary>Количество для чека (по шагу и минимуму единицы) после «Готово».</summary>
    public double Quantity { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public QuantityPadDialog(string productTitle, string priceLine, string? unitText, QuantityRule rule)
    {
        _rule = rule;
        _unitText = string.IsNullOrWhiteSpace(unitText) ? UnitCatalog.Display(rule.UnitCode) : UnitCatalog.Display(unitText);
        _price = (decimal)LocalCartService.ParsePrice(priceLine);
        var title = T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
        Title = title;
        SectionCards.AsModal(this, 600);

        var root = new StackPanel { Spacing = 12 };

        // — поле количества с единицей —
        _qty.FontSize = 28;
        _qty.FontWeight = FontWeight.Bold;
        _qty.MaxLength = 10;
        _qty.Text = "";
        _qty.Watermark = QuantityPadMath.Format(rule.Min);
        SectionCards.Field(_qty);
        AutomationProperties.SetName(_qty, title);
        _qty.TextChanged += (_, _) => OnChanged();
        var unitLabel = new TextBlock { Text = _unitText, FontSize = 22, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Use(unitLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        var qtyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        qtyRow.Children.Add(_qty);
        Grid.SetColumn(unitLabel, 1);
        qtyRow.Children.Add(unitLabel);
        root.Children.Add(qtyRow);

        // — шаги: прибавляют к полю —
        var steps = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var step in QuantityPadMath.StepsFor(rule.UnitCode))
        {
            var s = step;
            steps.Children.Add(KeyButton("+" + QuantityPadMath.Format(s), () => AddStep(s), 76));
        }
        root.Children.Add(steps);

        // — «Д × Ш» только для м² —
        if (rule.UnitCode == "m2")
        {
            _length.FontSize = 18;
            _width.FontSize = 18;
            _length.MaxLength = 8;
            _width.MaxLength = 8;
            SectionCards.Field(_length);
            SectionCards.Field(_width);
            AutomationProperties.SetName(_length, T("Длина, м", "Узундугу, м", "Length, m", "Uzunluk, m", "Uzunligi, m"));
            AutomationProperties.SetName(_width, T("Ширина, м", "Туурасы, м", "Width, m", "Genişlik, m", "Kengligi, m"));
            _length.TextChanged += (_, _) => OnDims();
            _width.TextChanged += (_, _) => OnDims();
            var dims = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
            dims.Children.Add(Labeled(T("Длина, м", "Узундугу, м", "Length, m", "Uzunluk, m", "Uzunligi, m"), _length));
            var w = Labeled(T("Ширина, м", "Туурасы, м", "Width, m", "Genişlik, m", "Kengligi, m"), _width);
            Grid.SetColumn(w, 2);
            dims.Children.Add(w);
            var hint = new TextBlock { Text = T("Д × Ш: площадь округляется вверх до шага.", "У × Т: аянт кадамга чейин жогору тегеректелет.", "L × W: the area is rounded up to the step.",
                "U × G: alan adıma kadar yukarı yuvarlanır.", "U × K: maydon qadamgacha yuqoriga yaxlitlanadi."), FontSize = 12 };
            Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
            root.Children.Add(dims);
            root.Children.Add(hint);
        }

        // — цифровая панель окна (0–9, запятая, стереть, очистить) —
        root.Children.Add(BuildKeypad());

        // — сумма строки —
        var totalLabel = new TextBlock { Text = T("Сумма строки", "Саптын суммасы", "Line total", "Satır tutarı", "Qator summasi"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Use(totalLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        var totalRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        totalRow.Children.Add(totalLabel);
        Grid.SetColumn(_total, 1);
        totalRow.Children.Add(_total);
        root.Children.Add(totalRow);

        var cancel = SectionCards.Cancel(T("Отмена", "Жок", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _ok = SectionCards.Primary(T("Готово", "Даяр", "Done", "Tamam", "Tayyor"), isDefault: true);
        _ok.IsEnabled = false;
        _ok.Click += (_, _) => Accept();

        var errorBox = SectionCards.ErrorBox(this, _error);
        var shell = SectionCards.Shell(title, productTitle + (_price > 0m ? " · " + _price.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " / " + _unitText : ""),
            root, SectionCards.Buttons(cancel, _ok), errorBox);
        shell.InitialFocus = _qty;
        Content = shell;
        OnChanged();
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));

    private StackPanel Labeled(string label, TextBox box)
    {
        var l = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(l, TextBlock.ForegroundProperty, "BrushTextSoft");
        return new StackPanel { Spacing = 4, Children = { l, box } };
    }

    /// <summary>Крупная кнопка (≥ 56 px по высоте): на сенсоре не промахнуться. Фокус не берёт — поле количества остаётся активным.</summary>
    private Button KeyButton(string text, Action action, double minWidth = 64)
    {
        var b = new Button
        {
            Content = text, MinWidth = minWidth, MinHeight = 56, Margin = new Thickness(0, 0, 8, 8), FontSize = 20,
            Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        b.Classes.Add("modal-secondary");
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => action();
        return b;
    }

    private Control BuildKeypad()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (var c = 0; c < 6; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var keys = new[] { "7", "8", "9", "4", "5", "6", "1", "2", "3", "0", ",", "⌫" };
        // 2 строки по 6 клавиш: 7 8 9 4 5 6 / 1 2 3 0 , ⌫ — компактно для экрана 1024×600.
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var b = KeyButton(key, () => PressKey(key), 64);
            Grid.SetColumn(b, i % 6);
            Grid.SetRow(b, i / 6);
            grid.Children.Add(b);
        }
        return grid;
    }

    private void PressKey(string key)
    {
        var text = _qty.Text ?? "";
        switch (key)
        {
            case "⌫":
                text = text.Length > 0 ? text[..^1] : text;
                break;
            case ",":
                if (!text.Contains(',') && !text.Contains('.'))
                    text = (text.Length == 0 ? "0" : text) + ",";
                break;
            default:
                if (text.Length < 10)
                    text += key;
                break;
        }
        _qty.Text = text;
        _qty.CaretIndex = text.Length;
    }

    private void AddStep(decimal step)
    {
        var current = ConsumptionCalculator.TryParseNumber(_qty.Text, out var v) ? v : 0m;
        _qty.Text = QuantityPadMath.Format(current + step).Replace('.', ',');
        _qty.CaretIndex = _qty.Text.Length;
    }

    private void OnDims()
    {
        if (_suspend)
            return;
        if (ConsumptionCalculator.TryParseNumber(_length.Text, out var l) && ConsumptionCalculator.TryParseNumber(_width.Text, out var w) && l > 0m && w > 0m)
            _qty.Text = QuantityPadMath.Format(QuantityPadMath.Area(l, w, _rule.Step)).Replace('.', ',');
    }

    private void OnChanged()
    {
        if (string.IsNullOrWhiteSpace(_qty.Text))
        {
            _error.IsVisible = false;
            _ok.IsEnabled = false;
            _total.Text = "—";
            return;
        }
        var check = QuantityPadMath.Check(_qty.Text, _rule, _unitText);
        _error.Text = check.Error ?? "";
        _error.IsVisible = check.Error is not null;
        _ok.IsEnabled = check.Ok;
        _total.Text = check.Ok && _price > 0m
            ? QuantityPadMath.LineTotal(check.Value, _price).ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " " + T("сом", "сом", "som", "som", "so'm")
            : "—";
    }

    private void Accept()
    {
        var check = QuantityPadMath.Check(_qty.Text, _rule, _unitText);
        if (!check.Ok)
            return;
        Quantity = check.Value;
        Close(true);
    }
}
