using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-18, редизайн программы владельца, этап О2 (макет design/NurMarket-design «OwnerNav»). Меню — две колонки:
/// • тёмная колонка групп (76): значок и полная подпись группы, выбранная — жёлтым; внизу «Тема», «Выйти» и пользователь;
/// • колонка пунктов выбранной группы (232): компания и тариф, пункты от 44 точек (выбранный — светло-жёлтый с полосой),
///   счётчики (красный — просрочено, серо-синий — ждёт), поиск «Найти раздел · Ctrl+K» внизу — по всем группам.
/// Группы и пункты, права, тариф и скрытые разделы — те же (BuildNavigation): группа, где всё скрыто, не показывается.
/// «««» сворачивает колонку пунктов (остаётся тёмная колонка; запоминается — UserPreferences.OwnerSidebarCollapsed, прежняя
/// настройка «меню только значками»). На экране до 1366 точек колонка сворачивается сама, когда открыт раздел (не «Сводка»).
/// В свёрнутом виде нажатие на группу раскрывает колонку с её пунктами; выбор пункта сворачивает её снова. Раздел — отдельное
/// окно поверх SectionHost, поэтому колонка не «наплывает» на него, а сдвигает (SyncSectionBounds после смены ширины).
/// Сворачивание групп заголовком (UserPreferences.OwnerNavCollapsedGroups, 2026-10-06) больше не используется — в колонке
/// пунктов одна группа за раз; поле настроек оставлено, чтобы не трогать файл настроек.</summary>
public partial class OwnerShellWindow
{
    private const double NavRailWidth = 76;
    private const double NavItemsWidth = 232;

    /// <summary>Шире этого (точек, без запаса развёрнутого окна) колонка пунктов сама не сворачивается.</summary>
    private const double NavAutoCollapseWidth = 1366;

    private sealed record NavGroup(string Key, string Title, Button? Rail, Ellipse? Dot, TextBlock Caption);

    private readonly List<NavGroup> _navGroups = new();

    /// <summary>Группа, чьи пункты показаны в колонке пунктов (выбрана в тёмной колонке или группа открытого раздела).</summary>
    private string? _navGroup;

    /// <summary>Колонка свёрнута, но владелец нажал группу (или Ctrl+K) — колонка раскрыта до выбора пункта.</summary>
    private bool _navPeek;

    private bool? _navOpenApplied;

    /// <summary>Значки групп в тёмной колонке — из тех же значков меню.</summary>
    private static string NavGroupIconKey(string group) => group switch
    {
        "home" => "HomeIcon",
        "products" => "WarehouseIcon",
        "sales" => "SalesIcon",
        "reports" => "FinanceIcon",
        "site" => "SiteSettingsIcon",
        "clients" => "RailClientsIcon",
        "manage" => "SettingsIcon",
        "help" => "HelpIcon",
        _ => "HomeIcon",
    };

    /// <summary>До 1366 точек, когда открыт раздел, колонка пунктов сворачивается сама (раздел получает место).</summary>
    private bool NavAutoCollapse
    {
        get
        {
            // 2026-10-18: сама колонка сворачивается только в новом виде (у бургера и старого вида своя разметка).
            if (_activeSection == null || _phoneLayout || MenuStyle != OwnerMenuStyles.New)
                return false;
            var width = Bounds.Width - OffScreenMargin.Left - OffScreenMargin.Right;
            return width > 0 && width <= NavAutoCollapseWidth;
        }
    }

    // 2026-10-19: у «Вкладок» колонка пунктов раскрывается только для поиска (_navPeek); OwnerSidebarCollapsed не читается.
    private bool NavItemsOpen =>
        _phoneLayout || BurgerMenu || _navPeek || (!TabsMenu && !(UserPreferences.Instance.OwnerSidebarCollapsed || NavAutoCollapse));

    /// <summary>Ширина меню и видимость колонки пунктов. Ширина SectionHost меняется — раздел сдвигается (SyncSectionBounds).</summary>
    private void ApplySidebarLayout()
    {
        // 2026-10-18, вид меню на выбор: «Бургер ☰» и «Старое» — своя разметка (OwnerShellWindow.MenuStyle.cs).
        if (ApplyOtherMenuLayout())
            return;
        var open = NavItemsOpen;
        NavItemsColumn.IsVisible = open;
        if (!_phoneLayout)
            RootGrid.ColumnDefinitions[0].Width = new GridLength(open ? NavRailWidth + NavItemsWidth : NavRailWidth);
        // 2026-10-19, «Вкладки»: вместо «»» — 🔍 поиска раздела (колонку пунктов сворачивать/разворачивать не нужно).
        ExpandNavButton.IsVisible = !open && !_phoneLayout && !TabsMenu;
        RailSearchButton.IsVisible = TabsMenu && !_phoneLayout;
        ApplyPhoneColumns();
        if (_navOpenApplied != open)
        {
            _navOpenApplied = open;
            Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Окно стало уже/шире 1366 — колонка пунктов сворачивается или раскрывается сама.</summary>
    private void OnShellSizeChangedForNav()
    {
        if (_navOpenApplied != NavItemsOpen)
            ApplySidebarLayout();
        ApplyRailDensity();
    }

    private string? _railDensity;

    /// <summary>2026-10-18, ревью О2: при 1366×768 и масштабе 125 % окно ~580 точек в высоту — «Управление» и «Помощь» уходили
    /// под низ тёмной колонки. Ниже 720 точек колонка плотнее (меньше отступы, логотип меньше), ниже 620 — только значки
    /// (подпись — во всплывающей подсказке, для диктора — AutomationProperties.Name, он задан всегда). Прокрутка — запасной вариант.</summary>
    private void ApplyRailDensity()
    {
        var height = Bounds.Height - OffScreenMargin.Top - OffScreenMargin.Bottom;
        // 2026-10-18: внизу колонки добавилась кнопка «Вид» (вид меню) — пороги выше на её высоту, и ступень «tight» между
        // плотной и «только значки»: на 1366×768 (окно ~728) колонка чуть плотнее, «Помощь» не уходит под «Тему».
        var density = _phoneLayout || height <= 0 || height >= 790 ? "normal"
            : height >= 712 ? "dense" : height >= 650 ? "tight" : "icons";
        if (density == _railDensity)
            return;
        _railDensity = density;
        var icons = density == "icons";
        NavRail.Classes.Set("dense", density != "normal");
        NavRail.Classes.Set("tight", density == "tight");
        NavRail.Classes.Set("iconsOnly", icons);
        RailPanel.Spacing = density == "normal" ? 4 : density == "dense" ? 3 : 1;
        LogoImage.Width = LogoImage.Height = density == "normal" ? 44 : 36;
        LogoGrid.Margin = density == "normal" ? new Thickness(0, 16, 0, 10) : new Thickness(0, 10, 0, 6);
        foreach (var group in _navGroups)
        {
            if (group.Rail is { } rail)
                ToolTip.SetTip(rail, icons ? group.Title : null);
        }
        // У «Темы» и «Выйти» подсказка есть всегда (ApplyTexts); имя для диктора — тоже.
    }

    private void Collapse_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-04, телефон: «««» в меню — закрыть меню на весь экран (OwnerShellWindow.Phone.cs).
        if (_phoneLayout)
        {
            SetPhoneMenu(false);
            return;
        }
        // 2026-10-18: в виде «Бургер ☰» «««» закрывает выехавшее меню.
        if (BurgerMenu)
        {
            SetBurgerOpen(false);
            return;
        }
        if (_navPeek || TabsMenu)
        {
            // Колонка была раскрыта нажатием на группу — просто свернуть обратно.
            // 2026-10-19: у «Вкладок» колонка — только поиск: ✕ закрывает её, настройку OwnerSidebarCollapsed не трогает.
            _navPeek = false;
        }
        else
        {
            var prefs = UserPreferences.Instance;
            prefs.OwnerSidebarCollapsed = true;
            prefs.SaveToDisk();
        }
        ApplySidebarLayout();
    }

    private void ExpandNav_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        if (prefs.OwnerSidebarCollapsed)
        {
            prefs.OwnerSidebarCollapsed = false;
            prefs.SaveToDisk();
        }
        // Свёрнута сама (до 1366 и открыт раздел) — раскрыть до выбора пункта.
        if (!NavItemsOpen)
            _navPeek = true;
        ApplySidebarLayout();
    }

    /// <summary>Нажатие на группу в тёмной колонке: показать её пункты (раздел не открывается).</summary>
    private void SelectNavGroup(string key)
    {
        // 2026-10-19, «Вкладки»: группа сразу открывает свою последнюю (или первую) вкладку (OwnerShellWindow.Tabs.cs).
        if (TabsMenu)
        {
            OpenTabsGroup(key);
            return;
        }
        if (!_phoneLayout)
        {
            if (!NavItemsOpen)
                _navPeek = true;
            else if (_navPeek && key == _navGroup)
                _navPeek = false;
        }
        _navGroup = key;
        if (!string.IsNullOrEmpty(NavSearchText.Text))
            NavSearchText.Text = "";
        ApplySidebarLayout();
        ApplyNavFilter();
    }

    private string? GroupOfKey(string key) => _navEntries.FirstOrDefault(x => x.Key == key)?.GroupKey;

    /// <summary>Группа появляется с первым видимым пунктом: подпись в колонке пунктов (видна при поиске) и кнопка в тёмной
    /// колонке. «Система» (выйти на рабочий стол) — без кнопки: «Выйти» внизу тёмной колонки, пункт находится поиском.</summary>
    private void AddNavGroup(string key, string title)
    {
        var caption = new TextBlock { Text = title, Classes = { "navCaption" }, IsVisible = false };
        // 2026-10-18: в старом виде над пунктами — заголовок группы, который её сворачивает (OwnerShellWindow.MenuStyle.cs).
        NavPanel.Children.Add(OldMenu ? OldNavGroupHeader(key, title) : caption);
        Button? rail = null;
        Ellipse? dot = null;
        if (key != "system")
        {
            var icon = new Path { Classes = { "railIcon" } };
            if (this.TryFindResource(NavGroupIconKey(key), out var resource) && resource is Geometry geometry)
                icon.Data = geometry;
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(icon);
            stack.Children.Add(new TextBlock { Text = title, Classes = { "railLabel" } });
            // Точка у значка: в группе есть счётчик (красная — просрочено) — видно и при свёрнутой колонке пунктов.
            dot = new Ellipse
            {
                Width = 8, Height = 8, IsVisible = false, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 16, 0),
            };
            var content = new Panel();
            content.Children.Add(stack);
            content.Children.Add(dot);
            rail = new Button { Classes = { "railGroup" }, Content = content };
            AutomationProperties.SetName(rail, title);
            rail.Click += (_, _) => SelectNavGroup(key);
            RailPanel.Children.Add(rail);
        }
        _navGroups.Add(new NavGroup(key, title, rail, dot, caption));
    }

    /// <summary>Пункт колонки пунктов: подпись (переносится, не обрезается) и, у «Заказов с сайта» и «Проката», счётчик справа.</summary>
    private Button NavItemButton(string key, string text, out Border? badge)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 10, 10, 10) };
        grid.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        badge = null;
        if (key is "siteorders" or "rentals")
        {
            var count = new TextBlock { FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Classes = { "num" } };
            badge = new Border { Child = count, IsVisible = false, IsHitTestVisible = false, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            SetNavBadgeTone(badge, urgent: false);
            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }
        var content = new Panel();
        content.Children.Add(new Border { Classes = { "navBar" } });
        content.Children.Add(grid);
        var button = new Button { Content = content, Classes = { "nav" } };
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Счётчик у пункта: красный — просрочено, серо-синий (второстепенный текст темы) — ждёт внимания.</summary>
    private void SetNavBadgeTone(Border badge, bool urgent)
    {
        badge.Tag = urgent ? "urgent" : null;
        // 2026-10-18: старый вид — число на плашке (красной — просрочено, акцент — ждёт), как в выпуске 1.17.58.
        if (badge.Classes.Contains("navBadge"))
        {
            UseBrush(badge, Border.BackgroundProperty, urgent ? "BrushDanger" : "BrushAccent");
            if (badge.Child is TextBlock pill)
            {
                if (urgent)
                    pill.Foreground = Brushes.White;
                else
                    UseBrush(pill, TextBlock.ForegroundProperty, "BrushAccentForeground");
            }
        }
        else if (badge.Child is TextBlock text)
            UseBrush(text, TextBlock.ForegroundProperty, urgent ? "BrushDanger" : "BrushTextMuted");
        UpdateRailDots();
    }

    /// <summary>Точки у групп тёмной колонки — по видимым счётчикам пунктов группы.</summary>
    private void UpdateRailDots()
    {
        foreach (var group in _navGroups)
        {
            if (group.Dot is not { } dot)
                continue;
            var badges = _navEntries
                .Where(x => x.GroupKey == group.Key)
                .Select(x => (x.Key == "siteorders" ? _siteOrdersBadge : x.Key == "rentals" ? _rentalsBadge : null))
                .Where(b => b is { IsVisible: true })
                .ToList();
            dot.IsVisible = badges.Count > 0;
            UseBrush(dot, Shape.FillProperty, badges.Any(b => Equals(b!.Tag, "urgent")) ? "BrushDanger" : "OwnerRailFg");
        }
        // 2026-10-19, «Вкладки»: числа у вкладок «Заказы с сайта» и «Прокат» — те же, что у пунктов.
        UpdateTabsBar();
    }

    // ------------------------------------------------------------------ цвета тёмной колонки

    private string? _railBrushKey;
    private bool _railBrushPending;

    private void QueueRailBrushes()
    {
        if (_railBrushPending)
            return;
        _railBrushPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _railBrushPending = false;
            ApplyRailBrushes();
        }, DispatcherPriority.Background);
    }

    private Color ThemeColor(string key, string fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is ISolidColorBrush brush
            ? brush.Color
            : Color.Parse(fallback);

    /// <summary>Цвета тёмной колонки из темы, без своих цветов у каждой темы: светлые темы — цвет текста темы (#0B2A3C у
    /// NurMarket, тёмно-синий у «Классической»), тёмные — темнее фона окна. Выбранная группа — акцент темы на 16 % заливке,
    /// а если акцент на колонке плохо читается (контраст меньше 3:1) — сплошной акцент с его цветом текста.</summary>
    private void ApplyRailBrushes()
    {
        var text = ThemeColor("BrushText", "#0B2A3C");
        var window = ThemeColor("BrushWindow", "#111827");
        var accent = ThemeColor("BrushAccent", "#FFD600");
        var accentFg = ThemeColor("BrushAccentForeground", "#0B2A3C");

        var bg = Luminance(text) < 0.3 ? text : Mix(window, Colors.Black, 0.3);
        if (Luminance(bg) > 0.08)
            bg = Mix(bg, Colors.Black, 0.5);
        bg = Color.FromRgb(bg.R, bg.G, bg.B);
        var fg = Mix(Colors.White, bg, 0.78);
        Color activeBg, activeFg;
        // 2026-10-18, ревью О2: контраст — против заливки выбранной группы (16 % акцента поверх колонки) и с порогом 4,5:1
        // (подпись 10,5 — мелкий текст).
        if (ContrastRatio(accent, Mix(accent, bg, 0.16)) >= 4.5)
        {
            activeBg = Color.FromArgb(0x29, accent.R, accent.G, accent.B);
            activeFg = accent;
        }
        else
        {
            activeBg = accent;
            activeFg = accentFg;
        }

        // 2026-10-18, вид «Бургер ☰»: затемнение под выехавшим меню — в тёмной теме чёрное плотнее, в светлой — тёмно-синее.
        var scrim = Luminance(window) < 0.2 ? Color.FromArgb(0xA6, 0, 0, 0) : Color.FromArgb(0x73, 0x0B, 0x1A, 0x26);
        var key = $"{bg}|{fg}|{activeBg}|{activeFg}|{scrim}";
        if (key == _railBrushKey)
            return;
        _railBrushKey = key;
        Resources["OwnerRailBg"] = new SolidColorBrush(bg);
        Resources["OwnerRailFg"] = new SolidColorBrush(fg);
        Resources["OwnerRailHoverBg"] = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        Resources["OwnerRailHoverFg"] = new SolidColorBrush(Colors.White);
        Resources["OwnerRailActiveBg"] = new SolidColorBrush(activeBg);
        Resources["OwnerRailActiveFg"] = new SolidColorBrush(activeFg);
        Resources["OwnerRailAvatarBg"] = new SolidColorBrush(Colors.White);
        Resources["OwnerRailAvatarFg"] = new SolidColorBrush(bg);
        Resources["OwnerScrim"] = new SolidColorBrush(scrim);
    }

    private static Color Mix(Color a, Color b, double share) => Color.FromRgb(
        (byte)Math.Round(a.R * share + b.R * (1 - share)),
        (byte)Math.Round(a.G * share + b.G * (1 - share)),
        (byte)Math.Round(a.B * share + b.B * (1 - share)));

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
