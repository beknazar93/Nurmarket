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

    /// <summary>2026-10-04: телефон или планшет (экран до 11") — да; стационарный кассовый терминал (CaravPOS и
    /// т. п., 15") — нет. По физической диагонали; если аппарат сообщает неверную плотность (бывает у дешёвых
    /// терминалов) — по наименьшей стороне экрана в точках.</summary>
    private static bool DetectHandheld(Activity activity)
    {
        try
        {
            var metrics = new Android.Util.DisplayMetrics();
#pragma warning disable CA1422, CS0618 // GetRealMetrics устарел с Android 11, но работает на всех версиях от 7.0
            activity.WindowManager?.DefaultDisplay?.GetRealMetrics(metrics);
#pragma warning restore CA1422, CS0618
            var widthInches = metrics.WidthPixels / (double)metrics.Xdpi;
            var heightInches = metrics.HeightPixels / (double)metrics.Ydpi;
            var inches = Math.Sqrt(widthInches * widthInches + heightInches * heightInches);
            var smallestDp = Math.Min(metrics.WidthPixels, metrics.HeightPixels) / (double)Math.Max(0.5f, metrics.Density);
            var handheld = inches is > 3 and < 40 ? inches < 11.0 : smallestDp < 720;
            PosLogger.Log(
                $"Android: экран {inches:0.0}\" (наименьшая сторона {smallestDp:0} точек) — " +
                (handheld ? "телефон/планшет: каталог и чек отдельными экранами." : "кассовый терминал: каталог и чек рядом."),
                "INFO");
            return handheld;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>До запуска Avalonia: платформенные замены Windows-функций.</summary>
    public static void BeforeAvalonia(Activity activity)
    {
        CurrentActivity = activity;
        if (_platformInstalled)
            return;
        _platformInstalled = true;

        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
        catch { /* уже зарегистрирован */ }

        // 2026-10-04: журнал кассы — ещё и в журнал Android (adb logcat -s NurMarket), см. PosLogger.Mirror.
        PosLogger.Mirror = (category, message) =>
        {
            var text = $"[{category}] {message}";
            if (category.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || category.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
                Android.Util.Log.Error("NurMarket", text);
            else if (category.Contains("WARN", StringComparison.OrdinalIgnoreCase))
                Android.Util.Log.Warn("NurMarket", text);
            else
                Android.Util.Log.Info("NurMarket", text);
        };

        // 2026-10-04, владелец: «раздели каталог и корзину для всех мобильных устройств» — телефон/планшет
        // или кассовый терминал (DeviceForm).
        NurMarketKassa.AvaloniaHost.Services.DeviceForm.IsHandheld = DetectHandheld(activity);
        // 2026-10-05, владелец: «при смене страницы каталога жёстко тормозит» — на телефоне видно ~6 плиток,
        // а страница создавала все 50. Телефон/планшет — 20 товаров на страницу, терминал — как было (50).
        if (NurMarketKassa.AvaloniaHost.Services.DeviceForm.IsHandheld)
            NurMarketKassa.ViewModels.Main.CatalogPanelViewModel.PageSizeOverride = 20;
        // 2026-10-05: «Скопировать информацию об устройстве» (окно «Удалённая поддержка») — сведения Android.
        NurMarketKassa.AvaloniaHost.Services.DeviceInfoReport.PlatformDetails = AndroidDeviceInfo.Collect;

        // 2026-10-04: время жизни кассы — до RegisterServices, иначе Avalonia бросает исключение (см. InstallLifetime).
        NurMarketKassa.AvaloniaHost.App.BeforeRegisterServices = InstallLifetime;

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
        // 2026-10-04: камера аппарата как сканер штрихкодов (CameraScan, кнопка «камера» в чеке, карточке товара, приёмке).
        AndroidCameraScanner.Install();
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

    /// <summary>2026-10-04: до Application.RegisterServices (App.BeforeRegisterServices) — время жизни
    /// «настольное» для кода кассы. Позже Avalonia менять его не даёт (живое падение на телефоне:
    /// «It's not possible to change ApplicationLifetime after Application was initialized»).</summary>
    public static void InstallLifetime(Avalonia.Application app)
    {
        if (app.ApplicationLifetime is AndroidDesktopLifetime)
            return;
        if (app.ApplicationLifetime is not ISingleViewApplicationLifetime single)
            throw new InvalidOperationException("Android: неожиданное время жизни приложения " + app.ApplicationLifetime?.GetType().Name);
        app.ApplicationLifetime = new AndroidDesktopLifetime(single);
    }

    /// <summary>После настройки Avalonia и до App.OnFrameworkInitializationCompleted: вид программы
    /// — стопка окон кассы (время жизни уже поставлено в <see cref="InstallLifetime"/>).</summary>
    public static void InstallShell(Activity activity)
    {
        var app = Avalonia.Application.Current
                  ?? throw new InvalidOperationException("Avalonia не запущена.");
        if (app.ApplicationLifetime is not AndroidDesktopLifetime lifetime)
            throw new InvalidOperationException("Android: время жизни кассы не поставлено, а стоит " + app.ApplicationLifetime?.GetType().Name);
        if (lifetime.Host is not null)
            return;

        var host = new WindowLayerHost();
        lifetime.AttachHost(host);
        // 2026-10-04: вид кассы — подогнанный под экран аппарата (ScreenFitHost).
        lifetime.MainView = new ScreenFitHost(host);
        AndroidCustomerDisplay.Initialize(activity);
    }

    public static void OnEnteredBackground() => AndroidDesktopLifetime.Instance?.OnEnteredBackground();

    public static void OnUsbDeviceAttached(Intent? intent) => AndroidPrinterTransport.OnDeviceAttached(intent);
}
