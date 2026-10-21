using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurRemoteSupport.Server;

/// <summary>Заметка оператора о кассе (что случилось, что сделали).</summary>
public sealed class NoteEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("operator")] public string Operator { get; set; } = "";
    [JsonPropertyName("operatorName")] public string OperatorName { get; set; } = "";
    [JsonPropertyName("company")] public string Company { get; set; } = "";
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

public sealed record NoteRequest(string? Company, string? Machine, string? SessionId, string? Text);

/// <summary>2026-10-16, владелец: «программа для сотрудников тех поддержки — все инструменты». История обращений: заметки
/// операторов по кассам (компания + компьютер), общие для всех операторов — файл {DataDir}/notes.jsonl, только дописывается.
/// Программа оператора показывает прошлые заметки при подключении к кассе и пишет новую по итогам сеанса.</summary>
public sealed class NotesStore(RelayOptions o)
{
    private readonly string _path = Path.Combine(o.DataDir, "notes.jsonl");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task AddAsync(NoteEntry e)
    {
        var line = JsonSerializer.Serialize(e, Opts) + "\n";
        await _lock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_path, line, new UTF8Encoding(false));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Заметки по компании (и компьютеру, если указан), новые сверху. Без компании — последние по всем кассам.</summary>
    public async Task<List<NoteEntry>> ListAsync(string? company, string? machine, int limit)
    {
        var result = new List<NoteEntry>();
        if (!File.Exists(_path))
            return result;
        string[] lines;
        await _lock.WaitAsync();
        try
        {
            lines = await File.ReadAllLinesAsync(_path, Encoding.UTF8);
        }
        finally
        {
            _lock.Release();
        }
        for (var i = lines.Length - 1; i >= 0 && result.Count < limit; i--)
        {
            NoteEntry? e;
            try
            {
                e = JsonSerializer.Deserialize<NoteEntry>(lines[i]);
            }
            catch (JsonException)
            {
                continue; // испорченная строка (обрыв записи) — пропускаем
            }
            if (e is null)
                continue;
            if (!string.IsNullOrWhiteSpace(company) && !string.Equals(e.Company, company.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(machine) && !string.Equals(e.Machine, machine.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(e);
        }
        return result;
    }
}
