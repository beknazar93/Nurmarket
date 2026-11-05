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

    /// <summary>2026-11-02 (аудит №2, К3-10): сервер окончательно не принял начисление/списание баллов (например, «Недостаточно бонусов» — баллы потратили на другой кассе) или
    /// очередь бонусов исчерпала попытки: скидка бонусами выдана, а баллы на сервере не списаны — владелец должен увидеть.</summary>
    public const string BonusRejected = "bonus_rejected";

    /// <summary>2026-11-01: смена закрыта «с пометкой» — внесения/изъятия не успели уйти на сервер (CashShiftService). Строка для владельца: что и на какую сумму.</summary>
    public const string ShiftClosedUnsentCash = "shift_closed_unsent_cash";

    /// <summary>2026-11-05 (сервер ceba67c, warning below_cost): чек из очереди проведён, но сервер предупредил — позиция продана ниже закупки. Справка для владельца, чек проведён.</summary>
    public const string SoldBelowCost = "sold_below_cost";

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
