using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-05, владелец: «найди бесплатную модель, которая ищет в интернете», «можем установить OmniRoute и настроить
/// несколько моделей?» → «да, делай». Проверка на ключе владельца: у бесплатного ключа Gemini поиск Google — 429 на всех
/// 18 моделях. Вместо отдельного шлюза (OmniRoute — отдельный сервер, ставить у каждого клиента нельзя) — несколько
/// поставщиков прямо в программе, по OpenAI-совместимому API, с бесплатными ключами владельца:
/// <list type="bullet">
/// <item>Groq — <c>openai/gpt-oss-120b</c> со встроенным поиском <c>browser_search</c> (бесплатный тариф: 1000 запросов в день,
/// 8000 токенов в минуту — поэтому в поиск уходит только сам вопрос, без сводки магазина). Groq Compound отключён 21.09.2026.</item>
/// <item>OpenRouter — <c>openrouter/free</c> (сам выбирает свободную бесплатную модель, большой контекст; 50 запросов в день
/// без оплаты) — запасной ответ, когда у Gemini кончился лимит или модель перегружена. Поиск в интернете у OpenRouter платный.</item>
/// </list>
/// </summary>
public static class AiProviders
{
    private const string GroqUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string OpenRouterUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string GroqSearchModel = "openai/gpt-oss-120b";
    // 2026-10-05, владелец: «если лимит Groq, то OpenRouter не может поискать и поставить фото?» — у OpenRouter поиск платный;
    // зато у Groq лимит поминутный и свой у каждой модели: вторая модель с browser_search — запасная.
    private const string GroqSearchModelSmall = "openai/gpt-oss-20b";
    private const string GroqChatModel = "openai/gpt-oss-120b";
    private const string OpenRouterModel = "openrouter/free";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static DateTime _groqSearchBlockedUntilUtc = DateTime.MinValue;
    private static readonly Dictionary<string, DateTime> SearchModelBlockedUntilUtc = new();
    // Поиски по одному: два параллельных поиска фото за секунды съедали поминутный лимит Groq (8000 токенов).
    private static readonly SemaphoreSlim SearchGate = new(1, 1);
    private static readonly Dictionary<string, DateTime> ChatBlockedUntilUtc = new();

    // 2026-10-05: на тарифе «Старт» ИИ нет (TariffGate.CanUseAi).
    public static bool HasGroq => TariffGate.CanUseAi && !string.IsNullOrWhiteSpace(UserPreferences.Instance.GroqApiKey);
    public static bool HasOpenRouter => TariffGate.CanUseAi && !string.IsNullOrWhiteSpace(UserPreferences.Instance.OpenRouterApiKey);
    public static bool HasFallback => HasGroq || HasOpenRouter;
    private static readonly Dictionary<string, DateTime> ProviderCooldownUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsProviderBlocked(string provider)
    {
        lock (ProviderCooldownUntilUtc)
            return ProviderCooldownUntilUtc.TryGetValue(provider, out var until) && DateTime.UtcNow < until;
    }

    private static void BlockProvider(string provider, TimeSpan duration)
    {
        lock (ProviderCooldownUntilUtc)
            ProviderCooldownUntilUtc[provider] = DateTime.UtcNow + duration;
    }

    /// <summary>Поиск в интернете через Groq доступен (ключ есть, лимит не исчерпан).</summary>
    public static bool CanSearchWeb => HasGroq && DateTime.UtcNow >= _groqSearchBlockedUntilUtc && !IsProviderBlocked("GroqSearch");

    /// <summary>Найти в интернете: короткий ответ по-русски с найденным и ссылки на источники.</summary>
    public static async Task<(string? Text, IReadOnlyList<TelegramAiChat.WebSource> Sources, string? Error)> SearchWebAsync(string query, CancellationToken ct)
    {
        var none = (IReadOnlyList<TelegramAiChat.WebSource>)Array.Empty<TelegramAiChat.WebSource>();
        if (!CanSearchWeb)
            return (null, none, HasGroq ? "поиск в интернете через Groq временно недоступен (лимит)" : "нет ключа Groq");
        if (IsProviderBlocked("GroqSearch"))
            return (null, none, "поиск в интернете через Groq временно недоступен (краткая блокировка)");
        var body = new JsonObject
        {
            ["model"] = GroqSearchModel,
            ["messages"] = new JsonArray(
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "Ищи в интернете и отвечай по-русски кратко и по делу: факты, цифры, даты, названия магазинов и сайтов. "
                                  + "Указывай ссылки на источники. Если не нашёл — так и скажи, не выдумывай.",
                },
                new JsonObject { ["role"] = "user", ["content"] = query }),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "browser_search" }),
            ["tool_choice"] = "required",
            ["max_completion_tokens"] = 1500,
        };
        await SearchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? lastError = null;
            foreach (var model in new[] { GroqSearchModel, GroqSearchModelSmall })
            {
                lock (SearchModelBlockedUntilUtc)
                {
                    if (SearchModelBlockedUntilUtc.TryGetValue(model, out var until) && DateTime.UtcNow < until)
                        continue;
                }
                body["model"] = model;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    // 2026-10-06, стресс-тест: Groq не отвечал — поиск фото ждал 3 × 60 с. Не дольше 30 с на запрос.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var (json, status) = await PostAsync(GroqUrl, UserPreferences.Instance.GroqApiKey!, body, timeout.Token).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (status is >= 200 and < 300)
                    {
                        var (text, sources) = ReadAnswer(json);
                        PosLogger.Log($"ИИ: поиск в интернете (Groq {model}) за {watch.ElapsedMilliseconds} мс, источников {sources.Count}.", "TELEGRAM");
                        return string.IsNullOrWhiteSpace(text) ? (null, sources, "Groq вернул пустой ответ") : (text, sources, null);
                    }
                    var message = ErrorText(json);
                    PosLogger.Log($"ИИ: поиск в интернете (Groq {model}) → HTTP {status} за {watch.ElapsedMilliseconds} мс ({message}).", "TELEGRAM");
                    if (status == 401)
                        return (null, none, "ключ Groq не подходит");
                    if (status == 0)
                    {
                        // Нет ответа — Groq недоступен: не ждать следующую модель и повторы, поиск через Groq — через 10 минут.
                        _groqSearchBlockedUntilUtc = DateTime.UtcNow.AddMinutes(10);
                        BlockProvider("GroqSearch", TimeSpan.FromMinutes(10));
                        return (null, none, "Groq не отвечает — поиск в интернете отложен на 10 минут");
                    }
                    lastError = $"Groq: {message}";
                    if (status != 429)
                    {
                        // 400/403 и сбои — эта модель пока не ищет; пробуем следующую.
                        if (status is 400 or 403)
                            BlockSearchModel(model, TimeSpan.FromMinutes(60));
                        break;
                    }
                    // 429: поминутный лимит — подождать, сколько просит Groq (до 20 с), и повторить; дневной или долгий — другая модель.
                    var wait = RetryAfter(message);
                    if (attempt == 0 && wait is { } w && w <= TimeSpan.FromSeconds(20) && !IsDailyLimit(message))
                    {
                        PosLogger.Log($"ИИ: Groq {model} — поминутный лимит, жду {w.TotalSeconds:0.#} с и повторяю.", "TELEGRAM");
                        await Task.Delay(w + TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
                        continue;
                    }
                    var cooldown = IsDailyLimit(message) ? TimeSpan.FromHours(1) : wait is { } w2 && w2 > TimeSpan.Zero ? w2 : TimeSpan.FromMinutes(1);
                    BlockSearchModel(model, cooldown);
                    BlockProvider("GroqSearch", cooldown);
                    break;
                }
            }
            lock (SearchModelBlockedUntilUtc)
            {
                // Обе модели заблокированы — общий запрет до ближайшего разблокирования.
                var blocked = SearchModelBlockedUntilUtc.Values.Where(v => v > DateTime.UtcNow).ToList();
                if (blocked.Count == 2)
                    _groqSearchBlockedUntilUtc = blocked.Min();
            }
            return (null, none, lastError ?? "поиск в интернете через Groq временно недоступен (лимит)");
        }
        finally
        {
            SearchGate.Release();
        }
    }

    private static void BlockSearchModel(string model, TimeSpan duration)
    {
        lock (SearchModelBlockedUntilUtc)
            SearchModelBlockedUntilUtc[model] = DateTime.UtcNow + duration;
    }

    /// <summary>«Please try again in 9.742s» / «in 1m30s» из ответа Groq.</summary>
    private static TimeSpan? RetryAfter(string message)
    {
        var m = Regex.Match(message ?? "", @"try again in\s+(?:(\d+)m)?\s*(?:(\d+(?:\.\d+)?)s)?", RegexOptions.IgnoreCase);
        if (!m.Success || (m.Groups[1].Value.Length == 0 && m.Groups[2].Value.Length == 0))
            return null;
        var minutes = m.Groups[1].Value.Length > 0 ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var seconds = m.Groups[2].Value.Length > 0 ? double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    private static bool IsDailyLimit(string message) =>
        (message ?? "").Contains("per day", StringComparison.OrdinalIgnoreCase)
        || (message ?? "").Contains("(RPD)", StringComparison.OrdinalIgnoreCase)
        || (message ?? "").Contains("(TPD)", StringComparison.OrdinalIgnoreCase);

    /// <summary>Запасной ответ, когда Gemini не ответил: OpenRouter (бесплатные модели, полная сводка), затем Groq
    /// (короткая сводка — у бесплатного Groq 8000 токенов в минуту).</summary>
    public static async Task<(string? Answer, string? Error)> ChatFallbackAsync(
        string system, IReadOnlyList<(string Role, string Text)> history, string question, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var (name, url, key, model, maxSystem) in new[]
                 {
                     ("OpenRouter", OpenRouterUrl, UserPreferences.Instance.OpenRouterApiKey, OpenRouterModel, 120_000),
                     ("Groq", GroqUrl, UserPreferences.Instance.GroqApiKey, GroqChatModel, 9_000),
                 })
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            lock (ChatBlockedUntilUtc)
            {
                if (ChatBlockedUntilUtc.TryGetValue(name, out var until) && DateTime.UtcNow < until)
                    continue;
            }
            var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system.Length > maxSystem ? system[..maxSystem] : system });
            foreach (var (role, text) in history.TakeLast(name == "Groq" ? 4 : 12))
                messages.Add(new JsonObject { ["role"] = role == "model" ? "assistant" : "user", ["content"] = text });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
            var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["max_tokens"] = 1200 };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (json, status) = await PostAsync(url, key!, body, ct).ConfigureAwait(false);
            if (status is >= 200 and < 300)
            {
                var (answer, _) = ReadAnswer(json);
                if (!string.IsNullOrWhiteSpace(answer))
                {
                    PosLogger.Log($"ИИ: ответила запасная модель {name} за {watch.ElapsedMilliseconds} мс.", "TELEGRAM");
                    return (answer, null);
                }
                lastError = $"{name}: пустой ответ";
                continue;
            }
            lastError = $"{name}: {ErrorText(json)}";
            if (status == 429)
            {
                lock (ChatBlockedUntilUtc)
                    ChatBlockedUntilUtc[name] = DateTime.UtcNow.AddMinutes(10);
                BlockProvider(name, TimeSpan.FromMinutes(10));
            }
            PosLogger.Log($"ИИ: запасная модель {name} → HTTP {status} за {watch.ElapsedMilliseconds} мс ({lastError}).", "TELEGRAM");
        }
        return (null, lastError ?? "запасных моделей нет");
    }

    /// <summary>Проверка ключа Groq: короткий ответ и (отдельно) поиск в интернете.</summary>
    public static async Task<(bool Ok, string Message)> TestGroqAsync(string key, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = GroqChatModel,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Скажи по-русски одним словом: работаю." }),
            ["max_tokens"] = 300,
        };
        var (json, status) = await PostAsync(GroqUrl, key, body, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            return (false, status == 401 ? "ключ не подходит" : ErrorText(json));
        body["tools"] = new JsonArray(new JsonObject { ["type"] = "browser_search" });
        body["tool_choice"] = "required";
        body["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Найди в интернете курс доллара к сому на сегодня. Одной строкой." });
        body["max_tokens"] = 600;
        var (json2, status2) = await PostAsync(GroqUrl, key, body, ct).ConfigureAwait(false);
        return status2 is >= 200 and < 300
            ? (true, "работает, поиск в интернете есть")
            : (true, "работает, но поиск в интернете недоступен: " + ErrorText(json2));
    }

    /// <summary>Проверка ключа OpenRouter: короткий ответ бесплатной модели.</summary>
    public static async Task<(bool Ok, string Message)> TestOpenRouterAsync(string key, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = OpenRouterModel,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Скажи по-русски одним словом: работаю." }),
            ["max_tokens"] = 300,
        };
        var (json, status) = await PostAsync(OpenRouterUrl, key, body, ct).ConfigureAwait(false);
        return status is >= 200 and < 300 ? (true, "работает") : (false, status == 401 ? "ключ не подходит" : ErrorText(json));
    }

    private static async Task<(string Json, int Status)> PostAsync(string url, string key, JsonObject body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("Authorization", "Bearer " + key.Trim());
            if (url == OpenRouterUrl)
            {
                // OpenRouter просит указывать сайт и название приложения.
                request.Headers.Add("HTTP-Referer", "https://nurcrm.kg");
                request.Headers.Add("X-Title", "NurMarket");
            }
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            return (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ("{\"error\":{\"message\":\"нет связи: " + ex.GetType().Name + "\"}}", 0);
        }
    }

    /// <summary>Текст ответа (без пометок цитат 【…】) и источники: ссылки из executed_tools и из самого текста.</summary>
    private static (string? Text, IReadOnlyList<TelegramAiChat.WebSource> Sources) ReadAnswer(string json)
    {
        var sources = new List<TelegramAiChat.WebSource>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (message.TryGetProperty("executed_tools", out var tools))
                CollectUrls(tools, sources);
            if (text is not null)
            {
                text = Regex.Replace(text, "【[^】]*】", "").Trim();
                foreach (Match m in Regex.Matches(text, @"https?://[^\s<>()""'\]\)]+"))
                    sources.Add(new TelegramAiChat.WebSource(Host(m.Value), m.Value.TrimEnd('.', ',', ';')));
            }
            return (text, sources.DistinctBy(x => x.Uri).Take(8).ToList());
        }
        catch
        {
            return (null, sources);
        }
    }

    private static void CollectUrls(JsonElement e, List<TelegramAiChat.WebSource> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && u.GetString() is { Length: > 8 } url && url.StartsWith("http"))
                    into.Add(new TelegramAiChat.WebSource(e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? Host(url) : Host(url), url));
                foreach (var p in e.EnumerateObject())
                    CollectUrls(p.Value, into);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                    CollectUrls(x, into);
                break;
        }
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;

    private static string ErrorText(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var msg = node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString();
            if (!string.IsNullOrWhiteSpace(msg))
                return msg.Length > 200 ? msg[..200] : msg;
        }
        catch
        {
            // не JSON
        }
        return json.Length > 120 ? json[..120] : json;
    }
}
