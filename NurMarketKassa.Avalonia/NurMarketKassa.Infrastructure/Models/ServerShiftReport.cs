using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Models;

/// <summary>Отчёт смены с сервера одним запросом — GET api/construction/shifts/{id}/report/
/// (2026-09-28, доработка NurCRM по ТЗ, пункт BE-10 / «2.5. Отчёт смены одним запросом»).
///
/// Раньше касса собирала отчёт смены из трёх мест: итоги смены с сервера (выручка, наличные,
/// безнал, расход), свои локальные счётчики (возвраты, внесения, изъятия, скидки — их видела
/// только та касса, где операцию сделали; у владельца на другом компьютере их не было вовсе)
/// и список чеков смены (скидки). Смешанная оплата уходила целиком в безнал.
///
/// Что проверено на тестовой компании NBS 2026-09-28:
/// <list type="bullet">
/// <item>sales_total = cash + transfer (и прочий безнал) + mixed_cash + mixed_card; долг в выручку
/// не входит (смена 314410e5: 10 787,60 + 440 = 11 227,60, долг 19 900 отдельно).</item>
/// <item>by_payment.debt — остаток долга по продажам смены (у продажи на 20 000 после взноса 100 —
/// 19 900), то есть то же, что касса считала по сделкам (CashShiftService.TryComputeDebtTotalAsync).</item>
/// <item>Ключей by_payment с нулём сервер иногда не присылает (нет «transfer», если безнала не было).</item>
/// <item>expected_cash = начало + наличные (в т.ч. наличная часть смешанной) + внесения
/// (shift_drawer_inflow) + прочие приходы наличными − изъятия: внесение 10 сом подняло его с 905 до 915,
/// изъятие 10 вернуло 905.</item>
/// <item>returns_total/returns_count пока 0 всегда: у возвратов на сервере поле shift пустое
/// (GET api/main/pos/returns/ → shift: null). Касса временно считает возвраты смены сама
/// по списку возвратов (NurCrmReportsApi.ReturnsForShiftAsync).</item>
/// </list></summary>
public sealed class ServerShiftReport
{
    public string ShiftId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? CashierId { get; init; }
    public DateTimeOffset? OpenedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public decimal? OpeningCash { get; init; }

    public int SalesCount { get; init; }
    public decimal SalesTotal { get; init; }

    /// <summary>by_payment как прислал сервер (cash, transfer, mixed_cash, mixed_card, debt, …).</summary>
    public IReadOnlyDictionary<string, decimal> ByPayment { get; init; } = new Dictionary<string, decimal>();

    public decimal Cash => Get("cash");
    public decimal MixedCash => Get("mixed_cash");
    public decimal MixedCard => Get("mixed_card");
    public decimal Debt => Get("debt");

    /// <summary>Безнал без смешанной: transfer, card, банки (mbank, optima, …) — всё, что не
    /// наличные, не смешанная и не долг.</summary>
    public decimal OtherNonCash
    {
        get
        {
            var sum = 0m;
            foreach (var (key, value) in ByPayment)
            {
                if (key is "cash" or "mixed_cash" or "mixed_card" or "debt")
                    continue;
                sum += value;
            }
            return sum;
        }
    }

    /// <summary>Наличные всего — вместе с наличной частью смешанной оплаты.</summary>
    public decimal CashTotal => Cash + MixedCash;

    /// <summary>Безнал всего — вместе с безналичной частью смешанной оплаты.</summary>
    public decimal NonCashTotal => OtherNonCash + MixedCard;

    public decimal Discounts { get; init; }
    public decimal BonusRedeemed { get; init; }
    public decimal ReturnsTotal { get; init; }
    public int ReturnsCount { get; init; }

    /// <summary>Внесения в ящик (cashflows с source_kind «shift_drawer_inflow»).</summary>
    public decimal Deposits { get; init; }

    /// <summary>Изъятия из ящика (source_kind «shift_drawer_outflow»).</summary>
    public decimal Withdrawals { get; init; }

    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal? ExpectedCash { get; init; }

    /// <summary>Пересчитанная кассиром сумма при закрытии; null у открытой смены.</summary>
    public decimal? CountedCash { get; init; }

    public decimal? Difference { get; init; }

    public bool IsOpen => string.Equals(Status, "open", StringComparison.OrdinalIgnoreCase);

    private decimal Get(string key) => ByPayment.TryGetValue(key, out var v) ? v : 0m;

    /// <summary>null — ответ не похож на отчёт смены (старый сервер, ошибка).</summary>
    public static ServerShiftReport? TryParse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("sales_total", out _))
            return null;

        var byPayment = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("by_payment", out var bp) && bp.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in bp.EnumerateObject())
            {
                if (Dec(p.Value) is { } v)
                    byPayment[p.Name.Trim().ToLowerInvariant()] = v;
            }
        }

        return new ServerShiftReport
        {
            ShiftId = Str(e, "shift") ?? "",
            Status = Str(e, "status") ?? "",
            CashierId = Str(e, "cashier"),
            OpenedAt = Date(e, "opened_at"),
            ClosedAt = Date(e, "closed_at"),
            OpeningCash = Num(e, "opening_cash"),
            SalesCount = (int)(Num(e, "sales_count") ?? 0m),
            SalesTotal = Num(e, "sales_total") ?? 0m,
            ByPayment = byPayment,
            Discounts = Num(e, "discounts") ?? 0m,
            BonusRedeemed = Num(e, "bonus_redeemed") ?? 0m,
            ReturnsTotal = Num(e, "returns_total") ?? 0m,
            ReturnsCount = (int)(Num(e, "returns_count") ?? 0m),
            Deposits = Num(e, "deposits") ?? 0m,
            Withdrawals = Num(e, "withdrawals") ?? 0m,
            IncomeTotal = Num(e, "income_total") ?? 0m,
            ExpenseTotal = Num(e, "expense_total") ?? 0m,
            ExpectedCash = Num(e, "expected_cash"),
            CountedCash = Num(e, "counted_cash"),
            Difference = Num(e, "difference"),
        };
    }

    /// <summary>Смена с цифрами этого отчёта: выручка, чеки, наличные/безнал (со смешанной),
    /// долг, приход/расход, ожидаемый и пересчитанный остаток. Номер, кассир, статус и время
    /// остаются от <paramref name="shift"/> — их касса уже знает и показывает по-своему.</summary>
    public ShiftModel ApplyTo(ShiftModel shift) => new()
    {
        Id = shift.Id,
        ShiftNumber = shift.ShiftNumber,
        OpenedAt = shift.OpenedAt ?? OpenedAt?.LocalDateTime,
        ClosedAt = shift.ClosedAt ?? ClosedAt?.LocalDateTime,
        Cashier = shift.Cashier,
        Status = shift.Status,
        Revenue = SalesTotal,
        // Пересчитанную сумму при закрытии знает и сервер; пока смена открыта (или закрыта только
        // на кассе и ждёт сервера) — та, что ввёл кассир.
        ClosingCash = CountedCash ?? shift.ClosingCash,
        OpeningCash = OpeningCash ?? shift.OpeningCash,
        CashSales = CashTotal,
        NonCashSales = NonCashTotal,
        DebtSales = Debt,
        SalesCount = SalesCount,
        ExpenseTotal = ExpenseTotal,
        IncomeTotal = IncomeTotal,
        ExpectedCash = ExpectedCash ?? shift.ExpectedCash,
        CashDiff = Difference ?? shift.CashDiff,
    };

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static decimal? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? Dec(v) : null;

    private static decimal? Dec(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
        JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static DateTimeOffset? Date(JsonElement e, string key) =>
        Str(e, key) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;
}
