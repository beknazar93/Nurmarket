using System.Net.Http;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 («темы с сайта»): скачивание небольшого файла (пакета темы) с ПРЕДЕЛОМ размера и без разбора как JSON — нужен точный набор байт для проверки sha256,
/// и код ответа (403 «не куплено» отличается от 404 и обрыва связи). Отдельный файл-часть, общий путь запросов кассы не тронут.</summary>
public sealed partial class NurMarketApiClient
{
    /// <summary>GET файла с Bearer-токеном. Status 0 — нет связи/таймаут. TooLarge — файл больше <paramref name="maxBytes"/> (дальше заголовка Content-Length и первых maxBytes+1 байт не читаем).</summary>
    internal async Task<(int Status, byte[]? Body, bool TooLarge)> DownloadLimitedAsync(string relativePath, long maxBytes, CancellationToken ct = default, TimeSpan? requestTimeout = null)
    {
        if (string.IsNullOrEmpty(AccessToken))
            return (401, null, false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, OutageCancelToken);
        linked.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(30));
        var slot = await AcquireSlotAsync(linked.Token).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUri(ApiPathNormalizer.EnsureTrailingSlash(relativePath, HttpMethod.Get), null));
            ApplyBearerAuthorization(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Тело ошибки (например {"code":"not_purchased"}) читаем коротко — оно нужно только для кода причины.
                var errBody = await ReadCappedAsync(resp, 16 * 1024, linked.Token).ConfigureAwait(false);
                return ((int)resp.StatusCode, errBody.Body, false);
            }
            if (resp.Content.Headers.ContentLength is { } len && len > maxBytes)
                return ((int)resp.StatusCode, null, true);
            var data = await ReadCappedAsync(resp, maxBytes, linked.Token).ConfigureAwait(false);
            return data.TooLarge ? ((int)resp.StatusCode, null, true) : ((int)resp.StatusCode, data.Body, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return (0, null, false);
        }
        finally
        {
            slot.Release();
        }
    }

    private static async Task<(byte[]? Body, bool TooLarge)> ReadCappedAsync(HttpResponseMessage resp, long maxBytes, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes)
                return (null, true);
            ms.Write(buffer, 0, read);
        }
        return (ms.ToArray(), false);
    }
}
