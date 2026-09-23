using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Авто-refresh JWT при 401: POST api/users/auth/refresh/, сохранение в DPAPI, повтор запроса.
/// </summary>
public sealed class JwtBearerRefreshHandler : DelegatingHandler
{
    private readonly NurMarketApiClient _api;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _refreshInProgress;

    public JwtBearerRefreshHandler(NurMarketApiClient api) => _api = api;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ApiPathNormalizer.ApplyToRequest(request);

        // login / refresh — только транспорт, без Bearer и без повторного refresh при 401
        if (IsAuthEndpoint(request))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        _api.ApplyBearerAuthorization(request);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized
            || string.IsNullOrEmpty(_api.RefreshToken)
            || Volatile.Read(ref _refreshInProgress) != 0)
        {
            return response;
        }

        response.Dispose();

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _refreshInProgress) != 0)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };

            Interlocked.Exchange(ref _refreshInProgress, 1);
            try
            {
                if (!await _api.RefreshAccessAndPersistAsync(cancellationToken).ConfigureAwait(false))
                {
                    _api.ClearSession();
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
                }

                using var retryRequest = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
                ApiPathNormalizer.ApplyToRequest(retryRequest);
                _api.ApplyBearerAuthorization(retryRequest);
                return await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInProgress, 0);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static bool IsAuthEndpoint(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return path.Contains("/auth/login", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/auth/refresh", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
            clone.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refreshGate.Dispose();
        base.Dispose(disposing);
    }
}
