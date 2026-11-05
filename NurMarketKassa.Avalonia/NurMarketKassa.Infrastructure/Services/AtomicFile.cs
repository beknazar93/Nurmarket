using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-29 (аварийный аудит кассы, п. 7): запись файлов данных, переживающая обрыв питания.
/// Было: <c>File.WriteAllText</c> прямо в рабочий файл — обрыв посреди записи оставлял полфайла,
/// а чтение битого файла молча отдавало «пусто» и следующая запись затирала всё (открытые чеки,
/// очередь закрытий смен, история касс, очередь бонусов).
/// Теперь: пишем во временный файл → сбрасываем на диск (<c>Flush(true)</c>) → подменяем рабочий
/// (<c>File.Replace</c>, прежняя версия уходит в <c>.bak</c>). При чтении: битый рабочий файл
/// переименовывается в <c>.bad</c>, данные берутся из <c>.bak</c> и восстанавливаются в рабочий
/// файл, в журнал пишется строка WARNING.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Записывает текст атомарно. Исключения пробрасываются вызывающему (он решает, что писать в журнал).</summary>
    public static void WriteAllText(string path, string text)
    {
        WriteCore(path, text);
        Deferred.TryRemove(path, out _); // запись прошла напрямую — отложенное состояние устарело
    }

    private static void WriteCore(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(true); // на диск, а не в кэш системы
        }

        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    /// <summary>2026-11-01 (контрольный аудит, Н2-01/Н-07): то же, что <see cref="WriteAllText"/>, но с короткими повторами, если файл на миг занят (антивирус, индексатор,
    /// другая программа читает файл): до <paramref name="attempts"/> попыток с паузой <paramref name="pauseMs"/> мс, потом исключение пробрасывается.</summary>
    public static void WriteAllTextWithRetry(string path, string text, int attempts = 5, int pauseMs = 20)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                WriteAllText(path, text);
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(pauseMs * attempt);
            }
        }
    }

    /// <summary>Читает текст файла. <paramref name="isValid"/> — проверка «файл целый» (по умолчанию — это корректный JSON).
    /// null — файла нет вовсе или нет ни целого файла, ни целой копии .bak.</summary>
    public static string? ReadAllText(string path, Func<string, bool>? isValid = null)
    {
        isValid ??= IsJson;
        var backup = path + ".bak";

        string? mainError = null;
        if (File.Exists(path))
        {
            // 2026-10-29 (приёмка 1.17.65, В-3): «файл занят» (антивирус, индексатор, другой процесс) — не порча. Читаем с повтором 3 × 50 мс;
            // не вышло — пробрасываем IOException: рабочий файл НЕ переименовывается в .bad и данные НЕ откатываются к .bak (последняя запись
            // сохраняется). Битым считается только файл, который прочитался, но пуст или не проходит проверку (JSON).
            string text;
            try
            {
                text = ReadWithRetry(path);
            }
            catch (FileNotFoundException)
            {
                return null; // исчез между проверкой и чтением — как «файла нет»
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PosLogger.Log($"Файл {Path.GetFileName(path)} занят и не прочитан ({ex.Message}) — не считаем повреждённым, копию .bak не берём.", "WARNING");
                throw;
            }

            if (!string.IsNullOrWhiteSpace(text) && isValid(text))
                return text;
            mainError = "файл повреждён";
        }
        else
        {
            // Рабочего файла нет вовсе (новая установка, смена аккаунта, намеренное удаление) — копию .bak не воскрешаем.
            return null;
        }

        // Рабочий файл отсутствует или битый — пробуем копию прошлой записи.
        string? restored = null;
        try
        {
            if (File.Exists(backup))
            {
                var text = File.ReadAllText(backup);
                if (isValid(text))
                    restored = text;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Копия {Path.GetFileName(backup)} не прочитана: {ex.Message}", "WARNING");
        }

        try
        {
            if (File.Exists(path))
                File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Битый файл {Path.GetFileName(path)} не переименован в .bad: {ex.Message}", "WARNING");
        }

        if (restored is not null)
        {
            PosLogger.Log(
                $"Файл {Path.GetFileName(path)} {mainError} — данные восстановлены из копии .bak.",
                "WARNING");
            try
            {
                File.Copy(backup, path, overwrite: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Копия {Path.GetFileName(backup)} не возвращена в {Path.GetFileName(path)}: {ex.Message}", "WARNING");
            }
            return restored;
        }

        PosLogger.Log(
            $"Файл {Path.GetFileName(path)} повреждён ({mainError}), целой копии .bak нет — работаем с пустыми данными, битый файл сохранён как .bad.",
            "ERROR");
        return null;
    }

    /// <summary>2026-11-02 (аудит №2 кассы, К3-01): исход чтения хранилища. <see cref="Missing"/> — файла нет (пусто, писать можно); <see cref="Ok"/> — прочитан;
    /// <see cref="Unreadable"/> — файл есть, но прочитать не удалось (занят антивирусом/другой программой, нет доступа) даже после повторов: это НЕ «пусто» —
    /// хранилище не должно писать поверх файла (иначе данные пропадут); <see cref="ReadResult.Text"/> тогда — копия .bak (для показа) или null.</summary>
    public enum ReadStatus { Missing, Ok, Unreadable }

    public readonly record struct ReadResult(ReadStatus Status, string? Text, bool FromMemory = false)
    {
        /// <summary>true — писать в файл можно (файла нет или он прочитан).</summary>
        public bool CanWrite => Status != ReadStatus.Unreadable;
    }

    /// <summary>Читает хранилище, различая «нет файла» и «не удалось прочитать». Занят/нет доступа — до <paramref name="attempts"/> повторов с паузой
    /// <paramref name="pauseMs"/> мс; не вышло — копия .bak (только для чтения) и статус Unreadable. Битый файл (не JSON) читается как раньше: из .bak.</summary>
    private static ReadResult TryReadFile(string path, Func<string, bool>? isValid, int attempts, int pauseMs)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var text = ReadAllText(path, isValid);
                return text is null ? new ReadResult(ReadStatus.Missing, null) : new ReadResult(ReadStatus.Ok, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < attempts)
                {
                    Thread.Sleep(pauseMs);
                    continue;
                }
                PosLogger.Log($"Файл {Path.GetFileName(path)} занят и не прочитан за {attempts} попыток ({ex.Message}) — запись поверх файла запрещена до следующего успешного чтения.", "WARNING");
                string? backup = null;
                try
                {
                    var bak = path + ".bak";
                    if (File.Exists(bak) && File.ReadAllText(bak) is { } bakText && (isValid ?? IsJson)(bakText))
                        backup = bakText;
                }
                catch (Exception bex) when (bex is IOException or UnauthorizedAccessException)
                {
                    // и копию прочитать нельзя — отдаём null
                }
                return new ReadResult(ReadStatus.Unreadable, backup);
            }
        }
    }

    // ======================= 2026-11-02 (приёмка круга 3, К3-01): отложенные записи =======================
    // Файл занят (антивирус, индексатор) — хранилище не пишет поверх, а ОТКЛАДЫВАЕТ запись: самое свежее состояние лежит в памяти, и «память» — приоритетный источник чтения
    // (TryRead отдаёт её, а не старый файл), пока файл не записан. Фоновый таймер раз в 7 с пробует записать (с объединением с файлом, когда он станет читаемым).
    // При закрытии программы — последняя попытка; не вышло — запасной файл рядом (*.pending), который подхватывается при следующем старте (новее по времени — побеждает).

    private sealed class DeferredWrite(string name, string text, Func<string?, string, string>? merge)
    {
        public string Name { get; } = name;
        public string Text { get; } = text;
        public Func<string?, string, string>? Merge { get; } = merge;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeferredWrite> Deferred = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object DeferredTimerGate = new();
    private static Timer? _deferredTimer;
    private static int _exitHooked;

    /// <summary>Отложить запись: <paramref name="text"/> — полное свежее состояние файла; <paramref name="merge"/>(текст файла или null, текст памяти) → что записать,
    /// когда файл станет читаемым (по умолчанию пишется текст памяти). Замена уже отложенной записи того же файла — новее выигрывает.</summary>
    public static void DeferWrite(string path, string storeName, string text, Func<string?, string, string>? merge = null)
    {
        var deferred = new DeferredWrite(storeName, text, merge);
        Deferred[path] = deferred;
        // 2026-11-05 (аудит №3, Р3-04): функция слияния хранилища запоминается по пути — ею же сливается запасной файл при восстановлении.
        if (merge is not null)
            MergeByPath[path] = merge;
        // 2026-11-05 (Р3-04): запасной файл пишется СРАЗУ при отказе (не только при закрытии): убийство процесса (установщик через 15 с, «Всё равно завершить»
        // в Windows) не даёт сработать ProcessExit — раньше отложенное состояние тогда терялось. Не чаще раза в 2 с на файл.
        WritePendingThrottled(path, deferred);
        NoteWriteBlocked(storeName);
        lock (DeferredTimerGate)
        {
            _deferredTimer ??= new Timer(_ => FlushDeferred(onExit: false), null, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7));
            if (Interlocked.Exchange(ref _exitHooked, 1) == 0)
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushDeferred(onExit: true);
        }
    }

    /// <summary>Снять отложенную запись и запасной файл (хранилище очищено или записано само).</summary>
    public static void CancelDeferred(string path)
    {
        Deferred.TryRemove(path, out _);
        try { File.Delete(path + ".pending"); } catch (Exception) { /* запасного файла нет или занят */ }
    }

    private static void FlushDeferred(bool onExit)
    {
        foreach (var (path, deferred) in Deferred.ToArray())
        {
            try
            {
                var read = TryReadFile(path, null, onExit ? 2 : 1, 100);
                if (read.Status == ReadStatus.Unreadable)
                {
                    if (onExit)
                        WritePending(path, deferred);
                    continue;
                }
                var text = deferred.Merge is { } merge ? merge(read.Text, deferred.Text) : deferred.Text;
                WriteCore(path, text);
                if (Deferred.TryGetValue(path, out var current) && ReferenceEquals(current, deferred))
                    Deferred.TryRemove(path, out _);
                try { File.Delete(path + ".pending"); } catch (Exception) { /* нет запасного файла */ }
                PosLogger.Log($"{deferred.Name}: отложенная запись выполнена — файл освободился.", "INFO");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (onExit)
                    WritePending(path, deferred);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"{deferred.Name}: отложенная запись не выполнена ({ex.Message}).", "WARNING");
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<string?, string, string>> MergeByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> PendingWrittenAt = new(StringComparer.OrdinalIgnoreCase);

    private static void WritePendingThrottled(string path, DeferredWrite deferred)
    {
        var now = DateTime.UtcNow;
        if (PendingWrittenAt.TryGetValue(path, out var last) && now - last < TimeSpan.FromSeconds(2))
            return;
        PendingWrittenAt[path] = now;
        try
        {
            using var stream = new FileStream(path + ".pending", FileMode.Create, FileAccess.Write, FileShare.Read);
            var bytes = Utf8NoBom.GetBytes(deferred.Text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true); // 2026-11-05: на диск, не в кэш системы
        }
        catch (Exception ex)
        {
            PosLogger.Log($"{deferred.Name}: запасной файл сразу не записан ({ex.Message}).", "DEBUG");
        }
    }

    private static void WritePending(string path, DeferredWrite deferred)
    {
        try
        {
            using (var stream = new FileStream(path + ".pending", FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var bytes = Utf8NoBom.GetBytes(deferred.Text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true); // 2026-11-05 (Р3-04): сброс на диск
            }
            PosLogger.Log($"{deferred.Name}: файл занят при закрытии — состояние сохранено в {Path.GetFileName(path)}.pending, подхватится при следующем запуске.", "WARNING");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"{deferred.Name}: не удалось сохранить запасной файл ({ex.Message}).", "ERROR");
        }
    }

    /// <summary>Запасной файл *.pending прошлого запуска: он новее рабочего файла — становится отложенной записью (дальше её пишет таймер); старее — удаляется.</summary>
    private static void RecoverPending(string path)
    {
        var pending = path + ".pending";
        try
        {
            if (!File.Exists(pending) || Deferred.ContainsKey(path))
                return;
            var pendingStamp = File.GetLastWriteTimeUtc(pending);
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= pendingStamp)
            {
                // 2026-11-05 (Р3-04): рабочий файл новее — запасной НЕ удаляем молча (в нём могли быть правки, которых в файле нет): если функция слияния
                // хранилища известна — сливаем, иначе откладываем копию рядом (*.pending.old, перезаписывается) и пишем в журнал.
                var oldText = File.ReadAllText(pending);
                if (IsJson(oldText) && MergeByPath.TryGetValue(path, out var knownMerge) && TryReadFile(path, null, 1, 100) is { Status: not ReadStatus.Unreadable } current
                    && current.Text != oldText)
                {
                    Deferred[path] = new DeferredWrite(Path.GetFileName(path), oldText, knownMerge);
                    PosLogger.Log($"{Path.GetFileName(path)}: запасной файл старее рабочего — сливаем с ним функцией хранилища.", "WARNING");
                    lock (DeferredTimerGate)
                        _deferredTimer ??= new Timer(_ => FlushDeferred(onExit: false), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7));
                    return;
                }
                try
                {
                    File.Move(pending, pending + ".old", overwrite: true);
                    PosLogger.Log($"{Path.GetFileName(path)}: запасной файл старее рабочего — отложен как {Path.GetFileName(pending)}.old (на случай ручного восстановления).", "WARNING");
                }
                catch (Exception)
                {
                    File.Delete(pending);
                }
                return;
            }
            var text = File.ReadAllText(pending);
            if (IsJson(text))
            {
                PosLogger.Log($"{Path.GetFileName(path)}: найден запасной файл прошлого запуска — состояние восстановлено и будет записано.", "WARNING");
                MergeByPath.TryGetValue(path, out var recoverMerge);
                Deferred[path] = new DeferredWrite(Path.GetFileName(path), text, recoverMerge);
                lock (DeferredTimerGate)
                    _deferredTimer ??= new Timer(_ => FlushDeferred(onExit: false), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7));
            }
            else
                File.Delete(pending);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"{Path.GetFileName(path)}: запасной файл не прочитан ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Читает хранилище, различая «нет файла» и «не удалось прочитать». Приоритет — отложенная запись в памяти (её ещё нет в файле). Занят/нет доступа — до
    /// <paramref name="attempts"/> повторов с паузой <paramref name="pauseMs"/> мс; не вышло — копия .bak (только для чтения) и статус Unreadable.</summary>
    public static ReadResult TryRead(string path, Func<string, bool>? isValid = null, int attempts = 5, int pauseMs = 150)
    {
        RecoverPending(path);
        if (Deferred.TryGetValue(path, out var deferred))
            return new ReadResult(ReadStatus.Ok, deferred.Text, FromMemory: true);
        return TryReadFile(path, isValid, attempts, pauseMs);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> BlockedNotified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Хранилище отказалось писать (файл не прочитан): запись в журнал и тост владельцу не чаще раза в 5 минут на хранилище. Изменения остаются в памяти до освобождения файла.</summary>
    public static void NoteWriteBlocked(string storeName)
    {
        var now = Environment.TickCount64;
        if (BlockedNotified.TryGetValue(storeName, out var last) && now - last is >= 0 and < 300_000)
            return;
        BlockedNotified[storeName] = now;
        PosLogger.Log($"{storeName}: файл занят другой программой и не прочитан — запись отложена, чтобы не затереть данные.", "WARNING");
        try
        {
            CatalogCacheService.RaiseToast(Tr.T(
                $"Файл «{storeName}» занят другой программой (антивирус?) — данные не перезаписываются, изменения держатся в памяти, пока файл не освободится.",
                $"«{storeName}» файлы башка программа тарабынан ээленген (антивирус?) — маалымат кайра жазылбайт, өзгөртүүлөр файл бошогонго чейин эстутумда турат.",
                $"The “{storeName}” file is locked by another program (antivirus?) — data is not overwritten; changes stay in memory until the file is free.",
                $"«{storeName}» dosyası başka bir program tarafından kullanılıyor (antivirüs?) — veriler üzerine yazılmıyor, değişiklikler dosya serbest kalana kadar bellekte tutulur.",
                $"«{storeName}» fayli boshqa dastur tomonidan band (antivirus?) — ma'lumotlar ustiga yozilmaydi, o'zgarishlar fayl bo'shaguncha xotirada turadi."), true);
        }
        catch (Exception)
        {
            // тост — подсказка; журнал уже записан
        }
    }

    /// <summary>2026-10-29 (В-3): чтение с тремя попытками и паузой 50 мс при «файл занят» (sharing violation и прочие IOException).</summary>
    internal static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (attempt < 3
                                       && ex is IOException or UnauthorizedAccessException
                                       && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
