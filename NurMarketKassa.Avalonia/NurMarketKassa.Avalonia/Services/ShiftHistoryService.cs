using System.Globalization;
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
                .ConstructionShiftsListAsync(ct: cancellationToken)
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
                // "cashier_display" — реальное имя ("Иван Иванов"), подтверждено рабочим кодом
                // в FinanceWindow.ParseShiftRow (тот же самый эндпоинт api/construction/shifts/).
                // Раньше здесь его не было вообще — на реальных данных "cashier"/"cashier_name"/
                // "user_name" в ответе сервера пусты, и в итоге показывался "user_id" (голый
                // GUID) — воспроизведено пользователем в StaffTimesheetWindow (2026-09-05), но
                // тот же баг был и в уже существующем окне "История смен" (тот же Parse).
                Cashier = TryString(row, "cashier_display", "cashier", "cashier_name", "user_name", "user_id") ?? "—",
                Status = isOpen ? "Активна" : "Закрыта",
                // "sales_total" — та же история, что и с cashier_display: реальное поле выручки
                // в ответе сервера, подтверждено FinanceWindow.ParseShiftRow (2026-09-05,
                // воспроизведено пользователем — Табель показывал "0 сом" всем кассирам, хотя в
                // "Истории смен" по тем же сменам видна настоящая ненулевая выручка).
                Revenue = TryDecimal(row, "sales_total", "revenue", "total", "sum"),
                // Фактически пересчитанная кассиром сумма в ящике при закрытии смены.
                ClosingCash = TryNullableDecimal(row, "closing_cash", "closing_balance", "close_cash",
                    "cash_closing", "closing_amount", "actual_cash", "cash_at_close", "fact_cash"),
                // "opening_cash"/"start_cash" — те же имена полей, что ShiftBalanceHelper уже
                // проверил на реальном ответе этого же эндпоинта для текущей открытой смены.
                OpeningCash = TryNullableDecimal(row, "opening_cash", "start_cash"),
                // 2026-09-14: те же поля, что ShiftBalanceHelper.ReadShiftTotals уже проверил
                // для ТЕКУЩЕЙ открытой смены ("cash_sales_total"/"noncash_sales_total") и
                // FinanceWindow.ParseShiftRow — для "sales_count" (число чеков).
                CashSales = TryNullableDecimal(row, "cash_sales_total", "cash_sales"),
                NonCashSales = TryNullableDecimal(row, "noncash_sales_total", "non_cash_sales_total", "non_cash_sales"),
                DebtSales = TryNullableDecimal(row, "debt_sales_total", "debt_total", "debt"),
                SalesCount = TryNullableInt(row, "sales_count"),
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
            // Числа в JSON API всегда приходят с точкой — разбираем в инвариантной культуре.
            if (p.ValueKind == JsonValueKind.String &&
                decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d2)) return d2;
        }
        return 0;
    }

    private static int? TryNullableInt(JsonElement row, params string[] names)
    {
        foreach (var n in names)
        {
            if (!row.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)) return i;
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var i2)) return i2;
        }
        return null;
    }

    private static decimal? TryNullableDecimal(JsonElement row, params string[] names)
    {
        foreach (var n in names)
        {
            if (!row.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d)) return d;
            if (p.ValueKind == JsonValueKind.String &&
                decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d2)) return d2;
        }
        return null;
    }
}
