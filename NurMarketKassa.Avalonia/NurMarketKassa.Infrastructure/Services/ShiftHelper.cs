using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Смена (construction/shifts) — как _pick_open_shift_id_from_list и статусы в main.py.</summary>
public static class ShiftHelper
{
    /// <param name="cashierId">Текущий кассир. Если задан, своей считается только его смена —
    /// см. комментарий ниже (2026-09-26).</param>
    public static string? PickOpenShiftId(JsonElement shiftsPayload, string? cashboxId, string? cashierId = null)
    {
        var candidates = new List<(JsonElement Row, string Id)>();
        foreach (var row in EnumerateList(shiftsPayload))
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            if (!RowLooksLikeOpenShift(row))
                continue;
            var rid = CartDisplayHelper.TryCartId(row);
            if (string.IsNullOrEmpty(rid))
                continue;
            candidates.Add((row, rid));
        }

        if (candidates.Count == 0)
            return null;

        // 2026-09-26, живой баг из магазина: «при открытой смене просит открыть смену» — оплата
        // падает с «Смена не открыта. Сначала откройте смену на кассе, затем начните продажу».
        // Сервер продаёт только в СВОЕЙ смене кассира на этой кассе, а рядом с чужой сменой
        // открыть свою разрешает (оба правила проверены на тестовой компании). Касса же брала
        // любую открытую смену кассы — например, не закрытую вчера другим продавцом или открытую
        // владельцем с сайта — и показывала «смена открыта»; после сброса «Открыть смену» снова
        // подхватывало ту же чужую смену, и круг повторялся. Своей теперь считается только
        // смена этого кассира; при чужой касса предлагает открыть свою.
        var own = string.IsNullOrWhiteSpace(cashierId)
            ? candidates
            : candidates.Where(c => !IsOtherCashiersShift(c.Row, cashierId)).ToList();

        // Касса ещё не выбрана (первый вход) — сверять не с чем.
        if (string.IsNullOrWhiteSpace(cashboxId))
            return own.Count > 0 ? own[0].Id : null;

        foreach (var (row, rid) in own)
        {
            if (RowMatchesCashbox(row, cashboxId))
                return rid;
        }

        if (own.Count != candidates.Count && candidates.Any(c => RowMatchesCashbox(c.Row, cashboxId)))
            PosLogger.Log("На этой кассе открыта смена другого кассира; своей смены нет — касса предложит открыть свою.", "SHIFT");

        // 2026-09-24, живой баг: своей смены нет — и раньше здесь возвращалась первая открытая
        // смена компании, чья угодно. Касса «Основная» тихо садилась на смену «Касса 2»
        // (открытую на сайте или другой кассой): продажи уходили в чужую кассу и не появлялись
        // ни в «Продажах», ни в итогах этой кассы, а когда чужая смена закрывалась или сервер
        // отказывал, оплата падала и товар оставался в чеке. Смену того же кассира на другой
        // кассе подхватывает ShiftStateService — вместе с самой кассой, см.
        // FindCashierShiftOnOtherCashbox.
        return null;
    }

    /// <summary>Открытая смена этого же кассира, но на другой кассе. Касса могла сменить
    /// свою кассу между запусками (автовыбор «Основной» после переустановки, см.
    /// MainWindow.RefreshShiftStateAsync), и тогда своя смена лежит под прежней кассой. Такую
    /// смену подхватываем целиком, вместе с её кассой, а смену другого кассира — никогда:
    /// это чужое рабочее место.</summary>
    public static (string ShiftId, string CashboxId, string? CashboxName)? FindCashierShiftOnOtherCashbox(
        JsonElement shiftsPayload, string? cashierId)
    {
        if (string.IsNullOrWhiteSpace(cashierId))
            return null;

        foreach (var row in EnumerateList(shiftsPayload))
        {
            if (row.ValueKind != JsonValueKind.Object || !RowLooksLikeOpenShift(row))
                continue;
            if (!string.Equals(ReadCashierId(row), cashierId.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            var shiftId = CartDisplayHelper.TryCartId(row);
            var cashboxId = ReadCashboxId(row);
            if (string.IsNullOrEmpty(shiftId) || string.IsNullOrEmpty(cashboxId))
                continue;

            var name = row.TryGetProperty("cashbox_name", out var n) ? JsonScalar(n) : null;
            return (shiftId, cashboxId, name);
        }

        return null;
    }

    /// <summary>Смена открыта другим кассиром. Если сервер не указал кассира — не чужая
    /// (прежнее поведение).</summary>
    internal static bool IsOtherCashiersShift(JsonElement row, string? cashierId)
    {
        if (string.IsNullOrWhiteSpace(cashierId))
            return false;
        var rowCashier = ReadCashierId(row);
        return !string.IsNullOrWhiteSpace(rowCashier)
               && !string.Equals(rowCashier, cashierId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadCashierId(JsonElement row)
    {
        foreach (var key in new[] { "cashier", "cashier_id", "user", "opened_by" })
        {
            if (!row.TryGetProperty(key, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("id", out var id))
                return JsonScalar(id);
            if (JsonScalar(v) is { } s)
                return s;
        }

        return null;
    }

    internal static string? ReadCashboxId(JsonElement row)
    {
        if (row.TryGetProperty("cashbox", out var cb))
        {
            if (cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("id", out var cid))
                return JsonScalar(cid);
            if (JsonScalar(cb) is { } s)
                return s;
        }

        return row.TryGetProperty("cashbox_id", out var cbi) ? JsonScalar(cbi) : null;
    }

    private static bool RowMatchesCashbox(JsonElement row, string cashboxId)
    {
        if (row.TryGetProperty("cashbox", out var cb))
        {
            if (cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("id", out var cid))
            {
                var s = JsonScalar(cid);
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

        if (row.TryGetProperty("cashbox_id", out var cbi))
        {
            var s = JsonScalar(cbi);
            if (!string.IsNullOrEmpty(s) && string.Equals(s, cashboxId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool RowLooksLikeOpenShift(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return false;

        if (TruthyBool(row, "is_open"))
            return true;

        if (row.TryGetProperty("status", out var st))
        {
            if (IsOpenStatusString(st))
                return true;
        }

        if (row.TryGetProperty("state", out var state))
            return IsOpenStatusString(state);

        return false;
    }

    private static bool IsOpenStatusString(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String)
            return false;
        var s = v.GetString()?.Trim().ToLowerInvariant() ?? "";
        return s is "open" or "active" or "opened" or "in_progress";
    }

    private static bool TruthyBool(JsonElement obj, string prop)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var v))
            return false;
        if (v.ValueKind == JsonValueKind.True)
            return true;
        if (v.ValueKind == JsonValueKind.False)
            return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString()?.Trim().ToLowerInvariant();
            return s is "1" or "true" or "yes" or "on";
        }

        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && Math.Abs(d) > double.Epsilon;
    }

    private static IEnumerable<JsonElement> EnumerateList(JsonElement data)
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

    private static string? JsonScalar(JsonElement v) =>
        v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
}
