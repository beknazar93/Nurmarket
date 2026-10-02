using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Настройки бота на сервере NurCRM (GET /api/main/telegram-bot/settings/).</summary>
public sealed record ServerBotSettings(
    string Mode,
    string? BotUsername,
    bool TokenSet,
    bool WebhookOk,
    string? WebhookError,
    string? OwnerChatId,
    string? OwnerChatTitle,
    bool ShiftSummaryEnabled,
    bool CommandsEnabled,
    bool AiEnabled,
    bool AiKeySet,
    bool ConsultantEnabled,
    bool VoiceRepliesEnabled,
    bool HealthReported = false,
    DateTimeOffset? LastUpdateAt = null,
    DateTimeOffset? LastReplyAt = null,
    bool RentalReminders = false)
{
    /// <summary>Бот работает на сервере: токен у сервера и режим «server» — касса Telegram не опрашивает.</summary>
    public bool IsServerMode => TokenSet && string.Equals(Mode, "server", StringComparison.OrdinalIgnoreCase);

    /// <summary>2026-10-01: сервер принял сообщения, но не отвечает на них больше 2 минут (01.10 вебхук
    /// принимал сообщения, а обработчик очереди молчал — ТЗ часть 7, раздел 1). Видно только
    /// когда сервер отдаёт last_update_at / last_reply_at.</summary>
    public bool IsStuck => IsServerMode && LastUpdateAt is { } u && u - (LastReplyAt ?? DateTimeOffset.MinValue) > TimeSpan.FromMinutes(2)
        && DateTimeOffset.UtcNow - u > TimeSpan.FromMinutes(2);
}

/// <summary>Сводка обращений к боту на сервере (GET /api/main/telegram-bot/stats/).</summary>
public sealed record ServerBotStats(int Messages, int People, int Orders, double OrdersTotal);

/// <summary>Одно обращение покупателя к боту на сервере (GET /api/main/telegram-bot/inquiries/).</summary>
public sealed record ServerBotInquiry(DateTimeOffset? At, string ChatId, string Name, string Username, string Text, string Reply, bool IsVoice, string? OrderNumber, double? OrderTotal);

/// <summary>Покупатель бота на сервере (GET /api/main/telegram-bot/customers/).</summary>
public sealed record ServerBotCustomer(string ChatId, string Name, string Username, int Messages, int Orders, DateTimeOffset? LastAt);

/// <summary>2026-10-01, ТЗ часть 5 сделана бэкендом, владелец: «займись ботом, чтобы он работал всегда;
/// ИИ — на сервер». Бот на сервере NurCRM: вебхук Telegram + очередь, отвечает круглые сутки, даже
/// когда касса и программа владельца выключены. Здесь — эндпоинты из ТЗ: настройки (токен и ключ ИИ
/// только на запись), получатель, пробные сообщения, статистика и обращения.
///
/// <see cref="LastKnownServerMode"/> — последнее известное состояние: опрос Telegram в программе
/// (TelegramBotPollingService) при «server» выключается (Telegram и не даст getUpdates при вебхуке),
/// а сводку смены шлёт сервер — касса её не дублирует.</summary>
public sealed class ServerTelegramBotApi
{
    private const string Root = "api/main/telegram-bot/";
    private readonly NurMarketApiClient _api;

    public ServerTelegramBotApi(NurMarketApiClient api)
    {
        _api = api;
        Current = this;
    }

    /// <summary>Экземпляр из DI — для сервисов, которые создаются не через DI (опрос бота).</summary>
    public static ServerTelegramBotApi? Current { get; private set; }

    /// <summary>null — ещё не спрашивали (или сервер без бота).</summary>
    public static bool? LastKnownServerMode { get; private set; }

    /// <summary>2026-10-02: бот на сервере сам напоминает о сроке проката — программа не дублирует.</summary>
    public static bool LastKnownRentalReminders { get; private set; }

    /// <summary>Режим, узнанный у самого Telegram (getUpdates отказал из-за вебхука или снова
    /// работает) — без запроса к NurCRM.</summary>
    public static void NoteModeFromTelegram(bool serverMode) => LastKnownServerMode = serverMode;

    public async Task<ServerBotSettings?> GetSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, Root + "settings/", null, null, ct).ConfigureAwait(false);
            var settings = Parse(data);
            LastKnownServerMode = settings?.IsServerMode;
            LastKnownRentalReminders = settings is { IsServerMode: true, RentalReminders: true };
            return settings;
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // Старый сервер без бота — работаем, как раньше (опрос в программе).
            LastKnownServerMode = false;
            return null;
        }
    }

    /// <summary>PATCH настроек. Токен и ключ ИИ — только на запись; пустая строка — удалить.
    /// Ошибку сервера (например, «Telegram: Unauthorized» на неверный токен) — текстом в исключении.</summary>
    public async Task<ServerBotSettings?> PatchSettingsAsync(IReadOnlyDictionary<string, object?> body, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Patch, Root + "settings/", body, null, ct).ConfigureAwait(false);
        var settings = Parse(data);
        if (settings is not null)
            LastKnownServerMode = settings.IsServerMode;
        return settings;
    }

    /// <summary>Перенести бота этого компьютера на сервер: токен, получатель, ключ ИИ и переключатели
    /// из настроек программы, режим «server» (сервер сам ставит вебхук). Общий путь для кнопки в мастере
    /// бота и для вопроса после обновления (ServerBotOffer).</summary>
    public Task<ServerBotSettings?> MoveCurrentBotToServerAsync(CancellationToken ct = default)
    {
        var prefs = UserPreferences.Instance;
        if (string.IsNullOrWhiteSpace(prefs.TelegramBotToken))
            throw new InvalidOperationException("Бот не подключён.");
        var body = new Dictionary<string, object?>
        {
            ["mode"] = "server",
            ["token"] = prefs.TelegramBotToken,
            ["shift_summary_enabled"] = prefs.TelegramShiftSummaryEnabled,
            ["commands_enabled"] = prefs.TelegramCommandsEnabled,
            ["consultant_enabled"] = true,
            ["voice_replies_enabled"] = true,
            ["ai_enabled"] = !string.IsNullOrWhiteSpace(prefs.TelegramAiKey),
        };
        if (!string.IsNullOrWhiteSpace(prefs.TelegramChatId))
            body["owner_chat_id"] = prefs.TelegramChatId;
        if (!string.IsNullOrWhiteSpace(prefs.TelegramAiKey))
            body["ai_key"] = prefs.TelegramAiKey;
        return PatchSettingsAsync(body, ct);
    }

    public Task<JsonElement> DetectOwnerChatAsync(CancellationToken ct = default) =>
        _api.RequestAsync(HttpMethod.Post, Root + "detect-owner-chat/", new Dictionary<string, object?>(), null, ct);

    public Task<JsonElement> TestMessageAsync(CancellationToken ct = default) =>
        _api.RequestAsync(HttpMethod.Post, Root + "test-message/", new Dictionary<string, object?>(), null, ct);

    public Task<JsonElement> TestAiAsync(CancellationToken ct = default) =>
        _api.RequestAsync(HttpMethod.Post, Root + "test-ai/", new Dictionary<string, object?>(), null, ct, TimeSpan.FromSeconds(60));

    public async Task<ServerBotStats> GetStatsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "stats/", null, Period(from, to), ct).ConfigureAwait(false);
        return new ServerBotStats(Int(data, "messages"), Int(data, "people"), Int(data, "orders"), Num(data, "orders_total"));
    }

    public async Task<IReadOnlyList<ServerBotInquiry>> GetInquiriesAsync(DateTime from, DateTime to, int pageSize = 200, CancellationToken ct = default)
    {
        var query = Period(from, to);
        query["page_size"] = pageSize.ToString(CultureInfo.InvariantCulture);
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "inquiries/", null, query, ct).ConfigureAwait(false);
        var list = new List<ServerBotInquiry>();
        foreach (var r in Rows(data))
        {
            string? number = null;
            double? total = null;
            if (r.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Object)
            {
                number = Str(order, "number");
                total = Num(order, "total");
            }
            list.Add(new ServerBotInquiry(Date(r, "created_at"), Str(r, "chat_id") ?? "", Str(r, "name") ?? "", Str(r, "username") ?? "",
                Str(r, "text") ?? "", Str(r, "reply") ?? "", r.TryGetProperty("is_voice", out var v) && v.ValueKind == JsonValueKind.True, number, total));
        }
        return list;
    }

    public async Task<IReadOnlyList<ServerBotCustomer>> GetCustomersAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "customers/", null, Period(from, to), ct).ConfigureAwait(false);
        return Rows(data).Select(r => new ServerBotCustomer(Str(r, "chat_id") ?? "", Str(r, "name") ?? "", Str(r, "username") ?? "",
            Int(r, "messages"), Int(r, "orders"), Date(r, "last_at"))).ToList();
    }

    /// <summary>Текст ошибки сервера из ApiException (поле token / detail), без токенов.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is ApiException { Payload: { ValueKind: JsonValueKind.Object } payload })
        {
            foreach (var key in new[] { "token", "ai_key", "detail", "non_field_errors" })
            {
                if (!payload.TryGetProperty(key, out var v))
                    continue;
                if (v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? ex.Message;
                if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                    return v[0].ToString();
            }
        }
        return SensitiveDataRedactor.Redact(ex.Message);
    }

    private static ServerBotSettings? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object)
            return null;
        return new ServerBotSettings(
            Str(d, "mode") ?? "",
            Str(d, "bot_username"),
            Bool(d, "token_set"),
            Bool(d, "webhook_ok"),
            Str(d, "webhook_error"),
            Str(d, "owner_chat_id"),
            Str(d, "owner_chat_title"),
            Bool(d, "shift_summary_enabled"),
            Bool(d, "commands_enabled"),
            Bool(d, "ai_enabled"),
            Bool(d, "ai_key_set"),
            Bool(d, "consultant_enabled"),
            Bool(d, "voice_replies_enabled"),
            d.TryGetProperty("last_reply_at", out _),
            Date(d, "last_update_at"),
            Date(d, "last_reply_at"),
            // 2026-10-02 (ТЗ часть 7, п. 4.5): сервер сам шлёт напоминания о сроке проката.
            Bool(d, "rental_reminders"));
    }

    private static Dictionary<string, string> Period(DateTime from, DateTime to) => new()
    {
        ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
            return r.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                _ => 0,
            }
            : 0;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
