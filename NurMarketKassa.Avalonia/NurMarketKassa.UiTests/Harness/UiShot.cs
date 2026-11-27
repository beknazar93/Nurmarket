using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;

using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using SkiaSharp;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>Тема для снимка: светлая и тёмная — «Классическая», сенсорная — тема «Сенсорная» (крупные плитки и кнопки).</summary>
public enum UiTheme { Light, Dark, Touch }

/// <summary>Размер окна кассы для снимка.</summary>
public readonly record struct UiSize(int Width, int Height)
{
    public static readonly UiSize Small = new(1024, 640);
    public static readonly UiSize Large = new(1366, 768);
    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>2026-11-06 (В14): общий код сквозных тестов окон — тема и язык как в кассе, показ окна в Headless, снимок кадра, сравнение с эталоном
/// и проверка «текст не обрезан». Все вызовы — из потока интерфейса ([AvaloniaFact]).</summary>
public static class UiShot
{
    public static readonly AppLanguage[] Languages = [AppLanguage.Russian, AppLanguage.Kyrgyz];

    /// <summary>Ставит тему (светлая/тёмная/сенсорная) и язык так же, как касса: App.ApplyTheme-путь без записи настроек на диск.</summary>
    public static void Setup(UiTheme theme, AppLanguage lang)
    {
        var app = (NurMarketKassa.AvaloniaHost.App)Application.Current!;
        ResetStaticsIfNewApplication(app);
        NurMarketKassa.AvaloniaHost.Services.TariffDraft.ResetForTests(); // 2026-11-26: общий набор «Своё» (Маркетплейс ↔ диалог) — статический; каждый сценарий начинает с пустого
        var dark = theme == UiTheme.Dark;
        UserPreferences.Instance.Language = lang;
        UserPreferences.Instance.DarkTheme = dark;
        UserPreferences.Instance.CheckoutPrintReceipt = false; // сценарии оплаты меняют этот выбор — для одинакового вида окна оплаты сбрасываем
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        AccentThemeService.Apply(theme == UiTheme.Touch ? "touch" : "classic", dark);
        LocalizationManager.Apply(lang);
        Pump();
    }

    /// <summary>2026-11-20: изоляция снимков экранов от прежних тестов общего прогона. Всё это лежит во временной папке данных одного прогона и переживает тесты:
    /// state.json (касса при каждом окне пишет ширину колонок, вкладку каталога, чеки — следующее окно кассы читает; main-kassa выходил с другой шириной чека),
    /// label-template.json и barcode-print-settings.json (тесты штрихкодов оставляли размер этикетки 58×40 вместо 40×30), события смены ShiftEvents
    /// (тесты возвратов оставляли «Возвраты 625,00 сом» в окне «Продажи»). Перед снимком отменяем отложенные записи и возвращаем чистое состояние.</summary>
    public static void ResetPersistedStateForSnapshots()
    {
        NurMarketKassa.ViewModels.Main.ApplicationStateService.CancelPendingSaves();
        // 2026-11-20: выбранная вкладка «Весы» — статическое поле окна (общее для всех экземпляров): тесты весов оставляли «Весы на кассе», и снимки «Настройки → Весы»
        // в полном прогоне расходились с эталоном («Весы с этикетками» при первом заходе). Возвращаем «первый заход».
        typeof(NurMarketKassa.AvaloniaHost.Views.Settings.ScaleSettingsView)
            .GetField("_lastSection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, -1);
        // 2026-11-20: и сфера магазина: тест сфер, оставивший «Услуги», открывал снимок кассы на вкладке «Кызматтар» с пустым списком (main-kassa ky в полном прогоне).
        UserPreferences.Instance.MarketSphere = MarketSpheres.Grocery;
        UserPreferences.Instance.MarketSphereDetail = null;
        foreach (var file in new[] { "state.json", "label-template.json", "barcode-print-settings.json" })
        {
            try
            {
                File.Delete(Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, file));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        DatabaseService.Instance.WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ShiftEvents";
            command.ExecuteNonQuery();
        });
    }

    private static Application? _lastApp;

    /// <summary>Avalonia.Headless.XUnit может создать новый экземпляр приложения для следующего теста, а LocalizationManager держит в статических полях
    /// словарь языка прежнего экземпляра («The ResourceDictionary already has a parent»). Для нового экземпляра обнуляем эти поля — в программе
    /// экземпляр один, поэтому приложение не меняем.</summary>
    private static void ResetStaticsIfNewApplication(Application app)
    {
        if (ReferenceEquals(_lastApp, app))
            return;
        _lastApp = app;
        var t = typeof(LocalizationManager);
        t.GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, null);
        ((System.Collections.IDictionary?)t.GetField("Cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null))?.Clear();
        Interlocked.Increment(ref ApplicationInstances);
    }

    /// <summary>Сколько разных экземпляров приложения видели тесты (для отчёта).</summary>
    public static int ApplicationInstances;

    /// <summary>Выполняет отложенные задания интерфейса и один кадр отрисовки.</summary>
    public static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>2026-11-19: Avalonia (Gestures) после нажатия мыши заводит одноразовый DispatcherTimer, чей обработчик держит PointerPressedEventArgs, а он — окно
    /// (_rootVisual). В Headless такой таймер не успевает сработать и удерживает закрытое окно (PIN, правка накладной) — это стенд, а не утечка приложения.
    /// Здесь таймер «доигрывается»: ждём его срабатывания до ~1,5 с, а оставшиеся жестовые таймеры снимаются (Stop) — их действие уже не нужно окну, которого нет.
    /// Возвращает, сколько таймеров пришлось снять принудительно.</summary>
    public static int DrainGestureTimers()
    {
        var timersField = typeof(Dispatcher).GetField("_timers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var tickField = typeof(DispatcherTimer).GetField("Tick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (timersField?.GetValue(Dispatcher.UIThread) is not System.Collections.IList timers || tickField is null)
            return 0;
        static bool IsGesture(DispatcherTimer t, System.Reflection.FieldInfo tick)
        {
            if (tick.GetValue(t) is not EventHandler h)
                return false;
            var target = h.Target;
            var action = target?.GetType().GetField("action")?.GetValue(target) as Delegate;
            return (action?.Target?.GetType().FullName ?? "").Contains("Avalonia.Input.Gestures", StringComparison.Ordinal);
        }
        List<DispatcherTimer> Pending() => timers.Cast<DispatcherTimer>().Where(t => IsGesture(t, tickField)).ToList();
        for (var i = 0; i < 30 && Pending().Count > 0; i++)
        {
            Thread.Sleep(50);
            Pump();
        }
        var left = Pending();
        foreach (var t in left)
            t.Stop();
        return left.Count;
    }

    /// <summary>Даёт фоновым загрузкам окна (ответ «нет связи» от заглушки, подсказки, таймеры) закончиться до снимка: несколько коротких пауз с прогоном очереди интерфейса.</summary>
    public static void Settle(int rounds = 5, int pauseMs = 30)
    {
        for (var i = 0; i < rounds; i++)
        {
            Thread.Sleep(pauseMs);
            Pump();
        }
    }

    /// <summary>2026-11-21: ждёт (до 20 с, очередь интерфейса крутится), пока запуск окон кассы и загрузки каталога закончатся, и даёт доиграть последним заданиям интерфейса.
    /// Первые ~300 мс ждём в любом случае: событие Loaded окна приходит чуть позже показа, и до него «запусков в работе» ещё ноль.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> StartupWaited = new();

    public static void WaitMainWindowStartup()
    {
        var started = Environment.TickCount64;
        var until = started + 20000;
        while (Environment.TickCount64 < until)
        {
            Pump();
            var idle = NurMarketKassa.AvaloniaHost.Views.MainKassir.MainWindow.StartupsInProgress == 0 && NurMarketKassa.ViewModels.Main.CatalogPanelViewModel.ActiveLoads == 0;
            if (idle && Environment.TickCount64 - started > 300)
                break;
            Thread.Sleep(25);
        }
        Settle(3, 30);
    }

    /// <summary>Показывает окно (если задан размер — фиксирует его) и возвращает кадр.</summary>
    public static SKBitmap Render(Window window, UiSize? size = null)
    {
        if (size is { } s)
        {
            window.SizeToContent = SizeToContent.Manual;
            window.Width = s.Width;
            window.Height = s.Height;
        }
        if (!window.IsVisible)
            window.Show();
        Settle();
        // 2026-11-21 (снимок main-kassa: в кадр иногда попадала строка «Синхронизация каталога с сервером…», 0,66 % при допуске 0,4 %): окно кассы при показе само запускает вход в смену и загрузку
        // каталога, а строка состояния в это время показывает ход загрузки. Кадр снимаем, когда запуск этого окна и все загрузки каталога закончились (не по таймеру) — состояние одно и то же. Допуск не менялся.
        if (window is NurMarketKassa.AvaloniaHost.Views.MainKassir.MainWindow && StartupWaited.TryAdd(window, new object())) // один раз на окно
            WaitMainWindowStartup();
        // 2026-11-21 (прогон 1.17.91): headless иногда не успевает отдать кадр сразу после Show при нагрузке (случайно в разных тестах: «Кадр не получен») — ждём кадр до ~2 с, а не падаем на первом null.
        var captured = window.CaptureRenderedFrame();
        for (var attempt = 0; attempt < 10 && captured is null; attempt++)
        {
            Settle(2, 100);
            captured = window.CaptureRenderedFrame();
        }
        using var frame = captured ?? throw new InvalidOperationException("Кадр не получен (проверьте UseSkia и UseHeadlessDrawing=false).");
        using var ms = new MemoryStream();
        frame.Save(ms);
        ms.Position = 0;
        return SKBitmap.Decode(ms);
    }

    /// <summary>Снимок окна и сравнение с эталоном <paramref name="name"/>; возвращает текст расхождения или null.</summary>
    public static string? Shoot(Window window, string name, UiSize? size = null)
    {
        using var bmp = Render(window, size);
        return Snapshots.PngSnapshot.Compare(name, bmp);
    }

    public static string Slug(UiTheme theme, AppLanguage lang, UiSize? size = null) =>
        $"{theme.ToString().ToLowerInvariant()}-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}" + (size is { } s ? $"-{s}" : "");
}
