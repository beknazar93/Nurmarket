using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>Один ключ Google Gemini из списка владельца.</summary>
public sealed class GeminiKeyEntry
{
    /// <summary>Метка для себя («Аккаунт 1», псевдоним почты). В журнал не пишется — только номер и последние 4 символа ключа.</summary>
    public string Label { get; set; } = "";

    public string Key { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>Состояние ключа для окна настройки.</summary>
public enum GeminiKeyState { Working, Exhausted, Invalid, Disabled }

/// <summary>
/// 2026-10-26, владелец: «наш десктоп: добавь возможность добавить API Gemini из нескольких Google-аккаунтов».
/// Бесплатный лимит Gemini считается на проект/аккаунт (запросов в минуту и в сутки), поэтому владелец может задать до 10 ключей,
/// и касса берёт их по очереди: «первый рабочий» (порядок списка), при 429/RESOURCE_EXHAUSTED ключ помечается «исчерпан до …»
/// (минутный лимит — на время из ответа Google, по умолчанию 60 с; суточный — до полуночи по тихоокеанскому времени, когда Google
/// сбрасывает сутки), при «API key not valid» — «недействителен», и тот же запрос повторяется со следующим ключом.
///
/// ФОРМАТ ХРАНЕНИЯ (обратная совместимость): основной ключ — как раньше, UserPreferences.TelegramAiKey (его читают мобилка, бот на
/// сервере и старые кассы). Остальные — UserPreferences.TelegramAiKeysExtra, строка JSON (секрет, как и основной: на диске через
/// DPAPI, в облаке — в «secrets» личных настроек NurCRM):
/// {"v":1,"mainLabel":"Аккаунт 1","mainEnabled":true,"extra":[{"label":"Аккаунт 2","key":"AIza…","enabled":true}]}.
/// Порядок списка = основной ключ первым, затем extra по порядку. Старая касса видит только основной ключ — ничего не ломается.
///
/// Состояние «исчерпан/недействителен» хранится только в памяти (после перезапуска ключ пробуют заново — один лишний запрос).
/// Квота Gemini считается по модели, поэтому блокировка — на пару (ключ, модель): ключ, у которого кончился лимит одной модели,
/// продолжает отвечать на других.
/// </summary>
public static class GeminiKeyPool
{
    public const int MaxKeys = 10;

    /// <summary>Адрес API. Меняется только стендом проверки (без сети, подменный сервер на 127.0.0.1); в программе — всегда Google.</summary>
    internal static string BaseUrl = "https://generativelanguage.googleapis.com";

    /// <summary>Клиент проверки ключей (список моделей). Стенд подменяет его отражением.</summary>
    internal static HttpClient CheckHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly object Sync = new();
    private static readonly Dictionary<string, DateTime> Blocked = new(); // "ключ\nмодель" -> до (UTC)
    private static readonly Dictionary<string, DateTime> InvalidUntil = new(); // ключ -> до (UTC)
    private static readonly HashSet<string> Verified = new();

    private static string? _cacheMain;
    private static string? _cacheExtra;
    private static bool _cacheValid;
    private static List<GeminiKeyEntry> _cache = new();

    /// <summary>Сколько повторять запрос с «недействительным» ключом: через это время ключ снова пробуется сам.</summary>
    private static readonly TimeSpan InvalidRetry = TimeSpan.FromMinutes(30);

    public static string LiveEndpoint =>
        (BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws") + BaseUrl[BaseUrl.IndexOf("://", StringComparison.Ordinal)..]
        + "/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    // ------------------------------------------------------------------------------------------ список ключей

    /// <summary>Все ключи по порядку: основной (TelegramAiKey), затем дополнительные. Копия — менять можно.</summary>
    public static List<GeminiKeyEntry> Entries()
    {
        var prefs = UserPreferences.Instance;
        var main = prefs.TelegramAiKey;
        var extra = prefs.TelegramAiKeysExtra;
        lock (Sync)
        {
            if (!_cacheValid || main != _cacheMain || extra != _cacheExtra)
            {
                _cache = Parse(main, extra);
                _cacheMain = main;
                _cacheExtra = extra;
                _cacheValid = true;
            }
            return _cache.Select(e => new GeminiKeyEntry { Label = e.Label, Key = e.Key, Enabled = e.Enabled }).ToList();
        }
    }

    internal static List<GeminiKeyEntry> Parse(string? mainKey, string? extraJson)
    {
        var list = new List<GeminiKeyEntry>();
        string mainLabel = "";
        var mainEnabled = true;
        JsonArray? extra = null;
        if (!string.IsNullOrWhiteSpace(extraJson))
        {
            try
            {
                if (JsonNode.Parse(extraJson) is JsonObject root)
                {
                    mainLabel = root["mainLabel"]?.GetValue<string>() ?? "";
                    mainEnabled = root["mainEnabled"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
                    extra = root["extra"] as JsonArray;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                PosLogger.Log("Ключи Gemini: дополнительный список не разобран — используется только основной ключ.", "WARNING");
            }
        }
        if (!string.IsNullOrWhiteSpace(mainKey))
            list.Add(new GeminiKeyEntry { Label = mainLabel, Key = mainKey.Trim(), Enabled = mainEnabled });
        if (extra is not null)
        {
            foreach (var node in extra)
            {
                if (node is not JsonObject o)
                    continue;
                var key = (o["key"]?.GetValue<string>() ?? "").Trim();
                if (key.Length == 0 || list.Any(x => x.Key == key))
                    continue;
                list.Add(new GeminiKeyEntry
                {
                    Label = o["label"]?.GetValue<string>() ?? "",
                    Key = key,
                    Enabled = o["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var eb) || eb,
                });
                if (list.Count >= MaxKeys)
                    break;
            }
        }
        return list;
    }

    /// <summary>Сохранить список: первый — в TelegramAiKey (как раньше), остальные — в TelegramAiKeysExtra. Пустые ключи и повторы
    /// отбрасываются, не больше <see cref="MaxKeys"/>. Запись в облако идёт тем же путём, что у одиночного ключа (SaveToDisk).</summary>
    public static void Save(IEnumerable<GeminiKeyEntry> entries)
    {
        var clean = new List<GeminiKeyEntry>();
        foreach (var e in entries)
        {
            var key = (e.Key ?? "").Trim();
            if (key.Length == 0 || clean.Any(x => x.Key == key))
                continue;
            clean.Add(new GeminiKeyEntry { Label = (e.Label ?? "").Trim(), Key = key, Enabled = e.Enabled });
            if (clean.Count >= MaxKeys)
                break;
        }
        var prefs = UserPreferences.Instance;
        if (clean.Count == 0)
        {
            prefs.TelegramAiKey = null;
            prefs.TelegramAiKeysExtra = null;
        }
        else
        {
            prefs.TelegramAiKey = clean[0].Key;
            if (clean.Count == 1 && clean[0].Label.Length == 0 && clean[0].Enabled)
                prefs.TelegramAiKeysExtra = null;
            else
            {
                var extra = new JsonArray();
                foreach (var e in clean.Skip(1))
                    extra.Add(new JsonObject { ["label"] = e.Label, ["key"] = e.Key, ["enabled"] = e.Enabled });
                prefs.TelegramAiKeysExtra = new JsonObject
                {
                    ["v"] = 1,
                    ["mainLabel"] = clean[0].Label,
                    ["mainEnabled"] = clean[0].Enabled,
                    ["extra"] = extra,
                }.ToJsonString();
            }
        }
        prefs.SaveToDisk();
    }

    /// <summary>Первый включённый ключ (для проверки «ИИ настроен» и для старых мест, которым нужна одна строка).</summary>
    public static string? FirstEnabledKey() => Entries().FirstOrDefault(e => e.Enabled)?.Key;

    public static int EnabledCount() => Entries().Count(e => e.Enabled);

    // ------------------------------------------------------------------------------------------ выбор ключа

    /// <summary>Ключ для запроса к модели: первый включённый, не недействительный и не исчерпанный для этой модели. null — свободных нет.
    /// Если список пуст (ключ не задан) — тоже null.</summary>
    public static string? Pick(string model)
    {
        var now = DateTime.UtcNow;
        var entries = Entries();
        lock (Sync)
        {
            foreach (var e in entries)
            {
                if (!e.Enabled)
                    continue;
                if (InvalidUntil.TryGetValue(e.Key, out var inv) && inv > now)
                    continue;
                if (Blocked.TryGetValue(BlockKey(e.Key, model), out var until) && until > now)
                    continue;
                if (Blocked.TryGetValue(BlockKey(e.Key, "*"), out var all) && all > now) // лимит, пойманный проверкой ключа (без модели)
                    continue;
                return e.Key;
            }
        }
        return null;
    }

    private static string BlockKey(string key, string model) => key + "\n" + model;

    /// <summary>Разобрать неудачный ответ Google. true — этот ключ теперь помечен (лимит или недействителен): запрос стоит повторить
    /// со следующим ключом. Ничего не меняет в ответах других видов (503, 404 модели, 400 запроса).</summary>
    public static bool ReportFailure(string key, string model, int status, string body)
    {
        body ??= "";
        if (IsQuota(status, body))
        {
            var until = QuotaUntil(body, out var daily);
            lock (Sync)
                Blocked[BlockKey(key, model)] = until;
            PosLogger.Log($"ИИ: {Describe(key)} — лимит {(daily ? "за сутки" : "в минуту")} у {model}, ключ исчерпан до {until.ToLocalTime():dd.MM HH:mm}.", "WARNING");
            return true;
        }
        if (IsInvalidKey(status, body))
        {
            lock (Sync)
                InvalidUntil[key] = DateTime.UtcNow + InvalidRetry;
            PosLogger.Log($"ИИ: {Describe(key)} — Google не принял ключ (HTTP {status}), помечен «недействителен».", "WARNING");
            return true;
        }
        return false;
    }

    /// <summary>Ответ получен — ключ рабочий (снимает отметку «недействителен»).</summary>
    public static void ReportSuccess(string key)
    {
        lock (Sync)
        {
            InvalidUntil.Remove(key);
            Verified.Add(key);
        }
    }

    public static bool IsInvalidKey(int status, string body)
    {
        if (status == 401)
            return true;
        if (status is not (400 or 403))
            return false;
        if (body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase) || body.Contains("API key not valid", StringComparison.OrdinalIgnoreCase)
            || body.Contains("API key expired", StringComparison.OrdinalIgnoreCase) || body.Contains("leaked", StringComparison.OrdinalIgnoreCase)
            || body.Contains("CONSUMER_SUSPENDED", StringComparison.OrdinalIgnoreCase) || body.Contains("has been suspended", StringComparison.OrdinalIgnoreCase)
            || body.Contains("API Key not found", StringComparison.OrdinalIgnoreCase))
            return true;
        // 403 без слова «model» — отказ ключу; про модель («нет доступа к модели») — это не вина ключа (там пробуют следующую модель).
        return status == 403 && !body.Contains("model", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsQuota(int status, string body) =>
        status == 429 || body.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
        || (status == 403 && body.Contains("quota", StringComparison.OrdinalIgnoreCase));

    /// <summary>До какого момента ключ считается исчерпанным. Суточный лимит (в ответе PerDay) — до полуночи по тихоокеанскому времени
    /// (так Google сбрасывает сутки); иначе — retryDelay из ответа (минимум 10 с, максимум сутки), без него — 60 с.</summary>
    public static DateTime QuotaUntil(string body, out bool daily)
    {
        var now = DateTime.UtcNow;
        daily = Regex.IsMatch(body ?? "", @"PerDay|per day|daily", RegexOptions.IgnoreCase);
        if (daily)
            return NextPacificMidnightUtc(now);
        var delay = TimeSpan.FromSeconds(60);
        var m = Regex.Match(body ?? "", @"""retryDelay""\s*:\s*""([0-9.]+)s""");
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            delay = TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling(seconds) + 1, 10, 86400));
        return now + delay;
    }

    public static DateTime NextPacificMidnightUtc(DateTime utcNow)
    {
        TimeZoneInfo? tz = null;
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                break;
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        if (tz is null)
        {
            // Без базы часовых поясов: тихоокеанское время зимой UTC−8 (летом −7, разница в час не страшна).
            var local = utcNow.AddHours(-8);
            return local.Date.AddDays(1).AddHours(8);
        }
        var pacific = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var midnight = DateTime.SpecifyKind(pacific.Date.AddDays(1), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, tz);
    }

    // ------------------------------------------------------------------------------------------ состояние для окна

    /// <summary>Состояние ключа и момент (местное время), до которого он исчерпан.</summary>
    public static (GeminiKeyState State, DateTime? Until) StateOf(GeminiKeyEntry entry)
    {
        if (!entry.Enabled)
            return (GeminiKeyState.Disabled, null);
        var now = DateTime.UtcNow;
        lock (Sync)
        {
            if (InvalidUntil.TryGetValue(entry.Key, out var inv) && inv > now)
                return (GeminiKeyState.Invalid, null);
            DateTime? latest = null;
            var prefix = entry.Key + "\n";
            foreach (var (k, until) in Blocked)
                if (until > now && k.StartsWith(prefix, StringComparison.Ordinal) && (latest is null || until > latest))
                    latest = until;
            if (latest is { } l)
                return (GeminiKeyState.Exhausted, l.ToLocalTime());
        }
        return (GeminiKeyState.Working, null);
    }

    /// <summary>«Рабочих ключей: N из M»: M — все в списке, N — включённые, не исчерпанные и не недействительные.</summary>
    public static (int Working, int Total) Counts()
    {
        var entries = Entries();
        return (entries.Count(e => StateOf(e).State == GeminiKeyState.Working), entries.Count);
    }

    /// <summary>Ближайший момент (местное время), когда освободится хоть один ключ для модели; null — никогда не блокировались.</summary>
    public static DateTime? NextRetryLocal(string model)
    {
        var now = DateTime.UtcNow;
        var entries = Entries().Where(e => e.Enabled).ToList();
        DateTime? best = null;
        lock (Sync)
        {
            foreach (var e in entries)
            {
                DateTime? until = null;
                if (InvalidUntil.TryGetValue(e.Key, out var inv) && inv > now)
                    until = inv;
                if (Blocked.TryGetValue(BlockKey(e.Key, model), out var b) && b > now && (until is null || b > until))
                    until = b;
                if (until is { } u && (best is null || u < best))
                    best = u;
            }
        }
        return best?.ToLocalTime();
    }

    /// <summary>Сообщение, когда все ключи исчерпаны.</summary>
    public static string AllExhaustedMessage(string model)
    {
        var total = EnabledCount();
        var next = NextRetryLocal(model);
        var when = next is { } n ? $", следующая попытка после {n:HH:mm}" : "";
        return total > 1
            ? $"исчерпан бесплатный лимит у всех ключей Gemini ({total}){when}"
            : $"исчерпан бесплатный лимит запросов Google{when}";
    }

    /// <summary>Что сказать, когда свободных ключей для модели нет: все недействительны или исчерпаны.</summary>
    public static string UnavailableMessage(string model)
    {
        var enabled = Entries().Where(e => e.Enabled).ToList();
        var now = DateTime.UtcNow;
        bool AllInvalid()
        {
            lock (Sync)
                return enabled.Count > 0 && enabled.All(e => InvalidUntil.TryGetValue(e.Key, out var u) && u > now);
        }
        return AllInvalid() ? "ключ Google не подходит — проверьте его в настройках" : AllExhaustedMessage(model);
    }

    /// <summary>Сбросить отметки (кнопка «Проверить» у ключа и тесты).</summary>
    public static void ClearState(string? key = null)
    {
        lock (Sync)
        {
            if (key is null)
            {
                Blocked.Clear();
                Verified.Clear();
                InvalidUntil.Clear();
                return;
            }
            InvalidUntil.Remove(key);
            foreach (var k in Blocked.Keys.Where(k => k.StartsWith(key + "\n", StringComparison.Ordinal)).ToList())
                Blocked.Remove(k);
        }
    }

    /// <summary>Для журнала: номер в списке и последние 4 символа — сам ключ в журнал не попадает.</summary>
    public static string Describe(string key)
    {
        var entries = Entries();
        var idx = entries.FindIndex(e => e.Key == key);
        return $"ключ #{(idx >= 0 ? idx + 1 : 0)} …{Tail(key)}";
    }

    public static string Tail(string key) => key.Length <= 4 ? "****" : key[^4..];

    /// <summary>Убирает из текста все ключи списка (на случай, если Google вернул ключ в сообщении об ошибке).</summary>
    public static string Mask(string text)
    {
        foreach (var e in Entries())
            if (e.Key.Length >= 8)
                text = text.Replace(e.Key, "***" + Tail(e.Key));
        return text;
    }

    // ------------------------------------------------------------------------------------------ проверка ключа

    /// <summary>Результат кнопки «Проверить».</summary>
    public enum CheckResult { Ok, Invalid, Exhausted, NoConnection, Other }

    /// <summary>Кнопка «Проверить»: лёгкий запрос списка моделей (GET models) — не тратит лимит генерации. Обновляет состояние ключа
    /// (рабочий снимает отметки). Until — местное время, до которого ключ исчерпан; Http — код ответа при Other.</summary>
    public static async Task<(CheckResult Result, DateTime? Until, int Http)> CheckAsync(string key, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/v1beta/models?pageSize=1");
            request.Headers.Add("x-goog-api-key", key);
            using var response = await CheckHttp.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                ClearState(key);
                lock (Sync)
                    Verified.Add(key);
                PosLogger.Log($"ИИ: {Describe(key)} — проверка прошла.", "INFO");
                return (CheckResult.Ok, null, status);
            }
            if (ReportFailure(key, "*", status, body))
            {
                var st = StateOf(new GeminiKeyEntry { Key = key, Enabled = true });
                return st.State == GeminiKeyState.Invalid ? (CheckResult.Invalid, null, status) : (CheckResult.Exhausted, st.Until, status);
            }
            return (CheckResult.Other, null, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (CheckResult.NoConnection, null, 0);
        }
    }

    /// <summary>Ключ хоть раз успешно отвечал (запрос или «Проверить») в этом запуске — окно пишет «рабочий», а не «не проверен».</summary>
    public static bool IsVerified(string key)
    {
        lock (Sync)
            return Verified.Contains(key);
    }
}
