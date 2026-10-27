namespace NurMarketKassa.Services;

/// <summary>2026-10-27 (аудит кассы, В-3): сумма возврата чека со скидкой на весь чек, когда у кассы нет своей записи о нём
/// (SaleReturnLedger) — первый частичный возврат сделан на сайте или другой кассе, либо запись потеряна. Раньше доля скидки
/// считалась равной 1 (возврат по полной цене строки), и сумма всех возвратов могла оказаться больше, чем заплатил покупатель.
/// Чистая арифметика без файлов и сети.</summary>
public static class ReturnShareEstimator
{
    /// <summary>Доля, которую покупатель реально заплатил за строки: итог продажи / сумма строк (после скидок на позиции, до скидки на
    /// чек). Не больше 1; нет итога или сумма строк неизвестна — 1 (как раньше).</summary>
    public static decimal Factor(decimal? saleTotal, decimal linesSum) =>
        saleTotal is { } total && total > 0m && linesSum > 0.005m ? Math.Min(1m, total / linesSum) : 1m;

    /// <summary>Потолок суммы возврата без своей записи: не больше итога чека, как его отдаёт сервер сейчас. Сколько уже возвращали
    /// не здесь, касса не знает точно (сервер мог сам убрать скидку из чека после первого возврата) — поэтому только по итогу.</summary>
    public static decimal Cap(decimal requested, decimal? saleTotal) =>
        saleTotal is { } total && total > 0m ? Math.Min(requested, total) : requested;

    /// <summary>2026-10-27 (ревью, См-5): потолок по полю <c>refundable_total</c> продажи, если сервер его уже отдаёт (Б-09 единого ТЗ: «сколько ещё можно
    /// вернуть» с учётом прошлых возвратов с сайта и других касс). Нет поля или оно ≤ 0 (не рассчитано) — сумма как есть: тогда остаётся
    /// оценка по итогу чека и ответ сервера (409 refund_exceeds_paid).</summary>
    public static decimal CapRefundable(decimal requested, decimal? refundableTotal) =>
        refundableTotal is { } left && left > 0m ? Math.Min(requested, left) : requested;

    /// <summary>Оценка уже возвращённого по строкам: сумма строки × (было − осталось) / было × доля. Только для подсказки кассиру.</summary>
    public static decimal PriorReturned(decimal lineTotal, double originalQuantity, double refundableQuantity, decimal factor) =>
        originalQuantity > 0 && refundableQuantity < originalQuantity - 1e-6
            ? Math.Round(lineTotal * (decimal)(originalQuantity - refundableQuantity) / (decimal)originalQuantity * factor, 2, MidpointRounding.AwayFromZero)
            : 0m;
}
