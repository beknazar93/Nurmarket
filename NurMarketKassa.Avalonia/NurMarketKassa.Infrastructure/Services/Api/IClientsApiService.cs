using System.Collections.Generic;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Клиентская база NurCRM: /api/main/clients/ (используется для продаж «в долг»).</summary>
public interface IClientsApiService
{
    /// <summary>GET /api/main/clients/ (?search=, постранично до конца списка).</summary>
    Task<List<JsonElement>> GetClientsAsync(string? search, CancellationToken ct = default);

    /// <summary>POST /api/main/clients/ (type=client).</summary>
    /// <param name="address">Адрес клиента — необязателен. Нужен прежде всего долгам: если
    /// покупатель перестал приходить, по телефону его не всегда найти, а по адресу можно.
    /// Поле есть в карточке клиента на сервере («address»), касса его просто не заполняла.</param>
    Task<JsonElement> CreateClientAsync(string fullName, string phone, string? email, string? address = null, CancellationToken ct = default);

    /// <summary>PATCH /api/main/clients/{id}/.</summary>
    Task<JsonElement> UpdateClientAsync(string id, string fullName, string phone, string? email, string? address = null, CancellationToken ct = default);

    /// <summary>DELETE /api/main/clients/{id}/.</summary>
    Task DeleteClientAsync(string id, CancellationToken ct = default);
}
