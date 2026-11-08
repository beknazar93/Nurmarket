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
    Label = "NurCRM Касса",
    Theme = "@style/NurTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation
                           | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density
                           | ConfigChanges.Locale | ConfigChanges.LayoutDirection | ConfigChanges.FontScale
                           // 2026-11-05 (аудит №3 А3-13): смена цвета/шрифта системы, SIM, сенсорного экрана не пересоздаёт окно
                           | ConfigChanges.ColorMode | ConfigChanges.FontWeightAdjustment | ConfigChanges.Mcc | ConfigChanges.Mnc
                           | ConfigChanges.Touchscreen)]
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
        // 2026-11-06: В4 А4-20 — ориентация экрана по выбору кассира (авто / только альбомная / только книжная).
        AndroidOrientation.Apply(this);
        // 2026-11-06: В4 А5-10 — право на уведомления спрашиваем после входа, а не в OnStart (иначе окно прав перебивало вход и первый запрос Bluetooth).
        AndroidPlatformHooks.AfterSignIn = () => RunOnUiThread(AskNotificationsOnce);
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
        NurMarketKassa.Services.SyncCenter.NoteInput(); // 2026-10-30 (оптимизация, Б1): время последнего касания — основа «простоя» на Android
        if (ev is null || !NurMarketKassa.AndroidNestedLoop.Enabled)
            return DispatchTouchWithGuardReset(ev);
        var copy = Android.Views.MotionEvent.Obtain(ev);
        if (copy is null)
            return DispatchTouchWithGuardReset(ev);
        InputHandler.Post(() =>
        {
            try
            {
                DispatchTouchWithGuardReset(copy);
            }
            finally
            {
                copy.Recycle();
            }
        });
        return true;
    }

    /// <summary>2026-11-08, владелец: «сенсор откликается очень плохо». Отменённое системой касание (ACTION_CANCEL — ладонь, жест
    /// «назад», шторка) Avalonia не отпускает, и TouchGuard до 5 с считал каждое новое касание «вторым пальцем» — нажатия глушились.
    /// Новое касание (ACTION_DOWN) у Android всегда первое на экране, после ACTION_CANCEL пальцев нет — в эти моменты сбрасываем
    /// TouchGuard. Сброс — в той же очереди, что и само касание, поэтому порядок событий не меняется.</summary>
    private bool DispatchTouchWithGuardReset(Android.Views.MotionEvent? ev)
    {
        var action = ev?.ActionMasked;
        if (action == Android.Views.MotionEventActions.Down)
            NurMarketKassa.AvaloniaHost.Services.TouchGuard.ResetFromPlatform();
        var handled = base.DispatchTouchEvent(ev);
        if (action == Android.Views.MotionEventActions.Cancel)
            NurMarketKassa.AvaloniaHost.Services.TouchGuard.ResetFromPlatform();
        return handled;
    }

    public override bool DispatchKeyEvent(Android.Views.KeyEvent? e)
    {
        NurMarketKassa.Services.SyncCenter.NoteInput(); // 2026-10-30: клавиша/сканер — тоже ввод
        // Громкость, питание, камера — системе как есть.
        // 2026-11-05 (аудит №4, А4-03): HID-сканер режет начало кода — пауза между цифрами считалась по времени ОБРАБОТКИ.
        // Теперь на время обработки клавиши кладём время самого события (AndroidKeyClock), как GetMessageTime в Windows.
        if (e is null || !NurMarketKassa.AndroidNestedLoop.Enabled || IsSystemKey(e.KeyCode))
            return e is null ? base.DispatchKeyEvent(e) : AndroidKeyClock.Dispatch(e, k => base.DispatchKeyEvent(k));
        var copy = new Android.Views.KeyEvent(e);
        InputHandler.Post(() => AndroidKeyClock.Dispatch(copy, k => base.DispatchKeyEvent(k)));
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
        // 2026-10-30 (оптимизация кассы, Б1): на экране — обычные интервалы опроса сервера; считаем это вводом, чтобы после долгого фона
        // опрос сразу стал частым (подсказка FeedKick будит циклы).
        NurMarketKassa.Services.SyncCenter.AppInBackground = false;
        NurMarketKassa.Services.SyncCenter.WindowMinimized = false;
        NurMarketKassa.Services.SyncCenter.NoteInput();
        NurMarketKassa.Services.SyncCenter.FeedKick.Kick();
        AndroidMulticast.Resume(); // 2026-11-05 (А4-10)
        KassaKeepAliveService.Start(this, IsOwnerProgram);
        // 2026-11-05 (аудит №4, А4-19): экран не гаснет, пока открыта смена (кассовая программа; у владельца — как обычно).
        if (!IsOwnerProgram)
        {
            NurMarketKassa.Services.ShiftService.ShiftStateChanged -= OnShiftStateChanged;
            NurMarketKassa.Services.ShiftService.ShiftStateChanged += OnShiftStateChanged;
            ApplyKeepScreenOn(NurMarketKassa.Services.ShiftService.IsShiftOpen);
        }
        // 2026-11-06: В4 А5-10 — прежде спрашивали здесь, на экране входа. Теперь — после входа (AndroidPlatformHooks.AfterSignIn);
        // если касса вернулась из фона уже после входа — спросим сразу.
        if (AndroidDesktopLifetime.Instance?.IsSignedIn == true)
            AskNotificationsOnce();
    }

    private void OnShiftStateChanged(bool open) => RunOnUiThread(() => ApplyKeepScreenOn(open));

    private void ApplyKeepScreenOn(bool keep)
    {
        try
        {
            if (keep)
                Window?.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
            else
                Window?.ClearFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
        }
        catch
        {
            // не критично
        }
    }

    /// <summary>2026-11-05 (аудит №3 А3-11, №4 А4-30): право на уведомления (Android 13+) — спросить один раз, иначе значок
    /// службы «касса работает» скрыт. Отказ не повторяем: дальше — только вручную в настройках Android.</summary>
    private void AskNotificationsOnce()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)
                || AndroidPermissions.IsGranted(Android.Manifest.Permission.PostNotifications))
                return;
            var prefs = GetSharedPreferences("nurmarket_permissions", FileCreationMode.Private);
            if (prefs?.GetBoolean("notifications_asked", false) == true)
                return;
            prefs?.Edit()?.PutBoolean("notifications_asked", true)?.Apply();
            _ = AndroidPermissions.RequestAsync(AndroidPermissions.Notifications, Android.Manifest.Permission.PostNotifications);
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: запрос уведомлений — {ex.Message}", "WARNING");
        }
    }

    protected override void OnStop()
    {
        // 2026-10-30 (Б1): касса в фоне живёт как служба переднего плана — опросы ленты/пульса/смены/настроек редкие (60 с и реже), LAN 15 с.
        NurMarketKassa.Services.SyncCenter.AppInBackground = true;
        NurMarketKassa.Services.SyncCenter.WindowMinimized = true;
        NurMarketKassa.Services.ShiftService.ShiftStateChanged -= OnShiftStateChanged;
        ApplyKeepScreenOn(false);
        AndroidBootstrap.OnEnteredBackground();
        base.OnStop();
    }

    /// <summary>2026-10-05: касса свёрнута или системе мало памяти — отдаём неиспользуемую память
    /// (сборка мусора с уплотнением), чтобы свёрнутая касса занимала меньше.</summary>
    public override void OnTrimMemory(TrimMemory level)
    {
        base.OnTrimMemory(level);
        // 2026-11-06: В4 А4-18 — касса на экране, а памяти мало (RunningLow / RunningCritical) или уходит в фон: сбрасываем кэш картинок
        // каталога (оставляем несколько последних плиток — они на экране); пересоздаются по мере прокрутки.
        if (level >= TrimMemory.RunningLow)
        {
            try { NurMarketKassa.AvaloniaHost.Converters.AssetPathToBitmapConverter.TrimThumbs(level >= TrimMemory.RunningCritical ? 12 : 48); }
            catch (Exception ex) { NurMarketKassa.Services.PosLogger.Log($"Android: кэш картинок не сброшен — {ex.Message}", "WARNING"); }
        }
        // 2026-11-05 (аудит №4, А4-18): раньше уровни «касса на экране, памяти мало» (RunningLow/RunningCritical) пропускались,
        // а при уходе в фон шла блокирующая сборка с уплотнением на главном потоке — касса «замирала» при быстром возврате.
        // Теперь: на экране при нехватке — обычная сборка без уплотнения; в фоне — то же в фоновом потоке.
        if (level < TrimMemory.RunningLow)
            return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
            catch
            {
                // не критично
            }
        });
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // 2026-11-05 (А4-13): все ответы на запросы прав — через общий помощник (камера, Bluetooth, микрофон, уведомления).
        AndroidPermissions.OnResult(requestCode, grantResults);
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
    Label = "NurCRM Владелец",
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
                           | ConfigChanges.Locale | ConfigChanges.LayoutDirection | ConfigChanges.FontScale
                           // 2026-11-05 (аудит №3 А3-13): смена цвета/шрифта системы, SIM, сенсорного экрана не пересоздаёт окно
                           | ConfigChanges.ColorMode | ConfigChanges.FontWeightAdjustment | ConfigChanges.Mcc | ConfigChanges.Mnc
                           | ConfigChanges.Touchscreen)]
public class OwnerActivity : MainActivity
{
    protected override bool IsOwnerProgram => true;
}
