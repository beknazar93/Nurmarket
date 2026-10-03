using System.Diagnostics;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Portable;

/// <summary>2026-10-04: то, что в Windows-кассе делает сама Windows, а на Linux и Android — каждая
/// платформа по-своему. Запускающий проект (Linux или Android) подставляет свои обработчики при старте;
/// без них работают обычные для Linux способы.</summary>
public static class PortablePlatform
{
    /// <summary>Android подставляет свой проигрыватель WAV.</summary>
    public static Action<byte[]>? WavPlayer { get; set; }

    public static void PlayWav(byte[] wav)
    {
        if (WavPlayer is { } player)
        {
            player(wav);
            return;
        }

        if (!OperatingSystem.IsLinux())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"nurmarket-sound-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);
        try
        {
            // paplay — PulseAudio/PipeWire (обычный рабочий стол), aplay — ALSA (минимальные системы).
            foreach (var exe in new[] { "paplay", "aplay" })
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(exe, $"\"{path}\"")
                    {
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                    });
                    if (p is null)
                        continue;
                    p.WaitForExit(15000);
                    if (p.ExitCode == 0)
                        return;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // программы нет — пробуем следующую
                }
            }
            PosLogger.Log("Звук: нет paplay/aplay — сигнал не воспроизведён.", "WARNING");
        }
        finally
        {
            try { File.Delete(path); } catch { /* временный файл */ }
        }
    }
}
