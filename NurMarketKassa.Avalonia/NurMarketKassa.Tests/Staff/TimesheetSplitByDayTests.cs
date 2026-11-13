using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Staff;

/// <summary>2026-11-11 (живой показ 1.17.85, Д-2: «Табель» показывал «~28,1», «~27,2», «~41,6» ч в день): смена делится по календарным дням, в дне — не больше 24 ч;
/// смена через полночь, многодневная, незакрытая; пометка «длиннее 16 ч — возможно, забыли закрыть»; итоги «дн · ч». Деньги и зарплату табель не трогает.</summary>
public sealed class TimesheetSplitByDayTests
{
    private static ShiftHistoryEntry Shift(string id, string name, DateTime opened, DateTime? closed) =>
        new() { CashierId = id, Cashier = name, OpenedAt = opened, ClosedAt = closed };

    private static DateOnly D(int day) => new(2026, 11, day);

    [Fact]
    public void Shift_over_midnight_is_split_between_the_two_days()
    {
        var map = TimesheetMarksService.ShiftHoursByDay([Shift("u1", "Айгуль", new DateTime(2026, 11, 5, 20, 0, 0), new DateTime(2026, 11, 6, 4, 0, 0))], "u1", "Айгуль");
        map[D(5)].Should().Be(4);
        map[D(6)].Should().Be(4);
        map.Values.Sum().Should().Be(8, "часы не теряются и не задваиваются");
    }

    [Fact]
    public void Multi_day_shift_gives_each_day_its_part_and_never_more_than_24_hours()
    {
        // «~41,6» с живого показа: смена открыта 10.11 в 08:00 и закрыта 11.11 в 17:36 — раньше вся длина ложилась на 10.11
        var map = TimesheetMarksService.ShiftHoursByDay([Shift("u1", "Nur Test", new DateTime(2026, 11, 10, 8, 0, 0), new DateTime(2026, 11, 12, 1, 36, 0))], "u1", "Nur Test");
        map[D(10)].Should().Be(16);
        map[D(11)].Should().Be(24);
        map[D(12)].Should().BeApproximately(1.6, 0.0001);
        map.Values.Should().OnlyContain(h => h <= 24, "в сутках не больше 24 часов");
    }

    [Fact]
    public void Overlapping_shifts_of_one_cashier_are_capped_at_24_hours_per_day()
    {
        var map = TimesheetMarksService.ShiftHoursByDay(
        [
            Shift("u1", "Айгуль", new DateTime(2026, 11, 1, 0, 0, 0), new DateTime(2026, 11, 1, 23, 0, 0)),
            Shift("u1", "Айгуль", new DateTime(2026, 11, 1, 10, 0, 0), new DateTime(2026, 11, 1, 18, 0, 0)), // вторая касса, та же учётка
        ], "u1", "Айгуль");
        map[D(1)].Should().Be(24, "«~28,1» в день быть не может — не больше 24 ч");
    }

    [Fact]
    public void Open_shift_is_not_counted_and_closed_shift_ending_at_midnight_does_not_touch_the_next_day()
    {
        var map = TimesheetMarksService.ShiftHoursByDay(
        [
            Shift("u1", "Айгуль", new DateTime(2026, 11, 7, 9, 0, 0), null),
            Shift("u1", "Айгуль", new DateTime(2026, 11, 8, 18, 0, 0), new DateTime(2026, 11, 9, 0, 0, 0)),
        ], "u1", "Айгуль");
        map.Should().NotContainKey(D(7), "незакрытая смена ещё не знает своей длины");
        map[D(8)].Should().Be(6);
        map.Should().NotContainKey(D(9), "смена закрыта ровно в полночь");
    }

    [Fact]
    public void Long_shift_days_are_flagged_as_maybe_left_open()
    {
        var shifts = new[]
        {
            Shift("u1", "Nur Test", new DateTime(2026, 11, 10, 8, 0, 0), new DateTime(2026, 11, 11, 17, 36, 0)), // 33,6 ч
            Shift("u1", "Nur Test", new DateTime(2026, 11, 13, 9, 0, 0), new DateTime(2026, 11, 13, 18, 0, 0)),  // обычная
        };
        var days = TimesheetMarksService.LongShiftDays(shifts, "u1", "Nur Test");
        days.Should().BeEquivalentTo([D(10), D(11)]);
        StaffTimesheetService.Aggregate(shifts, null, null).Single().LongShiftCount.Should().Be(1, "пометка «длиннее 16 ч» осталась");
    }

    [Fact]
    public void Totals_use_split_hours_so_a_present_day_never_exceeds_24()
    {
        var shifts = new[] { Shift("u1", "Nur Test", new DateTime(2026, 11, 1, 9, 0, 0), new DateTime(2026, 11, 2, 13, 6, 0)) }; // 28,1 ч
        var byShift = TimesheetMarksService.ShiftHoursByDay(shifts, "u1", "Nur Test");
        var marks = new[]
        {
            new TimesheetMark { EmployeeKey = "u1", EmployeeName = "Nur Test", Date = D(1), Status = TimesheetStatus.Present, Auto = true },
            new TimesheetMark { EmployeeKey = "u1", EmployeeName = "Nur Test", Date = D(2), Status = TimesheetStatus.Present, Auto = true },
        };
        var totals = TimesheetMarksService.Summarize(marks, D(1), D(30), byShift);
        totals.PresentDays.Should().Be(2);
        totals.PresentHours.Should().BeApproximately(28.1, 0.001, "15 ч в первый день + 13,1 ч во второй — та же длина смены, но по дням");
        byShift[D(1)].Should().Be(15);
        byShift[D(2)].Should().BeApproximately(13.1, 0.0001);
    }

    [Fact]
    public void Shift_totals_tab_keeps_the_same_hours_for_normal_and_night_shifts()
    {
        var rows = StaffTimesheetService.Aggregate(
        [
            Shift("u1", "Айгуль", new DateTime(2026, 11, 5, 20, 0, 0), new DateTime(2026, 11, 6, 4, 0, 0)),
            Shift("u1", "Айгуль", new DateTime(2026, 11, 7, 9, 0, 0), new DateTime(2026, 11, 7, 15, 0, 0)),
        ], null, null);
        rows.Single().TotalWorked.Should().Be(TimeSpan.FromHours(14));
        rows.Single().DaysWorked.Should().Be(2, "дни — по дню открытия смены, как было");
    }
}
