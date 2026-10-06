using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, владелец: «теперь займись редизайном и удобством админки». В меню программы владельца больше
/// 30 разделов в 8 группах, и нужный приходилось искать прокруткой. Теперь:
/// • поиск раздела вверху меню — по названию, группе и привычным словам («долг», «прибыль», «принтер»…); Ctrl+K — из
///   любого раздела, Enter открывает первый найденный, Esc очищает;
/// • заголовок группы сворачивает и разворачивает её (запоминается — UserPreferences.OwnerNavCollapsedGroups);
///   открытый раздел виден и в свёрнутой группе.
/// В меню «только иконки» поиска и сворачивания нет — там групп не видно.</summary>
public partial class OwnerShellWindow
{
    private sealed record NavEntry(string Key, string Title, string? GroupKey, Button Button, Action Open);

    private readonly List<NavEntry> _navEntries = new();
    private readonly Dictionary<string, (Button Header, Path Chevron, string Title)> _navGroupHeaders = new();
    private TextBlock? _navNoMatch;
    private bool _navSearchWired;

    /// <summary>Привычные слова, по которым ищут раздел (кроме названия и группы).</summary>
    private static readonly Dictionary<string, string> NavKeywords = new()
    {
        ["warehouse"] = "товар остаток остатки приёмка приемка ревизия инвентаризация списание перемещение этикетка ценник штрихкод",
        ["calculator"] = "наценка цена себестоимость",
        ["restock"] = "заказать закупка срок годности просрочка",
        ["supplierreturns"] = "поставщик брак",
        ["branches"] = "филиал перемещение склад",
        ["sales"] = "чек чеки история возврат",
        ["debts"] = "долг долги рассрочка должник",
        ["rentals"] = "прокат аренда",
        ["finance"] = "выручка касса смена смены отчёт",
        ["analytics"] = "отчёт сезонность график",
        ["abc"] = "abc авс",
        ["profitcash"] = "прибыль доход расход деньги",
        ["losssales"] = "убыток скидка минус",
        ["sizesreport"] = "размер цвет одежда",
        ["siteorders"] = "заказ интернет магазин",
        ["clients"] = "покупатель бонус баллы",
        ["telegrambot"] = "бот телеграм",
        ["salary"] = "сотрудник кассир зарплата",
        ["marketplace"] = "купить функция тариф",
        ["settings"] = "принтер весы язык тема тариф аккаунт",
        ["support"] = "помощь anydesk",
        ["logs"] = "ошибка журнал",
    };

    private static HashSet<string> CollapsedNavGroups() =>
        new((UserPreferences.Instance.OwnerNavCollapsedGroups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private bool NavIconsOnly => UserPreferences.Instance.OwnerSidebarCollapsed && !_phoneLayout;

    private Control NavGroupHeader(string key, string title)
    {
        var text = new TextBlock { Text = title.ToUpper(UiCulture), VerticalAlignment = VerticalAlignment.Center };
        var chevron = new Path { Margin = new Thickness(6, 0, 2, 0), RenderTransformOrigin = RelativePoint.Center };
        if (this.TryFindResource("ChevronDownIcon", out var icon) && icon is Geometry geometry)
            chevron.Data = geometry;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        var header = new Button { Content = grid, Classes = { "navGroupBtn" } };
        ToolTip.SetTip(header, Tr.T("Свернуть или развернуть группу", "Топту жыйноо же ачуу", "Collapse or expand the group", "Grubu daralt veya genişlet",
            "Guruhni yig'ish yoki ochish"));
        header.Click += (_, _) => ToggleNavGroup(key);
        _navGroupHeaders[key] = (header, chevron, title);
        return header;
    }

    private void ToggleNavGroup(string key)
    {
        var collapsed = CollapsedNavGroups();
        if (!collapsed.Remove(key))
            collapsed.Add(key);
        UserPreferences.Instance.OwnerNavCollapsedGroups = collapsed.Count == 0 ? null : string.Join(",", collapsed);
        UserPreferences.Instance.SaveToDisk();
        ApplyNavFilter();
    }

    private static bool NavMatches(NavEntry entry, string groupTitle, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = entry.Title + " " + groupTitle + " " + (NavKeywords.TryGetValue(entry.Key, out var extra) ? extra : "");
        return words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Что видно в меню: при поиске — найденное (группы без находок прячутся), без поиска — всё, кроме
    /// пунктов свёрнутых групп (открытый раздел виден всегда).</summary>
    private void ApplyNavFilter()
    {
        WireNavSearch();
        NavSearchText.Watermark = Tr.T("Найти раздел", "Бөлүмдү табуу", "Find a section", "Bölüm bul", "Bo'limni topish");
        var iconsOnly = NavIconsOnly;
        var query = iconsOnly ? "" : (NavSearchText.Text ?? "").Trim();
        var filtering = query.Length > 0;
        var collapsedGroups = CollapsedNavGroups();
        var activeKey = _activeSection?.Key ?? "overview";
        var groupsWithItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shown = 0;
        foreach (var entry in _navEntries)
        {
            var groupTitle = entry.GroupKey != null && _navGroupHeaders.TryGetValue(entry.GroupKey, out var g) ? g.Title : "";
            var visible = filtering
                ? NavMatches(entry, groupTitle, query)
                : iconsOnly || entry.GroupKey is null || !collapsedGroups.Contains(entry.GroupKey) || entry.Key == activeKey;
            entry.Button.IsVisible = visible;
            if (visible)
            {
                shown++;
                if (entry.GroupKey != null)
                    groupsWithItems.Add(entry.GroupKey);
            }
        }
        foreach (var (key, (header, chevron, _)) in _navGroupHeaders)
        {
            header.IsVisible = !filtering || groupsWithItems.Contains(key);
            chevron.RenderTransform = !filtering && collapsedGroups.Contains(key) ? new RotateTransform(-90) : null;
        }

        if (_navNoMatch == null || !NavPanel.Children.Contains(_navNoMatch))
        {
            _navNoMatch = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 14, 8, 0) };
            _navNoMatch.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            NavPanel.Children.Add(_navNoMatch);
        }
        _navNoMatch.Text = Tr.T("Такого раздела нет. Попробуйте другое слово.", "Мындай бөлүм жок. Башка сөздү байкап көрүңүз.",
            "No such section. Try another word.", "Böyle bir bölüm yok. Başka bir kelime deneyin.", "Bunday bo'lim yo'q. Boshqa so'zni sinab ko'ring.");
        _navNoMatch.IsVisible = filtering && shown == 0;
    }

    private void WireNavSearch()
    {
        if (_navSearchWired)
            return;
        _navSearchWired = true;
        NavSearchText.TextChanged += (_, _) => ApplyNavFilter();
        NavSearchText.AddHandler(KeyDownEvent, NavSearch_KeyDown, RoutingStrategies.Tunnel);
        NavSearchText.GotFocus += (_, _) => UpdateNavSearchLook();
        NavSearchText.LostFocus += (_, _) => UpdateNavSearchLook();
        NavSearchText.TextChanged += (_, _) => UpdateNavSearchLook();
        AddHandler(KeyDownEvent, NavHotkey_KeyDown, RoutingStrategies.Tunnel);
        UpdateNavSearchLook();
        // 2026-10-06, владелец: «открывать товар на складе голосом» — ИИ (операция open) открывает «Склад» с этим товаром.
        ProductActionPlan.OpenProduct = product => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_navEntries.FirstOrDefault(x => x.Key == "warehouse") is not { } entry)
                return;
            entry.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_sections.FirstOrDefault(x => x.Key == "warehouse")?.Window is WarehouseWindow warehouse)
                warehouse.ShowProduct(product);
        });
        // 2026-10-06, владелец: «чтобы наш ИИ напрямую открывал программу и показывал наглядно изменения».
        ProductActionPlan.OpenSection = key => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_navEntries.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)) is { } entry)
                entry.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        ProductActionPlan.ShowChangedProducts = (keys, summary) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_navEntries.FirstOrDefault(x => x.Key == "warehouse") is not { } entry)
                return;
            entry.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (_sections.FirstOrDefault(x => x.Key == "warehouse")?.Window is WarehouseWindow warehouse)
                warehouse.ShowChanged(keys, summary);
        });
    }

    private void UpdateNavSearchLook()
    {
        var focused = NavSearchText.IsFocused;
        NavSearchBox.Bind(Border.BorderBrushProperty, this.GetResourceObservable(focused ? "BrushAccent" : "BrushBorder"));
        // Подсказка Ctrl+K — только пока поле пустое и не в фокусе (на телефоне клавиатуры нет).
        NavSearchKeyHint.IsVisible = !focused && string.IsNullOrEmpty(NavSearchText.Text) && !_phoneLayout;
    }

    private void NavSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // Первый найденный раздел — как нажатие на пункт меню; поиск очищается.
            var first = _navEntries.FirstOrDefault(x => x.Button.IsVisible);
            e.Handled = true;
            if (first == null || (NavSearchText.Text ?? "").Trim().Length == 0)
                return;
            NavSearchText.Text = "";
            first.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(NavSearchText.Text))
                NavSearchText.Text = "";
            else
                Focus();
        }
    }

    private void NavHotkey_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            FocusNavSearch();
        }
    }

    /// <summary>Ctrl+K в окне раздела — тоже к поиску (разделы — отдельные окна поверх правой части).</summary>
    private void AttachNavHotkey(Window section) =>
        section.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                FocusNavSearch();
            }
        }, RoutingStrategies.Tunnel);

    private void FocusNavSearch()
    {
        if (_phoneLayout)
            SetPhoneMenu(true);
        else if (UserPreferences.Instance.OwnerSidebarCollapsed)
            Collapse_Click(null, new RoutedEventArgs());
        Activate();
        NavSearchText.Focus();
        NavSearchText.SelectAll();
    }
}
