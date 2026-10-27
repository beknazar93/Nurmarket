namespace NurMarketKassa.Services.Api;

/// <summary>One counted line for an inventory session (revision or write-off draft).</summary>
public sealed record InventorySessionItem(string ProductId, double QuantityFact);

/// <summary>2026-10-24 (ТЗ «Склад», пакет Р1): состояние акта на сервере — чтобы повтор проведения не создавал второй акт и не проводил первый дважды.</summary>
public sealed record InventorySessionInfo(string Id, string Status, bool IsApplied);

/// <summary>2026-10-27, аудит владельца В-2: одна строка акта, прочитанного с сервера (GET inventory/sessions/): было / стало / разница.</summary>
public sealed record InventoryActLine(string ProductId, string Name, double Before, double Fact, double Delta);

/// <summary>2026-10-27, аудит владельца В-2: акт (ревизия, списание, приход) любой кассы компании — для «Истории списаний» и «Прошлых ревизий»
/// программы владельца, которые раньше показывали только этот компьютер. CreatedUtc — время проведения (или создания), UTC.</summary>
public sealed record InventoryAct(string Id, string Note, string Status, bool IsApplied, DateTime CreatedUtc, string CreatedById,
    string CreatedByName, IReadOnlyList<InventoryActLine> Lines);

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

    /// <summary>2026-10-24: GET /api/main/inventory/sessions/{id}/ — проведён ли акт. null — сервер не отдал состояние (акта нет / не умеет);
    /// по умолчанию null, чтобы чужие реализации интерфейса не ломались.</summary>
    Task<InventorySessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult<InventorySessionInfo?>(null);

    /// <summary>2026-10-27, аудит владельца В-2: GET /api/main/inventory/sessions/ — акты всех касс компании (новые сверху). null — сервер
    /// недоступен / не отдал список (вызывающий оставляет местный журнал); по умолчанию null, чтобы чужие реализации не ломались.</summary>
    Task<IReadOnlyList<InventoryAct>?> ListActsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InventoryAct>?>(null);
}
