using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Разбор строки списка продаж POS для отображения в UI.</summary>
public static class PosSaleRowFormatter
{
    public static string? TrySaleId(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "id", "sale_id", "uuid", "pk" })
        {
            if (!row.TryGetProperty(key, out var v))
                continue;
            var s = JsonScalar(v);
            if (!string.IsNullOrEmpty(s))
                return s;
        }

        return null;
    }

    public static string SummaryLine(JsonElement row)
    {
        var when = TryWhenLabel(row);
        var total = TryTotalLabel(row);
        var id = TrySaleId(row);
        var idShort = id;
        if (!string.IsNullOrEmpty(id) && id.Length > 20)
            idShort = id[..12] + "…";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(when))
            parts.Add(when);
        if (!string.IsNullOrEmpty(total))
            parts.Add(total);
        if (!string.IsNullOrEmpty(idShort))
            parts.Add($"№ {idShort}");

        return parts.Count > 0 ? string.Join(" · ", parts) : (id ?? "—");
    }

    private static string? TryWhenLabel(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[] { "created_at", "sold_at", "closed_at", "date", "created" })
        {
            if (!row.TryGetProperty(key, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var raw = v.GetString()?.Trim();
                if (string.IsNullOrEmpty(raw))
                    continue;
                if (raw.Length >= 16 && raw[4] == '-' && raw[7] == '-')
                    return raw[..16].Replace('T', ' ');
                if (raw.Length >= 10)
                    return raw[..10];
                return raw;
            }
        }

        return null;
    }

    private static string? TryTotalLabel(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[]
                 {
                     "total", "grand_total", "amount_due", "total_amount", "sum", "payable_total",
                     "order_total",
                 })
        {
            if (TryDouble(row, key) is { } v)
                return $"{v.ToString("0.00", CultureInfo.InvariantCulture)} сом";
        }

        if (row.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "total", "grand_total", "amount" })
            {
                if (TryDouble(t, key) is { } v)
                    return $"{v.ToString("0.00", CultureInfo.InvariantCulture)} сом";
            }
        }

        return null;
    }

    private static double? TryDouble(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)
                ? x
                : null,
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
