using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Controls;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-2), 3.2–3.4: тёмный баннер «Рекомендуем» наверху витрины.
/// До 5 элементов (IsFeatured), ‹ › и точки, автосмена раз в 8 с (пауза при наведении), таймер стоит, пока баннер не на экране
/// (DetachedFromVisualTree). Цвета баннера фиксированные (тёмный всегда, как в брендбуке) — в обеих темах программы одинаков.
/// Справа — первый слайд элемента (если файл есть), иначе обложка кодом: градиент цвета элемента и векторный значок.</summary>
internal sealed class MarketplaceHero : Border
{
    private const string Ink = "#191919";
    private static readonly IBrush White = Brushes.White;
    private static readonly IBrush Light = new SolidColorBrush(Color.Parse("#DDDDDD"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#CCCCCC"));

    private readonly IReadOnlyList<MarketplaceItem> _items;
    private readonly Action<string> _open;
    private readonly bool _compact;
    private readonly ContentControl _slot = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<int, Bitmap?> _bitmaps = new();
    private int _index;
    private bool _hover;

    public MarketplaceHero(IReadOnlyList<MarketplaceItem> items, bool compact, Action<string> open)
    {
        _items = items;
        _compact = compact;
        _open = open;
        Height = compact ? 156 : 220;
        CornerRadius = new CornerRadius(20);
        ClipToBounds = true;
        Child = new Grid
        {
            Children =
            {
                new BrandPattern { Foreground = White, Opacity = 0.06, CellSize = 64 },
                _slot,
            },
        };
        _timer.Tick += (_, _) =>
        {
            if (!_hover && _items.Count > 1)
                Show((_index + 1) % _items.Count);
        };
        PointerEntered += (_, _) => _hover = true;
        PointerExited += (_, _) => _hover = false;
        Show(0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_slot.Content == null)
            Show(_index);
        if (_items.Count > 1)
            _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Таймер стоит и картинки освобождаются, пока баннера не видно (на странице «подробно» он не нужен).
        _timer.Stop();
        _slot.Content = null;
        foreach (var bmp in _bitmaps.Values)
            bmp?.Dispose();
        _bitmaps.Clear();
    }

    private void Show(int index)
    {
        _index = (index % _items.Count + _items.Count) % _items.Count;
        var item = _items[_index];
        var color = ParseColor(item.IconColor);
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(Ink), 0),
                new GradientStop(Color.Parse(AccentThemeService.MixHex(Ink, ToHex(color), 0.35)), 1),
            },
        };
        _slot.Content = BuildSlide(item);
    }

    private void Restart()
    {
        if (_items.Count > 1)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    private Control BuildSlide(MarketplaceItem item)
    {
        var iconSize = _compact ? 56.0 : 72.0;
        var icon = MarketplaceStoreView.BuildIcon(item, iconSize);
        icon.VerticalAlignment = VerticalAlignment.Top;

        var label = new TextBlock
        {
            Text = item.KindLabel.ToUpperInvariant() + " · " + Tr.T("РЕКОМЕНДУЕМ", "СУНУШТАЙБЫЗ", "RECOMMENDED", "ÖNERİLEN", "TAVSIYA ETAMIZ"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = LabelBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = _compact ? 22 : 28,
            FontWeight = FontWeight.Bold,
            Foreground = White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        var tagline = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Tagline) ? item.ShortDescription : item.Tagline,
            FontSize = _compact ? 14 : 15,
            Foreground = Light,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = _compact ? 1 : 2,
        };
        var texts = new StackPanel { Spacing = 2, MaxWidth = _compact ? 560 : 580, Children = { label, title, tagline } };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { icon, texts } };

        var more = new Button { Content = Tr.T("Подробнее", "Кененирээк", "Details", "Ayrıntılar", "Batafsil") };
        more.Classes.Add("mp-hero-btn");
        more.Height = _compact ? 36 : 40;
        Avalonia.Automation.AutomationProperties.SetName(more, item.Title);
        more.Click += (_, _) => _open(item.Id);

        var price = BuildPrice(item);
        var nav = BuildNav();

        Control actions;
        if (_compact)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
            more.Margin = new Thickness(0, 0, 16, 0);
            Grid.SetColumn(price, 1);
            Grid.SetColumn(nav, 3);
            row.Children.Add(more);
            row.Children.Add(price);
            row.Children.Add(nav);
            actions = row;
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { more, price } };
            actions = new StackPanel { Spacing = 10, Children = { row, nav } };
            nav.HorizontalAlignment = HorizontalAlignment.Left;
        }

        var left = new StackPanel
        {
            Spacing = _compact ? 8 : 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { head, actions },
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(_compact ? "*" : "*,Auto"),
            Margin = _compact ? new Thickness(20, 12) : new Thickness(28, 14, 28, 14),
            Background = Brushes.Transparent,
        };
        grid.Children.Add(left);
        if (!_compact)
        {
            var pic = BuildPicture(item);
            pic.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(pic, 1);
            grid.Children.Add(pic);
        }

        grid.Cursor = new Cursor(StandardCursorType.Hand);
        grid.Tapped += (_, e) =>
        {
            // Нажатие на ‹ › и точки — не открытие; на любое другое место баннера — открытие страницы «подробно».
            for (var v = e.Source as Visual; v != null && !ReferenceEquals(v, grid); v = v.GetVisualParent())
                if (v is Button)
                    return;
            _open(item.Id);
        };
        return grid;
    }

    private Control BuildPrice(MarketplaceItem item)
    {
        var line = item.TileStatusLine;
        var check = line.StartsWith('✓');
        var text = new TextBlock
        {
            Text = line.TrimStart('✓', ' '),
            FontSize = _compact ? 15 : 16,
            FontWeight = FontWeight.Bold,
            Foreground = White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (check)
            panel.Children.Add(new PathIcon { Width = 16, Height = 16, Data = MarketplaceIcons.Get("check"), Foreground = White, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(text);
        return panel;
    }

    private Control BuildNav()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (_items.Count < 2)
            return panel;

        Button Arrow(string key, int delta, string name)
        {
            var b = new Button { Content = MarketplaceIcons.Icon(key, 18) };
            b.Classes.Add("mp-hero-nav");
            Avalonia.Automation.AutomationProperties.SetName(b, name);
            b.Click += (_, _) =>
            {
                Show(_index + delta);
                Restart();
            };
            return b;
        }

        panel.Children.Add(Arrow("chevron-left", -1, Tr.T("Назад", "Артка", "Previous", "Önceki", "Orqaga")));
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
        for (var i = 0; i < _items.Count; i++)
        {
            var target = i;
            var dot = new Button();
            dot.Classes.Add("mp-hero-dot");
            dot.Classes.Set("active", i == _index);
            Avalonia.Automation.AutomationProperties.SetName(dot, _items[i].Title);
            dot.Click += (_, _) =>
            {
                Show(target);
                Restart();
            };
            dots.Children.Add(dot);
        }
        panel.Children.Add(dots);
        panel.Children.Add(Arrow("chevron-right", 1, Tr.T("Вперёд", "Алдыга", "Next", "Sonraki", "Oldinga")));
        return panel;
    }

    private Control BuildPicture(MarketplaceItem item)
    {
        var frame = new Border
        {
            Width = 340,
            Height = 191,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 8 24 0 #66000000"),
            Background = Brushes.Transparent,
        };
        var bmp = GetBitmap(_index, item);
        if (bmp != null)
        {
            frame.Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
        }
        else
        {
            // Нет слайда — обложка кодом: градиент цвета элемента и белый (или тёмный по контрасту) значок.
            var color = ParseColor(item.IconColor);
            var hex = ToHex(color);
            var cover = new Grid
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(color, 0),
                        new GradientStop(Color.Parse(AccentThemeService.MixHex(hex, "#000000", 0.35)), 1),
                    },
                },
            };
            var geo = MarketplaceIcons.Get(item.IconKey);
            if (geo != null)
                cover.Children.Add(new PathIcon { Width = 88, Height = 88, Data = geo, Foreground = new SolidColorBrush(Color.Parse(MarketplaceStoreView.GlyphColorOn(hex))), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            else
                cover.Children.Add(new TextBlock { Text = item.IconGlyph, FontSize = 64, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            frame.Child = cover;
        }
        return frame;
    }

    private Bitmap? GetBitmap(int index, MarketplaceItem item)
    {
        if (_bitmaps.TryGetValue(index, out var cached))
            return cached;
        Bitmap? bmp = null;
        if (item.Slides.Count > 0 && MarketplaceMedia.ResolveSlide(item, item.Slides[0]) is { } uri)
            bmp = MarketplaceMedia.LoadBitmap(uri);
        _bitmaps[index] = bmp;
        return bmp;
    }

    private static Color ParseColor(string hex) => Color.TryParse(hex, out var c) ? c : Color.Parse("#FFB208");

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
