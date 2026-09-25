using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NurSupportBot.Telegram;

/// <summary>Тонкий клиент Telegram Bot API: только те методы, что нужны боту. Без сторонней
/// библиотеки — у неё API меняется от версии к версии, а здесь десяток простых JSON-вызовов.</summary>
public sealed class TgClient
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public TgClient(string token, string apiUrl = "https://api.telegram.org/")
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"{apiUrl.TrimEnd('/')}/bot{token}/"),
            Timeout = TimeSpan.FromSeconds(90),
        };
    }

    /// <summary>Вызов метода Bot API. Ошибку Telegram бросает как <see cref="TgException"/>.</summary>
    public async Task<JsonNode?> CallAsync(string method, object args, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(method, args, Json, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(body);
        if (node?["ok"]?.GetValue<bool>() != true)
            throw new TgException(method, (int)resp.StatusCode, node?["description"]?.GetValue<string>() ?? body,
                node?["parameters"]?["retry_after"]?.GetValue<int>());
        return node["result"];
    }

    public async Task<List<Update>> GetUpdatesAsync(long offset, CancellationToken ct)
    {
        var result = await CallAsync("getUpdates", new
        {
            offset,
            timeout = 50,
            allowed_updates = new[] { "message", "callback_query" },
        }, ct).ConfigureAwait(false);
        return result?.Deserialize<List<Update>>(Json) ?? new();
    }

    public async Task<Message?> SendMessageAsync(long chatId, string html, InlineKeyboard? keyboard = null,
        long? replyTo = null, CancellationToken ct = default)
    {
        var result = await CallAsync("sendMessage", new
        {
            chat_id = chatId,
            text = html,
            parse_mode = "HTML",
            link_preview_options = new { is_disabled = true },
            reply_markup = keyboard,
            reply_parameters = replyTo is { } r ? new { message_id = r, allow_sending_without_reply = true } : null,
        }, ct).ConfigureAwait(false);
        return result?.Deserialize<Message>(Json);
    }

    public Task EditMessageAsync(long chatId, long messageId, string html, InlineKeyboard? keyboard = null,
        CancellationToken ct = default) =>
        CallAsync("editMessageText", new
        {
            chat_id = chatId,
            message_id = messageId,
            text = html,
            parse_mode = "HTML",
            link_preview_options = new { is_disabled = true },
            reply_markup = keyboard,
        }, ct);

    /// <summary>Видео, гифка, фото или файл по file_id, который Telegram выдал при загрузке.</summary>
    public async Task<Message?> SendMediaAsync(long chatId, string kind, string fileId, string? captionHtml,
        InlineKeyboard? keyboard = null, CancellationToken ct = default)
    {
        var (method, field) = kind switch
        {
            "video" => ("sendVideo", "video"),
            "animation" => ("sendAnimation", "animation"),
            "photo" => ("sendPhoto", "photo"),
            _ => ("sendDocument", "document"),
        };
        var args = new JsonObject
        {
            ["chat_id"] = chatId,
            [field] = fileId,
            ["parse_mode"] = "HTML",
        };
        if (!string.IsNullOrEmpty(captionHtml))
            args["caption"] = captionHtml;
        if (keyboard != null)
            args["reply_markup"] = JsonSerializer.SerializeToNode(keyboard, Json);
        var result = await CallAsync(method, args, ct).ConfigureAwait(false);
        return result?.Deserialize<Message>(Json);
    }

    /// <summary>Копия сообщения (текст, фото, видео, файл) в другой чат; возвращает id копии.</summary>
    public async Task<long> CopyMessageAsync(long toChat, long fromChat, long messageId, long? replyTo = null,
        InlineKeyboard? keyboard = null, CancellationToken ct = default)
    {
        var result = await CallAsync("copyMessage", new
        {
            chat_id = toChat,
            from_chat_id = fromChat,
            message_id = messageId,
            reply_markup = keyboard,
            reply_parameters = replyTo is { } r ? new { message_id = r, allow_sending_without_reply = true } : null,
        }, ct).ConfigureAwait(false);
        return result?["message_id"]?.GetValue<long>() ?? 0;
    }

    public Task AnswerCallbackAsync(string callbackId, string? text = null, CancellationToken ct = default) =>
        CallAsync("answerCallbackQuery", new { callback_query_id = callbackId, text }, ct);

    public async Task DeleteMessageAsync(long chatId, long messageId, CancellationToken ct = default)
    {
        try
        {
            await CallAsync("deleteMessage", new { chat_id = chatId, message_id = messageId }, ct).ConfigureAwait(false);
        }
        catch (TgException)
        {
            // Сообщение уже удалено или старше 48 часов — не важно.
        }
    }

    public Task SetCommandsAsync(IEnumerable<(string Command, string Description)> commands, CancellationToken ct = default) =>
        CallAsync("setMyCommands", new
        {
            commands = commands.Select(c => new { command = c.Command, description = c.Description }).ToArray(),
        }, ct);

    public async Task<User?> GetMeAsync(CancellationToken ct = default) =>
        (await CallAsync("getMe", new { }, ct).ConfigureAwait(false))?.Deserialize<User>(Json);

    /// <summary>Файл из памяти — выгрузка базы инструкций администратору.</summary>
    public async Task SendDocumentBytesAsync(long chatId, string fileName, byte[] content, string? caption, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id" },
            { new ByteArrayContent(content), "document", fileName },
        };
        if (!string.IsNullOrEmpty(caption))
            form.Add(new StringContent(caption), "caption");
        using var resp = await _http.PostAsync("sendDocument", form, ct).ConfigureAwait(false);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (node?["ok"]?.GetValue<bool>() != true)
            throw new TgException("sendDocument", (int)resp.StatusCode, node?["description"]?.GetValue<string>() ?? "", null);
    }

    /// <summary>Скачивает файл, присланный боту (загрузка базы инструкций администратором).</summary>
    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken ct = default)
    {
        var info = await CallAsync("getFile", new { file_id = fileId }, ct).ConfigureAwait(false);
        var path = info?["file_path"]?.GetValue<string>() ?? throw new TgException("getFile", 0, "нет пути к файлу", null);
        var fileUrl = _http.BaseAddress!.ToString().Replace("/bot", "/file/bot") + path;
        return await _http.GetByteArrayAsync(fileUrl, ct).ConfigureAwait(false);
    }
}

public sealed class TgException : Exception
{
    public TgException(string method, int status, string description, int? retryAfter)
        : base($"{method}: {status} {description}")
    {
        Status = status;
        Description = description;
        RetryAfter = retryAfter;
    }

    public int Status { get; }
    public string Description { get; }
    public int? RetryAfter { get; }
}

public sealed class InlineKeyboard
{
    [JsonPropertyName("inline_keyboard")]
    public List<List<Button>> Rows { get; } = new();

    public InlineKeyboard Row(params Button[] buttons)
    {
        var row = buttons.Where(b => b != null).ToList();
        if (row.Count > 0)
            Rows.Add(row);
        return this;
    }
}

public sealed class Button
{
    public string Text { get; set; } = "";
    public string? CallbackData { get; set; }
    public string? Url { get; set; }

    public static Button Cb(string text, string data) => new() { Text = text, CallbackData = data };
    public static Button Link(string text, string url) => new() { Text = text, Url = url };
}

public sealed class Update
{
    public long UpdateId { get; set; }
    public Message? Message { get; set; }
    public CallbackQuery? CallbackQuery { get; set; }
}

public sealed class Message
{
    public long MessageId { get; set; }
    public Chat Chat { get; set; } = new();
    public User? From { get; set; }
    public string? Text { get; set; }
    public string? Caption { get; set; }
    public List<PhotoSize>? Photo { get; set; }
    public FileRef? Video { get; set; }
    public FileRef? Animation { get; set; }
    public FileRef? Document { get; set; }
    public FileRef? VideoNote { get; set; }
    public FileRef? Voice { get; set; }
    public Message? ReplyToMessage { get; set; }

    [JsonIgnore]
    public bool HasMedia => Photo is { Count: > 0 } || Video != null || Animation != null || Document != null
        || VideoNote != null || Voice != null;
}

public sealed class Chat
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string? Title { get; set; }
}

public sealed class User
{
    public long Id { get; set; }
    public bool IsBot { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Username { get; set; }

    [JsonIgnore]
    public string DisplayName => string.Join(" ", new[] { FirstName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class CallbackQuery
{
    public string Id { get; set; } = "";
    public User From { get; set; } = new();
    public Message? Message { get; set; }
    public string? Data { get; set; }
}

public sealed class PhotoSize
{
    public string FileId { get; set; } = "";
    public int Width { get; set; }
}

public sealed class FileRef
{
    public string FileId { get; set; } = "";
    public string? FileName { get; set; }
}
