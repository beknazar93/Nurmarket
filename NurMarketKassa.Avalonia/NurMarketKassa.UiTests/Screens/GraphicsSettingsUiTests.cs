using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Win32;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (решение владельца): две настройки в «Настройки → Экран → Производительность».
/// (1) «Размытие фона под окнами (для мощных ПК)»: по умолчанию выкл, действует сразу (ModalMotion.BlurEnabled), «режим слабого устройства» его
/// выключает, 3 медленных открытия модалок подряд — предложение выключить. (2) «Режим отрисовки» (Windows): Авто / Совместимый / Программный,
/// сопоставление с Win32PlatformOptions, маркер аварийного отката «render-mode-trial». Снимки карточки: %TEMP%\nurmarket-tests\ui-actual\gfx-settings-*.png.</summary>
[Collection("UiScenarios")]
public sealed class GraphicsSettingsUiTests
{
    private sealed class PrefsScope : IDisposable
    {
        private readonly bool _blur = UserPreferences.Instance.ModalBackgroundBlur;
        private readonly bool _low = UserPreferences.Instance.LowPerformanceMode;
        private readonly string _mode = UserPreferences.Instance.WindowsRenderMode;

        public PrefsScope(bool blur = false, bool low = false, string mode = RenderModeSettings.Auto)
        {
            var p = UserPreferences.Instance;
            p.ModalBackgroundBlur = blur;
            p.LowPerformanceMode = low;
            p.WindowsRenderMode = mode;
            ModalBlurGuard.ResetForTests();
        }

        public void Dispose()
        {
            var p = UserPreferences.Instance;
            p.ModalBackgroundBlur = _blur;
            p.LowPerformanceMode = _low;
            p.WindowsRenderMode = _mode;
            ModalBlurGuard.ResetForTests();
        }
    }

    // ---------------------------------------------------------------- настройки по умолчанию, чтение/запись

    [Fact]
    public void Defaults_are_blur_off_and_render_auto()
    {
        var fresh = new UserPreferences();
        fresh.ModalBackgroundBlur.Should().BeFalse("размытие по умолчанию выключено");
        fresh.WindowsRenderMode.Should().Be(RenderModeSettings.Auto, "режим отрисовки по умолчанию — как было");
        RenderModeSettings.Normalize(fresh.WindowsRenderMode).Should().Be(RenderModeSettings.Auto);
    }

    [Fact]
    public void Settings_are_written_to_user_settings_and_read_back_before_start()
    {
        // Пишем только во временную папку стенда (test-safe ставит NURMARKET_DATA_ROOT); без неё — не трогаем файл настроек.
        if (AppPaths.DataRootOverride is null)
            return;
        using var scope = new PrefsScope(blur: true, mode: RenderModeSettings.Compatible);
        UserPreferences.Instance.SaveToDisk();
        var json = File.ReadAllText(RenderModeSettings.SettingsFilePath);
        RenderModeSettings.ReadModeFromSettingsJson(json).Should().Be(RenderModeSettings.Compatible, "Program читает режим из user-settings.json до старта окна");
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
            doc.RootElement.GetProperty(nameof(UserPreferences.ModalBackgroundBlur)).GetBoolean().Should().BeTrue();
        scope.Dispose();
        UserPreferences.Instance.SaveToDisk();
        RenderModeSettings.ReadModeFromSettingsJson(File.ReadAllText(RenderModeSettings.SettingsFilePath)).Should().Be(RenderModeSettings.Auto);
    }

    [Fact]
    public void Render_mode_text_is_parsed_safely()
    {
        RenderModeSettings.Normalize(null).Should().Be(RenderModeSettings.Auto);
        RenderModeSettings.Normalize(" COMPAT ").Should().Be(RenderModeSettings.Compatible);
        RenderModeSettings.Normalize("software").Should().Be(RenderModeSettings.Software);
        RenderModeSettings.Normalize("gpu-magic").Should().Be(RenderModeSettings.Auto, "неизвестное — Авто");
        RenderModeSettings.ReadModeFromSettingsJson("{\"WindowsRenderMode\":\"software\",\"LowPerformanceMode\":false}").Should().Be(RenderModeSettings.Software);
        RenderModeSettings.ReadModeFromSettingsJson("{}").Should().Be(RenderModeSettings.Auto);
        RenderModeSettings.ReadModeFromSettingsJson("{ битый").Should().Be(RenderModeSettings.Auto);
        RenderModeSettings.ReadModeFromSettingsJson(null).Should().Be(RenderModeSettings.Auto);
    }

    // ---------------------------------------------------------------- размытие

    [Fact]
    public void Low_performance_mode_blocks_blur()
    {
        ModalMotion.IsBlurOn(setting: true, lowPerformance: false).Should().BeTrue();
        ModalMotion.IsBlurOn(setting: true, lowPerformance: true).Should().BeFalse("в режиме слабого устройства размытия нет");
        ModalMotion.IsBlurOn(setting: false, lowPerformance: false).Should().BeFalse();
    }

    [Fact]
    public void Blur_switches_on_and_off_live()
    {
        using var scope = new PrefsScope();
        ModalMotion.BlurEnabled.Should().BeFalse("по умолчанию");
        UserPreferences.Instance.ModalBackgroundBlur = true;
        ModalMotion.BlurEnabled.Should().BeTrue("включили — следующая модалка уже с размытием, без перезапуска");
        UserPreferences.Instance.LowPerformanceMode = true;
        ModalMotion.BlurEnabled.Should().BeFalse("слабое устройство выключает размытие");
        UserPreferences.Instance.LowPerformanceMode = false;
        UserPreferences.Instance.ModalBackgroundBlur = false;
        ModalMotion.BlurEnabled.Should().BeFalse();
    }

    [Fact]
    public void Slow_guard_suggests_after_three_slow_opens_in_a_row()
    {
        var s = (Count: 0, Suggest: false);
        s = ModalBlurGuard.Step(s.Count, false, true, 700);
        s = ModalBlurGuard.Step(s.Count, false, true, 900);
        s.Suggest.Should().BeFalse("два медленных — ещё рано");
        s = ModalBlurGuard.Step(s.Count, false, true, 300);
        s.Count.Should().Be(0, "быстрое открытие сбрасывает счёт");
        s = ModalBlurGuard.Step(s.Count, false, true, 600);
        s = ModalBlurGuard.Step(s.Count, false, true, 600);
        s = ModalBlurGuard.Step(s.Count, false, true, 501);
        s.Suggest.Should().BeTrue("три медленных подряд (> 500 мс)");
        ModalBlurGuard.Step(2, false, true, 500).Suggest.Should().BeFalse("ровно 500 мс — не медленно");
        ModalBlurGuard.Step(2, false, blurOn: false, 2000).Suggest.Should().BeFalse("размытие выключено — не спрашиваем");
        ModalBlurGuard.Step(2, alreadyAsked: true, true, 2000).Suggest.Should().BeFalse("один вопрос за запуск");
    }

    [AvaloniaFact]
    public void Slow_modal_opens_offer_to_turn_blur_off_once()
    {
        if (AppPaths.DataRootOverride is null)
            return; // 2026-11-11: тест пишет user-settings.json — только во временной папке стенда (test-safe)
        using var scope = new PrefsScope(blur: true);
        var asked = 0;
        ModalBlurGuard.Ask = () =>
        {
            asked++;
            return Task.FromResult(true);
        };
        ModalOpenTiming.Report("TestDialog", 100, 800);
        ModalOpenTiming.Report("TestDialog", 100, 800);
        UiShot.Pump();
        asked.Should().Be(0);
        ModalOpenTiming.Report("TestDialog", 100, 800);
        UiShot.Settle(3);
        asked.Should().Be(1, "три медленных открытия подряд при включённом размытии — вопрос «Размытие тормозит — выключить?»");
        UserPreferences.Instance.ModalBackgroundBlur.Should().BeFalse("«Выключить» — размытие выключено сразу");
        ModalMotion.BlurEnabled.Should().BeFalse();

        UserPreferences.Instance.ModalBackgroundBlur = true;
        for (var i = 0; i < 6; i++)
            ModalOpenTiming.Report("TestDialog", 100, 800);
        UiShot.Settle(3);
        asked.Should().Be(1, "второй раз за запуск не спрашиваем");
    }

    // ---------------------------------------------------------------- режим отрисовки

    [Fact]
    public void Render_mode_maps_to_win32_options()
    {
        RenderModeWin32.BuildOptions(RenderModeSettings.Auto, lowPerformance: false).Should().BeNull("Авто — параметры Avalonia по умолчанию, как было");
        RenderModeWin32.BuildOptions("непонятно", lowPerformance: false).Should().BeNull();

        var compat = RenderModeWin32.BuildOptions(RenderModeSettings.Compatible, lowPerformance: false)!;
        compat.Should().NotBeNull();
        compat.CompositionMode.Should().Equal(new[] { Win32CompositionMode.RedirectionSurface }, "без DirectComposition/WinUI-композиции");
        compat.RenderingMode.Should().Equal(new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }, "видеокарта остаётся");

        var software = RenderModeWin32.BuildOptions(RenderModeSettings.Software, lowPerformance: false)!;
        software.RenderingMode.Should().Equal(new[] { Win32RenderingMode.Software });

        var low = RenderModeWin32.BuildOptions(RenderModeSettings.Compatible, lowPerformance: true)!;
        low.RenderingMode.Should().Equal(new[] { Win32RenderingMode.Software }, "режим слабого устройства — программная отрисовка, как раньше");
    }

    [Fact]
    public void Render_mode_option_is_windows_only()
    {
        RenderModeSettings.IsAvailable(isWindows: true, isAndroid: false).Should().BeTrue();
        RenderModeSettings.IsAvailable(isWindows: false, isAndroid: false).Should().BeFalse("Linux/macOS — пункт скрыт");
        RenderModeSettings.IsAvailable(isWindows: false, isAndroid: true).Should().BeFalse("Android — пункт скрыт");
        RenderModeSettings.PrepareAt("нет-файла", "нет-маркера", lowPerformance: false, available: false).Should().BeNull("не Windows — ничего не делаем");
    }

    [Fact]
    public void Startup_decision_rules()
    {
        RenderModeSettings.Decide(RenderModeSettings.Auto, false, false).Should().Be(new RenderModeSettings.StartupDecision("auto", "auto", false, false));
        RenderModeSettings.Decide(RenderModeSettings.Compatible, false, false).Should().Be(new RenderModeSettings.StartupDecision("compat", "compat", false, true));
        RenderModeSettings.Decide(RenderModeSettings.Compatible, false, true).Should().Be(new RenderModeSettings.StartupDecision("compat", "auto", true, false),
            "маркер прошлого запуска на месте — прошлый запуск упал до окна, этот идёт в «Авто»");
        RenderModeSettings.Decide(RenderModeSettings.Software, true, false).StartTrial.Should().BeFalse("слабое устройство и так программное — проверка не нужна");
        RenderModeSettings.Decide(RenderModeSettings.Auto, false, true).RolledBack.Should().BeFalse("режим уже «Авто» — откатывать нечего");
    }

    [Fact]
    public void Crash_with_custom_render_mode_rolls_back_to_auto()
    {
        if (AppPaths.DataRootOverride is null)
            return; // 2026-11-11: тест пишет user-settings.json — только во временной папке стенда (test-safe)
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "render-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = Path.Combine(dir, "user-settings.json");
        var marker = Path.Combine(dir, "Local", RenderModeSettings.TrialMarkerName);
        var notices = new List<string>();
        var oldNotify = RenderModeSettings.Notify;
        RenderModeSettings.Notify = notices.Add;
        using var scope = new PrefsScope(mode: RenderModeSettings.Compatible);
        try
        {
            File.WriteAllText(settings, "{\"WindowsRenderMode\":\"compat\"}");

            // Запуск 1: нестандартный режим — маркер проверки поставлен.
            var first = RenderModeSettings.PrepareAt(settings, marker, lowPerformance: false, available: true)!;
            first.Effective.Should().Be(RenderModeSettings.Compatible);
            first.StartTrial.Should().BeTrue();
            File.Exists(marker).Should().BeTrue("маркер «render-mode-trial» ставится при старте с нестандартным режимом");

            // «Упал»: окно не показалось, маркер остался. Запуск 2 — «Авто».
            var second = RenderModeSettings.PrepareAt(settings, marker, lowPerformance: false, available: true)!;
            second.RolledBack.Should().BeTrue();
            second.Effective.Should().Be(RenderModeSettings.Auto);
            File.Exists(marker).Should().BeTrue("маркер держится, пока «Авто» не записано в настройки (иначе следующий запуск снова упал бы)");

            // Главное окно показано — настройка «Авто», маркер снят, уведомление.
            RenderModeSettings.CompleteRollback(marker);
            UserPreferences.Instance.WindowsRenderMode.Should().Be(RenderModeSettings.Auto);
            File.Exists(marker).Should().BeFalse();
            notices.Should().ContainSingle().Which.Should().Be(RenderModeSettings.RollbackNotice, "«Режим отрисовки сброшен после сбоя» (на языке программы)");

            // Удачный запуск: маркер снимается после показа главного окна.
            var third = RenderModeSettings.PrepareAt(settings, marker, lowPerformance: false, available: true)!;
            third.StartTrial.Should().BeTrue();
            RenderModeSettings.CompleteTrial(marker, third.Effective);
            File.Exists(marker).Should().BeFalse();
            RenderModeSettings.PrepareAt(settings, marker, lowPerformance: false, available: true)!.RolledBack.Should().BeFalse("прошлый запуск дошёл до окна");

            // Остаток маркера при режиме «Авто» просто убирается.
            File.WriteAllText(settings, "{\"WindowsRenderMode\":\"auto\"}");
            var stale = RenderModeSettings.PrepareAt(settings, marker, lowPerformance: false, available: true)!;
            stale.RolledBack.Should().BeFalse();
            File.Exists(marker).Should().BeFalse();
        }
        finally
        {
            RenderModeSettings.Notify = oldNotify;
            RenderModeSettings.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
        }
    }

    // ---------------------------------------------------------------- карточка настроек

    public static IEnumerable<object[]> Sizes() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task Performance_card_shows_blur_and_render_mode_without_clipping(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        using var scope = new PrefsScope();
        var view = new ScreenSettingsView();
        var window = new Window { Content = view };
        try
        {
            view.FindControl<ToggleButton>("PerfGroupToggle")!.IsChecked = true;
            using (UiShot.Render(window, new UiSize(width, height))) { }
            var tag = $"{width}x{height} {lang}";

            var blur = view.BlurCheck!;
            blur.IsEffectivelyVisible.Should().BeTrue(tag);
            blur.IsChecked.Should().BeFalse("по умолчанию выключено");
            blur.IsEnabled.Should().BeTrue();
            view.RenderModeBlock!.IsEffectivelyVisible.Should().BeTrue("Windows — пункт «Режим отрисовки» виден");
            Radio(view, "GfxRenderAuto").IsChecked.Should().BeTrue("по умолчанию «Авто»");
            Find<Button>(view, "GfxRenderReset").IsEnabled.Should().BeFalse("уже «Авто»");

            // Прокрутка к группе — снимок с видимыми строками.
            view.RenderModeBlock!.BringIntoView();
            UiShot.Settle(3);
            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            var clipped = TextClipChecker.Find(window, "gfx-settings", langCode).Where(v => v.Detail.Contains("#Gfx")).ToList();
            clipped.Should().BeEmpty($"текст строк не обрезан ({tag}): " + string.Join("; ", clipped.Select(v => v.Detail)));
            using var shot = UiShot.Render(window);
            using var image = SKImage.FromBitmap(shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"gfx-settings-{langCode}-{width}x{height}.png"), data.ToArray());
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Toggles_apply_right_away_and_low_performance_locks_blur()
    {
        if (AppPaths.DataRootOverride is null)
            return; // 2026-11-11: тест пишет user-settings.json — только во временной папке стенда (test-safe)
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        using var scope = new PrefsScope();
        var view = new ScreenSettingsView();
        var window = new Window { Content = view };
        try
        {
            view.FindControl<ToggleButton>("PerfGroupToggle")!.IsChecked = true;
            using (UiShot.Render(window, UiSize.Small)) { }

            view.BlurCheck!.IsChecked = true;
            UserPreferences.Instance.ModalBackgroundBlur.Should().BeTrue("переключатель действует сразу, без «Сохранить»");
            ModalMotion.BlurEnabled.Should().BeTrue();

            view.LowPerformanceModeCheck.IsChecked = true;
            UiShot.Pump();
            view.BlurCheck.IsEnabled.Should().BeFalse("в режиме слабого устройства переключатель неактивен");
            view.BlurCheck.IsChecked.Should().BeFalse();
            Find<TextBlock>(view, "GfxBlurDesc").Text.Should().Contain("слабого устройства");
            UserPreferences.Instance.ModalBackgroundBlur.Should().BeTrue("выбор пользователя не теряется — вернётся после выключения режима");
            view.LowPerformanceModeCheck.IsChecked = false;
            UiShot.Pump();
            view.BlurCheck.IsEnabled.Should().BeTrue();
            view.BlurCheck.IsChecked.Should().BeTrue();

            Radio(view, "GfxRenderCompat").IsChecked = true;
            UserPreferences.Instance.WindowsRenderMode.Should().Be(RenderModeSettings.Compatible, "выбор сохраняется сразу, действует после перезапуска");
            Find<TextBlock>(view, "GfxRenderNote").Text.Should().Contain("после перезапуска");
            var reset = Find<Button>(view, "GfxRenderReset");
            reset.IsEnabled.Should().BeTrue();
            reset.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            UserPreferences.Instance.WindowsRenderMode.Should().Be(RenderModeSettings.Auto, "«Вернуть как было» — Авто");
            Radio(view, "GfxRenderAuto").IsChecked.Should().BeTrue();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static RadioButton Radio(Control root, string name) => Find<RadioButton>(root, name);
}
