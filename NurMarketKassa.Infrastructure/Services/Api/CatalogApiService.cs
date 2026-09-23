using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Реализация каталожных запросов поверх настроенного транспорта <see cref="NurMarketApiClient"/>.
/// </summary>
public sealed class CatalogApiService : ICatalogApiService
{
    private readonly NurMarketApiClient _client;

    public CatalogApiService(NurMarketApiClient client) => _client = client;

    public async Task<List<JsonElement>> GetAgentProductsAsync(CancellationToken ct = default)
    {
        foreach (var path in new[]
                 {
                     "api/main/agents/me/products/",
                     "api/main/products/agent-stock/",
                     "api/main/agents/products/",
                 })
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                var list = NurMarketApiClient.UnwrapList(data);
                if (list.Count > 0)
                    return list;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                /* next path */
            }
        }

        return new List<JsonElement>();
    }

    public async Task<bool> SetProductFavoriteAsync(string productId, bool isFavorite, CancellationToken ct = default)
    {
        var pid = productId.Trim();
        if (pid.Length == 0)
            return false;

        var escaped = Uri.EscapeDataString(pid);
        var bodyFavorite = new Dictionary<string, string> { ["is_favorite"] = isFavorite ? "true" : "false" };
        var bodyToggle = new Dictionary<string, string>();

        var attempts = new List<(HttpMethod Method, string Path, Dictionary<string, string>? Body)>
        {
            (HttpMethod.Patch, $"api/main/products/{escaped}/", bodyFavorite),
            (HttpMethod.Patch, $"api/main/products/list/{escaped}/", bodyFavorite),
            (HttpMethod.Post, $"api/main/products/{escaped}/favorite/", bodyToggle),
            (HttpMethod.Post, $"api/main/products/list/{escaped}/favorite/", bodyToggle),
            (HttpMethod.Post, $"api/main/products/{escaped}/toggle-favorite/", bodyToggle),
            (HttpMethod.Put, $"api/main/products/{escaped}/favorite/", bodyFavorite),
        };

        foreach (var (method, path, body) in attempts)
        {
            try
            {
                await _client.RequestAsync(method, path, body, null, ct).ConfigureAwait(false);
                return true;
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                /* next */
            }
        }

        return false;
    }

    public async Task<List<ProductDto>> ProductsSearchAsync(string query, int limit = 40, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new List<ProductDto>();

        limit = Math.Clamp(limit, 1, 20000);
        var list = new List<ProductDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 1;
        var hasNext = true;

        while (hasNext && list.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var qs = new Dictionary<string, string>
            {
                ["search"] = q,
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
                ["page_size"] = "300",
            };

            var response = await _client.RequestDataAsync<ApiListResponse<ProductDto>>(
                HttpMethod.Get, "api/main/products/list/", null, qs, ct).ConfigureAwait(false);

            if (response?.Results == null || response.Results.Count == 0)
                break;

            foreach (var item in response.Results)
            {
                if (string.IsNullOrEmpty(item.Id) || !seen.Add(item.Id))
                    continue;
                list.Add(item);
                if (list.Count >= limit)
                    break;
            }

            hasNext = response.Results.Count >= 300;
            page++;
            if (page > 100)
                break;
        }

        return list;
    }

    public async Task<CatalogVersionInfo?> ProductsCatalogVersionAsync(CancellationToken ct = default)
    {
        foreach (var path in new[]
                 {
                     "api/main/products/catalog-meta/",
                     "api/main/products/meta/",
                     "api/main/catalog/version/",
                     "api/main/products/version/",
                 })
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                var parsed = CatalogVersionParser.TryParseMeta(data);
                if (parsed != null && !parsed.IsEmpty)
                {
                    parsed = new CatalogVersionInfo
                    {
                        CatalogVersion = parsed.CatalogVersion,
                        LastModified = parsed.LastModified,
                        Token = parsed.Token,
                        Source = path,
                    };
                    return parsed;
                }
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        foreach (var path in new[] { "api/main/products/list/", "api/main/products/" })
        {
            try
            {
                var qs = new Dictionary<string, string>
                {
                    ["page"] = "1",
                    ["page_size"] = "1",
                    ["ordering"] = "-updated_at",
                };
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                var parsed = CatalogVersionParser.TryParseListProbe(data);
                if (parsed != null && !parsed.IsEmpty)
                {
                    parsed = new CatalogVersionInfo
                    {
                        CatalogVersion = parsed.CatalogVersion,
                        LastModified = parsed.LastModified,
                        Token = parsed.Token,
                        Source = $"{path} (probe)",
                    };
                    return parsed;
                }
            }
            catch (ApiException ex) when (ex.StatusCode is 404 or 405)
            {
                continue;
            }
        }

        return null;
    }

    public async Task<List<JsonElement>> ProductsCatalogAsync(int limit, int maxPages, CancellationToken ct = default)
    {
        var outList = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new[] { "api/main/products/list/", "api/main/products/" };
        ApiException? last404 = null;
        limit = Math.Max(1, limit);
        maxPages = maxPages <= 0 ? 100 : Math.Max(1, maxPages);
        const int pageSize = 300;

        foreach (var path in paths)
        {
            outList.Clear();
            seen.Clear();

            try
            {
                var page = 1;
                var hasNext = true;

                while (hasNext && outList.Count < limit && page <= maxPages)
                {
                    ct.ThrowIfCancellationRequested();
                    var qs = new Dictionary<string, string>
                    {
                        ["page"] = page.ToString(CultureInfo.InvariantCulture),
                        ["page_size"] = pageSize.ToString(CultureInfo.InvariantCulture),
                    };

                    var data = await _client.RequestAsync(HttpMethod.Get, path, null, qs, ct).ConfigureAwait(false);
                    var batch = NurMarketApiClient.UnwrapList(data);
                    if (batch.Count == 0)
                        break;

                    foreach (var p in batch)
                    {
                        var pid = TryProductIdString(p);
                        if (string.IsNullOrEmpty(pid) || !seen.Add(pid))
                            continue;
                        outList.Add(p);
                        if (outList.Count >= limit)
                            return outList;
                    }

                    hasNext = HasNextPage(data, batch.Count, pageSize);
                    page++;
                }

                if (outList.Count > 0)
                    return outList;
            }
            catch (ApiException e)
            {
                last404 = e;
                if (e.StatusCode != 404)
                    throw;
            }
        }

        if (last404 != null && outList.Count == 0)
            throw last404;
        return outList;
    }

    public async Task<JsonElement?> ProductsDetailAsync(string productId, CancellationToken ct = default)
    {
        var pid = Uri.EscapeDataString(productId.Trim());
        if (pid.Length == 0)
            return null;
        foreach (var path in new[] { $"api/main/products/{pid}/", $"api/main/products/list/{pid}/" })
        {
            try
            {
                var data = await _client.RequestAsync(HttpMethod.Get, path, null, null, ct).ConfigureAwait(false);
                if (data.ValueKind != JsonValueKind.Object)
                    continue;
                if (data.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object &&
                    inner.TryGetProperty("id", out _))
                    return inner.Clone();
                if (data.TryGetProperty("id", out _))
                    return data.Clone();
            }
            catch (ApiException e)
            {
                if (e.StatusCode is 404 or 405 or 410)
                    continue;
                return null;
            }
        }

        return null;
    }

    public async Task<bool> TrySetProductStockAsync(string productId, double quantity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return false;

        var escaped = Uri.EscapeDataString(productId.Trim());
        var qtyText = quantity.ToString("0.###", CultureInfo.InvariantCulture);
        var bodies = new[]
        {
            new Dictionary<string, string> { ["stock_quantity"] = qtyText, ["quantity"] = qtyText },
            new Dictionary<string, string> { ["quantity"] = qtyText },
        };

        var paths = new[]
        {
            $"api/main/products/{escaped}/",
            $"api/main/products/list/{escaped}/",
            $"api/main/agents/me/products/{escaped}/",
        };

        foreach (var path in paths)
        {
            foreach (var body in bodies)
            {
                try
                {
                    await _client.RequestAsync(HttpMethod.Patch, path, body, null, ct).ConfigureAwait(false);
                    return true;
                }
                catch (ApiException ex) when (ex.StatusCode is 404 or 405)
                {
                    /* next */
                }
            }
        }

        return false;
    }

    private static bool HasNextPage(JsonElement data, int batchCount, int pageSize)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
            {
                var url = next.GetString();
                return !string.IsNullOrWhiteSpace(url);
            }
        }

        return batchCount >= pageSize;
    }

    private static string? TryProductIdString(JsonElement p)
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
