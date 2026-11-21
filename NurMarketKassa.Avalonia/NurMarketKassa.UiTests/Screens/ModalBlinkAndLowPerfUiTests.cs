using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 (жалобы владельца: «моргает, когда модалка открывается/закрывается» и «при включении режима слабого ПК не обязательно перезаходить»).
/// (1) Окно получает итоговый размер ДО показа (DialogScreenFit.PrepareBeforeShow) и не меняет его после Opened — состояние окна в первом кадре и после показа одно и то же.
/// (2) Режим слабого устройства меняется без перезапуска: настройка действует сразу (событие), анимации модалок выключаются, ресурсы темы (окна диалогов) пересобираются,
/// строка «применится после перезапуска» есть только для программной отрисовки.</summary>
[Collection("UiScenarios")]
public sealed class ModalBlinkAndLowPerfUiTests
{
    private static readonly MethodInfo PrepareBeforeShow = typeof(Window).Assembly
        .GetType("NurMarketKassa.AvaloniaHost.Views.Dialogs.DialogScreenFit")!
        .GetMethod("PrepareBeforeShow", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo OnWindowOpened = typeof(Window).Assembly
        .GetType("NurMarketKassa.AvaloniaHost.Views.Dialogs.DialogScreenFit")!
        .GetMethod("OnWindowOpened", BindingFlags.NonPublic | BindingFlags.Static)!;

    private sealed class LowScope : IDisposable
    {
        private readonly bool _low = UserPreferences.Instance.LowPerformanceMode;
        private readonly bool _hint = UserPreferences.Instance.WeakPcHintShown;

        public void Dispose()
        {
            UserPreferences.Instance.LowPerformanceMode = _low;
            UserPreferences.Instance.WeakPcHintShown = _hint;
        }
    }

    // ---------------------------------------------------------------- моргание: размер окна до и после показа

    [AvaloniaFact]
    public async Task Oversized_window_gets_final_size_before_show_and_keeps_it_after_opened()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // Окно в XAML «рассчитано на большой монитор» — как Склад/Настройки/Редакторы; экран заметно меньше.
        var window = new Window { Width = 6000, Height = 5000, Content = new Border { Background = Avalonia.Media.Brushes.White } };
        try
        {
            window.IsVisible.Should().BeFalse("окно ещё не показано");
            PrepareBeforeShow.Invoke(null, [window]);

            var beforeShow = (window.Width, window.Height, window.MaxWidth, window.MaxHeight);
            beforeShow.Width.Should().BeLessThan(6000, "размер подогнан под экран до показа, а не после Opened");
            beforeShow.Height.Should().BeLessThan(5000);

            var openedSizes = new List<(double W, double H)>();
            window.Opened += (_, _) => openedSizes.Add((window.Width, window.Height));
            window.Show();
            UiShot.Pump();
            OnWindowOpened.Invoke(null, [window]); // тот же обработчик, что в программе: после показа размер больше не меняется
            UiShot.Pump();

            openedSizes.Should().ContainSingle();
            (window.Width, window.Height).Should().Be((beforeShow.Width, beforeShow.Height), "после Opened окно не «сжимается» второй раз — нет вспышки большого окна");
            window.Opacity.Should().Be(1, "показ окна не оставляет его прозрачным");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Pre_show_fit_runs_once_per_show_and_ignores_the_main_kassa_windows()
    {
        await TestHost.WarmAsync();
        var small = new Window { Width = 400, Height = 300 };
        PrepareBeforeShow.Invoke(null, [small]);
        PrepareBeforeShow.Invoke(null, [small]);
        small.Width.Should().Be(400, "окно, которое и так помещается, не меняется");
        small.Height.Should().Be(300);
        small.Close();
    }

    // ---------------------------------------------------------------- режим слабого ПК без перезапуска

    [AvaloniaFact]
    public async Task Low_performance_toggle_applies_without_restart()
    {
        if (AppPaths.DataRootOverride is null)
            return; // тест пишет user-settings.json — только во временной папке стенда (test-safe)
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        using var scope = new LowScope();
        UserPreferences.Instance.LowPerformanceMode = false;
        LowPerformanceLive.Attach();
        var events = 0;
        void OnChanged() => events++;
        UserPreferences.LowPerformanceModeChanged += OnChanged;
        var view = new ScreenSettingsView();
        var window = new Window { Content = view };
        try
        {
            view.FindControl<ToggleButton>("PerfGroupToggle")!.IsChecked = true;
            using (UiShot.Render(window, UiSize.Small)) { }

            var applied = LowPerformanceLive.AppliedCount;
            view.LowPerformanceModeCheck.IsChecked = true;
            UiShot.Pump();

            UserPreferences.Instance.LowPerformanceMode.Should().BeTrue("переключатель действует сразу, без «Сохранить» и без перезапуска");
            events.Should().Be(1, "смена режима — одно событие");
            ModalMotion.ReduceMotion.Should().BeTrue("анимации модалок выключаются сразу");
            ModalMotion.IsBlurOn(true, UserPreferences.Instance.LowPerformanceMode).Should().BeFalse("размытие под модалками — тоже сразу");
            LowPerformanceLive.AppliedCount.Should().BeGreaterThan(applied, "тема, обои и стекло пересобраны по событию");
            var hint = Application.Current!.Resources["DialogTransparencyHint"] as List<WindowTransparencyLevel>;
            hint.Should().NotBeNull();
            hint!.Should().Equal([WindowTransparencyLevel.None]); // окна диалогов стали обычными (непрозрачными) без перезахода

            // Только программная отрисовка ждёт перезапуска: строка есть, лишь если программа запущена с другим режимом.
            var note = view.FindControl<TextBlock>("GfxLowPerfNote");
            note.Should().NotBeNull();
            RenderModeSettings.LowPerformanceRestartNeeded(true).Should().Be(RenderModeSettings.StartedLowPerformance is false);
            note!.IsVisible.Should().Be(RenderModeSettings.StartedLowPerformance is false, "«применится после перезапуска» — только для отрисовки");

            view.LowPerformanceModeCheck.IsChecked = false;
            UiShot.Pump();
            UserPreferences.Instance.LowPerformanceMode.Should().BeFalse();
            events.Should().Be(2);
            (Application.Current.Resources["DialogTransparencyHint"] as List<WindowTransparencyLevel>)!
                .Should().Equal([WindowTransparencyLevel.Transparent]); // выключили режим — прозрачные окна диалогов вернулись без перезахода
        }
        finally
        {
            UserPreferences.LowPerformanceModeChanged -= OnChanged;
            window.Close();
            UiShot.Pump();
        }
    }

    [Fact]
    public void Restart_note_only_when_started_with_a_different_render_mode()
    {
        var prop = typeof(RenderModeSettings).GetProperty(nameof(RenderModeSettings.StartedLowPerformance))!;
        var before = RenderModeSettings.StartedLowPerformance;
        try
        {
            prop.SetValue(null, null);
            RenderModeSettings.LowPerformanceRestartNeeded(true).Should().BeFalse("запуск не из Program (стенд) — перезапуск не требуется");
            prop.SetValue(null, false);
            RenderModeSettings.LowPerformanceRestartNeeded(true).Should().BeTrue("запущено с видеокартой, режим включили — отрисовка сменится после перезапуска");
            RenderModeSettings.LowPerformanceRestartNeeded(false).Should().BeFalse("вернули как при запуске");
            prop.SetValue(null, true);
            RenderModeSettings.LowPerformanceRestartNeeded(false).Should().BeTrue();
        }
        finally
        {
            prop.SetValue(null, before);
        }
    }

    [Theory]
    [InlineData(2L * 1024 * 1024 * 1024, 8, true)]   // 2 ГБ
    [InlineData(4L * 1024 * 1024 * 1024, 8, true)]   // 4 ГБ
    [InlineData(8L * 1024 * 1024 * 1024, 2, true)]   // 2 ядра
    [InlineData(8L * 1024 * 1024 * 1024, 4, false)]  // обычный ПК
    [InlineData(0L, 0, false)]                        // цифры недоступны
    public void Weak_pc_rule(long memoryBytes, int cores, bool expected)
    {
        UserPreferences.LooksLikeWeakPc(memoryBytes, cores).Should().Be(expected);
        WeakPcAdvisor.ShouldAsk(alreadyShown: false, lowModeOn: false, memoryBytes, cores).Should().Be(expected);
    }

    [Fact]
    public void Weak_pc_hint_is_asked_once_and_never_when_mode_is_already_on()
    {
        const long twoGb = 2L * 1024 * 1024 * 1024;
        WeakPcAdvisor.ShouldAsk(alreadyShown: true, lowModeOn: false, twoGb, 8).Should().BeFalse("подсказка показывалась — больше не спрашиваем");
        WeakPcAdvisor.ShouldAsk(alreadyShown: false, lowModeOn: true, twoGb, 8).Should().BeFalse("режим уже включён");
        WeakPcAdvisor.Question().Should().NotBeNullOrWhiteSpace();
    }
}
