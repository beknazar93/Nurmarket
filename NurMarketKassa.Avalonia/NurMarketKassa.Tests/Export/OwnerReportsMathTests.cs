using FluentAssertions;
using NurMarketKassa.Services;
using static NurMarketKassa.Services.OwnerReportsMath;

namespace NurMarketKassa.Tests.Export;

/// <summary>2026-11-13 (Л-13 «Отчёты владельца»): расчёты сравнения периодов, отчёта по кассирам и тепловой карты — без сети и интерфейса.</summary>
public sealed class OwnerReportsMathTests
{
    private static SaleFact Sale(string at, string cashier, decimal total, string status = "paid", decimal discount = 0m) =>
        new(DateTime.Parse(at, System.Globalization.CultureInfo.InvariantCulture), cashier, total, discount, status);

    // ── Сравнение периодов ──

    [Fact]
    public void ComparePeriod_Week_ShiftsBySevenDays()
    {
        var (from, to) = ComparePeriod(new DateTime(2026, 11, 7), new DateTime(2026, 11, 13), PeriodKind.Week, CompareBase.Previous);
        from.Should().Be(new DateTime(2026, 10, 31));
        to.Should().Be(new DateTime(2026, 11, 6));
    }

    [Fact]
    public void ComparePeriod_Month_SameDaysOfPreviousMonth_ClampedToShortMonth()
    {
        // 1–31 марта к февралю: февраль короче — конец не вылезает в март
        var (from, to) = ComparePeriod(new DateTime(2027, 3, 1), new DateTime(2027, 3, 31), PeriodKind.Month, CompareBase.Previous);
        from.Should().Be(new DateTime(2027, 2, 1));
        to.Should().Be(new DateTime(2027, 2, 28));
        // 1–13 ноября к октябрю: те же числа
        var (f2, t2) = ComparePeriod(new DateTime(2026, 11, 1), new DateTime(2026, 11, 13), PeriodKind.Month, CompareBase.Previous);
        f2.Should().Be(new DateTime(2026, 10, 1));
        t2.Should().Be(new DateTime(2026, 10, 13));
    }

    [Fact]
    public void ComparePeriod_Day_IsYesterday_Custom_IsSameLengthBefore()
    {
        var (f, t) = ComparePeriod(new DateTime(2026, 11, 13), new DateTime(2026, 11, 13), PeriodKind.Day, CompareBase.Previous);
        (f, t).Should().Be((new DateTime(2026, 11, 12), new DateTime(2026, 11, 12)));
        var (cf, ct) = ComparePeriod(new DateTime(2026, 11, 1), new DateTime(2026, 11, 10), PeriodKind.Custom, CompareBase.Previous);
        cf.Should().Be(new DateTime(2026, 10, 22));
        ct.Should().Be(new DateTime(2026, 10, 31));
    }

    [Fact]
    public void ComparePeriod_YearAgo_KeepsLengthAndHandlesLeapDay()
    {
        var (f, t) = ComparePeriod(new DateTime(2026, 11, 1), new DateTime(2026, 11, 13), PeriodKind.Month, CompareBase.YearAgo);
        (f, t).Should().Be((new DateTime(2025, 11, 1), new DateTime(2025, 11, 13)));
        // 29 февраля 2028 → 28 февраля 2027, период той же длины
        var (lf, lt) = ComparePeriod(new DateTime(2028, 2, 29), new DateTime(2028, 3, 2), PeriodKind.Custom, CompareBase.YearAgo);
        lf.Should().Be(new DateTime(2027, 2, 28));
        lt.Should().Be(new DateTime(2027, 3, 2));
    }

    [Fact]
    public void ComparePeriod_ReversedDates_AreSwapped()
    {
        var (f, t) = ComparePeriod(new DateTime(2026, 11, 13), new DateTime(2026, 11, 7), PeriodKind.Week, CompareBase.Previous);
        (f, t).Should().Be((new DateTime(2026, 10, 31), new DateTime(2026, 11, 6)));
    }

    [Theory]
    [InlineData(110, 100, 10.0)]
    [InlineData(90, 100, -10.0)]
    [InlineData(100, 100, 0.0)]
    [InlineData(1234.56, 1000, 23.5)]
    public void DeltaPercent_RoundsToTenth(double cur, double prev, double expected) =>
        DeltaPercent((decimal)cur, (decimal)prev).Should().Be((decimal)expected);

    [Fact]
    public void DeltaPercent_NoPrevious_IsNull()
    {
        DeltaPercent(100m, 0m).Should().BeNull();
        DeltaPercent(0m, 0m).Should().BeNull();
    }

    [Fact]
    public void Arrow_UpDownFlatAndNone()
    {
        Arrow(12.5m).Should().Be("↑ 12,5 %");
        Arrow(-3m).Should().Be("↓ 3 %");
        Arrow(0.3m).Should().Be("→ 0 %");
        Arrow(-0.4m).Should().Be("→ 0 %");
        Arrow(250m).Should().Be("↑ 250 %");
        Arrow(null).Should().Be("—");
        Direction(12m).Should().Be(1);
        Direction(-12m).Should().Be(-1);
        Direction(0m).Should().Be(0);
        Direction(null).Should().BeNull();
    }

    // ── Кассиры ──

    [Fact]
    public void ByCashier_CountsRevenueReturnsDiscountsAndSortsByRevenue()
    {
        var sales = new[]
        {
            Sale("2026-11-13 10:00", "Айгуль", 1000.50m, discount: 50m),
            Sale("2026-11-13 11:00", "Айгуль", 499.50m),
            Sale("2026-11-13 12:00", "Айгуль", 300m, "canceled"),
            Sale("2026-11-13 12:30", "Айгуль", 200m, "partially_returned", discount: 10m),
            Sale("2026-11-13 13:00", "Бек", 5000m),
            Sale("2026-11-13 14:00", "Бек", 800m, "debt"),
            Sale("2026-11-13 15:00", "", 100m),
        };
        var rows = ByCashier(sales);
        rows.Select(r => r.Cashier).Should().ContainInOrder("Бек", "Айгуль");
        var bek = rows[0];
        bek.Revenue.Should().Be(5000m);
        bek.Checks.Should().Be(1); // долг в выручку и чеки не входит
        var ai = rows.Single(r => r.Cashier == "Айгуль");
        ai.Revenue.Should().Be(1700m);
        ai.Checks.Should().Be(3);
        ai.AvgCheck.Should().Be(566.67m);
        ai.Returns.Should().Be(1);
        ai.ReturnsSum.Should().Be(300m);
        ai.PartialReturns.Should().Be(1);
        ai.DiscountSum.Should().Be(60m);
        ai.ReturnsPercent.Should().Be(25m); // 1 из 4
        rows.Should().Contain(r => r.Cashier == CashierName(null)); // кассир без имени — «Не указан»
    }

    [Fact]
    public void ByCashier_MoneyIsExactDecimal_NoFloatDrift()
    {
        var sales = Enumerable.Range(0, 1000).Select(i => Sale("2026-11-13 10:00", "А", 0.1m)).ToList();
        var row = ByCashier(sales).Single();
        row.Revenue.Should().Be(100m);
        row.Checks.Should().Be(1000);
        row.AvgCheck.Should().Be(0.1m);
    }

    [Fact]
    public void WithErrors_AddsCountsByNameIgnoringCase_Total_SumsAll()
    {
        var rows = ByCashier([Sale("2026-11-13 10:00", "Айгуль", 100m), Sale("2026-11-13 11:00", "Бек", 200m)]);
        var withErrors = WithErrors(rows, new Dictionary<string, int> { ["айгуль"] = 3, ["Неизвестный"] = 7 });
        withErrors.Single(r => r.Cashier == "Айгуль").Errors.Should().Be(3);
        withErrors.Single(r => r.Cashier == "Бек").Errors.Should().Be(0);
        var total = Total(withErrors);
        total.Revenue.Should().Be(300m);
        total.Checks.Should().Be(2);
        total.AvgCheck.Should().Be(150m);
        total.Errors.Should().Be(3);
    }

    // ── Тепловая карта ──

    [Fact]
    public void WeekdayIndex_MondayFirst()
    {
        WeekdayIndex(new DateTime(2026, 11, 9)).Should().Be(0); // понедельник
        WeekdayIndex(new DateTime(2026, 11, 13)).Should().Be(4); // пятница
        WeekdayIndex(new DateTime(2026, 11, 15)).Should().Be(6); // воскресенье
    }

    [Fact]
    public void BuildHeatMap_GroupsByWeekdayAndHour_SkipsReturnsAndDebt()
    {
        var map = BuildHeatMap(
        [
            Sale("2026-11-13 18:05", "А", 1000m), // пт 18
            Sale("2026-11-13 18:55", "Б", 500m), // пт 18
            Sale("2026-11-13 09:00", "А", 100m), // пт 9
            Sale("2026-11-14 18:10", "А", 700m), // сб 18
            Sale("2026-11-14 18:20", "А", 900m, "canceled"),
            Sale("2026-11-14 19:20", "А", 900m, "debt"),
        ]);
        map.Revenue[4, 18].Should().Be(1500m);
        map.Checks[4, 18].Should().Be(2);
        map.Revenue[5, 18].Should().Be(700m);
        map.TotalRevenue.Should().Be(2300m);
        map.TotalChecks.Should().Be(4);
        map.DayRevenue(4).Should().Be(1600m);
        map.HourRevenue(18).Should().Be(2200m);
        map.Peak().Should().Be((4, 18));
        map.BestDay().Should().Be(4);
        map.BestHour().Should().Be(18);
        map.Intensity(4, 18).Should().Be(1.0);
        map.Intensity(0, 0).Should().Be(0.0);
        map.Intensity(5, 18).Should().BeGreaterThan(0.0).And.BeLessThan(1.0);
    }

    [Fact]
    public void BuildHeatMap_Empty_HasNoPeak()
    {
        var map = BuildHeatMap([]);
        map.Peak().Should().BeNull();
        map.BestDay().Should().BeNull();
        map.BestHour().Should().BeNull();
    }

    // ── Сравнение итогов ──

    [Fact]
    public void CompareTotals_RevenueChecksAverage_WithArrows()
    {
        var cur = TotalsOf([Sale("2026-11-13 10:00", "А", 1200m), Sale("2026-11-13 11:00", "А", 800m)]);
        var prev = TotalsOf([Sale("2026-11-06 10:00", "А", 1000m), Sale("2026-11-06 11:00", "А", 500m), Sale("2026-11-06 12:00", "А", 500m), Sale("2026-11-06 13:00", "А", 99m, "canceled")]);
        var lines = CompareTotals(cur, prev);
        lines[0].Percent.Should().Be(0m); // 2000 против 2000
        lines[1].Percent.Should().Be(-33.3m); // 2 против 3
        lines[2].Percent.Should().Be(50m); // 1000 против 666,67
        Arrow(lines[1].Percent).Should().Be("↓ 33,3 %");
        Arrow(lines[2].Percent).Should().Be("↑ 50 %");
    }

    [Fact]
    public void CompareTotals_NoPreviousSales_NoPercent()
    {
        var lines = CompareTotals(TotalsOf([Sale("2026-11-13 10:00", "А", 100m)]), TotalsOf([]));
        lines.Should().OnlyContain(l => l.Percent == null);
    }

    // ── Excel-выгрузка таблицы раздела ──

    [Fact]
    public void SectionTable_OfCashiers_ExportsToExcel()
    {
        var rows = ByCashier([Sale("2026-11-13 10:00", "Айгуль", 1000m), Sale("2026-11-13 11:00", "Бек", 2000m)]);
        var table = new SectionTable("Отчёт по кассирам", "13.11.2026",
            [new XlColumn("Кассир"), new XlColumn("Выручка, сом", XlKind.Money, XlTotal.Sum), new XlColumn("Чеков", XlKind.Integer, XlTotal.Sum)],
            rows.Select(r => new object?[] { r.Cashier, r.Revenue, r.Checks }).ToList());
        var path = Path.Combine(NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path, "owner-cashiers.xlsx");
        SectionTableExport.ExportToExcel(path, table, "Тест");
        new FileInfo(path).Length.Should().BeGreaterThan(1000);
    }
}
