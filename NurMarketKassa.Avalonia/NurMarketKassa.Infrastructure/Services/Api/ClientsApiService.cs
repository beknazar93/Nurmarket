using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Реализация клиентской базы поверх NurCRM (/api/main/clients/).</summary>
public sealed class ClientsApiService : IClientsApiService
{
    // Розничная база клиентов небольшая; ограничение на число страниц — просто
    // защита от бесконечного цикла, если сервер вернёт зацикленный next.
    private const int MaxPages = 40;

    private readonly NurMarketApiClient _client;

    public ClientsApiService(NurMarketApiClient client) => _client = client;

    public async Task<List<JsonElement>> GetClientsAsync(string? search, CancellationToken ct = default)
    {
        var result = new List<JsonElement>();

        for (var page = 1; page <= MaxPages; page++)
        {
            var query = new Dictionary<string, string>
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(search))
                query["search"] = search.Trim();

            var data = await _client
                .RequestAsync(HttpMethod.Get, "api/main/clients/", null, query, ct)
                .ConfigureAwait(false);

            var pageItems = NurMarketApiClient.UnwrapList(data);
            if (pageItems.Count == 0)
                break;

            result.AddRange(pageItems);

            var hasNext = data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String;
            if (!hasNext)
                break;
        }

        return result;
    }

    public Task<JsonElement> CreateClientAsync(
        string fullName,
        string phone,
        string? email,
        string? address = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["full_name"] = fullName.Trim(),
            ["phone"] = phone.Trim(),
            ["type"] = "client",
        };
        if (!string.IsNullOrWhiteSpace(email))
            body["email"] = email.Trim();
        if (!string.IsNullOrWhiteSpace(address))
            body["address"] = address.Trim();

        return _client.RequestAsync(HttpMethod.Post, "api/main/clients/", body, null, ct);
    }

    public Task<JsonElement> UpdateClientAsync(
        string id,
        string fullName,
        string phone,
        string? email,
        string? address = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>
        {
            ["full_name"] = fullName.Trim(),
            ["phone"] = phone.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(email))
            body["email"] = email.Trim();
        // Пустую строку отправляем намеренно: так адрес можно и стереть, а не только задать.
        if (address is not null)
            body["address"] = address.Trim();

        return _client.RequestAsync(HttpMethod.Patch, $"api/main/clients/{id}/", body, null, ct);
    }

    public Task DeleteClientAsync(string id, CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Delete, $"api/main/clients/{id}/", null, null, ct);
}
