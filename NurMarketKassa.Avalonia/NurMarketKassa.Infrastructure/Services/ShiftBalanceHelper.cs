using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Извлечение остатка денежных средств из ответов API смены (Z-отчёт).</summary>
public static class ShiftBalanceHelper
{
    public static decimal? TryReadBalance(JsonElement shiftRow)
    {
        if (shiftRow.ValueKind != JsonValueKind.Object)
            return null;

        // 2026-09-12: подтверждено живым захватом DevTools (GET .../construction/shifts/?status=open)
        // — поле называется именно "expected_cash" ("сколько должно быть в кассе сейчас": открытие +
        // наличные продажи + приходы - расходы). Раньше оно стояло 7-м в списке, а перед ним шёл
        // "opening_cash" (стартовая сумма смены, БЕЗ учёта продаж) — тот почти всегда тоже присутствует
        // в ответе и подхватывался первым, так что "Остаток по системе" мог показывать стартовую сумму
        // смены вместо реального ожидаемого остатка. Этот метод вызывается только на строке ОТКРЫТОЙ
        // смены (см. FindOpenShiftBalance/ReadShiftTotals), где "closing_cash" всегда null — остальные
        // ключи ниже остаются просто как защитные варианты на случай другой формы ответа API.
        foreach (var key in new[]
                 {
                     "expected_cash", "current_cash", "cash_balance", "balance", "closing_cash",
                     "opening_cash", "cash_in_drawer", "total_cash",
                 })
        {
            if (TryReadDecimal(shiftRow, key) is { } v)
                return v;
        }

        if (shiftRow.TryGetProperty("totals", out var totals) && totals.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "cash", "balance", "current_cash" })
            {
                if (TryReadDecimal(totals, key) is { } v)
                    return v;
            }
        }

        return null;
    }

    public static decimal? FindOpenShiftBalance(JsonElement shiftsPayload, string? cashboxId)
    {
        var row = FindOpenShiftRow(shiftsPayload, cashboxId);
        return row is { } r ? TryReadBalance(r) : null;
    }

    public static ShiftTotals? FindOpenShiftTotals(JsonElement shiftsPayload, string? cashboxId)
    {
        var row = FindOpenShiftRow(shiftsPayload, cashboxId);
        return row is { } r ? ReadShiftTotals(r) : null;
    }

    /// <summary>2026-09-15, живой баг: "не работает подсчёт! отчёт 0, но по факту в этой смене
    /// уже была продажа" — PosApp.PosCashboxId (тот же холдер, что уже не раз подводил в этой
    /// сессии — см. её комментарии у RejectedCashboxIds) мог разойтись с cashbox_id, который
    /// сервер реально записал за открытой сменой; строгое совпадение по кассе тогда не находило
    /// НИ ОДНОЙ строки, и диалог закрытия смены показывал 0.00 вместо настоящего остатка —
    /// хотя сама продажа прошла нормально (кассу для оплаты и кассу для отображения баланса
    /// это разные, не всегда согласованные поля). Раз одновременно открытая смена может быть
    /// только одна, при отсутствии совпадения по кассе безопасно берём единственную открытую
    /// строку целиком — показать реальные цифры лучше, чем молча показать ноль.</summary>
    private static JsonElement? FindOpenShiftRow(JsonElement shiftsPayload, string? cashboxId)
    {
        JsonElement? firstOpenAnyCashbox = null;
        var openCount = 0;
        foreach (var row in EnumerateRows(shiftsPayload))
        {
            if (!RowLooksOpen(row))
                continue;
            openCount++;
            firstOpenAnyCashbox ??= row;
            if (string.IsNullOrWhiteSpace(cashboxId) || RowMatchesCashbox(row, cashboxId))
                return row;
        }

        // Кассой не совпало ни разу — используем найденную открытую смену, только если она
        // ровно одна: при нескольких открытых сменах разных касс подстановка "первой попавшейся"
        // рискует показать чужой остаток, что хуже честного "—".
        return openCount == 1 ? firstOpenAnyCashbox : null;
    }

    public static string FormatBalance(decimal? balance) =>
        balance.HasValue
            ? $"{balance.Value.ToString("0.00", CultureInfo.InvariantCulture)} сом"
            : "—";

    /// <summary>2026-09-12, по просьбе пользователя ("сделай так же как в вебе") — детальная
    /// разбивка для диалога закрытия смены (начальная сумма/продажи/наличные/безналичные/
    /// ожидаемая сумма), как показывает веб. Имена полей подтверждены живым захватом DevTools
    /// на реальном ответе GET .../construction/shifts/?status=open (opening_cash: "10000.00",
    /// sales_total: "220.00", cash_sales_total: "220.00", noncash_sales_total: "0.00",
    /// expected_cash: "10220.00" — ровно те же цифры, что показывает веб-диалог "Завершение
    /// смены"). Один-два запасных варианта имени оставлены на случай будущих изменений формата
    /// ответа API. Если поле всё же не находится — значение остаётся null (в диалоге покажется
    /// "—", а не вводящий в заблуждение 0.00).</summary>
    public static ShiftTotals ReadShiftTotals(JsonElement shiftRow)
    {
        if (shiftRow.ValueKind != JsonValueKind.Object)
            return new ShiftTotals();

        var debtSales = FirstOf(shiftRow, "debt_sales_total", "debt_total", "debt");

        // 2026-09-15, диагностика живого бага ("Долг 480 сом за смену, где по факту только один
        // долг на 160, из которых 40 оплатили сразу"): имя поля для DebtSales — до сих пор ГАДАНИЕ
        // по аналогии с cash_sales_total/noncash_sales_total (см. комментарий ниже), НИКОГДА не
        // подтверждённое живым захватом DevTools, в отличие от остальных полей этого метода. Раз
        // подозрение теперь в том, что найденное поле — это не долг именно ЭТОЙ смены, а какая-то
        // накопленная/общая цифра (три тестовые продажи «в долг» по 160 сом в ТРЁХ РАЗНЫХ сменах
        // одной и той же кассы дали ровно 480 = 3×160 в отчёте каждый раз) — логируем весь сырой
        // JSON строки смены один раз, чтобы поймать реальный набор полей и решить, что там на
        // самом деле лежит, вместо дальнейших догадок.
        if (debtSales is { } dv && dv != 0m)
            PosLogger.Log($"[DEBUG] Shift row with non-zero debt total ({dv:0.00}): {shiftRow.GetRawText()}", "SHIFT");

        return new ShiftTotals
        {
            OpeningCash = FirstOf(shiftRow, "opening_cash", "start_cash"),
            TotalSales = FirstOf(shiftRow, "sales_total", "total_sales"),
            CashSales = FirstOf(shiftRow, "cash_sales_total", "cash_sales"),
            NonCashSales = FirstOf(shiftRow, "noncash_sales_total", "non_cash_sales_total", "non_cash_sales"),
            // 2026-09-15, по просьбе пользователя ("долг не показывает") — предположительные
            // имена полей по аналогии с cash_sales_total/noncash_sales_total; сервер их не
            // подтверждён живым захватом DevTools, поэтому если не найдётся ни одно — останется
            // null (диалог покажет "—" вместо вводящего в заблуждение 0.00).
            DebtSales = debtSales,
            ExpectedCash = TryReadBalance(shiftRow),
            ExpenseTotal = FirstOf(shiftRow, "expense_total"),
            IncomeTotal = FirstOf(shiftRow, "income_total"),
            CashDiff = FirstOf(shiftRow, "cash_diff"),
            // 2026-09-14, по просьбе пользователя ("нужен подробный отчёт при закрытии смены,
            // как на вебке") — "sales_count" того же ответа, подтверждено рабочим кодом
            // FinanceWindow.ParseShiftRow.
            SalesCount = TryReadInt(shiftRow, "sales_count"),
        };
    }

    private static int? TryReadInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(v.GetString(), out var i2) ? i2 : null,
            _ => null,
        };
    }

    private static decimal? FirstOf(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryReadDecimal(obj, key) is { } v)
                return v;
        }

        return null;
    }

    public sealed class ShiftTotals
    {
        public decimal? OpeningCash { get; init; }
        public decimal? TotalSales { get; init; }
        public decimal? CashSales { get; init; }
        public decimal? NonCashSales { get; init; }
        public decimal? DebtSales { get; init; }
        public decimal? ExpectedCash { get; init; }
        public int? SalesCount { get; init; }

        /// <summary>Приходы и расходы по кассе с сервера (income_total / expense_total того
        /// же ответа). Нужны отчёту при закрытии смены: локальный cash_history.json знает
        /// только операции, сделанные на этом компьютере, и расход в печатном чеке выходил
        /// меньше настоящего.</summary>
        public decimal? ExpenseTotal { get; init; }
        public decimal? IncomeTotal { get; init; }
        public decimal? CashDiff { get; init; }
    }

    private static IEnumerable<JsonElement> EnumerateRows(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in data.EnumerateArray())
                yield return el;
            yield break;
        }

        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("results", out var r) &&
            r.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in r.EnumerateArray())
                yield return el;
        }
    }

    private static bool RowLooksOpen(JsonElement row)
    {
        if (row.TryGetProperty("is_open", out var open) && open.ValueKind == JsonValueKind.True)
            return true;

        if (row.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
        {
            var s = st.GetString()?.Trim().ToLowerInvariant() ?? "";
            return s is "open" or "active" or "opened" or "in_progress";
        }

        return false;
    }

    private static bool RowMatchesCashbox(JsonElement row, string cashboxId)
    {
        if (row.TryGetProperty("cashbox_id", out var cbId))
        {
            var s = JsonScalar(cbId);
            if (!string.IsNullOrEmpty(s) && string.Equals(s, cashboxId, StringComparison.Ordinal))
                return true;
        }

        if (row.TryGetProperty("cashbox", out var cb))
        {
            if (cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("id", out var id))
            {
                var s = JsonScalar(id);
                if (!string.IsNullOrEmpty(s) && string.Equals(s, cashboxId, StringComparison.Ordinal))
                    return true;
            }
            else
            {
                var s = JsonScalar(cb);
                if (!string.IsNullOrEmpty(s) && string.Equals(s, cashboxId, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static decimal? TryReadDecimal(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v))
            return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : null,
            _ => null,
        };
    }

    private static string? JsonScalar(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
}
