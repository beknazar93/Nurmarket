using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-10-22 (меню, сворачиваемые разделы), владелец: «в кассе разделы сделай сворачиваемыми». В новом виде меню ☰ группы
/// («Продажи», «Смена и деньги», «Товары», «Работа», «Отчёты», «Экран», «Настройки и помощь») сворачиваются заголовком:
///  • заголовок — кнопка во всю ширину (≥ 44 px) с шевроном ▾/▸; в свёрнутой группе — число пунктов и её бейджи («Некорректные чеки 4»);
///  • раскрытие — плавно (прозрачность и сдвиг 140 мс), содержимое группы не пересобирается — только прячется/показывается;
///  • какие свёрнуты — UserPreferences.KassaMenuCollapsedGroups (этот ПК, в облако не уходит), по умолчанию всё раскрыто;
///  • «Свернуть всё / Раскрыть всё» — ссылкой под поиском; при поиске (Ctrl+K) свёрнутые группы с найденным временно раскрыты;
///  • клавиатура: заголовок — в обходе ↑↓/Tab, Enter/Пробел на нём сворачивает; свёрнутые плитки в обход не попадают;
///  • экранный диктор (UIA): «Продажи, свёрнуто, 5 пунктов» / «Продажи, развёрнуто».
/// Плитки, права, поиск и команды — прежние (SideMenuView.axaml.cs); здесь только заголовки групп.</summary>
public partial class SideMenuView
{
    private sealed class MenuGroup
    {
        public string Key = "";
        public StackPanel Box = null!;
        public TextBlock Caption = null!;
        public StackPanel Body = null!;
        public Button Header = null!;
        public TextBlock Chevron = null!;
        public TextBlock Title = null!;
        public TextBlock Count = null!;
        public StackPanel Badges = null!;
        public bool Collapsed;
        public bool ShownExpanded = true;
    }

    private readonly List<MenuGroup> _groups = new();
    private Button? _collapseAllLink;
    private Button? _expandAllLink;
    private StackPanel? _groupLinks;

    private static readonly (string Box, string Key)[] GroupNames =
    {
        ("NewGroupSales", "sales"), ("NewGroupMoney", "money"), ("NewGroupGoods", "goods"), ("NewGroupWork", "work"),
        ("NewGroupReports", "reports"), ("NewGroupScreen", "screen"), ("NewGroupSystem", "system"),
    };

    /// <summary>Заголовки групп: подпись группы (km-caption) остаётся источником текста (ApplyTexts) и прячется; её место занимает
    /// кнопка-заголовок, а всё остальное содержимое группы переносится в одну панель «тело» — её и прячем.</summary>
    private void InitGroups()
    {
        AddGroupStyles();
        var collapsed = CollapsedKeys();
        foreach (var (boxName, key) in GroupNames)
        {
            if (this.FindControl<StackPanel>(boxName) is not { } box || box.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("km-caption")) is not { } caption)
                continue;
            var body = new StackPanel { RenderTransform = new TranslateTransform() };
            foreach (var child in box.Children.Where(c => !ReferenceEquals(c, caption)).ToList())
            {
                box.Children.Remove(child);
                body.Children.Add(child);
            }
            var g = new MenuGroup { Key = key, Box = box, Caption = caption, Body = body, Collapsed = collapsed.Contains(key) };
            g.Chevron = new TextBlock { FontSize = 16, Width = 20, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            g.Chevron.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            g.Title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            g.Title.Classes.Add("km-caption");
            g.Count = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            g.Count.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            g.Badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            row.Children.Add(g.Chevron);
            Grid.SetColumn(g.Title, 1);
            row.Children.Add(g.Title);
            Grid.SetColumn(g.Badges, 2);
            row.Children.Add(g.Badges);
            Grid.SetColumn(g.Count, 3);
            row.Children.Add(g.Count);
            g.Header = new Button { Content = row, Focusable = false };
            g.Header.Classes.Add("km-group-head");
            var group = g;
            g.Header.Click += (_, _) => ToggleGroup(group);
            caption.IsVisible = false;
            box.Children.Insert(box.Children.IndexOf(caption) + 1, g.Header);
            box.Children.Add(body);
            caption.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty)
                    UpdateGroupHeaders();
            };
            _groups.Add(g);
        }

        // «Свернуть всё · Раскрыть всё» — мелкими ссылками под поиском (не во время поиска).
        _collapseAllLink = LinkButton(() => SetAllGroups(true));
        _expandAllLink = LinkButton(() => SetAllGroups(false));
        _groupLinks = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 4, Margin = new Thickness(0, 4, 0, -6),
            Children = { _collapseAllLink, _expandAllLink } };
        var at = NewMenuContent.Children.IndexOf(NewSearchPanel);
        if (at >= 0)
            NewMenuContent.Children.Insert(at + 1, _groupLinks);
        UpdateGroupHeaders();
        ApplyGroupCollapse(false, animate: false);
    }

    private Button LinkButton(Action click)
    {
        var b = new Button { Focusable = false };
        b.Classes.Add("km-group-link");
        b.Click += (_, _) => click();
        return b;
    }

    private static HashSet<string> CollapsedKeys() =>
        new((UserPreferences.Instance.KassaMenuCollapsedGroups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    private void SaveCollapsed()
    {
        var keys = _groups.Where(g => g.Collapsed).Select(g => g.Key).ToList();
        UserPreferences.Instance.KassaMenuCollapsedGroups = keys.Count == 0 ? null : string.Join(",", keys);
        try
        {
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Kassa: свёрнутые группы меню не сохранены: {ex.Message}", "WARNING");
        }
    }

    private void ToggleGroup(MenuGroup g)
    {
        // Во время поиска группа показана целиком (найденное) — заголовок поиск не ломает: щелчок просто запоминает выбор.
        g.Collapsed = !g.Collapsed;
        SaveCollapsed();
        ApplyGroupCollapse(_query.Length > 0, animate: true);
        if (_kb is not null && !IsNavigable(_kb))
            SetKb(g.Header, bringIntoView: false);
    }

    private void SetAllGroups(bool collapse)
    {
        foreach (var g in _groups)
            g.Collapsed = collapse;
        SaveCollapsed();
        ApplyGroupCollapse(_query.Length > 0, animate: true);
        if (_kb is not null && !IsNavigable(_kb))
            SetKb(null);
    }

    /// <summary>Что раскрыто: группа не свёрнута или идёт поиск (найденное видно всегда). Раскрытие — плавно; сворачивание — сразу.</summary>
    private void ApplyGroupCollapse(bool searching, bool animate)
    {
        foreach (var g in _groups)
        {
            var expanded = !g.Collapsed || searching;
            if (expanded == g.ShownExpanded && g.Body.IsVisible == expanded)
                continue;
            g.ShownExpanded = expanded;
            g.Body.IsVisible = expanded;
            if (expanded && animate)
                _ = PlayGroupOpen(g.Body);
        }
        if (_groupLinks is not null)
            _groupLinks.IsVisible = !searching && _groups.Count > 0;
        UpdateGroupHeaders();
    }

    private static async Task PlayGroupOpen(Control body)
    {
        try
        {
            var anim = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(140),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, -6d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
                },
            };
            await anim.RunAsync(body).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // без анимации — просто видно
        }
    }

    /// <summary>Подписи заголовков: шеврон, название, в свёрнутой — число пунктов и бейджи группы; имя для диктора.</summary>
    private void UpdateGroupHeaders()
    {
        foreach (var g in _groups)
        {
            var collapsedNow = !g.ShownExpanded;
            g.Chevron.Text = collapsedNow ? "▸" : "▾";
            g.Title.Text = g.Caption.Text;
            g.Title.Margin = new Thickness(6, 0, 0, 0);
            var cells = _cells.Where(c => !c.SearchOnly && g.Body.IsLogicalAncestorOf(c.Cell) && c.Button.IsVisible).ToList();
            var n = cells.Count;
            g.Count.Text = collapsedNow && n > 0 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            g.Count.IsVisible = g.Count.Text.Length > 0;
            // Бейджи свёрнутой группы — рядом с заголовком: «Некорректные чеки 4».
            g.Badges.Children.Clear();
            var badgeNames = new List<string>();
            if (collapsedNow)
                foreach (var c in cells)
                    foreach (var badge in c.Button.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("km-count") && b.Classes.Contains("shown")))
                    {
                        var number = (badge.Child as TextBlock)?.Text ?? "";
                        var label = c.Button.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("km-tile-text"))?.Text ?? "";
                        var pill = new Border { Classes = { "km-group-badge" } };
                        if (badge.Classes.Contains("danger"))
                            pill.Classes.Add("danger");
                        pill.Child = new TextBlock { Text = (label.Length > 0 ? label + " " : "") + number, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
                        g.Badges.Children.Add(pill);
                        badgeNames.Add(label + " " + number);
                    }
            g.Badges.IsVisible = g.Badges.Children.Count > 0;
            var state = collapsedNow
                ? Tr.T("свёрнуто", "жыйыштырылган", "collapsed", "daraltıldı", "yig'ilgan")
                  + (n > 0 ? ", " + Tr.T($"{n} {RuItems(n)}", $"{n} пункт", n == 1 ? "1 item" : $"{n} items", $"{n} öğe", $"{n} ta band") : "")
                : Tr.T("развёрнуто", "ачылган", "expanded", "genişletildi", "ochilgan");
            AutomationProperties.SetName(g.Header, string.Join(", ", new[] { g.Caption.Text ?? "", state }.Concat(badgeNames).Where(x => !string.IsNullOrWhiteSpace(x))));
            ToolTip.SetTip(g.Header, collapsedNow
                ? Tr.T("Показать пункты группы", "Топтун пункттарын көрсөтүү", "Show the group's items", "Grubun öğelerini göster", "Guruh bandlarini ko'rsatish")
                : Tr.T("Свернуть группу", "Топту жыйыштыруу", "Collapse the group", "Grubu daralt", "Guruhni yig'ish"));
        }
        if (_collapseAllLink is not null && _expandAllLink is not null)
        {
            _collapseAllLink.Content = Tr.T("Свернуть всё", "Баарын жыйыштыруу", "Collapse all", "Tümünü daralt", "Hammasini yig'ish");
            _expandAllLink.Content = Tr.T("Раскрыть всё", "Баарын ачуу", "Expand all", "Tümünü genişlet", "Hammasini ochish");
            AutomationProperties.SetName(_collapseAllLink, (string)_collapseAllLink.Content);
            AutomationProperties.SetName(_expandAllLink, (string)_expandAllLink.Content);
            _collapseAllLink.IsEnabled = _groups.Any(g => !g.Collapsed);
            _expandAllLink.IsEnabled = _groups.Any(g => g.Collapsed);
        }
    }

    private static string RuItems(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "пункт",
        (>= 2 and <= 4, not (>= 12 and <= 14)) => "пункта",
        _ => "пунктов",
    };

    private bool IsGroupHeader(Button b) => b.Classes.Contains("km-group-head");

    /// <summary>Заголовки видимых групп — в обходе с клавиатуры (по месту на экране, вместе с плитками).</summary>
    private IEnumerable<Button> GroupHeaders() => _groups.Where(g => g.Box.IsVisible).Select(g => g.Header);

    /// <summary>Пробел на выбранном заголовке (не в поле поиска) — свернуть/раскрыть.</summary>
    private bool HandleGroupSpace(KeyEventArgs e, bool inSearch)
    {
        if (inSearch || e.Key != Key.Space || _kb is null || !IsGroupHeader(_kb))
            return false;
        e.Handled = true;
        _kb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    private void AddGroupStyles()
    {
        static Style S(Func<Selector?, Selector> sel, params (AvaloniaProperty P, object V)[] setters)
        {
            var st = new Style(sel);
            foreach (var (p, v) in setters)
                st.Setters.Add(new Setter(p, v));
            return st;
        }
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-head"),
            (Button.BackgroundProperty, Brushes.Transparent), (Button.BorderThicknessProperty, new Thickness(0)), (Button.CornerRadiusProperty, new CornerRadius(10)),
            (Button.PaddingProperty, new Thickness(4, 0, 8, 0)), (Button.MinHeightProperty, 44.0), (Button.MarginProperty, new Thickness(0, 6, 0, 2)),
            (Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch), (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center), (Button.CursorProperty, new Cursor(StandardCursorType.Hand))));
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-head").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, Brushes.Transparent)));
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-head").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelSoft"))));
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-head").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelPressed"))));
        // Подпись в заголовке — как прежний заголовок группы, но без его полей (поля даёт кнопка).
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-head").Descendant().OfType<TextBlock>().Class("km-caption"),
            (TextBlock.MarginProperty, new Thickness(6, 0, 0, 0))));
        Styles.Add(S(x => x.OfType<Border>().Class("km-group-badge"),
            (Border.CornerRadiusProperty, new CornerRadius(11)), (Border.PaddingProperty, new Thickness(8, 2)), (Border.MinHeightProperty, 22.0),
            (Border.BackgroundProperty, new DynamicResourceExtension("BrushText"))));
        Styles.Add(S(x => x.OfType<Border>().Class("km-group-badge").Child().OfType<TextBlock>(),
            (TextBlock.FontSizeProperty, 12.0), (TextBlock.FontWeightProperty, FontWeight.Bold), (TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
            (TextBlock.ForegroundProperty, new DynamicResourceExtension("BrushPanel"))));
        Styles.Add(S(x => x.OfType<Border>().Class("km-group-badge").Class("danger"),
            (Border.BackgroundProperty, new DynamicResourceExtension("BrushDanger"))));
        Styles.Add(S(x => x.OfType<Border>().Class("km-group-badge").Class("danger").Child().OfType<TextBlock>(),
            (TextBlock.ForegroundProperty, Brushes.White)));
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-link"),
            (Button.BackgroundProperty, Brushes.Transparent), (Button.BorderThicknessProperty, new Thickness(0)), (Button.PaddingProperty, new Thickness(8, 4)),
            (Button.MinHeightProperty, 32.0), (Button.FontSizeProperty, 12.5), (Button.CursorProperty, new Cursor(StandardCursorType.Hand)),
            (Button.ForegroundProperty, new DynamicResourceExtension("BrushTextSoft"))));
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-link").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, Brushes.Transparent)));
        Styles.Add(S(x => x.OfType<Button>().Class("km-group-link").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelSoft")), (ContentPresenter.ForegroundProperty, new DynamicResourceExtension("BrushText"))));
    }
}
