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

    /// <summary>
    /// Достаёт верхнеуровневое поле "type". null — не JSON-объект, нет type или он не строка.
    /// 2026-10-27 (С-1): если ключ "type" встречается в объекте дважды — тоже null: иначе сервер видел бы первый,
    /// а касса (JsonDocument берёт последний) — другой, и оператор мог бы протащить служебное сообщение.
    /// </summary>
    public static string? PeekType(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
            string? result = null;
            var seen = false;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject) return result;
                if (r.TokenType != JsonTokenType.PropertyName) return null;
                var isType = r.ValueTextEquals("type"u8);
                if (!r.Read()) return null;
                if (isType)
                {
                    if (seen) return null;
                    seen = true;
                    result = r.TokenType == JsonTokenType.String ? r.GetString() : null;
                }
                if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>2026-10-27 (В-2): ввод оператора, который при «Только просмотр» кассира не пересылается (мышь, клавиши, текст, буфер, файлы).</summary>
    public static bool IsInputType(string type) =>
        type is "mouse" or "key" or "text" or "clipboard-set" or "clipboard-get" || type.StartsWith("file-", StringComparison.Ordinal);

    /// <summary>2026-10-27: строка от клиента для лога — без управляющих символов и не длиннее max.</summary>
    public static string LogSafe(string? s, int max = 64)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return t.Length > max ? t[..max] + "…" : t;
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
