using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>2026-09-09: реестр ключей активации автономного (офлайн) режима — файл JSON в
/// приватном GitHub-репозитории владельца (тот же, что для релизов), используется как дешёвая
/// замена отдельному серверу лицензий (по явному решению владельца — заводить новый сервер не
/// нужно). Один ключ — одна активация: этот класс читает файл, проверяет, что ключ там есть и
/// ещё не использован, и в ТОМ ЖЕ запросе помечает его использованным (GitHub Contents API,
/// PUT с sha предыдущей версии файла — защищает от гонки, если бы две активации пришли
/// одновременно, вторая получит конфликт и должна будет попробовать снова).
///
/// 2026-09-22 — ТОКЕН УБРАН ИЗ ИСХОДНИКА. Раньше он был зашит константой прямо здесь, и это
/// оказалось хуже, чем считалось при написании класса: токен давал запись во ВЕСЬ репозиторий,
/// а тот же самый репозиторий раздаёт автообновления (VelopackUpdateService). То есть любой,
/// кто достал строку из .exe (команда `strings`, минута работы), мог залить свой релиз — и все
/// установленные кассы скачали бы и поставили его сами. Это не утечка ключей активации, это
/// выполнение произвольного кода на кассах всех клиентов.
///
/// ЧЕСТНОЕ ОГРАНИЧЕНИЕ, которое надо понимать: любой секрет, положенный в клиент, публичен —
/// его нельзя спрятать ни обфускацией, ни шифрованием, потому что программа обязана уметь его
/// расшифровать. Поэтому «безопасного» варианта с токеном внутри кассы не существует в
/// принципе. Правильное решение — вынести реестр ключей за минимальный серверный эндпоинт,
/// который сам ходит в GitHub; касса тогда никаких прав на репозиторий не имеет.
///
/// Пока этого эндпоинта нет, токен берётся ИЗВНЕ бинарника (переменная среды или файл рядом с
/// .exe) и в репозиторий не попадает. Если его нет — активация автономного режима возвращает
/// понятную ошибку вместо падения.</summary>
public sealed class GithubLicenseRegistryClient
{
    /// <summary>Токен читается ИЗВНЕ и намеренно отсутствует в исходнике и в бинарнике.
    /// Порядок поиска: переменная среды NURMARKET_LICENSE_TOKEN, затем файл license-token.txt
    /// рядом с исполняемым файлом (он внесён в .gitignore). null — активация недоступна.</summary>
    private static readonly string? Token = ResolveToken();

    private static string? ResolveToken()
    {
        try
        {
            var fromEnv = Environment.GetEnvironmentVariable("NURMARKET_LICENSE_TOKEN");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                return fromEnv.Trim();

            var path = Path.Combine(AppContext.BaseDirectory, "license-token.txt");
            if (File.Exists(path))
            {
                var fromFile = File.ReadAllText(path).Trim();
                if (fromFile.Length > 0)
                    return fromFile;
            }
        }
        catch (Exception)
        {
            // Нет прав на чтение файла/переменной — ведём себя так же, как при отсутствии токена.
        }

        return null;
    }

    private const string Owner = "beknazar93";
    private const string Repo = "Nurmarket";
    private const string FilePath = "licenses/standalone-keys.json";
    private const string Branch = "main";

    // 2026-11-06 (аудит скорости, шаг 15 / Н-17, 25.2): клиент GitHub — из общей фабрики внешних клиентов (автораспаковка, смена DNS).
    private static readonly HttpClient SharedHttp = CreateClient(Token);

    private static HttpClient CreateClient(string? token)
    {
        var http = ExternalHttp.Create(TimeSpan.FromSeconds(20), "NurMarketKassa-Kassa");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    private readonly HttpClient _http;
    private readonly string? _token;

    /// <summary>Пауза перед перечитыванием реестра после неясного ответа (подменяется в тестах).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

    public GithubLicenseRegistryClient()
    {
        _http = SharedHttp;
        _token = Token;
    }

    /// <summary>Для тестов: свой HTTP-клиент (поддельный GitHub) и токен.</summary>
    internal GithubLicenseRegistryClient(HttpClient http, string? token)
    {
        _http = http;
        _token = token;
    }

    /// <summary>Чем кончилась запись реестра: Saved — GitHub принял; Conflict — файл изменился между чтением и записью (409/422);
    /// Failed — отказ (401/403/404…), запись точно не прошла; Unknown — срок или 5xx: запись могла пройти, а ответ потерялся.</summary>
    internal enum SaveOutcome { Saved, Conflict, Failed, Unknown }

    /// <summary>Что говорит реестр про ключ (по записи файла).</summary>
    internal enum KeyState { NotFound, Free, UsedByThisPc, UsedByOther }

    /// <summary>Метка этого ПК в реестре: первые 12 символов SHA-256 от ключа установки. Личных данных нет.</summary>
    internal static string DeviceTag()
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(DeviceIdentity.Key));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    internal static KeyState Classify(JsonObject registry, string key, string deviceTag)
    {
        if (!registry.TryGetPropertyValue(key, out var node) || node is not JsonObject entry)
            return KeyState.NotFound;
        var status = entry["status"]?.GetValue<string>();
        if (!string.Equals(status, "used", StringComparison.OrdinalIgnoreCase))
            return KeyState.Free;
        var device = entry["device"]?.GetValue<string>();
        return !string.IsNullOrEmpty(device) && string.Equals(device, deviceTag, StringComparison.Ordinal)
            ? KeyState.UsedByThisPc
            : KeyState.UsedByOther;
    }

    /// <summary>Пытается активировать ключ. true — ключ был в реестре, не использован, теперь
    /// помечен использованным. false с сообщением — ключ неверный/уже использован/сетевая
    /// ошибка (сообщение — то, что можно показать кассиру).</summary>
    public async Task<(bool Success, string? Error)> TryActivateAsync(string activationKey, CancellationToken ct = default)
    {
        var key = activationKey.Trim();
        if (key.Length == 0)
            return (false, "Введите ключ активации.");

        // Без токена запрос всё равно упрётся в 401/404 — лучше сказать это сразу и понятно,
        // чем показывать кассиру сетевую ошибку.
        if (string.IsNullOrWhiteSpace(_token))
            return (false, "Активация автономного режима сейчас недоступна на этом компьютере. Обратитесь в поддержку.");

        try
        {
            var (registry, sha) = await LoadRegistryAsync(ct).ConfigureAwait(false);

            var tag = DeviceTag();
            switch (Classify(registry, key, tag))
            {
                case KeyState.NotFound:
                    return (false, "Ключ не найден. Проверьте, что он введён без ошибок.");
                case KeyState.UsedByOther:
                    return (false, "Этот ключ уже был использован для активации на другом устройстве.");
                case KeyState.UsedByThisPc:
                    // 2026-11-06 (Н-17): ключ уже отмечен именно этим ПК (прошлая попытка прошла на GitHub, а ответ потерялся) — это успех, а не «ключ использован».
                    return (true, null);
            }

            var activationId = Guid.NewGuid().ToString("N");
            registry[key] = new JsonObject
            {
                ["status"] = "used",
                ["usedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["device"] = tag,
                ["activationId"] = activationId,
            };

            var outcome = await SaveRegistryAsync(registry, sha, $"Activate license key {MaskKey(key)}", ct).ConfigureAwait(false);
            switch (outcome)
            {
                case SaveOutcome.Saved:
                    return (true, null);
                case SaveOutcome.Unknown:
                    // 2026-11-06 (Н-17, деньги): запись мог принять GitHub, а ответ потерялся по сроку или 5xx. Повторная активация дала бы «ключ использован» —
                    // купленный ключ «сгорал». Перечитываем реестр и смотрим, наша ли там запись.
                    if (await VerifyOurWriteAsync(key, activationId, tag, ct).ConfigureAwait(false))
                        return (true, null);
                    return (false, Tr.T(
                        "Не удалось подтвердить активацию: сервер не ответил вовремя. Повторите через минуту — ключ на этом компьютере не пропадёт.",
                        "Активдештирүү ырасталган жок: сервер өз убагында жооп берген жок. Бир мүнөттөн кийин кайталаңыз — ключ бул компьютерде жоголбойт.",
                        "Activation could not be confirmed: the server did not respond in time. Try again in a minute — the key will not be lost on this computer.",
                        "Etkinleştirme doğrulanamadı: sunucu zamanında yanıt vermedi. Bir dakika sonra tekrar deneyin — anahtar bu bilgisayarda kaybolmaz.",
                        "Faollashtirish tasdiqlanmadi: server o'z vaqtida javob bermadi. Bir daqiqadan keyin qayta urinib ko'ring — kalit bu kompyuterda yo'qolmaydi."));
                default:
                    return (false, "Не удалось подтвердить активацию на сервере (возможно, кто-то активировал ключ одновременно). Попробуйте ещё раз.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "Активация отменена.");
        }
        catch (TaskCanceledException)
        {
            return (false, "Не удалось связаться с сервером активации — проверьте интернет-соединение.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Не удалось связаться с сервером активации: {ex.Message}");
        }
    }

    /// <summary>Перечитать реестр (до 3 раз с паузой 2 с) и проверить, что запись с нашим <paramref name="activationId"/> на месте.</summary>
    private async Task<bool> VerifyOurWriteAsync(string key, string activationId, string tag, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var (registry, _) = await LoadRegistryAsync(ct).ConfigureAwait(false);
                if (registry.TryGetPropertyValue(key, out var node) && node is JsonObject entry
                    && string.Equals(entry["activationId"]?.GetValue<string>(), activationId, StringComparison.Ordinal)
                    && Classify(registry, key, tag) == KeyState.UsedByThisPc)
                    return true;
                // Реестр прочитан, но нашей записи нет — запись не прошла; повтор чтения ничего не изменит.
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
            {
                PosLogger.Log($"Реестр ключей: перечитать после неясного ответа не вышло (попытка {attempt + 1}): {ex.GetType().Name}", "WARNING");
            }
        }
        return false;
    }

    private async Task<(JsonObject Registry, string Sha)> LoadRegistryAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/contents/{FilePath}?ref={Branch}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return (new JsonObject(), "");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var sha = doc.RootElement.GetProperty("sha").GetString() ?? "";
        var base64 = doc.RootElement.GetProperty("content").GetString() ?? "";
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Replace("\n", "")));
        var registry = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        return (registry, sha);
    }

    private async Task<SaveOutcome> SaveRegistryAsync(JsonObject registry, string previousSha, string message, CancellationToken ct)
    {
        var json = registry.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        var payload = new JsonObject
        {
            ["message"] = message,
            ["content"] = base64,
            ["branch"] = Branch,
        };
        if (!string.IsNullOrEmpty(previousSha))
            payload["sha"] = previousSha;

        var url = $"https://api.github.com/repos/{Owner}/{Repo}/contents/{FilePath}";
        try
        {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PutAsync(url, content, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return SaveOutcome.Saved;
            var code = (int)resp.StatusCode;
            if (code is 409 or 422)
                return SaveOutcome.Conflict;
            return code >= 500 || code == 408 ? SaveOutcome.Unknown : SaveOutcome.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or IOException)
        {
            // Срок или обрыв после отправки: запись могла пройти.
            PosLogger.Log($"Реестр ключей: ответ на запись не получен ({ex.GetType().Name}) — проверяю реестр.", "WARNING");
            return SaveOutcome.Unknown;
        }
    }

    private static string MaskKey(string key) =>
        key.Length <= 4 ? "****" : key[..4] + "****";
}
