using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05 (аудит №3, Р3-01 / В4-05): память программы владельца росла с 0,4 до 2,9–3,65 ГБ за часы, причина не найдена.
/// Диагностика для следующего замера (в журнал, без тяжёлых действий): раз в 5 минут — куча, частная память, дескрипторы, потоки, число окон,
/// кэш картинок поиска фото; при превышении 1 ГБ (не чаще раза в час) — ещё и размер живых объектов после сборки; и по каждому разделу —
/// «до / через 8 с после» открытия и закрытия (<see cref="LogAround"/>), чтобы видно было, какой раздел держит гигабайты.</summary>
public static class MemoryDiagnostics
{
    private const long OverBytes = 1024L * 1024 * 1024;
    private static Timer? _timer;
    private static DateTime _lastHeavyUtc = DateTime.MinValue;

    public static void Start()
    {
        if (_timer is not null)
            return;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    private static string Mb(long bytes) => (bytes / (1024 * 1024)).ToString();

    /// <summary>Одна строка: куча (до сборки), частная память, дескрипторы, потоки.</summary>
    public static string Snapshot()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var info = GC.GetGCMemoryInfo();
            return $"куча {Mb(GC.GetTotalMemory(false))} МБ (LOH {Mb(info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes : 0)} МБ, сборок gen2: {GC.CollectionCount(2)}), "
                   + $"частная {Mb(process.PrivateMemorySize64)} МБ, дескрипторы {process.HandleCount}, потоки {process.Threads.Count}";
        }
        catch (Exception ex)
        {
            return "память не прочитана: " + ex.Message;
        }
    }

    private static async Task TickAsync()
    {
        try
        {
            var windows = -1;
            try
            {
                windows = await Dispatcher.UIThread.InvokeAsync(() =>
                    Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d ? d.Windows.Count : -1);
            }
            catch (Exception)
            {
                // окон не посчитали — остальное пишем
            }

            var (photos, photoBytes) = ProductPhotoFinder.RecentImagesStats();
            PosLogger.Log($"ПАМЯТЬ: {Snapshot()}; окон {windows}; кэш картинок поиска фото {photos} шт. / {Mb(photoBytes)} МБ.", "INFO");

            using var process = Process.GetCurrentProcess();
            if ((process.PrivateMemorySize64 > OverBytes || GC.GetTotalMemory(false) > OverBytes) && DateTime.UtcNow - _lastHeavyUtc > TimeSpan.FromHours(1))
            {
                _lastHeavyUtc = DateTime.UtcNow;
                var before = GC.GetTotalMemory(false);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                PosLogger.Log($"ПАМЯТЬ >1 ГБ: после сборки живых объектов {Mb(GC.GetTotalMemory(false))} МБ (было {Mb(before)}), {Snapshot()}; окон {windows}.", "WARNING");
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ПАМЯТЬ: замер не удался ({ex.Message}).", "DEBUG");
        }
    }

    /// <summary>Что-то открыли/закрыли (раздел): строка «до» сразу и «через 8 с» с разницей — по ней видно, кто растит память.</summary>
    public static void LogAround(string what)
    {
        try
        {
            var heap0 = GC.GetTotalMemory(false);
            long private0;
            using (var process = Process.GetCurrentProcess())
                private0 = process.PrivateMemorySize64;
            PosLogger.Log($"ПАМЯТЬ до «{what}»: {Snapshot()}.", "INFO");
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                using var process = Process.GetCurrentProcess();
                PosLogger.Log($"ПАМЯТЬ через 8 с после «{what}»: {Snapshot()}; изменение: куча {(GC.GetTotalMemory(false) - heap0) / (1024 * 1024):+0;-0;0} МБ, частная {(process.PrivateMemorySize64 - private0) / (1024 * 1024):+0;-0;0} МБ.", "INFO");
            });
        }
        catch (Exception)
        {
            // диагностика не должна мешать работе
        }
    }
}
