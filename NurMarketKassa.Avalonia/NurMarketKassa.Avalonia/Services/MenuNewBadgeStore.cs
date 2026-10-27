using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27, аудит Н-18: значки «Новое» в меню ☰ стояли навсегда. Теперь пункт, который кассир уже открыл, больше не помечается:
/// ключи открытых пунктов — в маленьком текстовом файле рядом с настройками (в базу и настройки не пишем).</summary>
public static class MenuNewBadgeStore
{
    private static readonly object Gate = new();
    private static HashSet<string>? _seen;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "menu-badges-seen.txt");

    public static bool IsSeen(string key)
    {
        lock (Gate)
            return Load().Contains(key);
    }

    public static void MarkSeen(string key)
    {
        try
        {
            lock (Gate)
            {
                var seen = Load();
                if (!seen.Add(key))
                    return;
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, seen.OrderBy(k => k, StringComparer.Ordinal));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Меню: не удалось запомнить открытый пункт «{key}»: {ex.Message}", "WARNING");
        }
    }

    private static HashSet<string> Load()
    {
        if (_seen is not null)
            return _seen;
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath))
                    if (line.Trim() is { Length: > 0 } key)
                        set.Add(key);
        }
        catch
        {
            // файл не читается — значки покажутся снова, это не страшно
        }
        return _seen = set;
    }
}
