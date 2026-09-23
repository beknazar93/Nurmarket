namespace NurMarketKassa.Services.Api;

/// <summary>One counted line for an inventory session (revision or write-off draft).</summary>
public sealed record InventorySessionItem(string ProductId, double QuantityFact);

/// <summary>
/// Ревизия/списание оформляются через один и тот же серверный механизм "инвентаризационных
/// актов": черновик с фактическим количеством по каждой позиции, затем проведение — сервер
/// сам считает разницу с текущим остатком и списывает/приходует её.
/// </summary>
public interface IInventoryApiService
{
    /// <summary>POST /api/main/inventory/sessions/ — черновик акта.</summary>
    Task<string?> CreateSessionAsync(string note, IReadOnlyList<InventorySessionItem> items, CancellationToken ct = default);

    /// <summary>POST /api/main/inventory/sessions/{id}/apply/ — проведение акта.</summary>
    Task ApplySessionAsync(string sessionId, bool allowNegative, CancellationToken ct = default);
}
