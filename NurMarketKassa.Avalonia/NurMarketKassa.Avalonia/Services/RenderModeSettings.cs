using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-11 (решение владельца, «после сворачивания остаётся пол-окна» на некоторых видеокартах): настройка «Режим отрисовки» (только Windows).
/// «Авто» — как было (Avalonia: AngleEgl + WinUIComposition/DirectComposition), «Совместимый» — без DirectComposition (RedirectionSurface),
/// «Программный» — без видеокарты. Хранится в user-settings.json (UserPreferences.WindowsRenderMode, локально), читается в Program ДО старта окна
/// и действует со следующего запуска. Сопоставление с Win32PlatformOptions — RenderModeWin32 (на Android сборки Avalonia.Win32 нет).
///
/// Аварийный откат: при запуске с нестандартным режимом ставится маркер «render-mode-trial» (%LOCALAPPDATA%\{папка программы}). Он снимается, когда
/// главное окно (не заставка) провисело на экране 10 секунд или программа штатно закрылась после показа главного окна. Если при следующем запуске
/// маркер на месте, а режим всё ещё нестандартный — прошлый запуск упал или завис до окна: этот запуск идёт в «Авто», в журнал — ERROR, после показа
/// главного окна настройка записывается «Авто», маркер снимается и показывается «Режим отрисовки сброшен после сбоя».</summary>
public static class RenderModeSettings
{
    public const string Auto = "auto";
    public const string Compatible = "compat";
    public const string Software = "software";

    public const string TrialMarkerName = "render-mode-trial";

    /// <summary>Сколько главное окно должно провисеть на экране, чтобы нестандартный режим считался рабочим.</summary>
    public static readonly TimeSpan SuccessDelay = TimeSpan.FromSeconds(10);

    /// <summary>Неизвестное и пустое — «Авто».</summary>
    public static string Normalize(string? mode)
    {
        var m = mode?.Trim().ToLowerInvariant();
        return m is Compatible or Software ? m : Auto;
    }

    /// <summary>Пункт настроек есть только в Windows (на Linux/Android режим выбирает своя платформа).</summary>
    public static bool IsAvailable(bool isWindows, bool isAndroid) => isWindows && !isAndroid;

    public static bool IsAvailableHere => IsAvailable(OperatingSystem.IsWindows(), OperatingSystem.IsAndroid());

    /// <summary>Режим из текста user-settings.json (поле WindowsRenderMode); битый текст — «Авто».</summary>
    public static string ReadModeFromSettingsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Auto;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(nameof(UserPreferences.WindowsRenderMode), out var prop)
                   && prop.ValueKind == JsonValueKind.String
                ? Normalize(prop.GetString())
                : Auto;
        }
        catch (JsonException)
        {
            return Auto;
        }
    }

    /// <summary>Решение при запуске (чистая функция).</summary>
    /// <param name="Chosen">Режим из настроек.</param>
    /// <param name="Effective">С каким режимом стартуем.</param>
    /// <param name="RolledBack">Прошлый запуск с нестандартным режимом не дошёл до окна — этот идёт в «Авто».</param>
    /// <param name="StartTrial">Ставить маркер проверки (нестандартный режим и не «слабое устройство», где и так программная отрисовка).</param>
    public sealed record StartupDecision(string Chosen, string Effective, bool RolledBack, bool StartTrial);

    public static StartupDecision Decide(string? chosen, bool lowPerformance, bool trialMarkerExists)
    {
        var mode = Normalize(chosen);
        if (trialMarkerExists && mode != Auto)
            return new StartupDecision(mode, Auto, RolledBack: true, StartTrial: false);
        return new StartupDecision(mode, mode, RolledBack: false, StartTrial: mode != Auto && !lowPerformance);
    }

    public static string SettingsFilePath => Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "user-settings.json");

    public static string TrialMarkerPath => Path.Combine(AppPaths.Local, AppMode.DataFolderName, TrialMarkerName);

    /// <summary>Решение этого запуска (null — не Windows или Program не вызывал <see cref="PrepareAtStartup"/>: стенды, тесты).</summary>
    public static StartupDecision? Current { get; private set; }

    /// <summary>С каким режимом строится окно (Program.BuildAvaloniaApp).</summary>
    public static string EffectiveMode => Current?.Effective ?? Auto;

    /// <summary>Подпись режима для журнала и настроек.</summary>
    public static string Title(string mode) => Normalize(mode) switch
    {
        Compatible => Tr.T("Совместимый", "Шайкеш", "Compatible", "Uyumlu", "Mos"),
        Software => Tr.T("Программный", "Программалык", "Software", "Yazılımsal", "Dasturiy"),
        _ => Tr.T("Авто", "Авто", "Auto", "Otomatik", "Avto"),
    };

    private static string LogName(string mode) => Normalize(mode) switch
    {
        Compatible => "Совместимый (RedirectionSurface, без DirectComposition)",
        Software => "Программный (без видеокарты)",
        _ => "Авто (AngleEgl + WinUIComposition/DirectComposition)",
    };

    /// <summary>Program.Main, до построения AppBuilder: решение по настройке и маркеру, маркер, строка в журнал.</summary>
    public static void PrepareAtStartup(bool lowPerformance) =>
        Current = PrepareAt(SettingsFilePath, TrialMarkerPath, lowPerformance, IsAvailableHere);

    /// <summary>То же с явными путями (тесты: «прошлый запуск упал» — маркер оставлен).</summary>
    public static StartupDecision? PrepareAt(string settingsPath, string markerPath, bool lowPerformance, bool available)
    {
        if (!available)
            return null;
        try
        {
            var chosen = File.Exists(settingsPath) ? ReadModeFromSettingsJson(File.ReadAllText(settingsPath)) : Auto;
            var markerExists = File.Exists(markerPath);
            var d = Decide(chosen, lowPerformance, markerExists);
            if (d.RolledBack)
            {
                PosLogger.Log($"Режим отрисовки «{LogName(d.Chosen)}» сброшен на «Авто»: прошлый запуск с ним не дошёл до главного окна (сбой или зависание).", "ERROR");
            }
            else if (markerExists)
            {
                File.Delete(markerPath); // остаток прошлой проверки, режим уже «Авто»
            }
            if (d.StartTrial)
            {
                var dir = Path.GetDirectoryName(markerPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(markerPath, d.Effective + " " + DateTime.Now.ToString("s"));
            }
            PosLogger.Log($"Режим отрисовки Windows: {LogName(d.Effective)}"
                          + (lowPerformance ? "; включён «режим слабого устройства» — программная отрисовка" : "")
                          + (d.StartTrial ? "; проверка запуска (маркер снимется после показа главного окна)." : "."), "INFO");
            return d;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Режим отрисовки: не удалось прочитать настройку или маркер ({ex.Message}) — «Авто».", "WARNING");
            return new StartupDecision(Auto, Auto, false, false);
        }
    }

    /// <summary>Главное окно (не заставка) показано и не свёрнуто.</summary>
    public static bool IsMainWindowShown(Window? w) =>
        w is { IsVisible: true } && w.GetType().Name != "SplashWindow" && w.WindowState != WindowState.Minimized;

    private static bool _completed;

    /// <summary>App: после показа заставки. Следит за главным окном: проверка пройдена (10 с) — маркер снят; откат — настройка «Авто» и уведомление.</summary>
    public static void WatchStartup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Current is not { } d || (!d.StartTrial && !d.RolledBack))
            return;
        var markerPath = TrialMarkerPath;
        var shownSince = DateTime.MinValue;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_completed)
            {
                timer.Stop();
                return;
            }
            if (shownSince == DateTime.MinValue)
            {
                if (!IsMainWindowShown(desktop.MainWindow))
                    return;
                shownSince = DateTime.UtcNow;
                if (d.RolledBack)
                {
                    timer.Stop();
                    CompleteRollback(markerPath);
                }
                return;
            }
            if (DateTime.UtcNow - shownSince >= SuccessDelay)
            {
                timer.Stop();
                CompleteTrial(markerPath, d.Effective);
            }
        };
        timer.Start();
        // Штатное закрытие после показа главного окна — режим рабочий (кассир просто закрыл программу раньше 10 с).
        desktop.Exit += (_, _) =>
        {
            if (!_completed && d.StartTrial && shownSince != DateTime.MinValue)
                CompleteTrial(markerPath, d.Effective);
        };
    }

    /// <summary>Проверка пройдена: маркер снят.</summary>
    public static void CompleteTrial(string markerPath, string mode)
    {
        _completed = true;
        DeleteMarker(markerPath);
        PosLogger.Log($"Режим отрисовки «{LogName(mode)}»: главное окно показано — проверка запуска пройдена.", "INFO");
    }

    /// <summary>Откат: настройка «Авто» записана, маркер снят, уведомление.</summary>
    public static void CompleteRollback(string markerPath)
    {
        _completed = true;
        try
        {
            UserPreferences.Instance.WindowsRenderMode = Auto;
            UserPreferences.Instance.SaveToDisk();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Режим отрисовки: настройка «Авто» не записана ({ex.Message}).", "WARNING");
        }
        DeleteMarker(markerPath);
        PosLogger.Log("Режим отрисовки сброшен на «Авто» после сбоя — настройка записана.", "WARNING");
        try
        {
            App.GetRequiredService<IUserPrompts>().ShowWarning(RollbackNotice);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Режим отрисовки: уведомление не показано ({ex.Message}).", "WARNING");
        }
    }

    public static string RollbackNotice => Tr.T(
        "Режим отрисовки сброшен после сбоя: прошлый запуск с выбранным режимом не открыл окно. Включён режим «Авто» (Настройки → Экран → Производительность).",
        "Сүрөт тартуу режими катадан кийин баштапкыга кайтарылды: тандалган режим менен мурунку ишке киргизүү терезени ачкан жок. «Авто» режими күйгүзүлдү (Жөндөөлөр → Экран → Өндүрүмдүүлүк).",
        "The rendering mode was reset after a failure: the last start with the chosen mode did not open the window. “Auto” is on now (Settings → Screen → Performance).",
        "Çizim modu bir hatadan sonra sıfırlandı: seçilen modla son başlatma pencereyi açmadı. “Otomatik” mod açıldı (Ayarlar → Ekran → Performans).",
        "Chizish rejimi nosozlikdan keyin tiklandi: tanlangan rejim bilan oxirgi ishga tushirish oynani ochmadi. «Avto» rejimi yoqildi (Sozlamalar → Ekran → Unumdorlik).");

    private static void DeleteMarker(string markerPath)
    {
        try
        {
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Режим отрисовки: маркер проверки не удалён ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Только для тестов: сброс состояния запуска.</summary>
    public static void ResetForTests(StartupDecision? current = null)
    {
        Current = current;
        _completed = false;
    }
}
