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

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NurMarketKassa-Kassa");
        if (!string.IsNullOrWhiteSpace(Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", Token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
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
        if (string.IsNullOrWhiteSpace(Token))
            return (false, "Активация автономного режима сейчас недоступна на этом компьютере. Обратитесь в поддержку.");

        try
        {
            var (registry, sha) = await LoadRegistryAsync(ct).ConfigureAwait(false);

            if (registry.TryGetPropertyValue(key, out var existingNode) && existingNode is JsonObject existing)
            {
                var status = existing["status"]?.GetValue<string>();
                if (string.Equals(status, "used", StringComparison.OrdinalIgnoreCase))
                    return (false, "Этот ключ уже был использован для активации на другом устройстве.");
            }
            else
            {
                return (false, "Ключ не найден. Проверьте, что он введён без ошибок.");
            }

            registry[key] = new JsonObject
            {
                ["status"] = "used",
                ["usedAtUtc"] = DateTime.UtcNow.ToString("O"),
            };

            var saved = await SaveRegistryAsync(registry, sha, $"Activate license key {MaskKey(key)}", ct).ConfigureAwait(false);
            return saved
                ? (true, null)
                : (false, "Не удалось подтвердить активацию на сервере (возможно, кто-то активировал ключ одновременно). Попробуйте ещё раз.");
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

    private async Task<(JsonObject Registry, string Sha)> LoadRegistryAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/contents/{FilePath}?ref={Branch}";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);

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

    private async Task<bool> SaveRegistryAsync(JsonObject registry, string previousSha, string message, CancellationToken ct)
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
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PutAsync(url, content, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    private static string MaskKey(string key) =>
        key.Length <= 4 ? "****" : key[..4] + "****";
}
