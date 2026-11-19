using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Один день сотрудника в табеле сервера: план/факт, опоздание, отработанные минуты, выручка. Все поля, кроме даты, необязательны.</summary>
public sealed class ServerTimesheetDay
{
    public DateOnly Date { get; init; }
    public DateTime? PlannedStart { get; init; }
    public DateTime? PlannedEnd { get; init; }
    public DateTime? OpenedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    public int LateMinutes { get; init; }
    public int WorkedMinutes { get; init; }
    public decimal? Revenue { get; init; }
}

/// <summary>Сотрудник в табеле сервера: дни и итоги (если сервер итогов не прислал — считаются по дням).</summary>
public sealed class ServerTimesheetEmployee
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<ServerTimesheetDay> Days { get; init; } = Array.Empty<ServerTimesheetDay>();
    public int WorkedMinutes { get; init; }
    public int DaysWorked { get; init; }
    public int LateMinutes { get; init; }
    public decimal? Revenue { get; init; }
}

/// <summary>2026-11-19 (ответ Умара 19.11, п. 1): готовый табель сервера GET api/main/staff/timesheet/?month=YYYY-MM — по сотрудникам и дням план/факт, опоздания, минуты, выручка, итоги.
/// Точная форма JSON бэкендом не описана (в ТЗ Б-64 — по дням {planned_start, planned_end, opened_at, closed_at, late_minutes, worked_minutes, revenue} и итоги сотрудника), поэтому разбор терпимый:
/// корень — массив или объект с employees/results/items/data/rows/staff (или один сотрудник с days); сотрудник — employee/employee_id/user/user_id/id + имя; дни — массив или объект «дата → день»;
/// неизвестные поля игнорируются, отсутствующие — 0/null. Касса использует табель только как запасной источник часов по дням, где смены месяца не пришли (отметки и смены он не перекрывает).</summary>
public sealed class ServerTimesheet
{
    public int Year { get; init; }
    public int Month { get; init; }
    public IReadOnlyList<ServerTimesheetEmployee> Employees { get; init; } = Array.Empty<ServerTimesheetEmployee>();

    private static readonly string[] ListKeys = ["employees", "results", "items", "rows", "staff", "data"];
    private static readonly string[] IdKeys = ["employee_id", "user_id", "employee", "user", "cashier_id", "id"];
    private static readonly string[] NameKeys = ["employee_name", "full_name", "name", "user_name", "cashier", "cashier_name"];
    private static readonly string[] DayKeys = ["days", "rows", "items", "by_day", "daily"];
    private static readonly string[] TotalKeys = ["totals", "total", "summary"];

    public static ServerTimesheet Parse(JsonElement root, int year, int month)
    {
        var list = new List<ServerTimesheetEmployee>();
        try
        {
            Collect(root, list, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            PosLogger.Log($"Табель сервера: неожиданная форма ответа ({ex.GetType().Name}) — не используется.", "STAFF");
            list.Clear();
        }
        return new ServerTimesheet { Year = year, Month = month, Employees = list };
    }

    private static void Collect(JsonElement el, List<ServerTimesheetEmployee> into, int depth)
    {
        if (depth > 3)
            return;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in el.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object && ReadEmployee(x) is { } e)
                    into.Add(e);
            return;
        }
        if (el.ValueKind != JsonValueKind.Object)
            return;
        foreach (var k in ListKeys)
            if (el.TryGetProperty(k, out var part) && part.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                var before = into.Count;
                Collect(part, into, depth + 1);
                if (into.Count > before)
                    return;
            }
        if (ReadEmployee(el) is { } single) // кассир без права «Сотрудники»: сервер мог вернуть только себя прямо в корне
            into.Add(single);
    }

    private static ServerTimesheetEmployee? ReadEmployee(JsonElement e)
    {
        var daysEl = DayKeys.Select(k => e.TryGetProperty(k, out var d) ? d : default).FirstOrDefault(d => d.ValueKind is JsonValueKind.Array or JsonValueKind.Object);
        if (daysEl.ValueKind == JsonValueKind.Undefined)
            return null;
        var key = "";
        var name = Str(e, NameKeys) ?? "";
        foreach (var k in IdKeys)
            if (e.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Object)
                {
                    key = Str(v, "id", "uuid") ?? key;
                    if (name.Length == 0)
                        name = Str(v, NameKeys) ?? "";
                }
                else
                    key = Scalar(v) ?? key;
                if (key.Length > 0)
                    break;
            }
        var days = new List<ServerTimesheetDay>();
        if (daysEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in daysEl.EnumerateArray())
                if (d.ValueKind == JsonValueKind.Object && ReadDay(d, null) is { } day)
                    days.Add(day);
        }
        else
        {
            foreach (var p in daysEl.EnumerateObject()) // {"2026-11-05": {...}}
                if (p.Value.ValueKind == JsonValueKind.Object && ReadDay(p.Value, p.Name) is { } day)
                    days.Add(day);
        }

        var totals = TotalKeys.Select(k => e.TryGetProperty(k, out var t) ? t : default).FirstOrDefault(t => t.ValueKind == JsonValueKind.Object);
        var hasTotals = totals.ValueKind == JsonValueKind.Object;
        int? worked = hasTotals && Num(totals, "worked_minutes", "minutes") is { } w ? (int)w : null;
        int? late = hasTotals && Num(totals, "late_minutes") is { } l ? (int)l : null;
        int? daysWorked = hasTotals && Num(totals, "days_worked", "worked_days", "days") is { } dw ? (int)dw : null;
        return new ServerTimesheetEmployee
        {
            Key = key,
            Name = name,
            Days = days,
            WorkedMinutes = worked ?? days.Sum(d => d.WorkedMinutes),
            LateMinutes = late ?? days.Sum(d => d.LateMinutes),
            DaysWorked = daysWorked ?? days.Count(d => d.WorkedMinutes > 0),
            Revenue = (hasTotals ? Num(totals, "revenue") : null) ?? SumRevenue(days),
        };
    }

    private static decimal? SumRevenue(List<ServerTimesheetDay> days) => days.Any(d => d.Revenue.HasValue) ? days.Sum(d => d.Revenue ?? 0) : null;

    private static ServerTimesheetDay? ReadDay(JsonElement d, string? dateFromKey)
    {
        var text = dateFromKey ?? Str(d, "date", "day");
        if (text == null || text.Length < 10 || !DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;
        return new ServerTimesheetDay
        {
            Date = date,
            PlannedStart = Time(d, date, "planned_start"),
            PlannedEnd = Time(d, date, "planned_end"),
            OpenedAt = Time(d, date, "opened_at"),
            ClosedAt = Time(d, date, "closed_at"),
            LateMinutes = (int)(Num(d, "late_minutes") ?? 0),
            WorkedMinutes = (int)(Num(d, "worked_minutes") ?? 0),
            Revenue = Num(d, "revenue"),
        };
    }

    /// <summary>Время дня: полная дата-время (с поясом — в местное) или «HH:mm[:ss]» на дату дня.</summary>
    private static DateTime? Time(JsonElement d, DateOnly day, string name)
    {
        var s = Str(d, name);
        if (string.IsNullOrWhiteSpace(s))
            return null;
        if (s.Length > 8 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.LocalDateTime;
        return TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? day.ToDateTime(t) : null;
    }

    private static string? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };

    private static string? Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && Scalar(v) is { Length: > 0 } s)
                return s;
        return null;
    }

    private static decimal? Num(JsonElement e, params string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
                    return d;
                if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                    return s;
            }
        return null;
    }

    /// <summary>Часы по дням сотрудника (по ключу или по имени). Нет такого сотрудника — пусто.</summary>
    public Dictionary<DateOnly, double> HoursByDay(string key, string name)
    {
        var map = new Dictionary<DateOnly, double>();
        var emp = Employees.FirstOrDefault(e => key.Length > 0 && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                  ?? Employees.FirstOrDefault(e => name.Trim().Length > 0 && string.Equals(e.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (emp == null)
            return map;
        foreach (var d in emp.Days)
            if (d.WorkedMinutes > 0)
                map[d.Date] = (map.TryGetValue(d.Date, out var h) ? h : 0) + d.WorkedMinutes / 60.0;
        return map;
    }
}
