using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;

namespace NurMarketKassa.Services;

/// <summary>
/// Проверяет последний релиз GitHub (см. <see cref="UpdateSettings.ManifestUrl"/>, обычно
/// вида https://api.github.com/repos/{owner}/{repo}/releases/latest) и сравнивает версию
/// тега с текущей сборкой. Ничего не скачивает и не устанавливает сама — только сообщает,
/// что есть новее, и отдаёт прямую ссылку на файл релиза.
/// </summary>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly UpdateSettings _settings;

    public UpdateCheckService(AppSettings appSettings) => _settings = appSettings.Updates;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub's REST API rejects requests without a User-Agent header.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NurMarketKassa", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        // GetEntryAssembly() is the running .exe (NurMarketKassa.Avalonia); GetExecutingAssembly()
        // would instead resolve to this Infrastructure.dll's own (unrelated, unversioned) assembly.
        var currentVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "0.0.0.0";

        if (string.IsNullOrWhiteSpace(_settings.ManifestUrl))
            return new UpdateCheckResult(false, false, currentVersion, null, null, null);

        try
        {
            using var response = await Http
                .GetAsync(_settings.ManifestUrl, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return new UpdateCheckResult(
                    true, false, currentVersion, null, null, "Релиз GitHub не содержит tag_name.");
            }

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("browser_download_url", out var urlProp))
                    {
                        downloadUrl = urlProp.GetString();
                        break;
                    }
                }
            }
            // No installable asset attached to the release — fall back to the release page itself.
            downloadUrl ??= root.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() : null;

            var isNewer = TryParseVersion(tagName, out var latest)
                && TryParseVersion(currentVersion, out var current)
                && latest > current;

            return new UpdateCheckResult(true, isNewer, currentVersion, tagName, downloadUrl, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            PosLogger.Log($"Update check failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
            return new UpdateCheckResult(true, false, currentVersion, null, null, ex.Message);
        }
    }

    private static bool TryParseVersion(string raw, out Version version)
    {
        var cleaned = raw.Trim().TrimStart('v', 'V');
        return Version.TryParse(cleaned, out version!);
    }
}
