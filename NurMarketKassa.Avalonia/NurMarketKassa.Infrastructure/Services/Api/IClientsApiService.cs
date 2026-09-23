using System.Collections.Generic;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Клиентская база NurCRM: /api/main/clients/ (используется для продаж «в долг»).</summary>
public interface IClientsApiService
{
    /// <summary>GET /api/main/clients/ (?search=, постранично до конца списка).</summary>
    Task<List<JsonElement>> GetClientsAsync(string? search, CancellationToken ct = default);

    /// <summary>POST /api/main/clients/ (type=client).</summary>
    Task<JsonElement> CreateClientAsync(string fullName, string phone, string? email, CancellationToken ct = default);

    /// <summary>PATCH /api/main/clients/{id}/.</summary>
    Task<JsonElement> UpdateClientAsync(string id, string fullName, string phone, string? email, CancellationToken ct = default);

    /// <summary>DELETE /api/main/clients/{id}/.</summary>
    Task DeleteClientAsync(string id, CancellationToken ct = default);
}
