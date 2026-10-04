using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace NurMarketKassa.Droid;

// 2026-10-04, Android-касса. Две программы, как в Windows: касса и программа владельца —
// две иконки, два процесса (у владельца свой процесс ":owner" и своя папка данных NurMarketOwner,
// см. AppMode), чтобы они не мешали друг другу на одном аппарате.
//
// ConfigurationChanges: подключение сканера/клавиатуры по USB (keyboard), поворот, смена плотности
// не пересоздают окно — иначе открытый чек пережил бы пересоздание только чудом.

[Activity(
    Label = "NurMarket Касса",
    Theme = "@style/NurTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
                           | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density
                           | ConfigChanges.Locale | ConfigChanges.LayoutDirection | ConfigChanges.FontScale)]
[IntentFilter(new[] { "android.hardware.usb.action.USB_DEVICE_ATTACHED" })]
[MetaData("android.hardware.usb.action.USB_DEVICE_ATTACHED", Resource = "@xml/usb_device_filter")]
public class MainActivity : AvaloniaMainActivity<NurMarketKassa.AvaloniaHost.App>
{
    protected virtual bool IsOwnerProgram => false;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // 2026-10-04, «на Android программа не открывается»: любая ошибка — на экран (AndroidCrashReport),
        // а не молчаливое закрытие. Прошлый запуск упал — сначала его текст.
        AndroidCrashReport.Install(this);
        AndroidCrashReport.ShowPendingIfAny(this);
        // Как первая строка Program.Main в Windows: режим до любых путей к данным.
        NurMarketKassa.Services.AppMode.Initialize(IsOwnerProgram ? new[] { "--owner" } : Array.Empty<string>());
        AndroidBootstrap.BeforeAvalonia(this);
        base.OnCreate(savedInstanceState);
        // 2026-10-04, редизайн под любые устройства: телефон боком — поле у выреза камеры было белым (фон окна
        // Android после запуска Avalonia). Теперь тёмное, как панели состояния и навигации.
        Window?.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(Android.Graphics.Color.Black));
        RequestHighestRefreshRate();
    }

    /// <summary>2026-10-05, владелец: «плавный интерфейс хотя бы 60 Гц, по возможности максимальная частота». Экран
    /// телефона умеет 60/90 Гц (бывает 120/144), но программе без запроса Android часто оставляет 60. Просим режим
    /// с наибольшей частотой при том же разрешении; выше системной настройки «Частота обновления» Android не даст.</summary>
    private void RequestHighestRefreshRate()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(23) || Window is not { } window)
                return;
#pragma warning disable CA1422 // DefaultDisplay устарел с Android 11, но работает на всех версиях
            var display = WindowManager?.DefaultDisplay;
#pragma warning restore CA1422
            var current = display?.GetMode();
            var best = display?.GetSupportedModes()?
                .Where(m => current is null || (m.PhysicalWidth == current.PhysicalWidth && m.PhysicalHeight == current.PhysicalHeight))
                .OrderByDescending(m => m.RefreshRate)
                .FirstOrDefault();
            if (best is null)
                return;
            var attributes = window.Attributes;
            if (attributes is null)
                return;
            attributes.PreferredDisplayModeId = best.ModeId;
            attributes.PreferredRefreshRate = best.RefreshRate;
            window.Attributes = attributes;
            NurMarketKassa.Services.PosLogger.Log(
                $"Android: частота экрана — запрошено {best.RefreshRate:0} Гц (режим {best.ModeId}), сейчас {current?.RefreshRate ?? 0:0} Гц.", "UI");
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: частота экрана не изменена — {ex.Message}", "WARNING");
        }
    }

    // ---- 2026-10-05: ввод — следующим сообщением очереди, а не внутри обработки касания ----
    // Падение SIGSEGV и «зависание» на 6 с (журнал 05.10, окно «Фильтр»): синхронное окно кассы (PosDialogHost.Show →
    // AndroidNestedLoop) запускало вложенный цикл сообщений прямо внутри dispatchTouchEvent. Android ждал окончания
    // обработки касания, пока открыто окно (HANG, следующие касания с задержкой — «тротлит»), а после закрытия окна
    // внешний цикл продолжал с уже освобождённым объектом ввода — нативное падение. Теперь касание и клавиша
    // забираются у Android сразу (копия события), а обрабатываются отдельным сообщением: окна «Да/Нет» открываются
    // вне обработки ввода, как модальные окна в обычных Android-программах. Порядок событий сохраняется (одна очередь).

    private Android.OS.Handler? _inputHandler;

    private Android.OS.Handler InputHandler => _inputHandler ??= new Android.OS.Handler(Android.OS.Looper.MainLooper!);

    public override bool DispatchTouchEvent(Android.Views.MotionEvent? ev)
    {
        if (ev is null || !NurMarketKassa.AndroidNestedLoop.Enabled)
            return base.DispatchTouchEvent(ev);
        var copy = Android.Views.MotionEvent.Obtain(ev);
        if (copy is null)
            return base.DispatchTouchEvent(ev);
        InputHandler.Post(() =>
        {
            try
            {
                base.DispatchTouchEvent(copy);
            }
            finally
            {
                copy.Recycle();
            }
        });
        return true;
    }

    public override bool DispatchKeyEvent(Android.Views.KeyEvent? e)
    {
        // Громкость, питание, камера — системе как есть.
        if (e is null || !NurMarketKassa.AndroidNestedLoop.Enabled || IsSystemKey(e.KeyCode))
            return base.DispatchKeyEvent(e);
        var copy = new Android.Views.KeyEvent(e);
        InputHandler.Post(() => base.DispatchKeyEvent(copy));
        return true;
    }

    private static bool IsSystemKey(Android.Views.Keycode code) => code is Android.Views.Keycode.VolumeUp
        or Android.Views.Keycode.VolumeDown or Android.Views.Keycode.VolumeMute or Android.Views.Keycode.Power
        or Android.Views.Keycode.Camera or Android.Views.Keycode.Focus or Android.Views.Keycode.Home
        or Android.Views.Keycode.AppSwitch or Android.Views.Keycode.Headsethook or Android.Views.Keycode.MediaPlayPause;

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont()
            // 2026-10-04, «иконки исчезли»: шрифта значков Windows на Android нет — NurIcons (IconFontFallback).
            // 2026-10-04, стресс-тест: 46 окон кассы в разметке просят «Segoe UI» — на Android его нет, и
            // Avalonia брала системный шрифт (тоньше и другой ширины, чем остальная касса). Недостающий шрифт
            // теперь — Noto Sans кассы (AppFontFamily). Раньше это случайно делала поздняя подстановка ресурсов
            // окна, которую исправил TopLevel.TryGetResource.
            .With(new Avalonia.Media.FontManagerOptions
            {
                DefaultFamilyName = "avares://NurMarketKassa.Avalonia/Assets/Fonts#Noto Sans",
                FontFallbacks = NurMarketKassa.AvaloniaHost.Services.IconFontFallback.Options.FontFallbacks,
            })
            .AfterSetup(_ => AndroidBootstrap.InstallShell(this));

    /// <summary>2026-10-05, «при сворачивании программа закрывается»: пока касса на экране — служба
    /// переднего плана (KeepAliveService), свёрнутую кассу Android не выгружает.</summary>
    protected override void OnStart()
    {
        base.OnStart();
        KassaKeepAliveService.Start(this, IsOwnerProgram);
    }

    protected override void OnStop()
    {
        AndroidBootstrap.OnEnteredBackground();
        base.OnStop();
    }

    /// <summary>2026-10-05: касса свёрнута или системе мало памяти — отдаём неиспользуемую память
    /// (сборка мусора с уплотнением), чтобы свёрнутая касса занимала меньше.</summary>
    public override void OnTrimMemory(TrimMemory level)
    {
        base.OnTrimMemory(level);
        if (level < TrimMemory.UiHidden)
            return;
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Java.Lang.Runtime.GetRuntime()?.Gc();
        }
        catch
        {
            // не критично
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // 2026-10-04: разрешение на камеру для сканера штрихкодов.
        AndroidCameraScanner.OnPermissionResult(requestCode, grantResults);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        // Подключили USB-принтер — разрешение на него Android выдал вместе с этим вызовом.
        AndroidBootstrap.OnUsbDeviceAttached(intent);
    }
}

/// <summary>Программа владельца — тот же код с ключом --owner (как ярлык «NurMarket Владелец» в Windows).</summary>
[Activity(
    Label = "NurMarket Владелец",
    Theme = "@style/NurTheme.NoActionBar",
    Icon = "@drawable/icon_owner",
    MainLauncher = true,
    Exported = true,
    Process = ":owner",
    TaskAffinity = "kg.nurmarket.owner",
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
                           | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density
                           | ConfigChanges.Locale | ConfigChanges.LayoutDirection | ConfigChanges.FontScale)]
public class OwnerActivity : MainActivity
{
    protected override bool IsOwnerProgram => true;
}
