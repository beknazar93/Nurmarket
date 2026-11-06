using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave2;

/// <summary>2026-11-06: В2 Л4-15 — «Зарплата от прибыли»: доли «по своим продажам» не дают в сумме больше базы; смены длиннее 16 ч не идут в часы;
/// люди в табеле связываются по id кассира (однофамильцы не сливаются), имя — запасной ключ.</summary>
public sealed class ProfitPayWave2Tests
{
    private static ProfitPayScheme Own(string id, decimal percent = 100m) =>
        new() { UserId = id, Name = "Имя " + id, Scope = ProfitPayScope.Own, Rule = new ProfitPayRule { Base = ProfitPayBase.Margin, Percent = percent } };

    private static ProfitPayEmployee Emp(string id, decimal sales) => new() { UserId = id, Name = "Сотрудник " + id, Sales = sales };

    [Fact]
    public void OwnScope_SalesOfAllEmployeesAboveRevenue_SharesNeverExceedTheBase()
    {
        // выручка сервера после возвратов 1000, а продажи сотрудников до возвратов: 700 + 600 = 1300
        var f = new ProfitPayFigures { Revenue = 1_000m, Cogs = 600m, Gross = 400m };
        var res = ProfitPayCalculator.Compute(new ProfitPayShopSettings { Rounding = ProfitPayRounding.Tyiyn }, [Own("a"), Own("b")], f, [Emp("a", 700m), Emp("b", 600m)]);

        res.Lines.Sum(l => l.BaseValue).Should().BeLessThanOrEqualTo(res.Margin, "сумма баз всех сотрудников не больше результата магазина");
        res.Lines.Sum(l => l.BaseValue).Should().BeApproximately(400m, 0.0001m);
        res.Lines.Single(l => l.UserId == "a").BaseValue.Should().BeApproximately(400m * 700m / 1300m, 0.0001m);
    }

    [Fact]
    public void OwnScope_SalesBelowRevenue_KeepsOldShare()
    {
        var f = new ProfitPayFigures { Revenue = 10_000m, Cogs = 6_000m, Gross = 4_000m };
        var res = ProfitPayCalculator.Compute(new ProfitPayShopSettings(), [Own("a", 10m)], f, [Emp("a", 5_000m)]);

        res.Lines.Single().BaseValue.Should().Be(2_000m);
    }

    private static ShiftHistoryEntry Shift(string name, string id, DateTime from, double hours) =>
        new() { Cashier = name, CashierId = id, OpenedAt = from, ClosedAt = from.AddHours(hours) };

    [Fact]
    public void HoursInPeriod_ShiftLongerThanSixteenHours_IsSkippedAndCounted()
    {
        var day = new DateTime(2026, 11, 2, 8, 0, 0);
        var shifts = new[]
        {
            Shift("Бек", "", day, 8),
            Shift("Бек", "", day.AddDays(1), 60),    // забыли закрыть на выходные
            Shift("Бек", "", day.AddDays(5), 16),    // ровно 16 — считается
            Shift("Бек", "", day.AddDays(6), 16.5),  // чуть больше — нет
        };

        var hours = ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30), out var skipped);

        skipped.Should().Be(2);
        hours["бек"].Should().Be(24m);
    }

    [Fact]
    public void HoursInPeriod_LinksByCashierId_NamesakesDoNotMerge()
    {
        var day = new DateTime(2026, 11, 2, 9, 0, 0);
        var shifts = new[]
        {
            Shift("Айгуль К.", "u1", day, 6),
            Shift("Айгуль К.", "u2", day.AddDays(1), 4),
        };

        var hours = ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30));

        hours[ProfitPayCalculator.IdKey("u1")].Should().Be(6m);
        hours[ProfitPayCalculator.IdKey("u2")].Should().Be(4m);
        hours.Should().NotContainKey(ProfitPayCalculator.NameKey("Айгуль К."), "двух людей с одним именем по имени различить нельзя — запасного ключа нет");
    }

    [Fact]
    public void HoursInPeriod_RenamedCashier_SameId_OneTotal_NameKeyStillWorksAsFallback()
    {
        var day = new DateTime(2026, 11, 2, 9, 0, 0);
        var shifts = new[]
        {
            Shift("Бек", "u7", day, 5),
            Shift("Бекзат", "u7", day.AddDays(1), 3),
        };

        var hours = ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30));

        hours[ProfitPayCalculator.IdKey("u7")].Should().Be(8m, "переименование не делит часы");
        hours["бек"].Should().Be(8m, "запасной ключ по имени ведёт на общий итог своего же id (имя принадлежит одному человеку)");
    }

    [Fact]
    public void HoursInPeriod_ShiftWithoutId_UsesNameKey_AsBefore()
    {
        var shifts = new[] { Shift("  Нур   Тест ", "", new DateTime(2026, 11, 1, 9, 0, 0), 3.5) };

        ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30))["нур тест"].Should().Be(3.5m);
    }
}
