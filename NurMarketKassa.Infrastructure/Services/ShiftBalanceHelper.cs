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

        foreach (var key in new[]
                 {
                     "current_cash", "cash_balance", "balance", "closing_cash", "opening_cash",
                     "cash_in_drawer", "expected_cash", "total_cash",
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
        foreach (var row in EnumerateRows(shiftsPayload))
        {
            if (!RowLooksOpen(row))
                continue;
            if (!string.IsNullOrWhiteSpace(cashboxId) && !RowMatchesCashbox(row, cashboxId))
                continue;
            return TryReadBalance(row);
        }

        return null;
    }

    public static string FormatBalance(decimal? balance) =>
        balance.HasValue
            ? $"{balance.Value.ToString("0.00", CultureInfo.InvariantCulture)} сом"
            : "—";

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
