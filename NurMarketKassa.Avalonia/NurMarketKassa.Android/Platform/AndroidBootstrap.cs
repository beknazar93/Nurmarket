using Android.App;
using Android.Content;
using Avalonia.Controls.ApplicationLifetimes;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-04: запуск кассы на Android — всё, что в Windows делают Program.Main и
/// «настольное» время жизни Avalonia.</summary>
internal static class AndroidBootstrap
{
    /// <summary>Текущая активность (для разрешений, USB, второго экрана).</summary>
    public static Activity? CurrentActivity { get; private set; }

    public static Context AppContext => Android.App.Application.Context;

    private static bool _platformInstalled;

    /// <summary>До запуска Avalonia: платформенные замены Windows-функций.</summary>
    public static void BeforeAvalonia(Activity activity)
    {
        CurrentActivity = activity;
        if (_platformInstalled)
            return;
        _platformInstalled = true;

        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch { /* уже зарегистрирован */ }

        // Host.CreateDefaultBuilder (App.axaml.cs) берёт папку содержимого из текущей папки и следит
        // за appsettings.json. На Android текущая папка — «/», следить за ней нельзя: ставим папку
        // программы и выключаем слежение (настройки на Android всё равно не меняются на ходу).
        try
        {
            if (activity.FilesDir?.AbsolutePath is { Length: > 0 } filesDir)
                Directory.SetCurrentDirectory(filesDir);
        }
        catch { /* останется как есть */ }
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

        // Звук (сигналы, голосовые подсказки) — через MediaPlayer Android.
        NurMarketKassa.AvaloniaHost.Portable.PortablePlatform.WavPlayer = AndroidSound.PlayWav;
        // Печать чека: USB host, Bluetooth, сеть (TCP 9100) — см. AndroidPrinterTransport.
        PrinterPortService.PlatformTransport = new AndroidPrinterTransport();
        AndroidPlatformHooks.FinishApplication = code =>
        {
            try { CurrentActivity?.FinishAffinity(); } catch { /* активность уже закрыта */ }
            Java.Lang.JavaSystem.Exit(code);
        };
        AndroidPlatformHooks.MoveToBackground = () =>
        {
            try { CurrentActivity?.MoveTaskToBack(true); } catch { /* активность уже закрыта */ }
        };

        // Запасной выключатель вложенного цикла синхронных диалогов (AndroidNestedLoop): если на
        // каком-то аппарате он поведёт себя плохо, достаточно создать пустой файл
        // «android-no-nested-loop» в папке данных программы — диалоги станут «показать и не ждать».
        try
        {
            var flag = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppMode.DataFolderName, "android-no-nested-loop");
            if (File.Exists(flag))
            {
                AndroidNestedLoop.Enabled = false;
                PosLogger.Log("Android: вложенный цикл диалогов выключен файлом android-no-nested-loop.", "WARNING");
            }
        }
        catch { /* нет папки данных — оставляем по умолчанию */ }
        PosLogger.Log($"Android: запуск {(AppMode.IsOwner ? "программы владельца" : "кассы")}, " +
                      $"Android {Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt}), " +
                      $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model}.", "INFO");
    }

    /// <summary>После настройки Avalonia и до App.OnFrameworkInitializationCompleted: вид программы
    /// — стопка окон кассы, время жизни — «настольное» для кода кассы.</summary>
    public static void InstallShell(Activity activity)
    {
        var app = Avalonia.Application.Current
                  ?? throw new InvalidOperationException("Avalonia не запущена.");
        if (app.ApplicationLifetime is AndroidDesktopLifetime)
            return;
        if (app.ApplicationLifetime is not ISingleViewApplicationLifetime single)
            throw new InvalidOperationException("Android: неожиданное время жизни приложения " + app.ApplicationLifetime?.GetType().Name);

        var host = new WindowLayerHost();
        var lifetime = new AndroidDesktopLifetime(single, host);
        app.ApplicationLifetime = lifetime;
        single.MainView = host;
        AndroidCustomerDisplay.Initialize(activity);
    }

    public static void OnEnteredBackground() => AndroidDesktopLifetime.Instance?.OnEnteredBackground();

    public static void OnUsbDeviceAttached(Intent? intent) => AndroidPrinterTransport.OnDeviceAttached(intent);
}
