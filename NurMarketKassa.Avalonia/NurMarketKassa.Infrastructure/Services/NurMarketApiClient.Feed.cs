using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-27 (Б-46 сервера, CATALOG_FEED): GET с If-None-Match. Ответ 304 — не ошибка, а «ничего нового»; ETag ответа возвращается
/// вызывающему. Отдельный метод в отдельном файле (как NurMarketApiClient.Headers.cs), чтобы не трогать общий путь запросов кассы.
/// branch=… добавляется как у всех запросов (BuildUri).</summary>
public sealed partial class NurMarketApiClient
{
    internal async Task<(JsonElement Body, int StatusCode, string? ETag)> GetConditionalAsync(
        string relativePath,
        IReadOnlyDictionary<string, string>? query,
        string? ifNoneMatch,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, OutageCancelToken);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        await _httpSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, HttpMethod.Get);
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(relativePath, query));
            ApplyBearerAuthorization(req);
            if (!string.IsNullOrWhiteSpace(ifNoneMatch))
                req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

            using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
            var etag = resp.Headers.ETag?.ToString();
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                OnlineContactTracker.RecordSuccess();
                return (default, 304, etag ?? ifNoneMatch);
            }

            var text = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && string.IsNullOrEmpty(RefreshToken))
                ClearSession();

            if (!resp.IsSuccessStatusCode)
            {
                var msg = resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? AuthInvalidHintRu
                    : ApiErrorParser.Parse(resp, text);
                throw new ApiException(msg, (int)resp.StatusCode, TryParse(text));
            }

            OnlineContactTracker.RecordSuccess();
            return (ParseSuccessBody(text, relativePath), (int)resp.StatusCode, etag);
        }
        finally
        {
            _httpSlots.Release();
        }
    }
}
