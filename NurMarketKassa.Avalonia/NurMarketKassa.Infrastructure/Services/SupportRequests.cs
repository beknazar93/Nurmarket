using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (Л-9 «Поддержка», кодовая часть): запись об одном обращении в поддержку из окна «Написать в поддержку».
/// Описание хранится уже очищенным от ключей доступа.</summary>
public sealed class SupportRequestRecord
{
    public const string StatusSent = "sent";
    public const string StatusFailed = "failed";
    public const string StatusNotConfigured = "notconfigured";

    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Description { get; set; } = "";
    /// <summary>sent — ушло в поддержку; failed — не ушло (нет связи, отказ канала); notconfigured — канал поддержки не подключён.</summary>
    public string Status { get; set; } = StatusFailed;
    public string? Error { get; set; }
    public int LogLines { get; set; }
    public bool WithScreenshot { get; set; }
    public string Version { get; set; } = "";
    public string Company { get; set; } = "";
    public string Branch { get; set; } = "";
    /// <summary>Откуда открыто окно: «меню», «окно ошибки», «журнал».</summary>
    public string Source { get; set; } = "";
}

/// <summary>Часы работы поддержки и срок первого ответа — тексты-настройки. Значения по умолчанию — «уточняется» (часы решит владелец):
/// пока файл <c>support-info.json</c> (рядом с программой или в папке данных) не задан, показывается заглушка. Формат файла:
/// <c>{"hours": "08:00–01:00", "response": "до 10 минут"}</c> или по языкам <c>{"hours": {"ru": "…", "ky": "…", "en": "…", "tr": "…", "uz": "…"}}</c>.</summary>
public static class SupportInfo
{
    private const string FileName = "support-info.json";

    public static string Hours() => Read("hours");

    public static string ResponseTime() => Read("response");

    private static string Pending() => Tr.T("уточняется", "тактала элек", "to be confirmed", "netleştiriliyor", "aniqlanmoqda");

    private static string Read(string key)
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, DataDirectory })
        {
            try
            {
                var path = Path.Combine(dir, FileName);
                if (!File.Exists(path))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty(key, out var value))
                    continue;
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Object => PickLanguage(value),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                PosLogger.Log($"Поддержка: файл {FileName} не прочитан ({ex.GetType().Name}).", "WARNING");
            }
        }
        return Pending();
    }

    private static string? PickLanguage(JsonElement obj)
    {
        var code = UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => "ky",
            AppLanguage.English => "en",
            AppLanguage.Turkish => "tr",
            AppLanguage.Uzbek => "uz",
            _ => "ru",
        };
        if (obj.TryGetProperty(code, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            return v.GetString();
        return obj.TryGetProperty("ru", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString() : null;
    }

    private static string DataDirectory => Path.Combine(AppPaths.Local, AppMode.DataFolderName);
}

/// <summary>Хвост журнала кассы для обращения: последние N строк, каждая — через центральную очистку секретов
/// (токены, пароли, JWT, телефоны, почта). Читает файл без блокировки (журнал открыт на запись).</summary>
public static class SupportLogTail
{
    /// <summary>Сколько строк прикладывать по умолчанию.</summary>
    public const int DefaultLines = 300;
    private const long MaxReadBytes = 1024 * 1024;

    public static string LogPath => Path.Combine(AppPaths.Local, AppMode.DataFolderName, "Logs", "nurmarket-kassa.log");

    /// <summary>Последние <paramref name="maxLines"/> строк файла, очищенные от секретов; <paramref name="count"/> — сколько строк вошло. Нет файла — пусто.</summary>
    public static string Build(string path, int maxLines, out int count)
    {
        count = 0;
        try
        {
            if (!File.Exists(path) || maxLines <= 0)
                return "";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxReadBytes)
                fs.Seek(-MaxReadBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var all = reader.ReadToEnd().Split('\n');
            var lines = all.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (lines.Count > maxLines)
                lines = lines.GetRange(lines.Count - maxLines, maxLines);
            count = lines.Count;
            // Очистка — построчно: шаблон телефона допускает пробелы между цифрами, на целом тексте он склеивал соседние строки журнала.
            return string.Join(Environment.NewLine, lines.Select(SensitiveDataRedactor.Redact));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Поддержка: журнал не прочитан ({ex.GetType().Name}).", "WARNING");
            return "";
        }
    }
}

/// <summary>Журнал отправленных обращений: файл <c>support-requests.json</c> в папке данных программы, последние 100.</summary>
public static class SupportRequestJournal
{
    private const int MaxItems = 100;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Файл журнала; в тестах можно подменить.</summary>
    internal static string PathOverride { get; set; } = "";

    private static string FilePath => string.IsNullOrEmpty(PathOverride)
        ? Path.Combine(AppPaths.Local, AppMode.DataFolderName, "support-requests.json")
        : PathOverride;

    public static List<SupportRequestRecord> Load()
    {
        lock (Gate)
            return LoadCore();
    }

    private static List<SupportRequestRecord> LoadCore()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<SupportRequestRecord>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Поддержка: журнал обращений не прочитан ({ex.GetType().Name}).", "WARNING");
        }
        return [];
    }

    /// <summary>Добавляет или обновляет запись (по Id); новые — сверху.</summary>
    public static void Save(SupportRequestRecord record)
    {
        lock (Gate)
        {
            var list = LoadCore();
            var at = list.FindIndex(r => r.Id == record.Id);
            if (at >= 0)
                list[at] = record;
            else
                list.Insert(0, record);
            if (list.Count > MaxItems)
                list.RemoveRange(MaxItems, list.Count - MaxItems);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(list, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PosLogger.Log($"Поддержка: журнал обращений не записан ({ex.GetType().Name}).", "WARNING");
            }
        }
    }
}

/// <summary>Сборка и отправка обращения: описание + хвост журнала + сведения о программе + снимок окна (по желанию) → zip → бот поддержки
/// (<see cref="SupportLogService"/>, тот же канал, что у «Отправить в поддержку»). Никаких новых внешних служб.</summary>
public static class SupportRequestService
{
    public const int MaxDescriptionChars = 2000;

    /// <summary>Описание проблемы без ключей доступа и обрезанное по длине.</summary>
    public static string CleanDescription(string? text)
    {
        var t = SensitiveDataRedactor.RedactCredentials((text ?? "").Trim());
        return t.Length <= MaxDescriptionChars ? t : t[..MaxDescriptionChars];
    }

    /// <summary>Zip обращения: request.txt (описание и сведения), log-tail.txt (если приложен), screenshot.png (если есть).</summary>
    public static byte[] BuildZip(SupportRequestRecord record, string logTail, byte[]? screenshotPng)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var request = archive.CreateEntry("request.txt");
            using (var w = new StreamWriter(request.Open(), new UTF8Encoding(true)))
            {
                w.WriteLine($"Обращение № {record.Id}");
                w.WriteLine($"Время: {record.CreatedAt:dd.MM.yyyy HH:mm:ss}");
                w.WriteLine($"Версия: {record.Version}");
                w.WriteLine($"Компания: {Dash(record.Company)}");
                w.WriteLine($"Филиал: {Dash(record.Branch)}");
                w.WriteLine($"Откуда: {Dash(record.Source)}");
                w.WriteLine($"ОС: {Environment.OSVersion}, .NET {Environment.Version}");
                w.WriteLine();
                w.WriteLine("Описание проблемы:");
                w.WriteLine(record.Description);
            }
            if (logTail.Length > 0)
            {
                var log = archive.CreateEntry("log-tail.txt", CompressionLevel.Optimal);
                using var w = new StreamWriter(log.Open(), new UTF8Encoding(true));
                w.Write(logTail);
            }
            if (screenshotPng is { Length: > 0 })
            {
                var shot = archive.CreateEntry("screenshot.png", CompressionLevel.NoCompression);
                using var s = shot.Open();
                s.Write(screenshotPng);
            }
        }
        return ms.ToArray();
    }

    private static string Dash(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    /// <summary>Отправить обращение. Запись попадает в журнал в любом случае (с итоговым статусом); возвращается она же.
    /// <paramref name="existing"/> — повторная отправка той же записи (тот же номер).</summary>
    public static async Task<SupportRequestRecord> SubmitAsync(string description, bool includeLog, byte[]? screenshotPng, string source,
        string branch, SupportRequestRecord? existing = null, CancellationToken ct = default)
    {
        var company = CompanyInfoService.LastCompany?.Name;
        if (string.IsNullOrWhiteSpace(company))
            company = UserPreferences.Instance.StoreName;
        var record = existing ?? new SupportRequestRecord
        {
            Id = DateTime.Now.ToString("yyMMdd-HHmmss"),
            CreatedAt = DateTime.Now,
            Description = CleanDescription(description),
            Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?",
            Company = company ?? "",
            Branch = branch,
            Source = source,
        };
        var lines = 0;
        var tail = includeLog ? SupportLogTail.Build(SupportLogTail.LogPath, SupportLogTail.DefaultLines, out lines) : "";
        record.LogLines = lines;
        record.WithScreenshot = screenshotPng is { Length: > 0 };
        string? error;
        try
        {
            var zip = BuildZip(record, tail, screenshotPng);
            var note = record.Description.Length <= 300 ? record.Description : record.Description[..300];
            var caption = SupportLogService.BuildCaption($"№ {record.Id}: {note}", "🆘 Обращение NurCRM");
            error = await SupportLogService.SendRequestAsync(zip, caption, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
        {
            error = ex.Message;
        }
        record.Error = error;
        record.Status = error is null ? SupportRequestRecord.StatusSent
            : error == SupportLogService.NotConfiguredMessage ? SupportRequestRecord.StatusNotConfigured
            : SupportRequestRecord.StatusFailed;
        SupportRequestJournal.Save(record);
        PosLogger.Log($"Поддержка: обращение {record.Id} — {record.Status} (журнал {record.LogLines} строк, снимок {(record.WithScreenshot ? "да" : "нет")}).", "SUPPORT");
        return record;
    }

    /// <summary>Текст для WhatsApp, когда бот не подключён или не дошёл: описание и сведения, без журнала.</summary>
    public static string BuildFallbackText(SupportRequestRecord record) =>
        $"NurCRM {record.Version}, {Dash(record.Company)}, № {record.Id}\n{record.Description}";
}
