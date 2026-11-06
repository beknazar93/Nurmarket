using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-06 (Б2-2, живой тест Этапа 2): какие чеки клиента показывает «Оплата долга». Раньше — только <c>status=debt</c>; долговой чек после частичного возврата
/// получает на сервере статус <c>partially_returned</c> и пропадал из «Непогашенных», хотя остаток долга по нему есть (сервер при частичном возврате долг сам не уменьшает).
/// Теперь к списку «debt» добавляются чеки других статусов (<see cref="ExtraStatuses"/>) с ПОЛОЖИТЕЛЬНЫМ остатком долга по полям сервера.
/// Окончательный остаток по каждому чеку, как и раньше, уточняется по сделке (remaining_debt): чек с нулевым остатком уходит в «Историю», а не в «Непогашенные».</summary>
public static class DebtSaleListRules
{
    /// <summary>Статусы, кроме <c>debt</c>, в которых у чека может остаться долг. Отменённые и полностью возвращённые сюда не входят (по ним долга нет).</summary>
    public static readonly string[] ExtraStatuses = ["partially_returned"];

    private const double Epsilon = 0.005;

    /// <summary>Остаток долга по строке продажи: первое числовое из <c>debt_remaining</c>, <c>remaining_debt</c>; если их нет — <c>debt_amount</c>; ни одного поля — null.</summary>
    public static double? SaleDebtRemaining(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in new[] { "debt_remaining", "remaining_debt", "debt_amount" })
            if (TryNumber(sale, name) is { } value)
                return value;
        return null;
    }

    /// <summary>У чека есть положительный остаток долга (поле сервера &gt; 0).</summary>
    public static bool HasOpenDebt(JsonElement sale) => SaleDebtRemaining(sale) is { } left && left > Epsilon;

    /// <summary>Список «debt» плюс чеки из <paramref name="others"/> (другие статусы) с положительным остатком долга, которых ещё нет в списке (по id). Порядок: сначала «debt».</summary>
    public static List<JsonElement> Merge(IReadOnlyList<JsonElement> debt, IReadOnlyList<JsonElement> others)
    {
        var result = new List<JsonElement>(debt);
        var seen = new HashSet<string>(debt.Select(IdOf).Where(id => id.Length > 0), StringComparer.OrdinalIgnoreCase);
        foreach (var sale in others)
        {
            if (!HasOpenDebt(sale))
                continue;
            var id = IdOf(sale);
            if (id.Length > 0 && !seen.Add(id))
                continue;
            result.Add(sale);
        }
        return result;
    }

    /// <summary>Сводка сервера (clients/debtors) спорит со списком чеков: не совпадает НИ с суммой чеков, НИ с суммой чеков вместе с «долгом без продажи» (какую из двух сводка включает — зависит от сервера).</summary>
    public static bool SummaryMismatch(double serverTotal, double saleRowsTotal, double allRowsTotal) =>
        Math.Abs(serverTotal - saleRowsTotal) > 0.01 && Math.Abs(serverTotal - allRowsTotal) > 0.01;

    private static string IdOf(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty("id", out var id))
            return "";
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString()?.Trim() ?? "",
            JsonValueKind.Number => id.GetRawText(),
            _ => "",
        };
    }

    private static double? TryNumber(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
