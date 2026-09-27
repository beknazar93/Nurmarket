using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Раскладки главного экрана кассы (Настройки → Экран → «Вид кассы»), 2026-09-28.
///
/// Раньше их было две: «standard» (каталог + корзина) и «onec» (1С). Теперь шесть. Значение
/// хранится там же, где раньше, — UserPreferences.MainLayoutMode, поэтому у работающих касс ничего
/// не меняется: "standard" остаётся «Классикой», "onec" — «1С». Неизвестное значение сводится к
/// «Классике».
///
/// Названия в интерфейсе нейтральные («Табличная», «Карточки», «Минимал», «Профи»): чужие марки
/// на экране кассы не показываем, образцы указаны только в комментариях самих раскладок.</summary>
public static class KassaLayouts
{
    public const string Standard = "standard";
    public const string Table = "table";
    public const string Cards = "cards";
    public const string Minimal = "minimal";
    public const string Pro = "pro";
    public const string OneC = "onec";

    public sealed record LayoutOption(string Id, Func<string> Label, Func<string> Description);

    /// <summary>Порядок здесь — порядок карточек в настройках.</summary>
    public static readonly IReadOnlyList<LayoutOption> All =
    [
        new(Standard,
            () => Tr.T("Классика", "Классика", "Classic", "Klasik", "Klassika"),
            () => Tr.T(
                "Каталог плитками слева, чек справа, ширину колонок можно менять. Вид, с которым касса ставится.",
                "Сол жакта плиткалар менен каталог, оң жакта чек, тилкелердин туурасын өзгөртүүгө болот. Касса ушул көрүнүш менен орнотулат.",
                "Catalog tiles on the left, the receipt on the right, adjustable column width. The till's default look.",
                "Solda karo katalog, sağda fiş; sütun genişliği ayarlanabilir. Kasanın varsayılan görünümü.",
                "Chapda plitkali katalog, o'ngda chek, ustunlar kengligini o'zgartirish mumkin. Kassa shu ko'rinishda o'rnatiladi.")),
        new(Table,
            () => Tr.T("Табличная", "Таблица", "Table", "Tablo", "Jadval"),
            () => Tr.T(
                "Чек плотной таблицей (№, товар, кол-во, цена, сумма) и крупный итог, сверху поле штрихкода, справа быстрые товары и цветные кнопки оплаты. Для работы с клавиатуры и сканера.",
                "Чек тыгыз таблица түрүндө (№, товар, саны, баасы, суммасы) жана чоң жыйынтык, өйдө жакта штрихкод талаасы, оң жакта тез товарлар жана түстүү төлөм баскычтары. Баскычтоп жана сканер менен иштөө үчүн.",
                "The receipt as a dense table (No., item, qty, price, amount) with a large total, a barcode field on top, quick products and colored payment buttons on the right. Built for keyboard and scanner work.",
                "Fiş sık bir tablo olarak (No, ürün, miktar, fiyat, tutar) ve büyük toplam; üstte barkod alanı, sağda hızlı ürünler ve renkli ödeme düğmeleri. Klavye ve barkod okuyucuyla çalışmak için.",
                "Chek zich jadval ko'rinishida (№, mahsulot, miqdor, narx, summa) va katta jami, tepada shtrix-kod maydoni, o'ngda tezkor mahsulotlar va rangli to'lov tugmalari. Klaviatura va skaner bilan ishlash uchun.")),
        new(Cards,
            () => Tr.T("Карточки", "Карточкалар", "Cards", "Kartlar", "Kartochkalar"),
            () => Tr.T(
                "Светлый экран: категории кнопками-чипами сверху, товары карточками по центру, чек справа с кнопками +/− и большая зелёная кнопка «Оплатить».",
                "Жарык экран: өйдө жакта категориялар чип-баскычтар менен, ортодо товарлар карточка түрүндө, оң жакта +/− баскычтары бар чек жана чоң жашыл «Төлөө» баскычы.",
                "A light screen: category chips on top, product cards in the center, the receipt on the right with +/− steppers and a big green “Pay” button.",
                "Aydınlık ekran: üstte kategori çipleri, ortada ürün kartları, sağda +/− düğmeli fiş ve büyük yeşil «Öde» düğmesi.",
                "Yorug' ekran: tepada kategoriyalar chip-tugmalar ko'rinishida, o'rtada mahsulot kartochkalari, o'ngda +/− tugmali chek va katta yashil «To'lash» tugmasi.")),
        new(Minimal,
            () => Tr.T("Минимал", "Минимал", "Minimal", "Minimal", "Minimal"),
            () => Tr.T(
                "Много воздуха и крупные цветные плитки товаров, справа «Текущая продажа» и огромная кнопка «Оплатить» с суммой. Ничего лишнего на экране.",
                "Көп бош орун жана чоң түстүү плиткалар, оң жакта «Учурдагы сатуу» жана сумма жазылган чоң «Төлөө» баскычы. Экранда ашыкча эч нерсе жок.",
                "Lots of whitespace and large colored product tiles, “Current sale” on the right and a huge “Pay” button with the amount. Nothing extra on the screen.",
                "Bol boşluk ve büyük renkli ürün karoları, sağda «Mevcut satış» ve tutarı gösteren kocaman «Öde» düğmesi. Ekranda gereksiz hiçbir şey yok.",
                "Ko'p bo'sh joy va katta rangli mahsulot plitkalari, o'ngda «Joriy savdo» va summasi yozilgan katta «To'lash» tugmasi. Ekranda ortiqcha hech narsa yo'q.")),
        new(Pro,
            () => Tr.T("Профи", "Профи", "Pro", "Profesyonel", "Professional"),
            () => Tr.T(
                "Тёмная боковая панель с разделами, крупный поиск со списком результатов, справа чек с блоком покупателя и итогами. Для магазинов с большим ассортиментом.",
                "Бөлүмдөрү бар караңгы каптал панель, натыйжалар тизмеси менен чоң издөө, оң жакта сатып алуучу блогу жана жыйынтыгы бар чек. Ассортименти чоң дүкөндөр үчүн.",
                "A dark side rail with sections, a large search with a results list, and the receipt on the right with a customer block and totals. For stores with a large assortment.",
                "Bölümleri olan koyu yan menü, sonuç listeli büyük arama, sağda müşteri bölümü ve toplamlarla fiş. Ürün çeşidi geniş mağazalar için.",
                "Bo'limlari bor qorong'i yon panel, natijalar ro'yxati bilan katta qidiruv, o'ngda xaridor bloki va jamilari bor chek. Assortimenti katta do'konlar uchun.")),
        new(OneC,
            () => Tr.T("1С", "1С", "1C", "1C", "1C"),
            () => Tr.T(
                "Как «Рабочее место кассира» 1С: строка сканера, крупная активная строка чека, итоги справа. Товар добавляется сканером или из избранного.",
                "1С «Кассирдин жумуш орду» сыяктуу: сканер сабы, чектин чоң активдүү сабы, оң жакта жыйынтык. Товар сканер же тандалмалар аркылуу кошулат.",
                "Like the 1C “Cashier workplace”: a scanner line, a large active receipt line, totals on the right. Products are added by scanner or from favorites.",
                "1C «Kasiyer çalışma yeri» gibi: barkod satırı, büyük etkin fiş satırı, sağda toplamlar. Ürün barkod okuyucuyla veya favorilerden eklenir.",
                "1C «Kassir ish joyi» kabi: skaner qatori, chekning katta faol qatori, o'ngda jamilar. Mahsulot skaner yoki sevimlilardan qo'shiladi.")),
    ];

    /// <summary>Сохранённый id → существующий; неизвестный (или пустой) — «Классика».</summary>
    public static string Normalize(string? id)
    {
        foreach (var option in All)
        {
            if (string.Equals(option.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase))
                return option.Id;
        }
        return Standard;
    }

    /// <summary>Раскладки, которые живут в отдельном контроле (не «Классика» и не «1С» — те
    /// объявлены прямо в MainWindow.axaml).</summary>
    public static bool IsAlternative(string id) => id is Table or Cards or Minimal or Pro;

    public static Control? CreateView(string id) => id switch
    {
        Table => new TableLayoutView(),
        Cards => new CardsLayoutView(),
        Minimal => new MinimalLayoutView(),
        Pro => new ProLayoutView(),
        _ => null,
    };

    // ------------------------------------------------------------------ миниатюры

    /// <summary>Схематичная миниатюра раскладки для карточки в настройках — цветами текущей темы,
    /// чтобы кассир видел не абстрактную картинку, а свою кассу в этой раскладке.</summary>
    public static Control BuildPreview(string id, double width = 220, double height = 132)
    {
        var p = new PreviewPalette();
        var root = new Border
        {
            Width = width,
            Height = height,
            Background = p.Window,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            BorderBrush = p.Border,
            BorderThickness = new Thickness(1),
        };

        root.Child = id switch
        {
            Table => TablePreview(p),
            Cards => CardsPreview(p),
            Minimal => MinimalPreview(p),
            Pro => ProPreview(p),
            OneC => OneCPreview(p),
            _ => StandardPreview(p),
        };
        return root;
    }

    private sealed class PreviewPalette
    {
        public readonly IBrush Window = Res("BrushWindow", "#EFF3F8");
        public readonly IBrush Panel = Res("BrushPanel", "#FFFFFF");
        public readonly IBrush Soft = Res("BrushPanelSoft", "#E2E8F0");
        public readonly IBrush Border = Res("BrushBorder", "#CBD5E1");
        public readonly IBrush Accent = Res("BrushAccent", "#F7D617");
        public readonly IBrush Success = Res("BrushSuccess", "#047857");
        public readonly IBrush Warning = Res("BrushWarning", "#B45309");
        public readonly IBrush Text = Res("BrushTextSoft", "#64748B");
        public readonly IBrush Dark = new SolidColorBrush(Color.Parse("#111827"));

        private static IBrush Res(string key, string fallback) =>
            Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var value) == true && value is IBrush brush
                ? brush
                : new SolidColorBrush(Color.Parse(fallback));
    }

    private static Border Box(IBrush fill, double w = double.NaN, double h = double.NaN, double r = 3, Thickness? margin = null, IBrush? stroke = null) =>
        new()
        {
            Background = fill,
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(r),
            Margin = margin ?? default,
            BorderBrush = stroke,
            BorderThickness = stroke is null ? default : new Thickness(1),
        };

    private static Border Line(IBrush fill, double w, double h = 3, Thickness? margin = null) =>
        Box(fill, w, h, 1.5, margin ?? new Thickness(0, 0, 0, 4));

    private static Panel Tiles(PreviewPalette p, int columns, int rows, double w, double h, IBrush? fill = null, bool colored = false)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        IBrush[] colors = [p.Accent, p.Success, p.Warning, p.Text, p.Accent, p.Success, p.Warning, p.Text, p.Accent];
        for (var i = 0; i < columns * rows; i++)
            wrap.Children.Add(Box(colored ? colors[i % colors.Length] : fill ?? p.Panel, w, h, 3, new Thickness(0, 0, 4, 4), colored ? null : p.Border));
        return wrap;
    }

    private static Control ReceiptLines(PreviewPalette p, int count, double w)
    {
        var stack = new StackPanel { Spacing = 0 };
        for (var i = 0; i < count; i++)
            stack.Children.Add(Line(p.Text, w - (i % 2) * 12, 3, new Thickness(0, 0, 0, 5)));
        return stack;
    }

    private static Grid Columns(string definition) => new() { ColumnDefinitions = new ColumnDefinitions(definition) };

    private static T At<T>(T control, int column, int row = 0) where T : Control
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        return control;
    }

    private static Control StandardPreview(PreviewPalette p)
    {
        var grid = Columns("1.4*,*");
        grid.Margin = new Thickness(6);
        grid.Children.Add(At(new StackPanel
        {
            Children = { Box(p.Soft, double.NaN, 8, 2, new Thickness(0, 0, 6, 6)), Tiles(p, 4, 3, 24, 26) },
        }, 0));
        var cart = new Border { Background = p.Panel, CornerRadius = new CornerRadius(4), Padding = new Thickness(6), BorderBrush = p.Border, BorderThickness = new Thickness(1) };
        var cartGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        cartGrid.Children.Add(ReceiptLines(p, 5, 60));
        cartGrid.Children.Add(At(Box(p.Accent, double.NaN, 14, 3), 0, 1));
        cart.Child = cartGrid;
        grid.Children.Add(At(cart, 1));
        return grid;
    }

    private static Control TablePreview(PreviewPalette p)
    {
        var outer = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(6) };
        outer.Children.Add(Box(p.Panel, double.NaN, 10, 2, new Thickness(0, 0, 0, 5), p.Border));
        var grid = Columns("1.3*,*");
        var table = new Border { Background = p.Panel, CornerRadius = new CornerRadius(3), BorderBrush = p.Border, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 5, 0) };
        var tableGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        tableGrid.Children.Add(Box(p.Soft, double.NaN, 7, 0));
        var rows = new StackPanel { Margin = new Thickness(4, 4, 4, 0) };
        for (var i = 0; i < 6; i++)
            rows.Children.Add(new Border { Height = 7, BorderBrush = p.Border, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 2), Child = Line(p.Text, 50, 2, new Thickness(0, 2, 0, 0)) });
        tableGrid.Children.Add(At(rows, 0, 1));
        var total = Box(p.Text, 34, 9, 2);
        total.HorizontalAlignment = HorizontalAlignment.Right;
        tableGrid.Children.Add(At(new Border { Height = 16, Margin = new Thickness(4), Child = total }, 0, 2));
        table.Child = tableGrid;
        grid.Children.Add(At(table, 0));
        var right = new StackPanel();
        right.Children.Add(Tiles(p, 3, 2, 18, 14));
        var buttons = new UniformGrid { Columns = 2 };
        buttons.Children.Add(Box(p.Success, double.NaN, 10, 2, new Thickness(0, 0, 3, 3)));
        buttons.Children.Add(Box(p.Accent, double.NaN, 10, 2, new Thickness(0, 0, 3, 3)));
        buttons.Children.Add(Box(p.Warning, double.NaN, 10, 2, new Thickness(0, 0, 3, 3)));
        buttons.Children.Add(Box(p.Soft, double.NaN, 10, 2, new Thickness(0, 0, 3, 3)));
        right.Children.Add(buttons);
        right.Children.Add(Box(p.Success, double.NaN, 14, 3, new Thickness(0, 2, 3, 0)));
        grid.Children.Add(At(right, 1));
        outer.Children.Add(At(grid, 0, 1));
        return outer;
    }

    private static Control CardsPreview(PreviewPalette p)
    {
        var grid = Columns("1.4*,*");
        grid.Margin = new Thickness(6);
        var left = new StackPanel();
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 0, 0, 6) };
        chips.Children.Add(Box(p.Accent, 18, 7, 4));
        for (var i = 0; i < 4; i++)
            chips.Children.Add(Box(p.Panel, 16, 7, 4, null, p.Border));
        left.Children.Add(chips);
        var cards = new WrapPanel();
        for (var i = 0; i < 6; i++)
        {
            cards.Children.Add(new Border
            {
                Width = 30, Height = 38, Background = p.Panel, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 5, 5),
                BorderBrush = p.Border, BorderThickness = new Thickness(1),
                Child = new StackPanel { Margin = new Thickness(3), Children = { Box(p.Soft, double.NaN, 16, 2), Line(p.Text, 18, 2, new Thickness(0, 4, 0, 3)), Box(p.Success, 8, 8, 4) } },
            });
        }
        left.Children.Add(cards);
        grid.Children.Add(At(left, 0));
        var cart = new Border { Background = p.Panel, CornerRadius = new CornerRadius(4), Padding = new Thickness(5), BorderBrush = p.Border, BorderThickness = new Thickness(1) };
        var cartGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var lines = new StackPanel { Spacing = 3 };
        for (var i = 0; i < 3; i++)
            lines.Children.Add(new Border { Height = 16, Background = p.Soft, CornerRadius = new CornerRadius(3), Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(3, 8, 0, 0), Children = { Box(p.Border, 6, 5, 1), Box(p.Border, 6, 5, 1) } } });
        cartGrid.Children.Add(lines);
        cartGrid.Children.Add(At(Box(p.Success, double.NaN, 15, 3), 0, 1));
        cart.Child = cartGrid;
        grid.Children.Add(At(cart, 1));
        return grid;
    }

    private static Control MinimalPreview(PreviewPalette p)
    {
        var grid = Columns("1.5*,*");
        var left = new StackPanel { Margin = new Thickness(8) };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        tabs.Children.Add(new Border { Width = 18, Height = 6, BorderBrush = p.Accent, BorderThickness = new Thickness(0, 0, 0, 2) });
        tabs.Children.Add(Box(p.Soft, 14, 3, 1));
        tabs.Children.Add(Box(p.Soft, 14, 3, 1));
        left.Children.Add(tabs);
        left.Children.Add(Tiles(p, 3, 2, 30, 30, null, colored: true));
        grid.Children.Add(At(left, 0));
        var sale = new Border { Background = p.Panel, BorderBrush = p.Border, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(8) };
        var saleGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        saleGrid.Children.Add(Line(p.Text, 40, 5, new Thickness(0, 0, 0, 8)));
        saleGrid.Children.Add(At(ReceiptLines(p, 3, 56), 0, 1));
        saleGrid.Children.Add(At(Box(p.Accent, double.NaN, 22, 5), 0, 2));
        sale.Child = saleGrid;
        grid.Children.Add(At(sale, 1));
        return grid;
    }

    private static Control ProPreview(PreviewPalette p)
    {
        var grid = Columns("16,1.4*,*");
        grid.Children.Add(At(new Border
        {
            Background = p.Dark,
            Child = new StackPanel { Margin = new Thickness(4, 8, 4, 0), Spacing = 6, Children = { Box(p.Accent, 8, 8, 2), Box(p.Text, 8, 8, 2), Box(p.Text, 8, 8, 2), Box(p.Text, 8, 8, 2) } },
        }, 0));
        var center = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        center.Children.Add(new Border { Background = p.Dark, Padding = new Thickness(6, 5), Child = Box(p.Panel, double.NaN, 10, 3) });
        var list = new StackPanel { Margin = new Thickness(6) };
        for (var i = 0; i < 6; i++)
            list.Children.Add(new Border { Height = 12, Background = p.Panel, BorderBrush = p.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = Line(p.Text, 40 - i % 3 * 6, 2, new Thickness(12, 5, 0, 0)) });
        center.Children.Add(At(list, 0, 1));
        grid.Children.Add(At(center, 1));
        var cart = new Border { Background = p.Panel, BorderBrush = p.Border, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(6) };
        var cartGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        cartGrid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 6), Children = { Box(p.Soft, 12, 12, 6), Line(p.Text, 26, 3, new Thickness(0, 4, 0, 0)) } });
        cartGrid.Children.Add(At(ReceiptLines(p, 4, 46), 0, 1));
        cartGrid.Children.Add(At(Box(p.Success, double.NaN, 15, 3), 0, 2));
        cart.Child = cartGrid;
        grid.Children.Add(At(cart, 2));
        return grid;
    }

    private static Control OneCPreview(PreviewPalette p)
    {
        var outer = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(6) };
        outer.Children.Add(Box(p.Soft, double.NaN, 10, 2, new Thickness(0, 0, 0, 5), p.Warning));
        var grid = Columns("*,Auto");
        var left = new Border { Background = p.Panel, CornerRadius = new CornerRadius(3), BorderBrush = p.Border, BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 0, 5, 0) };
        left.Child = new StackPanel { Children = { Box(p.Soft, double.NaN, 18, 3, new Thickness(0, 0, 0, 5), p.Success), ReceiptLines(p, 4, 90) } };
        grid.Children.Add(At(left, 0));
        var right = new Border { Width = 50, Background = p.Panel, CornerRadius = new CornerRadius(3), BorderBrush = p.Border, BorderThickness = new Thickness(1), Padding = new Thickness(4) };
        var rightGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        rightGrid.Children.Add(ReceiptLines(p, 3, 36));
        rightGrid.Children.Add(At(Box(p.Accent, double.NaN, 12, 2), 0, 1));
        right.Child = rightGrid;
        grid.Children.Add(At(right, 1));
        outer.Children.Add(At(grid, 0, 1));
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 5, 0, 0) };
        for (var i = 0; i < 4; i++)
            pills.Children.Add(Box(p.Soft, 24, 8, 4, null, p.Success));
        outer.Children.Add(At(pills, 0, 2));
        return outer;
    }
}
