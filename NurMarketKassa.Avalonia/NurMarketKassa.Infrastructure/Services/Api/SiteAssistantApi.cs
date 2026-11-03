using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services.Api;

/// <summary>Настройки ИИ-помощника магазина на витрине (GET/PUT api/main/inbox/assistant/settings/).
/// Ключ Gemini сюда не приходит никогда — только признак <see cref="KeySet"/> (ключ у каждого магазина свой, хранится на сервере).
/// <see cref="Raw"/> — ответ сервера как есть: из него берутся имена полей и значения, которые экран не показывает.</summary>
public sealed record AssistantSettings(bool Enabled, int? DailyLimit, bool KeySet, int? UsedToday, string EnabledKey, string LimitKey, string KeyField, JsonObject Raw);

/// <summary>Ответ «Проверить помощника» (POST api/main/inbox/assistant/test/): текст, вызванные функции, признаки «занят» и «числа заменены».</summary>
public sealed record AssistantTestResult(string Answer, IReadOnlyList<string> Functions, bool Busy, bool NumbersReplaced, int CardCount);

/// <summary>2026-11-01 (ТЗ Умару 01.11, ИИ-1/ИИ-2; ответ Умара: «Настройки — GET/PUT api/main/inbox/assistant/settings/ (включён, потолок),
/// "Проверить помощника" — POST api/main/inbox/assistant/test/»). Ключ Gemini — вариант «а»: у каждого магазина свой, общего ключа NurCRM нет.
/// Ключ только на запись: уходит PUT-ом при «Сохранить», назад не приходит, в журнал и на экран не попадает.
/// Имена полей настроек в ответе Умара не названы (файл «чат и ИИ-помощник витрины» в папку не попал), поэтому читаем по образцу
/// telegram-bot/settings (ai_enabled, ai_key_set) и по близким именам; пишем под тем именем, которое сервер прислал.</summary>
public sealed class SiteAssistantApi
{
    private const string Root = "api/main/inbox/assistant/";
    private readonly NurMarketApiClient _api;
    private static bool _patchUnsupported;

    public SiteAssistantApi(NurMarketApiClient api) => _api = api;

    private static readonly string[] EnabledNames = { "ai_enabled", "enabled", "is_enabled", "assistant_enabled" };
    private static readonly string[] LimitNames = { "ai_daily_calls", "daily_limit", "daily_calls", "daily_cap", "ai_daily_limit" };
    private static readonly string[] KeySetNames = { "ai_key_set", "key_set", "gemini_key_set", "has_key", "api_key_set" };
    private static readonly string[] KeyFieldNames = { "ai_key", "gemini_api_key", "gemini_key", "api_key" };
    private static readonly string[] UsedNames = { "used_today", "calls_today", "today_calls", "ai_calls_today", "usage_today" };

    /// <summary>Сервер ещё не умеет настройки помощника (адреса нет): блок показывает «Скоро».</summary>
    public static bool IsRouteMissing(Exception ex) =>
        ex is ApiException { StatusCode: 404 or 405 or 501 }
        || (ex is ApiException { Payload: { ValueKind: JsonValueKind.Object } p } && p.ToString().Contains("route_not_found", StringComparison.OrdinalIgnoreCase));

    /// <summary>null — адреса нет на сервере.</summary>
    public async Task<AssistantSettings?> GetSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, Root + "settings/", null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return Parse(data);
        }
        catch (ApiException ex) when (IsRouteMissing(ex))
        {
            return null;
        }
    }

    /// <summary>PUT только изменённых полей (null — поле не трогаем). Ключ Gemini — только если владелец ввёл новый. Если сервер требует
    /// остальные поля (400 «обязательное поле»), повторяем один раз со всем документом настроек, который он сам прислал.</summary>
    public async Task<AssistantSettings> SaveSettingsAsync(AssistantSettings current, bool? enabled, int? dailyLimit, string? newKey, CancellationToken ct = default)
    {
        var changes = new Dictionary<string, object?>();
        if (enabled is { } e)
            changes[current.EnabledKey] = current.EnabledKey == "ai_mode" ? (e ? "auto" : "off") : e;
        if (dailyLimit is { } d)
            changes[current.LimitKey] = d;
        if (!string.IsNullOrWhiteSpace(newKey))
            changes[current.KeyField] = newKey.Trim();
        if (changes.Count == 0)
            return current;

        JsonElement data;
        try
        {
            // 2026-11-01 (контрольный аудит В2-09): сначала PATCH — он точно не заменяет документ целиком (PUT без поля ключа мог бы стереть
            // сохранённый ключ Gemini); сервер без PATCH (405) — PUT только с изменёнными полями, дальше как раньше.
            if (_patchUnsupported)
                data = await _api.RequestAsync(HttpMethod.Put, Root + "settings/", changes, null, ct, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
            else
            {
                try
                {
                    data = await _api.RequestAsync(HttpMethod.Patch, Root + "settings/", changes, null, ct, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.StatusCode == 405)
                {
                    _patchUnsupported = true;
                    data = await _api.RequestAsync(HttpMethod.Put, Root + "settings/", changes, null, ct, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                }
            }
        }
        catch (ApiException ex) when (ex.StatusCode == 400 && LooksLikeMissingFields(ex))
        {
            var full = new Dictionary<string, object?>();
            foreach (var (k, v) in current.Raw)
            {
                if (k.EndsWith("_set", StringComparison.OrdinalIgnoreCase) || k is "id" or "updated_at" or "created_at" || UsedNames.Contains(k))
                    continue; // служебные и только для чтения
                // 2026-11-01 (приёмка 1.17.72, В-2): поле ключа из ответа сервера (может быть маской «AIza…****») в повтор НЕ копируем никогда —
                // ключ уходит только из changes и только если владелец ввёл новый.
                if (KeyFieldNames.Contains(k, StringComparer.OrdinalIgnoreCase) || k.Contains("key", StringComparison.OrdinalIgnoreCase) || k.Contains("secret", StringComparison.OrdinalIgnoreCase))
                    continue;
                full[k] = v is null ? null : JsonSerializer.Deserialize<object>(v.ToJsonString());
            }
            foreach (var (k, v) in changes)
                full[k] = v;
            data = await _api.RequestAsync(HttpMethod.Put, Root + "settings/", full, null, ct, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
        }

        // Ключ в журнал не пишем — только какие поля менялись.
        PosLogger.Log($"Помощник магазина: настройки сохранены ({string.Join(", ", changes.Keys.Select(k => k == current.KeyField ? "ключ ИИ" : k))}).", "SHOWCASE");
        return (data.ValueKind == JsonValueKind.Object ? Parse(data) : null)
               ?? (await GetSettingsAsync(ct).ConfigureAwait(false)) ?? current;
    }

    /// <summary>«Проверить помощника»: пробный вопрос, ответ сервера и список вызванных функций (как telegram-bot/test-ai/).</summary>
    public async Task<AssistantTestResult> TestAsync(string question, string lang, CancellationToken ct = default)
    {
        // Имя поля вопроса в ответе Умара не названо — шлём оба привычных (лишнее поле DRF отбрасывает).
        var body = new Dictionary<string, object?> { ["question"] = question, ["message"] = question, ["lang"] = lang };
        var d = await _api.RequestAsync(HttpMethod.Post, Root + "test/", body, null, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (d.ValueKind != JsonValueKind.Object)
            return new AssistantTestResult("", Array.Empty<string>(), false, false, 0);

        var answer = First(d, "answer", "text", "reply", "message") ?? "";
        var functions = new List<string>();
        foreach (var key in new[] { "functions_called", "functions", "tools_called" })
        {
            if (!d.TryGetProperty(key, out var f) || f.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var x in f.EnumerateArray())
            {
                var name = x.ValueKind == JsonValueKind.String ? x.GetString() : First(x, "name", "function");
                if (!string.IsNullOrWhiteSpace(name))
                    functions.Add(name!);
            }
            break;
        }
        var busy = string.Equals(First(d, "ai"), "busy", StringComparison.OrdinalIgnoreCase);
        var replaced = d.TryGetProperty("hallucination_blocked", out var hb) && hb.ValueKind == JsonValueKind.True;
        var cards = d.TryGetProperty("cards", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;
        return new AssistantTestResult(answer, functions, busy, replaced, cards);
    }

    /// <summary>Человеческий текст вместо «помощник недоступен» и кодов сервера (ключ не задан, выключен, потолок, занят).</summary>
    public static string FriendlyError(Exception ex)
    {
        var code = "";
        if (ex is ApiException { Payload: { ValueKind: JsonValueKind.Object } p })
            code = (First(p, "code", "error", "detail", "message") ?? "").ToLowerInvariant();
        if (ex is ApiException { StatusCode: 429 } || code.Contains("busy") || code.Contains("limit") || code.Contains("quota"))
            return Tr.T("Помощник занят: исчерпан потолок вызовов ИИ на сегодня или лимит Google. Покупатели увидят «Помощник занят» и кнопки связи. Поднимите потолок или попробуйте позже.",
                "Жардамчы бош эмес: бүгүнкү ЖИ чакыруулар чеги же Google лимити түгөндү. Кардарлар «Жардамчы бош эмес» жана байланыш баскычтарын көрөт. Чекти көтөрүңүз же кийинчерээк аракет кылыңыз.",
                "The assistant is busy: today's AI call ceiling or the Google limit is used up. Customers will see “Assistant is busy” and contact buttons. Raise the ceiling or try later.",
                "Asistan meşgul: bugünkü yapay zekâ çağrı sınırı veya Google limiti doldu. Müşteriler «Asistan meşgul» ve iletişim düğmelerini görür. Sınırı yükseltin veya sonra deneyin.",
                "Yordamchi band: bugungi SI chaqiruvlari chegarasi yoki Google limiti tugadi. Xaridorlar «Yordamchi band» va aloqa tugmalarini ko'radi. Chegarani oshiring yoki keyinroq urinib ko'ring.");
        if (code.Contains("key") || code.Contains("unavailable") || code.Contains("disabled") || code.Contains("not_configured") || code.Contains("недоступ"))
            return Tr.T("Помощник недоступен: включите его и укажите ключ Gemini своего магазина (Google AI Studio), затем нажмите «Сохранить настройки помощника».",
                "Жардамчы жеткиликсиз: аны күйгүзүп, дүкөнүңүздүн Gemini ачкычын (Google AI Studio) жазыңыз, анан «Жардамчынын жөндөөлөрүн сактоо» басыңыз.",
                "The assistant is unavailable: turn it on and enter your shop's Gemini key (Google AI Studio), then press “Save assistant settings”.",
                "Asistan kullanılamıyor: açın ve mağazanızın Gemini anahtarını (Google AI Studio) girin, sonra «Asistan ayarlarını kaydet»e basın.",
                "Yordamchi mavjud emas: uni yoqing va do'koningizning Gemini kalitini (Google AI Studio) kiriting, so'ng «Yordamchi sozlamalarini saqlash»ni bosing.");
        return ServerOutageMonitor.FriendlyError(ex, showServerText: true);
    }

    // ── разбор ──────────────────────────────────────────────────────────────────────

    private static bool LooksLikeMissingFields(ApiException ex) =>
        ex.Payload is { ValueKind: JsonValueKind.Object } p
        && (p.ToString().Contains("required", StringComparison.OrdinalIgnoreCase) || p.ToString().Contains("обязательн", StringComparison.OrdinalIgnoreCase));

    private static AssistantSettings? Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || JsonNode.Parse(data.GetRawText()) is not JsonObject raw)
            return null;

        var enabledKey = EnabledNames.FirstOrDefault(raw.ContainsKey) ?? "ai_enabled";
        var enabled = raw.ContainsKey(enabledKey) && raw[enabledKey] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && eb;
        // ai_mode (off|draft|auto) из ТЗ Б-79: «выключен» только off.
        if (!raw.ContainsKey(enabledKey) && raw["ai_mode"] is JsonValue mode && mode.TryGetValue<string>(out var m))
        {
            enabledKey = "ai_mode";
            enabled = !string.Equals(m, "off", StringComparison.OrdinalIgnoreCase);
        }
        var limitKey = LimitNames.FirstOrDefault(raw.ContainsKey) ?? "ai_daily_calls";
        var keySetName = KeySetNames.FirstOrDefault(raw.ContainsKey);
        return new AssistantSettings(
            enabled,
            Int(raw, limitKey),
            keySetName != null && raw[keySetName] is JsonValue kv && kv.TryGetValue<bool>(out var kb) && kb,
            UsedNames.Select(n => Int(raw, n)).FirstOrDefault(v => v != null),
            enabledKey,
            limitKey,
            KeyFieldNames.FirstOrDefault(raw.ContainsKey) ?? "ai_key",
            raw);
    }

    private static int? Int(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v)
            return null;
        if (v.TryGetValue<int>(out var i))
            return i;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static string? First(JsonElement e, params string[] keys)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString()!.Trim();
        return null;
    }
}
