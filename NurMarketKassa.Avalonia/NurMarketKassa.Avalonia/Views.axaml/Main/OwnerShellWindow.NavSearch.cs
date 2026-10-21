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
/// • заголовок группы сворачивал и разворачивал её — 2026-10-18, этап О2: меню в две колонки (OwnerShellWindow.Nav.cs),
///   в колонке пунктов одна группа за раз, поиск — внизу колонки и по всем группам; при свёрнутой колонке Ctrl+K раскрывает её.</summary>
public partial class OwnerShellWindow
{
    private sealed record NavEntry(string Key, string Title, string? GroupKey, Button Button, Action Open);

    private readonly List<NavEntry> _navEntries = new();
    private TextBlock? _navNoMatch;
    private bool _navSearchWired;

    /// <summary>Привычные слова, по которым ищут раздел (кроме названия и группы).</summary>
    private static readonly Dictionary<string, string> NavKeywords = new()
    {
        // 2026-10-07: кыргызские слова — «кампаны ач», «карыздарды көрсөт» в разговоре с Нур Советником (NavigateByWords).
        // 2026-10-06: советник теперь «Нур Советник» / «Нур Кеңешчи» — по старым словам тоже находится.
        ["aiadvisor"] = "ии ai советник кеңешчи нур помощник",
        ["warehouse"] = "товар остаток остатки приёмка приемка ревизия инвентаризация списание перемещение этикетка ценник штрихкод кампа кампаны товарлар калдык",
        ["calculator"] = "наценка цена себестоимость",
        ["restock"] = "заказать закупка срок годности просрочка толуктоо мөөнөт",
        // 2026-10-20, этап П1: раздел «Поставщики» — и по-кыргызски, и по-английски.
        ["suppliers"] = "поставщик поставщики поставщику долг поставщику закупка закупки жеткирүүчү жеткирүүчүлөр supplier suppliers tedarikçi yetkazib",
        ["supplierreturns"] = "поставщик брак",
        ["branches"] = "филиал перемещение склад филиалдар",
        // 2026-10-18: «Продажи» и «Финансы» — один пункт (OwnerShellWindow.SalesFinance.cs): слова обоих + названия частей.
        ["salesfinance"] = "продажи финансы чек чеки история возврат возвраты сатуу сатуулар выручка касса смена смены отчёт каржы чектер",
        ["debts"] = "долг долги рассрочка должник карыз карыздар карызкор",
        ["rentals"] = "прокат аренда ижара",
        ["analytics"] = "отчёт сезонность график аналитика талдоо",
        ["abc"] = "abc авс",
        ["profitcash"] = "прибыль доход расход деньги пайда киреше акча",
        ["losssales"] = "убыток скидка минус",
        ["sizesreport"] = "размер цвет одежда",
        ["siteorders"] = "заказ интернет магазин",
        ["clients"] = "покупатель бонус баллы кардар кардарлар клиент",
        ["telegrambot"] = "бот телеграм телеграм",
        ["salary"] = "сотрудник кассир зарплата эмгек айлык кызматкер табель",
        ["marketplace"] = "купить функция тариф",
        ["settings"] = "принтер весы язык тема тариф аккаунт жөндөө жөндөөлөр",
        ["support"] = "помощь удалённо", // 2026-10-16: удалённая помощь — своя «Нур Поддержка»
        ["logs"] = "ошибка журнал",
    };

    private static bool NavMatches(NavEntry entry, string groupTitle, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = entry.Title + " " + groupTitle + " " + (NavKeywords.TryGetValue(entry.Key, out var extra) ? extra : "");
        return words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Что видно в колонке пунктов: без поиска — пункты выбранной группы; при поиске (Ctrl+K) — найденное во ВСЕХ
    /// группах, с подписью группы над найденным. 2026-10-18, этап О2: группы сворачивались заголовком — теперь одна группа за раз.</summary>
    private void ApplyNavFilter()
    {
        WireNavSearch();
        NavSearchText.Watermark = Tr.T("Найти раздел", "Бөлүмдү табуу", "Find a section", "Bölüm bul", "Bo'limni topish");
        var query = (NavSearchText.Text ?? "").Trim();
        var filtering = query.Length > 0;
        // 2026-10-18: старый вид — все группы списком, свёрнутые заголовком (OwnerShellWindow.MenuStyle.cs).
        if (OldMenu)
        {
            ShowNavNoMatch(ApplyOldNavVisibility(query) == 0);
            UpdateTabsBar(); // 2026-10-19: полоса вкладок прячется (вид сменили на «Старое»)
            return;
        }
        if (_navGroup is null || !_navGroups.Any(g => g.Key == _navGroup && g.Rail != null))
            _navGroup = GroupOfKey(_activeSection?.Key ?? "overview") ?? _navGroups.FirstOrDefault(g => g.Rail != null)?.Key;
        var groupsWithItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shown = 0;
        foreach (var entry in _navEntries)
        {
            var groupTitle = _navGroups.FirstOrDefault(g => g.Key == entry.GroupKey)?.Title ?? "";
            var visible = filtering ? NavMatches(entry, groupTitle, query) : entry.GroupKey == _navGroup;
            entry.Button.IsVisible = visible;
            if (visible)
            {
                shown++;
                if (entry.GroupKey != null)
                    groupsWithItems.Add(entry.GroupKey);
            }
        }
        foreach (var group in _navGroups)
        {
            group.Caption.IsVisible = filtering && groupsWithItems.Contains(group.Key);
            group.Rail?.Classes.Set("active", group.Key == _navGroup);
        }
        var current = _navGroups.FirstOrDefault(g => g.Key == _navGroup);
        NavGroupTitle.Text = current?.Title ?? "";
        NavGroupTitle.IsVisible = !filtering && current != null;
        ShowNavNoMatch(filtering && shown == 0);
        // 2026-10-19, вид «Вкладки»: вкладки группы открытого раздела (OwnerShellWindow.Tabs.cs).
        UpdateTabsBar();
    }

    private void ShowNavNoMatch(bool show)
    {
        if (_navNoMatch == null || !NavPanel.Children.Contains(_navNoMatch))
        {
            _navNoMatch = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 14, 8, 0) };
            _navNoMatch.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            NavPanel.Children.Add(_navNoMatch);
        }
        _navNoMatch.Text = Tr.T("Такого раздела нет. Попробуйте другое слово.", "Мындай бөлүм жок. Башка сөздү байкап көрүңүз.",
            "No such section. Try another word.", "Böyle bir bölüm yok. Başka bir kelime deneyin.", "Bunday bo'lim yo'q. Boshqa so'zni sinab ko'ring.");
        _navNoMatch.IsVisible = show;
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
            // 2026-10-18: «sales» и «finance» советника — объединённый раздел «Продажи и финансы» (вкладка «Чеки»).
            if (key is not null && (key.Equals("sales", StringComparison.OrdinalIgnoreCase) || key.Equals("finance", StringComparison.OrdinalIgnoreCase)))
                key = SalesFinanceKey;
            if (_navEntries.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)) is { } entry)
            {
                entry.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (entry.Key == SalesFinanceKey)
                    OpenSalesFinance("checks");
            }
        });
        ProductActionPlan.NavigateByWords = NavigateByWords;
        // 2026-10-07, владелец: «открывай вкладки только после команды открыть» — после «Выполнить» склад сам не открывается:
        // итог — в чате советника, а отметка «Изменено ИИ» ставится на складе, если он уже открыт (увидите, когда перейдёте).
        ProductActionPlan.ShowChangedProducts = (keys, summary) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_sections.FirstOrDefault(x => x.Key == "warehouse")?.Window is WarehouseWindow warehouse)
                warehouse.ShowChanged(keys, summary);
        });
    }

    /// <summary>2026-10-06, владелец: «дай ИИ звонку полный доступ к программе — открывать вкладки и разделы». Раздел меню по
    /// словам фразы: название пункта (вес 2) и привычные слова NavKeywords (вес 1), по основе слова (первые 5 букв), чтобы
    /// «складе», «зарплату», «финансах» узнавались. Открывает найденный пункт, как щелчок в меню; ответ — его название.</summary>
    private string? NavigateByWords(string phrase, bool open)
    {
        static string Stem(string w) => w.Length > 5 ? w[..5] : w;
        static List<string> Words(string s) => System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3).ToList();
        var skip = new HashSet<string> { "откро", "откры", "перей", "зайди", "покаж", "разде", "вклад", "экран", "прогр", "пожал", "давай", "тепер", "прове", "фото", "ачып", "ачкыл" };
        var said = Words(phrase).Select(Stem).Where(w => !skip.Contains(w)).Distinct().ToList();
        if (said.Count == 0)
            return null;
        static bool Same(string a, string b) => a.Length >= 4 && b.Length >= 4 && (a.StartsWith(Stem(b)) || b.StartsWith(a));
        NavEntry? best = null;
        var bestScore = 0;
        foreach (var entry in _navEntries)
        {
            var title = Words(entry.Title).Select(Stem).ToList();
            var extra = NavKeywords.TryGetValue(entry.Key, out var k) ? Words(k).Select(Stem).ToList() : new List<string>();
            var score = said.Sum(w => title.Any(t => Same(w, t)) ? 2 : extra.Any(x => Same(w, x)) ? 1 : 0);
            if (score > bestScore)
                (best, bestScore) = (entry, score);
        }
        if (best is null || !open)
            return best?.Title;
        best.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // 2026-10-18: «Продажи и финансы» — на вкладке по словам фразы («смены», «возвраты», «история»; иначе «Чеки»).
        if (best.Key == SalesFinanceKey)
            OpenSalesFinance(SalesFinanceTabFromWords(phrase));
        PosLogger.Log($"Owner app: ИИ-советник открыл раздел «{best.Title}» по фразе владельца.", "UI");
        return best.Title;
    }

    private void UpdateNavSearchLook()
    {
        var focused = NavSearchText.IsFocused;
        // 2026-10-18, этап О2: рамка в фокусе — BrushFocusRing (жёлтая по белому почти не видна, контраст ~1,4:1).
        NavSearchBox.Bind(Border.BorderBrushProperty, this.GetResourceObservable(focused ? "BrushFocusRing" : "BrushBorder"));
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
            {
                Focus();
                // 2026-10-18, этап О2: колонка была раскрыта для поиска (Ctrl+K при свёрнутой) — свернуть обратно.
                // Вид «Бургер ☰»: Esc при пустом поиске закрывает меню (окно-обработчик в OwnerShellWindow.MenuStyle.cs).
                SetBurgerOpen(false);
                if (_navPeek)
                {
                    _navPeek = false;
                    ApplySidebarLayout();
                }
            }
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
        else if (BurgerMenu)
            SetBurgerOpen(true); // 2026-10-18: вид «Бургер ☰» — меню выезжает, поиск в нём
        else if (OldMenu)
        {
            // 2026-10-18: старый вид «только значки» — развернуть с подписями (поиска в значках нет), как в выпуске 1.17.58.
            if (UserPreferences.Instance.OwnerSidebarCollapsed)
                OldCollapse_Click(null, new RoutedEventArgs());
        }
        else if (!NavItemsOpen)
        {
            // 2026-10-18, этап О2: колонка пунктов свёрнута — раскрыть её до выбора раздела (поиск внизу колонки).
            _navPeek = true;
            ApplySidebarLayout();
        }
        Activate();
        NavSearchText.Focus();
        NavSearchText.SelectAll();
    }
}
