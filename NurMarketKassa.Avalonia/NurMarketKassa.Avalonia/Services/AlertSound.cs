using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-02, владелец: «оповещение прям со звуком должно быть в админке». Короткий сигнал
/// «динь-дон» (две ноты, ~0,7 с) — синтезируется в память как WAV, без файлов и без системных звуков
/// Windows (их могут выключить в схеме звуков). Играет в фоне, как VoicePromptPlayer.</summary>
public static class AlertSound
{
    private static byte[]? _chime;

    public static void PlayChime()
    {
        Task.Run(() =>
        {
            try
            {
                _chime ??= BuildChime();
                using var buffer = new MemoryStream(_chime);
                using var player = new SoundPlayer(buffer);
                player.PlaySync();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Звук оповещения не воспроизведён: {ex.Message}", "WARNING");
            }
        });
    }

    /// <summary>Две затухающие ноты: ми (659 Гц) и до (523 Гц) — с обертоном, чтобы звучало как колокольчик.</summary>
    private static byte[] BuildChime()
    {
        const int rate = 44100;
        var notes = new[] { (Freq: 659.25, Start: 0.0, Len: 0.45), (Freq: 523.25, Start: 0.22, Len: 0.55) };
        var total = (int)(rate * 0.8);
        var samples = new double[total];
        foreach (var (freq, start, len) in notes)
        {
            var from = (int)(start * rate);
            var count = (int)(len * rate);
            for (var i = 0; i < count && from + i < total; i++)
            {
                var t = (double)i / rate;
                var attack = Math.Min(1.0, t / 0.008);
                var env = attack * Math.Exp(-t * 6.0);
                samples[from + i] += env * (Math.Sin(2 * Math.PI * freq * t) + 0.35 * Math.Sin(2 * Math.PI * freq * 2 * t));
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = total * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var v in samples)
            w.Write((short)Math.Clamp(v * 0.42 * short.MaxValue, short.MinValue, short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
