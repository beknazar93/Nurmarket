using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Staff;

public sealed class StaffTimesheetServiceTests
{
    private static ShiftHistoryEntry Shift(string cashier, DateTime opened, DateTime? closed, decimal revenue) => new()
    {
        ShiftNumber = Guid.NewGuid().ToString("N"),
        Cashier = cashier,
        OpenedAt = opened,
        ClosedAt = closed,
        Revenue = revenue,
    };

    [Fact]
    public void Aggregate_SumsHoursAndRevenuePerCashier()
    {
        var shifts = new[]
        {
            Shift("Айгуль", new DateTime(2026, 9, 1, 9, 0, 0), new DateTime(2026, 9, 1, 17, 0, 0), 10000m),
            Shift("Айгуль", new DateTime(2026, 9, 2, 9, 0, 0), new DateTime(2026, 9, 2, 15, 0, 0), 8000m),
            Shift("Бекзат", new DateTime(2026, 9, 1, 8, 0, 0), new DateTime(2026, 9, 1, 20, 0, 0), 25000m),
        };

        var rows = StaffTimesheetService.Aggregate(shifts, from: null, to: null);

        rows.Should().HaveCount(2);
        var aigul = rows.Single(r => r.Cashier == "Айгуль");
        aigul.ShiftCount.Should().Be(2);
        aigul.DaysWorked.Should().Be(2);
        aigul.TotalWorked.Should().Be(TimeSpan.FromHours(14));
        aigul.TotalRevenue.Should().Be(18000m);
        aigul.AverageShift.Should().Be(TimeSpan.FromHours(7));

        var bekzat = rows.Single(r => r.Cashier == "Бекзат");
        bekzat.ShiftCount.Should().Be(1);
        bekzat.TotalWorked.Should().Be(TimeSpan.FromHours(12));
    }

    [Fact]
    public void Aggregate_CountsTwoShiftsSameDayAsOneDayWorked()
    {
        var shifts = new[]
        {
            Shift("Айгуль", new DateTime(2026, 9, 1, 9, 0, 0), new DateTime(2026, 9, 1, 13, 0, 0), 1000m),
            Shift("Айгуль", new DateTime(2026, 9, 1, 14, 0, 0), new DateTime(2026, 9, 1, 18, 0, 0), 1000m),
        };

        var rows = StaffTimesheetService.Aggregate(shifts, from: null, to: null);

        rows.Should().ContainSingle();
        rows[0].ShiftCount.Should().Be(2);
        rows[0].DaysWorked.Should().Be(1);
    }

    [Fact]
    public void Aggregate_ExcludesOpenShifts()
    {
        var shifts = new[]
        {
            Shift("Айгуль", new DateTime(2026, 9, 1, 9, 0, 0), closed: null, revenue: 0m),
        };

        var rows = StaffTimesheetService.Aggregate(shifts, from: null, to: null);

        rows.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_FiltersByDateRange()
    {
        var shifts = new[]
        {
            Shift("Айгуль", new DateTime(2026, 9, 1, 9, 0, 0), new DateTime(2026, 9, 1, 17, 0, 0), 10000m),
            Shift("Айгуль", new DateTime(2026, 9, 10, 9, 0, 0), new DateTime(2026, 9, 10, 17, 0, 0), 5000m),
        };

        var rows = StaffTimesheetService.Aggregate(shifts, from: new DateTime(2026, 9, 5), to: new DateTime(2026, 9, 15));

        rows.Should().ContainSingle();
        rows[0].ShiftCount.Should().Be(1);
        rows[0].TotalRevenue.Should().Be(5000m);
    }

    [Fact]
    public void Aggregate_GroupsCashierNameCaseInsensitively()
    {
        var shifts = new[]
        {
            Shift("айгуль", new DateTime(2026, 9, 1, 9, 0, 0), new DateTime(2026, 9, 1, 17, 0, 0), 1000m),
            Shift("Айгуль", new DateTime(2026, 9, 2, 9, 0, 0), new DateTime(2026, 9, 2, 17, 0, 0), 1000m),
        };

        var rows = StaffTimesheetService.Aggregate(shifts, from: null, to: null);

        rows.Should().ContainSingle();
        rows[0].ShiftCount.Should().Be(2);
    }
}
