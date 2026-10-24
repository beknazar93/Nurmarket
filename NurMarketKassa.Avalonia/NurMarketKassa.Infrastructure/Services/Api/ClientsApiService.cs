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

        // 2026-10-23 (аудит, круг 6: «Клиенты» у владельца 3,1 с): страницы шли строго по очереди. Первая страница даёт
        // count и размер страницы — остальные берём параллельно (по 4), те же запросы, без новых. Нет count — как раньше.
        var (first, firstNext, count) = await GetClientsPageCoreAsync(1, search, ct).ConfigureAwait(false);
        if (first.Count == 0)
            return result;
        result.AddRange(first);
        if (!firstNext)
            return result;

        if (count > first.Count)
        {
            var pages = Math.Min(MaxPages, (int)Math.Ceiling(count / (double)first.Count));
            if (pages >= 3)
            {
                var gate = new SemaphoreSlim(4);
                var tasks = Enumerable.Range(2, pages - 1).Select(async n =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try { return (await GetClientsPageCoreAsync(n, search, ct).ConfigureAwait(false)).Items; }
                    finally { gate.Release(); }
                }).ToList();
                foreach (var items in await Task.WhenAll(tasks).ConfigureAwait(false))
                    result.AddRange(items);
                return result;
            }
        }

        for (var page = 2; page <= MaxPages; page++)
        {
            var (pageItems, hasNext) = await GetClientsPageAsync(page, search, ct).ConfigureAwait(false);
            if (pageItems.Count == 0)
                break;

            result.AddRange(pageItems);
            if (!hasNext)
                break;
        }

        return result;
    }

    /// <summary>2026-10-04: одна страница списка — тот же запрос, что был телом цикла
    /// <see cref="GetClientsAsync"/>. Окно оплаты долга показывает первую страницу сразу, остальные
    /// догружает фоном (раньше ждало все страницы, при молчащем сервере — до 55 с).</summary>
    public async Task<(List<JsonElement> Items, bool HasNext)> GetClientsPageAsync(
        int page, string? search, CancellationToken ct = default)
    {
        var (items, hasNext, _) = await GetClientsPageCoreAsync(page, search, ct).ConfigureAwait(false);
        return (items, hasNext);
    }

    private async Task<(List<JsonElement> Items, bool HasNext, int Count)> GetClientsPageCoreAsync(
        int page, string? search, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["page"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(search))
            query["search"] = search.Trim();

        var data = await _client
            .RequestAsync(HttpMethod.Get, "api/main/clients/", null, query, ct)
            .ConfigureAwait(false);

        var pageItems = NurMarketApiClient.UnwrapList(data);
        var hasNext = page < MaxPages
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("next", out var next)
            && next.ValueKind == JsonValueKind.String;
        var count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : 0;
        return (pageItems, hasNext && pageItems.Count > 0, count);
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

    public Task<JsonElement> ResolveQrAsync(string qrText, CancellationToken ct = default) =>
        _client.RequestAsync(HttpMethod.Post, "api/main/clients/resolve-qr/", new Dictionary<string, string> { ["token"] = qrText.Trim() }, null, ct);
}
