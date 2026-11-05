using Android.Content;
using Android.Net.Wifi;

namespace NurMarketKassa.Droid;

/// <summary>2026-11-05 (аудит №4, А4-10): Wi-Fi Android без MulticastLock отбрасывает широковещательные пакеты, поэтому
/// Android-касса не видела соседние кассы (обмен остатками по сети, LanSyncService). Блокировка берётся при запуске поиска
/// соседей и отпускается через 5 минут после ухода программы в фон (экономия батареи); возврат на экран берёт её снова.
/// Право CHANGE_WIFI_MULTICAST_STATE в манифесте есть.</summary>
internal static class AndroidMulticast
{
    private static readonly object Gate = new();
    private static WifiManager.MulticastLock? _lock;
    private static CancellationTokenSource? _release;
    private static bool _wanted; // поиск соседей запускался — блокировка нужна, пока касса на экране

    public static void Install() =>
        NurMarketKassa.Services.Lan.LanSyncService.BeforeDiscovery = Acquire;

    public static void Acquire()
    {
        lock (Gate)
        {
            _release?.Cancel();
            _release = null;
            _wanted = true;
            try
            {
                if (_lock is { IsHeld: true })
                    return;
                var wifi = AndroidBootstrap.AppContext.GetSystemService(Context.WifiService) as WifiManager;
                _lock ??= wifi?.CreateMulticastLock("nur-lan");
                _lock?.SetReferenceCounted(false);
                _lock?.Acquire();
                NurMarketKassa.Services.PosLogger.Log("Android: MulticastLock взят (поиск соседних касс по Wi-Fi).", "LAN");
            }
            catch (Exception ex)
            {
                NurMarketKassa.Services.PosLogger.Log($"Android: MulticastLock не взят — {ex.Message}", "LAN");
            }
        }
    }

    /// <summary>Касса ушла в фон: отпустить блокировку через 5 минут, если не вернулись.</summary>
    public static void ReleaseLater()
    {
        lock (Gate)
        {
            if (_lock is not { IsHeld: true })
                return;
            _release?.Cancel();
            var cts = _release = new CancellationTokenSource();
            _ = Task.Delay(TimeSpan.FromMinutes(5), cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled)
                    return;
                lock (Gate)
                {
                    try { if (_lock is { IsHeld: true }) _lock.Release(); }
                    catch { /* уже отпущена */ }
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Касса вернулась на экран: поиск соседей идёт дальше — блокировка нужна снова.</summary>
    public static void Resume()
    {
        bool wanted;
        lock (Gate)
        {
            _release?.Cancel();
            _release = null;
            wanted = _wanted;
        }
        if (wanted)
            Acquire();
    }
}
