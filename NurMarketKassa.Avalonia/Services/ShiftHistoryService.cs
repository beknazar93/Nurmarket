using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>Avalonia-host copy of shift history loading (uses AvaloniaHost.App statics).</summary>
public static class ShiftHistoryService
{
    public static async Task<IReadOnlyList<ShiftHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await NurMarketKassa.AvaloniaHost.App.ShiftApi
                .ConstructionShiftsListAsync(cancellationToken)
                .ConfigureAwait(false);
            return Parse(payload);
        }
        catch
        {
            return Array.Empty<ShiftHistoryEntry>();
        }
    }

    public static IReadOnlyList<ShiftHistoryEntry> Parse(JsonElement payload)
    {
        var list = new List<ShiftHistoryEntry>();
        foreach (var row in EnumerateRows(payload))
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var id = TryString(row, "id", "shift_id", "uuid") ?? "";
            if (string.IsNullOrEmpty(id)) continue;
            var isOpen = TryBool(row, "is_open", "open") || string.Equals(TryString(row, "status"), "open", StringComparison.OrdinalIgnoreCase);
            list.Add(new ShiftHistoryEntry
            {
                ShiftNumber = id,
                OpenedAt = TryDate(row, "opened_at", "open_time", "started_at", "created_at"),
                ClosedAt = TryDate(row, "closed_at", "close_time", "ended_at", "finished_at"),
                Cashier = TryString(row, "cashier", "cashier_name", "user_name", "user_id") ?? "—",
                Status = isOpen ? "Активна" : "Закрыта",
                Revenue = TryDecimal(row, "revenue", "total", "sum"),
            });
        }

        return list.OrderByDescending(s => s.OpenedAt ?? DateTime.MinValue).ToList();
    }

    private static IEnumerable<JsonElement> EnumerateRows(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in payload.EnumerateArray()) yield return el;
            yield break;
        }

        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "data", "items", "results", "shifts" })
            {
                if (payload.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray()) yield return el;
                    yield break;
                }
            }
        }
    }

    private static string? TryString(JsonElement row, params string[] names)
    {
        foreach (var n in names)
        {
            if (row.TryGetProperty(n, out var p))
            {
                if (p.ValueKind == JsonValueKind.String) return p.GetString();
                if (p.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return p.ToString();
            }
        }
        return null;
    }

    private static bool TryBool(JsonElement row, params string[] names)
    {
        foreach (var n in names)
        {
            if (row.TryGetProperty(n, out var p))
            {
                if (p.ValueKind == JsonValueKind.True) return true;
                if (p.ValueKind == JsonValueKind.False) return false;
                if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
            }
        }
        return false;
    }

    private static DateTime? TryDate(JsonElement row, params string[] names)
    {
        foreach (var n in names)
        {
            if (!row.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.String && DateTime.TryParse(p.GetString(), out var dt)) return dt;
        }
        return null;
    }

    private static decimal TryDecimal(JsonElement row, params string[] names)
    {
        foreach (var n in names)
        {
            if (!row.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out var d2)) return d2;
        }
        return 0;
    }
}
