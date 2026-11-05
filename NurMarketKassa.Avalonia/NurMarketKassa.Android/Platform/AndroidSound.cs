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
        var failed = false;
        try
        {
            player = new MediaPlayer();
            player.SetAudioAttributes(new AudioAttributes.Builder()
                // 2026-11-05 (А3-10): Notification глушится режимом «Не беспокоить» — сигналы кассы пропадали; Media слышно всегда
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Sonification)!
                .Build());
            player.SetDataSource(path);
            player.Completion += (_, _) => done.Set();
            player.Error += (_, _) => done.Set();
            player.Prepare();
            player.Start();
            // PlaySync в Windows ждёт конца звука; на главном потоке ждать нельзя (интерфейс замрёт).
            if (!NurMarketKassa.AndroidNestedLoop.OnMainThread)
                done.Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            failed = true;
            PosLogger.Log($"Android: звук не воспроизведён — {ex.Message}", "WARNING");
        }
        finally
        {
            // 2026-11-05 (А3-10): ошибка (например, Prepare) — завершения не будет, освобождаем сразу, в том числе на главном потоке
            if (failed || !NurMarketKassa.AndroidNestedLoop.OnMainThread)
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
