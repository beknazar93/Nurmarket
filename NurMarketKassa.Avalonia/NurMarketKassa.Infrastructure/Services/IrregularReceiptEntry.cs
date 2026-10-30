namespace NurMarketKassa.Services;

/// <summary>
/// Постоянный (не удаляется при синхронизации) журнал чеков, прошедших нестандартно —
/// «Некорректные чеки» (этап 2 бэклога «Доработки и добавление функционала»).
/// </summary>
public sealed class IrregularReceiptEntry
{
    public const string InsufficientStock = "insufficient_stock";

    /// <summary>2026-10-20 (живой аудит кассы, К1): сервер записал возврат больше положенного (скидка на чек не учтена) —
    /// кассиру показана сумма кассы, расхождение — сюда, чтобы владелец видел.</summary>
    public const string ReturnOverpaid = "return_overpaid";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>insufficient_stock | ... (расширяется на этапе 3 логами системных сбоев).</summary>
    public string Tag { get; set; } = InsufficientStock;

    public string? CashierName { get; set; }

    public string? Note { get; set; }

    public double Total { get; set; }

    public string CartJson { get; set; } = "{}";

    /// <summary>2026-10-30 («Некорректные чеки»: записи висели вечно): запись разобрана и закрыта вручную — скрыта из списка, но хранится.
    /// Поля лежат в JSON записи (схема таблицы не меняется).</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Почему закрыта (текст кассира или причина по умолчанию) и кто закрыл.</summary>
    public string? ClosedReason { get; set; }

    public string? ClosedBy { get; set; }
}
