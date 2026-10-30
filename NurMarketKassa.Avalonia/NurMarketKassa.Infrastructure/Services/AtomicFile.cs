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
