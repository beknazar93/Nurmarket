using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-09-28, BE-11: запрос со своими заголовками. Обычный <see cref="RequestAsync"/>
/// заголовков не принимает, а новый адрес оплаты NurCRM (POST api/main/pos/checkout/) без
/// заголовка Idempotency-Key отвечает 400. Отдельный метод в отдельном файле, чтобы не трогать
/// общий путь запросов, которым пользуется вся касса. Повтор при 401 делает тот же
/// JwtBearerRefreshHandler и переносит заголовки в повторный запрос.</summary>
public sealed partial class NurMarketApiClient
{
    internal async Task<(JsonElement Body, int StatusCode)> RequestWithHeadersAsync(
        HttpMethod method,
        string relativePath,
        object? jsonBody,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default,
        TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(AccessToken))
            throw new ApiException(AuthInvalidHintRu, 401);

        // 2026-10-04: + отмена при объявлении аварии (см. NurMarketApiClient._outageCancel).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, OutageCancelToken);
        if (requestTimeout.HasValue)
            linked.CancelAfter(requestTimeout.Value);

        await _httpSlots.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            relativePath = ApiPathNormalizer.EnsureTrailingSlash(relativePath, method);
            using var req = new HttpRequestMessage(method, BuildUri(relativePath, null));
            ApplyBearerAuthorization(req);
            foreach (var (name, value) in headers)
                req.Headers.TryAddWithoutValidation(name, value);
            if (jsonBody is not null)
            {
                var json = JsonSerializer.Serialize(jsonBody, _jsonWrite);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
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

            // 2026-09-29: 200 с HTML/обрывком JSON — ApiException 502 (см. ParseSuccessBody): оплата
            // повторит тем же ключом идемпотентности, а не покажет кассиру ошибку разбора.
            return (ParseSuccessBody(text, relativePath), (int)resp.StatusCode);
        }
        finally
        {
            _httpSlots.Release();
        }
    }
}
