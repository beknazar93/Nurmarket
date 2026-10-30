using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-10-23: страница «подробно» — часть MarketplaceStoreView.
// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-3, 3.5–3.8, 3.10): шапка с кнопкой действия, лента слайдов, анимация,
// блоки «Чем поможет», «Как включить», «Для кого», правая колонка с ценой, липкая нижняя панель; две колонки при ширине ≥ 1180.
public sealed partial class MarketplaceStoreView
{
    private ScrollViewer? _detailScroll;

    // Раскладка страницы по ширине: Wide ≥ 1180 (две колонки), Medium — одна колонка, Compact ≤ 1100, Phone ≤ 640.
    private enum DetailMode { Wide, Medium, Compact, Phone }

    private const double DetailContentMaxWidth = 1144;
    private const double DetailLeftColumnMaxWidth = 820;
    private const double DetailRightColumnWidth = 300;

    private static DetailMode DetailModeFor(double width) =>
        width <= 0 || width >= 1180 ? DetailMode.Wide : width > 1100 ? DetailMode.Medium : width > 640 ? DetailMode.Compact : DetailMode.Phone;

    private void ShowDetail(string id, bool keepScroll)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            ShowStorefront();
            return;
        }

        var offset = keepScroll && _detailScroll != null ? _detailScroll.Offset : default;
        _openId = id;
        _page.Content = BuildDetail(item);
        if (keepScroll && _detailScroll is { } scroll)
        {
            var target = offset;
            Dispatcher.UIThread.Post(() => scroll.Offset = target, DispatcherPriority.Loaded);
        }
        else if (_detailScroll is { } fresh)
        {
            // Фокус внутрь страницы: Esc, стрелки и прокрутка клавишами работают сразу (плитку, по которой нажали, уже нет в дереве).
            Dispatcher.UIThread.Post(() => fresh.Focus(), DispatcherPriority.Loaded);
        }
    }

    private Control BuildDetail(MarketplaceItem item)
    {
        var mode = DetailModeFor(Bounds.Width);
        var wide = mode == DetailMode.Wide;
        var margin = mode is DetailMode.Wide or DetailMode.Medium ? 24.0 : 16.0;

        // ---- левая и правая колонки
        var left = new StackPanel { Spacing = 24 };
        var right = new StackPanel { Spacing = 16 };
        FillLeftColumn(left, item, mode);
        FillRightColumn(right, item, wide);

        // ---- верх страницы: шапка, пояснение, лента/обложка
        var content = new StackPanel { Spacing = 20, MaxWidth = DetailContentMaxWidth, HorizontalAlignment = HorizontalAlignment.Stretch };
        var header = BuildDetailHeader(item, mode, out var actionAnchor);
        content.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(item.Note))
            content.Children.Add(NoteBox(item.Note));
        var media = BuildMediaTop(item, mode, out var strip);
        if (media != null)
            content.Children.Add(media);

        if (wide)
        {
            left.MaxWidth = DetailLeftColumnMaxWidth;
            left.HorizontalAlignment = HorizontalAlignment.Left;
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,24,{DetailRightColumnWidth}") };
            columns.Children.Add(left);
            Grid.SetColumn(right, 2);
            right.VerticalAlignment = VerticalAlignment.Top;
            columns.Children.Add(right);
            content.Children.Add(columns);
        }
        else
        {
            content.Children.Add(left);
            content.Children.Add(right);
        }

        var body = new Border
        {
            Padding = new Thickness(margin, 4, margin, 88), // снизу запас под липкую панель
            Child = content,
        };

        _detailScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
            Content = body,
        };

        // ---- липкая нижняя панель: только когда кнопка шапки ушла за верх экрана
        var sticky = BuildStickyBar(item, mode);
        if (sticky != null)
        {
            sticky.IsVisible = false;
            sticky.VerticalAlignment = VerticalAlignment.Bottom;
            var scroll = _detailScroll;
            void Update()
            {
                var bottom = actionAnchor.TranslatePoint(new Point(0, actionAnchor.Bounds.Height), body)?.Y;
                sticky.IsVisible = bottom is { } y && scroll.Offset.Y > y;
            }
            scroll.ScrollChanged += (_, _) => Update();
            scroll.SizeChanged += (_, _) => Update();
        }

        var crumbs = BuildCrumbs(item, margin);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(crumbs);
        Grid.SetRow(_detailScroll, 1);
        root.Children.Add(_detailScroll);
        if (sticky != null)
        {
            Grid.SetRow(sticky, 1);
            root.Children.Add(sticky);
        }

        // Ширина окна сменила класс раскладки (две колонки ↔ одна) — страница перестраивается, прокрутка сохраняется.
        root.SizeChanged += (_, e) =>
        {
            if (DetailModeFor(e.NewSize.Width) != mode)
                Dispatcher.UIThread.Post(() =>
                {
                    if (_openId == item.Id && ReferenceEquals(_page.Content, root))
                        ShowDetail(item.Id, keepScroll: true);
                });
        };
        // ← → листают ленту слайдов, когда фокус на странице (в полях ввода и списках стрелки остаются им).
        root.KeyDown += (_, e) =>
        {
            if (e.Handled || strip == null || e.Source is TextBox or ComboBox or Slider)
                return;
            if (strip.HandleKey(e.Key))
                e.Handled = true;
        };
        return root;
    }

    // ------------------------------------------------------------------ крошки

    private Control BuildCrumbs(MarketplaceItem item, double margin)
    {
        var groupLabel = DetailGroupLabel(item.Group);
        var label = Tr.T("Маркетплейс", "Маркетплейс", "Marketplace", "Marketplace", "Marketplace") + (groupLabel.Length > 0 ? " / " + groupLabel : "");
        var back = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    MarketplaceDetailUi.PathIconOf("chevron-left", 14),
                    new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 6),
        };
        back.Classes.Add("btn-secondary");
        var backName = Tr.T("Назад", "Артка", "Back", "Geri", "Orqaga");
        Avalonia.Automation.AutomationProperties.SetName(back, backName);
        ToolTip.SetTip(back, backName);
        back.Click += (_, _) => GoBack();
        return new Border
        {
            Padding = new Thickness(margin, 6, margin, 6),
            Child = new Grid { MaxWidth = DetailContentMaxWidth, Children = { back } },
        };
    }

    private static string DetailGroupLabel(MarketplaceGroup group) => group switch
    {
        MarketplaceGroup.Sales => Tr.T("Продажи и клиенты", "Сатуу жана кардарлар", "Sales and customers", "Satış ve müşteriler", "Sotuv va mijozlar"),
        MarketplaceGroup.Warehouse => Tr.T("Склад и печать", "Кампа жана басып чыгаруу", "Warehouse and printing", "Depo ve yazdırma", "Ombor va chop etish"),
        MarketplaceGroup.Owner => Tr.T("Для владельца", "Ээси үчүн", "For the owner", "Sahibi için", "Egasi uchun"),
        MarketplaceGroup.Packs => Tr.T("Пакеты «Стандарта»", "«Стандарт» пакеттери", "“Standard” packs", "«Standart» paketleri", "«Standart» paketlari"),
        MarketplaceGroup.Layouts => Tr.T("Виды кассы", "Касса көрүнүштөрү", "Till layouts", "Kasa görünümleri", "Kassa ko'rinishlari"),
        MarketplaceGroup.Themes => Tr.T("Темы", "Темалар", "Themes", "Temalar", "Mavzular"),
        MarketplaceGroup.Soon => Tr.T("Скоро", "Жакында", "Coming soon", "Yakında", "Tez orada"),
        _ => "",
    };

    // ------------------------------------------------------------------ шапка

    private Control BuildDetailHeader(MarketplaceItem item, DetailMode mode, out Control actionAnchor)
    {
        var small = mode is DetailMode.Compact or DetailMode.Phone;
        var iconSize = mode == DetailMode.Phone ? 64 : small ? 72 : 96;

        var title = new TextBlock { Text = item.Title, FontSize = small ? 22 : 26, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Fg(title, "BrushText");

        var meta = new TextBlock
        {
            Text = string.Join(" · ", new[] { item.Vendor, item.KindLabel, string.IsNullOrEmpty(item.Version) ? "" : Tr.T("версия ", "версия ", "version ", "sürüm ", "versiya ") + item.Version }
                .Where(s => !string.IsNullOrEmpty(s))),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        };
        Fg(meta, "BrushTextSoft");

        var info = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { title, meta } };
        // До трёх коротких чипов («Офлайн», «Русский и кыргызский»…); пока каталог их не задал — прежние метки.
        var facts = (item.KeyFacts.Count > 0 ? item.KeyFacts : item.Tags).Take(3).ToList();
        if (facts.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            foreach (var fact in facts)
                chips.Children.Add(Chip(fact, "BrushTextMuted"));
            info.Children.Add(chips);
        }

        var actions = BuildActionBlock(item, mode, bar: false, out actionAnchor);

        var grid = new Grid();
        var icon = BuildIcon(item, iconSize);
        icon.VerticalAlignment = VerticalAlignment.Top;
        if (mode == DetailMode.Phone)
        {
            grid.ColumnDefinitions = new ColumnDefinitions("Auto,16,*");
            grid.RowDefinitions = new RowDefinitions("Auto,Auto");
            grid.Children.Add(icon);
            Grid.SetColumn(info, 2);
            grid.Children.Add(info);
            actions.HorizontalAlignment = HorizontalAlignment.Stretch;
            actions.Margin = new Thickness(0, 14, 0, 0);
            Grid.SetRow(actions, 1);
            Grid.SetColumnSpan(actions, 3);
            grid.Children.Add(actions);
        }
        else
        {
            grid.ColumnDefinitions = new ColumnDefinitions("Auto,20,*,20,Auto");
            grid.Children.Add(icon);
            Grid.SetColumn(info, 2);
            grid.Children.Add(info);
            Grid.SetColumn(actions, 4);
            actions.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(actions);
        }
        return grid;
    }

    private Border Chip(string text, string brushKey)
    {
        var label = new TextBlock { Text = text, FontSize = 12.5 };
        Fg(label, brushKey);
        var pill = new Border
        {
            Padding = new Thickness(10, 3),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Child = label,
        };
        pill.Bind(Border.BorderBrushProperty, pill.GetResourceObservable("BrushBorder"));
        return pill;
    }

    // ------------------------------------------------------------------ кнопка действия, цена, состояния

    /// <summary>Подпись кнопки без эмодзи/галочки в начале и без «— 2000 сом» в конце (цена показывается отдельно, не в кнопке).
    /// 2026-10-23: нужно, пока каталог (П-1) сам не убрал значок замка, галочку и цену из подписей.</summary>
    private static string CleanLabel(string label)
    {
        var start = 0;
        while (start < label.Length && !char.IsLetterOrDigit(label[start]))
            start++;
        var text = label[start..].Trim();
        var cut = text.LastIndexOf(" — ", StringComparison.Ordinal);
        if (cut > 0 && text[(cut + 3)..].Any(char.IsDigit))
            text = text[..cut];
        return text;
    }

    private static string InTariffWord() => Tr.T("Входит в тариф", "Тарифке кирет", "Included in the plan", "Tarifeye dahil", "Tarifga kiradi");

    /// <summary>Главная кнопка + цена/состояние + второстепенные кнопки. bar=true — вариант для липкой панели (в строку).</summary>
    private Panel BuildActionBlock(MarketplaceItem item, DetailMode mode, bool bar, out Control anchor)
    {
        var small = mode is DetailMode.Compact or DetailMode.Phone;
        var panel = bar
            ? (Panel)new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center }
            : new StackPanel { Spacing = 8, MinWidth = mode == DetailMode.Phone ? 0 : 220, HorizontalAlignment = HorizontalAlignment.Right };

        // --- что сказать о цене и состоянии (цена — только пока функция не куплена; у купленной цены нет)
        var statusText = item.StatusText;
        var tariff = InTariffWord();
        string headline = "", period = "", sub = "";
        if (item.IsComingSoon)
        {
        }
        else if (item.Status == MarketplaceStatus.Paid)
        {
            headline = item.Price is null || item.Price.Amount is null ? Tr.T("Платно", "Акылуу", "Paid", "Ücretli", "Pullik") : item.Price.Label;
            if (item.Price is { Amount: not null, MonthlyFee: null, Period: MarketplacePricePeriod.OneTime })
                period = Tr.T("разово", "бир жолу", "one-time", "tek sefer", "bir marta");
        }
        else if (item.Status == MarketplaceStatus.Installed || !string.IsNullOrEmpty(statusText))
        {
            // 2026-10-31 (ТЗ Умара): включено на сервере без ключа — «Подключено», а не «Куплено».
            sub = statusText == tariff ? tariff : item.ConnectedByServer ? statusText : item.Price != null ? Tr.T("Куплено", "Сатып алынган", "Purchased", "Satın alındı", "Sotib olingan") : "";
        }
        else if (item.Price is null)
        {
            headline = Tr.T("Бесплатно", "Акысыз", "Free", "Ücretsiz", "Bepul");
        }

        // --- главная кнопка
        Button? main = null;
        var height = small ? 44 : 48;
        if (item.IsComingSoon)
        {
            main = MainButton(Tr.T("Скоро", "Жакында", "Coming soon", "Yakında", "Tez orada"), null, "btn-secondary", false, height, null);
        }
        else if (item.Primary is { } primary)
        {
            var done = !primary.IsEnabled;
            main = MainButton(CleanLabel(primary.Label), done ? "check" : item.Status == MarketplaceStatus.Paid ? "lock-outline" : null,
                done ? "btn-secondary" : "btn-primary", primary.IsEnabled, height, primary.Run);
        }
        else if (item.Status == MarketplaceStatus.Installed || !string.IsNullOrEmpty(statusText))
        {
            var label = string.IsNullOrEmpty(statusText) || statusText == tariff
                ? Tr.T("Включено", "Күйгүзүлгөн", "Enabled", "Etkin", "Yoqilgan")
                : statusText;
            main = MainButton(label, "check", "btn-secondary", false, height, null);
        }

        anchor = main ?? (Control)panel;
        if (bar)
        {
            var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            AddPriceLines(text, headline, period, sub, item.Title);
            foreach (var action in item.Secondary)
                panel.Children.Add(ActionButton(action, "btn-secondary"));
            if (main != null)
                panel.Children.Add(main);
            return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { text, panel }, VerticalAlignment = VerticalAlignment.Center };
        }

        if (main != null)
            panel.Children.Add(main);
        var priceLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = mode == DetailMode.Phone ? HorizontalAlignment.Center : HorizontalAlignment.Right };
        AddPriceLines(priceLine, headline, period, sub, null);
        if (priceLine.Children.Count > 0)
            panel.Children.Add(priceLine);
        foreach (var action in item.Secondary)
            panel.Children.Add(ActionButton(action, "btn-secondary"));
        // 2026-10-31 (ТЗ Умара): под кнопками — «Оплата пока через поддержку: оставьте заявку…».
        if (!string.IsNullOrWhiteSpace(item.ActionHint))
        {
            var hint = new TextBlock { Text = item.ActionHint, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, TextAlignment = mode == DetailMode.Phone ? TextAlignment.Center : TextAlignment.Right };
            Fg(hint, "BrushTextSoft");
            panel.Children.Add(hint);
        }
        return panel;
    }

    private void AddPriceLines(Panel host, string headline, string period, string sub, string? fallbackTitle)
    {
        if (headline.Length > 0)
        {
            var price = new TextBlock { Text = headline, FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            Fg(price, "BrushText");
            host.Children.Add(price);
            if (period.Length > 0)
            {
                var dot = new TextBlock { Text = "· " + period, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                Fg(dot, "BrushTextSoft");
                host.Children.Add(dot);
            }
        }
        else if (sub.Length > 0)
        {
            var line = new TextBlock { Text = sub, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            Fg(line, "BrushTextSoft");
            host.Children.Add(line);
        }
        else if (fallbackTitle != null)
        {
            var line = new TextBlock { Text = fallbackTitle, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis };
            Fg(line, "BrushText");
            host.Children.Add(line);
        }
    }

    private static Button MainButton(string label, string? iconKey, string cls, bool enabled, double height, Action? run)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        if (iconKey != null)
            row.Children.Add(MarketplaceDetailUi.PathIconOf(iconKey, 16));
        row.Children.Add(new TextBlock { Text = label, FontSize = 14.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var button = new Button
        {
            Content = row,
            MinHeight = height,
            MinWidth = 220,
            Padding = new Thickness(22, 8),
            IsEnabled = enabled,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        button.Classes.Add(cls);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        if (run != null)
            button.Click += (_, _) => run();
        return button;
    }

    private static Button ActionButton(MarketplaceAction action, string cls)
    {
        var label = CleanLabel(action.Label);
        var button = new Button
        {
            Content = label,
            MinHeight = 40,
            Padding = new Thickness(18, 8),
            IsEnabled = action.IsEnabled,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add(cls);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action.Run();
        return button;
    }

    private Control? BuildStickyBar(MarketplaceItem item, DetailMode mode)
    {
        // Панель нужна, только когда есть что нажать: включённое главное действие или второстепенные кнопки.
        if (item.IsComingSoon || (item.Primary is not { IsEnabled: true } && item.Secondary.Count == 0))
            return null;
        var block = BuildActionBlock(item, mode, bar: true, out _);
        var bar = new Border
        {
            Padding = new Thickness(mode is DetailMode.Wide or DetailMode.Medium ? 24 : 16, 10),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Grid { MaxWidth = DetailContentMaxWidth, Children = { block } },
        };
        block.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable("BrushPanelElevated"));
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("BrushBorder"));
        return bar;
    }

    // ------------------------------------------------------------------ лента слайдов / обложка

    /// <summary>Лента слайдов под шапкой (3.7 п.3). Темы — без ленты (их предпросмотр в левой колонке); нет файлов — обложка (3.10).</summary>
    private Control? BuildMediaTop(MarketplaceItem item, DetailMode mode, out MarketplaceSlideStrip? strip)
    {
        strip = null;
        if (item.Kind == MarketplaceKind.Theme)
            return null;

        var height = mode switch { DetailMode.Phone => 180.0, DetailMode.Compact => 200.0, _ => 280.0 };
        var slides = new List<(string Uri, string Caption)>();
        foreach (var slide in item.Slides)
            if (MarketplaceMedia.ResolveSlide(item, slide) is { } uri)
                slides.Add((uri, slide.Caption));

        var useHeroAsCover = item.HeroVisual != null && (item.Kind == MarketplaceKind.Layout || item.Id == "customerdisplay");
        if (slides.Count == 0)
        {
            var cover = BuildCover(item, height, useHeroAsCover);
            // Источник по сети (на будущее): слайды объявлены, а в кэше их ещё нет.
            if (item.Slides.Count > 0 && MarketplaceMedia.Source is not LocalMarketplaceMediaSource)
            {
                var note = MarketplaceDetailUi.Text(Tr.T("Картинки загрузятся, когда появится интернет", "Сүрөттөр интернет пайда болгондо жүктөлөт",
                    "Pictures will load when the internet is back", "Resimler internet geldiğinde yüklenecek", "Rasmlar internet paydo bo'lganda yuklanadi"), 12, "BrushTextSoft");
                return new StackPanel { Spacing = 6, Children = { cover, note } };
            }
            return cover;
        }

        var host = new ContentControl();
        var created = new MarketplaceSlideStrip(item.Title, slides, height, ShowOverlay, HideOverlay);
        strip = created;
        host.Content = created;
        // Ни один слайд не открылся (файлы битые) — тихо возвращаем обложку, без «нет картинки».
        created.AllFailed += () => host.Content = BuildCover(item, height, useHeroAsCover);
        return host;
    }

    private static Color MixColor(Color a, Color b, double t) =>
        Color.FromRgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Обложка вместо ленты: градиент цвета элемента, значок, название и строка пользы; для «Скоро» — жёлтая плашка.
    /// Для видов кассы и экрана покупателя вместо значка — крупный предпросмотр (HeroVisual).</summary>
    private Control BuildCover(MarketplaceItem item, double height, bool useHero)
    {
        if (useHero && item.HeroVisual != null)
        {
            var hero = item.HeroVisual();
            hero.HorizontalAlignment = HorizontalAlignment.Center;
            hero.VerticalAlignment = VerticalAlignment.Center;
            var holder = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(16), Child = hero };
            holder.Bind(Border.BackgroundProperty, holder.GetResourceObservable("BrushPanelSoft"));
            return holder;
        }

        var color = Color.TryParse(item.IconColor, out var c) ? c : Color.Parse("#FFB208");
        var black = Color.FromRgb(0x10, 0x10, 0x10);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(MixColor(color, black, 0.6), 0), new GradientStop(MixColor(color, black, 0.38), 1) },
        };

        Control symbol;
        if (!string.IsNullOrEmpty(item.IconKey) && MarketplaceIcons.Get(item.IconKey) is { } geometry)
            symbol = new PathIcon { Width = 72, Height = 72, Data = geometry, Foreground = Brushes.White };
        else
            symbol = new TextBlock { Text = item.IconGlyph, FontSize = 52, HorizontalAlignment = HorizontalAlignment.Center };
        symbol.HorizontalAlignment = HorizontalAlignment.Center;
        symbol.IsHitTestVisible = false;

        var line = !string.IsNullOrWhiteSpace(item.Tagline) ? item.Tagline : item.ShortDescription;
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 640, Margin = new Thickness(24, 12) };
        stack.Children.Add(symbol);
        stack.Children.Add(new TextBlock { Text = item.Title, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(line) && height >= 190)
            stack.Children.Add(new TextBlock { Text = line, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });

        var grid = new Grid { Children = { stack } };
        if (item.IsComingSoon)
        {
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F9D919")),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(16),
                Child = new TextBlock { Text = Tr.T("Скоро", "Жакында", "Coming soon", "Yakında", "Tez orada"), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#191919")) },
            });
        }
        return new Border { Height = height, CornerRadius = new CornerRadius(20), ClipToBounds = true, Background = gradient, Child = grid };
    }

    // ------------------------------------------------------------------ левая колонка

    private void FillLeftColumn(StackPanel left, MarketplaceItem item, DetailMode mode)
    {
        // Тема: вместо ленты — большой предпросмотр (его рисует MarketplaceLookPreview, пакет П-4).
        if (item.Kind == MarketplaceKind.Theme && item.HeroVisual != null)
        {
            var hero = item.HeroVisual();
            hero.HorizontalAlignment = HorizontalAlignment.Left;
            left.Children.Add(hero);
        }

        // Анимация «Как это работает» (если файл есть; нет файла — блока нет).
        var demoUri = item.Demo != null ? MarketplaceMedia.ResolveDemo(item) : null;
        if (demoUri != null)
        {
            var player = new MarketplaceDemoPlayer(demoUri);
            var stack = new StackPanel { Spacing = 8, Children = { player } };
            if (!string.IsNullOrWhiteSpace(item.Demo!.Caption))
                stack.Children.Add(BodyText(item.Demo.Caption));
            var block = DetailSection(Tr.T("Как это работает", "Бул кандай иштейт", "How it works", "Nasıl çalışır", "Qanday ishlaydi"), stack);
            player.Failed += () => block.IsVisible = false;
            left.Children.Add(block);
        }

        if (!string.IsNullOrWhiteSpace(item.LongDescription))
            left.Children.Add(DetailSection(Tr.T("Описание", "Сүрөттөмө", "Description", "Açıklama", "Tavsif"), BodyText(item.LongDescription)));

        // Экран покупателя со слайдами: живой предпросмотр отдельным блоком под лентой.
        if (item.Id == "customerdisplay" && item.HeroVisual != null && item.Slides.Any(s => MarketplaceMedia.ResolveSlide(item, s) != null))
        {
            var hero = item.HeroVisual();
            hero.HorizontalAlignment = HorizontalAlignment.Left;
            left.Children.Add(DetailSection(Tr.T("Как будет у вас", "Сизде кандай болот", "How it will look for you", "Sizde nasıl görünecek", "Sizda qanday bo'ladi"), hero));
        }

        if (item.Benefits.Count > 0)
        {
            var columns = mode == DetailMode.Phone ? 1 : mode == DetailMode.Compact ? 2 : 3;
            left.Children.Add(DetailSection(Tr.T("Чем поможет вам", "Сизге кандай жардам берет", "How it helps you", "Size nasıl yardımcı olur", "Sizga qanday yordam beradi"), BuildBenefits(item.Benefits, columns)));
        }

        if (item.HowTo.Count > 0)
            left.Children.Add(DetailSection(Tr.T("Как включить", "Кантип күйгүзүү", "How to turn it on", "Nasıl açılır", "Qanday yoqiladi"), BuildHowTo(item.HowTo)));

        if (item.Audience.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var who in item.Audience)
                chips.Children.Add(Chip(who, "BrushTextMuted"));
            left.Children.Add(DetailSection(Tr.T("Для кого", "Кимге ылайык", "Who it's for", "Kimler için", "Kim uchun"), chips));
        }

        if (item.InlineControlId != null && InlineControlProvider?.Invoke(item.InlineControlId) is { } inline)
            left.Children.Add(DetailSection(Tr.T("Управление", "Башкаруу", "Controls", "Yönetim", "Boshqaruv"), inline));

        if (item.Features.Count > 0)
        {
            var title = item.IsComingSoon
                ? Tr.T("Что будет", "Эмне болот", "What's planned", "Neler olacak", "Nimalar bo'ladi")
                : item.Kind is MarketplaceKind.Feature or MarketplaceKind.Service
                    ? Tr.T("Что внутри", "Ичинде эмне бар", "What's inside", "İçinde neler var", "Ichida nima bor")
                    : Tr.T("Возможности", "Мүмкүнчүлүктөр", "Highlights", "Özellikler", "Imkoniyatlar");
            left.Children.Add(DetailSection(title, CheckList(item.Features)));
        }

        if (!string.IsNullOrWhiteSpace(item.WhatsNew))
            left.Children.Add(DetailSection(Tr.T("Что нового", "Эмне жаңы", "What's new", "Yenilikler", "Yangiliklar"), CheckList([item.WhatsNew])));
    }

    private Control BuildBenefits(IReadOnlyList<MarketplaceBenefit> benefits, int columns)
    {
        var grid = new UniformGrid { Columns = Math.Max(1, columns) };
        foreach (var benefit in benefits)
        {
            var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Left };
            circle.Bind(Border.BackgroundProperty, circle.GetResourceObservable("BrushAccentSoft"));
            var glyph = MarketplaceDetailUi.PathIconOf(benefit.IconKey, 22, "BrushText");
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            circle.Child = glyph;

            var title = new TextBlock { Text = benefit.Title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            Fg(title, "BrushText");
            var text = new TextBlock { Text = benefit.Text, FontSize = 13.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap };
            Fg(text, "BrushTextMuted");

            var card = new Border
            {
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(14),
                Child = new StackPanel { Spacing = 8, Children = { circle, title, text } },
            };
            card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanelSoft"));
            grid.Children.Add(card);
        }
        // Поля карточек справа/снизу компенсирует отрицательный отступ обёртки.
        return new Border { Margin = new Thickness(0, 0, -12, -12), Child = grid };
    }

    private Control BuildHowTo(IReadOnlyList<string> steps)
    {
        var stack = new StackPanel { Spacing = 12 };
        for (var i = 0; i < steps.Count; i++)
        {
            var digit = new TextBlock { Text = (i + 1).ToString(), FontSize = 14, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var circle = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top, Child = digit };
            // Светлая тема: фон #191919, цифра #FFB208 (8,8:1); тёмная: фон BrushText, цифра BrushPanel.
            void Paint()
            {
                if (circle.ActualThemeVariant == ThemeVariant.Dark)
                {
                    circle.Bind(Border.BackgroundProperty, circle.GetResourceObservable("BrushText"));
                    digit.Bind(TextBlock.ForegroundProperty, digit.GetResourceObservable("BrushPanel"));
                }
                else
                {
                    circle.Background = new SolidColorBrush(Color.Parse("#191919"));
                    digit.Foreground = new SolidColorBrush(Color.Parse("#FFB208"));
                }
            }
            circle.AttachedToVisualTree += (_, _) => Paint();
            circle.ActualThemeVariantChanged += (_, _) => Paint();

            var text = new TextBlock { Text = steps[i], FontSize = 14, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            Fg(text, "BrushText");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
            row.Children.Add(circle);
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        return stack;
    }

    // ------------------------------------------------------------------ правая колонка

    private void FillRightColumn(StackPanel right, MarketplaceItem item, bool wide)
    {
        // Цена и условия
        var price = new StackPanel { Spacing = 6 };
        if (item.Status == MarketplaceStatus.Paid)
        {
            var label = item.Price is null || item.Price.Amount is null ? Tr.T("Платно", "Акылуу", "Paid", "Ücretli", "Pullik") : item.Price.Label;
            price.Children.Add(MarketplaceDetailUi.Text(label, 18, "BrushText", FontWeight.Bold));
        }
        else if (!item.IsComingSoon && item.Price is null && item.Status != MarketplaceStatus.Installed && string.IsNullOrEmpty(item.StatusText))
        {
            price.Children.Add(MarketplaceDetailUi.Text(Tr.T("Бесплатно", "Акысыз", "Free", "Ücretsiz", "Bepul"), 18, "BrushText", FontWeight.Bold));
        }
        else if (item.Status == MarketplaceStatus.Installed || !string.IsNullOrEmpty(item.StatusText))
        {
            var state = item.StatusText == InTariffWord() ? InTariffWord()
                : item.Price != null ? Tr.T("Куплено", "Сатып алынган", "Purchased", "Satın alındı", "Sotib olingan") : "";
            if (state.Length > 0)
                price.Children.Add(MarketplaceDetailUi.Text(state, 14, "BrushSuccess", FontWeight.SemiBold));
        }
        if (!string.IsNullOrWhiteSpace(item.PriceTerms))
            price.Children.Add(MarketplaceDetailUi.Text(item.PriceTerms, 13.5, "BrushTextMuted"));
        if (price.Children.Count > 0)
            right.Children.Add(DetailCard(Tr.T("Цена и условия", "Баасы жана шарттары", "Price and terms", "Fiyat ve koşullar", "Narx va shartlar"), price));

        if (item.Requirements.Count > 0)
            right.Children.Add(DetailCard(Tr.T("Требования", "Талаптар", "Requirements", "Gereksinimler", "Talablar"), IconList(item.Requirements)));

        var info = new StackPanel { Spacing = 6 };
        void InfoRow(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("136,*") };
            row.Children.Add(MarketplaceDetailUi.Text(name, 12.5, "BrushTextSoft"));
            var v = MarketplaceDetailUi.Text(value, 13.5, "BrushText");
            Grid.SetColumn(v, 1);
            row.Children.Add(v);
            info.Children.Add(row);
        }
        InfoRow(Tr.T("Разработчик", "Иштеп чыгуучу", "Developer", "Geliştirici", "Ishlab chiquvchi"), item.Vendor);
        InfoRow(Tr.T("Версия", "Версия", "Version", "Sürüm", "Versiya"), item.Version);
        InfoRow(Tr.T("Вид", "Түрү", "Type", "Tür", "Turi"), item.KindLabel);
        if (info.Children.Count > 0)
            right.Children.Add(DetailCard(Tr.T("Информация", "Маалымат", "Information", "Bilgi", "Ma'lumot"), info));

        var similar = SimilarItems(item);
        if (similar.Count > 0)
        {
            var title = Tr.T("Похожие", "Окшоштор", "Similar", "Benzerler", "O'xshashlar");
            if (wide)
                right.Children.Add(DetailCard(title, BuildSimilarRows(similar)));
            else
                right.Children.Add(DetailSection(title, BuildSimilarStrip(similar)));
        }
    }

    private Control DetailCard(string title, Control content)
    {
        var heading = MarketplaceDetailUi.Text(title, 15, "BrushText", FontWeight.Bold);
        var card = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 10, Children = { heading, content } },
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanel"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("BrushBorder"));
        return card;
    }

    // ------------------------------------------------------------------ общие блоки

    /// <summary>Раздел страницы: заголовок H2 (18 Bold) и содержимое.</summary>
    private StackPanel DetailSection(string title, Control content)
    {
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold };
        Fg(heading, "BrushText");
        return new StackPanel { Spacing = 10, Children = { heading, content } };
    }

    private TextBlock BodyText(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 14, LineHeight = 21, TextWrapping = TextWrapping.Wrap };
        Fg(block, "BrushTextMuted");
        return block;
    }

    /// <summary>Пункты с галочкой (check 16, BrushSuccess) вместо «·».</summary>
    private Control CheckList(IEnumerable<string> lines) => IconRows(lines, "check", "BrushSuccess");

    /// <summary>Пункты с информационным значком (требования).</summary>
    private Control IconList(IEnumerable<string> lines) => IconRows(lines, "information-outline", "BrushTextSoft");

    private Control IconRows(IEnumerable<string> lines, string iconKey, string iconBrush)
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var line in lines)
        {
            var icon = MarketplaceDetailUi.PathIconOf(iconKey, 16, iconBrush);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 3, 10, 0);
            var text = BodyText(line);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            row.Children.Add(icon);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        return stack;
    }

    private Control NoteBox(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap };
        Fg(block, "BrushTextMuted");
        var icon = MarketplaceDetailUi.PathIconOf("information-outline", 18, "BrushTextSoft");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 10, 0);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(icon);
        Grid.SetColumn(block, 1);
        row.Children.Add(block);
        var box = new Border { Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(12), Child = row };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("BrushPanelSoft"));
        return box;
    }

    // ------------------------------------------------------------------ «Похожие»

    private List<MarketplaceItem> SimilarItems(MarketplaceItem item)
    {
        var result = item.RelatedIds.Select(id => _items.FirstOrDefault(i => i.Id == id)).Where(i => i != null && i.Id != item.Id).Select(i => i!).ToList();
        if (result.Count == 0)
            result = _items.Where(i => i.Group == item.Group && i.Id != item.Id).Take(4).ToList();
        if (result.Count == 0)
            result = _items.Where(i => i.Kind == item.Kind && i.Id != item.Id).Take(4).ToList();
        return result;
    }

    /// <summary>Строки по 56 px: значок 40, название, цена или состояние (правая колонка на широком экране).</summary>
    private Control BuildSimilarRows(IEnumerable<MarketplaceItem> items)
    {
        var stack = new StackPanel { Spacing = 4 };
        foreach (var similar in items)
        {
            var icon = BuildIcon(similar, 40);
            icon.VerticalAlignment = VerticalAlignment.Center;
            var name = new TextBlock { Text = similar.Title, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
            Fg(name, "BrushText");
            var status = new TextBlock { Text = similar.TileStatusLine, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
            Fg(status, StatusBrushKey(similar));
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Children = { name, status } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
            row.Children.Add(icon);
            Grid.SetColumn(texts, 2);
            row.Children.Add(texts);
            var border = new Border { Height = 56, Padding = new Thickness(6, 0), CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Child = row };
            var id = similar.Id;
            MarketplaceDetailUi.MakeClickable(border, similar.Title, () => OpenFromTile(id), "BrushPanelSoft");
            stack.Children.Add(border);
        }
        return stack;
    }

    private Control BuildSimilarStrip(IEnumerable<MarketplaceItem> items)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var item in items)
            strip.Children.Add(BuildTile(item));
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = strip };
        EnableDragScroll(scroll); // 2026-10-26: ленту можно тянуть рукой
        // Колесо мыши листает ленту по горизонтали (полосы прокрутки нет).
        scroll.PointerWheelChanged += (_, e) =>
        {
            var max = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            var next = Math.Clamp(scroll.Offset.X - delta * 80, 0, max);
            if (max > 0 && next != scroll.Offset.X)
            {
                scroll.Offset = new Vector(next, 0);
                e.Handled = true;
            }
        };
        return scroll;
    }
}
