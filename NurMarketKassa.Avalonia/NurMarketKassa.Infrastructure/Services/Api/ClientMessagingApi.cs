using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-08, ответ сервера Умара (п. 31, Б-30): согласия клиента на сообщения бота (GET/PATCH main/clients/{id}/consents/).
/// Оба согласия у всех прежних клиентов = false: сервер им не пишет, пока клиент не нажмёт /start в боте или кассир не включит согласие.
/// Поля могут отсутствовать или быть null — это «не включено».</summary>
public sealed record ClientConsents(bool Service, bool Marketing, DateTimeOffset? UpdatedAt, string? Source, string Language)
{
    public static readonly string[] Languages = { "ru", "ky", "en", "tr", "uz" };

    public static ClientConsents Empty { get; } = new(false, false, null, null, "");

    /// <summary>Терпимый разбор: {"consents":{"service","marketing","updated_at","source"},"language":"ru"}; допускается и плоский вид.</summary>
    public static ClientConsents Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Empty;
        var box = root.TryGetProperty("consents", out var c) && c.ValueKind == JsonValueKind.Object ? c : root;
        var lang = Str(root, "language") ?? "";
        return new ClientConsents(Flag(box, "service"), Flag(box, "marketing"), Date(box, "updated_at"), Str(box, "source"),
            Languages.Contains(lang, StringComparer.OrdinalIgnoreCase) ? lang.ToLowerInvariant() : "");
    }

    /// <summary>Тело PATCH только с изменёнными полями; null — менять нечего.</summary>
    public static Dictionary<string, object?>? BuildPatch(ClientConsents before, ClientConsents after)
    {
        var body = new Dictionary<string, object?>();
        var consents = new Dictionary<string, object?>();
        if (before.Service != after.Service)
            consents["service"] = after.Service;
        if (before.Marketing != after.Marketing)
            consents["marketing"] = after.Marketing;
        if (consents.Count > 0)
            body["consents"] = consents;
        if (!string.Equals(before.Language, after.Language, StringComparison.OrdinalIgnoreCase) && after.Language.Length > 0)
            body["language"] = after.Language;
        return body.Count == 0 ? null : body;
    }

    internal static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    internal static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>Один привязанный канал клиента (п. 35, Б-74: GET main/clients/{id}/channels/) — только для показа.</summary>
public sealed record ClientChannel(string Kind, string Title)
{
    public static IReadOnlyList<ClientChannel> ParseList(JsonElement root)
    {
        var list = new List<ClientChannel>();
        foreach (var r in ClientMessagingApi.Rows(root))
        {
            if (r.ValueKind != JsonValueKind.Object)
                continue;
            var kind = ClientConsents.Str(r, "channel") ?? ClientConsents.Str(r, "kind") ?? ClientConsents.Str(r, "type") ?? ClientConsents.Str(r, "provider") ?? "";
            var title = ClientConsents.Str(r, "display_name") ?? ClientConsents.Str(r, "name") ?? ClientConsents.Str(r, "username")
                ?? ClientConsents.Str(r, "title") ?? ClientConsents.Str(r, "external_id") ?? ClientConsents.Str(r, "chat_id") ?? "";
            if (title.Length > 0 && ClientConsents.Str(r, "username") is { Length: > 0 } u && title != u && !title.StartsWith('@'))
                title += " (@" + u.TrimStart('@') + ")";
            if (kind.Length == 0 && title.Length == 0)
                continue;
            list.Add(new ClientChannel(kind, title));
        }
        return list;
    }

    /// <summary>Человеческое имя канала: telegram → Telegram, whatsapp → WhatsApp.</summary>
    public string KindDisplay => Kind.ToLowerInvariant() switch
    {
        "telegram" or "tg" => "Telegram",
        "whatsapp" or "wa" => "WhatsApp",
        "instagram" or "ig" => "Instagram",
        "" => "—",
        var k => char.ToUpperInvariant(k[0]) + k[1..],
    };
}

/// <summary>Тихие часы сообщений покупателям (п. 31): PATCH users/company/ {"quiet_hours":{"from","to","tz"}}.
/// Сервер по умолчанию 21:00–09:00 Asia/Bishkek; если поля в ответе нет — показываем это значение как «по умолчанию».</summary>
public sealed record QuietHours(string From, string To, string Tz, bool IsDefault)
{
    public static QuietHours Default { get; } = new("21:00", "09:00", "Asia/Bishkek", true);

    public static readonly string[] TimeZones =
    {
        "Asia/Bishkek", "Asia/Almaty", "Asia/Tashkent", "Asia/Dushanbe", "Asia/Ashgabat", "Asia/Baku", "Asia/Yerevan", "Asia/Tbilisi",
        "Europe/Moscow", "Europe/Istanbul", "Europe/Kyiv", "Asia/Dubai", "Asia/Novosibirsk", "UTC",
    };

    /// <summary>"9:5" → "09:05"; мусор → null.</summary>
    public static string? NormalizeTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var p = text.Trim().Split(':');
        if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            return null;
        return h is >= 0 and <= 23 && m is >= 0 and <= 59 ? $"{h:00}:{m:00}" : null;
    }

    /// <summary>Из ответа GET users/company/: нет поля / не объект / битое время — значение по умолчанию.</summary>
    public static QuietHours FromCompany(JsonElement company)
    {
        if (company.ValueKind != JsonValueKind.Object || !company.TryGetProperty("quiet_hours", out var q) || q.ValueKind != JsonValueKind.Object)
            return Default;
        var from = NormalizeTime(ClientConsents.Str(q, "from"));
        var to = NormalizeTime(ClientConsents.Str(q, "to"));
        if (from is null || to is null)
            return Default;
        var tz = ClientConsents.Str(q, "tz");
        return new QuietHours(from, to, string.IsNullOrWhiteSpace(tz) ? Default.Tz : tz!, false);
    }

    public Dictionary<string, object?> ToPatchBody() => new()
    {
        ["quiet_hours"] = new Dictionary<string, object?> { ["from"] = From, ["to"] = To, ["tz"] = Tz },
    };
}

/// <summary>Строка журнала сообщений бота (GET main/telegram-bot/outbox/). Формат полей сервер не расписал — разбор терпим к неизвестным.</summary>
public sealed record OutboxMessage(DateTimeOffset? At, string ClientName, string Kind, string Status, string Text, string Reason, DateTimeOffset? SendAfter);

public sealed record OutboxPage(IReadOnlyList<OutboxMessage> Rows, bool HasNext, int? Count);

/// <summary>2026-11-08, п. 31 и п. 35 ответа сервера Умара: согласия клиента, каналы, тихие часы, журнал сообщений бота.
/// Ошибки — ApiException (403 — нет прав, 404 — сервер без метода); офлайн/сбой сети — прочие исключения (решает окно).</summary>
public sealed class ClientMessagingApi
{
    private readonly NurMarketApiClient _api;

    public ClientMessagingApi(NurMarketApiClient api) => _api = api;

    private static string ClientRoot(string clientId) => "api/main/clients/" + Uri.EscapeDataString(clientId) + "/";

    public async Task<ClientConsents> GetConsentsAsync(string clientId, CancellationToken ct = default) =>
        ClientConsents.Parse(await _api.RequestAsync(HttpMethod.Get, ClientRoot(clientId) + "consents/", null, null, ct).ConfigureAwait(false));

    /// <summary>Отправляет только изменённые поля; ничего не изменилось — запроса нет, возвращается прежнее. Ответ сервера (с source:"cashier") — новое состояние.</summary>
    public async Task<ClientConsents> PatchConsentsAsync(string clientId, ClientConsents before, ClientConsents after, CancellationToken ct = default)
    {
        var body = ClientConsents.BuildPatch(before, after);
        if (body is null)
            return before;
        var data = await _api.RequestAsync(HttpMethod.Patch, ClientRoot(clientId) + "consents/", body, null, ct).ConfigureAwait(false);
        var parsed = ClientConsents.Parse(data);
        // Сервер мог не вернуть тело — берём то, что отправили.
        return data.ValueKind == JsonValueKind.Object ? parsed : after with { Source = "cashier", UpdatedAt = DateTimeOffset.Now };
    }

    /// <summary>Каналы клиента. null — нет прав (403) или сервер без метода (404): блок прячем.</summary>
    public async Task<IReadOnlyList<ClientChannel>?> GetChannelsAsync(string clientId, CancellationToken ct = default)
    {
        try
        {
            return ClientChannel.ParseList(await _api.RequestAsync(HttpMethod.Get, ClientRoot(clientId) + "channels/", null, null, ct).ConfigureAwait(false));
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 404)
        {
            return null;
        }
    }

    public async Task<QuietHours> GetQuietHoursAsync(CancellationToken ct = default) =>
        QuietHours.FromCompany(await _api.RequestAsync(HttpMethod.Get, "api/users/company/", null, null, ct).ConfigureAwait(false));

    public Task<JsonElement> PatchQuietHoursAsync(QuietHours value, CancellationToken ct = default) =>
        _api.RequestAsync(HttpMethod.Patch, "api/users/company/", value.ToPatchBody(), null, ct);

    /// <summary>Журнал сообщений бота; пустые фильтры не отправляются. Постранично, если сервер отдаёт страницы (page, next).</summary>
    public async Task<OutboxPage> GetOutboxAsync(string? client, string? status, string? kind, int page = 1, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(client)) query["client"] = client.Trim();
        if (!string.IsNullOrWhiteSpace(status)) query["status"] = status.Trim();
        if (!string.IsNullOrWhiteSpace(kind)) query["kind"] = kind.Trim();
        if (page > 1) query["page"] = page.ToString(CultureInfo.InvariantCulture);
        var data = await _api.RequestAsync(HttpMethod.Get, "api/main/telegram-bot/outbox/", null, query, ct).ConfigureAwait(false);
        return ParseOutbox(data);
    }

    public static OutboxPage ParseOutbox(JsonElement data)
    {
        var rows = new List<OutboxMessage>();
        foreach (var r in Rows(data))
        {
            if (r.ValueKind != JsonValueKind.Object)
                continue;
            string name = "";
            if (r.TryGetProperty("client", out var c))
                name = c.ValueKind == JsonValueKind.Object ? ClientConsents.Str(c, "full_name") ?? ClientConsents.Str(c, "name") ?? ""
                    : c.ValueKind is JsonValueKind.String ? c.GetString() ?? "" : "";
            if (name.Length == 0)
                name = ClientConsents.Str(r, "client_name") ?? ClientConsents.Str(r, "chat_id") ?? "";
            var text = ClientConsents.Str(r, "text") ?? ClientConsents.Str(r, "body") ?? ClientConsents.Str(r, "message") ?? "";
            var reason = ClientConsents.Str(r, "reason") ?? ClientConsents.Str(r, "error") ?? ClientConsents.Str(r, "skip_reason") ?? ClientConsents.Str(r, "defer_reason") ?? "";
            rows.Add(new OutboxMessage(
                ClientConsents.Date(r, "created_at") ?? ClientConsents.Date(r, "sent_at") ?? ClientConsents.Date(r, "at"),
                name, ClientConsents.Str(r, "kind") ?? "", ClientConsents.Str(r, "status") ?? "", text, reason,
                ClientConsents.Date(r, "send_after") ?? ClientConsents.Date(r, "deferred_until") ?? ClientConsents.Date(r, "scheduled_at")));
        }
        var hasNext = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString());
        int? count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var cn) && cn.ValueKind == JsonValueKind.Number && cn.TryGetInt32(out var ci) ? ci : null;
        return new OutboxPage(rows, hasNext, count);
    }

    internal static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();
        if (data.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "results", "channels", "items" })
                if (data.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.Array)
                    return r.EnumerateArray();
        return Array.Empty<JsonElement>();
    }
}
