using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NurRemoteSupport.Server;

public sealed class JournalEntry
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("operator")] public string? Operator { get; set; }
    [JsonPropertyName("operatorName")] public string? OperatorName { get; set; }
    [JsonPropertyName("operatorIp")] public string? OperatorIp { get; set; }
    [JsonPropertyName("company")] public string? Company { get; set; }
    [JsonPropertyName("machine")] public string? Machine { get; set; }
    [JsonPropertyName("app")] public string? App { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("hostIp")] public string? HostIp { get; set; }
    // 2026-10-16, v1.1: кассир и описание проблемы (из hello).
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
    [JsonPropertyName("start")] public DateTimeOffset? Start { get; set; }
    [JsonPropertyName("end")] public DateTimeOffset End { get; set; }
    [JsonPropertyName("durationSec")] public long DurationSec { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("bytesToOperator")] public long BytesToOperator { get; set; }
    [JsonPropertyName("bytesToHost")] public long BytesToHost { get; set; }
}

/// <summary>
/// Журнал сеансов: journal.jsonl, одна строка на сеанс, только дописывается (2026-09-29).
/// </summary>
public sealed class Journal
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<Journal> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Journal(RelayOptions o, ILogger<Journal> log)
    {
        _path = o.JournalPath;
        _log = log;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string FilePath => _path;

    public async Task AppendAsync(JournalEntry e)
    {
        var line = JsonSerializer.Serialize(e, JsonOpts) + "\n";
        await _gate.WaitAsync();
        try
        {
            Jsonl.RotateIfNeeded(_path); // 2026-10-27 (С-7): новый месяц — прошлый уходит в journal-ГГГГ-ММ.jsonl
            await using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(line);
            await fs.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            // Журнал не должен ронять сеанс; строку оставим хотя бы в логе.
            _log.LogError(ex, "Не удалось записать журнал сеанса {Line}", line.TrimEnd());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Последние записи (новые сверху): текущий файл, затем месячные архивы. Читает только хвост.</summary>
    public async Task<List<JsonNode>> TailAsync(int limit)
    {
        var result = new List<JsonNode>();
        if (limit <= 0) return result;
        await _gate.WaitAsync();
        try
        {
            foreach (var line in Jsonl.ReadAllNewestFirst(_path))
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node is not null) result.Add(node);
                }
                catch (JsonException) { /* обрезанная строка */ }
                if (result.Count >= limit) break;
            }
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "Не удалось прочитать журнал сеансов");
        }
        finally
        {
            _gate.Release();
        }
        return result;
    }
}
