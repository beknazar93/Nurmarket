namespace NurMarketKassa.Services;

/// <summary>2026-11-26 (владелец: «после возврата товара поставщику долг от товара у поставщика не минусуется»): сервер при компенсации <c>debt_offset</c> сам уменьшает долг
/// (признак supplier_return_debt_offset_v2, ответ Умара 05.11: «зачёт уменьшает долг, переплата не создаётся»; уточнение Умара 26.11: сумма возврата гасит долг поставщика от старых приходов к новым), а при <c>cash</c> долг НЕ меняется — деньги идут в кассу.
/// Окно «Новый возврат поставщику» по умолчанию ставило «cash», и долг «не минусовался». Здесь — правила выбора по умолчанию и подсказки по долгу (расчёт не во view):
/// есть долг — по умолчанию зачёт в долг; нет долга или возврат больше долга — понятная подсказка, а не молчание.</summary>
public static class SupplierReturnOffset
{
    public const string Cash = "cash";
    public const string DebtOffset = "debt_offset";
    public const string None = "none";

    /// <summary>Что показать под выбором «что с деньгами».</summary>
    public enum Advice
    {
        /// <summary>Подсказки нет (долг неизвестен или «только списать со склада»).</summary>
        Nothing,
        /// <summary>Выбраны деньги в кассу, а у поставщика есть долг — долг не изменится (напоминание).</summary>
        CashWhileDebt,
        /// <summary>Выбран зачёт, а долга нет — зачесть не в чем.</summary>
        NoDebt,
        /// <summary>Выбран зачёт, возврат больше долга — зачтётся не больше долга.</summary>
        ExceedsDebt,
        /// <summary>Выбран зачёт, долг покрывает возврат — «долг станет …».</summary>
        Fits,
    }

    /// <summary>Вид компенсации по умолчанию: у поставщика есть долг — зачёт в долг; долга нет или он неизвестен — деньги в кассу (как раньше).
    /// 2026-11-26 (ответ Умара: debt_offset гасит долг поставщика от старых приходов к новым, а не только долг выбранного прихода).</summary>
    public static string DefaultCompensation(double? debt) => debt is > 0.005 ? DebtOffset : Cash;

    public static Advice Judge(double? debt, double returnTotal, string compensation)
    {
        if (debt is not { } d)
            return Advice.Nothing;
        if (string.Equals(compensation, Cash, StringComparison.OrdinalIgnoreCase))
            return d > 0.005 ? Advice.CashWhileDebt : Advice.Nothing; // напоминание нужно сразу, пока строки возврата ещё не введены — вид денег выбирают раньше количества
        if (!string.Equals(compensation, DebtOffset, StringComparison.OrdinalIgnoreCase))
            return Advice.Nothing;
        if (d <= 0.005)
            return Advice.NoDebt;
        return returnTotal > d + 0.005 ? Advice.ExceedsDebt : Advice.Fits;
    }

    /// <summary>Нужен ли вопрос «оформить так?» перед отправкой: зачёт, которого не из чего делать полностью.</summary>
    public static bool NeedsConfirm(Advice advice) => advice is Advice.NoDebt or Advice.ExceedsDebt;

    /// <summary>Ожидаемый долг после возврата: при зачёте — меньше на сумму возврата, но не ниже нуля; при остальных видах — прежний. null — долг неизвестен.</summary>
    public static double? ExpectedAfter(double? before, double returnTotal, string compensation)
    {
        if (before is not { } b)
            return null;
        if (!string.Equals(compensation, DebtOffset, StringComparison.OrdinalIgnoreCase))
            return b;
        return (double)Math.Max(0m, Math.Round((decimal)b - (decimal)returnTotal, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>Итог для окна «было → стало» после возврата.</summary>
    public enum Outcome
    {
        /// <summary>Долг до или после неизвестен — итог не показываем.</summary>
        Unknown,
        /// <summary>Зачёт — долг уменьшился.</summary>
        Reduced,
        /// <summary>Зачёт, а долг не изменился, хотя был — сервер не зачёл (предупреждение).</summary>
        NotReduced,
        /// <summary>Возврат деньгами или без денег — долг не должен меняться.</summary>
        Unchanged,
    }

    public static Outcome Classify(double? before, double? after, string compensation)
    {
        if (before is not { } b || after is not { } a)
            return Outcome.Unknown;
        if (!string.Equals(compensation, DebtOffset, StringComparison.OrdinalIgnoreCase))
            return Outcome.Unchanged;
        if (b <= 0.005)
            return Outcome.Unchanged; // зачесть было не в чем — долг и не мог уменьшиться
        return a < b - 0.005 ? Outcome.Reduced : Outcome.NotReduced;
    }
}
