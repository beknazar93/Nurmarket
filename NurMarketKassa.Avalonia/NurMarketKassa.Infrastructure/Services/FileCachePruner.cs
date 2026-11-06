namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (аудит скорости, шаг 21; аудит №5 — диск без предела): чистка папок-кэшей по размеру и по числу файлов.
/// Миниатюры товаров лежат в %LOCALAPPDATA% и не должны расти без конца (предел ≈ 300 МБ), отчёты об ошибках — не больше нескольких десятков.
/// Удаляются самые старые файлы (по времени записи); ошибки удаления (файл занят) не бросаются — файл останется до следующего раза.</summary>
public static class FileCachePruner
{
    /// <summary>Предел папки миниатюр товаров, байт.</summary>
    public const long ThumbCacheLimitBytes = 300L * 1024 * 1024;

    /// <summary>Удалить самые старые файлы папки (включая вложенные), пока размер не станет ≤ <paramref name="targetFraction"/> × <paramref name="maxBytes"/>.
    /// Если размер уже в пределах — ничего не делает. Временные <c>*.tmp</c> старше часа удаляются всегда.</summary>
    public static (int Deleted, long FreedBytes) PruneBySize(string dir, long maxBytes, double targetFraction = 0.9)
    {
        var deleted = 0;
        long freed = 0;
        try
        {
            if (!Directory.Exists(dir))
                return (0, 0);

            var files = new List<(string Path, long Size, DateTime Time)>();
            long total = 0;
            var staleTmp = DateTime.UtcNow.AddHours(-1);
            foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && fi.LastWriteTimeUtc < staleTmp)
                    {
                        var size = fi.Length;
                        fi.Delete();
                        deleted++;
                        freed += size;
                        continue;
                    }
                    files.Add((path, fi.Length, fi.LastWriteTimeUtc));
                    total += fi.Length;
                }
                catch
                {
                    // файл исчез или занят — пропускаем
                }
            }

            if (total <= maxBytes)
                return (deleted, freed);

            var target = (long)(maxBytes * Math.Clamp(targetFraction, 0.1, 1.0));
            foreach (var f in files.OrderBy(x => x.Time))
            {
                if (total <= target)
                    break;
                try
                {
                    File.Delete(f.Path);
                    total -= f.Size;
                    freed += f.Size;
                    deleted++;
                }
                catch
                {
                    // занят — останется до следующей чистки
                }
            }
        }
        catch
        {
            // чистка кэша — не критичная работа
        }
        return (deleted, freed);
    }

    /// <summary>Оставить в папке не больше <paramref name="keep"/> самых новых файлов по маске, остальные удалить.</summary>
    public static int PruneByCount(string dir, string searchPattern, int keep)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(dir) || keep < 0)
                return 0;
            var files = Directory.EnumerateFiles(dir, searchPattern)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(keep)
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); deleted++; }
                catch { /* занят — останется */ }
            }
        }
        catch
        {
            // не критично
        }
        return deleted;
    }

    /// <summary>Перенести файлы из старой папки в новую (без перезаписи уже существующих) и убрать пустую старую. Возвращает число перенесённых.</summary>
    public static int MoveAll(string fromDir, string toDir)
    {
        var moved = 0;
        try
        {
            if (!Directory.Exists(fromDir) || string.Equals(Path.GetFullPath(fromDir), Path.GetFullPath(toDir), StringComparison.OrdinalIgnoreCase))
                return 0;
            foreach (var src in Directory.EnumerateFiles(fromDir, "*", SearchOption.AllDirectories).ToList())
            {
                try
                {
                    var rel = Path.GetRelativePath(fromDir, src);
                    var dst = Path.Combine(toDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (File.Exists(dst))
                        File.Delete(src);
                    else
                    {
                        File.Move(src, dst);
                        moved++;
                    }
                }
                catch
                {
                    // файл занят — перенесётся при следующем запуске
                }
            }
            try { Directory.Delete(fromDir, recursive: true); } catch { /* остались занятые файлы */ }
        }
        catch
        {
            // перенос кэша — не критичная работа
        }
        return moved;
    }
}
