namespace NurMarketKassa.Models;

public sealed class ShiftModel
{
    public string Id { get; init; } = "";

    public string ShiftNumber { get; init; } = "";

    public DateTime? OpenedAt { get; init; }

    public DateTime? ClosedAt { get; init; }

    public string Cashier { get; init; } = "—";

    public string Status { get; init; } = "Закрыта";

    public decimal Revenue { get; init; }

    /// <summary>Фактически пересчитанная сумма в денежном ящике при закрытии смены (null у открытой смены).</summary>
    public decimal? ClosingCash { get; init; }

    /// <summary>Стартовая сумма смены (см. ShiftHistoryEntry.OpeningCash).</summary>
    public decimal? OpeningCash { get; init; }

    /// <summary>2026-09-14, см. ShiftHistoryEntry — разбивка для подробного отчёта закрытия смены.</summary>
    public decimal? CashSales { get; init; }
    public decimal? NonCashSales { get; init; }

    /// <summary>2026-09-15: settable (не init), т.к. при просмотре смены из «Истории смен»
    /// исходное значение (из непроверенного поля списка смен сервера) уточняется асинхронно
    /// реальным remaining_debt по сделкам — см. ICashShiftService.ResolveShiftDebtTotalAsync,
    /// тот же приём, что уже работает для только что закрытой смены.</summary>
    public decimal? DebtSales { get; set; }
    public int? SalesCount { get; init; }

    /// <summary>Приходы и расходы по кассе — с сервера, поля «income_total» и «expense_total»
    /// того же ответа api/construction/shifts/ (имена подтверждены живым запросом 2026-09-23).
    ///
    /// Раньше Z-отчёт считал изъятия по локальному cash_history.json, где лежат только те
    /// операции, что сделали на ЭТОМ компьютере. Изъятие, проведённое на вебе или на другой
    /// кассе, в отчёт не попадало: у владельца расход был 1 940, а в чеке печаталось 490, и
    /// расхождение сходилось там, где его быть не должно.</summary>
    public decimal? ExpenseTotal { get; init; }
    public decimal? IncomeTotal { get; init; }

    /// <summary>Ожидаемая сумма в ящике и расхождение — тоже с сервера («expected_cash»,
    /// «cash_diff»). Считать их самим незачем: сервер знает обо всех операциях, а касса —
    /// только о своих.</summary>
    public decimal? ExpectedCash { get; init; }
    public decimal? CashDiff { get; init; }


    public bool IsActive => string.Equals(Status, "Активна", StringComparison.OrdinalIgnoreCase);

    public static ShiftModel FromEntry(ShiftHistoryEntry entry) => new()
    {
        Id = entry.ShiftNumber,
        ShiftNumber = entry.ShiftNumber,
        OpenedAt = entry.OpenedAt,
        ClosedAt = entry.ClosedAt,
        Cashier = entry.Cashier,
        Status = entry.Status,
        Revenue = entry.Revenue,
        ClosingCash = entry.ClosingCash,
        OpeningCash = entry.OpeningCash,
        CashSales = entry.CashSales,
        NonCashSales = entry.NonCashSales,
        DebtSales = entry.DebtSales,
        SalesCount = entry.SalesCount,
        ExpenseTotal = entry.ExpenseTotal,
        IncomeTotal = entry.IncomeTotal,
        ExpectedCash = entry.ExpectedCash,
        CashDiff = entry.CashDiff,
    };
}
