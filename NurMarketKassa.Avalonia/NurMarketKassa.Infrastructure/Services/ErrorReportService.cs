using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NurMarketKassa.Services.Lan;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-05, ТЗ часть 7, раздел 3 — сервер выложил приём отчётов `POST /api/support/error-reports/` 05.10 около 04:30
/// (владелец: «если бэкенд добавит — сразу применяй»). Ошибки уровня error и выше и падения программы уходят на сервер
/// сами: одинаковые за 10 минут сворачиваются в один отчёт с count (по fingerprint — тип + текст без чисел и номеров),
/// к отчёту — последние 50 строк журнала. Пачки до 50 отчётов раз в 5 минут; без связи и без входа копятся в файле
/// (не больше 200) и досылаются. Текст уже очищен от токенов и паролей (PosLogger → SensitiveDataRedactor), сервер
/// чистит ещё раз. Кнопка «Отправить в поддержку» пока работает как раньше (zip в Telegram).
/// Сервер без адреса (404) — до перезапуска не спрашиваем.
/// </summary>
public static class ErrorReportService
{
    private const int MaxQueue = 200;
    private const int BatchSize = 50;
    private static readonly TimeSpan CollapseWindow = TimeSpan.FromMinutes(10);

    /// <summary>Отправка: относительный путь, тело. Задаётся при запуске (App) — через NurMarketApiClient.</summary>
    public static Func<object, Task<JsonElement>>? Sender { get; set; }

    private static readonly object Sync = new();
    private static readonly Queue<string> Recent = new();
    private static List<Report>? _queue;
    private static Timer? _timer;
    private static bool _unsupported;
    private static int _sending;

    private sealed class Report
    {
        public string ClientReportId { get; set; } = Guid.NewGuid().ToString();
        public string Level { get; set; } = "error";
        public string Category { get; set; } = "other";
        public string Fingerprint { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Stack { get; set; }
        public List<string> Context { get; set; } = new();
        public int Count { get; set; } = 1;
        public DateTimeOffset FirstAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset LastAt { get; set; } = DateTimeOffset.Now;
    }

    private static string QueuePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "error-reports.json");

    /// <summary>Подписаться на журнал и запустить отправку раз в 5 минут. Вызывать один раз при запуске.</summary>
    public static void Start()
    {
        if (_timer is not null)
            return;
        PosLogger.Observer = OnLog;
        _timer = new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
    }

    private static void OnLog(LogLevel level, string category, string message)
    {
        lock (Sync)
        {
            Recent.Enqueue($"{DateTime.Now:HH:mm:ss} [{category}] {(message.Length > 300 ? message[..300] : message)}");
            while (Recent.Count > 50)
                Recent.Dequeue();
        }

        var payFailed = message.Contains("PAY failed", StringComparison.OrdinalIgnoreCase);
        if (level < LogLevel.Error && !payFailed)
            return;
        // Сами отчёты об отправке отчётов не отправляем — иначе при сбое сервера получится петля.
        if (message.StartsWith("Отчёты об ошибках", StringComparison.Ordinal))
            return;

        try
        {
            Add(level == LogLevel.Critical ? "critical" : "error", MapCategory(category, message, payFailed), message);
        }
        catch
        {
            // отчёт не должен ломать программу
        }
    }

    private static void Add(string level, string category, string message)
    {
        var (text, stack) = SplitStack(message);
        var fingerprint = Fingerprint(category, text, stack);
        lock (Sync)
        {
            var queue = Queue();
            var now = DateTimeOffset.Now;
            var same = queue.LastOrDefault(r => r.Fingerprint == fingerprint && now - r.FirstAt < CollapseWindow);
            if (same is not null)
            {
                same.Count++;
                same.LastAt = now;
            }
            else
            {
                queue.Add(new Report
                {
                    Level = level, Category = category, Fingerprint = fingerprint,
                    Message = text.Length > 1000 ? text[..1000] : text,
                    Stack = stack is { Length: > 8000 } ? stack[..8000] : stack,
                    Context = Recent.ToList(),
                });
                while (queue.Count > MaxQueue)
                    queue.RemoveAt(0);
            }
            SaveQueue(queue);
        }
    }

    public static async Task FlushAsync()
    {
        if (_unsupported || Sender is null || Interlocked.Exchange(ref _sending, 1) == 1)
            return;
        try
        {
            while (true)
            {
                List<Report> batch;
                lock (Sync)
                {
                    // Отчёт, который ещё может свернуться с новыми такими же ошибками, ждёт конца окна 10 минут.
                    batch = Queue().Where(r => DateTimeOffset.Now - r.FirstAt >= CollapseWindow).Take(BatchSize).ToList();
                }
                if (batch.Count == 0)
                    break;

                var body = new Dictionary<string, object?>
                {
                    ["reports"] = batch.Select(r => new Dictionary<string, object?>
                    {
                        ["client_report_id"] = r.ClientReportId,
                        ["app"] = AppMode.IsOwner ? "owner" : "kassa",
                        ["version"] = AppVersion,
                        ["os"] = RuntimeInformation.OSDescription,
                        ["device_id"] = LanSyncService.DeviceId,
                        ["level"] = r.Level,
                        ["category"] = r.Category,
                        ["fingerprint"] = r.Fingerprint,
                        ["message"] = r.Message,
                        ["stack"] = r.Stack,
                        ["context"] = r.Context,
                        ["count"] = r.Count,
                        ["first_at"] = r.FirstAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                        ["last_at"] = r.LastAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                    }).ToList(),
                };
                await Sender(body).ConfigureAwait(false);
                lock (Sync)
                {
                    var ids = batch.Select(b => b.ClientReportId).ToHashSet();
                    var queue = Queue();
                    queue.RemoveAll(r => ids.Contains(r.ClientReportId));
                    SaveQueue(queue);
                }
                PosLogger.Log($"Отчёты об ошибках: отправлено в поддержку {batch.Count}.", "SYNC");
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            _unsupported = true;
            PosLogger.Log("Отчёты об ошибках: у сервера нет support/error-reports/ — отчёты остаются на компьютере.", "SYNC");
        }
        catch (Exception ex)
        {
            // Нет связи, не вошли (401), лимит (429) — отправим позже.
            PosLogger.Log($"Отчёты об ошибках: отправка отложена ({ex.Message}).", "DEBUG");
        }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
        }
    }

    private static string AppVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version?.ToString(3) ?? "";

    /// <summary>Раздел журнала → категория отчёта из ТЗ (payment, shift, print, …).</summary>
    private static string MapCategory(string category, string message, bool payFailed)
    {
        if (payFailed)
            return "payment";
        var c = category.ToUpperInvariant();
        if (c.Contains("CRITICAL") && message.Contains("Unhandled", StringComparison.OrdinalIgnoreCase))
            return "crash";
        return c switch
        {
            _ when c.Contains("PAY") => "payment",
            _ when c.Contains("SHIFT") => "shift",
            _ when c.Contains("PRINT") => "print",
            _ when c.Contains("DRAWER") => "drawer",
            _ when c.Contains("SCALE") || c.Contains("WEIGHT") => "scales",
            _ when c.Contains("OFFLINE") => "offline",
            _ when c.Contains("SYNC") || c.Contains("CATALOG") => "sync",
            _ when c.Contains("AUTH") || c.Contains("LOGIN") => "login",
            _ when c.Contains("TELEGRAM") || c.Contains("BOT") => "bot",
            _ when c.Contains("UPDATE") => "update",
            _ when c.Contains("UI") => "ui",
            _ => "other",
        };
    }

    private static (string Text, string? Stack) SplitStack(string message)
    {
        var at = message.IndexOf("\n   at ", StringComparison.Ordinal);
        if (at < 0)
            at = message.IndexOf("\r\n   at ", StringComparison.Ordinal);
        return at < 0 ? (message.Trim(), null) : (message[..at].Trim(), message[at..].Trim());
    }

    /// <summary>sha1: категория + текст без чисел, номеров и id + 3 верхних кадра стека.</summary>
    private static string Fingerprint(string category, string text, string? stack)
    {
        var norm = Regex.Replace(text, @"[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}|[0-9a-fA-F]{24,}|\d+([.,]\d+)?", "#");
        var frames = stack is null ? "" : string.Join("|", stack.Split('\n').Take(3).Select(f => Regex.Replace(f.Trim(), @":line \d+", "")));
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(category + "\n" + norm + "\n" + frames));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static List<Report> Queue()
    {
        if (_queue is not null)
            return _queue;
        try
        {
            if (File.Exists(QueuePath))
                _queue = JsonSerializer.Deserialize<List<Report>>(File.ReadAllText(QueuePath));
        }
        catch
        {
            // испорченный файл — начинаем заново
        }
        return _queue ??= new List<Report>();
    }

    private static void SaveQueue(List<Report> queue)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
            var tmp = QueuePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(queue));
            File.Move(tmp, QueuePath, overwrite: true);
        }
        catch
        {
            // не сохранилось — отправим из памяти
        }
    }
}
