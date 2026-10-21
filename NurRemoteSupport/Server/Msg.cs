using System.Text.Json;

namespace NurRemoteSupport.Server;

/// <summary>Сборка и разбор JSON-сообщений протокола (см. PROTOCOL.md).</summary>
public static class Msg
{
    private static readonly JsonSerializerOptions Opts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Служебные типы: сервер их обрабатывает сам и никогда не пересылает другой стороне.</summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "hello", "session", "join", "join-request", "consent", "waiting-consent", "started",
        "locked", "expired", "error", "ping", "pong", "end",
    };

    public static byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o, Opts);

    public static byte[] Error(string code) => Json(new { type = "error", code });

    public static byte[] End(string reason) => Json(new { type = "end", reason });

    /// <summary>Быстро достаёт верхнеуровневое поле "type" (без разбора всего сообщения). null — не JSON-объект или нет type.</summary>
    public static string? PeekType(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject) return null;
                if (r.TokenType != JsonTokenType.PropertyName) return null;
                var isType = r.ValueTextEquals("type"u8);
                if (!r.Read()) return null;
                if (isType) return r.TokenType == JsonTokenType.String ? r.GetString() : null;
                r.Skip();
            }
        }
        catch (JsonException) { }
        return null;
    }

    public static string? Str(JsonElement root, string name, int maxLen = 100)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        if (s is null) return null;
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > maxLen ? s[..maxLen] : s;
    }

    public static int? Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out var i) ? i : null;
    }

    public static bool? Bool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }
}
