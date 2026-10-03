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
        // Как первая строка Program.Main в Windows: режим до любых путей к данным.
        NurMarketKassa.Services.AppMode.Initialize(IsOwnerProgram ? new[] { "--owner" } : Array.Empty<string>());
        AndroidBootstrap.BeforeAvalonia(this);
        base.OnCreate(savedInstanceState);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .AfterSetup(_ => AndroidBootstrap.InstallShell(this));

    protected override void OnStop()
    {
        AndroidBootstrap.OnEnteredBackground();
        base.OnStop();
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
