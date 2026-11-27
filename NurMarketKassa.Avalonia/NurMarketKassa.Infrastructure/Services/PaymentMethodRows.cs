namespace NurMarketKassa.Services;

/// <summary>2026-11-27 (живой проход): разбор строк charts.payment_methods сводной аналитики сервера.</summary>
public static class PaymentMethodRows
{
    /// <summary>Сервер отдаёт способ «debt» двумя строками: долговые чеки, оплаченные потом (входят в выручку «без долга»),
    /// и «продано в долг целиком» (= cards.credit_sales_total, в выручку не входит). Пока строк «debt» больше одной,
    /// убираем ту, что равна credit_sales_total (одну) — иначе «Способы оплаты» показывают «В долг» дважды и итог больше выручки.
    /// Одна строка «debt» не трогается.</summary>
    public static List<(string Method, double Count, double Total)> DropCreditSalesDuplicate(
        List<(string Method, double Count, double Total)> methods, double creditSalesTotal)
    {
        static bool IsDebt(string? m) => string.Equals(m?.Trim(), "debt", StringComparison.OrdinalIgnoreCase);
        if (creditSalesTotal <= 0 || methods.Count(m => IsDebt(m.Method)) < 2)
            return methods;
        // последняя подходящая: сервер ставит строку «в долг целиком» после остальных способов
        var idx = methods.FindLastIndex(m => IsDebt(m.Method) && Math.Abs(m.Total - creditSalesTotal) < 0.005);
        if (idx < 0)
            return methods;
        var copy = new List<(string Method, double Count, double Total)>(methods);
        copy.RemoveAt(idx);
        return copy;
    }

    /// <summary>Безнал «Продаж и финансов» = выручка − наличные − смешанная − оплаченные потом долговые чеки.
    /// Оплаченная часть долга = все строки «debt» − credit_sales_total (то, что ещё «в долг целиком», в выручку не входит).
    /// Раньше безнал = выручка − наличные − смешанная: долговой чек, погашенный наличными, попадал в безнал (85 → 150).</summary>
    public static decimal NonCash(decimal revenue, decimal cash, decimal mixed, decimal debtRowsTotal, decimal? creditSalesTotal)
    {
        // старый сервер без credit_sales_total — как раньше (не знаем, какая часть долга оплачена)
        if (creditSalesTotal is not { } credit)
            return revenue - cash - mixed;
        var paidDebt = Math.Max(0m, debtRowsTotal - Math.Max(0m, credit));
        return revenue - cash - mixed - paidDebt;
    }
}
