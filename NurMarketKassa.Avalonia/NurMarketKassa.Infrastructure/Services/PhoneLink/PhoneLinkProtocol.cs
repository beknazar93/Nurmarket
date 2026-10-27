using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services.PhoneLink;

/// <summary>2026-10-27, пакет К-П2 (ТЗ «Мобилка и касса», путь B): коды ответов протокола связи кассы с телефоном (v1).</summary>
public static class PhoneLinkCodes
{
    public const string Ok = "ok";
    public const string Added = "added";
    public const string NotFound = "not_found";
    public const string TillInactive = "till_inactive";
    public const string Duplicate = "duplicate";
    public const string BadSig = "bad_sig";
    public const string BadTime = "bad_time";
    public const string Replay = "replay";
    public const string TooBig = "too_big";
    public const string UnknownPhone = "unknown_phone";
    public const string Revoked = "revoked";
    public const string Disabled = "disabled";
    public const string BadRequest = "bad_request";
    public const string Busy = "busy";
    /// <summary>2026-10-27, ревью С-1: скан старше допустимого (телефон прислал время скана, прошло больше 30 с) — в чек не добавлен.</summary>
    public const string Stale = "stale";
}

/// <summary>Разобранный запрос телефона: одна строка JSON {v, op, phone, ts, nonce, body, sig}. <see cref="Body"/> — СТРОКА
/// (JSON внутри строки): подпись считается по её UTF-8 байтам как есть.</summary>
public sealed class PhoneLinkRequest
{
    public int V { get; init; }
    public string Op { get; init; } = "";
    public string Phone { get; init; } = "";
    public long Ts { get; init; }
    public string Nonce { get; init; } = "";
    public string Body { get; init; } = "";
    public string Sig { get; init; } = "";
}

/// <summary>Запрос, прошедший проверку подписи, времени и повтора: его получают обработчики операций (scan и т. д.).</summary>
public sealed class PhoneLinkCall
{
    public string Op { get; init; } = "";
    public string Phone { get; init; } = "";
    /// <summary>Имя телефона из сопряжения (может быть пустым).</summary>
    public string PhoneName { get; init; } = "";
    /// <summary>Тело как строка (то, что подписано).</summary>
    public string BodyText { get; init; } = "";
    /// <summary>Тело как JSON-объект; пустой объект, если тело пустое. Не JSON-объект — запрос отклоняется до обработчика.</summary>
    public JsonObject Body { get; init; } = new();
    public string RemoteAddress { get; init; } = "";
}

/// <summary>Ответ кассы: {"ok":bool,"code":"…","msg":"…", …доп. поля}.</summary>
public sealed class PhoneLinkReply
{
    public bool Ok { get; init; }
    public string Code { get; init; } = PhoneLinkCodes.Ok;
    public string? Msg { get; init; }
    public JsonObject? Extra { get; init; }
    /// <summary>2026-10-27, ревью Ф-4: подпись ответа (см. <see cref="PhoneLinkProtocol.SignReply"/>); null — ответ без подписи
    /// (запрос не прошёл проверку подписи — ключ неизвестен или неверен).</summary>
    public string? Sig { get; init; }

    public PhoneLinkReply WithSig(string sig) => new() { Ok = Ok, Code = Code, Msg = Msg, Extra = Extra, Sig = sig };

    public static PhoneLinkReply Success(string code = PhoneLinkCodes.Ok, string? msg = null, JsonObject? extra = null) =>
        new() { Ok = true, Code = code, Msg = msg, Extra = extra };

    public static PhoneLinkReply Fail(string code, string? msg = null, JsonObject? extra = null) =>
        new() { Ok = false, Code = code, Msg = msg, Extra = extra };
}

/// <summary>Сведения о кассе для ответа на hello.</summary>
public sealed record PhoneLinkHostInfo(string Kassa, string Company8, bool ShiftOpen);

/// <summary>2026-10-27, К-П2: формат сообщений, подпись и вспомогательные функции протокола v1.
/// Подпись: HMAC-SHA256(key, UTF8("1|"+op+"|"+phone+"|"+ts+"|"+nonce+"|"+sha256hex(UTF8(body)))), key — 32 байта из QR.
/// Ключи и подписи в журнал не пишутся.</summary>
public static class PhoneLinkProtocol
{
    public const int Version = 1;
    public const int FirstPort = 47830;
    public const int LastPort = 47839;
    /// <summary>Строка запроса (без перевода строки) — не больше 64 КБ.</summary>
    public const int MaxLineBytes = 64 * 1024;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);
    // 2026-10-27, ревью Ф-6: окно запоминания nonce должно быть не короче 2 × допустимый сдвиг часов (запрос с ts «на 10 минут вперёд»
    // принимается ещё 10 минут после ts, то есть до 20 минут после отправки) — раньше 15 мин: 5 минут повтор был возможен.
    // Ёмкость 1000 общая на все телефоны — поднята, чтобы один телефон не вытеснял чужие записи.
    public static readonly TimeSpan NonceWindow = TimeSpan.FromMinutes(25);
    public const int NonceCapacity = 5000;
    /// <summary>2026-10-27, ревью Ф-4: версия формулы подписи ответа (поле ответа «sv»).</summary>
    public const int ReplySigVersion = 1;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(5);
    public const int KeyBytes = 32;

    private static readonly JsonSerializerOptions ReplyJson = new()
    {
        // Русский текст — как есть (UTF-8), а не \uXXXX: короче и читаемо; JSON от этого не меняется.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    // ------------------------------------------------------------------ base64url

    public static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryBase64UrlDecode(string? text, out byte[] data)
    {
        data = Array.Empty<byte>();
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var c in text)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
                return false;
        }
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            data = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string NewKey() => Base64UrlEncode(RandomNumberGenerator.GetBytes(KeyBytes));

    // ------------------------------------------------------------------ подпись

    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Что подписывается: «1|op|phone|ts|nonce|sha256hex(body)».</summary>
    public static string SigningString(string op, string phone, long ts, string nonce, string body) =>
        string.Concat("1|", op, "|", phone, "|", ts.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", nonce, "|", Sha256Hex(body));

    public static string Sign(byte[] key, string op, string phone, long ts, string nonce, string body) =>
        Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(SigningString(op, phone, ts, nonce, body))));

    /// <summary>Сравнение подписи за постоянное время.</summary>
    public static bool VerifySignature(byte[] key, PhoneLinkRequest request)
    {
        if (!TryBase64UrlDecode(request.Sig, out var given))
            return false;
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(SigningString(request.Op, request.Phone, request.Ts, request.Nonce, request.Body)));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    // ------------------------------------------------------------------ подпись ответа (2026-10-27, ревью Ф-4)

    /// <summary>Хэш «лишних» полей ответа (всё, кроме ok, code, msg, sig, sv): ключи по порядку (ordinal), строка «ключ=значение»
    /// на каждое поле через перевод строки (LF, 0x0A), значение — строка как есть, true/false, целое десятичным числом. Другие типы в подписанных
    /// ответах не используются. Пусто — sha256 пустой строки.</summary>
    public static string ExtraDigest(JsonObject? extra)
    {
        var lines = new List<string>();
        if (extra is not null)
        {
            foreach (var (key, value) in extra.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (key is "ok" or "code" or "msg" or "sig" or "sv")
                    continue;
                lines.Add(key + "=" + CanonicalValue(value));
            }
        }
        return Sha256Hex(string.Join("\n", lines));
    }

    private static string CanonicalValue(JsonNode? node)
    {
        if (node is not JsonValue v)
            return "";
        if (v.TryGetValue<string>(out var text))
            return text;
        if (v.TryGetValue<bool>(out var b))
            return b ? "true" : "false";
        if (v.TryGetValue<long>(out var l))
            return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToJsonString();
    }

    /// <summary>Что подписывается в ответе: «1r|op|phone|nonce|ok(1/0)|code|sha256hex(лишние поля)»; op, phone, nonce — из запроса.</summary>
    public static string ReplySigningString(string op, string phone, string nonce, PhoneLinkReply reply) =>
        string.Concat("1r|", op, "|", phone, "|", nonce, "|", reply.Ok ? "1" : "0", "|", reply.Code, "|", ExtraDigest(reply.Extra));

    /// <summary>Подпись ответа тем же ключом телефона, что и запрос: base64url(HMAC-SHA256(key, ReplySigningString)).</summary>
    public static string SignReply(byte[] key, string op, string phone, string nonce, PhoneLinkReply reply) =>
        Base64UrlEncode(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(ReplySigningString(op, phone, nonce, reply))));

    // ------------------------------------------------------------------ разбор запроса

    /// <summary>Разбор строки запроса. error — код ответа (bad_request), если формат неверный.</summary>
    public static bool TryParseRequest(string line, out PhoneLinkRequest? request, out string? error)
    {
        request = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Bad(out error);

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version)
                return Bad(out error);
            if (!TryString(root, "op", out var op) || op.Length == 0 || op.Length > 32)
                return Bad(out error);
            if (!TryString(root, "phone", out var phone) || phone.Length < 8 || phone.Length > 64 || phone.Any(char.IsControl) || phone.Contains('|'))
                return Bad(out error);
            if (!root.TryGetProperty("ts", out var tsEl) || tsEl.ValueKind != JsonValueKind.Number || !tsEl.TryGetInt64(out var ts))
                return Bad(out error);
            if (!TryString(root, "nonce", out var nonce) || nonce.Length < 16 || nonce.Length > 64 || !TryBase64UrlDecode(nonce, out _))
                return Bad(out error);
            // body — именно строка (JSON внутри строки), иначе хэш для подписи неоднозначен.
            if (!TryString(root, "body", out var body))
                return Bad(out error);
            if (!TryString(root, "sig", out var sig) || sig.Length == 0 || sig.Length > 128)
                return Bad(out error);

            request = new PhoneLinkRequest { V = version, Op = op, Phone = phone, Ts = ts, Nonce = nonce, Body = body, Sig = sig };
            return true;
        }
        catch (JsonException)
        {
            return Bad(out error);
        }

        static bool Bad(out string? e)
        {
            e = PhoneLinkCodes.BadRequest;
            return false;
        }

        static bool TryString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
                return false;
            value = el.GetString() ?? "";
            return true;
        }
    }

    /// <summary>Тело операции (строка) как JSON-объект. Пустая строка — пустой объект. Не объект — null.</summary>
    public static JsonObject? ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ ответ

    /// <summary>Ответ одной строкой JSON (без перевода строки в конце).</summary>
    public static string BuildReply(PhoneLinkReply reply)
    {
        var obj = new JsonObject { ["ok"] = reply.Ok, ["code"] = reply.Code };
        if (!string.IsNullOrWhiteSpace(reply.Msg))
            obj["msg"] = reply.Msg;
        if (reply.Extra is not null)
        {
            foreach (var (key, value) in reply.Extra)
            {
                if (key is "ok" or "code" or "msg")
                    continue;
                if (key is "sig" or "sv")
                    continue;
                obj[key] = value?.DeepClone();
            }
        }
        // 2026-10-27, ревью Ф-4: подпись ответа — необязательное поле (старые телефоны его просто не читают).
        if (!string.IsNullOrEmpty(reply.Sig))
        {
            obj["sv"] = ReplySigVersion;
            obj["sig"] = reply.Sig;
        }
        return obj.ToJsonString(ReplyJson);
    }

    // ------------------------------------------------------------------ QR

    /// <summary>8 hex-символов SHA-256 от идентификатора компании (по нему телефон узнаёт «ту же компанию»).</summary>
    public static string CompanyHash8(string? companyId) =>
        Sha256Hex(companyId ?? "").Substring(0, 8);

    /// <summary>Строка для QR: nurmarket-link:v1?d=…&amp;h=…&amp;p=…&amp;k=…&amp;n=…&amp;c=…</summary>
    public static string BuildQr(string deviceId, string ipv4, int port, string keyBase64Url, string kassaName, string company8) =>
        "nurmarket-link:v1?d=" + Uri.EscapeDataString(deviceId)
        + "&h=" + Uri.EscapeDataString(ipv4)
        + "&p=" + port.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "&k=" + keyBase64Url
        + "&n=" + Uri.EscapeDataString(kassaName)
        + "&c=" + company8;
}

/// <summary>2026-10-27, К-П2: защита от повтора — последние 1000 значений nonce за 15 минут (ключ — телефон + nonce).</summary>
public sealed class PhoneLinkNonceCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Key, DateTime At)> _order = new();

    /// <summary>true — значение новое (запомнено); false — повтор.</summary>
    public bool TryRegister(string phone, string nonce, DateTime nowUtc)
    {
        var key = phone + "|" + nonce;
        lock (_gate)
        {
            Prune(nowUtc);
            if (_seen.ContainsKey(key))
                return false;
            _seen[key] = nowUtc;
            _order.Enqueue((key, nowUtc));
            while (_order.Count > PhoneLinkProtocol.NonceCapacity)
                _seen.Remove(_order.Dequeue().Key);
            return true;
        }
    }

    private void Prune(DateTime nowUtc)
    {
        while (_order.Count > 0 && nowUtc - _order.Peek().At > PhoneLinkProtocol.NonceWindow)
            _seen.Remove(_order.Dequeue().Key);
    }
}
