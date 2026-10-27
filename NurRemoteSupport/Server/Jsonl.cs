using System.Text;

namespace NurRemoteSupport.Server;

/// <summary>
/// 2026-10-27 (С-7): помощники для файлов .jsonl (журнал сеансов и заметки): ротация по месяцам и чтение с конца.
/// Текущий файл (journal.jsonl) не меняет имя; когда наступает новый месяц, прошлый уходит в journal-2026-10.jsonl.
/// </summary>
internal static class Jsonl
{
    /// <summary>Если файл последний раз писали в прошлом месяце — переносит его в архив «имя-ГГГГ-ММ.расширение». Вызывать под блокировкой записи.</summary>
    public static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return;
            var written = fi.LastWriteTimeUtc;
            var now = DateTime.UtcNow;
            if (written.Year == now.Year && written.Month == now.Month) return;
            var archive = ArchivePath(path, written.Year, written.Month);
            if (File.Exists(archive))
            {
                // Архив за этот месяц уже есть (сервер перезапускали) — дописываем.
                using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var dst = new FileStream(archive, FileMode.Append, FileAccess.Write, FileShare.Read))
                    src.CopyTo(dst);
                File.Delete(path);
            }
            else
            {
                File.Move(path, archive);
            }
        }
        catch (IOException) { /* не вышло сейчас — повторим при следующей записи */ }
    }

    private static string ArchivePath(string path, int year, int month)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}-{year:D4}-{month:D2}{Path.GetExtension(path)}");
    }

    /// <summary>Архивы файла от новых к старым.</summary>
    public static IEnumerable<string> Archives(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var name = Path.GetFileNameWithoutExtension(path);
        return Directory.EnumerateFiles(dir, $"{name}-????-??{Path.GetExtension(path)}")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Текущий файл и затем его архивы, от новых строк к старым — читаются только нужные куски с конца.</summary>
    public static IEnumerable<string> ReadAllNewestFirst(string path)
    {
        foreach (var l in ReadLinesReverse(path)) yield return l;
        foreach (var a in Archives(path))
            foreach (var l in ReadLinesReverse(a)) yield return l;
    }

    /// <summary>Строки файла с конца к началу (блоками по 64 КБ, файл целиком в память не читается). Пустые строки пропускаются.</summary>
    public static IEnumerable<string> ReadLinesReverse(string path)
    {
        if (!File.Exists(path)) yield break;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int Block = 64 * 1024;
        var pos = fs.Length;
        var carry = Array.Empty<byte>();
        while (pos > 0)
        {
            var take = (int)Math.Min(Block, pos);
            pos -= take;
            var buf = new byte[take + carry.Length];
            fs.Seek(pos, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, take);
            Buffer.BlockCopy(carry, 0, buf, take, carry.Length);
            var end = buf.Length;
            for (var i = buf.Length - 1; i >= 0; i--)
            {
                if (buf[i] != (byte)'\n') continue;
                if (i + 1 < end)
                {
                    var line = Encoding.UTF8.GetString(buf, i + 1, end - i - 1).Trim();
                    if (line.Length > 0) yield return line;
                }
                end = i;
            }
            carry = buf[..end];
        }
        if (carry.Length > 0)
        {
            var line = Encoding.UTF8.GetString(carry).Trim();
            if (line.Length > 0) yield return line;
        }
    }
}
