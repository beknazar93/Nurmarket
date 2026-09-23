using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Базовый класс доменных API-сервисов: транспортный слой поверх настроенного
/// <see cref="HttpClient"/> и разделяемой <see cref="ApiSession"/>.
/// Содержит унифицированный конвейер запросов (Bearer-токен, branch=…, авто-refresh при 401)
/// и низкоуровневые JSON-помощники.
/// </summary>
public abstract class ApiServiceBase
{
    public const string AuthInvalidHintRu =
        "Сессия недействительна (часто из‑за входа с другого ПК или телефона). " +
        "Нажмите «Выйти» в кассе и войдите снова.";

    protected HttpClient Http { get; }
    protected ApiSession Session { get; }

    protected ApiServiceBase(HttpClient http, ApiSession session)
    {
        Http = http;
        Session = session;
    }

    /// <summary>Единая точка настройки HttpClient (BaseUrl, Accept). Идемпотентна.</summary>
    public static void ConfigureHttpClient(HttpClient http, AppSettings settings)
    {
        if (http.BaseAddress is null)
        {
            var baseUrl = settings.ApiBaseUrl.Trim().TrimEnd('/') + "/";
            http.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        }

        if (!http.DefaultRequestHeaders.Accept.Any(h =>
                string.Equals(h.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
    }

    /// <summary>
    /// Оптимизированный запрос с потоковой десериализацией (горячие пути: каталог, поиск).
    /// </summary>
    protected async Task<T?> RequestDataAsync<T>(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(Session.AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        await Session.HttpSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, method);
            var uri = BuildUri(relativePath, query);
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session.AccessToken);
            if (jsonBody is not null)
            {
                var json = JsonSerializer.Serialize(jsonBody, Session.JsonWrite);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(Session.RefreshToken))
            {
                if (await RefreshAccessUnlockedAsync(linked.Token).ConfigureAwait(false))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session.AccessToken);
                    using var retryResp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                    retryResp.EnsureSuccessStatusCode();
                    using var retryStream = await retryResp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                    return await JsonSerializer.DeserializeAsync<T>(retryStream, Session.JsonRead, linked.Token).ConfigureAwait(false);
                }

                Session.Clear();
                throw new ApiException(AuthInvalidHintRu, 401);
            }

            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, Session.JsonRead, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            Session.HttpSlots.Release();
        }
    }

    /// <summary>Универсальный запрос с Bearer и query branch=… (как branch_params() в Python).</summary>
    protected async Task<JsonElement> RequestAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(Session.AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        await Session.HttpSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await SendOnceAsync(method, relativePath, jsonBody, query, retryRefresh: true, linked.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            Session.HttpSlots.Release();
        }
    }

    private async Task<JsonElement> SendOnceAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string>? query,
        bool retryRefresh,
        CancellationToken ct)
    {
        relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, method);
        var uri = BuildUri(relativePath, query);
        using var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session.AccessToken);
        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, Session.JsonWrite);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && retryRefresh && !string.IsNullOrEmpty(Session.RefreshToken))
        {
            if (await RefreshAccessUnlockedAsync(ct).ConfigureAwait(false))
                return await SendOnceAsync(method, relativePath, jsonBody, query, retryRefresh: false, ct).ConfigureAwait(false);
            Session.Clear();
        }
        else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && string.IsNullOrEmpty(Session.RefreshToken))
        {
            Session.Clear();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var msg = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? AuthInvalidHintRu
                : ApiErrorParser.Parse(resp, text);
            JsonElement? payload = TryParse(text);
            throw new ApiException(msg, (int)resp.StatusCode, payload);
        }

        if (string.IsNullOrWhiteSpace(text))
            return default;

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<bool> RefreshAccessUnlockedAsync(CancellationToken ct)
    {
        await Session.RefreshSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrEmpty(Session.RefreshToken))
                return false;
            var body = new Models.Auth.RefreshRequest { Refresh = Session.RefreshToken };
            using var content = new StringContent(JsonSerializer.Serialize(body, Session.JsonWrite), Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync("api/users/auth/refresh/", content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return false;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("access", out var acc) && acc.ValueKind == JsonValueKind.String)
            {
                Session.AccessToken = acc.GetString();
                return true;
            }

            return false;
        }
        finally
        {
            Session.RefreshSync.Release();
        }
    }

    protected Uri BuildUri(string relativePath, IReadOnlyDictionary<string, string>? query)
    {
        var path = relativePath.TrimStart('/');
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(Session.ActiveBranchId))
            qs.Add("branch=" + Uri.EscapeDataString(Session.ActiveBranchId));
        if (query is not null)
        {
            foreach (var kv in query)
                qs.Add(Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value ?? ""));
        }

        var rel = qs.Count == 0 ? path : path + "?" + string.Join("&", qs);
        return new Uri(Http.BaseAddress!, rel);
    }

    /// <summary>Скачивание бинарника с авторизацией (превью с того же API).</summary>
    public async Task<byte[]?> DownloadAuthorizedAsync(string absoluteUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteUrl))
            return null;
        if (string.IsNullOrEmpty(Session.AccessToken))
            return null;

        await Session.HttpSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session.AccessToken);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Session.HttpSlots.Release();
        }
    }

    protected static JsonElement? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            using var d = JsonDocument.Parse(text);
            return d.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    protected static List<JsonElement> UnwrapList(JsonElement data)
    {
        var list = new List<JsonElement>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
                list.Add(el.Clone());
            return list;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("results", out var r) &&
            r.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in r.EnumerateArray())
                list.Add(el.Clone());
        }

        return list;
    }

    protected static JsonElement UnwrapListRootElement(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner))
        {
            if (inner.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return inner.Clone();
        }

        return data.Clone();
    }

    protected static JsonElement UnwrapDataObject(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var inner) &&
            inner.ValueKind == JsonValueKind.Object)
            return inner.Clone();
        return data.Clone();
    }

    protected static string? TryProductIdString(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var id))
            return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(id.GetString()) ? null : id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }
}
