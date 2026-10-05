using System.Collections.Generic;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Клиентская база NurCRM: /api/main/clients/ (используется для продаж «в долг»).</summary>
public interface IClientsApiService
{
    /// <summary>GET /api/main/clients/ (?search=, постранично до конца списка).</summary>
    Task<List<JsonElement>> GetClientsAsync(string? search, CancellationToken ct = default);

    /// <summary>2026-10-04: одна страница GET /api/main/clients/ и есть ли следующая — окно оплаты
    /// долга показывает первую страницу сразу, остальные догружает фоном.</summary>
    Task<(List<JsonElement> Items, bool HasNext)> GetClientsPageAsync(int page, string? search, CancellationToken ct = default);

    /// <summary>POST /api/main/clients/ (type=client).</summary>
    /// <param name="address">Адрес клиента — необязателен. Нужен прежде всего долгам: если
    /// покупатель перестал приходить, по телефону его не всегда найти, а по адресу можно.
    /// Поле есть в карточке клиента на сервере («address»), касса его просто не заполняла.</param>
    Task<JsonElement> CreateClientAsync(string fullName, string phone, string? email, string? address = null, CancellationToken ct = default);

    /// <summary>PATCH /api/main/clients/{id}/.</summary>
    Task<JsonElement> UpdateClientAsync(string id, string fullName, string phone, string? email, string? address = null, CancellationToken ct = default);

    /// <summary>DELETE /api/main/clients/{id}/.</summary>
    Task DeleteClientAsync(string id, CancellationToken ct = default);

    /// <summary>2026-10-05, сервер NurCRM (появилось 05.10 в 14:44): POST /api/main/clients/resolve-qr/ {"token": "NURCRMT…"} —
    /// QR из приложения покупателя → клиент этой компании; нет такого — сервер заводит его из профиля приложения.</summary>
    Task<JsonElement> ResolveQrAsync(string qrText, CancellationToken ct = default);
}
