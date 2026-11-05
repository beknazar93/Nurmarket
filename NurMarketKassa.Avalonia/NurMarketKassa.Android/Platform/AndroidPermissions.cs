using Android.App;
using Android.Content.PM;
using Android.OS;

namespace NurMarketKassa.Droid;

/// <summary>2026-11-05 (аудит №4, А4-13): один помощник запросов прав Android и таблица кодов. Раньше камера держала
/// свой код 4711, Bluetooth взял 4712 (а ТЗ ИИ-звонка отдавало 4712 микрофону), результат шёл только камере.
/// Теперь каждый запрос — Task, который завершается ответом пользователя (MainActivity.OnRequestPermissionsResult).</summary>
internal static class AndroidPermissions
{
    /// <summary>Камера-сканер штрихкодов.</summary>
    public const int Camera = 4711;
    /// <summary>Bluetooth-принтер (CONNECT + SCAN).</summary>
    public const int Bluetooth = 4712;
    /// <summary>Микрофон (ИИ-звонок, голосовой вопрос) — по ТЗ 03.11.</summary>
    public const int Microphone = 4713;
    /// <summary>Камера ИИ-звонка (видео) — по ТЗ 03.11.</summary>
    public const int CallCamera = 4714;
    /// <summary>Уведомления (Android 13+).</summary>
    public const int Notifications = 4715;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, TaskCompletionSource<bool>> Pending = new();
    // 2026-11-05 (ревью волн 0–3, А-7): когда запрос ушёл — чтобы «потерянный» (активность пересоздали, ответа нет) не блокировал код навсегда.
    private static readonly Dictionary<int, DateTime> PendingSince = new();
    private static readonly TimeSpan PendingMaxAge = TimeSpan.FromSeconds(60);

    public static bool IsGranted(string permission)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
            return true; // до Android 6 права даются при установке
        return AndroidBootstrap.AppContext.CheckSelfPermission(permission) == Permission.Granted;
    }

    /// <summary>Спросить права (если ещё не выданы). Завершается, когда пользователь ответил в окне Android.</summary>
    public static Task<bool> RequestAsync(int code, params string[] permissions)
    {
        if (permissions.All(IsGranted))
            return Task.FromResult(true);
        var activity = AndroidBootstrap.CurrentActivity;
        if (activity is null)
            return Task.FromResult(false);
        TaskCompletionSource<bool> tcs;
        lock (Gate)
        {
            if (Pending.TryGetValue(code, out var existing))
            {
                if (DateTime.UtcNow - PendingSince.GetValueOrDefault(code, DateTime.UtcNow) < PendingMaxAge)
                    return existing.Task; // окно уже открыто — ждём тот же ответ
                existing.TrySetResult(false); // ответа нет больше минуты — завершаем старый запрос и спрашиваем заново
            }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending[code] = tcs;
            PendingSince[code] = DateTime.UtcNow;
        }
        activity.RunOnUiThread(() =>
        {
            try { activity.RequestPermissions(permissions, code); }
            catch (Exception ex)
            {
                NurMarketKassa.Services.PosLogger.Log($"Android: запрос прав {code} — {ex.Message}", "WARNING");
                Complete(code, false);
            }
        });
        return tcs.Task;
    }

    /// <summary>То же, но из фонового потока (печать): ждёт ответ не дольше <paramref name="timeout"/>.
    /// В главном потоке не ждёт (ответ приходит именно в нём) — вернёт false, а окно Android всё равно откроется.</summary>
    public static bool RequestAndWait(int code, TimeSpan timeout, params string[] permissions)
    {
        var task = RequestAsync(code, permissions);
        if (task.IsCompleted)
            return task.Result;
        if (Looper.MyLooper() == Looper.MainLooper)
            return false;
        try { return task.Wait(timeout) && task.Result; }
        catch { return false; }
    }

    /// <summary>Вызывает MainActivity.OnRequestPermissionsResult.</summary>
    public static void OnResult(int requestCode, Permission[] grantResults) =>
        Complete(requestCode, grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted));

    private static void Complete(int code, bool granted)
    {
        TaskCompletionSource<bool>? tcs;
        lock (Gate)
        {
            Pending.Remove(code, out tcs);
            PendingSince.Remove(code);
        }
        tcs?.TrySetResult(granted);
    }
}
