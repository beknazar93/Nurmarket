using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Доменный сервис смен: список касс/смен, открытие и закрытие смены на сервере.
/// </summary>
public interface IShiftApiService
{
    /// <summary>GET /api/construction/cashboxes/ — при 404 возвращает [].</summary>
    Task<JsonElement> ConstructionCashboxesListAsync(CancellationToken ct = default);

    /// <summary>GET /api/construction/shifts/ — список смен (статус смены с сервера).</summary>
    Task<JsonElement> ConstructionShiftsListAsync(CancellationToken ct = default);

    /// <summary>POST открытия смены (перебор URL и вариантов тела).</summary>
    Task<JsonElement> ConstructionShiftOpenAsync(string cashboxId, string openingCash = "0.00", CancellationToken ct = default);

    /// <summary>POST закрытия смены (перебор URL).</summary>
    Task<JsonElement> ConstructionShiftCloseAsync(string shiftId, string? closingCash = null, CancellationToken ct = default);
}
