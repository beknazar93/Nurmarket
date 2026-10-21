using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-19, решение владельца: четвёртый вид меню «Вкладки» (вид «Новое» не меняется). В «Новом» при 2 пунктах в
/// группе («Сводка», «Нур Советник») колонка пунктов почти пустая, две тёмные плашки рядом спорят, раздел сжат. Здесь:
/// • слева только тёмная колонка групп (значок + подпись, точки-счётчики, «Тема», «Вид», «Выйти», кружок пользователя) —
///   разметка О2 (OwnerShellWindow.Nav.cs) с колонкой пунктов, которая раскрывается только для поиска (🔍 под логотипом, Ctrl+K);
/// • над разделом полоса TabsBar: «NBS · Товары» мелко (компания и группа; тариф — в подсказке), под ней пункты группы вкладками
///   высотой 42; выбранная — тёмно-синий текст и подчёркивание (правило 2). Не помещаются — последние уходят в «Ещё ▾» (меню),
///   выбранная видна всегда. Группа из одного пункта — без вкладок, только заголовок раздела;
/// • нажатие на группу открывает её последнюю открытую (или первую) вкладку — тем же пунктом меню (права, тариф, скрытые разделы);
/// • кнопки окна и плашка проката переносятся из шапки в полосу вкладок; шапка раздела («Склад») скрыта — название уже во вкладке.
///   В «Сводке» шапка (заголовок, дата, период, Советник) остаётся под вкладками. Раздел (отдельное окно поверх SectionHost)
///   встаёт под полосой вкладок на всю ширину справа от колонки групп (SyncSectionBounds по SectionHost).
/// На телефоне вид всегда «Новое» (меню на весь экран).</summary>
public partial class OwnerShellWindow
{
    private sealed record TabItemRef(string Key, Button Tab);

    private readonly List<TabItemRef> _tabs = new();
    /// <summary>Последняя открытая вкладка группы — её открывает нажатие на группу.</summary>
    private readonly Dictionary<string, string> _tabsLastByGroup = new();
    private Button? _tabsMoreButton;
    private Ellipse? _tabsMoreDot;
    private string? _tabsKey;
    private bool _tabsWired;
    private bool _tabsFitPending;

    private void WireTabs()
    {
        if (_tabsWired)
            return;
        _tabsWired = true;
        TabsMain.SizeChanged += (_, _) =>
        {
            QueueFitTabs();
            FitHeaderTools();
        };
        // Стрелки влево/вправо — к соседней вкладке (Tab — как обычно, по всем кнопкам).
        TabsStrip.AddHandler(KeyDownEvent, TabsStrip_KeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>Кнопки окна и плашка проката: в полосу вкладок («Вкладки») или обратно в шапку. Grid.Column у них остаётся
    /// прежним — при возврате они встают на своё место в шапке (и после ☰ бургера, которая сдвигает колонки шапки).</summary>
    private void PlaceTabsParts(bool tabs)
    {
        WireTabs();
        if (tabs)
        {
            if (ReferenceEquals(CaptionButtons.Parent, HeaderGrid))
            {
                HeaderGrid.Children.Remove(CaptionButtons);
                TabsCaptionSlot.Child = CaptionButtons;
            }
            if (ReferenceEquals(RentalAlertCard.Parent, HeaderGrid))
            {
                HeaderGrid.Children.Remove(RentalAlertCard);
                RentalAlertCard.Margin = default;
                TabsAlertSlot.Child = RentalAlertCard;
            }
        }
        else
        {
            if (ReferenceEquals(CaptionButtons.Parent, TabsCaptionSlot))
            {
                TabsCaptionSlot.Child = null;
                HeaderGrid.Children.Add(CaptionButtons);
            }
            if (ReferenceEquals(RentalAlertCard.Parent, TabsAlertSlot))
            {
                TabsAlertSlot.Child = null;
                RentalAlertCard.Margin = new Thickness(0, 0, 12, 0);
                HeaderGrid.Children.Add(RentalAlertCard);
            }
        }
        UpdateTabsBar();
    }

    private Border? TabBadgeOf(string key) => key == "siteorders" ? _siteOrdersBadge : key == "rentals" ? _rentalsBadge : null;

    /// <summary>Полоса вкладок: группа открытого раздела, её пункты вкладками (из ApplyNavFilter и UpdateRailDots).</summary>
    private void UpdateTabsBar()
    {
        if (TabsBar is null)
            return;
        var on = TabsMenu;
        TabsBar.IsVisible = on;
        // Шапка раздела у «Вкладок» не нужна: название — выбранная вкладка, кнопки окна — в полосе. В «Сводке» — остаётся.
        HeaderGrid.IsVisible = !on || _activeSection == null;
        if (!on)
        {
            _tabsKey = null;
            return;
        }

        var activeKey = _activeSection?.Key ?? "overview";
        var groupKey = GroupOfKey(activeKey) ?? _navGroup;
        var group = _navGroups.FirstOrDefault(g => g.Key == groupKey);
        var entries = _navEntries.Where(x => x.GroupKey == groupKey && groupKey != null).ToList();
        if (groupKey != null && entries.Any(x => x.Key == activeKey))
            _tabsLastByGroup[groupKey] = activeKey;

        TabsCrumbText.Text = CompanyNameText.Text + (group != null ? " · " + group.Title : "");
        // Тариф и срок — в подсказке (как у карточки компании в колонке пунктов).
        ToolTip.SetTip(TabsCrumbText, ToolTip.GetTip(CompanyCard));
        var single = entries.Count <= 1;
        TabsStrip.IsVisible = !single;
        // Группа из одного пункта — без вкладок, только заголовок раздела (в «Сводке» крупный заголовок — в шапке ниже).
        TabsTitleText.IsVisible = single && _activeSection != null;
        TabsTitleText.Text = _activeSection != null ? TitleFor(_activeSection) : "";

        static string BadgeText(Border? b) => b is { IsVisible: true, Child: TextBlock t } ? t.Text + (Equals(b.Tag, "urgent") ? "!" : "") : "";
        var key = string.Join("|", entries.Select(x => x.Key + ":" + x.Title + ":" + BadgeText(TabBadgeOf(x.Key)))) + "#" + activeKey + "#" + single;
        if (key == _tabsKey)
        {
            QueueFitTabs();
            return;
        }
        _tabsKey = key;
        TabsStrip.Children.Clear();
        _tabs.Clear();
        _tabsMoreButton = null;
        if (!single)
        {
            foreach (var entry in entries)
            {
                var tab = TabButton(entry.Key, entry.Title, TabBadgeOf(entry.Key));
                tab.Classes.Set("active", entry.Key == activeKey);
                TabsStrip.Children.Add(tab);
                _tabs.Add(new TabItemRef(entry.Key, tab));
            }
            TabsStrip.Children.Add(MoreTabButton());
        }
        QueueFitTabs();
    }

    /// <summary>Вкладка: подпись и, у «Заказов с сайта» и «Проката», число (красное — просрочено). Нажатие — тот же пункт меню.</summary>
    private Button TabButton(string key, string title, Border? navBadge)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var name = title;
        if (navBadge is { IsVisible: true, Child: TextBlock count })
        {
            var urgent = Equals(navBadge.Tag, "urgent");
            var number = new TextBlock { Text = count.Text, FontSize = 11.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            var pill = new Border
            {
                Child = number, MinWidth = 20, Padding = new Thickness(6, 1, 6, 2), CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
            };
            UseBrush(pill, Border.BackgroundProperty, urgent ? "BrushDanger" : "BrushAccent");
            if (urgent)
                number.Foreground = Brushes.White;
            else
                UseBrush(number, TextBlock.ForegroundProperty, "BrushAccentForeground");
            content.Children.Add(pill);
            name += " (" + count.Text + ")";
        }
        var tab = new Button { Classes = { "ownerTab" }, Content = content, Tag = key };
        AutomationProperties.SetName(tab, name);
        if (_navButtons.TryGetValue(key, out var navButton) && ToolTip.GetTip(navButton) is string tip && tip != title)
            ToolTip.SetTip(tab, tip);
        tab.Click += (_, _) => OpenTab(key);
        return tab;
    }

    /// <summary>«Ещё ▾»: вкладки, которые не поместились, — меню под кнопкой (точка — у спрятанной вкладки есть число).</summary>
    private Button MoreTabButton()
    {
        var label = Tr.T("Ещё", "Дагы", "More", "Daha", "Yana");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var chevron = new Path { Classes = { "tabChevron" } };
        if (this.TryFindResource("ChevronDownIcon", out var icon) && icon is Geometry geometry)
            chevron.Data = geometry;
        content.Children.Add(chevron);
        _tabsMoreDot = new Ellipse { Width = 7, Height = 7, IsVisible = false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-2, 8, 0, 0) };
        UseBrush(_tabsMoreDot, Shape.FillProperty, "BrushDanger");
        content.Children.Add(_tabsMoreDot);
        var more = new Button { Classes = { "ownerTab" }, Content = content, IsVisible = false };
        var tip = Tr.T("Ещё разделы этой группы", "Бул топтун дагы бөлүмдөрү", "More sections of this group", "Bu grubun diğer bölümleri", "Bu guruhning boshqa bo'limlari");
        ToolTip.SetTip(more, tip);
        AutomationProperties.SetName(more, tip);
        more.Click += (_, _) => ShowMoreTabs(more);
        _tabsMoreButton = more;
        return more;
    }

    private MenuFlyout? _tabsMoreFlyout;

    private void ShowMoreTabs(Button anchor)
    {
        _tabsMoreFlyout?.Hide();
        var menu = _tabsMoreFlyout = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft };
        // Тот же вид меню, что «⋯» у строк склада: пункты 44, фон и рамка темы (PosDialogStyles.axaml).
        menu.FlyoutPresenterClasses.Add("row-menu");
        foreach (var item in _tabs.Where(t => !t.Tab.IsVisible))
        {
            var title = _navEntries.FirstOrDefault(x => x.Key == item.Key)?.Title ?? item.Key;
            var badge = TabBadgeOf(item.Key);
            var header = title + (badge is { IsVisible: true, Child: TextBlock count } ? "  ·  " + count.Text : "");
            var menuItem = new MenuItem { Header = header };
            AutomationProperties.SetName(menuItem, header);
            var key = item.Key;
            menuItem.Click += (_, _) => OpenTab(key);
            menu.Items.Add(menuItem);
        }
        if (menu.Items.Count > 0)
            menu.ShowAt(anchor);
    }

    /// <summary>Вкладка (или пункт «Ещё») — нажатие того же пункта меню: права, тариф и закрытие поиска — как в меню.</summary>
    private void OpenTab(string key) =>
        _navEntries.FirstOrDefault(x => x.Key == key)?.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Нажатие на группу: открыть её последнюю открытую вкладку (или первую).</summary>
    private void OpenTabsGroup(string key)
    {
        if (!string.IsNullOrEmpty(NavSearchText.Text))
            NavSearchText.Text = "";
        if (_navPeek)
        {
            _navPeek = false;
            ApplySidebarLayout();
        }
        var entries = _navEntries.Where(x => x.GroupKey == key).ToList();
        if (entries.Count == 0)
            return;
        var target = _tabsLastByGroup.TryGetValue(key, out var last) && entries.FirstOrDefault(x => x.Key == last) is { } remembered
            ? remembered
            : entries[0];
        target.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>🔍 под логотипом: тот же поиск, что Ctrl+K (колонка пунктов раскрывается с полем поиска); повторно — закрыть.</summary>
    private void RailSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (_navPeek && string.IsNullOrEmpty(NavSearchText.Text))
        {
            _navPeek = false;
            ApplySidebarLayout();
            return;
        }
        FocusNavSearch();
    }

    private void ApplyTabsTexts()
    {
        var tip = Tr.T("Найти раздел (Ctrl+K)", "Бөлүмдү табуу (Ctrl+K)", "Find a section (Ctrl+K)", "Bölüm bul (Ctrl+K)", "Bo'limni topish (Ctrl+K)");
        ToolTip.SetTip(RailSearchButton, tip);
        AutomationProperties.SetName(RailSearchButton, tip);
        _tabsKey = null; // «Ещё» и подписи — на новом языке при следующей сборке вкладок
    }

    private void QueueFitTabs()
    {
        if (_tabsFitPending)
            return;
        _tabsFitPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _tabsFitPending = false;
            FitTabs();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Сколько вкладок помещается: остальные — в «Ещё ▾». Выбранная видна всегда (места до неё освобождаются справа).</summary>
    private void FitTabs()
    {
        if (!TabsMenu || _tabs.Count == 0 || _tabsMoreButton is null)
            return;
        var available = TabsMain.Bounds.Width - 2;
        if (available <= 0)
            return;
        var spacing = TabsStrip.Spacing;
        // Ширину вкладки знает только видимая — сначала показать все и измерить.
        var widths = new List<double>();
        foreach (var item in _tabs)
        {
            item.Tab.IsVisible = true;
            item.Tab.Measure(Size.Infinity);
            widths.Add(item.Tab.DesiredSize.Width + spacing);
        }
        var shown = new HashSet<int>(Enumerable.Range(0, _tabs.Count));
        if (widths.Sum() > available)
        {
            _tabsMoreButton.IsVisible = true;
            _tabsMoreButton.Measure(Size.Infinity);
            var budget = available - _tabsMoreButton.DesiredSize.Width;
            var active = _tabs.FindIndex(t => t.Tab.Classes.Contains("active"));
            shown.Clear();
            var used = 0.0;
            if (active >= 0)
            {
                shown.Add(active);
                used = widths[active];
            }
            for (var i = 0; i < _tabs.Count; i++)
            {
                if (i == active)
                    continue;
                if (used + widths[i] > budget)
                    break;
                shown.Add(i);
                used += widths[i];
            }
        }
        var anyHidden = false;
        var hiddenBadge = false;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var visible = shown.Contains(i);
            _tabs[i].Tab.IsVisible = visible;
            if (!visible)
            {
                anyHidden = true;
                hiddenBadge |= TabBadgeOf(_tabs[i].Key) is { IsVisible: true };
            }
        }
        _tabsMoreButton.IsVisible = anyHidden;
        if (_tabsMoreDot is not null)
            _tabsMoreDot.IsVisible = hiddenBadge;
    }

    private void TabsStrip_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End))
            return;
        var buttons = TabsStrip.Children.OfType<Button>().Where(b => b.IsVisible).ToList();
        var index = buttons.FindIndex(b => b.IsFocused);
        if (index < 0 || buttons.Count == 0)
            return;
        index = e.Key switch
        {
            Key.Left => (index - 1 + buttons.Count) % buttons.Count,
            Key.Right => (index + 1) % buttons.Count,
            Key.Home => 0,
            _ => buttons.Count - 1,
        };
        buttons[index].Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}
