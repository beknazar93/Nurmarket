using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Controls;

/// <summary>2026-10-27, ТЗ «Сайт — темы для магазина одежды» (С-2): местный предпросмотр готовой темы сайта — «мини-сайт» в её цветах:
/// шапка, главный блок с картинкой темы, категории, карточки 3:4 (значки, цена, старая цена, остаток, кнопка), подвал. Рисуется кодом
/// по тем же ключам, что читает витрина market.nurcrm.kg (раздел 1.3 ТЗ), поэтому показывает именно то, что уйдёт в настройки.
/// Никаких запросов на сервер: только ресурсы программы. Тексты не рисуются (названия и подписи — в окне программы), цифры цен условные.
/// Цвета — локальные кисти: тема программы не перекрашивается.</summary>
public static class SiteThemePreview
{
    /// <summary>Мини-сайт темы. phone — вид телефона (ширина 375, колонки для телефона), иначе компьютер (ширина 1000).
    /// Высота — по содержимому (две строки карточек).</summary>
    public static Control Build(SiteThemeInfo theme, bool phone)
    {
        var p = theme.Patch;
        var colors = p["theme"]?["colors"] as JsonObject;
        IBrush Br(string key, string fallback) => new SolidColorBrush(Hex(colors?[key]?.ToString(), fallback));
        Color Cl(string key, string fallback) => Hex(colors?[key]?.ToString(), fallback);

        var width = phone ? 375d : 1000d;
        var k = phone ? 0.8 : 1.0; // масштаб мелких деталей
        var radius = Math.Clamp(p["theme"]?["radius"]?.GetValue<double>() ?? 8, 0, 28) * (phone ? 0.8 : 1);
        var buttonStyle = p["theme"]?["button_style"]?.ToString() ?? "filled";
        var accent = Cl("accent", "#F7D74F");
        var accentText = Br("accent_text", "#111111");
        var textBrush = Br("text", "#111111");
        var mutedBrush = Br("text_muted", "#666666");
        var borderBrush = Br("border", "#E5E5E5");
        var surface = Br("surface", "#FFFFFF");

        var root = new StackPanel { Width = width, Background = Br("background", "#FFFFFF"), ClipToBounds = true };

        // ---- шапка
        var headerH = 56 * k;
        var header = new Grid { Height = headerH, Background = Br("header_bg", "#FFFFFF") };
        var hText = Br("header_text", "#111111");
        var center = (p["header"]?["layout"]?.ToString() ?? "logo_left") == "logo_center";
        Control Logo() => new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8 * k, VerticalAlignment = VerticalAlignment.Center,
            Children = { new Border { Width = 26 * k, Height = 26 * k, CornerRadius = new CornerRadius(Math.Min(radius, 13 * k)), Background = new SolidColorBrush(accent) }, Bar(70 * k, 9 * k, hText) },
        };
        if (center)
        {
            var logo = Logo();
            logo.HorizontalAlignment = HorizontalAlignment.Center;
            header.Children.Add(logo);
            header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * k, Margin = new Thickness(18 * k, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Bar(22 * k, 3 * k, hText), } });
            header.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12 * k, Margin = new Thickness(18 * k, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Children = { Circle(16 * k, null, hText), Circle(16 * k, hText, null) },
            });
        }
        else
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(phone ? "Auto,*,Auto" : "Auto,*,Auto"), Margin = new Thickness(18 * k, 0), VerticalAlignment = VerticalAlignment.Center };
            var logo = Logo();
            row.Children.Add(logo);
            if (!phone)
            {
                var search = new Border
                {
                    Height = 30, Margin = new Thickness(40, 0), CornerRadius = new CornerRadius(Math.Min(radius, 15)), BorderThickness = new Thickness(1), BorderBrush = borderBrush,
                    Background = Br("surface", "#FFFFFF"), Child = Bar(90, 7, mutedBrush, new Thickness(12, 0, 0, 0)),
                };
                Grid.SetColumn(search, 1);
                row.Children.Add(search);
            }
            var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 * k, Children = { Circle(16 * k, null, hText), Circle(16 * k, hText, null) } };
            Grid.SetColumn(icons, 2);
            row.Children.Add(icons);
            header.Children.Add(row);
        }
        root.Children.Add(new Border { BorderBrush = borderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = header });

        // ---- главный блок
        var heroStyle = p["hero"]?["style"]?.ToString() ?? "card";
        var overlay = Math.Clamp(p["hero"]?["overlay"]?.GetValue<double>() ?? 0.3, 0, 0.8);
        var alignCenter = (p["hero"]?["align"]?.ToString() ?? "left") == "center";
        if (heroStyle != "hidden")
        {
            var heroH = phone ? 200d : 250d;
            var image = LoadImage(theme, "hero.image", 960);
            Control Picture() => image is null
                ? new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(accent, 0), new GradientStop(Cl("background", "#FFFFFF"), 1) } } }
                : new Image { Source = image, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center };
            var cta = Button(buttonStyle, accent, accentText, radius, 120 * k, 34 * k, borderBrush);

            if (heroStyle == "cover")
            {
                var g = new Grid { Height = heroH, ClipToBounds = true };
                g.Children.Add(Picture());
                g.Children.Add(new Border { Background = new SolidColorBrush(Colors.Black, overlay) });
                var text = new StackPanel { Spacing = 10 * k, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(alignCenter ? 0 : 36 * k, 0),
                    HorizontalAlignment = alignCenter ? HorizontalAlignment.Center : HorizontalAlignment.Left };
                var al = alignCenter ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                text.Children.Add(Bar(240 * k, 18 * k, Brushes.White, al: al));
                text.Children.Add(Bar(170 * k, 9 * k, new SolidColorBrush(Colors.White, 0.8), al: al));
                cta.HorizontalAlignment = al;
                cta.Margin = new Thickness(0, 6 * k, 0, 0);
                text.Children.Add(cta);
                g.Children.Add(text);
                root.Children.Add(g);
            }
            else if (heroStyle == "split")
            {
                var g = new Grid { Height = heroH, ColumnDefinitions = new ColumnDefinitions("*,*"), Background = surface };
                var text = new StackPanel { Spacing = 10 * k, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30 * k, 0, 10 * k, 0) };
                text.Children.Add(Bar(190 * k, 16 * k, textBrush));
                text.Children.Add(Bar(140 * k, 9 * k, mutedBrush));
                cta.HorizontalAlignment = HorizontalAlignment.Left;
                cta.Margin = new Thickness(0, 6 * k, 0, 0);
                text.Children.Add(cta);
                g.Children.Add(text);
                var pic = new Border { ClipToBounds = true, Child = Picture() };
                Grid.SetColumn(pic, 1);
                g.Children.Add(pic);
                root.Children.Add(g);
            }
            else // card: скруглённая карточка на фоне страницы
            {
                var g = new Grid { Height = heroH - 30, Margin = new Thickness(18 * k, 14 * k), ColumnDefinitions = new ColumnDefinitions("*,*") };
                var text = new StackPanel { Spacing = 10 * k, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24 * k, 0, 10 * k, 0) };
                text.Children.Add(Bar(170 * k, 16 * k, textBrush));
                text.Children.Add(Bar(120 * k, 9 * k, mutedBrush));
                cta.HorizontalAlignment = HorizontalAlignment.Left;
                cta.Margin = new Thickness(0, 6 * k, 0, 0);
                text.Children.Add(cta);
                g.Children.Add(text);
                var pic = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(radius), Child = Picture() };
                Grid.SetColumn(pic, 1);
                g.Children.Add(pic);
                root.Children.Add(new Border { Background = surface, CornerRadius = new CornerRadius(radius), BorderBrush = borderBrush, BorderThickness = new Thickness(1), Margin = new Thickness(18 * k, 14 * k, 18 * k, 0), Child = g });
                g.Margin = default;
            }
        }

        // ---- категории
        var catStyle = p["categories"]?["style"]?.ToString() ?? "chips";
        if (catStyle != "hidden")
        {
            var cats = new WrapPanel { Margin = new Thickness(18 * k, 14 * k, 18 * k, 4 * k), Orientation = Orientation.Horizontal };
            var count = phone ? 3 : 5;
            for (var i = 0; i < count; i++)
            {
                var selected = i == 0;
                if (catStyle == "tiles")
                    cats.Children.Add(new Border
                    {
                        Width = (phone ? 100 : 150) * 1, Height = (phone ? 54 : 70), Margin = new Thickness(0, 0, 10 * k, 8 * k), CornerRadius = new CornerRadius(radius),
                        Background = selected ? new SolidColorBrush(accent) : surface, BorderBrush = borderBrush, BorderThickness = new Thickness(1),
                        Child = Bar(60 * k, 8 * k, selected ? accentText : textBrush, al: HorizontalAlignment.Center),
                    });
                else if (catStyle == "list")
                    cats.Children.Add(new Border
                    {
                        Padding = new Thickness(0, 4 * k, 18 * k, 6 * k), Margin = new Thickness(0, 0, 4 * k, 0),
                        BorderBrush = selected ? textBrush : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2),
                        Child = Bar((50 + (i % 3) * 14) * k, 8 * k, selected ? textBrush : mutedBrush),
                    });
                else
                    cats.Children.Add(new Border
                    {
                        Height = 28 * k, MinWidth = 70 * k, Margin = new Thickness(0, 0, 8 * k, 8 * k), CornerRadius = new CornerRadius(14 * k),
                        Background = selected ? new SolidColorBrush(accent) : surface, BorderBrush = selected ? new SolidColorBrush(accent) : borderBrush, BorderThickness = new Thickness(1),
                        Child = Bar((38 + (i % 3) * 10) * k, 7 * k, selected ? accentText : textBrush, al: HorizontalAlignment.Center),
                    });
            }
            root.Children.Add(cats);
        }

        // ---- карточки: две строки
        var cols = p["products"]?["columns"] as JsonObject;
        var n = Math.Clamp(cols?[phone ? "mobile" : "desktop"]?.GetValue<int>() ?? (phone ? 2 : 4), 1, 6);
        var gap = 12 * k;
        var side = 18 * k;
        var cardW = Math.Floor((width - side * 2 - gap * (n - 1)) / n - 1); // -1: запас от погрешности, иначе последняя карточка ряда уходит вниз
        var card = p["card"] as JsonObject;
        var show = card?["show"] as JsonObject;
        var template = card?["template"]?.ToString() ?? "standard";
        var cardShadow = card?["shadow"]?.GetValue<bool>() ?? false;
        var cardBorder = card?["border"]?.GetValue<bool>() ?? false;
        var stockMode = show?["stock"]?.ToString() ?? "hidden";
        var photo = LoadImage(theme, "card.placeholder_image", 320);
        var grid = new WrapPanel { Margin = new Thickness(side, 10 * k, side, side), Orientation = Orientation.Horizontal };
        var total = n * (phone ? 2 : 1); // на компьютере один ряд карточек, на телефоне два
        for (var i = 0; i < total; i++)
        {
            var photoBox = new Grid { Height = cardW * 4 / 3, ClipToBounds = true };
            photoBox.Children.Add(photo is null ? new Border { Background = borderBrush } : new Image { Source = photo, Stretch = Stretch.UniformToFill });
            if (show?["new_badge"]?.GetValue<bool>() == true && i % 3 == 0)
                photoBox.Children.Add(Badge(Br("badge_new_bg", "#047857"), Br("badge_new_text", "#FFFFFF"), radius, k, HorizontalAlignment.Left));
            else if (show?["discount_badge"]?.GetValue<bool>() == true && i % 3 == 1)
                photoBox.Children.Add(Badge(Br("badge_sale_bg", "#DC2626"), Br("badge_sale_text", "#FFFFFF"), radius, k, HorizontalAlignment.Right));
            var info = new StackPanel { Spacing = 5 * k, Margin = new Thickness(template == "compact" ? 6 * k : 10 * k, 8 * k, template == "compact" ? 6 * k : 10 * k, template == "compact" ? 8 * k : 12 * k) };
            if (show?["category"]?.GetValue<bool>() == true)
                info.Children.Add(Bar(40 * k, 6 * k, mutedBrush));
            info.Children.Add(Bar(cardW * 0.7, 8 * k, textBrush));
            var prices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 * k };
            prices.Children.Add(Price("2 490", Br("price", "#111111"), FontWeight.Bold, k, false));
            if (show?["old_price"]?.GetValue<bool>() == true && i % 3 == 1)
                prices.Children.Add(Price("3 100", Br("old_price", "#999999"), FontWeight.Normal, k, true));
            info.Children.Add(prices);
            if (stockMode == "always" || (stockMode == "low_only" && i % 3 == 2))
                info.Children.Add(Bar(52 * k, 6 * k, mutedBrush));
            if (show?["add_button"]?.GetValue<bool>() != false)
            {
                var b = Button(buttonStyle, accent, accentText, radius, double.NaN, (template == "compact" ? 24 : 30) * k, borderBrush);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Margin = new Thickness(0, 4 * k, 0, 0);
                info.Children.Add(b);
            }
            var body = new StackPanel { Children = { photoBox, info } };
            grid.Children.Add(new Border
            {
                Width = cardW, Margin = new Thickness(0, 0, i % n == n - 1 ? 0 : gap, gap), CornerRadius = new CornerRadius(radius), ClipToBounds = true, Background = surface,
                BorderBrush = cardBorder ? borderBrush : Brushes.Transparent, BorderThickness = new Thickness(cardBorder ? 1 : 0),
                BoxShadow = cardShadow ? new BoxShadows(new BoxShadow { Blur = 10, OffsetY = 3, Color = Color.FromArgb(40, 0, 0, 0) }) : default,
                Child = body,
            });
        }
        root.Children.Add(grid);

        // ---- подвал
        var fBrush = Br("footer_text", "#FFFFFF");
        if (p["footer"]?["enabled"]?.GetValue<bool>() != false)
            root.Children.Add(new Border
            {
                Height = 64 * k, Background = Br("footer_bg", "#111111"),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 * k, Margin = new Thickness(side, 0), VerticalAlignment = VerticalAlignment.Center,
                    Children = { Bar(80 * k, 9 * k, fBrush), Bar(60 * k, 6 * k, fBrush), Bar(60 * k, 6 * k, fBrush), Bar(50 * k, 6 * k, fBrush) } },
            });
        return root;
    }

    /// <summary>Миниатюра для галереи и Маркетплейса: верх мини-сайта (шапка, главный блок, категории, первый ряд карточек), обрезано по размеру.</summary>
    public static Control Thumb(SiteThemeInfo theme, double width, double height)
    {
        var box = new Viewbox { Stretch = Stretch.UniformToFill, StretchDirection = StretchDirection.Both, Child = new Border { Width = 1000, Height = 625, ClipToBounds = true, Child = new Canvas { Width = 1000, Height = 625, Children = { Build(theme, false) } } } };
        return new Border { Width = width, Height = height, ClipToBounds = true, Child = box };
    }

    // ------------------------------------------------------------------ помощники

    private static Color Hex(string? value, string fallback) =>
        Color.TryParse(value, out var c) ? c : Color.Parse(fallback);

    private static Border Bar(double w, double h, IBrush brush, Thickness? margin = null, HorizontalAlignment al = HorizontalAlignment.Left) =>
        new() { Width = w, Height = h, CornerRadius = new CornerRadius(h / 2), Background = brush, HorizontalAlignment = al, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? default };

    private static Control Circle(double size, IBrush? fill, IBrush? stroke) =>
        new Ellipse { Width = size, Height = size, Fill = fill, Stroke = stroke, StrokeThickness = stroke is null ? 0 : 2 };

    private static Border Badge(IBrush bg, IBrush fg, double radius, double k, HorizontalAlignment al) =>
        new() { Margin = new Thickness(8 * k), Padding = new Thickness(8 * k, 4 * k), CornerRadius = new CornerRadius(Math.Min(radius, 8)), Background = bg, HorizontalAlignment = al, VerticalAlignment = VerticalAlignment.Top,
            Child = Bar(26 * k, 6 * k, fg) };

    private static TextBlock Price(string text, IBrush brush, FontWeight weight, double k, bool strike) =>
        new() { Text = text, FontSize = 14 * k, FontWeight = weight, Foreground = brush, TextDecorations = strike ? TextDecorations.Strikethrough : null, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Кнопка по стилю темы: filled — залитая акцентом, outline — контур акцентом, soft — бледная заливка. Подписи нет: ставится полоска.</summary>
    private static Border Button(string style, Color accent, IBrush accentText, double radius, double w, double h, IBrush border)
    {
        var accentBrush = new SolidColorBrush(accent);
        IBrush bg = style switch { "outline" => Brushes.Transparent, "soft" => new SolidColorBrush(accent, 0.18), _ => accentBrush };
        IBrush bar = style == "filled" ? accentText : accentBrush;
        // подпись контурной/мягкой кнопки — цвет акцента, как и на сайте
        return new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(Math.Min(radius, h / 2)), Background = bg,
            BorderBrush = style == "outline" ? accentBrush : Brushes.Transparent, BorderThickness = new Thickness(style == "outline" ? 1.5 : 0),
            Child = Bar(Math.Max(24, (double.IsNaN(w) ? 80 : w) * 0.4), Math.Max(4, h * 0.2), bar, al: HorizontalAlignment.Center),
        };
    }

    private static Bitmap? LoadImage(SiteThemeInfo theme, string path, int decodeWidth)
    {
        try
        {
            foreach (var img in theme.Images)
            {
                if (img.Path != path)
                    continue;
                var uri = theme.AssetUri(img.File);
                if (!AssetLoader.Exists(uri))
                    return null;
                using var stream = AssetLoader.Open(uri);
                return Bitmap.DecodeToWidth(stream, decodeWidth);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Предпросмотр темы «{theme.Code}»: картинка не открылась ({ex.Message}).", "WARNING");
        }
        return null;
    }
}
