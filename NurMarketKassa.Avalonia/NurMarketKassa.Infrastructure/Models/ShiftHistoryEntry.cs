using System;

namespace NurMarketKassa.Models;

public sealed class ShiftHistoryEntry
{
    public string ShiftNumber { get; init; } = "";
    public DateTime? OpenedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public string Cashier { get; init; } = "—";
    public string Status { get; init; } = "Закрыта";
    public decimal Revenue { get; init; }

    /// <summary>Фактически пересчитанная сумма в денежном ящике при закрытии смены (null у открытой смены).</summary>
    public decimal? ClosingCash { get; init; }

    /// <summary>2026-09-13: стартовая сумма смены (БЕЗ учёта продаж), поля "opening_cash"/
    /// "start_cash" того же ответа GET construction/shifts/, что уже проверен рабочим кодом в
    /// ShiftBalanceHelper.TryReadBalance для ТЕКУЩЕЙ (открытой) смены. Раньше здесь этого поля
    /// не было вовсе, и "Общий план" в Истории смен вычислял начальную сумму из операций с типом
    /// OpeningBalance — которые нигде в коде не создаются, поэтому "Начальная сумма" всегда
    /// показывала 0, а "Ожидаемая сумма" была занижена ровно на стартовую наличность смены.</summary>
    public decimal? OpeningCash { get; init; }

    /// <summary>2026-09-14, по просьбе пользователя ("нужен подробный отчёт при закрытии
    /// смены, как на вебке") — та же разбивка наличные/безналичные, что ShiftBalanceHelper уже
    /// читает для ТЕКУЩЕЙ открытой смены из "cash_sales_total"/"noncash_sales_total" того же
    /// ответа GET construction/shifts/ — применяется и к закрытым сменам в Истории.</summary>
    public decimal? CashSales { get; init; }
    public decimal? NonCashSales { get; init; }

    /// <summary>2026-09-15, по просьбе пользователя ("долг не показывает") — предположительные
    /// имена полей, не подтверждены живым захватом DevTools (см. ShiftBalanceHelper.ReadShiftTotals).</summary>
    public decimal? DebtSales { get; init; }

    /// <summary>Количество чеков за смену — поле "sales_count" того же ответа, подтверждено
    /// рабочим кодом FinanceWindow.ParseShiftRow.</summary>
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

}
