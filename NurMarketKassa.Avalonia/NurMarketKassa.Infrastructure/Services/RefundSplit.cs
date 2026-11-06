namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В2 Л4-16 / АР-1 — ЕДИНАЯ формула доли «наличные / безнал» возврата. Раньше была написана в двух местах (окно возврата
/// ReturnSaleDialog.ComputeRefundSplit и офлайн-план OfflineRefundPlanner) — правка одной незаметно разводила возврат со связью и без неё.
/// Правило: возвращается пропорционально тому, что по чеку ещё не возвращено наличными и безналом; наличная доля округляется до копеек «от нуля»
/// и не больше самой суммы, безнал — остаток (сумма долей всегда равна сумме возврата). Нечего делить (оба остатка 0) — всё наличными
/// (вызывающий сам решает, помечать ли это «способ неизвестен»). Чистая арифметика: без файлов, окон и сети.</summary>
public static class RefundSplit
{
    /// <summary>Разделить <paramref name="amount"/> по остаткам <paramref name="cashLeft"/> / <paramref name="nonCashLeft"/>. Отрицательные остатки считаются нулём.</summary>
    public static (decimal Cash, decimal NonCash) Split(decimal amount, decimal cashLeft, decimal nonCashLeft)
    {
        var cashPart = Math.Max(0m, cashLeft);
        var nonCashPart = Math.Max(0m, nonCashLeft);
        var total = cashPart + nonCashPart;
        if (total <= 0m)
            return (amount, 0m);
        var cash = Math.Min(amount, Math.Round(amount * cashPart / total, 2, MidpointRounding.AwayFromZero));
        return (cash, amount - cash);
    }
}
