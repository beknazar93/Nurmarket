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

    /// <summary>Последние записи (новые сверху). Читает только хвост файла.</summary>
    public async Task<List<JsonNode>> TailAsync(int limit)
    {
        var result = new List<JsonNode>();
        if (!File.Exists(_path) || limit <= 0) return result;
        await _gate.WaitAsync();
        byte[] data;
        try
        {
            await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, Math.Max(64 * 1024, (long)limit * 1024));
            fs.Seek(-take, SeekOrigin.End);
            data = new byte[take];
            await fs.ReadExactlyAsync(data);
        }
        finally
        {
            _gate.Release();
        }
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Первая строка может быть обрезана — её пропускаем, если хвост не с начала файла.
        for (var i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            try
            {
                var node = JsonNode.Parse(lines[i]);
                if (node is not null) result.Add(node);
            }
            catch (JsonException) { }
        }
        return result;
    }
}
