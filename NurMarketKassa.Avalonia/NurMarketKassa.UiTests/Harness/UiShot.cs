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
        var dark = theme == UiTheme.Dark;
        UserPreferences.Instance.Language = lang;
        UserPreferences.Instance.DarkTheme = dark;
        UserPreferences.Instance.CheckoutPrintReceipt = false; // сценарии оплаты меняют этот выбор — для одинакового вида окна оплаты сбрасываем
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        AccentThemeService.Apply(theme == UiTheme.Touch ? "touch" : "classic", dark);
        LocalizationManager.Apply(lang);
        Pump();
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

    /// <summary>Даёт фоновым загрузкам окна (ответ «нет связи» от заглушки, подсказки, таймеры) закончиться до снимка: несколько коротких пауз с прогоном очереди интерфейса.</summary>
    public static void Settle(int rounds = 5, int pauseMs = 30)
    {
        for (var i = 0; i < rounds; i++)
        {
            Thread.Sleep(pauseMs);
            Pump();
        }
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
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Кадр не получен (проверьте UseSkia и UseHeadlessDrawing=false).");
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
