using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11, жалобы владельца: (А) «затемнение работает криво — сначала долго грузятся анимации, потом тёмная модалка, потом
/// осветляется, дольше 2 с»; (Б) «частое сворачивание — кривые кнопки, программа наполовину; после сворачивания/выхода кнопки перестают
/// работать, в закупках не нажимается удаление». Проверки: затемнение владельца есть ДО разметки тяжёлой модалки и без размытия; цена
/// кадра с размытием и с затемнением (замер Skia без видеокарты — как программный рендер); серия «свернул-развернул» даёт одну перерисовку
/// и не портит размер/масштаб; в «Закупки → Приём товара» ✕ строки нажимается после серии сворачиваний и ухода в фон.</summary>
[Collection("UiScenarios")]
public sealed class ModalDimAndMinimizeTests
{
    private readonly ITestOutputHelper _out;

    public ModalDimAndMinimizeTests(ITestOutputHelper output) => _out = output;

    private static readonly string ReportDir = Path.Combine(Path.GetTempPath(), "nurmarket-tests");

    // ---------------------------------------------------------------- чистая логика

    [Fact]
    public void Degenerate_window_geometry_is_recognised()
    {
        WindowRestoreGuard.IsDegenerate(new PixelPoint(-32000, -32000), new Size(160, 28)).Should().BeTrue("свёрнутое окно Windows");
        WindowRestoreGuard.IsDegenerate(new PixelPoint(0, 0), new Size(0, 0)).Should().BeTrue("размер ещё не задан");
        WindowRestoreGuard.IsDegenerate(new PixelPoint(0, 0), new Size(double.NaN, 600)).Should().BeTrue();
        WindowRestoreGuard.IsDegenerate(new PixelPoint(-1920, 0), new Size(1920, 1080)).Should().BeFalse("монитор слева — отрицательные координаты нормальны");
        WindowRestoreGuard.IsDegenerate(new PixelPoint(0, 0), new Size(1366, 728)).Should().BeFalse();
    }

    // 2026-11-11 (Д-3, живая проверка «Склада»): после второго сворачивания окно владельца «FullScreen», но 1936×1040 в (−8,0) — края за экраном, снизу панель задач.
    [Fact]
    public void Fullscreen_geometry_after_restore_is_checked_against_screen()
    {
        var screen = new PixelRect(0, 0, 1920, 1080);
        var work = new PixelRect(0, 0, 1920, 1040);

        var full = FullscreenHelper.ExpectedRect(WindowState.FullScreen, borderless: false, screen, work);
        full.Should().Be(screen, "полный экран — весь экран, с панелью задач");
        FullscreenHelper.NeedsGeometryFix(new PixelRect(-8, 0, 1936, 1040), full).Should().BeTrue("живой случай NBS: на 8 px за краями, высота рабочей области");
        FullscreenHelper.NeedsGeometryFix(new PixelRect(0, 0, 1920, 1080), full).Should().BeFalse("правильный полный экран");
        FullscreenHelper.NeedsGeometryFix(new PixelRect(1, 0, 1921, 1080), full).Should().BeFalse("округление масштаба — не ошибка");

        var max = FullscreenHelper.ExpectedRect(WindowState.Maximized, borderless: true, screen, work);
        max.Should().Be(work, "развёрнутое без рамки — рабочая область без панели задач");
        FullscreenHelper.NeedsGeometryFix(new PixelRect(0, 0, 1920, 1040), max, FullscreenHelper.MaximizedTolerance).Should().BeFalse();
        FullscreenHelper.NeedsGeometryFix(new PixelRect(-8, -8, 1936, 1056), max, FullscreenHelper.MaximizedTolerance).Should().BeFalse("невидимая рамка Windows 8 px — не дёргаем окно");
        FullscreenHelper.NeedsGeometryFix(new PixelRect(0, 0, 1920, 1080), max, FullscreenHelper.MaximizedTolerance).Should().BeTrue("развёрнутое залезло под панель задач");

        FullscreenHelper.ExpectedRect(WindowState.Maximized, borderless: false, screen, work).Should().BeNull("с рамкой ОС место задаёт Windows");
        FullscreenHelper.ExpectedRect(WindowState.Normal, borderless: true, screen, work).Should().BeNull();
        FullscreenHelper.NeedsGeometryFix(new PixelRect(-8, 0, 1936, 1040), null).Should().BeFalse();

        // второй монитор слева (отрицательные координаты)
        var left = new PixelRect(-1366, 0, 1366, 768);
        FullscreenHelper.NeedsGeometryFix(new PixelRect(-1366, 0, 1366, 768), FullscreenHelper.ExpectedRect(WindowState.FullScreen, false, left, left)).Should().BeFalse();
    }

    [Fact]
    public void Slow_modal_open_is_logged_only_when_slow_and_throttled()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        ModalOpenTiming.ShouldLog(300, DateTime.MinValue, now).Should().BeFalse("быстро — молчим");
        ModalOpenTiming.ShouldLog(501, DateTime.MinValue, now).Should().BeTrue();
        ModalOpenTiming.ShouldLog(2500, now.AddSeconds(-5), now).Should().BeFalse("не чаще раза в 30 с");
        ModalOpenTiming.ShouldLog(2500, now.AddSeconds(-31), now).Should().BeTrue();
        ModalOpenTiming.Format("CheckoutDialog", 1200, 2100, 3).Should().Contain("2100 мс").And.Contain("1200 мс").And.Contain("3");
    }

    // ---------------------------------------------------------------- (А) затемнение

    private sealed class HeavyContent : Control
    {
        private readonly Action _onFirstMeasure;
        private bool _measured;

        public HeavyContent(Action onFirstMeasure) => _onFirstMeasure = onFirstMeasure;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!_measured)
            {
                _measured = true;
                _onFirstMeasure();
                Thread.Sleep(400); // «тяжёлое содержимое»: долгая первая разметка
            }
            return new Size(400, 300);
        }
    }

    private static IEnumerable<Border> DimLayers(Window w) =>
        OverlayLayer.GetOverlayLayer(w)?.Children.OfType<Border>().Where(b => b.Classes.Contains("nur-modal-dim")) ?? Enumerable.Empty<Border>();

    private static Window MakeOwner(int width, int height)
    {
        var panel = new WrapPanel();
        for (var i = 0; i < 400; i++)
            panel.Children.Add(new Border
            {
                Width = 120, Height = 44, Margin = new Thickness(4), Background = (i % 3) switch { 0 => Brushes.SteelBlue, 1 => Brushes.Goldenrod, _ => Brushes.SeaGreen },
                Child = new TextBlock { Text = "Товар " + i, Foreground = Brushes.White, Margin = new Thickness(6) },
            });
        return new Window { Width = width, Height = height, Content = panel, Background = Brushes.White };
    }

    [AvaloniaFact]
    public async Task Heavy_dialog_owner_is_dimmed_before_dialog_layout_and_without_blur()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = MakeOwner(1366, 768);
        owner.Show();
        UiShot.Pump();
        var sw = new Stopwatch();
        double dimAtLayout = -1, msAtLayout = -1;
        var dialog = new Window
        {
            Width = 400, Height = 300, SystemDecorations = SystemDecorations.None,
            Content = new HeavyContent(() =>
            {
                msAtLayout = sw.Elapsed.TotalMilliseconds;
                dimAtLayout = DimLayers(owner).Select(b => b.Opacity).DefaultIfEmpty(0).Max();
            }),
        };
        try
        {
            sw.Start();
            var task = PosDialogHost.ShowAsync(dialog, owner);
            var shownMs = sw.Elapsed.TotalMilliseconds;
            UiShot.Pump();

            _out.WriteLine($"затемнение к началу разметки модалки: {dimAtLayout:0.00} (через {msAtLayout:0} мс), ShowDialog вернулся через {shownMs:0} мс");
            dimAtLayout.Should().BeGreaterThan(0.2, "затемнение владельца стоит ДО первой разметки тяжёлой модалки (было — после показа и нарастало 140 мс)");
            msAtLayout.Should().BeLessThan(100, "до разметки модалки ничего долгого не происходит");
            ((Control)owner.Content!).Effect.Should().BeNull("размытия фона больше нет — только затемнение");
            DimLayers(owner).Should().ContainSingle().Which.IsHitTestVisible.Should().BeTrue("затемнение закрывает окно-владельца от нажатий");

            dialog.Close();
            UiShot.Pump();
            await task;
        }
        finally
        {
            if (dialog.IsVisible)
                dialog.Close();
            owner.Close();
            UiShot.Pump();
        }
        DimLayers(owner).Should().BeEmpty("окно закрыто — слой затемнения убран");
    }

    /// <summary>Замер: кадр окна кассы, когда меняется радиус размытия (как делала прежняя анимация на каждом кадре), против кадра, когда меняется
    /// только непрозрачность слоя затемнения. Skia без видеокарты — тот же путь, что «программный рендер» слабых касс.</summary>
    [AvaloniaTheory]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public void Blur_frames_cost_more_than_dim_frames(int width, int height)
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var owner = MakeOwner(width, height);
        owner.Show();
        UiShot.Settle(2);
        try
        {
            var content = (Control)owner.Content!;
            var layer = OverlayLayer.GetOverlayLayer(owner)!;
            var dim = new Border { Background = Brushes.Black, Opacity = 0, Width = width, Height = height };
            layer.Children.Add(dim);

            double Measure(Action<int> change)
            {
                const int frames = 8;
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < frames; i++)
                {
                    change(i);
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = owner.CaptureRenderedFrame();
                }
                return sw.Elapsed.TotalMilliseconds / frames;
            }

            Measure(i => dim.Opacity = 0.1 + i * 0.01); // прогрев
            var dimMs = Measure(i => dim.Opacity = 0.05 + i * 0.04);
            var blurMs = Measure(i => content.Effect = new BlurEffect { Radius = 1 + (i % 6) });
            content.Effect = null;
            layer.Children.Remove(dim);

            var line = $"{width}x{height}: кадр с затемнением {dimMs:0.0} мс, кадр с новым BlurEffect {blurMs:0.0} мс (×{blurMs / Math.Max(0.1, dimMs):0.0})";
            _out.WriteLine(line);
            Directory.CreateDirectory(ReportDir);
            File.AppendAllText(Path.Combine(ReportDir, "modal-dim-timing.txt"), DateTime.Now.ToString("s") + " " + line + Environment.NewLine);
            // На 1366×768 разница на быстром процессоре мала (×1,2–1,3) и при нагрузке ПК может «перевернуться» — проверяем на Full HD (×1,8–2,1).
            if (width >= 1920)
                blurMs.Should().BeGreaterThan(dimMs, "размытие всего окна на каждом кадре дороже затемнения");
        }
        finally
        {
            owner.Close();
            UiShot.Pump();
        }
    }

    // ---------------------------------------------------------------- (Б) сворачивание

    /// <summary>Цикл диспетчера на <paramref name="ms"/> мс: в Headless таймеры DispatcherTimer срабатывают только внутри цикла (RunJobs их не продвигает).</summary>
    private static void RunLoopFor(int ms)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(ms + 2000)); // страховка
        using var stop = DispatcherTimer.RunOnce(() => cts.Cancel(), TimeSpan.FromMilliseconds(ms));
        Dispatcher.UIThread.MainLoop(cts.Token);
        UiShot.Pump();
    }

    [AvaloniaFact]
    public void Minimize_bounce_gives_one_settle_and_keeps_size_and_scale()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var root = new LayoutTransformControl { Child = new Border { Width = 1280, Height = 840 } };
        var window = new Window { Width = 1366, Height = 728, Content = root };
        window.Show();
        UiShot.Pump();
        UiScaleHelper.Apply(root, 1280, 840);
        var scaleBefore = (root.LayoutTransform as ScaleTransform)?.ScaleX ?? 1.0;
        var restored = 0;
        WindowRestoreGuard.Attach(window, () => restored++);
        var resetsBefore = InputStateReset.ResetCount;
        try
        {
            for (var i = 0; i < 8; i++)
            {
                window.WindowState = WindowState.Minimized;
                Dispatcher.UIThread.RunJobs();
                window.WindowState = WindowState.Normal;
                Dispatcher.UIThread.RunJobs();
            }
            restored.Should().Be(0, "пока идёт серия сворачиваний — ни одной перестройки (дребезг)");
            InputStateReset.ResetCount.Should().BeGreaterThanOrEqualTo(resetsBefore + 8, "при каждом сворачивании сбрасывается залипший ввод");

            RunLoopFor(600);
            _out.WriteLine($"окно: позиция {window.Position}, клиент {window.ClientSize}, видно {window.IsVisible}, свёрнуто {WindowRestoreGuard.IsMinimizedNow(window)}, пропуск {WindowRestoreGuard.ShouldSkipLayoutWork(window)}, ручка {window.TryGetPlatformHandle()?.Handle}");
            restored.Should().Be(1, "после серии — одна перерисовка и пересчёт раскладки");
            WindowRestoreGuard.SettledCount(window).Should().Be(1);
            window.Width.Should().Be(1366);
            window.Height.Should().Be(728);
            ((root.LayoutTransform as ScaleTransform)?.ScaleX ?? 1.0).Should().Be(scaleBefore, "масштаб интерфейса не испорчен");

            // Свернули и оставили свёрнутым — перерисовки нет.
            window.WindowState = WindowState.Minimized;
            RunLoopFor(300);
            restored.Should().Be(1);
            window.WindowState = WindowState.Normal;
            RunLoopFor(600);
            restored.Should().Be(2);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Purchases_receive_remove_button_works_after_minimize_and_background()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var saved = CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        var names = new[] { "Сахар 1 кг", "Мука 2 кг", "Масло 1 л" };
        for (var i = 0; i < names.Length; i++)
            CatalogCacheService.Products.Add(new CatalogProductTileVm("mm" + i, names[i], "50 сом", false) { Barcode = "460000000010" + i, Unit = "шт", Quantity = 10, PurchasePrice = 40 });
        var window = new PurchasesWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            window.OpenReceive(new ReceivePrefill(null, null, null, null,
                names.Select((n, i) => ("mm" + i, n, "шт", 1.0, (double?)40)).ToArray()));
            UiShot.Settle(12);
            WindowRestoreGuard.Attach(window);

            List<Button> RemoveButtons() => window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.IsEffectivelyVisible && (Avalonia.Automation.AutomationProperties.GetName(b) ?? "").StartsWith("Убрать строку", StringComparison.Ordinal)).ToList();
            RemoveButtons().Should().HaveCount(3);

            // Нажатие в поле количества и «потеря» отпускания (окно ушло в фон с зажатой кнопкой — захват остаётся у поля).
            var field = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Tag as string == "rcv-cell");
            var fieldPoint = field.TranslatePoint(new Point(field.Bounds.Width / 2, field.Bounds.Height / 2), window)!.Value;
            window.MouseDown(fieldPoint, MouseButton.Left);
            UiShot.Pump();

            for (var i = 0; i < 6; i++)
            {
                window.WindowState = WindowState.Minimized;
                Dispatcher.UIThread.RunJobs();
                window.WindowState = WindowState.Normal;
                Dispatcher.UIThread.RunJobs();
                InputStateReset.OnDeactivated(window); // ушли в другое приложение и вернулись
                UiShot.Pump();
            }
            window.MouseUp(fieldPoint, MouseButton.Left); // кнопка мыши отпущена (устройство Headless — в исходное состояние)
            UiShot.Settle(6);

            // Все ✕ видимы, доступны и ничем не перекрыты (никакого слоя поверх).
            foreach (var b in RemoveButtons())
            {
                b.IsEffectivelyEnabled.Should().BeTrue();
                var c = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), window)!.Value;
                var hit = window.InputHitTest(c) as Visual;
                (hit is not null && (ReferenceEquals(hit, b) || hit.GetVisualAncestors().Contains(b))).Should().BeTrue("нажатие по ✕ попадает в саму кнопку, а не в слой поверх");
            }

            UiDrive.Click(window, RemoveButtons()[0]);
            UiShot.Settle(4);
            RemoveButtons().Should().HaveCount(2, "✕ строки приёмки нажимается после сворачиваний и ухода в фон");
            UiDrive.Click(window, RemoveButtons()[^1]);
            UiShot.Settle(4);
            RemoveButtons().Should().HaveCount(1);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(saved);
        }
    }
}
