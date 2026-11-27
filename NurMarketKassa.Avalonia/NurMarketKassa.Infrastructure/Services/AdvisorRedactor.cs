using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-11-27 (Нур-советник: доступ к разделам, правило безопасности «б»): что уходит в ИИ из инструментов советника — без секретов.
/// Поля-секреты (ключи API, токены, пароли, PIN, реквизиты карт, подписи) выбрасываются целиком; телефоны маскируются (видны 2 последние цифры);
/// закупочные цены, наценка и прибыль — только если они видны этому пользователю (<see cref="Options.PurchaseVisible"/>); в тексте ловятся ключи
/// Gemini/Groq/OpenRouter, JWT, токены Телеграм-бота, номера карт и телефонов. Списки — не длиннее <see cref="Options.MaxItems"/>.</summary>
public static class AdvisorRedactor
{
    public sealed record Options(bool PurchaseVisible, int MaxItems = 30, int MaxString = 300);

    /// <summary>Слова имён полей-секретов (имя поля бьётся на слова: snake_case, camelCase).</summary>
    private static readonly HashSet<string> SecretWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apikey", "token", "secret", "password", "passwd", "pass", "pwd", "pin", "pincode", "jwt", "cvv", "cvc",
        "auth", "authorization", "cookie", "otp", "hash", "salt", "credential", "credentials", "bearer", "keys", "tokens", "pins", "passwords",
    };

    /// <summary>Поля, где секрет — только строка (номер карты, IBAN, сессия, лицензия); число под таким именем — сумма («card»: 1500 — оплаты картой).</summary>
    private static readonly HashSet<string> SoftSecretWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "card", "cards", "pan", "iban", "session", "license", "signature", "refresh", "private", "account", "requisites", "bik",
    };

    /// <summary>Поля телефонов — значение маскируется.</summary>
    private static readonly HashSet<string> PhoneWords = new(StringComparer.OrdinalIgnoreCase) { "phone", "phones", "tel", "telephone", "mobile", "whatsapp", "msisdn" };

    /// <summary>Поля закупки и прибыли — только тем, кому они видны в программе.</summary>
    private static readonly HashSet<string> PurchaseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cost", "costs", "purchase", "purchases", "margin", "markup", "profit", "profits", "cogs", "netcost", "buy", "zakup",
    };

    /// <summary>Поля-коды (штрихкод, артикул, номер документа) — длинные цифры в них не телефоны и не карты.</summary>
    private static readonly HashSet<string> CodeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "barcode", "barcodes", "code", "codes", "sku", "article", "id", "ids", "number", "esf", "plu", "inn", "okpo", "serial", "part", "oem", "uuid",
    };

    internal static IReadOnlyList<string> Words(string name)
    {
        var words = new List<string>();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (!char.IsLetterOrDigit(ch))
            {
                Flush();
                continue;
            }
            if (char.IsUpper(ch) && sb.Length > 0 && !char.IsUpper(name[i - 1]))
                Flush();
            sb.Append(char.ToLowerInvariant(ch));
        }
        Flush();
        // Слитное имя целиком тоже проверяем («apikey», «pincode»).
        words.Add(Regex.Replace(name.ToLowerInvariant(), "[^a-zа-я0-9]", ""));
        return words;

        void Flush()
        {
            if (sb.Length > 0)
                words.Add(sb.ToString());
            sb.Clear();
        }
    }

    public static bool IsSecretField(string name) => Words(name).Any(SecretWords.Contains);

    /// <summary>Поле-секрет, если в нём строка или вложенный объект (номер карты, реквизиты счёта).</summary>
    public static bool IsSoftSecretField(string name) => Words(name).Any(SoftSecretWords.Contains);

    public static bool IsPhoneField(string name) => Words(name).Any(PhoneWords.Contains);

    public static bool IsPurchaseField(string name) => Words(name).Any(PurchaseWords.Contains)
                                                         || name.Contains("закуп", StringComparison.OrdinalIgnoreCase) || name.Contains("прибыл", StringComparison.OrdinalIgnoreCase)
                                                         || name.Contains("наценк", StringComparison.OrdinalIgnoreCase) || name.Contains("себестоим", StringComparison.OrdinalIgnoreCase);

    private static bool IsCodeField(string name) => Words(name).Any(CodeWords.Contains);

    /// <summary>Телефон: «+996 700 12 34 56», «0700 123 456», «996700123456» → «+996 *** ** *56» (видны 2 последние цифры).</summary>
    public static string MaskPhone(string? phone)
    {
        // Уже замаскирован (ответ проходит очистку дважды: данные и обёртка) — не трогаем.
        if (phone is not null && phone.Contains('*'))
            return phone;
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return "";
        return digits.Length <= 4 ? "***" : "*** ** *" + digits[^2..];
    }

    // Ключи и токены в свободном тексте.
    private static readonly Regex Secrets = new(
        @"AIza[0-9A-Za-z_\-]{20,}|gsk_[0-9A-Za-z]{16,}|sk-(or-)?[0-9A-Za-z_\-]{16,}|eyJ[0-9A-Za-z_\-]{8,}\.[0-9A-Za-z_\-]{8,}\.[0-9A-Za-z_\-]{8,}|\b\d{6,12}:[0-9A-Za-z_\-]{30,}\b|\bBearer\s+[0-9A-Za-z._\-]{12,}",
        RegexOptions.Compiled);

    // Телефон с «+» или киргизский «0XXX XXX XXX» / «996XXXXXXXXX».
    private static readonly Regex Phones = new(@"(?<![\d])(\+\d[\d\s\-()]{8,16}\d|996[\s\-]?\d{3}[\s\-]?\d{2,3}[\s\-]?\d{2,3}[\s\-]?\d{0,3}|0\d{3}[\s\-]?\d{3}[\s\-]?\d{3})(?![\d])", RegexOptions.Compiled);

    // Номер карты: 16–19 цифр (с пробелами/дефисами), проверка Луна.
    private static readonly Regex Cards = new(@"(?<!\d)(?:\d[ \-]?){15,18}\d(?!\d)", RegexOptions.Compiled);

    private static bool Luhn(string digits)
    {
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alt)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }
            sum += n;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    /// <summary>Свободный текст (сводки, описания): ключи → «[скрыто]», карты → «**** 1234», телефоны → маска.</summary>
    public static string RedactText(string? text, bool phones = true)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        var s = Secrets.Replace(text, "[скрыто]");
        s = Cards.Replace(s, m =>
        {
            var d = new string(m.Value.Where(char.IsDigit).ToArray());
            return d.Length is >= 16 and <= 19 && Luhn(d) ? "**** " + d[^4..] : m.Value;
        });
        if (phones)
            s = Phones.Replace(s, m => MaskPhone(m.Value));
        return s;
    }

    /// <summary>Очистить JSON перед отправкой в ИИ (копия; исходный узел не меняется).</summary>
    public static JsonNode? Redact(JsonNode? node, Options options) => RedactCore(node?.DeepClone(), options, null);

    private static JsonNode? RedactCore(JsonNode? node, Options o, string? field)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var soft = IsSoftSecretField(name) && obj[name] is not null && !(obj[name] is JsonValue jv && jv.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);
                    if (IsSecretField(name) || soft || (!o.PurchaseVisible && IsPurchaseField(name)))
                    {
                        obj.Remove(name);
                        continue;
                    }
                    if (IsPhoneField(name))
                    {
                        obj[name] = obj[name] is JsonValue v && v.TryGetValue<string>(out var phone) ? MaskPhone(phone) : null;
                        continue;
                    }
                    // Тот же узел (объект, число) обратно не присваиваем — у него уже есть родитель.
                    var child = obj[name];
                    var cleaned = RedactCore(child, o, name);
                    if (!ReferenceEquals(cleaned, child))
                        obj[name] = cleaned;
                }
                return obj;
            case JsonArray arr:
                var total = arr.Count;
                var keep = new JsonArray();
                foreach (var item in arr.Take(o.MaxItems).ToList())
                {
                    arr.Remove(item);
                    keep.Add(RedactCore(item, o, field));
                }
                if (total > o.MaxItems)
                    keep.Add(JsonValue.Create($"… ещё {total - o.MaxItems} (показаны первые {o.MaxItems})"));
                return keep;
            case JsonValue value when value.TryGetValue<string>(out var s):
                var clean = field is not null && IsCodeField(field) ? Secrets.Replace(s, "[скрыто]") : RedactText(s);
                if (clean.Length > o.MaxString)
                    clean = clean[..o.MaxString] + "…";
                return JsonValue.Create(clean);
            default:
                return node;
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        MaxDepth = 16,
    };

    /// <summary>Любой ответ провайдера (запись, список, JsonElement, текст) → JSON-узел.</summary>
    public static JsonNode? ToNode(object? data) => data switch
    {
        null => null,
        JsonNode n => n,
        string s => JsonValue.Create(s),
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        _ => JsonSerializer.SerializeToNode(data, data.GetType(), Json),
    };

    /// <summary>Компактный JSON не длиннее <paramref name="maxChars"/>: сначала укорачиваются списки и строки, в крайнем случае — обрезка с пометкой.</summary>
    public static string Compact(object? data, bool purchaseVisible, int maxChars)
    {
        JsonNode? raw;
        try
        {
            raw = ToNode(data);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            raw = JsonValue.Create(Convert.ToString(data, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }
        foreach (var (items, chars) in new[] { (30, 300), (15, 200), (8, 120), (4, 80) })
        {
            var text = Redact(raw, new Options(purchaseVisible, items, chars))?.ToJsonString(Json) ?? "null";
            if (text.Length <= maxChars)
                return text;
        }
        var last = Redact(raw, new Options(purchaseVisible, 3, 60))?.ToJsonString(Json) ?? "null";
        return last.Length <= maxChars ? last : JsonSerializer.Serialize(last[..Math.Max(0, maxChars - 40)] + "…(обрезано)", Json);
    }
}
