using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-0): крупные предпросмотры тем, видов кассы и экрана покупателя для страницы
// «подробно» вынесены из MarketplaceCatalog.cs сюда, чтобы пакет П-4 правил их отдельным файлом; каталог берёт HeroVisual только отсюда.
// 2026-10-23, П-4: вместо абстрактной схемы — мини-касса 640×360 в цветах ВЫБРАННОЙ темы (шапка, плитки с названием и ценой, чек,
// «Оплатить»; светлая и тёмная), виды кассы — крупная миниатюра раскладки 640×384, экран покупателя — живой предпросмотр масштабом ≥ 0,5.
// Правила: цвета темы — ЛОКАЛЬНЫЕ кисти (AccentThemeService.Apply не вызывается, тема программы не мигает); содержимое строится лениво
// (при появлении в дереве) и отпускается при уходе из него; подписки на ресурсы — только у самих элементов (GetResourceObservable).
internal static class MarketplaceLookPreview
{
    /// <summary>Предельная ширина крупного предпросмотра в колонке «подробно» (ТЗ 3.9, П-4).</summary>
    private const double MaxHeroWidth = 760;

    // ------------------------------------------------------------------ темы

    /// <summary>Крупный предпросмотр встроенной темы: снимки кассы slide-1 (светлый) / slide-2 (тёмный), если они есть в ресурсах,
    /// иначе — живая мини-касса кодом; над ним сегмент «Светлая | Тёмная» (по умолчанию — вариант программы).</summary>
    internal static Control ThemeHero(string themeId, bool dark) => ThemeStage(themeId, dark, false);

    /// <summary>Крупный предпросмотр своей темы: мини-касса кодом по цветам, скруглению и размеру шрифта темы (снимков у своих тем нет).</summary>
    internal static Control CustomThemeHero(string themeId, bool dark) => ThemeStage(themeId, dark, true);

    private static Control ThemeStage(string themeId, bool dark, bool custom)
    {
        var current = dark;
        var viewbox = new Viewbox { Stretch = Stretch.Uniform };
        var frame = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderThickness = new Thickness(1), Child = viewbox };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("BrushBorderStrong"));

        var caption = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushTextSoft"));

        Bitmap? bitmap = null;
        var light = SegmentButton(Tr.T("Светлая", "Ачык", "Light", "Açık", "Yorug'"));
        var night = SegmentButton(Tr.T("Тёмная", "Караңгы", "Dark", "Koyu", "Qorong'i"));

        void Release()
        {
            viewbox.Child = null;
            bitmap?.Dispose();
            bitmap = null;
        }

        void Show()
        {
            Release();
            SetActive(light, !current);
            SetActive(night, current);
            if (!custom && LoadSlide(themeId, current) is { } shot)
            {
                bitmap = shot;
                var image = new Image { Width = 640, Height = 360, Stretch = Stretch.Uniform, Source = shot };
                RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                viewbox.Child = image;
                caption.Text = Tr.T("Так выглядит касса в этой теме", "Касса ушул темада мындай көрүнөт", "How the till looks in this theme", "Kasa bu temada böyle görünür", "Kassa shu mavzuda shunday ko'rinadi");
            }
            else
            {
                viewbox.Child = MiniKassa(Mini.FromTheme(themeId, current));
                caption.Text = Tr.T("Схема кассы в цветах этой темы — программа не перекрашивается", "Ушул теманын түстөрүндөгү кассанын схемасы — программа өзгөрбөйт", "A sketch of the till in this theme's colors — the program is not recolored", "Bu temanın renklerinde kasa şeması — program yeniden boyanmaz", "Shu mavzu ranglarida kassa sxemasi — dastur qayta bo'yalmaydi");
            }
        }

        light.Click += (_, _) => { current = false; Show(); };
        night.Click += (_, _) => { current = true; Show(); };

        var segment = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Children = { light, night } };
        var root = new StackPanel { Spacing = 10, MaxWidth = MaxHeroWidth, Children = { segment, frame, caption } };
        // Ленивая сборка: ничего не рисуем и не грузим, пока предпросмотр не попал в дерево; ушёл — освободили картинку.
        root.AttachedToVisualTree += (_, _) => Show();
        root.DetachedFromVisualTree += (_, _) => Release();
        return root;
    }

    private static Button SegmentButton(string text)
    {
        var button = new Button { Content = text, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Classes.Add("mp-chip");
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static void SetActive(Button button, bool active)
    {
        if (active)
            button.Classes.Add("active");
        else
            button.Classes.Remove("active");
    }

    /// <summary>Снимок кассы в теме (slide-1 — светлый, slide-2 — тёмный) из Assets/marketplace/theme_*; null — снимка нет.</summary>
    private static Bitmap? LoadSlide(string themeId, bool dark)
    {
        try
        {
            var probe = new MarketplaceItem { Id = "theme:" + themeId, Title = themeId };
            return MarketplaceMedia.ResolveSlide(probe, new MarketplaceSlide(dark ? "slide-2" : "slide-1", "")) is { } uri
                ? MarketplaceMedia.LoadBitmap(uri)
                : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: снимок темы «{themeId}»: {ex.Message}", "WARNING");
            return null;
        }
    }

    // ------------------------------------------------------------------ палитра мини-кассы

    /// <summary>Локальные кисти и размеры для рисунка кассы: цвета темы (без Apply) или текущие цвета программы.</summary>
    private sealed class Mini
    {
        public IBrush Window = Brushes.White, Panel = Brushes.White, PanelSoft = Brushes.LightGray, Border = Brushes.Gray, TileBg = Brushes.White, TileBorder = Brushes.Gray;
        public IBrush Accent = Brushes.Orange, AccentFg = Brushes.Black, Text = Brushes.Black, TextSoft = Brushes.Gray, Success = Brushes.Green, Warning = Brushes.Orange, Price = Brushes.Black, Photo = Brushes.LightGray;
        public IBrush Rail = new ImmutableSolidColorBrush(Color.Parse("#111827"));
        public double Radius = 8, FontScale = 1, TileBorderWidth = 1;

        public double F(double size) => Math.Round(size * FontScale * 2) / 2;

        public static Mini FromTheme(string themeId, bool dark)
        {
            var c = AccentThemeService.GetThemeColors(themeId, dark);
            var pv = AccentThemeService.GetPreview(themeId, dark);
            string Hex(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v;
            var window = Hex(c.Background, pv?.Window ?? "#EFF3F8");
            var panel = Hex(c.Panel, "#FFFFFF");
            var text = Hex(c.Text, "#111827");
            var accent = Hex(c.Accent, "#FFB208");
            var tile = pv?.Tile ?? panel;
            var tileBorder = pv?.TileBorder ?? AccentThemeService.MixHex(panel, text, 0.12);

            var m = new Mini
            {
                Window = B(window),
                Panel = B(panel),
                PanelSoft = B(AccentThemeService.MixHex(panel, text, 0.08)),
                Border = B(AccentThemeService.MixHex(panel, text, 0.18)),
                TileBg = B(tile),
                TileBorder = B(tileBorder),
                Accent = B(accent),
                AccentFg = B(Hex(c.AccentText, "#111111")),
                Text = B(text),
                TextSoft = B(Hex(c.TextSoft, AccentThemeService.MixHex(text, panel, 0.4))),
                Success = B(Hex(c.Success, "#047857")),
                Warning = B(Hex(c.Warning, "#B45309")),
                Price = B(Readable(accent, tile, text)),
                Photo = B(AccentThemeService.MixHex(accent, tile, dark ? 0.72 : 0.8)),
            };

            // Скругление и размер шрифта — у своей темы свои, иначе базовой темы.
            var custom = CustomThemeStore.Find(themeId);
            var radius = custom?.CornerRadius ?? AccentThemeService.GetBaseCornerRadius(custom?.BaseThemeId ?? themeId);
            var font = custom?.FontSize ?? AccentThemeService.GetBaseFontSize(custom?.BaseThemeId ?? themeId);
            m.Radius = Math.Clamp(radius, 0, 24);
            m.FontScale = Math.Clamp(font / 14.0, 0.86, 1.25);
            // У «Контрастной» рамки плиток чёрные и толстые — иначе плитка белая на белом.
            m.TileBorderWidth = AccentThemeService.Contrast(tile, tileBorder) >= 9 ? 2 : 1;
            return m;
        }

        /// <summary>Цвета программы такими, какие они сейчас (для видов кассы: «своя касса в этой раскладке»).</summary>
        public static Mini FromApp()
        {
            IBrush Res(string key, string fallback) =>
                Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var v) == true && v is IBrush b
                    ? b
                    : B(fallback);
            var radius = Application.Current?.TryFindResource("SettingsButtonRadius", Application.Current.ActualThemeVariant, out var r) == true && r is CornerRadius cr ? cr.TopLeft : 8;
            var accent = Res("BrushAccent", "#FFB208");
            return new Mini
            {
                Window = Res("BrushWindow", "#EFF3F8"),
                Panel = Res("BrushPanel", "#FFFFFF"),
                PanelSoft = Res("BrushPanelSoft", "#E2E8F0"),
                Border = Res("BrushBorder", "#CBD5E1"),
                TileBg = Res("BrushCatalogTileBg", "#FFFFFF"),
                TileBorder = Res("BrushCatalogTileBorder", "#CBD5E1"),
                Accent = accent,
                AccentFg = Res("BrushAccentForeground", "#111111"),
                Text = Res("BrushText", "#111827"),
                TextSoft = Res("BrushTextSoft", "#64748B"),
                Success = Res("BrushSuccess", "#047857"),
                Warning = Res("BrushWarning", "#B45309"),
                Price = Res("BrushCatalogPrice", "#111827"),
                Photo = Res("BrushAccentSoft", "#FDE9B0"),
                Radius = Math.Clamp(radius, 0, 24),
            };
        }

        /// <summary>Цвет надписи на цветной кнопке — тот из белого/почти чёрного, что контрастнее.</summary>
        public IBrush On(IBrush background)
        {
            if (background is not ISolidColorBrush s)
                return Text;
            var hex = $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}";
            return AccentThemeService.Contrast(hex, "#FFFFFF") >= AccentThemeService.Contrast(hex, "#111111") ? Brushes.White : B("#111111");
        }

        private static string Readable(string color, string background, string text)
        {
            for (var t = 0.0; t <= 1.0; t += 0.1)
            {
                var candidate = AccentThemeService.MixHex(color, text, t);
                if (AccentThemeService.Contrast(candidate, background) >= 3.0)
                    return candidate;
            }
            return text;
        }
    }

    private static IBrush B(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    // ------------------------------------------------------------------ образцы данных мини-кассы

    private static string Rub(double v) => v.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Som => Tr.T("сом", "сом", "KGS", "som", "so'm");

    /// <summary>Образцы товаров: название на 5 языках + цена. Те же, что в образце чека экрана покупателя, где можно.</summary>
    private static (string Name, double Price)[] Products() =>
    [
        ("Coca-Cola 1,5 л", 110),
        (Tr.T("Хлеб белый", "Ак нан", "White bread", "Beyaz ekmek", "Oq non"), 35),
        (Tr.T("Молоко 1 л", "Сүт 1 л", "Milk 1 l", "Süt 1 l", "Sut 1 l"), 95),
        (Tr.T("Шоколад", "Шоколад", "Chocolate", "Çikolata", "Shokolad"), 85),
        (Tr.T("Яблоки", "Алма", "Apples", "Elma", "Olma"), 120),
        (Tr.T("Вода 0,5 л", "Суу 0,5 л", "Water 0.5 l", "Su 0,5 l", "Suv 0,5 l"), 40),
        (Tr.T("Печенье", "Печенье", "Cookies", "Bisküvi", "Pechenye"), 65),
        (Tr.T("Сок 1 л", "Шире 1 л", "Juice 1 l", "Meyve suyu 1 l", "Sok 1 l"), 130),
        (Tr.T("Чай чёрный", "Кара чай", "Black tea", "Siyah çay", "Qora choy"), 160),
    ];

    /// <summary>Строки образца чека: индекс товара в Products(), количество.</summary>
    private static readonly (int Product, int Qty)[] SampleReceipt = [(0, 3), (3, 2), (1, 1), (2, 2)];

    private static double SampleTotal()
    {
        var products = Products();
        var total = 0.0;
        foreach (var (p, q) in SampleReceipt)
            total += products[p].Price * q;
        return total;
    }

    // ------------------------------------------------------------------ кирпичики рисунка

    private static Border Box(IBrush? fill, double radius, IBrush? stroke = null, double strokeWidth = 1, Control? child = null, double w = double.NaN, double h = double.NaN, Thickness? margin = null, Thickness? padding = null) =>
        new()
        {
            Background = fill,
            CornerRadius = new CornerRadius(radius),
            BorderBrush = stroke,
            BorderThickness = stroke is null ? default : new Thickness(strokeWidth),
            Child = child,
            Width = w,
            Height = h,
            Margin = margin ?? default,
            Padding = padding ?? default,
        };

    private static TextBlock Tx(string text, double size, IBrush fg, bool bold = false, TextAlignment align = TextAlignment.Left, bool semi = false) =>
        new()
        {
            Text = text,
            FontSize = size,
            Foreground = fg,
            FontWeight = bold ? FontWeight.Bold : semi ? FontWeight.SemiBold : FontWeight.Normal,
            TextAlignment = align,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static T At<T>(T control, int column, int row = 0, int columnSpan = 1) where T : Control
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        if (columnSpan > 1)
            Grid.SetColumnSpan(control, columnSpan);
        return control;
    }

    /// <summary>Кнопка/плашка с центрированной надписью нужного контраста.</summary>
    private static Border Pill(Mini m, string text, IBrush fill, double size, double radius, double h = double.NaN, IBrush? stroke = null, bool bold = true, IBrush? fg = null, double w = double.NaN) =>
        Box(fill, radius, stroke, 1, new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeight.Bold : FontWeight.SemiBold,
            Foreground = fg ?? m.On(fill),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        }, w, h, padding: new Thickness(double.IsNaN(w) ? 8 : 0, 0)); // 2026-10-23: у узких кнопок (−/+) без полей, иначе знак обрезается

    /// <summary>Плитка товара: «фото», название, цена. fill — свой цвет плитки (минимал), иначе фон плитки темы.</summary>
    private static Border Tile(Mini m, string name, double price, double nameSize = 12, double priceSize = 13, IBrush? fill = null, bool plus = false)
    {
        var photo = fill is null ? m.Photo : null;
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        if (photo is not null)
        {
            // «Фото» товара: мягкая подложка с кружком-силуэтом, чтобы плитка не выглядела пустой.
            var glyph = new Ellipse { Width = 26, Height = 26, Fill = m.Accent, Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(At(Box(photo, Math.Max(2, m.Radius * 0.6), child: glyph, margin: new Thickness(0, 0, 0, 6)), 0, 0));
        }
        var nameBrush = fill is null ? m.Text : m.On(fill);
        grid.Children.Add(At(Tx(name, m.F(nameSize), nameBrush, semi: true), 0, 1));
        var priceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 0) };
        priceRow.Children.Add(At(Tx($"{Rub(price)} {Som}", m.F(priceSize), fill is null ? m.Price : nameBrush, bold: true), 0));
        if (plus)
            priceRow.Children.Add(At(Pill(m, "+", m.Accent, 14, 11, 22, w: 22), 1));
        grid.Children.Add(At(priceRow, 0, 2));
        return Box(fill ?? m.TileBg, m.Radius, fill is null ? m.TileBorder : null, m.TileBorderWidth, grid, padding: new Thickness(8));
    }

    private static Grid TileGrid(Mini m, int columns, int rows, int firstProduct, double gap, Func<int, Control> make)
    {
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        for (var i = 0; i < columns * rows; i++)
        {
            var tile = make(firstProduct + i);
            tile.Margin = new Thickness(i % columns == 0 ? 0 : gap / 2, i / columns == 0 ? 0 : gap / 2, i % columns == columns - 1 ? 0 : gap / 2, i / columns == rows - 1 ? 0 : gap / 2);
            grid.Children.Add(At(tile, i % columns, i / columns));
        }
        return grid;
    }

    private static Border ProductTile(Mini m, int index, double nameSize = 12, double priceSize = 13, IBrush? fill = null, bool plus = false)
    {
        var products = Products();
        var (name, price) = products[index % products.Length];
        return Tile(m, name, price, nameSize, priceSize, fill, plus);
    }

    /// <summary>Шапка кассы: значок и название магазина, поиск, «Смена открыта».</summary>
    private static Border Header(Mini m, double height = 38)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Box(m.Accent, Math.Max(2, m.Radius * 0.5), w: 20, h: 20));
        brand.Children.Add(Tx("Nur Demo", m.F(14), m.Text, bold: true));
        grid.Children.Add(At(brand, 0));
        var search = Box(m.PanelSoft, m.Radius, child: Tx(Tr.T("Поиск товара или штрихкод", "Товар же штрихкод издөө", "Search item or barcode", "Ürün veya barkod ara", "Mahsulot yoki shtrix-kod qidirish"), m.F(12), m.TextSoft),
            h: 26, padding: new Thickness(10, 0), margin: new Thickness(20, 0));
        search.MaxWidth = 260;
        search.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(At(search, 1));
        var shift = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        shift.Children.Add(Box(m.Success, 5, w: 10, h: 10));
        shift.Children.Add(Tx(Tr.T("Смена открыта", "Смена ачык", "Shift open", "Vardiya açık", "Smena ochiq"), m.F(12), m.TextSoft));
        grid.Children.Add(At(shift, 2));
        return new Border { Height = height, Background = m.Panel, BorderBrush = m.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    private static Control Chips(Mini m, double size = 12)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var labels = new[]
        {
            Tr.T("Все", "Баары", "All", "Tümü", "Hammasi"),
            Tr.T("Напитки", "Суусундуктар", "Drinks", "İçecekler", "Ichimliklar"),
            Tr.T("Сладкое", "Таттуу", "Sweets", "Tatlılar", "Shirinlik"),
            Tr.T("Выпечка", "Нан-токоч", "Bakery", "Fırın", "Non"),
        };
        for (var i = 0; i < labels.Length; i++)
        {
            var chip = i == 0
                ? Pill(m, labels[i], m.Accent, m.F(size), 14, 26, bold: false)
                : Pill(m, labels[i], m.Panel, m.F(size), 14, 26, m.Border, bold: false, fg: m.Text);
            row.Children.Add(chip);
        }
        return row;
    }

    /// <summary>Чек: заголовок, строки образца (название ×кол-во — сумма), итог и кнопка. stepper — кнопки −/+ (вид «Карточки»).</summary>
    private static Border ReceiptCard(Mini m, int lines, bool stepper, IBrush payFill, double payHeight, string? payText = null, double lineSize = 12, bool customer = false)
    {
        var products = Products();
        var total = SampleTotal();
        var grid = new Grid { RowDefinitions = new RowDefinitions(customer ? "Auto,Auto,*,Auto,Auto" : "Auto,*,Auto,Auto") };
        var r = 0;
        grid.Children.Add(At(Tx(Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek"), m.F(14), m.Text, bold: true), 0, r++));
        if (customer)
        {
            var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
            who.Children.Add(Box(m.PanelSoft, 9, w: 18, h: 18));
            who.Children.Add(Tx(Tr.T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor"), m.F(12), m.TextSoft));
            grid.Children.Add(At(who, 0, r++));
        }
        var list = new StackPanel { Spacing = stepper ? 6 : 5, Margin = new Thickness(0, 8, 0, 6) };
        for (var i = 0; i < Math.Min(lines, SampleReceipt.Length); i++)
        {
            var (p, q) = SampleReceipt[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(stepper ? "*,Auto,Auto" : "*,Auto") };
            row.Children.Add(At(Tx(stepper ? products[p].Name : $"{products[p].Name} ×{q}", m.F(lineSize), m.Text), 0));
            if (stepper)
            {
                var qty = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
                qty.Children.Add(Pill(m, "−", m.PanelSoft, 12, 6, 20, fg: m.Text, w: 20));
                qty.Children.Add(Tx(q.ToString(), m.F(12), m.Text, bold: true));
                qty.Children.Add(Pill(m, "+", m.PanelSoft, 12, 6, 20, fg: m.Text, w: 20));
                row.Children.Add(At(qty, 1));
            }
            row.Children.Add(At(Tx(Rub(products[p].Price * q), m.F(lineSize), m.Text, bold: true, align: TextAlignment.Right), stepper ? 2 : 1));
            list.Children.Add(row);
        }
        grid.Children.Add(At(list, 0, r++));
        var totalRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        totalRow.Children.Add(At(Tx(Tr.T("Итого", "Жыйыны", "Total", "Toplam", "Jami"), m.F(13), m.TextSoft), 0));
        totalRow.Children.Add(At(Tx($"{Rub(total)} {Som}", m.F(17), m.Text, bold: true), 1));
        grid.Children.Add(At(totalRow, 0, r++));
        grid.Children.Add(At(Pill(m, payText ?? Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), payFill, m.F(15), m.Radius, payHeight), 0, r));
        return Box(m.Panel, m.Radius, m.Border, 1, grid, padding: new Thickness(12));
    }

    // ------------------------------------------------------------------ мини-касса в теме (640×360)

    /// <summary>Мини-касса 640×360: шапка, чипы категорий, 6 плиток 2×3 с названием и ценой, чек из 3 строк, кнопка «Оплатить».</summary>
    private static Control MiniKassa(Mini m)
    {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,224"), Margin = new Thickness(12) };
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        left.Children.Add(At(Box(null, 0, child: Chips(m), margin: new Thickness(0, 0, 0, 10)), 0, 0));
        left.Children.Add(At(TileGrid(m, 3, 2, 0, 8, i => ProductTile(m, i)), 0, 1));
        body.Children.Add(At(left, 0));
        var receipt = ReceiptCard(m, 3, false, m.Accent, 42);
        receipt.Margin = new Thickness(12, 0, 0, 0);
        body.Children.Add(At(receipt, 1));

        var root = new Grid { Width = 640, Height = 360, RowDefinitions = new RowDefinitions("Auto,*"), Background = m.Window };
        root.Children.Add(At(Header(m), 0, 0));
        root.Children.Add(At(body, 0, 1));
        return root;
    }

    // ------------------------------------------------------------------ виды кассы (640×384)

    /// <summary>Крупная миниатюра раскладки в цветах текущей темы программы (ТЗ 3.9: обложка вида, пока нет снимков).</summary>
    internal static Control LayoutHero(string layoutId)
    {
        var viewbox = new Viewbox { Stretch = Stretch.Uniform };
        var frame = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderThickness = new Thickness(1), Child = viewbox };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("BrushBorderStrong"));
        var caption = new TextBlock
        {
            Text = Tr.T("Схема вида в цветах вашей темы", "Көрүнүштүн схемасы сиздин теманын түстөрүндө", "A sketch of the layout in your theme's colors", "Görünümün temanızın renklerindeki şeması", "Ko'rinish sxemasi mavzuingiz ranglarida"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushTextSoft"));
        var root = new StackPanel { Spacing = 10, MaxWidth = MaxHeroWidth, Children = { frame, caption } };
        root.AttachedToVisualTree += (_, _) =>
        {
            try
            {
                viewbox.Child = LayoutCanvas(layoutId, Mini.FromApp());
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Маркетплейс: вид кассы «{layoutId}»: {ex.Message}", "WARNING");
                viewbox.Child = new Viewbox { Width = 640, Height = 384, Stretch = Stretch.Uniform, Child = KassaLayouts.BuildPreview(layoutId, 220, 132) };
            }
        };
        root.DetachedFromVisualTree += (_, _) => viewbox.Child = null;
        return root;
    }

    private static Control LayoutCanvas(string id, Mini m)
    {
        var root = new Grid { Width = 640, Height = 384, Background = m.Window };
        root.Children.Add(id switch
        {
            KassaLayouts.Table => TableLayout(m),
            KassaLayouts.Cards => CardsLayout(m),
            KassaLayouts.Minimal => MinimalLayout(m),
            KassaLayouts.Pro => ProLayout(m),
            KassaLayouts.OneC => OneCLayout(m),
            _ => StandardLayout(m),
        });
        return root;
    }

    private static Control StandardLayout(Mini m)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(At(Header(m), 0, 0));
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,216"), Margin = new Thickness(12) };
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        left.Children.Add(At(Box(null, 0, child: Chips(m), margin: new Thickness(0, 0, 0, 10)), 0, 0));
        left.Children.Add(At(TileGrid(m, 4, 3, 0, 8, i => ProductTile(m, i, 11.5, 12.5)), 0, 1));
        body.Children.Add(At(left, 0));
        var receipt = ReceiptCard(m, 4, false, m.Accent, 40);
        receipt.Margin = new Thickness(12, 0, 0, 0);
        body.Children.Add(At(receipt, 1));
        grid.Children.Add(At(body, 0, 1));
        return grid;
    }

    private static Control TableLayout(Mini m)
    {
        var products = Products();
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12) };
        // Строка штрихкода сверху.
        var scan = Box(m.Panel, m.Radius, m.Warning, 1.5,
            new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    At(Tx(Tr.T("Штрихкод: наведите сканер или введите вручную", "Штрихкод: сканерди багыттаңыз же кол менен киргизиңиз", "Barcode: aim the scanner or type it", "Barkod: okuyucuyu tutun veya elle girin", "Shtrix-kod: skanerni yo'naltiring yoki qo'lda kiriting"), m.F(12), m.TextSoft), 0),
                    At(Tx("4870000000006", m.F(12), m.Text, bold: true), 1),
                },
            }, h: 30, margin: new Thickness(0, 0, 0, 10), padding: new Thickness(10, 0));
        grid.Children.Add(At(scan, 0, 0));
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,196") };

        // Таблица чека: № / товар / кол-во / цена / сумма.
        string cols = "24,*,34,46,56";
        Grid Row(string c, Control[] cells)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(c), Height = 24 };
            for (var i = 0; i < cells.Length; i++)
                row.Children.Add(At(cells[i], i));
            return row;
        }
        var table = new StackPanel();
        table.Children.Add(Box(m.PanelSoft, 0, child: Row(cols, [
            Tx("№", m.F(11), m.TextSoft, bold: true),
            Tx(Tr.T("Товар", "Товар", "Item", "Ürün", "Mahsulot"), m.F(11), m.TextSoft, bold: true),
            Tx(Tr.T("Кол.", "Саны", "Qty", "Adet", "Soni"), m.F(11), m.TextSoft, bold: true, align: TextAlignment.Right),
            Tx(Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx"), m.F(11), m.TextSoft, bold: true, align: TextAlignment.Right),
            Tx(Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"), m.F(11), m.TextSoft, bold: true, align: TextAlignment.Right),
        ]), padding: new Thickness(8, 0)));
        for (var i = 0; i < SampleReceipt.Length; i++)
        {
            var (p, q) = SampleReceipt[i];
            table.Children.Add(new Border
            {
                BorderBrush = m.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8, 0),
                Child = Row(cols, [
                    Tx((i + 1).ToString(), m.F(12), m.TextSoft),
                    Tx(products[p].Name, m.F(12), m.Text),
                    Tx(q.ToString(), m.F(12), m.Text, align: TextAlignment.Right),
                    Tx(Rub(products[p].Price), m.F(12), m.Text, align: TextAlignment.Right),
                    Tx(Rub(products[p].Price * q), m.F(12), m.Text, bold: true, align: TextAlignment.Right),
                ]),
            });
        }
        var tableCard = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        tableCard.Children.Add(At(table, 0, 0));
        var total = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 6, 10, 10) };
        total.Children.Add(At(Tx(Tr.T("ИТОГО", "ЖЫЙЫНЫ", "TOTAL", "TOPLAM", "JAMI"), m.F(13), m.TextSoft, bold: true), 0));
        total.Children.Add(At(Tx($"{Rub(SampleTotal())} {Som}", m.F(24), m.Text, bold: true), 1));
        tableCard.Children.Add(At(total, 0, 1));
        var tableBox = Box(m.Panel, m.Radius, m.Border, 1, tableCard, margin: new Thickness(0, 0, 10, 0));
        tableBox.ClipToBounds = true;
        body.Children.Add(At(tableBox, 0));

        // Справа: быстрые товары и цветные кнопки оплаты.
        var right = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        right.Children.Add(At(TileGrid(m, 2, 3, 4, 6, i => ProductTile(m, i, 11, 11.5)), 0, 0));
        var pay = new UniformGrid { Columns = 2, Margin = new Thickness(0, 8, 0, 0) };
        void PayBtn(string text, IBrush fill, IBrush? fg = null) => pay.Children.Add(Pill(m, text, fill, m.F(11.5), m.Radius, 30, fg: fg, stroke: fill == m.PanelSoft ? m.Border : null));
        PayBtn(Tr.T("Наличные", "Накта", "Cash", "Nakit", "Naqd"), m.Success);
        PayBtn(Tr.T("Карта", "Карта", "Card", "Kart", "Karta"), m.Accent, m.AccentFg);
        PayBtn(Tr.T("Перевод", "Которуу", "Transfer", "Havale", "O'tkazma"), m.Warning);
        PayBtn(Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), m.PanelSoft, m.Text);
        right.Children.Add(At(pay, 0, 1));
        var payAll = Pill(m, Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"), m.Success, m.F(15), m.Radius, 38);
        payAll.Margin = new Thickness(0, 6, 0, 0);
        right.Children.Add(At(payAll, 0, 2));
        body.Children.Add(At(right, 1));
        grid.Children.Add(At(body, 0, 1));
        return grid;
    }

    private static Control CardsLayout(Mini m)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,224"), Margin = new Thickness(12) };
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        left.Children.Add(At(Box(null, 0, child: Chips(m, 12.5), margin: new Thickness(0, 0, 0, 10)), 0, 0));
        left.Children.Add(At(TileGrid(m, 3, 2, 0, 10, i => ProductTile(m, i, 12.5, 13.5, plus: true)), 0, 1));
        grid.Children.Add(At(left, 0));
        var receipt = ReceiptCard(m, 4, true, m.Success, 52, Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash") + $" {Rub(SampleTotal())}");
        receipt.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(At(receipt, 1));
        return grid;
    }

    private static Control MinimalLayout(Mini m)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,208") };
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(18, 16, 14, 16) };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(0, 0, 0, 14) };
        var labels = new[] { Tr.T("Все", "Баары", "All", "Tümü", "Hammasi"), Tr.T("Напитки", "Суусундуктар", "Drinks", "İçecekler", "Ichimliklar"), Tr.T("Сладкое", "Таттуу", "Sweets", "Tatlılar", "Shirinlik") };
        for (var i = 0; i < labels.Length; i++)
            tabs.Children.Add(new Border
            {
                BorderBrush = i == 0 ? m.Accent : null, BorderThickness = new Thickness(0, 0, 0, i == 0 ? 3 : 0), Padding = new Thickness(0, 0, 0, 4),
                Child = Tx(labels[i], m.F(14), i == 0 ? m.Text : m.TextSoft, bold: i == 0),
            });
        left.Children.Add(At(tabs, 0, 0));
        IBrush[] colors = [m.Accent, m.Success, m.Warning, m.Warning, m.Accent, m.Success];
        left.Children.Add(At(TileGrid(m, 3, 2, 0, 12, i => ProductTile(m, i, 13.5, 14, colors[i % colors.Length])), 0, 1));
        grid.Children.Add(At(left, 0));

        var sale = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        sale.Children.Add(At(Tx(Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek"), m.F(14), m.Text, bold: true), 0, 0));
        var products = Products();
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < 3; i++)
        {
            var (p, q) = SampleReceipt[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(At(Tx($"{products[p].Name} ×{q}", m.F(12.5), m.Text), 0));
            row.Children.Add(At(Tx(Rub(products[p].Price * q), m.F(12.5), m.Text, bold: true), 1));
            list.Children.Add(row);
        }
        sale.Children.Add(At(list, 0, 1));
        sale.Children.Add(At(Pill(m, Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash") + $" {Rub(SampleTotal())} {Som}", m.Accent, m.F(15), m.Radius, 64, fg: m.AccentFg), 0, 2));
        grid.Children.Add(At(Box(m.Panel, 0, child: sale, padding: new Thickness(14)), 1));
        return grid;
    }

    private static Control ProLayout(Mini m)
    {
        var products = Products();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,208") };
        var rail = new StackPanel { Background = m.Rail, Spacing = 14 };
        rail.Children.Add(Box(m.Accent, 5, w: 20, h: 20, margin: new Thickness(12, 18, 12, 4)));
        for (var i = 0; i < 4; i++)
            rail.Children.Add(Box(B("#64748B"), 5, w: 20, h: 20, margin: new Thickness(12, 0)));
        grid.Children.Add(At(rail, 0));

        var center = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        center.Children.Add(At(new Border
        {
            Background = m.Rail, Padding = new Thickness(12, 10),
            Child = Box(m.Panel, m.Radius, child: Tx(Tr.T("Поиск: название, артикул или штрихкод", "Издөө: аталышы, артикул же штрихкод", "Search: name, SKU or barcode", "Ara: ad, stok kodu veya barkod", "Qidiruv: nomi, artikul yoki shtrix-kod"), m.F(13), m.TextSoft), h: 32, padding: new Thickness(12, 0)),
        }, 0, 0));
        var list = new StackPanel { Margin = new Thickness(12, 8, 8, 0) };
        for (var i = 0; i < 8; i++)
        {
            var (name, price) = products[i % products.Length];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,64,70"), Height = 31 };
            row.Children.Add(At(Tx(name, m.F(12.5), m.Text, semi: i == 0), 0));
            row.Children.Add(At(Tx(Tr.T("ост. ", "кал. ", "left ", "kalan ", "qoldi ") + (12 + i * 3), m.F(11.5), m.TextSoft, align: TextAlignment.Right), 1));
            row.Children.Add(At(Tx(Rub(price), m.F(12.5), m.Price, bold: true, align: TextAlignment.Right), 2));
            list.Children.Add(new Border
            {
                Background = i == 0 ? m.PanelSoft : null, BorderBrush = m.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8, 0), Child = row,
            });
        }
        center.Children.Add(At(list, 0, 1));
        grid.Children.Add(At(center, 1));

        var receipt = ReceiptCard(m, 4, false, m.Success, 42, customer: true);
        receipt.CornerRadius = new CornerRadius(0);
        receipt.BorderThickness = new Thickness(1, 0, 0, 0);
        grid.Children.Add(At(receipt, 2));
        return grid;
    }

    private static Control OneCLayout(Mini m)
    {
        var products = Products();
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12) };
        grid.Children.Add(At(Box(m.Panel, m.Radius, m.Warning, 1.5,
            new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    At(Tx(Tr.T("Сканер: отсканируйте штрихкод", "Сканер: штрихкодду сканерлеңиз", "Scanner: scan a barcode", "Okuyucu: barkodu okutun", "Skaner: shtrix-kodni skanerlang"), m.F(12.5), m.TextSoft), 0),
                    At(Tx("4870000000014", m.F(12.5), m.Text, bold: true), 1),
                },
            }, h: 32, margin: new Thickness(0, 0, 0, 10), padding: new Thickness(10, 0)), 0, 0));

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,176") };
        const string cols = "26,*,40,48,60";
        var table = new StackPanel();
        Grid Row(string[] cells, bool head, bool active)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(cols), Height = active ? 40 : 28 };
            for (var i = 0; i < cells.Length; i++)
            {
                var size = active ? 14 : 12;
                row.Children.Add(At(Tx(cells[i], m.F(head ? 11 : size), head ? m.TextSoft : m.Text, bold: head || active && i == 4, align: i >= 2 ? TextAlignment.Right : TextAlignment.Left), i));
            }
            return row;
        }
        table.Children.Add(Box(m.PanelSoft, 0, child: Row(["№", Tr.T("Товар", "Товар", "Item", "Ürün", "Mahsulot"), Tr.T("Кол.", "Саны", "Qty", "Adet", "Soni"), Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narx"), Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa")], true, false), padding: new Thickness(8, 0)));
        for (var i = 0; i < SampleReceipt.Length; i++)
        {
            var (p, q) = SampleReceipt[i];
            var active = i == 1;
            table.Children.Add(new Border
            {
                Background = active ? B(AccentHexOr(m.Accent, "#FFB208", m.Panel)) : null,
                BorderBrush = active ? m.Accent : m.Border, BorderThickness = new Thickness(active ? 2 : 0, active ? 2 : 0, active ? 2 : 0, active ? 2 : 1), Padding = new Thickness(6, 0),
                Child = Row([(i + 1).ToString(), products[p].Name, q.ToString(), Rub(products[p].Price), Rub(products[p].Price * q)], false, active),
            });
        }
        var tableBox = Box(m.Panel, m.Radius, m.Border, 1, table, margin: new Thickness(0, 0, 10, 0));
        tableBox.ClipToBounds = true;
        body.Children.Add(At(tableBox, 0));

        var side = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var totals = new StackPanel { Spacing = 2 };
        totals.Children.Add(Tx(Tr.T("Итого", "Жыйыны", "Total", "Toplam", "Jami"), m.F(12), m.TextSoft));
        totals.Children.Add(Tx($"{Rub(SampleTotal())} {Som}", m.F(22), m.Text, bold: true));
        side.Children.Add(At(Box(m.Panel, m.Radius, m.Border, 1, totals, padding: new Thickness(10), margin: new Thickness(0, 0, 0, 10)), 0, 0));
        var fav = new StackPanel { Spacing = 5 };
        fav.Children.Add(Tx(Tr.T("Избранное", "Тандалмалар", "Favorites", "Favoriler", "Tanlanganlar"), m.F(12), m.TextSoft, bold: true));
        for (var i = 4; i < 8; i++)
            fav.Children.Add(Box(m.Panel, m.Radius, m.Border, 1, Tx(products[i].Name, m.F(12), m.Text), h: 26, padding: new Thickness(8, 0)));
        side.Children.Add(At(fav, 0, 1));
        body.Children.Add(At(side, 1));
        grid.Children.Add(At(body, 0, 1));

        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        var names = new[]
        {
            "F2 " + Tr.T("Оплата", "Төлөм", "Pay", "Ödeme", "To'lov"),
            "F4 " + Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"),
            "F6 " + Tr.T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"),
            "F9 " + Tr.T("Отложить", "Токтотуу", "Hold", "Beklet", "Kechiktirish"),
        };
        foreach (var n in names)
            keys.Children.Add(Pill(m, n, m.PanelSoft, m.F(12), m.Radius, 28, m.Success, bold: false, fg: m.Text, w: 120));
        grid.Children.Add(At(keys, 0, 2));
        return grid;
    }

    /// <summary>Мягкая подложка активной строки: акцент, сильно разбавленный фоном панели.</summary>
    private static string AccentHexOr(IBrush accent, string fallback, IBrush panel)
    {
        string Hex(IBrush b, string f) => b is ISolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : f;
        return AccentThemeService.MixHex(Hex(accent, fallback), Hex(panel, "#FFFFFF"), 0.82);
    }

    // ------------------------------------------------------------------ экран покупателя

    /// <summary>Живой предпросмотр экрана покупателя (своя модель с образцом чека — настоящий экран не трогается): настройки экрана —
    /// ваши, масштаб не мельче 0,5 (640×360 при ширине колонки ≥ 640); переключатель образца «Чек | Пустой | Оплата принята».</summary>
    internal static Control CustomerDisplayHero()
    {
        var viewbox = new Viewbox { Stretch = Stretch.Uniform };
        var frame = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, BorderThickness = new Thickness(1), Child = viewbox };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("BrushBorderStrong"));
        var caption = new TextBlock
        {
            Text = Tr.T("Как будет у вас: экран с вашими настройками и образцом чека", "Сизде мындай болот: экран сиздин жөндөөлөрүңүз жана чектин үлгүсү менен", "How it will look for you: the screen with your settings and a sample receipt", "Sizde böyle olacak: ayarlarınızla ve örnek fişle ekran", "Sizda shunday bo'ladi: sozlamalaringiz va chek namunasi bilan ekran"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushTextSoft"));

        var receiptChip = SegmentButton(Tr.T("Чек", "Чек", "Receipt", "Fiş", "Chek"));
        var emptyChip = SegmentButton(Tr.T("Пустой экран", "Бош экран", "Empty screen", "Boş ekran", "Bo'sh ekran"));
        var paidChip = SegmentButton(Tr.T("Оплата принята", "Төлөм кабыл алынды", "Payment accepted", "Ödeme alındı", "To'lov qabul qilindi"));
        var segment = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left, Children = { receiptChip, emptyChip, paidChip } };
        foreach (var chip in new[] { receiptChip, emptyChip, paidChip })
            chip.Margin = new Thickness(0, 0, 8, 6);

        CustomerDisplayStateService? state = null;
        NurMarketKassa.AvaloniaHost.ViewModels.CustomerDisplayViewModel? vm = null;
        NurMarketKassa.AvaloniaHost.Views.Customer.CustomerDisplayView? view = null;
        var sample = "receipt";

        void Fill()
        {
            if (state is null)
                return;
            SetActive(receiptChip, sample == "receipt");
            SetActive(emptyChip, sample == "empty");
            SetActive(paidChip, sample == "paid");
            state.SetPaymentStatus(sample == "paid" ? CustomerDisplayPaymentStatus.Success : CustomerDisplayPaymentStatus.Idle);
            state.UpdateCart(sample == "empty"
                ? new CustomerDisplayCartSnapshot()
                : NurMarketKassa.AvaloniaHost.ViewModels.CustomerDisplayViewModel.BuildSampleSnapshot(paid: sample == "paid"));
        }

        void Release()
        {
            viewbox.Child = null;
            if (view is not null)
                view.DataContext = null;
            vm?.Dispose();
            vm = null;
            view = null;
            state = null;
        }

        receiptChip.Click += (_, _) => { sample = "receipt"; Fill(); };
        emptyChip.Click += (_, _) => { sample = "empty"; Fill(); };
        paidChip.Click += (_, _) => { sample = "paid"; Fill(); };

        var root = new StackPanel { Spacing = 10, MaxWidth = MaxHeroWidth, Children = { segment, frame, caption } };
        // Экран строим только когда блок попал в дерево, и разбираем при уходе — модель не остаётся «висеть».
        root.AttachedToVisualTree += (_, _) =>
        {
            Release();
            try
            {
                state = new CustomerDisplayStateService();
                vm = NurMarketKassa.AvaloniaHost.ViewModels.CustomerDisplayViewModel.CreateDesignPreview(state, UserPreferences.Instance.CustomerDisplay);
                view = new NurMarketKassa.AvaloniaHost.Views.Customer.CustomerDisplayView { Width = 1280, Height = 720, DataContext = vm };
                viewbox.Child = view;
                Fill();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Маркетплейс: предпросмотр экрана покупателя: {ex.Message}", "WARNING");
                Release();
            }
        };
        root.DetachedFromVisualTree += (_, _) => Release();
        return root;
    }

    // ------------------------------------------------------------------ значок темы (плитка витрины)

    /// <summary>Миниатюра темы её же цветами: фон окна, плитки каталога, кнопка оплаты. Для значка (hero == false) — без подписи.</summary>
    internal static Control ThemePreview(string themeId, bool dark, double w, double h, bool hero)
    {
        if (AccentThemeService.GetPreview(themeId, dark) is not { } pv)
            return new Border { Width = w, Height = h, CornerRadius = new CornerRadius(hero ? 14 : 10), Background = Brushes.Gray };

        var window = new SolidColorBrush(Color.Parse(pv.Window));
        var tile = new SolidColorBrush(Color.Parse(pv.Tile));
        var tileBorder = new SolidColorBrush(Color.Parse(pv.TileBorder));
        var accent = new SolidColorBrush(Color.Parse(pv.Accent));
        var text = new SolidColorBrush(Color.Parse(pv.Text));

        var pad = Math.Round(w * (hero ? 0.06 : 0.1));
        var gap = Math.Round(w * 0.035);
        var tileW = (w - pad * 2 - gap * 2) / 3;
        var tileH = h * (hero ? 0.34 : 0.3);
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap };
        for (var i = 0; i < 3; i++)
            tiles.Children.Add(new Border
            {
                Width = tileW,
                Height = tileH,
                Background = tile,
                BorderBrush = tileBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(hero ? 8 : 4),
                Child = new Border
                {
                    Height = Math.Max(3, tileH * 0.14),
                    Width = tileW * 0.55,
                    Margin = new Thickness(tileW * 0.12, 0, 0, tileH * 0.14),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    CornerRadius = new CornerRadius(2),
                    Background = accent,
                },
            });

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(tiles);
        stack.Children.Add(new Border
        {
            Height = h * (hero ? 0.14 : 0.16),
            Margin = new Thickness(0, h * 0.07, 0, 0),
            CornerRadius = new CornerRadius(hero ? 8 : 4),
            Background = accent,
        });
        if (hero)
            stack.Children.Add(new TextBlock
            {
                Text = Tr.T("Каталог и кнопка оплаты", "Каталог жана төлөм баскычы", "Catalog and pay button", "Katalog ve ödeme düğmesi", "Katalog va to'lov tugmasi"),
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = text,
                Opacity = 0.65,
            });

        return new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(hero ? 14 : 10),
            Background = window,
            Padding = new Thickness(pad, pad, pad, pad),
            Child = stack,
        };
    }
}
