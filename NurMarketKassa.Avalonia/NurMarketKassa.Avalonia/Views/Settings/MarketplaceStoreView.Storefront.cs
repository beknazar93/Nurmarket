using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-10-23: витрина Маркетплейса (поиск, чипы, ряды, плитки, значок) — часть MarketplaceStoreView.
// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-2): переработана — одна строка «поиск + чипы», тёмный баннер «Рекомендуем»
// (MarketplaceHero), ряды по задачам (MarketplaceGroup), крупные плитки с векторными значками (MarketplaceIcons) и меткой «НОВОЕ»,
// стрелки ‹ › у рядов, состояния «загрузка» и «ничего не найдено».
public sealed partial class MarketplaceStoreView
{
    // Размеры плиток (ТЗ 3.2/3.3): широкая витрина (от 1100 px) и узкая (1024×600).
    private const double WideBreakpoint = 1100;
    private const double TileGap = 12;

    private readonly ContentControl _list = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Grid _searchBox = new();

    // Группа, выбранная чипом (Продажи / Склад / …); виды и темы идут через _filter, чтобы работал внешний SetFilter().
    private MarketplaceGroup? _groupChip;
    // Узкая витрина: плитка 160×168, баннер 156 без снимка. Определяется по ширине самой витрины.
    private bool _narrow;

    // Имя группы в строке под названием плитки — только в сетке фильтра/поиска на широкой витрине (в ряду группа и так в заголовке).
    private bool _tileShowsGroup;

    private double TileWidth => _narrow ? 160 : 188;
    private double TileHeight => _narrow ? 176 : 184;
    private double TileIconSize => _narrow ? 48 : 56;

    // ------------------------------------------------------------------ витрина

    private void BuildStorefrontShell()
    {
        _search.Watermark = Tr.T("Поиск функций, тем и видов кассы", "Функцияларды, темаларды жана касса көрүнүштөрүн издөө",
            "Search features, themes and till layouts", "Özellik, tema ve kasa görünümü ara", "Funksiya, mavzu va kassa ko'rinishlarini qidirish");
        _search.Height = 40;
        _search.MinHeight = 40;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.Padding = new Thickness(36, 0, 12, 0);
        _search.CornerRadius = new CornerRadius(20);
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        _search.TextChanged += (_, _) =>
        {
            var q = _search.Text ?? "";
            if (q == _query)
                return;
            _query = q;
            RenderList();
        };

        var searchIcon = new PathIcon
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(14, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        if (Application.Current?.TryFindResource("IconSearch", out var geo) == true)
            searchIcon.Data = geo as Geometry;
        searchIcon.Bind(PathIcon.ForegroundProperty, searchIcon.GetResourceObservable("BrushTextSoft"));
        _searchBox.Width = 340;
        _searchBox.VerticalAlignment = VerticalAlignment.Center;
        _searchBox.Children.Add(_search);
        _searchBox.Children.Add(searchIcon);

        var chipScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _chips,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        EnableWheelScroll(chipScroll);

        // Одна строка 40 px: поиск слева, чипы справа с горизонтальной прокруткой (колесо мыши листает чипы).
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 16), Height = 40 };
        top.Children.Add(_searchBox);
        Grid.SetColumn(chipScroll, 1);
        top.Children.Add(chipScroll);

        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        shell.Children.Add(top);
        Grid.SetRow(_list, 1);
        shell.Children.Add(_list);
        _storefront = shell;

        // Витрина подстраивается под ширину: на 1024×600 — узкий вид (плитки 160×168, баннер 156 без снимка).
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width > 0 && e.NewSize.Width < WideBreakpoint;
            if (narrow == _narrow)
                return;
            _narrow = narrow;
            _searchBox.Width = narrow ? 240 : 340;
            if (_openId == null && _items.Count > 0)
                RenderList();
        };
    }

    private Grid? _storefront;

    private void ShowStorefront()
    {
        _openId = null;
        // Внешний SetFilter(Features/Layouts/Themes) сбрасывает выбор группы: группа действует только при фильтре «Все».
        if (_filter != MarketplaceFilter.All)
            _groupChip = null;
        BuildChips();
        RenderList();
        _page.Content = _storefront;
    }

    private void BuildChips()
    {
        _chips.Children.Clear();
        void Add(string label, bool active, Action click)
        {
            var chip = new Button { Content = label };
            chip.Classes.Add("mp-chip");
            chip.Classes.Set("active", active);
            chip.Click += (_, _) =>
            {
                click();
                BuildChips();
                RenderList();
            };
            _chips.Children.Add(chip);
        }

        void Group(MarketplaceGroup g)
        {
            if (_items.Any(i => i.Group == g))
                Add(GroupChip(g), _filter == MarketplaceFilter.All && _groupChip == g, () => ApplyGroup(g));
        }

        Add(Tr.T("Все", "Баары", "All", "Tümü", "Hammasi"), _filter == MarketplaceFilter.All && _groupChip == null,
            () => { _filter = MarketplaceFilter.All; _groupChip = null; });
        // «Функции» — не постоянный чип: появляется, только когда раздел открыт извне (Доп. функции), чтобы было видно, что выбрано.
        if (_filter == MarketplaceFilter.Features)
            Add(Tr.T("Функции", "Функциялар", "Features", "Özellikler", "Funksiyalar"), true, () => { });
        Group(MarketplaceGroup.Sales);
        Group(MarketplaceGroup.Warehouse);
        Group(MarketplaceGroup.Owner);
        Group(MarketplaceGroup.Packs);
        Add(GroupChip(MarketplaceGroup.Layouts), _filter == MarketplaceFilter.Layouts, () => ApplyGroup(MarketplaceGroup.Layouts));
        Add(GroupChip(MarketplaceGroup.Themes), _filter == MarketplaceFilter.Themes, () => ApplyGroup(MarketplaceGroup.Themes));
        Add(Tr.T("Установленные", "Орнотулгандар", "Installed", "Yüklenenler", "O'rnatilganlar"), _filter == MarketplaceFilter.Installed,
            () => { _filter = MarketplaceFilter.Installed; _groupChip = null; });
        Add(Tr.T("Платные", "Акылуу", "Paid", "Ücretli", "Pullik"), _filter == MarketplaceFilter.Paid,
            () => { _filter = MarketplaceFilter.Paid; _groupChip = null; });
    }

    /// <summary>Показать только одну группу (чип или «Все N ›» у ряда). Виды и темы идут через прежние фильтры по виду элемента.</summary>
    private void ApplyGroup(MarketplaceGroup g)
    {
        switch (g)
        {
            case MarketplaceGroup.Layouts:
                _filter = MarketplaceFilter.Layouts;
                _groupChip = null;
                break;
            case MarketplaceGroup.Themes:
                _filter = MarketplaceFilter.Themes;
                _groupChip = null;
                break;
            default:
                _filter = MarketplaceFilter.All;
                _groupChip = g;
                break;
        }
    }

    private static string GroupTitle(MarketplaceGroup g) => g switch
    {
        MarketplaceGroup.Sales => Tr.T("Продажи и клиенты", "Сатуу жана кардарлар", "Sales and customers", "Satış ve müşteriler", "Savdo va mijozlar"),
        MarketplaceGroup.Warehouse => Tr.T("Склад и печать", "Кампа жана басып чыгаруу", "Stock and printing", "Depo ve yazdırma", "Ombor va chop etish"),
        MarketplaceGroup.Owner => Tr.T("Для владельца", "Ээси үчүн", "For the owner", "Sahibi için", "Egasi uchun"),
        MarketplaceGroup.Packs => Tr.T("Пакеты «Стандарта»", "«Стандарт» пакеттери", "“Standard” packs", "«Standart» paketleri", "«Standart» paketlari"),
        MarketplaceGroup.Layouts => Tr.T("Виды кассы", "Касса көрүнүштөрү", "Till layouts", "Kasa görünümleri", "Kassa ko'rinishlari"),
        MarketplaceGroup.Themes => Tr.T("Темы", "Темалар", "Themes", "Temalar", "Mavzular"),
        _ => Tr.T("Скоро", "Жакында", "Coming soon", "Yakında", "Tez orada"),
    };

    private static string GroupChip(MarketplaceGroup g) => g switch
    {
        MarketplaceGroup.Sales => Tr.T("Продажи", "Сатуу", "Sales", "Satış", "Savdo"),
        MarketplaceGroup.Warehouse => Tr.T("Склад и печать", "Кампа жана басма", "Stock and print", "Depo ve baskı", "Ombor va chop"),
        MarketplaceGroup.Owner => Tr.T("Владельцу", "Ээсине", "Owner", "Sahibine", "Egasiga"),
        MarketplaceGroup.Packs => Tr.T("Пакеты", "Пакеттер", "Packs", "Paketler", "Paketlar"),
        _ => GroupTitle(g),
    };

    private static bool IsPaidItem(MarketplaceItem i) => i.Price != null && !i.IsComingSoon;

    private bool PassesFilter(MarketplaceItem i) =>
        (_groupChip == null || i.Group == _groupChip) && _filter switch
        {
            MarketplaceFilter.Features => i.Kind is MarketplaceKind.Feature or MarketplaceKind.Service,
            MarketplaceFilter.Themes => i.Kind == MarketplaceKind.Theme,
            MarketplaceFilter.Layouts => i.Kind == MarketplaceKind.Layout,
            MarketplaceFilter.Paid => IsPaidItem(i),
            MarketplaceFilter.Installed => i.Status == MarketplaceStatus.Installed,
            _ => true,
        };

    private void RenderList()
    {
        if (_items.Count == 0)
        {
            _list.Content = BuildSkeleton();
            return;
        }

        var searching = !string.IsNullOrWhiteSpace(_query);
        if (_filter == MarketplaceFilter.All && _groupChip == null && !searching)
        {
            var rows = new StackPanel { Spacing = 28, Margin = new Thickness(0, 0, 14, 16) };

            // Баннер: рекомендуемые, сначала ещё не подключённые функции (ТЗ 3.4), не больше пяти.
            var featured = _items.Where(i => i.IsFeatured)
                .OrderBy(i => i.Kind is MarketplaceKind.Feature or MarketplaceKind.Service && i.Status != MarketplaceStatus.Installed ? 0 : 1)
                .Take(5).ToList();
            if (featured.Count > 0)
                rows.Children.Add(new MarketplaceHero(featured, _narrow, OpenFromTile));

            foreach (var g in Enum.GetValues<MarketplaceGroup>())
            {
                var list = _items.Where(i => i.Group == g).ToList();
                if (list.Count > 0)
                    rows.Children.Add(BuildRow(g, list));
            }

            _list.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = rows };
            return;
        }

        var matches = _items.Where(PassesFilter).Where(i => !searching || i.Matches(_query)).ToList();
        if (matches.Count == 0)
        {
            _list.Content = BuildEmpty();
            return;
        }

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 16) };
        _tileShowsGroup = !_narrow && _groupChip == null && _filter is not (MarketplaceFilter.Layouts or MarketplaceFilter.Themes);
        foreach (var item in matches)
        {
            var tile = BuildTile(item);
            tile.Margin = new Thickness(0, 0, TileGap, TileGap);
            wrap.Children.Add(tile);
        }
        _tileShowsGroup = false;
        _list.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = wrap };
    }

    // ------------------------------------------------------------------ ряд

    private Control BuildRow(MarketplaceGroup group, IReadOnlyList<MarketplaceItem> items)
    {
        var heading = new TextBlock
        {
            Text = GroupTitle(group),
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Fg(heading, "BrushText");

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TileGap };
        foreach (var item in items)
            strip.Children.Add(BuildTile(item));
        // Полоса прокрутки скрыта (ТЗ 3.4): ряд листают кнопки ‹ ›, колесо мыши и панорамирование пальцем.
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = strip,
        };
        EnableWheelScroll(scroll);

        var (prevHost, prev) = RowNav("chevron-left", Tr.T("Назад", "Артка", "Previous", "Önceki", "Orqaga"));
        var (nextHost, next) = RowNav("chevron-right", Tr.T("Вперёд", "Алдыга", "Next", "Sonraki", "Oldinga"));
        var pitch = TileWidth + TileGap;
        prev.Click += (_, _) => AnimateScroll(scroll, scroll.Offset.X - StepOf(scroll, pitch));
        next.Click += (_, _) => AnimateScroll(scroll, scroll.Offset.X + StepOf(scroll, pitch));
        void UpdateNav()
        {
            var max = scroll.Extent.Width - scroll.Viewport.Width;
            prev.IsVisible = scroll.Offset.X > 0.5;
            next.IsVisible = max > 0.5 && scroll.Offset.X < max - 0.5;
        }
        scroll.ScrollChanged += (_, _) => UpdateNav();
        prev.IsVisible = false;
        next.IsVisible = false;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 10), MinHeight = 32 };
        head.Children.Add(heading);
        // «Все N ›» — открыть эту группу целиком; у «Скоро» своего чипа нет.
        if (group != MarketplaceGroup.Soon)
        {
            var all = new Button { Content = Tr.T($"Все {items.Count} ›", $"Баары {items.Count} ›", $"All {items.Count} ›", $"Tümü {items.Count} ›", $"Hammasi {items.Count} ›") };
            all.Classes.Add("mp-row-all");
            all.Margin = new Thickness(8, 0, 8, 0);
            all.Click += (_, _) =>
            {
                ApplyGroup(group);
                BuildChips();
                RenderList();
            };
            Grid.SetColumn(all, 1);
            head.Children.Add(all);
        }
        Grid.SetColumn(prevHost, 2);
        Grid.SetColumn(nextHost, 3);
        prevHost.Margin = new Thickness(0, 0, 6, 0);
        head.Children.Add(prevHost);
        head.Children.Add(nextHost);

        return new StackPanel { Spacing = 0, Children = { head, scroll } };
    }

    /// <summary>Круглая кнопка ‹ / › у ряда в рамке 32×32 (при скрытии кнопки заголовок не сдвигается).</summary>
    private static (Panel Host, Button Button) RowNav(string iconKey, string name)
    {
        var b = new Button { Content = MarketplaceIcons.Icon(iconKey, 18) };
        b.Classes.Add("mp-row-nav");
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        return (new Panel { Width = 32, Height = 32, Children = { b } }, b);
    }

    private static double StepOf(ScrollViewer sv, double pitch) => Math.Max(pitch, Math.Floor(sv.Viewport.Width / pitch) * pitch);

    // ------------------------------------------------------------------ прокрутка

    private static readonly ConditionalWeakTable<ScrollViewer, DispatcherTimer> ScrollTimers = new();

    /// <summary>Плавная прокрутка ряда: 8 шагов по 25 мс (≈200 мс) с замедлением в конце, без анимационных библиотек.</summary>
    private static void AnimateScroll(ScrollViewer sv, double target)
    {
        var max = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        target = Math.Clamp(target, 0, max);
        if (ScrollTimers.TryGetValue(sv, out var old))
            old.Stop();
        var from = sv.Offset.X;
        var step = 0;
        const int steps = 8;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            step++;
            var p = step / (double)steps;
            var eased = 1 - Math.Pow(1 - p, 3);
            sv.Offset = new Vector(from + (target - from) * eased, sv.Offset.Y);
            if (step >= steps)
                timer.Stop();
        };
        ScrollTimers.AddOrUpdate(sv, timer);
        timer.Start();
    }

    /// <summary>Колесо мыши над рядом/чипами листает по горизонтали; на краю отдаёт колесо вертикальной прокрутке страницы.</summary>
    private static void EnableWheelScroll(ScrollViewer sv)
    {
        EnableDragScroll(sv);
        sv.PointerWheelChanged += (_, e) =>
        {
            var max = sv.Extent.Width - sv.Viewport.Width;
            if (max <= 0.5)
                return;
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : e.Delta.Y;
            var x = sv.Offset.X - delta * 60;
            if ((delta > 0 && sv.Offset.X <= 0.5) || (delta < 0 && sv.Offset.X >= max - 0.5))
                return;
            sv.Offset = new Vector(Math.Clamp(x, 0, max), sv.Offset.Y);
            e.Handled = true;
        };
    }

    /// <summary>2026-10-26, владелец: «карточки в Маркетплейсе руками не двигаются, только стрелкой сверху». Ряд плиток и чипы
    /// листаются перетаскиванием (мышь, палец, перо): плитки — это кнопки, они забирали нажатие у ScrollViewer, поэтому
    /// встроенное панорамирование не срабатывало. Пока сдвиг меньше порога — обычный клик по плитке; дальше ряд
    /// забирает указатель, и «клик» по плитке после перетаскивания не происходит.</summary>
    private static void EnableDragScroll(ScrollViewer sv)
    {
        const double threshold = 8;
        var pressed = false;
        var dragging = false;
        var startX = 0.0;
        var startOffset = 0.0;

        sv.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(sv);
            if (e.Pointer.Type == PointerType.Mouse && !point.Properties.IsLeftButtonPressed)
                return;
            pressed = true;
            dragging = false;
            startX = point.Position.X;
            startOffset = sv.Offset.X;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        sv.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (!pressed)
                return;
            var max = sv.Extent.Width - sv.Viewport.Width;
            if (max <= 0.5)
                return;
            var dx = e.GetCurrentPoint(sv).Position.X - startX;
            if (!dragging)
            {
                if (Math.Abs(dx) < threshold)
                    return;
                dragging = true;
                e.Pointer.Capture(sv);
            }
            sv.Offset = new Vector(Math.Clamp(startOffset - dx, 0, max), sv.Offset.Y);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        void End(PointerEventArgs e)
        {
            if (dragging && ReferenceEquals(e.Pointer.Captured, sv))
                e.Pointer.Capture(null);
            pressed = false;
            dragging = false;
        }
        sv.AddHandler(InputElement.PointerReleasedEvent, (_, e) => End(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        sv.PointerCaptureLost += (_, _) => { pressed = false; dragging = false; };
    }

    // ------------------------------------------------------------------ плитка

    /// <summary>Плитка 188×184 (узкая 160×168): значок, название в две строки, вид и группа, цена или состояние. Короткое описание — в подсказке.</summary>
    private Button BuildTile(MarketplaceItem item)
    {
        var icon = BuildIcon(item, TileIconSize);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        top.Children.Add(icon);
        if (item.IsNew)
        {
            var badge = new Border { Child = new TextBlock { Text = Tr.T("НОВОЕ", "ЖАҢЫ", "NEW", "YENİ", "YANGI") } };
            badge.Classes.Add("mp-badge-new");
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(badge, 1);
            top.Children.Add(badge);
        }

        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 19,
            Height = 38,
            Margin = new Thickness(0, _narrow ? 8 : 10, 0, 0),
        };
        Fg(title, "BrushText");

        var kind = new TextBlock
        {
            Text = KindLine(item),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Margin = new Thickness(0, 2, 0, 0),
        };
        Fg(kind, "BrushTextSoft");

        var status = BuildStatusLine(item);
        status.VerticalAlignment = VerticalAlignment.Bottom;

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        body.Children.Add(top);
        Grid.SetRow(title, 1);
        body.Children.Add(title);
        Grid.SetRow(kind, 2);
        body.Children.Add(kind);
        Grid.SetRow(status, 4);
        body.Children.Add(status);

        var card = new Border { Child = body };
        card.Classes.Add("mp-tile-card");

        var tile = new Button
        {
            Width = TileWidth,
            Height = TileHeight,
            Content = card,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Tag = item.Id,
        };
        tile.Classes.Add("mp-tile");
        var hint = string.IsNullOrWhiteSpace(item.ShortDescription) ? item.Tagline : item.ShortDescription;
        ToolTip.SetTip(tile, string.IsNullOrWhiteSpace(hint) ? item.Title : hint);
        Avalonia.Automation.AutomationProperties.SetName(tile, item.Title);
        tile.Click += (_, _) => OpenFromTile(item.Id);
        return tile;
    }

    /// <summary>«Функция» (в сетке фильтра/поиска — «Функция · Склад и печать»): вид элемента и (для функций и услуг) короткое имя группы.</summary>
    private string KindLine(MarketplaceItem item)
    {
        if (_tileShowsGroup && item.Kind is MarketplaceKind.Feature or MarketplaceKind.Service && item.Group is not MarketplaceGroup.Soon)
            return item.KindLabel + " · " + GroupChip(item.Group);
        return item.KindLabel;
    }

    /// <summary>Нижняя строка плитки: цена или состояние. «Установлено/Включено» — значок галочки и зелёный текст, «Скоро» — предупреждающий цвет.</summary>
    private static Control BuildStatusLine(MarketplaceItem item)
    {
        var line = item.TileStatusLine;
        var check = line.StartsWith('✓');
        var text = new TextBlock
        {
            Text = line.TrimStart('✓', ' '),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            LineHeight = 16,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var brushKey = item.IsComingSoon ? "BrushWarning" : check ? "BrushSuccess" : item.Price is null ? "BrushTextSoft" : "BrushText";
        Fg(text, brushKey);
        // Grid, а не StackPanel: тексту задана ширина карточки, длинная цена («1500 сом + 400 сом/мес») переносится на вторую строку.
        var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        if (check)
        {
            var mark = MarketplaceIcons.Icon("check", 14);
            mark.Margin = new Thickness(0, 0, 4, 1);
            mark.VerticalAlignment = VerticalAlignment.Bottom;
            mark.Bind(TemplatedControl.ForegroundProperty, mark.GetResourceObservable("BrushSuccess"));
            panel.Children.Add(mark);
        }
        Grid.SetColumn(text, 1);
        panel.Children.Add(text);
        return panel;
    }

    private static string StatusBrushKey(MarketplaceItem item) => item.Status switch
    {
        MarketplaceStatus.Installed => "BrushSuccess",
        MarketplaceStatus.ComingSoon => "BrushWarning",
        MarketplaceStatus.Paid => "BrushText",
        _ => "BrushTextSoft",
    };

    /// <summary>Цвет знака на подложке цвета элемента: белый, если контраст ≥ 4,5:1, иначе лучший из белого и #191919
    /// (на жёлтом #FFB208 выходит #191919). Считается по середине вертикального градиента подложки.</summary>
    internal static string GlyphColorOn(string hex)
    {
        var mid = AccentThemeService.MixHex(hex, "#000000", 0.09);
        var onWhite = AccentThemeService.Contrast(mid, "#FFFFFF");
        if (onWhite >= 4.5)
            return "#FFFFFF";
        return AccentThemeService.Contrast(mid, "#191919") >= onWhite ? "#191919" : "#FFFFFF";
    }

    /// <summary>Квадратный значок со скруглением. Темы и виды кассы — нарисованный предпросмотр (IconVisual); остальные — векторный
    /// знак по IconKey на подложке-градиенте цвета элемента (сверху цвет, снизу на 18 % темнее); нет ключа — прежний эмодзи-глиф.</summary>
    internal static Control BuildIcon(MarketplaceItem item, double size)
    {
        var hex = Color.TryParse(item.IconColor, out var c) ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "#FFB208";
        var color = Color.Parse(hex);
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Round(size * 0.25)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (item.IconVisual != null)
        {
            border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("BrushBorderStrong"));
            var visual = item.IconVisual(size);
            visual.HorizontalAlignment = HorizontalAlignment.Center;
            visual.VerticalAlignment = VerticalAlignment.Center;
            border.Child = visual;
            return border;
        }

        border.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(color, 0),
                new GradientStop(Color.Parse(AccentThemeService.MixHex(hex, "#000000", 0.18)), 1),
            },
        };
        // Тонкая светлая кайма: тёмная подложка («Графит») не сливается с тёмной темой.
        border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var geo = MarketplaceIcons.Get(item.IconKey);
        if (geo != null)
        {
            border.Child = new PathIcon
            {
                Width = Math.Round(size / 2),
                Height = Math.Round(size / 2),
                Data = geo,
                Foreground = new SolidColorBrush(Color.Parse(GlyphColorOn(hex))),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            border.Child = new TextBlock
            {
                Text = item.IconGlyph,
                FontSize = size * 0.46,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return border;
    }

    // ------------------------------------------------------------------ состояния

    /// <summary>Ничего не найдено: значок, текст и «Сбросить поиск» (очищает строку поиска и все фильтры).</summary>
    private Control BuildEmpty()
    {
        var icon = MarketplaceIcons.Icon("magnify-close", 48);
        icon.Bind(TemplatedControl.ForegroundProperty, icon.GetResourceObservable("BrushTextSoft"));
        var text = new TextBlock
        {
            Text = Tr.T("Ничего не найдено", "Эч нерсе табылган жок", "Nothing found", "Hiçbir şey bulunamadı", "Hech narsa topilmadi"),
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Fg(text, "BrushText");
        var hint = new TextBlock
        {
            Text = Tr.T("Попробуйте другое слово или сбросьте поиск", "Башка сөздү байкап көрүңүз же издөөнү тазалаңыз",
                "Try another word or reset the search", "Başka bir kelime deneyin veya aramayı sıfırlayın", "Boshqa so'zni sinab ko'ring yoki qidiruvni tozalang"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Fg(hint, "BrushTextSoft");
        var reset = new Button
        {
            Content = Tr.T("Сбросить поиск", "Издөөнү тазалоо", "Reset search", "Aramayı sıfırla", "Qidiruvni tozalash"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(20, 0),
            Height = 40,
            Margin = new Thickness(0, 6, 0, 0),
        };
        reset.Classes.Add("btn-secondary");
        reset.Click += (_, _) =>
        {
            _filter = MarketplaceFilter.All;
            _groupChip = null;
            _query = "";
            _search.Text = "";
            BuildChips();
            RenderList();
        };
        return new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 48, 0, 0),
            MaxWidth = 420,
            Children = { icon, text, hint, reset },
        };
    }

    /// <summary>Загрузка (на будущее, для сетевого источника каталога): баннер и два ряда плиток-заготовок цвета BrushPanelSoft, без анимации.</summary>
    private Control BuildSkeleton()
    {
        static Border Block(double w, double h, double radius)
        {
            var b = new Border { Height = h, CornerRadius = new CornerRadius(radius) };
            if (!double.IsNaN(w))
                b.Width = w;
            b.Bind(Border.BackgroundProperty, b.GetResourceObservable("BrushPanelSoft"));
            return b;
        }

        var stack = new StackPanel { Spacing = 28, Margin = new Thickness(0, 0, 14, 16) };
        stack.Children.Add(Block(double.NaN, _narrow ? 156 : 220, 20));
        for (var r = 0; r < 2; r++)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TileGap };
            for (var i = 0; i < 6; i++)
                strip.Children.Add(Block(TileWidth, TileHeight, 16));
            var heading = Block(200, 20, 6);
            heading.HorizontalAlignment = HorizontalAlignment.Left;
            heading.Margin = new Thickness(0, 0, 0, 12);
            var stripScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = strip };
            EnableDragScroll(stripScroll); // 2026-10-26: ленту можно тянуть рукой
            stack.Children.Add(new StackPanel { Children = { heading, stripScroll } });
        }
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = stack };
    }
}
