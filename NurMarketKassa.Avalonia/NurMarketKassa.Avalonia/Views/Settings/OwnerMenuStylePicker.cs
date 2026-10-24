using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-18, владелец: «вид меню на выбор» в программе владельца — три вида, выбираются так же, как тема и вид
/// кассы: карточки с маленьким рисунком. Настройка личная (UserPreferences.OwnerMenuStyle, облако — SettingsCloudSync).
/// • «Новое» — тёмная колонка групп и колонка пунктов (этап О2), по умолчанию;
/// • «Бургер ☰» — меню спрятано, раздел на всю ширину, ☰ в шапке открывает меню поверх раздела;
/// • «Старое» — одна светлая колонка, как в выпуске 1.17.58;
/// • «Вкладки» (2026-10-19, решение владельца — четвёртый вид, «Новое» не меняется) — слева только тёмная колонка групп,
///   пункты группы — вкладками над разделом (OwnerShellWindow.Tabs.cs).
/// Касса (без --owner) эту настройку не читает.</summary>
public static class OwnerMenuStyles
{
    public const string New = "new";
    public const string Burger = "burger";
    public const string Old = "old";
    /// <summary>2026-10-19: «Вкладки» — значение в настройках "tabs"; неизвестное значение по-прежнему читается как «Новое».</summary>
    public const string Tabs = "tabs";

    public static readonly string[] All = { New, Tabs, Burger, Old };

    /// <summary>Вид сменили (карточкой в окне выбора или в Настройках) — программа владельца перестраивает меню сразу.</summary>
    public static event Action? Changed;

    public static string Normalize(string? id) => id switch
    {
        Burger => Burger,
        Old => Old,
        Tabs => Tabs,
        _ => New,
    };

    public static string Current => Normalize(UserPreferences.Instance.OwnerMenuStyle);

    public static void Set(string id)
    {
        id = Normalize(id);
        var prefs = UserPreferences.Instance;
        if (Normalize(prefs.OwnerMenuStyle) == id)
            return;
        // По умолчанию («Новое») — без значения в файле, как у остальных настроек с умолчанием.
        prefs.OwnerMenuStyle = id == New ? null : id;
        prefs.SaveToDisk();
        PosLogger.Log($"Owner app: вид меню — {id}.", "UI");
        Changed?.Invoke();
    }

    /// <summary>Вызвать подписчиков (вид пришёл из облака).</summary>
    public static void RaiseChanged() => Changed?.Invoke();

    /// <summary>2026-10-19: те же карточки и окно выбора — и для меню ☰ кассы (KassaMenuStyles).</summary>
    public static readonly MenuStyleCatalog Catalog = new()
    {
        All = All,
        Current = () => Current,
        Set = Set,
        Label = Label,
        Description = Description,
        BuildPreview = BuildPreview,
        Subscribe = h => Changed += h,
        Unsubscribe = h => Changed -= h,
        DialogSubtitle = () => Tr.T("Меняется сразу, открытый раздел остаётся. Можно сменить и в Настройках → Экран.",
            "Дароо өзгөрөт, ачык бөлүм калат. Жөндөөлөр → Экран бөлүмүнөн да алмаштырса болот.",
            "Changes right away; the open section stays. You can also change it in Settings → Screen.",
            "Hemen değişir, açık bölüm kalır. Ayarlar → Ekran'dan da değiştirebilirsiniz.",
            "Darhol o'zgaradi, ochiq bo'lim qoladi. Sozlamalar → Ekran bo'limida ham o'zgartirish mumkin."),
    };

    public static string Label(string id) => Normalize(id) switch
    {
        Burger => Tr.T("Бургер ☰", "Бургер ☰", "Burger ☰", "Hamburger ☰", "Burger ☰"),
        Old => Tr.T("Старое", "Эски", "Classic", "Klasik", "Eski"),
        Tabs => Tr.T("Вкладки", "Өтмөктөр", "Tabs", "Sekmeler", "Varaqlar"),
        _ => Tr.T("Новое", "Жаңы", "New", "Yeni", "Yangi"),
    };

    public static string Description(string id) => Normalize(id) switch
    {
        Burger => Tr.T(
            "Меню спрятано — раздел на всю ширину. Кнопка ☰ вверху открывает меню поверх раздела, Esc закрывает.",
            "Меню жашырылган — бөлүм толук туурасында. Үстүдөгү ☰ баскычы менюну бөлүмдүн үстүнө ачат, Esc жабат.",
            "The menu is hidden and the section uses the full width. The ☰ button at the top opens the menu over the section, Esc closes it.",
            "Menü gizli, bölüm tam genişlikte. Üstteki ☰ düğmesi menüyü bölümün üzerinde açar, Esc kapatır.",
            "Menyu yashirilgan — bo'lim to'liq kenglikda. Yuqoridagi ☰ tugmasi menyuni bo'lim ustida ochadi, Esc yopadi."),
        Old => Tr.T(
            "Как до обновления: одна колонка, все группы и пункты списком со значками.",
            "Жаңыртууга чейинкидей: бир тилке, бардык топтор жана пункттар белгилери менен тизме болуп.",
            "As before the update: one column with all groups and items listed with icons.",
            "Güncellemeden önceki gibi: tek sütun, tüm gruplar ve öğeler simgeleriyle liste halinde.",
            "Yangilanishdan oldingidek: bitta ustun, barcha guruhlar va bandlar belgilar bilan ro'yxatda."),
        Tabs => Tr.T(
            "Слева только группы, пункты — вкладками над разделом. Раздел шире.",
            "Сол жакта топтор гана, пункттар — бөлүмдүн үстүндө өтмөк болуп. Бөлүм кененирээк.",
            "Only groups on the left; items are tabs above the section. The section gets more room.",
            "Solda yalnızca gruplar, öğeler bölümün üstünde sekme olarak. Bölüm daha geniş.",
            "Chapda faqat guruhlar, bandlar — bo'lim ustida varaq bo'lib. Bo'lim kengroq."),
        _ => Tr.T(
            "Тёмная колонка групп и колонка пунктов выбранной группы. Колонку пунктов можно свернуть «««». По умолчанию.",
            "Топтордун караңгы тилкеси жана тандалган топтун пункттары. Пункттар тилкесин «««» менен жыйса болот. Демейки.",
            "A dark column of groups and a column with the items of the chosen group. The items column folds with «««». Default.",
            "Koyu grup sütunu ve seçilen grubun öğe sütunu. Öğe sütunu «««» ile daraltılabilir. Varsayılan.",
            "Guruhlarning qorong'i ustuni va tanlangan guruh bandlari ustuni. Bandlar ustunini «««» bilan yig'ish mumkin. Standart."),
    };

    /// <summary>Миниатюра вида: цвета — из текущей темы (тёмная колонка — как у меню: цвет текста светлой темы, в тёмной —
    /// почти чёрная).</summary>
    public static Control BuildPreview(Control host, string id, double width, double height)
    {
        IBrush B(string key, string fallback) =>
            host.TryFindResource(key, host.ActualThemeVariant, out var v) && v is IBrush b ? b : new SolidColorBrush(Color.Parse(fallback));
        var dark = host.ActualThemeVariant == ThemeVariant.Dark;
        var backdrop = B("BrushWindowBackdrop", "#F2F4F7");
        var panel = B("BrushPanel", "#FFFFFF");
        var border = B("BrushBorder", "#E4E7EC");
        var text = B("BrushText", "#0B2A3C");
        var soft = B("BrushBorderStrong", "#D0D5DD");
        var accent = B("BrushAccent", "#FFD600");
        var rail = new SolidColorBrush(dark ? Color.Parse("#0B0F14") : (text as ISolidColorBrush)?.Color ?? Color.Parse("#0B2A3C"));
        var railFg = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

        Border Bar(double w, double h, IBrush fill, double radius = 2, Thickness? margin = null) => new()
        {
            Width = w, Height = h, Background = fill, CornerRadius = new CornerRadius(radius),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = margin ?? new Thickness(0),
        };

        // Правая часть — «раздел»: заголовок и две строки карточек.
        Control Content(bool withBurger, bool withTabs = false)
        {
            var stack = new StackPanel { Spacing = 6, Margin = new Thickness(8, 8, 8, 0) };
            if (withTabs)
            {
                // 2026-10-19, «Вкладки»: мелкая строка «компания · группа», под ней вкладки (выбранная подчёркнута тёмно-синим).
                stack.Margin = new Thickness(0, 6, 0, 0);
                var tabs = new StackPanel { Spacing = 0, Margin = new Thickness(8, 0, 8, 0) };
                tabs.Children.Add(Bar(26, 3, soft, 1.5, new Thickness(0, 0, 0, 5)));
                var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                for (var i = 0; i < 4; i++)
                {
                    var on = i == 0;
                    strip.Children.Add(new Border
                    {
                        Padding = new Thickness(2, 0, 2, 4), BorderBrush = on ? text : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2.5),
                        Child = Bar(16 + (i * 6) % 10, 4, on ? text : soft, 2),
                    });
                }
                tabs.Children.Add(strip);
                stack.Children.Add(new Border { BorderBrush = border, BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs });
                var cardsRow = new StackPanel { Spacing = 6, Margin = new Thickness(8, 2, 8, 0) };
                for (var row = 0; row < 2; row++)
                {
                    var cards = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4, Height = row == 0 ? 20 : 30 };
                    for (var i = 0; i < cards.Columns; i++)
                        cards.Children.Add(new Border
                        {
                            Background = panel, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                            Margin = new Thickness(0, 0, i < cards.Columns - 1 ? 4 : 0, 0),
                        });
                    cardsRow.Children.Add(cards);
                }
                stack.Children.Add(cardsRow);
                return stack;
            }
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (withBurger)
            {
                var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                for (var i = 0; i < 3; i++)
                    lines.Children.Add(Bar(11, 2, text, 1));
                head.Children.Add(new Border
                {
                    Width = 20, Height = 18, CornerRadius = new CornerRadius(5), BorderBrush = text, BorderThickness = new Thickness(1.2),
                    Child = new Panel { Children = { lines }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            }
            head.Children.Add(new Border { Width = 46, Height = 7, CornerRadius = new CornerRadius(2), Background = text, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(head);
            for (var row = 0; row < 2; row++)
            {
                var cards = new Avalonia.Controls.Primitives.UniformGrid { Columns = withBurger ? 4 : 3, Height = row == 0 ? 22 : 34 };
                for (var i = 0; i < cards.Columns; i++)
                    cards.Children.Add(new Border
                    {
                        Background = panel, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(0, 0, i < cards.Columns - 1 ? 4 : 0, 0),
                    });
                stack.Children.Add(cards);
            }
            return stack;
        }

        // Пункты: строки-полоски, выбранный — тёмно-синий (как в меню).
        StackPanel Items(int count, int selected, bool icons)
        {
            var items = new StackPanel { Spacing = 3 };
            for (var i = 0; i < count; i++)
            {
                var on = i == selected;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                if (icons)
                    row.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(1.5), Background = on ? panel : soft, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(Bar(icons ? 20 + (i * 7) % 12 : 26 + (i * 9) % 14, 3, on ? panel : soft, 1.5));
                items.Children.Add(new Border
                {
                    Height = 9, Padding = new Thickness(3, 0), CornerRadius = new CornerRadius(3),
                    Background = on ? text : Brushes.Transparent, Child = row,
                });
            }
            return items;
        }

        var grid = new Grid();
        switch (Normalize(id))
        {
            case Tabs:
            {
                // 2026-10-19: тёмная колонка групп (как у «Нового»), без колонки пунктов — вкладки над разделом.
                grid.ColumnDefinitions = new ColumnDefinitions("20,*");
                var groups = new StackPanel { Spacing = 5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                groups.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = accent, Margin = new Thickness(0, 0, 0, 3) });
                for (var i = 0; i < 6; i++)
                    groups.Children.Add(new Border
                    {
                        Width = 12, Height = 9, CornerRadius = new CornerRadius(3),
                        Background = i == 1 ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD6, 0x00)) : Brushes.Transparent,
                        Child = new Border { Width = 6, Height = 5, CornerRadius = new CornerRadius(1.5), Background = i == 1 ? accent : railFg },
                    });
                grid.Children.Add(new Border { Background = rail, Child = groups });
                var content = Content(withBurger: false, withTabs: true);
                Grid.SetColumn(content, 1);
                grid.Children.Add(content);
                break;
            }
            case Burger:
                grid.Children.Add(Content(withBurger: true));
                break;
            case Old:
            {
                grid.ColumnDefinitions = new ColumnDefinitions("62,*");
                var side = new StackPanel { Spacing = 4, Margin = new Thickness(5, 6, 5, 0) };
                side.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 3,
                    Children = { new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2.5), Background = accent }, Bar(22, 4, text, 1.5, new Thickness(0, 2, 0, 0)) },
                });
                side.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(3), BorderBrush = border, BorderThickness = new Thickness(1) });
                side.Children.Add(Bar(18, 2, soft, 1, new Thickness(2, 2, 0, 0)));
                side.Children.Add(Items(3, 1, icons: true));
                side.Children.Add(Bar(18, 2, soft, 1, new Thickness(2, 2, 0, 0)));
                side.Children.Add(Items(3, -1, icons: true));
                grid.Children.Add(new Border { Background = panel, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 0), Child = side });
                var content = Content(withBurger: false);
                Grid.SetColumn(content, 1);
                grid.Children.Add(content);
                break;
            }
            default:
            {
                grid.ColumnDefinitions = new ColumnDefinitions("20,58,*");
                var groups = new StackPanel { Spacing = 5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                groups.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = accent, Margin = new Thickness(0, 0, 0, 3) });
                for (var i = 0; i < 6; i++)
                    groups.Children.Add(new Border
                    {
                        Width = 12, Height = 9, CornerRadius = new CornerRadius(3),
                        Background = i == 1 ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD6, 0x00)) : Brushes.Transparent,
                        Child = new Border { Width = 6, Height = 5, CornerRadius = new CornerRadius(1.5), Background = i == 1 ? accent : railFg },
                    });
                grid.Children.Add(new Border { Background = rail, Child = groups });
                var column = new StackPanel { Spacing = 5, Margin = new Thickness(5, 6, 5, 0) };
                column.Children.Add(Bar(30, 4, text, 1.5));
                column.Children.Add(Items(4, 1, icons: false));
                var items = new Border { Background = panel, BorderBrush = border, BorderThickness = new Thickness(0, 0, 1, 0), Child = column };
                Grid.SetColumn(items, 1);
                grid.Children.Add(items);
                var content = Content(withBurger: false);
                Grid.SetColumn(content, 2);
                grid.Children.Add(content);
                break;
            }
        }

        return new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = backdrop, BorderBrush = border, BorderThickness = new Thickness(1), Child = grid,
        };
    }
}

/// <summary>2026-10-19: набор видов меню для карточек и окна выбора — программы владельца (OwnerMenuStyles.Catalog) или меню ☰
/// кассы (KassaMenuStyles.Catalog). Карточки, окно и их поведение — общие.</summary>
public sealed class MenuStyleCatalog
{
    public required string[] All { get; init; }
    public required Func<string> Current { get; init; }
    public required Action<string> Set { get; init; }
    public required Func<string, string> Label { get; init; }
    public required Func<string, string> Description { get; init; }
    public required Func<Control, string, double, double, Control> BuildPreview { get; init; }
    public required Action<Action> Subscribe { get; init; }
    public required Action<Action> Unsubscribe { get; init; }
    public required Func<string> DialogSubtitle { get; init; }
}

/// <summary>Карточки видов меню (2026-10-19: четыре — добавлены «Вкладки») (окно «Вид меню» и Настройки → Экран). Нажатие применяет вид сразу.
/// 2026-10-19: те же карточки — и для меню ☰ кассы (Catalog = KassaMenuStyles.Catalog).</summary>
public sealed class OwnerMenuStylePicker : UserControl
{
    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal };

    public double CardWidth { get; init; } = 252;

    public MenuStyleCatalog Catalog { get; init; } = OwnerMenuStyles.Catalog;

    public OwnerMenuStylePicker()
    {
        Content = _panel;
        // Карточки нарисованы цветами темы и подписаны на языке программы — перестраиваем при показе, смене языка и вида.
        AttachedToVisualTree += (_, _) =>
        {
            Tr.LanguageChanged -= OnChanged;
            Tr.LanguageChanged += OnChanged;
            Catalog.Unsubscribe(OnChanged);
            Catalog.Subscribe(OnChanged);
            Rebuild();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Tr.LanguageChanged -= OnChanged;
            Catalog.Unsubscribe(OnChanged);
        };
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    private void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    public void Rebuild()
    {
        _panel.Children.Clear();
        var current = Catalog.Current();
        foreach (var id in Catalog.All)
        {
            var isActive = id == current;
            var title = new TextBlock { Text = Catalog.Label(id), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            // 2026-10-23: подписка на сам элемент, а не на страницу — иначе снятые карточки не собираются GC (утечка при пересборке)
            title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 4) };
            header.Children.Add(title);
            if (isActive)
            {
                // Выбранное — тёмно-синим с белым текстом (правило аудита 2), как выбранный период и пункт меню.
                var badgeText = new TextBlock
                {
                    Text = Tr.T("✓ Выбрано", "✓ Тандалган", "✓ Selected", "✓ Seçili", "✓ Tanlangan"),
                    FontSize = 11, FontWeight = FontWeight.SemiBold,
                };
                badgeText.Bind(TextBlock.ForegroundProperty, badgeText.GetResourceObservable("BrushPanel"));
                var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
                badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable("BrushText"));
                Grid.SetColumn(badge, 1);
                header.Children.Add(badge);
            }
            var description = new TextBlock { Text = Catalog.Description(id), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            description.Bind(TextBlock.ForegroundProperty, description.GetResourceObservable("BrushTextSoft"));

            var card = new Button
            {
                Tag = id,
                Width = CardWidth,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(12),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                VerticalAlignment = VerticalAlignment.Stretch,
                BorderThickness = new Thickness(isActive ? 2 : 1),
                CornerRadius = new CornerRadius(12),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = new StackPanel { Children = { Catalog.BuildPreview(this, id, CardWidth - 28, Math.Round((CardWidth - 28) * 0.56)), header, description } },
            };
            // Тот же класс, что у карточек вида кассы (наведение — стиль Button.LayoutCard ниже).
            card.Classes.Add("LayoutCard");
            card.Bind(Button.BackgroundProperty, card.GetResourceObservable("BrushPanel"));
            card.Bind(Button.BorderBrushProperty, card.GetResourceObservable(isActive ? "BrushText" : "BrushBorder"));
            Avalonia.Automation.AutomationProperties.SetName(card, Catalog.Label(id) + (isActive ? " — " + Tr.T("выбрано", "тандалган", "selected", "seçili", "tanlangan") : ""));
            card.Click += (_, _) => Catalog.Set(id);
            _panel.Children.Add(card);
        }
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        // Своё наведение вместо серого Fluent — как у KassaLayoutPicker.
        var hover = new Style(x => x.OfType<Button>().Class("LayoutCard").Class(":pointerover").Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter"));
        hover.Setters.Add(new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BrushPanelSoft")));
        hover.Setters.Add(new Setter(Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BrushText")));
        Styles.Add(hover);
    }
}

/// <summary>Окно «Вид меню» — из кнопки «Вид» внизу меню программы владельца (рядом с «Тема»). Общая оболочка ModalShell:
/// заголовок и ✕, карточки видов, «Готово».
/// 2026-10-19: то же окно — для меню ☰ кассы (catalog = KassaMenuStyles.Catalog, два вида — окно 672, карточки по 278).</summary>
public sealed class OwnerMenuStyleDialog : Window
{
    public OwnerMenuStyleDialog() : this(null)
    {
    }

    public OwnerMenuStyleDialog(MenuStyleCatalog? catalog, double width = 912, double cardWidth = 194)
    {
        catalog ??= OwnerMenuStyles.Catalog;
        Title = Tr.T("Вид меню", "Менюнун көрүнүшү", "Menu layout", "Menü görünümü", "Menyu ko'rinishi");
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();

        var done = new Button
        {
            Classes = { "modal-primary" },
            Content = Tr.T("Готово", "Даяр", "Done", "Tamam", "Tayyor"),
            IsDefault = true,
            IsCancel = true,
        };
        done.Click += (_, _) => Close();
        // 2026-10-19: четыре вида — четыре карточки в ряд (на 1024×768 окно 912 помещается; уже — карточки переносятся).
        var picker = new OwnerMenuStylePicker { CardWidth = cardWidth, Catalog = catalog };
        var shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = Title,
            Subtitle = catalog.DialogSubtitle(),
            Footer = done,
            Content = picker,
        };
        Content = shell;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        // Телефон и узкое окно: поля поуже, карточки переносятся (WrapPanel).
        NarrowLayout.Attach(this, 560, narrow => shell.Margin = narrow ? new Thickness(6) : new Thickness(16));
    }
}
