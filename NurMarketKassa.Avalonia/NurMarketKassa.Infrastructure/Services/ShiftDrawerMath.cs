namespace NurMarketKassa.Services;

/// <summary>2026-10-27 (аудит кассы, С-7/С-9): «в ящике должно быть» без ответа сервера — одна формула для окна смены,
/// X/Z-отчёта и печати. Раньше запасная формула не знала внесений, оплат долгов наличными и наличных возвратов
/// (Z без ответа сервера показывал ожидаемую сумму, не сходящуюся с окном закрытия). Формула сервера (expected_cash):
/// начало + наличные продажи (с наличной частью смешанной и предоплатой долга) + оплаты долгов наличными + внесения
/// (shift_drawer_inflow) − изъятия; наличные возвраты в ящике тоже уменьшают сумму. Чистая арифметика, без файлов и сети.</summary>
public static class ShiftDrawerMath
{
    /// <summary>Ожидаемая наличность в ящике по слагаемым. <paramref name="cashReturns"/> — только наличная часть возвратов
    /// (безналичные возвраты ящик не трогают).</summary>
    public static decimal Expected(decimal opening, decimal cashSales, decimal debtPaidCash, decimal deposits, decimal withdrawals, decimal cashReturns) =>
        opening + cashSales + debtPaidCash + deposits - withdrawals - cashReturns;

    /// <summary>С-9: сумма, которой «ещё нет на сервере» — внесения минус изъятия, не дошедшие до сервера, плюс наличные чеков
    /// офлайн-очереди. «Касса: N» в шапке уже включает её, X-отчёт сервера — нет.</summary>
    public static decimal NotOnServer(decimal pendingDeposits, decimal pendingWithdrawals, decimal queuedCash) =>
        pendingDeposits - pendingWithdrawals + queuedCash;
}
