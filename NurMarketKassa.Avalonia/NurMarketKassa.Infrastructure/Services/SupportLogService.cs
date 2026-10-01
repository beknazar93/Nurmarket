using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-01, владелец: «логи пусть отправляются с названием аккаунта, логином и названием
/// компании в телеграм-бот, который мы создадим в будущем». Журнал программы и отчёты о сбоях
/// упаковываются в zip и уходят документом в чат поддержки; подпись — компания, логин, программа
/// (касса/владелец), версия, компьютер.
///
/// Бот ещё не создан, поэтому токен и чат в код не зашиты: их читает файл <c>support-bot.json</c>
/// (<c>{"token": "…", "chat_id": "…"}</c>) — рядом с программой (его кладут в установщик, когда бот
/// появится) или в папке данных программы. Нет файла — отправка выключена, кнопка честно об этом
/// говорит. Токен нигде не пишется в журнал.</summary>
public static class SupportLogService
{
    private const string ConfigFileName = "support-bot.json";
    private const long MaxLogBytesPerFile = 3 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private sealed record Config(string Token, string ChatId);

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppMode.DataFolderName);

    private static Config? LoadConfig()
    {
        foreach (var dir in new[]
                 {
                     AppContext.BaseDirectory,
                     DataDirectory,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName),
                 })
        {
            try
            {
                var path = Path.Combine(dir, ConfigFileName);
                if (!File.Exists(path))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
                var chat = root.TryGetProperty("chat_id", out var c)
                    ? (c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.GetString())
                    : null;
                if (!string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(chat))
                    return new Config(token.Trim(), chat.Trim());
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Бот поддержки: файл настроек не прочитан ({ex.GetType().Name}).", "WARNING");
            }
        }
        return null;
    }

    public static bool IsConfigured => LoadConfig() is not null;

    /// <summary>Подпись к журналу: кто прислал. Без паролей и токенов.</summary>
    public static string BuildCaption(string? note)
    {
        var company = CompanyInfoService.LastCompany?.Name;
        if (string.IsNullOrWhiteSpace(company))
            company = UserPreferences.Instance.StoreName;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        var sb = new StringBuilder()
            .AppendLine("📋 Журнал NurMarket")
            .AppendLine($"Компания: {(string.IsNullOrWhiteSpace(company) ? "—" : company)}")
            .AppendLine($"Логин: {(string.IsNullOrWhiteSpace(PosApp.CurrentUserId) ? "—" : PosApp.CurrentUserId)}")
            .AppendLine($"Программа: {(AppMode.IsOwner ? "Владелец" : "Касса")} {version}")
            .AppendLine($"Компьютер: {Environment.MachineName}")
            .AppendLine($"Время: {DateTime.Now:dd.MM.yyyy HH:mm}");
        if (!string.IsNullOrWhiteSpace(note))
            sb.AppendLine($"Причина: {note.Trim()}");
        var text = sb.ToString();
        return text.Length <= 1000 ? text : text[..1000];
    }

    /// <summary>Отправить журнал и отчёты о сбоях. null — отправлено, иначе текст ошибки для кассира.</summary>
    public static async Task<string?> SendLogsAsync(string? note, CancellationToken ct = default)
    {
        var config = LoadConfig();
        if (config is null)
            return "Бот поддержки ещё не подключён.";

        byte[] zip;
        try
        {
            zip = BuildZip();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бот поддержки: журнал не упакован: {ex.Message}", "WARNING");
            return "Не удалось собрать журнал: " + ex.Message;
        }

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(config.ChatId), "chat_id");
            form.Add(new StringContent(BuildCaption(note)), "caption");
            var file = new ByteArrayContent(zip);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            form.Add(file, "document", $"nurmarket-log-{SafeName(CompanyInfoService.LastCompany?.Name)}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
            using var response = await Http.PostAsync($"https://api.telegram.org/bot{config.Token}/sendDocument", form, ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                PosLogger.Log($"Бот поддержки: журнал отправлен ({zip.Length / 1024} КБ).", "SUPPORT");
                return null;
            }
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            PosLogger.Log($"Бот поддержки: отказ Telegram {(int)response.StatusCode}: {SensitiveDataRedactor.Redact(body)}", "WARNING");
            return $"Telegram ответил ошибкой {(int)response.StatusCode}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            PosLogger.Log($"Бот поддержки: нет связи ({ex.GetType().Name}).", "WARNING");
            return "Нет связи с Telegram.";
        }
    }

    /// <summary>При запуске: накопленные отчёты о сбоях — в поддержку (если бот подключён), затем
    /// переносятся в папку «sent», чтобы не уходить повторно.</summary>
    public static async Task TrySendPendingCrashReportsAsync(CancellationToken ct = default)
    {
        try
        {
            var dir = Path.Combine(DataDirectory, "CrashReports");
            if (!IsConfigured || !Directory.Exists(dir))
                return;
            var files = Directory.GetFiles(dir, "crash_*.txt");
            if (files.Length == 0)
                return;
            var error = await SendLogsAsync($"сбой программы ({files.Length})", ct).ConfigureAwait(false);
            if (error is not null)
                return;
            var sent = Directory.CreateDirectory(Path.Combine(dir, "sent")).FullName;
            foreach (var f in files)
                File.Move(f, Path.Combine(sent, Path.GetFileName(f)), overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Бот поддержки: отчёты о сбоях не отправлены: {ex.Message}", "WARNING");
        }
    }

    private static byte[] BuildZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var logs = Path.Combine(DataDirectory, "Logs");
            foreach (var name in new[] { "nurmarket-kassa.log.1", "nurmarket-kassa.log" })
                AddTail(archive, Path.Combine(logs, name), name);
            var crashes = Path.Combine(DataDirectory, "CrashReports");
            if (Directory.Exists(crashes))
            {
                foreach (var f in Directory.GetFiles(crashes, "crash_*.txt").OrderByDescending(f => f).Take(20))
                    AddTail(archive, f, "crash/" + Path.GetFileName(f));
            }
            var info = archive.CreateEntry("info.txt");
            using var w = new StreamWriter(info.Open(), new UTF8Encoding(true));
            w.Write(BuildCaption(null));
            w.WriteLine($"ОС: {Environment.OSVersion}, .NET {Environment.Version}");
        }
        return ms.ToArray();
    }

    /// <summary>Последние <see cref="MaxLogBytesPerFile"/> байт файла (журнал может быть открыт на запись).</summary>
    private static void AddTail(ZipArchive archive, string path, string entryName)
    {
        if (!File.Exists(path))
            return;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > MaxLogBytesPerFile)
            fs.Seek(-MaxLogBytesPerFile, SeekOrigin.End);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var es = entry.Open();
        fs.CopyTo(es);
    }

    private static string SafeName(string? name)
    {
        var s = string.IsNullOrWhiteSpace(name) ? "company" : name.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
            s = s.Replace(ch, '_');
        return s.Length > 40 ? s[..40] : s;
    }
}
