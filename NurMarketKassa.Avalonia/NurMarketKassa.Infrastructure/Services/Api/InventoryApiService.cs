using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Реализация ревизии/списания поверх настроенного транспорта <see cref="NurMarketApiClient"/>.</summary>
public sealed class InventoryApiService : IInventoryApiService
{
    private readonly NurMarketApiClient _client;

    public InventoryApiService(NurMarketApiClient client) => _client = client;

    public async Task<string?> CreateSessionAsync(
        string note, IReadOnlyList<InventorySessionItem> items, CancellationToken ct = default)
    {
        var body = new
        {
            note = note ?? "",
            items = items.Select(i => new
            {
                product_id = i.ProductId,
                // 2026-09-12: было "0.###" (до 3 знаков) — сервер отвечал "Убедитесь, что вы
                // ввели не более 2 цифры после запятой" и вся ревизия падала, стоило только
                // взвешенному товару дать вес с 3 знаками (например 0.711 кг).
                quantity_fact = i.QuantityFact.ToString("0.##", CultureInfo.InvariantCulture),
            }).ToList(),
        };

        var response = await _client
            .RequestAsync(HttpMethod.Post, "api/main/inventory/sessions/", body, null, ct)
            .ConfigureAwait(false);

        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("id", out var idEl) &&
            idEl.ValueKind == JsonValueKind.String)
            return idEl.GetString();

        return null;
    }

    public Task ApplySessionAsync(string sessionId, bool allowNegative, CancellationToken ct = default)
    {
        var escaped = Uri.EscapeDataString(sessionId.Trim());
        var body = new { allow_negative = allowNegative };
        return _client.RequestAsync(HttpMethod.Post, $"api/main/inventory/sessions/{escaped}/apply/", body, null, ct);
    }

    // 2026-10-24 (ТЗ «Склад», Р1): состояние акта — как у мобилки (_sessionApplied): проведён, если is_applied / applied_at / статус «проведён».
    public async Task<InventorySessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var escaped = Uri.EscapeDataString(sessionId.Trim());
        JsonElement s;
        try
        {
            s = await _client.RequestAsync(HttpMethod.Get, $"api/main/inventory/sessions/{escaped}/", null, null, ct).ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }

        if (s.ValueKind != JsonValueKind.Object)
            return null;
        var status = s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
        var applied = (s.TryGetProperty("is_applied", out var ia) && ia.ValueKind == JsonValueKind.True)
            || (s.TryGetProperty("applied_at", out var at) && at.ValueKind != JsonValueKind.Null && at.ValueKind != JsonValueKind.Undefined)
            || status.ToLowerInvariant() is "applied" or "done" or "completed" or "closed" or "posted";
        return new InventorySessionInfo(sessionId, status, applied);
    }
}
