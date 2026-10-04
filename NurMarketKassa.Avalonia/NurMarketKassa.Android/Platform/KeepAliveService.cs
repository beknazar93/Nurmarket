using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-05, владелец: «при сворачивании программа закрывается». Живой случай (Redmi 12, Android 15):
/// через 2 с после «Домой» системный lowmemorykiller выгрузил кассу (oom_score_adj 700, 295 МБ в памяти и
/// 211 МБ в подкачке) — свёрнутая программа без службы для Android первая на выгрузку, и открытый чек, смена,
/// неотправленные офлайн-чеки ждали нового запуска (~20 с). Пока касса открыта, она — служба переднего плана
/// со значком в шторке («Касса работает»), как кассы и навигаторы: такую программу Android при нехватке памяти
/// не выгружает. Касса и программа владельца — разные процессы, поэтому и служб две (у владельца — в ":owner").</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class KassaKeepAliveService : Service
{
    private const int NotificationId = 1017;
    private const string ChannelId = "nurmarket_running";

    protected virtual bool IsOwnerProgram => false;

    /// <summary>Запуск из активности (касса на экране — Android это разрешает). Повторный вызов безвреден.</summary>
    internal static void Start(Activity activity, bool owner)
    {
        try
        {
            var intent = new Intent(activity, owner ? typeof(OwnerKeepAliveService) : typeof(KassaKeepAliveService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                activity.StartForegroundService(intent);
            else
                activity.StartService(intent);
        }
        catch (Exception ex)
        {
            // Не запустилась — касса работает как раньше, просто без защиты от выгрузки.
            PosLogger.Log($"Android: служба «Касса работает» не запущена: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var notification = BuildNotification();
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
                StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: служба «Касса работает» не стала службой переднего плана: {ex.GetType().Name}: {ex.Message}", "WARNING");
            StopSelf();
        }

        // Процесс всё-таки закрыли — без окна кассы служба сама не нужна, не перезапускаем.
        return StartCommandResult.NotSticky;
    }

    /// <summary>Кассу смахнули из списка недавних — это «закрыть»: служба больше не держит процесс.</summary>
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        StopSelf();
        base.OnTaskRemoved(rootIntent);
    }

    private Notification BuildNotification()
    {
        var title = IsOwnerProgram ? "NurMarket Владелец" : "NurMarket Касса";
        var text = IsOwnerProgram
            ? Tr.T("Программа владельца работает — нажмите, чтобы открыть",
                "Ээсинин программасы иштеп жатат — ачуу үчүн басыңыз",
                "The owner app is running — tap to open",
                "Sahip programı çalışıyor — açmak için dokunun",
                "Egasi dasturi ishlamoqda — ochish uchun bosing")
            : Tr.T("Касса работает — нажмите, чтобы открыть",
                "Касса иштеп жатат — ачуу үчүн басыңыз",
                "The till is running — tap to open",
                "Kasa çalışıyor — açmak için dokunun",
                "Kassa ishlamoqda — ochish uchun bosing");

        if (OperatingSystem.IsAndroidVersionAtLeast(26) && GetSystemService(NotificationService) is NotificationManager manager)
        {
            var channel = new NotificationChannel(ChannelId,
                Tr.T("Работа программы", "Программанын иштеши", "App running", "Program çalışması", "Dastur ishi"),
                NotificationImportance.Low);
            channel.SetShowBadge(false);
            manager.CreateNotificationChannel(channel);
        }

        var open = new Intent(this, IsOwnerProgram ? typeof(OwnerActivity) : typeof(MainActivity))
            .SetAction(Intent.ActionMain)
            .AddCategory(Intent.CategoryLauncher)
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
        var pendingFlags = PendingIntentFlags.UpdateCurrent;
        if (OperatingSystem.IsAndroidVersionAtLeast(23))
            pendingFlags |= PendingIntentFlags.Immutable;
        var pending = PendingIntent.GetActivity(this, 0, open, pendingFlags);

#pragma warning disable CA1422, CS0618 // Builder без канала и приоритет — для Android до 8.0
        var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this).SetPriority((int)NotificationPriority.Low);
#pragma warning restore CA1422, CS0618
        builder.SetContentTitle(title)
            .SetContentText(text)
            .SetSmallIcon(NurMarketKassa.AvaloniaHost.Resource.Drawable.ic_stat_kassa)
            .SetOngoing(true)
            .SetShowWhen(false)
            .SetContentIntent(pending);
        return builder.Build()!;
    }
}

/// <summary>2026-10-05: та же служба для программы владельца — в её процессе ":owner".</summary>
[Service(Exported = false, Process = ":owner", ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class OwnerKeepAliveService : KassaKeepAliveService
{
    protected override bool IsOwnerProgram => true;
}
