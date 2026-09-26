using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Win32;
using Velopack;

namespace NurMarketKassa.AvaloniaHost;

internal static class Program
{
    // Именованный мьютекс на весь компьютер — второй запуск кассы должен быть невозможен
    // (кассир случайно открывает второй ярлык поверх уже работающего, получаются два
    // процесса с двумя корзинами/сменами одновременно). Поле — иначе GC может собрать
    // и освободить мьютекс до выхода из Main, и проверка перестанет работать.
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first, before any other startup logic: on install/update/uninstall,
        // Velopack relaunches the exe with special flags to create/remove shortcuts etc.,
        // and this handles + exits on those without ever reaching the Avalonia UI.
        // Программа владельца ставится вместе с кассой (тот же exe с ключом --owner): её ярлык
        // появляется при установке и при обновлении, уходит при удалении (см. OwnerShortcuts).
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => NurMarketKassa.AvaloniaHost.Services.OwnerShortcuts.EnsureCreated(evenIfCreatedBefore: true))
            .OnAfterUpdateFastCallback(_ => NurMarketKassa.AvaloniaHost.Services.OwnerShortcuts.EnsureCreated(evenIfCreatedBefore: false))
            .OnBeforeUninstallFastCallback(_ => NurMarketKassa.AvaloniaHost.Services.OwnerShortcuts.Remove())
            .Run();

        // Касса или программа владельца — до любых путей к данным и общесистемных имён (AppMode).
        NurMarketKassa.Services.AppMode.Initialize(args);

        // У программы владельца своё имя: на одном компьютере с кассой они не должны мешать друг другу.
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            @"Global\NurMarketKassa-SingleInstance" + NurMarketKassa.Services.AppMode.InstanceSuffix, out var createdNew);
        if (!createdNew)
        {
            // Касса уже работает. Раньше здесь показывалось «Касса уже запущена, проверьте
            // панель задач» и всё — а если кассир свернул её на рабочий стол или она ушла в
            // трей, вернуть её было НЕЧЕМ: ярлык каждый раз отвечал тем же сообщением
            // (живой случай владельца 2026-09-22). Теперь второй запуск будит первый и молча
            // закрывается — для кассира ярлык просто возвращает кассу на экран.
            if (TrySignalExistingInstance())
                return;

            ShowAlreadyRunningMessage();
            return;
        }

        StartShowRequestListener();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
        }
    }

    /// <summary>Имя общесистемного сигнала «покажись». Отдельно от мьютекса: мьютекс отвечает
    /// на вопрос «кто-то уже работает?», а это — способ попросить того, кто работает, показаться.</summary>
    private static string ShowRequestEventName => @"Global\NurMarketKassa-ShowRequest" + NurMarketKassa.Services.AppMode.InstanceSuffix;

    /// <summary>Просит уже работающую кассу показать окно. false — сигнал передать не удалось
    /// (старая версия без слушателя, либо запрет на именованные объекты), тогда вызывающий
    /// показывает прежнее сообщение.</summary>
    private static bool TrySignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowRequestEventName);
            signal.Set();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Фоновый слушатель: пока касса работает, ждёт сигнала от повторного запуска и
    /// поднимает окно. Поток фоновый — он не помешает процессу завершиться.</summary>
    private static void StartShowRequestListener()
    {
        EventWaitHandle signal;
        try
        {
            signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        }
        catch (Exception)
        {
            return; // без слушателя просто останется старое поведение с сообщением
        }

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    signal.WaitOne();
                    Avalonia.Threading.Dispatcher.UIThread.Post(RestoreMainWindow);
                }
                catch (Exception)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "NurMarketKassa-ShowRequest",
        };
        thread.Start();
    }

    /// <summary>Возвращает окно кассы на экран: разворачивает свёрнутое, показывает скрытое
    /// и выводит поверх остальных окон.</summary>
    private static void RestoreMainWindow()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                return;
            }

            // MainWindow на старте — заставка, потом главное окно; если по какой-то причине
            // оно не выставлено, берём последнее открытое.
            var window = desktop.MainWindow;
            if (window is null)
            {
                var windows = desktop.Windows;
                window = windows.Count > 0 ? windows[windows.Count - 1] : null;
            }

            if (window is null)
                return;

            if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
                window.WindowState = Avalonia.Controls.WindowState.Normal;

            window.Show();
            window.Activate();

            // Avalonia.Activate() не всегда перетаскивает окно поверх чужих — Windows
            // ограничивает смену активного окна для процесса, который сейчас не на переднем
            // плане. Подстраховываемся прямым вызовом Win32.
            if (window.TryGetPlatformHandle()?.Handle is { } handle && handle != IntPtr.Zero)
            {
                ShowWindow(handle, SW_RESTORE);
                SetForegroundWindow(handle);
            }
        }
        catch (Exception)
        {
            // Не смогли показать окно — это не повод ронять работающую кассу.
        }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Прямой Win32 MessageBox — Avalonia на этом этапе ещё не инициализирована.</summary>
    private static void ShowAlreadyRunningMessage() =>
        MessageBox(
            IntPtr.Zero,
            "Касса уже запущена. Проверьте панель задач или системный трей.",
            "NurMarket Kassa",
            0x00000030 /* MB_ICONWARNING */);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        if (IsLowPerformanceModeEnabled())
        {
            // Программный рендер вместо GPU-ускорения: медленнее на сложных сценах,
            // зато не зависит от старых/проблемных видеодрайверов на слабых моноблоках.
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software },
            });
        }

        return builder;
    }

    /// <summary>
    /// Читает флаг "LowPerformanceMode" из user-settings.json напрямую, в обход
    /// полной инициализации DI/UserPreferences — на этом этапе хост ещё не запущен,
    /// а режим рендеринга нужно выбрать до старта Avalonia.
    /// </summary>
    private static bool IsLowPerformanceModeEnabled()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                NurMarketKassa.Services.AppMode.DataFolderName, "user-settings.json");
            if (!File.Exists(path))
                return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("LowPerformanceMode", out var prop)
                && prop.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}
