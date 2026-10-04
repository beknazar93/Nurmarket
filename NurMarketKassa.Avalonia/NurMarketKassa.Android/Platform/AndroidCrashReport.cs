using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-04, владелец: «при установке на Android программа не открывается». Причину без
/// аппарата не узнать: журнал кассы лежит во внутренней папке приложения, а Android просто закрывает
/// программу. Теперь любая необработанная ошибка записывается в файл и сразу показывается отдельным
/// экраном (свой процесс «:crash» — он живёт, даже когда касса падает): текст ошибки, модель аппарата,
/// версия Android, процессор. Кнопки «Отправить» (Телеграм, WhatsApp…), «Скопировать», «Закрыть».
/// Если экран не успел открыться — он откроется при следующем запуске кассы.</summary>
internal static class AndroidCrashReport
{
    private const string FileName = "last-crash.txt";
    private static bool _installed;
    private static int _reported;

    /// <summary>Самым первым делом в OnCreate — до Avalonia и кода кассы.</summary>
    public static void Install(Activity activity)
    {
        if (_installed)
            return;
        _installed = true;
        var ctx = activity.ApplicationContext ?? activity;
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Report(ctx, e.Exception, "UI");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(ctx, e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Не роняет программу — только в журнал.
            try { NurMarketKassa.Services.PosLogger.Log($"Android: необработанная ошибка задачи: {e.Exception}", "ERROR"); }
            catch { /* журнал недоступен */ }
        };
        // Ошибки самого Android (Java): записать и показать, затем — прежний обработчик (он закрывает программу).
        Java.Lang.Thread.DefaultUncaughtExceptionHandler =
            new JavaCrashHandler(ctx, Java.Lang.Thread.DefaultUncaughtExceptionHandler);
    }

    private sealed class JavaCrashHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
    {
        private readonly Context _ctx;
        private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _previous;

        public JavaCrashHandler(Context ctx, Java.Lang.Thread.IUncaughtExceptionHandler? previous)
        {
            _ctx = ctx;
            _previous = previous;
        }

        public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
        {
            Report(_ctx, new Exception($"Java ({t.Name}): {e}\n{Android.Util.Log.GetStackTraceString(e)}"), "Java");
            _previous?.UncaughtException(t, e);
        }
    }

    /// <summary>Прошлый запуск упал, а экран ошибки не открылся — показать сейчас.</summary>
    public static void ShowPendingIfAny(Activity activity)
    {
        try
        {
            var path = PathFor(activity);
            if (!File.Exists(path))
                return;
            StartViewer(activity, File.ReadAllText(path));
        }
        catch { /* показать не вышло — касса запускается как обычно */ }
    }

    private static void Report(Context ctx, Exception? ex, string source)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1)
            return;
        string text;
        try { text = Describe(ex, source); }
        catch { text = $"Источник: {source}\n\n{ex}"; }
        try { File.WriteAllText(PathFor(ctx), text); } catch { /* нет места — покажем без файла */ }
        try { NurMarketKassa.Services.PosLogger.Log("Android: программа упала: " + text, "ERROR"); } catch { /* журнал недоступен */ }
        try { StartViewer(ctx, text); } catch { /* откроется при следующем запуске */ }
    }

    private static void StartViewer(Context ctx, string text)
    {
        var intent = new Intent(ctx, typeof(CrashActivity));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        intent.PutExtra("text", text);
        ctx.StartActivity(intent);
    }

    internal static string PathFor(Context ctx) =>
        Path.Combine(ctx.FilesDir?.AbsolutePath ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), FileName);

    private static string Describe(Exception? ex, string source)
    {
        string version;
        try
        {
            var ctx = Android.App.Application.Context;
            version = ctx.PackageManager?.GetPackageInfo(ctx.PackageName ?? "", 0)?.VersionName ?? "?";
        }
        catch { version = "?"; }

        return $"NurMarket {version} ({(NurMarketKassa.Services.AppMode.IsOwner ? "владелец" : "касса")}), {DateTime.Now:dd.MM.yyyy HH:mm:ss}\n"
               + $"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), {Build.Manufacturer} {Build.Model}\n"
               + $"Процессор: {string.Join(", ", Build.SupportedAbis ?? Array.Empty<string>())}; 64-бит: {System.Environment.Is64BitProcess}\n"
               + $"Источник: {source}\n\n"
               + (ex?.ToString() ?? "(ошибка без описания)");
    }

    /// <summary>Подписи экрана ошибки на языке аппарата (ru, ky, en, tr, uz): настройки кассы в упавшем
    /// процессе могут быть не прочитаны, поэтому не через Tr.T.</summary>
    internal static string T(string ru, string ky, string en, string tr, string uz) =>
        (Java.Util.Locale.Default.Language ?? "ru") switch
        {
            "ky" => ky,
            "en" => en,
            "tr" => tr,
            "uz" => uz,
            _ => ru,
        };
}

/// <summary>Экран ошибки — обычный Android, без Avalonia и кода кассы (они могли и упасть).</summary>
[Activity(
    Label = "NurMarket",
    Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar",
    Process = ":crash",
    Exported = false,
    ExcludeFromRecents = true,
    LaunchMode = Android.Content.PM.LaunchMode.SingleTask)]
public class CrashActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var text = Intent?.GetStringExtra("text") ?? "";
        var T = AndroidCrashReport.T;

        var pad = (int)(16 * (Resources?.DisplayMetrics?.Density ?? 1));
        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(pad, pad, pad, pad);

        var title = new TextView(this)
        {
            Text = T("Программа закрылась из-за ошибки",
                "Программа ката менен жабылды",
                "The program closed because of an error",
                "Program bir hata nedeniyle kapandı",
                "Dastur xato tufayli yopildi"),
            TextSize = 20,
        };
        title.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
        root.AddView(title);

        var hint = new TextView(this)
        {
            Text = T("Нажмите «Отправить» и пришлите этот текст в поддержку NurMarket — по нему найдём причину.",
                "«Жөнөтүү» баскычын басып, бул текстти NurMarket колдоосуна жибериңиз — ал боюнча себебин табабыз.",
                "Tap “Send” and send this text to NurMarket support — it will show us the cause.",
                "«Gönder»e dokunun ve bu metni NurMarket desteğine gönderin — nedeni bununla bulacağız.",
                "«Yuborish»ni bosing va bu matnni NurMarket qo'llab-quvvatlash xizmatiga yuboring — sababini shu orqali topamiz."),
            TextSize = 15,
        };
        hint.SetPadding(0, pad / 2, 0, pad / 2);
        root.AddView(hint);

        var body = new TextView(this) { Text = text, TextSize = 12 };
        body.SetTextIsSelectable(true);
        body.SetTypeface(Android.Graphics.Typeface.Monospace, Android.Graphics.TypefaceStyle.Normal);
        var scroll = new ScrollView(this);
        scroll.AddView(body);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var buttons = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        Android.Widget.Button Add(string caption, Action onClick)
        {
            var b = new Android.Widget.Button(this) { Text = caption };
            b.Click += (_, _) => onClick();
            buttons.AddView(b, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            return b;
        }

        Add(T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"), () =>
        {
            var send = new Intent(Intent.ActionSend);
            send.SetType("text/plain");
            send.PutExtra(Intent.ExtraText, text);
            StartActivity(Intent.CreateChooser(send, "NurMarket"));
        });
        Add(T("Скопировать", "Көчүрүү", "Copy", "Kopyala", "Nusxalash"), () =>
        {
            if (GetSystemService(ClipboardService) is ClipboardManager clip)
                clip.PrimaryClip = ClipData.NewPlainText("NurMarket", text);
            Toast.MakeText(this, T("Скопировано", "Көчүрүлдү", "Copied", "Kopyalandı", "Nusxalandi"), ToastLength.Short)?.Show();
        });
        Add(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), () =>
        {
            try { File.Delete(AndroidCrashReport.PathFor(this)); } catch { /* уже нет */ }
            FinishAndRemoveTask();
        });
        root.AddView(buttons);

        SetContentView(root);
    }
}
