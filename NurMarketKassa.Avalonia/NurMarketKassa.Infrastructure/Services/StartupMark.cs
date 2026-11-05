using System.Diagnostics;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (аудит скорости, 20.1): метка STARTUP в журнале — сколько прошло от запуска процесса до первого окна кассира.
/// Пишется один раз за запуск; нужна для сравнения «до/после» следующих шагов ускорения старта.</summary>
public static class StartupMark
{
    private static int _done;

    public static void FirstWindowShown(string what)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0)
            return;
        try
        {
            using var proc = Process.GetCurrentProcess();
            var seconds = (DateTime.Now - proc.StartTime).TotalSeconds;
            PosLogger.Log(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"до первого окна кассира ({what}): {seconds:0.0} с от запуска процесса"), "STARTUP");
        }
        catch
        {
            // метка — только для замеров
        }
    }
}
