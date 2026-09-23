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

        return filtered
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Cashier) ? "—" : s.Cashier, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                return new StaffTimesheetRow
                {
                    Cashier = g.Key,
                    ShiftCount = list.Count,
                    DaysWorked = list.Select(s => s.OpenedAt!.Value.Date).Distinct().Count(),
                    TotalWorked = list.Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.ClosedAt!.Value - s.OpenedAt!.Value)),
                    TotalRevenue = list.Sum(s => s.Revenue),
                    FirstShift = list.Min(s => s.OpenedAt),
                    LastShift = list.Max(s => s.OpenedAt),
                };
            })
            .OrderByDescending(r => r.TotalWorked)
            .ToList();
    }
}
