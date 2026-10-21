using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-19, владелец: «так же и в кассе сделай старое/новое для выбора» — вид меню ☰ кассы, выбирается так же, как
/// вид меню программы владельца: карточки с рисунком (OwnerMenuStylePicker с этим набором), сразу, личная настройка
/// UserPreferences.KassaMenuStyle (облако — SettingsCloudSync, PersonalKeys).
/// • «Старое» — меню ☰ выпуска 1.17.58 (колонка 300: смена и остаток, свёрнутые разделы со значками) — по умолчанию:
///   у кассиров после обновления ничего не меняется;
/// • «Новое» — 2026-10-20 переделано (владелец о виде по макету KassaMenus: «это вообще кошмар что за дизайн???»): выезжающая
///   слева панель 400 с шапкой-карточкой смены (кассир, «Смена открыта», сумма в кассе, «Внесение / изъятие», «Закрыть смену»),
///   поиском действий (Ctrl+K), плитками быстрых действий и группами со значками; «Система» сворачивается; внизу «Сменить кассира»
///   и «Выйти».
/// Пункты, права, тариф, скрытые разделы и команды в обоих видах одни и те же (SideMenuViewModel); меняется только вид.
/// Программа владельца (--owner) эту настройку не читает.</summary>
public static class KassaMenuStyles
{
    public const string Old = "old";
    public const string New = "new";

    public static readonly string[] All = { Old, New };

    public static event Action? Changed;

    public static string Normalize(string? id) => id == New ? New : Old;

    public static string Current => Normalize(UserPreferences.Instance.KassaMenuStyle);

    public static void Set(string id)
    {
        id = Normalize(id);
        var prefs = UserPreferences.Instance;
        if (Normalize(prefs.KassaMenuStyle) == id)
            return;
        // По умолчанию («Старое») — без значения в файле.
        prefs.KassaMenuStyle = id == Old ? null : id;
        prefs.SaveToDisk();
        PosLogger.Log($"Kassa: вид меню ☰ — {id}.", "UI");
        Changed?.Invoke();
    }

    public static void RaiseChanged() => Changed?.Invoke();

    public static string Label(string id) => Normalize(id) == New
        ? Tr.T("Новое", "Жаңы", "New", "Yeni", "Yangi")
        : Tr.T("Старое", "Эски", "Classic", "Klasik", "Eski");

    public static string Description(string id) => Normalize(id) == New
        ? Tr.T(
            "Карточка смены с суммой в кассе сверху, поиск действия (Ctrl+K), плитки быстрых действий и разделы со значками.",
            "Үстүндө кассадагы сумма менен смена карточкасы, аракетти издөө (Ctrl+K), тез аракеттердин плиткалары жана белгилери бар бөлүмдөр.",
            "A shift card with the till balance on top, action search (Ctrl+K), quick action tiles and sections with icons.",
            "Üstte kasa bakiyeli vardiya kartı, işlem arama (Ctrl+K), hızlı işlem kutucukları ve simgeli bölümler.",
            "Yuqorida kassadagi summa bilan smena kartochkasi, amalni qidirish (Ctrl+K), tezkor amallar plitkalari va belgili bo'limlar.")
        : Tr.T(
            "Как до обновления: узкая колонка со значками, остаток в кассе вверху, разделы раскрываются нажатием. По умолчанию.",
            "Жаңыртууга чейинкидей: белгилери бар тар тилке, кассадагы калдык үстүндө, бөлүмдөр басканда ачылат. Демейки.",
            "As before the update: a narrow column with icons, cash balance on top, sections open on click. Default.",
            "Güncellemeden önceki gibi: simgeli dar sütun, üstte kasa bakiyesi, bölümler tıklayınca açılır. Varsayılan.",
            "Yangilanishdan oldingidek: belgili tor ustun, kassadagi qoldiq yuqorida, bo'limlar bosilganda ochiladi. Standart.");

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
        DialogSubtitle = () => Tr.T("Меню ☰ кассы. Меняется сразу; можно сменить и в Настройках → Экран.",
            "Кассанын ☰ менюсу. Дароо өзгөрөт; Жөндөөлөр → Экран бөлүмүнөн да алмаштырса болот.",
            "The till's ☰ menu. Changes right away; you can also change it in Settings → Screen.",
            "Kasanın ☰ menüsü. Hemen değişir; Ayarlar → Ekran'dan da değiştirebilirsiniz.",
            "Kassaning ☰ menyusi. Darhol o'zgaradi; Sozlamalar → Ekran bo'limida ham o'zgartirish mumkin."),
    };

    /// <summary>Миниатюра: экран кассы под затемнением и меню ☰ слева (цвета — из текущей темы).</summary>
    public static Control BuildPreview(Control host, string id, double width, double height)
    {
        IBrush B(string key, string fallback) =>
            host.TryFindResource(key, host.ActualThemeVariant, out var v) && v is IBrush b ? b : new SolidColorBrush(Color.Parse(fallback));
        var backdrop = B("BrushWindowBackdrop", "#F2F4F7");
        var panel = B("BrushPanel", "#FFFFFF");
        var border = B("BrushBorder", "#E4E7EC");
        var text = B("BrushText", "#0B2A3C");
        var soft = B("BrushBorderStrong", "#D0D5DD");
        var accent = B("BrushAccent", "#FFD600");

        Border Bar(double w, double h, IBrush fill, double radius = 1.5, Thickness? margin = null) => new()
        {
            Width = w, Height = h, Background = fill, CornerRadius = new CornerRadius(radius),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0),
        };

        // Под меню — экран кассы: строки чека слева, справа итог и жёлтая «Оплата».
        var screen = new Grid { ColumnDefinitions = new ColumnDefinitions("*,0.42*"), Margin = new Thickness(6) };
        var rows = new StackPanel { Spacing = 4, Margin = new Thickness(0, 14, 6, 0) };
        for (var i = 0; i < 6; i++)
            rows.Children.Add(new Border { Height = 9, CornerRadius = new CornerRadius(3), Background = panel, BorderBrush = border, BorderThickness = new Thickness(1) });
        screen.Children.Add(rows);
        var pay = new StackPanel { Spacing = 5, Margin = new Thickness(0, 14, 0, 0) };
        pay.Children.Add(new Border { Height = 40, CornerRadius = new CornerRadius(4), Background = panel, BorderBrush = border, BorderThickness = new Thickness(1) });
        pay.Children.Add(new Border { Height = 14, CornerRadius = new CornerRadius(4), Background = accent });
        Grid.SetColumn(pay, 1);
        screen.Children.Add(pay);

        var root = new Grid();
        root.Children.Add(screen);
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)) });

        Control drawer;
        if (Normalize(id) == New)
        {
            // 2026-10-20: панель слева — тёмная карточка смены (цвет текста темы) с суммой и двумя кнопками, поле поиска,
            // плитки 3 в ряд, строки со значками.
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(5) };
            var darkVariant = host.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            var heroBg = darkVariant ? B("BrushPanelSoft", "#26314A") : text;
            var heroFg = darkVariant ? text : panel;
            var hero = new Border
            {
                CornerRadius = new CornerRadius(4), Background = heroBg, Padding = new Thickness(5),
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = soft }, Bar(22, 3, heroFg) } },
                        Bar(30, 5, heroFg),
                        new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("*,3,*"),
                            Children =
                            {
                                new Border { Height = 7, CornerRadius = new CornerRadius(2), BorderBrush = heroFg, BorderThickness = new Thickness(1) },
                                new Border { Height = 7, CornerRadius = new CornerRadius(2), BorderBrush = heroFg, BorderThickness = new Thickness(1), [Grid.ColumnProperty] = 2 },
                            },
                        },
                    },
                },
            };
            stack.Children.Add(hero);
            stack.Children.Add(new Border { Height = 7, CornerRadius = new CornerRadius(2), BorderBrush = border, BorderThickness = new Thickness(1) });
            var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,3,*,3,*"), RowDefinitions = new RowDefinitions("Auto,3,Auto") };
            for (var r = 0; r < 2; r++)
                for (var c = 0; c < 3; c++)
                    tiles.Children.Add(new Border
                    {
                        Height = 11, CornerRadius = new CornerRadius(2.5), BorderBrush = border, BorderThickness = new Thickness(1),
                        Child = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(1), BorderBrush = text, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        [Grid.RowProperty] = r * 2, [Grid.ColumnProperty] = c * 2,
                    });
            stack.Children.Add(tiles);
            for (var i = 0; i < 3; i++)
                stack.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(1, 1, 0, 0),
                    Children = { new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(1), BorderBrush = text, BorderThickness = new Thickness(1) }, Bar(16 + i * 6, 3, text) },
                });
            drawer = new Border { Width = Math.Round(width * 0.38), HorizontalAlignment = HorizontalAlignment.Left, Background = panel, Child = stack };
        }
        else
        {
            // Колонка 300: название кассы и ✕, карточка смены с жёлтым остатком, свёрнутые разделы, пункты со значками.
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(6) };
            stack.Children.Add(new Grid { Children = { Bar(30, 4, text), new Border { Width = 6, Height = 6, HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(1.5), Background = soft } } });
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), BorderBrush = border, BorderThickness = new Thickness(1), Padding = new Thickness(4),
                Child = new StackPanel { Spacing = 3, Children = { Bar(20, 2.5, soft), Bar(28, 5, accent), Bar(24, 2.5, soft) } },
            });
            for (var g = 0; g < 3; g++)
            {
                stack.Children.Add(new Grid
                {
                    Margin = new Thickness(0, 2, 0, 0),
                    Children = { Bar(20, 2, soft, 1), new Border { Width = 4, Height = 2, HorizontalAlignment = HorizontalAlignment.Right, Background = soft } },
                });
            }
            for (var i = 0; i < 3; i++)
                stack.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4,
                    Children = { new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(1.5), BorderBrush = text, BorderThickness = new Thickness(1) }, Bar(18 + i * 6, 3, text) },
                });
            drawer = new Border { Width = Math.Round(width * 0.36), HorizontalAlignment = HorizontalAlignment.Left, Background = panel, Child = stack };
        }
        root.Children.Add(drawer);

        return new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = backdrop, BorderBrush = border, BorderThickness = new Thickness(1), Child = root,
        };
    }
}
