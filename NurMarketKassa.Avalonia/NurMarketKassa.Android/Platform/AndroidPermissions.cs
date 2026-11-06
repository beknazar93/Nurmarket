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
    private static readonly Dictionary<int, TaskCompletionSource<PermissionResult>> Pending = new();
    // 2026-11-05 (ревью волн 0–3, А-7): когда запрос ушёл — чтобы «потерянный» (активность пересоздали, ответа нет) не блокировал код навсегда.
    private static readonly Dictionary<int, DateTime> PendingSince = new();
    private static readonly Dictionary<int, string[]> PendingPermissions = new();
    private static readonly TimeSpan PendingMaxAge = TimeSpan.FromSeconds(60);
    // 2026-11-06 (аудит №5, А5-10): Android показывает одно окно прав за раз — второй запрос, пришедший при открытом окне, получал пустой ответ → «отказ»
    // (первая печать на новом терминале: окно уведомлений ещё на экране, запрос Bluetooth сразу отклонялся). Теперь запросы идут по очереди.
    private static readonly SemaphoreSlim Queue = new(1, 1);

    /// <summary>2026-11-06 (аудит №5, А5-10): итог запроса из трёх значений. Blocked — пользователь отказал с «Больше не спрашивать» (или система не показывает окно):
    /// дальше только вручную в настройках Android — карточка «Открыть настройки».</summary>
    public enum PermissionResult { Granted, Denied, Blocked }

    public static bool IsGranted(string permission)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
            return true; // до Android 6 права даются при установке
        return AndroidBootstrap.AppContext.CheckSelfPermission(permission) == Permission.Granted;
    }

    /// <summary>Спросить права (если ещё не выданы). Завершается, когда пользователь ответил в окне Android. Запросы разных прав идут по очереди.</summary>
    public static async Task<bool> RequestAsync(int code, params string[] permissions) =>
        await RequestDetailedAsync(code, permissions).ConfigureAwait(false) == PermissionResult.Granted;

    /// <summary>То же, но с различием «отказ» и «запрещено навсегда» (<see cref="PermissionResult.Blocked"/>).</summary>
    public static async Task<PermissionResult> RequestDetailedAsync(int code, params string[] permissions)
    {
        if (permissions.All(IsGranted))
            return PermissionResult.Granted;
        var activity = AndroidBootstrap.CurrentActivity;
        if (activity is null)
            return PermissionResult.Denied;
        Task<PermissionResult>? sameCode = null;
        lock (Gate)
        {
            if (Pending.TryGetValue(code, out var existing) && DateTime.UtcNow - PendingSince.GetValueOrDefault(code, DateTime.UtcNow) < PendingMaxAge)
                sameCode = existing.Task; // окно этого запроса уже открыто — ждём тот же ответ
        }
        if (sameCode is not null)
            return await sameCode.ConfigureAwait(false);

        // очередь: пока открыто окно другого запроса, ждём (но не дольше минуты — зависший запрос очередь не держит)
        if (!await Queue.WaitAsync(PendingMaxAge).ConfigureAwait(false))
            return PermissionResult.Denied;
        try
        {
            if (permissions.All(IsGranted))
                return PermissionResult.Granted; // пока ждали очередь, предыдущий запрос выдал и это
            TaskCompletionSource<PermissionResult> tcs;
            lock (Gate)
            {
                if (Pending.TryGetValue(code, out var stale))
                    stale.TrySetResult(PermissionResult.Denied); // ответа нет больше минуты — завершаем старый запрос и спрашиваем заново
                tcs = new TaskCompletionSource<PermissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending[code] = tcs;
                PendingSince[code] = DateTime.UtcNow;
                PendingPermissions[code] = permissions;
            }
            activity.RunOnUiThread(() =>
            {
                try { activity.RequestPermissions(permissions, code); }
                catch (Exception ex)
                {
                    NurMarketKassa.Services.PosLogger.Log($"Android: запрос прав {code} — {ex.Message}", "WARNING");
                    Complete(code, PermissionResult.Denied);
                }
            });
            if (await Task.WhenAny(tcs.Task, Task.Delay(PendingMaxAge)).ConfigureAwait(false) != tcs.Task)
            {
                NurMarketKassa.Services.PosLogger.Log($"Android: запрос прав {code} — ответа нет больше {PendingMaxAge.TotalSeconds:0} с, очередь освобождена.", "WARNING");
                Complete(code, PermissionResult.Denied);
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Queue.Release();
        }
    }

    /// <summary>То же, но из фонового потока (печать): ждёт ответ не дольше <paramref name="timeout"/>.
    /// В главном потоке не ждёт (ответ приходит именно в нём) — вернёт false, а окно Android всё равно откроется.</summary>
    public static bool RequestAndWait(int code, TimeSpan timeout, params string[] permissions)
    {
        var task = RequestAsync(code, permissions);
        if (task.IsCompleted)
            return task.Result;
        if (Looper.MyLooper() == Looper.MainLooper)
        {
            // 2026-11-06: В4 А-9 — раньше в главном потоке сразу возвращалось false (окно Android открывалось, а печать отказывала «разрешите и повторите»).
            // Главный поток блокировать нельзя (ответ приходит именно в нём), но можно крутить вложенный цикл сообщений (как у диалогов кассы):
            // интерфейс живёт, ответ пользователя обрабатывается, и печать идёт дальше. Цикл выключен (android-no-nested-loop) — прежнее поведение.
            if (!NurMarketKassa.AndroidNestedLoop.Enabled)
                return false;
            using var cts = new CancellationTokenSource(timeout);
            _ = task.ContinueWith(_ =>
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { /* цикл уже закончился по времени */ }
            }, TaskScheduler.Default);
            NurMarketKassa.AndroidNestedLoop.Run(cts.Token);
            return task.IsCompletedSuccessfully && task.Result;
        }
        try { return task.Wait(timeout) && task.Result; }
        catch { return false; }
    }

    /// <summary>Вызывает MainActivity.OnRequestPermissionsResult.</summary>
    public static void OnResult(int requestCode, Permission[] grantResults)
    {
        if (grantResults.Length > 0 && grantResults.All(r => r == Permission.Granted))
        {
            Complete(requestCode, PermissionResult.Granted);
            return;
        }
        // 2026-11-06 (А5-10): отказ. «Запрещено навсегда» — если система больше не показывает пояснение хотя бы для одного отказанного права
        // (после только что показанного окна это значит «Больше не спрашивать»). Пустой ответ (окно не показано, запрос отменён) — обычный отказ.
        var result = PermissionResult.Denied;
        try
        {
            string[]? asked;
            lock (Gate)
                PendingPermissions.TryGetValue(requestCode, out asked);
            var activity = AndroidBootstrap.CurrentActivity;
            if (grantResults.Length > 0 && asked is not null && activity is not null && OperatingSystem.IsAndroidVersionAtLeast(23))
            {
                for (var i = 0; i < asked.Length && i < grantResults.Length; i++)
                    if (grantResults[i] != Permission.Granted && !activity.ShouldShowRequestPermissionRationale(asked[i]))
                        result = PermissionResult.Blocked;
            }
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"Android: итог запроса прав {requestCode} — {ex.Message}", "WARNING");
        }
        Complete(requestCode, result);
    }

    private static void Complete(int code, PermissionResult result)
    {
        TaskCompletionSource<PermissionResult>? tcs;
        lock (Gate)
        {
            Pending.Remove(code, out tcs);
            PendingSince.Remove(code);
            PendingPermissions.Remove(code);
        }
        tcs?.TrySetResult(result);
    }
}
