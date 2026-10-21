using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-18, владелец: «вид меню на выбор» — три вида меню программы владельца, выбираются как тема: кнопка «Вид»
/// внизу меню (рядом с «Тема») и Настройки → Экран, карточками с рисунком (OwnerMenuStylePicker). Личная настройка
/// UserPreferences.OwnerMenuStyle. Во всех видах одни и те же пункты, права, тариф, «Разделы меню», Ctrl+K, выбранный пункт и
/// SyncSectionBounds — меняется только, где и как меню нарисовано:
/// • «Новое» — тёмная колонка групп и колонка пунктов (этап О2, OwnerShellWindow.Nav.cs);
/// • «Бургер ☰» — колонка меню шириной 0, раздел на всю ширину; ☰ в шапке открывает то же меню О2 (группы + пункты) поверх
///   раздела с затемнением. Раздел — отдельное окно поверх SectionHost, поэтому на время меню оно прячется, а в SectionHost
///   виден его снимок. Esc, щелчок мимо и выбор пункта закрывают меню;
/// • «Старое» — одна светлая колонка выпуска 1.17.58 (OldNavSidebar): логотип и ☰ «только значки», карточка компании, поиск
///   вверху, все группы списком (заголовок группы сворачивает её — UserPreferences.OwnerNavCollapsedGroups), пункты со значками.
///   Поле поиска и список пунктов — те же элементы, что в новом виде (переносятся сюда). Плашки «Прокат» в меню нет (решение
///   владельца — она в «Требует внимания» и над разделом);
/// • «Вкладки» (2026-10-19) — тёмная колонка групп, пункты группы — вкладками над разделом (OwnerShellWindow.Tabs.cs).
/// На телефоне (OwnerShellWindow.Phone.cs) всегда новый вид — меню на весь экран по ☰.</summary>
public partial class OwnerShellWindow
{
    private const double OldNavWidth = 252;
    private const double OldNavIconsWidth = 72;

    /// <summary>Вид меню на этом экране: на телефоне — всегда новый (меню на весь экран).</summary>
    private string MenuStyle => _phoneLayout ? OwnerMenuStyles.New : OwnerMenuStyles.Current;

    private bool BurgerMenu => MenuStyle == OwnerMenuStyles.Burger;
    private bool OldMenu => MenuStyle == OwnerMenuStyles.Old;

    /// <summary>2026-10-19: вид «Вкладки» — тёмная колонка групп, пункты группы вкладками над разделом (OwnerShellWindow.Tabs.cs).</summary>
    private bool TabsMenu => MenuStyle == OwnerMenuStyles.Tabs;

    /// <summary>Тёмная колонка групп стоит слева всегда: «Новое» и «Вкладки».</summary>
    private bool RailMenu => MenuStyle is OwnerMenuStyles.New or OwnerMenuStyles.Tabs;

    /// <summary>Колонка NavSidebar (тёмная колонка + пункты) видна: новый вид, «Вкладки» или выехавшее меню бургера.</summary>
    private bool NavSidebarShown => RailMenu || (BurgerMenu && _burgerOpen);

    private string? _placedMenuStyle;
    private bool _burgerOpen;
    private readonly Dictionary<string, (Button Header, Path Chevron)> _oldGroupHeaders = new();

    /// <summary>Из конструктора: смена вида (окно выбора, Настройки, облако) и Esc для выехавшего меню.</summary>
    private void AttachMenuStyle()
    {
        void OnStyleChanged() => Dispatcher.UIThread.Post(ApplyMenuStyle);
        OwnerMenuStyles.Changed += OnStyleChanged;
        SettingsCloudSync.Applied += OnStyleChanged;
        Closed += (_, _) =>
        {
            OwnerMenuStyles.Changed -= OnStyleChanged;
            SettingsCloudSync.Applied -= OnStyleChanged;
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            // Esc при непустом поиске сначала очищает поиск (NavSearch_KeyDown), при пустом — закрывает меню.
            if (e.Key == Key.Escape && _burgerOpen && string.IsNullOrEmpty(NavSearchText.Text))
            {
                e.Handled = true;
                SetBurgerOpen(false);
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Вид сменили — перестроить меню, не трогая открытый раздел (его окно встаёт по новой ширине SectionHost).</summary>
    private void ApplyMenuStyle()
    {
        if (MenuStyle == _placedMenuStyle)
            return;
        // ApplyTexts → BuildNavigation → EnsureMenuStyleParts: подписи, пункты и разметка под новый вид.
        ApplyTexts();
        AdjustHeaderForPhone(_activeSection == null);
        FitHeaderTools();
        Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
    }

    /// <summary>В начале BuildNavigation: поиск и список пунктов — в колонку своего вида, меню бургера — поверх правой части.</summary>
    private void EnsureMenuStyleParts()
    {
        var style = MenuStyle;
        if (style == _placedMenuStyle)
            return;
        _placedMenuStyle = style;
        CloseBurgerQuietly();
        // 2026-10-19: кнопки окна и плашка проката — в полосу вкладок или обратно в шапку (до ☰ бургера: она сдвигает колонки шапки).
        PlaceTabsParts(style == OwnerMenuStyles.Tabs);
        _navPeek = false; // раскрытая для поиска колонка пунктов прежнего вида не переходит в новый

        var old = style == OwnerMenuStyles.Old;
        if (old && NavSearchBox.Parent != OldNavSearchSlot)
        {
            NavItemsDock.Children.Remove(NavSearchBox);
            NavSearchBox.Margin = default;
            OldNavSearchSlot.Child = NavSearchBox;
            NavItemsScroll.Content = null;
            NavPanel.Margin = new Thickness(12, 2, 12, 12);
            OldNavScroll.Content = NavPanel;
        }
        else if (!old && NavSearchBox.Parent != NavItemsDock)
        {
            OldNavSearchSlot.Child = null;
            NavSearchBox.Margin = new Thickness(0, 10, 0, 0);
            // На прежнее место: после карточки компании (порядок детей DockPanel важен).
            NavItemsDock.Children.Insert(Math.Min(1, NavItemsDock.Children.Count), NavSearchBox);
            OldNavScroll.Content = null;
            NavPanel.Margin = default;
            NavItemsScroll.Content = NavPanel;
        }
        if (!old)
            NavSearchBox.IsVisible = true;
        // 2026-10-19, «Вкладки»: колонка пунктов раскрывается только для поиска (значок 🔍 / Ctrl+K) — поле сверху, под компанией.
        if (NavSearchBox.Parent == NavItemsDock)
        {
            var tabs = style == OwnerMenuStyles.Tabs;
            DockPanel.SetDock(NavSearchBox, tabs ? Dock.Top : Dock.Bottom);
            NavSearchBox.Margin = tabs ? new Thickness(0, 0, 0, 10) : new Thickness(0, 10, 0, 0);
        }
        OldNavSidebar.IsVisible = old;

        // Бургер: NavSidebar во всю высоту поверх правой части, слева, с тенью; в остальных видах — на своём месте.
        var burger = style == OwnerMenuStyles.Burger;
        Grid.SetColumnSpan(NavSidebar, burger ? 2 : 1);
        NavSidebar.HorizontalAlignment = burger ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        NavSidebar.Width = burger ? NavRailWidth + NavItemsWidth : double.NaN;
        NavSidebar.ZIndex = burger ? 31 : 0;
        NavSidebar.BoxShadow = burger ? BoxShadows.Parse("8 0 32 0 #47000000") : default;
        // Тень рисуется по фону рамки — у выехавшего меню фон карточки (в остальных видах фона нет, как было).
        if (burger)
            UseBrush(NavSidebar, Border.BackgroundProperty, "BrushPanel");
        else
            NavSidebar.ClearValue(Border.BackgroundProperty);
        if (!burger)
        {
            NavSidebar.Transitions = null;
            NavSidebar.RenderTransform = null;
        }

        // ☰ в шапке — у бургера и на телефоне (кнопку создаёт OwnerShellWindow.Phone.cs).
        if (burger || _phoneLayout)
            EnsurePhoneMenuButton();
        if (_phoneMenuButton is not null)
        {
            _phoneMenuButton.IsVisible = burger || _phoneLayout;
            _phoneMenuButton.Width = _phoneMenuButton.Height = _phoneLayout ? 44 : 38;
            _phoneMenuButton.Margin = new Thickness(0, 0, _phoneLayout ? 6 : 10, 0);
            var menuTip = Tr.T("Меню (Ctrl+K — найти раздел)", "Меню (Ctrl+K — бөлүмдү табуу)", "Menu (Ctrl+K — find a section)",
                "Menü (Ctrl+K — bölüm bul)", "Menyu (Ctrl+K — bo'limni topish)");
            ToolTip.SetTip(_phoneMenuButton, menuTip);
            AutomationProperties.SetName(_phoneMenuButton, Tr.T("Меню", "Меню", "Menu", "Menü", "Menyu"));
        }
        // У выехавшего меню вместо «««» — ✕ «Закрыть меню» (подсказка — ApplyMenuStyleTexts).
        // 2026-10-19: у «Вкладок» колонка раскрывается только для поиска — тоже ✕ «Закрыть» (свернуть «««» здесь нечего).
        if (CollapseButton.Content is Path collapseIcon && this.TryFindResource(burger || style == OwnerMenuStyles.Tabs ? "CloseIcon" : "CollapseNavIcon", out var iconData) && iconData is Geometry iconGeometry)
            collapseIcon.Data = iconGeometry;
        PosLogger.Log($"Owner app: вид меню «{style}».", "UI");
    }

    /// <summary>Разметка бургера и старого вида (из ApplySidebarLayout). false — новый вид или телефон: разметка О2.</summary>
    private bool ApplyOtherMenuLayout()
    {
        // 2026-10-19: «Вкладки» — разметка О2 (тёмная колонка 76; колонка пунктов — только при поиске).
        if (_phoneLayout || RailMenu)
            return false;
        var columns = RootGrid.ColumnDefinitions;
        columns[1].Width = new GridLength(1, GridUnitType.Star);
        ExpandNavButton.IsVisible = false;
        if (BurgerMenu)
        {
            columns[0].Width = new GridLength(0);
            NavItemsColumn.IsVisible = true;
            NavSidebar.IsVisible = _burgerOpen;
            BurgerScrim.IsVisible = _burgerOpen;
        }
        else
        {
            // Старый вид: 252 с подписями или 72 «только значки» (☰ у логотипа — прежняя настройка OwnerSidebarCollapsed).
            var collapsed = UserPreferences.Instance.OwnerSidebarCollapsed;
            columns[0].Width = new GridLength(collapsed ? OldNavIconsWidth : OldNavWidth);
            NavSidebar.IsVisible = false;
            BurgerScrim.IsVisible = false;
            OldLogoImage.IsVisible = !collapsed;
            OldLogoTextPanel.IsVisible = !collapsed;
            OldLogoGrid.Margin = collapsed ? new Thickness(0, 18, 0, 14) : new Thickness(18, 18, 12, 14);
            OldLogoGrid.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
            OldCompanyCard.IsVisible = !collapsed;
            OldUserInfoPanel.IsVisible = !collapsed;
            OldMenuStyleButton.IsVisible = !collapsed;
            OldThemeButton.IsVisible = !collapsed;
            OldExitButton.IsVisible = !collapsed;
            OldUserFooter.Padding = collapsed ? new Thickness(16, 12) : new Thickness(14, 12);
            NavSearchBox.IsVisible = !collapsed;
        }
        _navOpenApplied = NavItemsOpen;
        Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
        return true;
    }

    // ------------------------------------------------------------------ бургер ☰

    /// <summary>☰ в шапке: на телефоне — меню на весь экран, в виде «Бургер» — меню поверх раздела.</summary>
    private void MenuButton_Click()
    {
        if (_phoneLayout)
            SetPhoneMenu(true);
        else
            SetBurgerOpen(!_burgerOpen);
    }

    private void SetBurgerOpen(bool open)
    {
        if (open == _burgerOpen || (open && !BurgerMenu))
            return;
        _burgerOpen = open;
        if (open)
        {
            // Окно раздела лежит поверх окна программы — прячем его, а в SectionHost показываем снимок под затемнением.
            TakeSectionSnapshot();
            if (GroupOfKey(_activeSection?.Key ?? "overview") is { } group)
                _navGroup = group;
            if (!string.IsNullOrEmpty(NavSearchText.Text))
                NavSearchText.Text = "";
            ApplySidebarLayout();
            ApplyNavFilter();
            AnimateBurgerIn();
            Activate();
            return;
        }
        ApplySidebarLayout();
        RestoreSectionAfterBurger();
    }

    /// <summary>Выбор пункта в выехавшем меню: меню закрывается, снимок остаётся до показа раздела. true — меню было открыто.</summary>
    private bool CloseBurgerForNavigation()
    {
        if (!_burgerOpen)
            return false;
        _burgerOpen = false;
        ApplySidebarLayout();
        return true;
    }

    /// <summary>После выбора пункта: раздел не сменился (нет прав, внешнее окно) — вернуть прежний раздел на место.</summary>
    private void RestoreSectionAfterBurger()
    {
        if (_activeSection is { } section && !section.Window.IsVisible && !_burgerOpen)
            ShowSection(section);
        ClearSectionSnapshot();
    }

    /// <summary>Меню закрывается без возврата раздела (ShowSection сам покажет нужный; смена вида).</summary>
    private void CloseBurgerQuietly()
    {
        ClearSectionSnapshot();
        if (!_burgerOpen)
            return;
        _burgerOpen = false;
        NavSidebar.IsVisible = RailMenu;
        BurgerScrim.IsVisible = false;
    }

    private void BurgerScrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        SetBurgerOpen(false);
    }

    /// <summary>Меню выезжает слева за 0,18 с, затемнение проявляется (без анимации — в режиме слабого компьютера).</summary>
    private void AnimateBurgerIn()
    {
        if (UserPreferences.Instance.LowPerformanceMode)
            return;
        var duration = TimeSpan.FromMilliseconds(180);
        NavSidebar.Transitions = null;
        BurgerScrim.Transitions = null;
        NavSidebar.RenderTransform = TransformOperations.Parse($"translateX(-{NavRailWidth + NavItemsWidth}px)");
        BurgerScrim.Opacity = 0;
        NavSidebar.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = new CubicEaseOut() },
        };
        BurgerScrim.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration } };
        Dispatcher.UIThread.Post(() =>
        {
            NavSidebar.RenderTransform = TransformOperations.Identity;
            BurgerScrim.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    /// <summary>Снимок окна раздела в SectionHost и само окно — спрятать (на время выехавшего меню).</summary>
    private void TakeSectionSnapshot()
    {
        if (_activeSection is not { } section || !section.Window.IsVisible)
            return;
        var window = section.Window;
        try
        {
            var scale = window.RenderScaling;
            var size = window.ClientSize;
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
            var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            ClearSectionSnapshot();
            SectionSnapshot.Source = bitmap;
            SectionSnapshot.IsVisible = true;
        }
        catch (Exception ex)
        {
            // Без снимка под затемнением просто фон окна — меню работает так же.
            PosLogger.Log($"Owner app: снимок раздела под меню не получился ({ex.Message}).", "WARNING");
        }
        window.Hide();
    }

    private void ClearSectionSnapshot()
    {
        if (SectionSnapshot.Source is IDisposable bitmap)
        {
            SectionSnapshot.Source = null;
            bitmap.Dispose();
        }
        SectionSnapshot.IsVisible = false;
    }

    // ------------------------------------------------------------------ «Вид» и старый вид

    /// <summary>«Вид» внизу меню: окно с тремя карточками видов (меняется сразу).</summary>
    private async void MenuStyle_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Из выехавшего меню: сначала закрыть его и вернуть раздел, потом окно выбора — поверх раздела.
            SetBurgerOpen(false);
            await new OwnerMenuStyleDialog().ShowDialog(this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: окно «Вид меню» не открылось: {ex.Message}", "WARNING");
        }
    }

    /// <summary>☰ у логотипа старого вида: только значки / с подписями (как в выпуске 1.17.58).</summary>
    private void OldCollapse_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        prefs.OwnerSidebarCollapsed = !prefs.OwnerSidebarCollapsed;
        prefs.SaveToDisk();
        BuildNavigation();
        Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
    }

    /// <summary>Подписи старого вида и кнопки «Вид» (из ApplyTexts).</summary>
    private void ApplyMenuStyleTexts()
    {
        MenuStyleLabel.Text = Tr.T("Вид", "Көрүнүш", "Layout", "Görünüm", "Ko'rinish");
        // 2026-10-19: добавлен четвёртый вид «Вкладки».
        var styleTip = Tr.T("Вид меню: новое, вкладки, бургер ☰ или старое", "Менюнун көрүнүшү: жаңы, өтмөктөр, бургер ☰ же эски",
            "Menu layout: new, tabs, burger ☰ or classic", "Menü görünümü: yeni, sekmeler, hamburger ☰ veya klasik",
            "Menyu ko'rinishi: yangi, varaqlar, burger ☰ yoki eski");
        foreach (var button in new Control[] { MenuStyleButton, OldMenuStyleButton })
        {
            ToolTip.SetTip(button, styleTip);
            AutomationProperties.SetName(button, styleTip);
        }

        OwnerBadgeText.Text = Tr.T("Владелец", "Ээси", "Owner", "İşletme sahibi", "Egasi");
        OldCompanyNameText.Text = CompanyNameText.Text;
        OldTariffText.Text = TariffText.Text;
        OldTariffText.IsVisible = TariffText.IsVisible;
        ToolTip.SetTip(OldCompanyCard, ToolTip.GetTip(CompanyCard));
        var name = PosApp.CurrentUserDisplayName ?? "";
        OldUserNameText.Text = string.IsNullOrWhiteSpace(name) ? Tr.T("Пользователь", "Колдонуучу", "User", "Kullanıcı", "Foydalanuvchi") : name;
        OldUserRoleText.Text = Tr.T("Вход через NurCRM", "NurCRM аркылуу кирүү", "Signed in via NurCRM", "NurCRM ile giriş", "NurCRM orqali kirish");
        // 2026-10-20, живой аудит (меню): в узкой колонке имя и «Вход через …» обрезались — полный текст в подсказке.
        ToolTip.SetTip(OldUserInfoPanel, OldUserNameText.Text + "\n" + OldUserRoleText.Text);
        OldAvatarText.Text = AvatarText.Text;
        ToolTip.SetTip(OldAvatarBadge, ToolTip.GetTip(AvatarBadge));
        AutomationProperties.SetName(OldAvatarBadge, AutomationProperties.GetName(AvatarBadge));
        foreach (var (button, source) in new[] { (OldThemeButton, ThemeButton), (OldExitButton, ExitButton) })
        {
            ToolTip.SetTip(button, ToolTip.GetTip(source));
            AutomationProperties.SetName(button, AutomationProperties.GetName(source));
        }
        var collapseTip = Tr.T("Свернуть / развернуть меню", "Менюну жыйноо / ачуу", "Collapse / expand menu", "Menüyü daralt / genişlet", "Menyuni yig'ish / yoyish");
        ToolTip.SetTip(OldCollapseButton, collapseTip);
        AutomationProperties.SetName(OldCollapseButton, collapseTip);
        if (BurgerMenu || TabsMenu)
        {
            var closeTip = Tr.T("Закрыть меню (Esc)", "Менюну жабуу (Esc)", "Close the menu (Esc)", "Menüyü kapat (Esc)", "Menyuni yopish (Esc)");
            ToolTip.SetTip(CollapseButton, closeTip);
            AutomationProperties.SetName(CollapseButton, closeTip);
        }
        // 2026-10-19, «Вкладки»: 🔍 под логотипом и «Ещё ▾» (OwnerShellWindow.Tabs.cs).
        ApplyTabsTexts();
    }

    /// <summary>Пункт старого вида: значок, подпись (длинная — второй строкой), число у «Заказов с сайта» и «Проката».
    /// «Только значки» — без подписи, число над значком.</summary>
    private Button OldNavItemButton(string key, string iconKey, string text, out Border? badge)
    {
        var collapsed = UserPreferences.Instance.OwnerSidebarCollapsed;
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (this.TryFindResource(iconKey, out var icon) && icon is Geometry geometry)
            content.Children.Add(new Path { Data = geometry, Classes = { "navIcon" } });
        var label = new TextBlock
        {
            Text = text,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = !collapsed,
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        badge = null;
        if (key is "siteorders" or "rentals")
        {
            content.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            badge = new Border { Classes = { "navBadge" }, Child = new TextBlock(), IsVisible = false };
            if (collapsed)
            {
                badge.HorizontalAlignment = HorizontalAlignment.Right;
                badge.VerticalAlignment = VerticalAlignment.Top;
                badge.Margin = new Thickness(0, -9, -12, 0);
            }
            else
            {
                badge.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(badge, 2);
            }
            content.Children.Add(badge);
            SetNavBadgeTone(badge, urgent: false);
        }
        var button = new Button { Content = content, Classes = { "navOld" } };
        ToolTip.SetTip(button, text);
        AutomationProperties.SetName(button, text);
        if (collapsed)
        {
            button.Padding = new Thickness(0);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
        return button;
    }

    /// <summary>Заголовок группы старого вида (нажатие сворачивает группу); «только значки» — пустой промежуток.</summary>
    private Control OldNavGroupHeader(string key, string title)
    {
        if (UserPreferences.Instance.OwnerSidebarCollapsed)
            return new Border { Height = 1, Margin = new Thickness(8, 10), Background = Brushes.Transparent };
        var text = new TextBlock { Text = title.ToUpper(UiCulture), VerticalAlignment = VerticalAlignment.Center };
        var chevron = new Path { Margin = new Thickness(6, 0, 2, 0), RenderTransformOrigin = RelativePoint.Center };
        if (this.TryFindResource("ChevronDownIcon", out var icon) && icon is Geometry geometry)
            chevron.Data = geometry;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);
        var header = new Button { Content = grid, Classes = { "navGroupBtn" } };
        var tip = Tr.T("Свернуть или развернуть группу", "Топту жыйноо же ачуу", "Collapse or expand the group", "Grubu daralt veya genişlet",
            "Guruhni yig'ish yoki ochish");
        ToolTip.SetTip(header, tip);
        AutomationProperties.SetName(header, title + " — " + tip);
        header.Click += (_, _) => ToggleOldNavGroup(key);
        _oldGroupHeaders[key] = (header, chevron);
        return header;
    }

    private static HashSet<string> CollapsedNavGroups() =>
        new((UserPreferences.Instance.OwnerNavCollapsedGroups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private void ToggleOldNavGroup(string key)
    {
        var collapsed = CollapsedNavGroups();
        if (!collapsed.Remove(key))
            collapsed.Add(key);
        UserPreferences.Instance.OwnerNavCollapsedGroups = collapsed.Count == 0 ? null : string.Join(",", collapsed);
        UserPreferences.Instance.SaveToDisk();
        ApplyNavFilter();
    }

    /// <summary>Что видно в старом виде: при поиске — найденное (группы без находок прячутся), без поиска — всё, кроме пунктов
    /// свёрнутых групп (открытый раздел виден всегда). Возвращает, сколько пунктов видно.</summary>
    private int ApplyOldNavVisibility(string query)
    {
        var iconsOnly = UserPreferences.Instance.OwnerSidebarCollapsed;
        if (iconsOnly)
            query = "";
        var filtering = query.Length > 0;
        var collapsedGroups = CollapsedNavGroups();
        var activeKey = _activeSection?.Key ?? "overview";
        var groupsWithItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shown = 0;
        foreach (var entry in _navEntries)
        {
            var groupTitle = _navGroups.FirstOrDefault(g => g.Key == entry.GroupKey)?.Title ?? "";
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
        foreach (var (key, (header, chevron)) in _oldGroupHeaders)
        {
            header.IsVisible = !filtering || groupsWithItems.Contains(key);
            chevron.RenderTransform = !filtering && collapsedGroups.Contains(key) ? new RotateTransform(-90) : null;
        }
        return filtering ? shown : 1;
    }
}
