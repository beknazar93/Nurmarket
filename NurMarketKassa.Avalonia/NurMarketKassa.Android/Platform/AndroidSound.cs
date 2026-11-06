using Android.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.Droid;

/// <summary>2026-10-04, Android-касса: проигрывание WAV (сигналы кассы, голосовые подсказки) —
/// то, что в Windows делает System.Media.SoundPlayer. Подставляется в PortablePlatform.WavPlayer.</summary>
internal static class AndroidSound
{
    // ---- 2026-11-06: В4 А4-15 — короткие сигналы (писк сканера, ошибка) через SoundPool: WAV записывается на диск один раз (по хэшу),
    // загружается один раз, дальше проигрывается без файла и MediaPlayer.Prepare (раньше: файл + новый MediaPlayer на каждый сигнал, +20–80 мс).
    // Длинные записи (голосовые подсказки) и всё, что не PCM-WAV или SoundPool не принял, идут прежним путём.
    private const int ShortLimitBytes = 128 * 1024;

    private sealed class PoolEntry
    {
        public int Id;
        public bool Ready;
        public bool PlayWhenReady;
    }

    private static readonly object PoolGate = new();
    private static readonly Dictionary<string, PoolEntry> PoolEntries = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, PoolEntry> PoolById = new();
    private static SoundPool? _pool;

    private static SoundPool? Pool()
    {
        if (_pool is not null)
            return _pool;
        try
        {
            var attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Sonification)!
                .Build();
            var pool = new SoundPool.Builder().SetMaxStreams(4)!.SetAudioAttributes(attributes)!.Build()!;
            pool.LoadComplete += (_, e) =>
            {
                lock (PoolGate)
                {
                    if (!PoolById.TryGetValue(e.SampleId, out var entry))
                        return;
                    entry.Ready = e.Status == 0;
                    if (entry.Ready && entry.PlayWhenReady)
                        pool.Play(entry.Id, 1f, 1f, 1, 0, 1f);
                    entry.PlayWhenReady = false;
                }
            };
            _pool = pool;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: SoundPool недоступен — {ex.Message}", "WARNING");
        }
        return _pool;
    }

    /// <summary>Миллисекунды звучания PCM-WAV по заголовку; -1 — это не простой WAV (тогда SoundPool не используем).</summary>
    internal static int WavDurationMs(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' || wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
            return -1;
        var byteRate = BitConverter.ToInt32(wav, 28);
        return byteRate <= 0 ? -1 : (int)((wav.Length - 44L) * 1000 / byteRate);
    }

    private static bool TryPlayShort(byte[] wav)
    {
        if (wav.Length > ShortLimitBytes || WavDurationMs(wav) < 0 || Pool() is not { } pool)
            return false;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(wav));
        lock (PoolGate)
        {
            if (PoolEntries.TryGetValue(key, out var entry))
            {
                if (entry.Ready)
                    pool.Play(entry.Id, 1f, 1f, 1, 0, 1f);
                else
                    entry.PlayWhenReady = true; // ещё грузится — сыграет по окончании загрузки
                return true;
            }
            var folder = Path.Combine(Path.GetTempPath(), "NurCRM-sounds");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, key + ".wav");
            if (!File.Exists(path))
                File.WriteAllBytes(path, wav);
            var id = pool.Load(path, 1);
            if (id == 0)
                return false;
            var created = new PoolEntry { Id = id, PlayWhenReady = true };
            PoolEntries[key] = created;
            PoolById[id] = created;
            return true;
        }
    }

    public static void PlayWav(byte[] wav)
    {
        try
        {
            if (TryPlayShort(wav))
            {
                // PlaySync в Windows ждёт конца звука (так кассовые сценарии не накладывают сигналы); на главном потоке ждать нельзя.
                if (!NurMarketKassa.AndroidNestedLoop.OnMainThread)
                    Thread.Sleep(Math.Clamp(WavDurationMs(wav), 0, 3000));
                return;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Android: сигнал через SoundPool не удался ({ex.Message}) — обычный проигрыватель.", "WARNING");
        }
        PlayWavWithMediaPlayer(wav);
    }

    private static void PlayWavWithMediaPlayer(byte[] wav)
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
