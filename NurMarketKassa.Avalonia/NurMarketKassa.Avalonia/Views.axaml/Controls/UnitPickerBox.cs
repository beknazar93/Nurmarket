using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>2026-10-29, жалоба владельца «в добавлении товаров нет единицы (шт, кг, литр)»: выбор единицы измерения одним полем — строка поиска
/// плюс список прямо под ней (не Popup и не ComboBox: в окнах кассы без системной рамки они не открываются, как у «Категории» в карточке
/// товара). Печатаете «кг», «kg», «литр», «dona» — список сужается; Enter или щелчок по единице выбирает; нет такой в списке — «своя»
/// остаётся как введена. Значение (<see cref="Value"/>) — единица для хранения («шт», «кг», «л»…), на экране — по языку («шт.», «даана»).
/// Единицы берутся из единого справочника <see cref="UnitCatalog"/>.</summary>
public sealed class UnitPickerBox : UserControl
{
    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<UnitPickerBox, string>(nameof(Value), UnitCatalog.DefaultUnit, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private readonly TextBox _box;
    private readonly Button _arrow;
    private readonly Border _panel;
    private readonly WrapPanel _chips;
    private readonly TextBlock _customHint;
    private TopLevel? _top;
    private bool _syncing;

    /// <summary>Класс кнопок-единиц: в окнах со своими стилями (карточка товара, склад) — «SecondaryButton»/«PrimaryButton»; в окнах на ModalShell —
    /// «modal-secondary»/«modal-primary» (ставится до первого раскрытия списка).</summary>
    public string SecondaryClass { get; set; } = "SecondaryButton";

    public string PrimaryClass { get; set; } = "PrimaryButton";

    public UnitPickerBox()
    {
        _box = new TextBox
        {
            MinHeight = 44,
            VerticalContentAlignment = VerticalAlignment.Center,
            Watermark = Tr.T("Единица: шт, кг, л…", "Бирдик: шт, кг, л…", "Unit: pcs, kg, l…", "Birim: adet, kg, l…", "Birlik: dona, kg, l…"),
        };
        _arrow = new Button
        {
            Content = "▾",
            MinHeight = 44,
            MinWidth = 44,
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _arrow.Classes.Add("SecondaryButton");
        AttachedToVisualTree += (_, _) =>
        {
            if (SecondaryClass != "SecondaryButton")
            {
                _arrow.Classes.Remove("SecondaryButton");
                _arrow.Classes.Add(SecondaryClass);
            }
        };
        _arrow.Click += (_, _) => SetOpen(!_panel!.IsVisible);

        _chips = new WrapPanel { Orientation = Orientation.Horizontal };
        _customHint = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), IsVisible = false };
        _customHint.Bind(TextBlock.ForegroundProperty, _customHint.GetResourceObservable("BrushTextSoft"));
        var panelStack = new StackPanel();
        panelStack.Children.Add(_chips);
        panelStack.Children.Add(_customHint);
        _panel = new Border
        {
            IsVisible = false,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 8, 8, 4),
            Margin = new Thickness(0, 4, 0, 0),
            BorderThickness = new Thickness(1),
            Child = panelStack,
        };
        _panel.Bind(Border.BackgroundProperty, _panel.GetResourceObservable("BrushPanelSoft"));
        _panel.Bind(Border.BorderBrushProperty, _panel.GetResourceObservable("BrushBorder"));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_box, 0);
        Grid.SetColumn(_arrow, 1);
        row.Children.Add(_box);
        row.Children.Add(_arrow);
        var root = new StackPanel();
        root.Children.Add(row);
        root.Children.Add(_panel);
        Content = root;

        _box.GotFocus += (_, _) =>
        {
            _box.SelectAll();
            SetOpen(true);
        };
        _box.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;
            if (_box.IsFocused)
                SetOpen(true);
        };
        _box.KeyDown += OnBoxKeyDown;
        _box.LostFocus += (_, _) =>
        {
            // Щелчок по единице в списке забирает фокус у поля раньше, чем сработает кнопка, — тогда ввод фиксирует сама кнопка.
            if (_panel.IsVisible && _panel.IsPointerOver)
                return;
            Commit(_box.Text);
        };
        ShowCurrent();
    }

    /// <summary>Единица для хранения: «шт», «кг», «л»… Своя единица — как введена.</summary>
    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Подпись в строке поиска, пока она пуста.</summary>
    public string? Placeholder
    {
        get => _box.Watermark;
        set => _box.Watermark = value;
    }

    /// <summary>Поле ввода (для фокуса и стендов).</summary>
    public TextBox InputBox => _box;

    /// <summary>Открыт ли список единиц.</summary>
    public bool IsOpen => _panel.IsVisible;

    /// <summary>Раскрыть/свернуть список (для стенда снимков).</summary>
    public void Open(bool open = true) => SetOpen(open);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
            ShowCurrent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(PointerPressedEvent, OnTopPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(PointerPressedEvent, OnTopPointerPressed);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_panel.IsVisible)
            return;
        // Нажатие внутри этого поля (строка, стрелка, список) — не «мимо».
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, this))
                return;
        }
        Commit(_box.Text);
        SetOpen(false);
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit(_box.Text);
                SetOpen(false);
                break;
            case Key.Escape when _panel.IsVisible:
                e.Handled = true;
                ShowCurrent();
                SetOpen(false);
                break;
            case Key.Down:
                e.Handled = true;
                SetOpen(true);
                break;
        }
    }

    private void SetOpen(bool open)
    {
        if (open)
            Rebuild();
        _panel.IsVisible = open;
    }

    private void Rebuild()
    {
        _chips.Children.Clear();
        var text = _box.Text?.Trim() ?? "";
        // Строка поиска ещё показывает текущую единицу целиком — тогда показываем весь список, а не одну единицу.
        var showAll = text.Length == 0 || string.Equals(text, UnitCatalog.Display(Value), StringComparison.CurrentCultureIgnoreCase);
        var list = showAll ? UnitCatalog.All : UnitCatalog.Search(text);
        foreach (var unit in list)
        {
            var chip = new Button
            {
                Content = unit.Display,
                MinHeight = 40,
                MinWidth = 52,
                Margin = new Thickness(0, 0, 6, 6),
                Focusable = false,
                Tag = unit.Storage,
            };
            chip.Classes.Add(string.Equals(unit.Storage, Value, StringComparison.Ordinal) ? PrimaryClass : SecondaryClass);
            chip.Click += (_, _) =>
            {
                Select(unit.Storage);
                SetOpen(false);
            };
            _chips.Children.Add(chip);
        }
        var custom = text.Length > 0 && !showAll && UnitCatalog.Find(text) is null;
        if (custom)
        {
            var chip = new Button
            {
                Content = Tr.T($"Своя: «{text}»", $"Өз бирдигим: «{text}»", $"Custom: “{text}”", $"Özel: «{text}»", $"O'z birligim: «{text}»"),
                MinHeight = 40,
                Margin = new Thickness(0, 0, 6, 6),
                Focusable = false,
            };
            chip.Classes.Add(SecondaryClass);
            chip.Click += (_, _) =>
            {
                Select(text);
                SetOpen(false);
            };
            _chips.Children.Add(chip);
        }
        _customHint.IsVisible = list.Count == 0 && !custom;
        _customHint.Text = Tr.T("Такой единицы нет в списке.", "Тизмеде мындай бирдик жок.", "No such unit in the list.", "Listede böyle bir birim yok.", "Ro'yxatda bunday birlik yo'q.");
    }

    private void Select(string storage)
    {
        Value = UnitCatalog.NormalizeOrKeep(storage);
        ShowCurrent();
    }

    private void Commit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowCurrent();
            return;
        }
        var value = UnitCatalog.NormalizeOrKeep(text);
        if (!string.Equals(value, Value, StringComparison.Ordinal))
            Value = value;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        _syncing = true;
        try
        {
            _box.Text = UnitCatalog.Display(Value);
        }
        finally
        {
            _syncing = false;
        }
    }
}
