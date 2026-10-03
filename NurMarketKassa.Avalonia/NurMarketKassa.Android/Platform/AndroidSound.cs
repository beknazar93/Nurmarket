using Android.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-04, Android-касса: проигрывание WAV (сигналы кассы, голосовые подсказки) —
/// то, что в Windows делает System.Media.SoundPlayer. Подставляется в PortablePlatform.WavPlayer.</summary>
internal static class AndroidSound
{
    public static void PlayWav(byte[] wav)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nurmarket-sound-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);
        using var done = new ManualResetEventSlim(false);
        MediaPlayer? player = null;
        try
        {
            player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Notification)!
                .SetContentType(AudioContentType.Sonification)!
                .Build());
            player.SetDataSource(path);
            player.Completion += (_, _) => done.Set();
            player.Error += (_, _) => done.Set();
            player.Prepare();
            player.Start();
            // PlaySync в Windows ждёт конца звука; на главном потоке ждать нельзя (интерфейс замрёт).
            if (Android.OS.Looper.MainLooper is not { IsCurrentThread: true })
                done.Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: звук не воспроизведён — {ex.Message}", "WARNING");
        }
        finally
        {
            if (Android.OS.Looper.MainLooper is not { IsCurrentThread: true })
            {
                try { player?.Release(); } catch { /* уже освобождён */ }
                try { File.Delete(path); } catch { /* временный файл */ }
            }
            else if (player is not null)
            {
                var p = player;
                p.Completion += (_, _) =>
                {
                    try { p.Release(); } catch { /* уже освобождён */ }
                    try { File.Delete(path); } catch { /* временный файл */ }
                };
            }
        }
    }
}
