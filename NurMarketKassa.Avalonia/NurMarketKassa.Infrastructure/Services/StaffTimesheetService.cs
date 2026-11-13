using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>Строка табеля — агрегат по одному кассиру за выбранный период (2026-09-05, доп.
/// услуга "Учёт сотрудников" — первая часть: "Табель, смены"; "мотивация персонала" из
/// описания карточки маркетплейса сюда сознательно не входит — расчёт премий/бонусов требует
/// отдельно оговорённых правил, которых пользователь пока не задавал).</summary>
public sealed class StaffTimesheetRow
{
    public string Cashier { get; init; } = "";
    public int ShiftCount { get; init; }

    /// <summary>Число РАЗНЫХ календарных дней, в которые была хотя бы одна закрытая смена — не
    /// то же самое, что ShiftCount: два открытия за один день считаются как 1 день, а смена,
    /// открытая вечером и закрытая на следующий день после полуночи, считается по дню ОТКРЫТИЯ
    /// (2026-09-05, добавлено по запросу пользователя).</summary>
    public int DaysWorked { get; init; }

    public TimeSpan TotalWorked { get; init; }
    public decimal TotalRevenue { get; init; }
    public DateTime? FirstShift { get; init; }
    public DateTime? LastShift { get; init; }

    /// <summary>2026-11-02 (В-13): закрытых смен длиннее 16 ч — вероятно, забыли закрыть; часы по ним недостоверны, табель помечает «проверьте».</summary>
    public int LongShiftCount { get; init; }

    public TimeSpan AverageShift => ShiftCount > 0
        ? TimeSpan.FromTicks(TotalWorked.Ticks / ShiftCount)
        : TimeSpan.Zero;
}

/// <summary>Строит табель (Табель, смены — часть доп. услуги "Учёт сотрудников") из уже
/// существующих данных о сменах (ShiftHistoryService/сервер) — ничего нового не хранится и не
/// синхронизируется отдельно, это чистый агрегат поверх того, что касса и так знает о каждой
/// открытой/закрытой смене.</summary>
public static class StaffTimesheetService
{
    /// <summary>Часы считаются только по ЗАКРЫТЫМ сменам (ClosedAt задан) — открытая смена ещё
    /// не знает своей реальной длительности, включать "сейчас минус открытие" искажало бы табель
    /// каждый раз, когда кто-то просто оставил кассу открытой на ночь.</summary>
    public static IReadOnlyList<StaffTimesheetRow> Aggregate(
        IEnumerable<ShiftHistoryEntry> shifts, DateTime? from, DateTime? to)
    {
        var filtered = shifts.Where(s =>
            s.OpenedAt is { } opened &&
            s.ClosedAt is { } closed &&
            closed > opened &&
            (from is null || opened.Date >= from.Value.Date) &&
            (to is null || opened.Date <= to.Value.Date));

        // 2026-11-02 (аудит №2, В-13): группировка по id кассира (если сервер его дал), иначе по имени.
        return filtered
            .GroupBy(s => !string.IsNullOrWhiteSpace(s.CashierId) ? "id:" + s.CashierId : "n:" + (string.IsNullOrWhiteSpace(s.Cashier) ? "—" : s.Cashier), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                return new StaffTimesheetRow
                {
                    Cashier = list.Select(s => s.Cashier).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && n != "—") ?? "—",
                    LongShiftCount = list.Count(s => s.ClosedAt!.Value - s.OpenedAt!.Value > LongShift),
                    ShiftCount = list.Count,
                    DaysWorked = list.Select(s => s.OpenedAt!.Value.Date).Distinct().Count(),
                    // 2026-11-11 (Д-2): часы — по календарным дням, не больше 24 ч в день (наложившиеся смены одного кассира не дают «28 ч в сутки»).
                    TotalWorked = TimeSpan.FromHours(SplitByDay(list).Values.Sum()),
                    TotalRevenue = list.Sum(s => s.Revenue),
                    FirstShift = list.Min(s => s.OpenedAt),
                    LastShift = list.Max(s => s.OpenedAt),
                };
            })
            .OrderByDescending(r => r.TotalWorked)
            .ToList();
    }

    /// <summary>2026-11-11: смена дольше этого — «возможно, забыли закрыть» (как было: 16 ч).</summary>
    public static readonly TimeSpan LongShift = TimeSpan.FromHours(16);

    /// <summary>2026-11-11 (живой показ 1.17.85, Д-2): часы закрытых смен по календарным дням (местное время): смена через полночь и многодневная делятся по дням,
    /// в каждом дне — не больше 24 ч. Открытые смены (без ClosedAt) не считаются.</summary>
    public static Dictionary<DateOnly, double> SplitByDay(IEnumerable<ShiftHistoryEntry> shifts)
    {
        var map = new Dictionary<DateOnly, double>();
        foreach (var s in shifts)
        {
            if (s.OpenedAt is not { } o || s.ClosedAt is not { } c || c <= o)
                continue;
            var start = o;
            while (start < c)
            {
                var dayEnd = start.Date.AddDays(1);
                var end = c < dayEnd ? c : dayEnd;
                var day = DateOnly.FromDateTime(start);
                map[day] = map.GetValueOrDefault(day) + (end - start).TotalHours;
                start = end;
            }
        }
        foreach (var day in map.Keys.ToList())
            map[day] = Math.Min(24, map[day]);
        return map;
    }
}
