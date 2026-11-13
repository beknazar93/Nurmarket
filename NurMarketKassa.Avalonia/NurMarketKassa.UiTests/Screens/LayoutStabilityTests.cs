using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (владелец: «чтобы не было как с пульсацией», видео «у.mp4» — «Склад → Штрихкоды» сужался/раздувался при прокрутке колесом): общая защита
/// от пульсации и дрожания раскладки на основных экранах кассы и программы владельца (<see cref="LayoutStabilityProbe"/>). Сначала проба проверяется на
/// специально «плохом» элементе (колонки от ширины ScrollViewer с потерей пикселей, родитель по содержимому, порог без гистерезиса) — она обязана его поймать —
/// и на «хорошем» (растянут, колонки от своей ширины без потерь, гистерезис) — не должна ругаться. Потом прогон по экранам. Снимки провалов:
/// %TEMP%\nurmarket-tests\ui-actual\layout-stability-*.png; сводка времени — %TEMP%\nurmarket-tests\layout-stability.txt.</summary>
[Collection("UiScenarios")]
public sealed class LayoutStabilityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
    private List<CatalogProductTileVm>? _savedProducts;

    public LayoutStabilityTests(ITestOutputHelper output)
    {
        _out = output;
        TestHost.Ensure();
        UiFake.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;
        if (_savedProducts != null)
        {
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(_savedProducts);
        }
    }

    /// <summary>Каталог на 60 товаров: в «Штрихкодах» больше страницы плиток — есть что прокручивать (как 48 карточек на странице в видео).</summary>
    private void SeedCatalog(int count = 60)
    {
        _savedProducts ??= CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        for (var i = 0; i < count; i++)
            CatalogCacheService.Products.Add(new CatalogProductTileVm("ls" + i, $"Товар {i} для проверки раскладки", (10 + i * 7) + ".00 сом", false)
            {
                Barcode = (4600000001000L + i).ToString(),
                Article = "L" + i,
                Unit = "шт",
                Quantity = i % 30,
                PurchasePrice = 5 + i,
                Category = "Категория " + i % 6,
            });
    }

    private void Finish(LayoutStabilityProbe probe, Stopwatch sw)
    {
        // 2026-11-12 (аудит Fable, В-14): в строке — сколько областей реально прокручено (0 при «прокручивать нечего» — сценарий прокрутки на этом экране ничего не проверил)
        var line = $"{DateTime.Now:s} {probe.Screen}: сценариев {probe.Scenarios}, прокручено областей {probe.ScrolledAreas} (нечего прокручивать: {probe.NothingToScroll}), {sw.Elapsed.TotalSeconds:0.0} с, провалов {probe.Failures.Count}";
        _out.WriteLine(line);
        foreach (var n in probe.Notes)
            _out.WriteLine("  " + n);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "layout-stability.txt"), line + Environment.NewLine
                + string.Concat(probe.Failures.Select(f => f + Environment.NewLine))
                + string.Concat(probe.Notes.Where(n => n.StartsWith("НЕ ПУЛЬСАЦИЯ", StringComparison.Ordinal)).Select(n => "  заметка: " + n + Environment.NewLine)));
        }
        catch (IOException)
        {
        }
        probe.Failures.Should().BeEmpty(probe.Report());
    }

    // ================================================================== проба на эталонах

    /// <summary>«Плохой» эталон — тот же класс ошибок, что в видео: (1) колонки и ширина плиток считаются от ширины ScrollViewer с потерей пикселя на пересчёт,
    /// пересчёт при прокрутке, а корень выровнен по центру (сжимается по содержимому) — при каждом шаге колеса ширина уменьшается; (2) порог «компактного» режима
    /// без гистерезиса, и компактный режим сам расширяет панель (MinWidth) — около порога режим мигает на каждом прогоне.</summary>
    private sealed class PulsingCardsView : Grid
    {
        public readonly ScrollViewer Scroll;
        private readonly WrapPanel _panel = new();
        private readonly Button _select = new() { Content = "Выбрать все на странице" };
        private bool _compact;

        public PulsingCardsView()
        {
            HorizontalAlignment = HorizontalAlignment.Center; // ошибка: по содержимому
            RowDefinitions = new RowDefinitions("Auto,*");
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Children = { _select, new Button { Content = "Печать" } } };
            Children.Add(bar);
            for (var i = 0; i < 80; i++)
                _panel.Children.Add(new Border { Width = 290, Height = 120, Margin = new Thickness(5), Background = Brushes.SteelBlue });
            Scroll = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            SetRow(Scroll, 1);
            Children.Add(Scroll);
            Scroll.ScrollChanged += (_, e) =>
            {
                if (Math.Abs(e.OffsetDelta.Y) > 0.01)
                    LayoutCards(Scroll.Bounds.Width);
            };
            // переключение — на следующем кадре (Post здесь дал бы бесконечную петлю очереди — её ловит сторож LayoutStabilityProbe.Pump)
            SizeChanged += (_, _) => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
            {
                var c = Bounds.Width < 1250; // ошибка: без гистерезиса
                if (c == _compact)
                    return;
                _compact = c;
                _select.Content = c ? "На странице" : "Выбрать все на странице";
                _select.MinWidth = c ? 1300 : 0; // компактный режим расширяет панель — обратная связь
            });
        }

        private void LayoutCards(double w)
        {
            var cols = Math.Max(1, (int)((w + 10) / 300));
            var cardW = Math.Floor((w - cols * 10) / cols) - 1; // ошибка: потеря пикселя на каждый пересчёт
            foreach (var card in _panel.Children.OfType<Border>())
                card.Width = cardW;
        }
    }

    /// <summary>«Хороший» эталон: растянут на всю ширину, колонки — от своей ширины и только при её изменении, без потерь; порог с гистерезисом и без обратной связи.</summary>
    private sealed class StableCardsView : Grid
    {
        private readonly WrapPanel _panel = new();
        private readonly Button _select = new() { Content = "Выбрать все на странице" };
        private bool _compact;
        private double _lastWidth;

        public StableCardsView()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            RowDefinitions = new RowDefinitions("Auto,*");
            Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _select, new Button { Content = "Печать" } } });
            for (var i = 0; i < 80; i++)
                _panel.Children.Add(new Border { Width = 290, Height = 120, Margin = new Thickness(5), Background = Brushes.SeaGreen });
            var scroll = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            SetRow(scroll, 1);
            Children.Add(scroll);
            SizeChanged += (_, e) =>
            {
                var w = e.NewSize.Width;
                if (Math.Abs(w - _lastWidth) < 0.5)
                    return;
                _lastWidth = w;
                var cols = Math.Max(1, (int)(w / 300));
                var cardW = Math.Floor(w / cols) - 10;
                foreach (var card in _panel.Children.OfType<Border>())
                    card.Width = cardW;
                var c = _compact ? w < 1330 : w < 1250;
                if (c != _compact)
                {
                    _compact = c;
                    _select.Content = c ? "На странице" : "Выбрать все на странице";
                }
            };
        }
    }

    [AvaloniaFact]
    public void Probe_Catches_ColumnsFromScrollWidth_WithoutHysteresis()
    {
        LayoutStabilityProbe.Trace("=== Probe_Catches_ColumnsFromScrollWidth_WithoutHysteresis");
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // прокрутка: окно 1700 — режим устойчив, ширину «съедает» пересчёт колонок при каждом шаге колеса
        var view = new PulsingCardsView();
        var window = new Window { Width = 1700, Height = 800, Content = view };
        try
        {
            window.Show();
            UiShot.Settle(3, 20);
            var probe = new LayoutStabilityProbe("эталон-плохой", window);
            probe.Scroll();
            _out.WriteLine(probe.Report());
            probe.Failures.Should().NotBeEmpty("пробу обязана поймать пульсацию ширины при прокрутке");
            probe.Failures[0].Should().Contain("прокрутк").And.Contain("график по шагам");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }

        // ресайз: около порога режим мигает на каждом прогоне — «окно не успокаивается» и дребезг режима
        var view2 = new PulsingCardsView();
        var window2 = new Window { Width = 1000, Height = 800, Content = view2 };
        try
        {
            window2.Show();
            UiShot.Settle(3, 20);
            var probe2 = new LayoutStabilityProbe("эталон-плохой", window2);
            probe2.Resize("ресайз", w => window2.Width = w, () => window2.ClientSize.Width);
            _out.WriteLine(probe2.Report());
            probe2.Failures.Should().NotBeEmpty("пробу обязана поймать дребезг режима около порога без гистерезиса");
            probe2.Failures.Should().Contain(f => f.Contains("не успокаивается") || f.Contains("дребезг"));
        }
        finally
        {
            window2.Close();
            UiShot.Pump();
        }
    }

    /// <summary>Петля «SizeChanged → Post → смена ширины → SizeChanged…» не даёт очереди опустеть: без сторожа тест висел бы до таймаута test-safe (15 мин).
    /// Сторож пробы обрывает прогон понятным исключением. Сама петля в эталоне ограничена 25 с (если сторож не сработает — тест упадёт, а не повиснет).</summary>
    [AvaloniaFact]
    public void Probe_Watchdog_StopsEndlessPostLoop()
    {
        LayoutStabilityProbe.Trace("=== Probe_Watchdog_StopsEndlessPostLoop");
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var box = new Border { Width = 300, Height = 100, Background = Brushes.Goldenrod, HorizontalAlignment = HorizontalAlignment.Left };
        var window = new Window { Width = 800, Height = 400, Content = box };
        var stopAt = DateTime.MinValue; // петля включается только на время проверки
        box.SizeChanged += (_, _) =>
        {
            if (DateTime.UtcNow < stopAt)
                Dispatcher.UIThread.Post(() => box.Width = box.Width > 300 ? 300 : 301);
        };
        // 2026-11-12 (аудит Т-21): сторож для этого теста — 500 мс (раньше 10 с настоящего времени); исходное значение возвращается
        var savedWatchdog = LayoutStabilityProbe.PumpWatchdogMs;
        try
        {
            window.Show();
            UiShot.Pump();
            LayoutStabilityProbe.PumpWatchdogMs = 500;
            stopAt = DateTime.UtcNow.AddSeconds(25);
            box.Width = 301;
            var sw = Stopwatch.StartNew();
            LayoutStabilityProbe.Trace("=== новый тест");
            var act = () => LayoutStabilityProbe.Step();
            act.Should().Throw<LayoutStabilityProbe.LayoutLoopException>("сторож обрывает бесконечную петлю очереди");
            _out.WriteLine($"сторож сработал через {sw.Elapsed.TotalSeconds:0.0} с");
            sw.Elapsed.TotalSeconds.Should().BeInRange(0.4, 5, "сторож срабатывает по своему сроку (500 мс), а не по таймауту прогона");
        }
        finally
        {
            LayoutStabilityProbe.PumpWatchdogMs = savedWatchdog;
            stopAt = DateTime.MinValue;
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Probe_Passes_StableControl_InEveryScenario()
    {
        LayoutStabilityProbe.Trace("=== Probe_Passes_StableControl_InEveryScenario");
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var window = new Window { Width = 1400, Height = 800, Content = new StableCardsView() };
        try
        {
            window.Show();
            UiShot.Settle(3, 20);
            var probe = new LayoutStabilityProbe("эталон-хороший", window);
            probe.Settled();
            probe.Scroll(mustMove: true); // 2026-11-12 (В-14): эталон обязан реально прокрутиться — «прокручивать нечего» здесь был бы пропуск сценария
            probe.ScrolledAreas.Should().BeGreaterThan(0, "область с 80 плитками сдвинулась");
            probe.Resize("ресайз", w => window.Width = w, () => window.ClientSize.Width);
            window.Width = 1400;
            LayoutStabilityProbe.Step(3);
            probe.Modal();
            probe.Minimize();
            _out.WriteLine(probe.Report());
            probe.Failures.Should().BeEmpty(probe.Report());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ================================================================== 2026-11-12 (аудит Fable, В-13/В-14): проба сама должна падать на этих эталонах

    /// <summary>«Плохой» эталон В-14: пульсирует ТОЛЬКО то, что лежит внутри прокрутки (карточка сужается на каждый шаг колеса); ScrollViewer, его видимая область и ширина содержимого
    /// не меняются — прежняя проба «ничего внутри прокрутки не видела» и оставалась зелёной.</summary>
    private sealed class InnerPulseView : Grid
    {
        public InnerPulseView()
        {
            var card = new Border { Height = 100, Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.IndianRed };
            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(card);
            for (var i = 0; i < 40; i++)
                stack.Children.Add(new Border { Height = 80, Background = Brushes.SteelBlue });
            var sv = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            sv.ScrollChanged += (_, e) =>
            {
                if (Math.Abs(e.OffsetDelta.Y) > 0.01)
                    card.Width = Math.Max(100, card.Width - 5);
            };
            Children.Add(sv);
        }
    }

    /// <summary>«Плохой» эталон В-13: при ПЕРВОЙ прокрутке панель кнопок над прокруткой один раз становится шире и так остаётся; на втором проходе (новый исходный отпечаток) всё стабильно —
    /// прежняя проба писала такое в заметки и была зелёной.</summary>
    private sealed class OneShotJumpView : Grid
    {
        private bool _jumped;

        public OneShotJumpView()
        {
            RowDefinitions = new RowDefinitions("Auto,*");
            var bar = new Border { Height = 40, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Goldenrod };
            Children.Add(bar);
            var stack = new StackPanel { Spacing = 8 };
            for (var i = 0; i < 40; i++)
                stack.Children.Add(new Border { Height = 80, Background = Brushes.SeaGreen });
            var sv = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            SetRow(sv, 1);
            sv.ScrollChanged += (_, e) =>
            {
                if (!_jumped && Math.Abs(e.OffsetDelta.Y) > 0.01)
                {
                    _jumped = true;
                    bar.Width += 60;
                }
            };
            Children.Add(sv);
        }
    }

    private static LayoutStabilityProbe ProbeOn(string screen, Control view, out Window window)
    {
        window = new Window { Width = 800, Height = 500, Content = view };
        window.Show();
        UiShot.Settle(3, 20);
        return new LayoutStabilityProbe(screen, window);
    }

    [AvaloniaFact]
    public void Probe_Catches_PulsationInsideScroll_WhenOnlyContentMoves()
    {
        LayoutStabilityProbe.Trace("=== Probe_Catches_PulsationInsideScroll_WhenOnlyContentMoves");
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var probe = ProbeOn("эталон-плохой-внутри", new InnerPulseView(), out var window);
        try
        {
            probe.Scroll(mustMove: true);
            _out.WriteLine(probe.Report());
            probe.ScrolledAreas.Should().BeGreaterThan(0);
            probe.Failures.Should().NotBeEmpty("сужение карточки внутри прокрутки — пульсация, которую прежняя проба не видела");
            probe.Failures.Should().Contain(f => f.Contains("в прокрутке"), "в сообщении — положение внутри прокрутки");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Probe_LateSettle_IsFailure_UnlessExplicitlyAllowed()
    {
        LayoutStabilityProbe.Trace("=== Probe_LateSettle_IsFailure_UnlessExplicitlyAllowed");
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var strict = ProbeOn("эталон-прыжок", new OneShotJumpView(), out var w1);
        try
        {
            strict.Scroll(mustMove: true);
            _out.WriteLine(strict.Report());
            strict.Failures.Should().ContainSingle(f => f.Contains("ДОЕХАЛА"), "разовый прыжок раскладки (первый проход красный, второй зелёный) — провал, а не заметка");
        }
        finally
        {
            w1.Close();
            UiShot.Pump();
        }

        var allowed = ProbeOn("эталон-прыжок", new OneShotJumpView(), out var w2);
        try
        {
            allowed.AllowedLateSettle.Add("прокрутка: ScrollViewer");
            allowed.Scroll(mustMove: true);
            allowed.Failures.Should().BeEmpty("явно допущенный сценарий не краснеет");
            allowed.Notes.Should().Contain(n => n.StartsWith("ДОПУЩЕНО", StringComparison.Ordinal), "допущение остаётся на виду");
        }
        finally
        {
            w2.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public void Probe_Scroll_MustMove_FailsWhenNothingToScroll()
    {
        LayoutStabilityProbe.Trace("=== Probe_Scroll_MustMove_FailsWhenNothingToScroll");
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var probe = ProbeOn("эталон-без-прокрутки", new Border { Height = 100, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Gray }, out var window);
        try
        {
            probe.Scroll(); // как раньше: «нечего» — заметка, не провал
            probe.Failures.Should().BeEmpty();
            probe.NothingToScroll.Should().Be(1);
            probe.ScrolledAreas.Should().Be(0);
            probe.Scroll(mustMove: true);
            probe.Failures.Should().ContainSingle(f => f.Contains("прокрутка ничего не сдвинула"), "экран без прокрутки не должен засчитываться как проверенный");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-12 (аудит Л-14): раздел владельца, которому не хватает места (минимальная ширина 1500 при области 1000×500), раньше обрезался без прокрутки — кнопки справа и снизу
    /// были недостижимы. Теперь SectionFitHost раскладывает содержимое на нужный размер и даёт прокрутку; когда места хватает — содержимое на всё место, без прокрутки.</summary>
    [AvaloniaFact]
    public void SectionFitHost_ScrollsWhenContentDoesNotFit_AndFillsWhenItFits()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var tail = new Button { Content = "Правая кнопка", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var content = new Grid { MinWidth = 1500, MinHeight = 800, Children = { tail } };
        var host = new SectionFitHost { Child = content };
        var window = new Window { Width = 1000, Height = 500, Content = host };
        try
        {
            window.Show();
            UiShot.Settle(4, 30);
            content.Bounds.Width.Should().BeGreaterThanOrEqualTo(1500, "содержимое раскладывается на нужную ширину, а не сжимается и не обрезается");
            content.Bounds.Height.Should().BeGreaterThanOrEqualTo(800);
            var sv = host.GetVisualDescendants().OfType<ScrollViewer>().First();
            sv.Extent.Width.Should().BeGreaterThanOrEqualTo(1500);
            sv.Extent.Height.Should().BeGreaterThanOrEqualTo(800);
            sv.Offset = new Vector(sv.Extent.Width - sv.Viewport.Width, sv.Extent.Height - sv.Viewport.Height);
            UiShot.Settle(2, 30);
            var p = tail.TranslatePoint(new Point(tail.Bounds.Width, tail.Bounds.Height), window)!.Value;
            p.X.Should().BeLessThanOrEqualTo(window.ClientSize.Width + 1, "после прокрутки правая нижняя кнопка достижима внутри окна");
            p.Y.Should().BeLessThanOrEqualTo(window.ClientSize.Height + 1);
            p.X.Should().BeGreaterThan(window.ClientSize.Width / 2, "кнопка внутри видимой области, а не за левым краем");

            // места хватает — содержимое на всё место, прокрутки нет
            content.MinWidth = 300;
            content.MinHeight = 200;
            UiShot.Settle(4, 30);
            sv.Extent.Width.Should().BeLessThanOrEqualTo(sv.Viewport.Width + 1, "когда места хватает, горизонтальной прокрутки нет");
            sv.Extent.Height.Should().BeLessThanOrEqualTo(sv.Viewport.Height + 1);
            content.Bounds.Width.Should().BeApproximately(host.Bounds.Width, 20, "содержимое растянуто на всё место");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ================================================================== касса: окна каталога экранов

    public static IEnumerable<object[]> KassaScreens()
    {
        var names = new[] { "main-kassa", "purchases", "purchases-receive", "clients", "finance", "sales", "settings", "marketplace", "site-reviews", "site-collections" };
        foreach (var n in names)
        {
            yield return [n, 1024, 640];  // узко: компактные режимы + плавный ресайз 1000→1900→1000 (покрывает 1366 и 1440 по ширине)
            yield return [n, 1900, 1060]; // широко
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(KassaScreens))]
    public async Task Kassa_Screen_LayoutIsStable(string screen, int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SeedCatalog();
        var sw = Stopwatch.StartNew();
        LayoutStabilityProbe.Trace("=== новый тест");
        var window = ScreenCatalog.Get(screen).Create();
        var probe = new LayoutStabilityProbe($"касса {screen} {w}x{h}", window);
        try
        {
            using (UiShot.Render(window, new UiSize(w, h)))
            {
            }
            probe.Settled();
            probe.Scroll();
            probe.Tabs();
            probe.Modal();
            probe.Minimize();
            if (w <= 1024)
            {
                probe.Resize("ресайз", x => window.Width = x, () => window.ClientSize.Width);
                window.Width = w;
                LayoutStabilityProbe.Step(3);
                probe.Scroll("прокрутка после ресайза");
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
        Finish(probe, sw);
    }

    // ================================================================== «Склад»: все вкладки (касса и программа владельца)

    private static TabControl WarehouseTabs(WarehouseWindow wh) => UiDrive.Find<TabControl>(wh, "WarehouseTabs");

    private static TabItem? BarcodesTab(WarehouseWindow wh) =>
        WarehouseTabs(wh).Items.OfType<TabItem>().FirstOrDefault(t => t.Header is StackPanel sp && sp.Children.OfType<TextBlock>().Any(x => x.Text == "Штрихкоды"));

    /// <summary>Каждая видимая вкладка «Склада» (Товары, Приёмка, Штрихкоды, Массовое добавление, Ревизия, Списание, Перемещение, ABC, Редактор этикетки): покой и прокрутка.</summary>
    private static void EveryWarehouseTab(LayoutStabilityProbe probe, WarehouseWindow wh, string[]? only = null)
    {
        var tabs = WarehouseTabs(wh);
        foreach (var tab in tabs.Items.OfType<TabItem>().Where(t => t.IsVisible).ToList())
        {
            var title = LayoutStabilityProbe.TabTitle(tab);
            if (only != null && !only.Any(o => title.Contains(o, StringComparison.OrdinalIgnoreCase)))
                continue;
            tabs.SelectedItem = tab;
            UiShot.Settle(4, 30);
            probe.Settled($"«{title}»: покой");
            probe.Scroll($"«{title}»: прокрутка");
        }
        tabs.SelectedItem = UiDrive.Find<TabItem>(wh, "ProductsTabItem");
        UiShot.Settle(3, 30);
    }

    public static IEnumerable<object[]> KassaWarehouseSizes() =>
        new[] { new object[] { 1024, 640 }, new object[] { 1366, 768 }, new object[] { 1440, 750 }, new object[] { 1900, 1060 } };

    [AvaloniaTheory]
    [MemberData(nameof(KassaWarehouseSizes))]
    public async Task Kassa_Warehouse_EveryTab_LayoutIsStable(int w, int h)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        SeedCatalog();
        var sw = Stopwatch.StartNew();
        LayoutStabilityProbe.Trace("=== новый тест");
        var wh = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var probe = new LayoutStabilityProbe($"касса склад {w}x{h}", wh);
        try
        {
            using (UiShot.Render(wh, new UiSize(w, h)))
            {
            }
            var full = w == 1024 || w == 1900;
            EveryWarehouseTab(probe, wh, full ? null : ["Товары", "Приёмка", "Штрихкоды"]);
            // 2026-11-12 (аудит Т-6): склад на 60 товаров обязан реально прокрутиться хотя бы в одной области — иначе «прокрутка» ничего не проверила
            probe.ScrolledAreas.Should().BeGreaterThan(0, "на складе есть что прокручивать (60 товаров), «колесо мимо» и «нечего» не засчитываются");
            if (full)
                probe.Tabs();
            probe.Modal();
            probe.Minimize();
            if (w == 1024 && BarcodesTab(wh) is { } bc)
            {
                WarehouseTabs(wh).SelectedItem = bc;
                UiShot.Settle(4, 30);
                probe.Resize("«Штрихкоды»: ресайз", x => wh.Width = x, () => wh.ClientSize.Width);
                wh.Width = w;
                LayoutStabilityProbe.Step(3);
                probe.Scroll("«Штрихкоды»: прокрутка после ресайза");
            }
        }
        finally
        {
            wh.Close();
            UiShot.Pump();
        }
        Finish(probe, sw);
    }

    // ================================================================== программа владельца: разделы внутри оболочки (с обёрткой масштаба)

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static Window OpenOwnerSection(OwnerShellWindow shell, string key, Func<Window> create)
    {
        Window? window = null;
        var open = typeof(OwnerShellWindow).GetMethod("OpenSection", Flags, null, [typeof(string), typeof(Func<Window>)], null)!;
        open.Invoke(shell, [key, (Func<Window>)(() => window = create())]);
        UiShot.Settle(8, 30);
        return window!;
    }

    private static void SetOwnerScale(OwnerShellWindow shell, Window? section, double scale)
    {
        typeof(OwnerShellWindow).GetField("_shellScale", Flags)!.SetValue(shell, scale);
        if (section != null)
        {
            typeof(OwnerShellWindow).GetMethod("ApplySectionScale", Flags)!.Invoke(shell, [section]);
            typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", Flags)!.Invoke(shell, null);
        }
        UiShot.Settle(4, 30);
    }

    private static void ResizeShell(OwnerShellWindow shell, double width)
    {
        shell.Width = width;
        LayoutStabilityProbe.Step();
        typeof(OwnerShellWindow).GetMethod("SyncSectionBounds", Flags)!.Invoke(shell, null);
    }

    private static Func<Window>? OwnerSectionFactory(string key) => key switch
    {
        "warehouse" => () => TestHost.Services.GetRequiredService<WarehouseWindow>(),
        "barcodes" => () => new BarcodePrintSectionWindow(),
        "purchases" => () => new PurchasesWindow(),
        "clients" => () => TestHost.Services.GetRequiredService<ClientsWindow>(),
        // AsSalesAndFinance — internal (как в OwnerShellWindow.OpenSalesFinance): через отражение
        "sales-finance" => () => (Window)typeof(FinanceWindow).GetMethod("AsSalesAndFinance", Flags)!.Invoke(TestHost.Services.GetRequiredService<FinanceWindow>(), null)!,
        "abc" => () => TestHost.Services.GetRequiredService<AbcAnalysisWindow>(),
        "settings" => () => TestHost.Services.GetRequiredService<PosSettingsWindow>(),
        "marketplace" => () => new MarketplaceWindow().AsSection(),
        _ => null, // «overview» — сводка самой оболочки
    };

    /// <summary>Набор: «Склад» — в условиях видео (1900×1060 и 1440×750 при 90 %), на 100 % (1366×768, 1024×640) и 125 %; прочие разделы — 1366×768 при 100 % и 1900×1060 при 90 %.
    /// Плавный ресайз оболочки — у «Склада» (вкладка «Штрихкоды») и «Штрихкодов и ценников» при 90 %.</summary>
    public static IEnumerable<object[]> OwnerCases()
    {
        yield return ["warehouse", 1900, 1060, 0.9];
        yield return ["warehouse", 1440, 750, 0.9];
        yield return ["warehouse", 1366, 768, 1.0];
        yield return ["warehouse", 1024, 640, 0.9];
        yield return ["warehouse", 1440, 750, 1.25];
        foreach (var key in new[] { "barcodes", "purchases", "clients", "sales-finance", "abc", "settings", "marketplace", "overview" })
        {
            yield return [key, 1366, 768, 1.0];
            yield return [key, 1900, 1060, 0.9];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(OwnerCases))]
    public async Task Owner_Section_LayoutIsStable(string key, int w, int h, double scale)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        SeedCatalog();
        var sw = Stopwatch.StartNew();
        LayoutStabilityProbe.Trace("=== новый тест");
        var shell = new OwnerShellWindow { Width = w, Height = h };
        Window? section = null;
        LayoutStabilityProbe? probe = null;
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            // масштаб задан до открытия раздела — как у владельца («preference 90 % → applied 90 %» с запуска)
            SetOwnerScale(shell, null, scale);
            if (OwnerSectionFactory(key) is { } factory)
                section = OpenOwnerSection(shell, key, factory);
            // 2026-11-13: ABC — полоса срезов («АВС-анализ / Склад по стоимости / Сезонность») появляется, когда придёт ответ сервера; исходный отпечаток снимаем после неё (иначе «доехала» на 44 px — обычная поздняя загрузка, а не пульсация)
            if (key == "abc" && section != null)
                for (var wait = 0; wait < 50 && !section.GetVisualDescendants().OfType<Button>().Any(b => (UiDrive.TextOf(b) ?? "") == "ABC-анализ" || (UiDrive.TextOf(b) ?? "") == "АВС-анализ"); wait++)
                    UiShot.Settle(1, 100);
            var target = section ?? shell;
            probe = new LayoutStabilityProbe($"владелец {key} {w}x{h} {scale * 100:0}%", target);

            if (section is WarehouseWindow wh)
            {
                var full = scale == 0.9 && w >= 1440 || w == 1366;
                // 2026-11-13: в оболочке владельца таблица товаров заполняется позже, чем в кассе, — без ожидания «Товары: прокрутка» видела пустую таблицу («нечего»); ждём до 4 с, пока появится прокрутка
                for (var wait = 0; wait < 40 && !(wh.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault(g => g.Name == "ProductsGrid") is { } pg && pg.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().Any(b => b.Orientation == Orientation.Vertical && b.Maximum > 1)); wait++)
                    UiShot.Settle(1, 100);
                EveryWarehouseTab(probe, wh, full ? null : ["Товары", "Приёмка", "Штрихкоды"]);
                // 2026-11-12 (аудит Т-6): склад на 60 товаров обязан реально прокрутиться хотя бы в одной области — иначе «прокрутка» ничего не проверила
                probe.ScrolledAreas.Should().BeGreaterThan(0, "на складе есть что прокручивать (60 товаров), «колесо мимо» и «нечего» не засчитываются; заметки: " + string.Join(" | ", probe.Notes.Select(n => n.Length > 160 ? n[..160] : n).Take(12)));
                if (full)
                    probe.Tabs();
                if (BarcodesTab(wh) is { } bc)
                {
                    WarehouseTabs(wh).SelectedItem = bc;
                    UiShot.Settle(4, 30);
                }
            }
            else
            {
                probe.Settled();
                probe.Scroll();
                probe.Tabs();
            }

            if (section != null)
            {
                var other = Math.Abs(scale - 1.0) < 0.001 ? 0.9 : 1.0;
                probe.RoundTrip($"масштаб {scale * 100:0}→{other * 100:0}→{scale * 100:0} %", () => SetOwnerScale(shell, section, other), () => SetOwnerScale(shell, section, scale));
                probe.Scroll("прокрутка после смены масштаба");
            }
            probe.Modal();
            probe.Minimize("сворачивание оболочки", shell);
            if (scale == 0.9 && w == 1900 && key is "warehouse" or "barcodes")
            {
                probe.Resize("ресайз оболочки", x => ResizeShell(shell, x), () => target.ClientSize.Width);
                ResizeShell(shell, w);
                UiShot.Settle(3, 30);
                probe.Scroll("прокрутка после ресайза");
            }
        }
        finally
        {
            section?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
        Finish(probe!, sw);
    }
}
