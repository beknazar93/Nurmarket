namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В3 Л5-22 — межпроцессный замок на файле (FileShare.None; работает на Windows, Linux и Android, как у журнала оплат долга).
/// Касса и программа владельца на одном ПК делят файлы оповещений («уже отправили», очередь до утра): замок внутри процесса (lock/SemaphoreSlim) не мешал
/// двум программам одновременно прочитать «не отправляли» и обе отправили бы одно сообщение. Файл замка лежит рядом с общими файлами, содержимого нет.</summary>
public sealed class CrossProcessLock : IDisposable
{
    private FileStream? _stream;

    private CrossProcessLock(FileStream? stream) => _stream = stream;

    /// <summary>Замок «не взят, но работать можно»: файл замка недоступен по причине, не связанной с другой программой (нет прав, диск только для чтения).
    /// Лучше отправить без замка, чем не отправить вообще.</summary>
    private static CrossProcessLock Unavailable() => new(null);

    /// <summary>Взять замок. Ждёт до <paramref name="wait"/>. null — не дождались (замок держит другая программа); иначе объект, который нужно освободить (Dispose).</summary>
    public static CrossProcessLock? TryAcquire(string lockFilePath, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var attempt = TryOpen(lockFilePath, out var busy);
            if (attempt is not null || !busy)
                return attempt;
            if (DateTime.UtcNow >= deadline)
                return null;
            Thread.Sleep(25);
        }
    }

    /// <summary>То же без блокировки потока (Task.Delay между попытками) — для замка на время отправки в сеть.</summary>
    public static async Task<CrossProcessLock?> TryAcquireAsync(string lockFilePath, TimeSpan wait, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            var attempt = TryOpen(lockFilePath, out var busy);
            if (attempt is not null || !busy)
                return attempt;
            if (DateTime.UtcNow >= deadline)
                return null;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    private static CrossProcessLock? TryOpen(string path, out bool busy)
    {
        busy = false;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new CrossProcessLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
        }
        catch (IOException)
        {
            busy = true; // файл занят другой программой (или другим потоком)
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            PosLogger.Log($"Замок «{Path.GetFileName(path)}» недоступен ({ex.GetType().Name}) — работаем без межпрограммного замка.", "WARNING");
            return Unavailable();
        }
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Dispose();
    }
}
