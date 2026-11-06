using CsCheck;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — описательные тесты схемы зарплат от прибыли/маржи (ProfitPayCalculator). Фиксируют ТЕКУЩИЕ формулы:
/// маржа = валовая − возвраты − скидки; чистая прибыль = «Чистая прибыль» сервера (или валовая − расходы) с учётом отключённых статей;
/// начисление не бывает отрицательным; порог / только сверх порога / прогрессивная шкала; пул делится методом наибольших остатков.
/// Тексты шагов не проверяются (они на языке интерфейса) — только числа и признаки.</summary>
public sealed class ProfitPayCalculatorTests
{
    private static ProfitPayRule Rule(decimal pct, ProfitPayBase b = ProfitPayBase.Margin, decimal min = 0, bool excess = false, bool prog = false, decimal limit = 0, decimal above = 0) =>
        new() { Base = b, Percent = pct, MinProfit = min, OnlyExcess = excess, Progressive = prog, Limit = limit, PercentAbove = above };

    private static ProfitPayScheme Scheme(string id, ProfitPayScope scope, ProfitPayRule rule) =>
        new() { UserId = id, Name = "Имя " + id, Scope = scope, Rule = rule };

    private static ProfitPayFigures Figures(decimal revenue, decimal cogs, decimal gross, decimal? serverNet = null) =>
        new() { Revenue = revenue, Cogs = cogs, Gross = gross, ServerNet = serverNet };

    private static ProfitPayEmployee Emp(string id, decimal sales, decimal? hours = null) =>
        new() { UserId = id, Name = "Сотрудник " + id, Sales = sales, Hours = hours };

    private static ProfitPayResult Compute(ProfitPayShopSettings shop, IReadOnlyList<ProfitPayScheme> schemes, ProfitPayFigures f, params ProfitPayEmployee[] emps) =>
        ProfitPayCalculator.Compute(shop, schemes, f, emps);

    // ───────────── цифры магазина ─────────────

    [Fact]
    public void Margin_IsGrossMinusReturnsMinusDiscounts_ReturnsOnlyWhenSwitchedOn()
    {
        var f = Figures(10_000m, 6_000m, 4_000m);
        f.Returns = 300m;
        f.Discounts = 100m;

        var off = Compute(new ProfitPayShopSettings { SubtractReturns = false }, [], f);
        off.Returns.Should().Be(0m, "возвраты уже в выручке сервера и второй раз не вычитаются");
        off.Margin.Should().Be(3_900m);

        var on = Compute(new ProfitPayShopSettings { SubtractReturns = true }, [], f);
        on.Returns.Should().Be(300m);
        on.Margin.Should().Be(3_600m);
    }

    [Fact]
    public void NetProfit_NoServerNet_IsGrossMinusAllExpenses_WithDisabledItemsAddedBack()
    {
        // расходы: аренда 500 (вычитается), зарплаты 1000 (по умолчанию НЕ вычитаются), прочее 200 (вычитается)
        var f = Figures(10_000m, 6_000m, 4_000m);
        f.Expenses["rent"] = 500m;
        f.Expenses["salaries"] = 1_000m;
        f.Expenses["other"] = 200m;

        var res = Compute(new ProfitPayShopSettings(), [], f);

        // 4000 − (500+1000+200) = 2300, зарплаты отключены — возвращаются: 3300
        res.NetProfit.Should().Be(3_300m);
        res.ExpensesSkipped.Should().ContainSingle().Which.Amount.Should().Be(1_000m);
        res.ExpensesSubtracted.Select(x => x.Amount).Should().BeEquivalentTo(new[] { 500m, 200m });
    }

    [Fact]
    public void NetProfit_ServerNetWins_OverGrossMinusExpenses()
    {
        var f = Figures(10_000m, 6_000m, 4_000m, serverNet: 2_000m);
        f.Expenses["rent"] = 500m;

        Compute(new ProfitPayShopSettings(), [], f).NetProfit.Should().Be(2_000m);
    }

    [Fact]
    public void NetProfit_SupplierCash_SubtractedOnlyWhenSwitchedOn()
    {
        var f = Figures(1_000m, 600m, 400m);
        f.SupplierCash = 100m;

        Compute(new ProfitPayShopSettings { SubtractSupplierCash = false }, [], f).NetProfit.Should().Be(400m);
        Compute(new ProfitPayShopSettings { SubtractSupplierCash = true }, [], f).NetProfit.Should().Be(300m);
    }

    // ───────────── обычные схемы ─────────────

    [Fact]
    public void Own_Scope_UsesShareOfShopRevenue()
    {
        // маржа 4000, доля сотрудника в выручке 50 % → база 2000, 10 % → 200
        var schemes = new[] { Scheme("a", ProfitPayScope.Own, Rule(10m)) };
        var res = Compute(new ProfitPayShopSettings(), schemes, Figures(10_000m, 6_000m, 4_000m), Emp("a", 5_000m));

        var line = res.Lines.Should().ContainSingle().Subject;
        line.UserId.Should().Be("a");
        line.BaseValue.Should().Be(2_000m);
        line.Amount.Should().Be(200.00m);
        line.Zeroed.Should().BeFalse();
    }

    [Fact]
    public void Own_Scope_ShareIsCappedAtOneHundredPercent()
    {
        var schemes = new[] { Scheme("a", ProfitPayScope.Own, Rule(10m)) };
        var res = Compute(new ProfitPayShopSettings(), schemes, Figures(1_000m, 600m, 400m), Emp("a", 5_000m));

        res.Lines.Single().BaseValue.Should().Be(400m, "продажи больше выручки магазина не дают больше 100 %");
    }

    [Fact]
    public void Own_Scope_NoRevenue_ShareIsZero()
    {
        var schemes = new[] { Scheme("a", ProfitPayScope.Own, Rule(10m)) };
        var res = Compute(new ProfitPayShopSettings(), schemes, Figures(0m, 0m, 400m), Emp("a", 100m));

        res.Lines.Single().Amount.Should().Be(0m);
    }

    [Fact]
    public void Own_Scope_UnknownEmployee_GetsZeroSales()
    {
        var schemes = new[] { Scheme("ghost", ProfitPayScope.Own, Rule(10m)) };
        var res = Compute(new ProfitPayShopSettings(), schemes, Figures(1_000m, 600m, 400m));

        var line = res.Lines.Single();
        line.Name.Should().Be("Имя ghost", "имени нет в списке сотрудников — берётся сохранённое в схеме");
        line.Amount.Should().Be(0m);
    }

    [Fact]
    public void Shop_Scope_TakesPercentOfWholeShopResult()
    {
        var schemes = new[] { Scheme("a", ProfitPayScope.Shop, Rule(5m, ProfitPayBase.NetProfit)) };
        var f = Figures(10_000m, 6_000m, 4_000m, serverNet: 3_000m);

        var res = Compute(new ProfitPayShopSettings(), schemes, f, Emp("a", 1m));

        res.Lines.Single().BaseValue.Should().Be(3_000m);
        res.Lines.Single().Amount.Should().Be(150m);
    }

    [Fact]
    public void InactiveScheme_BaseNone_IsIgnored()
    {
        var schemes = new[] { Scheme("a", ProfitPayScope.Shop, Rule(10m, ProfitPayBase.None)) };
        Compute(new ProfitPayShopSettings(), schemes, Figures(1_000m, 600m, 400m)).Lines.Should().BeEmpty();
    }

    [Fact]
    public void NegativeOrZeroProfit_AccrualIsZero_WithNote_NeverNegative()
    {
        var f = Figures(1_000m, 900m, 100m);
        f.Expenses["rent"] = 500m; // чистая прибыль = 100 − 500 = −400
        var schemes = new[] { Scheme("a", ProfitPayScope.Shop, Rule(10m, ProfitPayBase.NetProfit)) };

        var res = Compute(new ProfitPayShopSettings(), schemes, f);

        res.NetProfit.Should().Be(-400m);
        var line = res.Lines.Single();
        line.Amount.Should().Be(0m);
        line.Zeroed.Should().BeTrue();
        line.Note.Should().NotBeNullOrWhiteSpace();
    }

    // ───────────── ApplyRule: процент, порог, прогрессия ─────────────

    [Fact]
    public void ApplyRule_PlainPercent()
    {
        var (amount, note, steps) = ProfitPayCalculator.ApplyRule(Rule(12.5m), 1_000m);
        amount.Should().Be(125m);
        note.Should().BeEmpty();
        steps.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void ApplyRule_ZeroOrNegativeBase_IsZeroWithNote(int baseValue)
    {
        var (amount, note, _) = ProfitPayCalculator.ApplyRule(Rule(10m), baseValue);
        amount.Should().Be(0m);
        note.Should().NotBeEmpty();
    }

    [Fact]
    public void ApplyRule_BelowThreshold_IsZero()
    {
        var (amount, note, _) = ProfitPayCalculator.ApplyRule(Rule(10m, min: 1_000m), 800m);
        amount.Should().Be(0m);
        note.Should().NotBeEmpty();
    }

    [Fact]
    public void ApplyRule_ThresholdReached_PercentOfWholeBase_UnlessOnlyExcess()
    {
        ProfitPayCalculator.ApplyRule(Rule(10m, min: 1_000m), 1_500m).Amount.Should().Be(150m);
        ProfitPayCalculator.ApplyRule(Rule(10m, min: 1_000m, excess: true), 1_500m).Amount.Should().Be(50m);
    }

    [Fact]
    public void ApplyRule_ThresholdEqualToBase_IsReached()
    {
        // граница: база ровно равна порогу — порог пройден (меньше — нет)
        ProfitPayCalculator.ApplyRule(Rule(10m, min: 1_000m), 1_000m).Amount.Should().Be(100m);
        ProfitPayCalculator.ApplyRule(Rule(10m, min: 1_000m, excess: true), 1_000m).Amount.Should().Be(0m);
    }

    [Fact]
    public void ApplyRule_Progressive_SplitsAtLimit()
    {
        // до 1000 — 10 %, свыше — 20 %; база 1500 → 100 + 100
        ProfitPayCalculator.ApplyRule(Rule(10m, prog: true, limit: 1_000m, above: 20m), 1_500m).Amount.Should().Be(200m);
        // база ниже предела — только первый процент
        ProfitPayCalculator.ApplyRule(Rule(10m, prog: true, limit: 1_000m, above: 20m), 400m).Amount.Should().Be(40m);
    }

    [Theory]
    [InlineData(ProfitPayRounding.Tyiyn, "123.454", "123.45")]
    [InlineData(ProfitPayRounding.Tyiyn, "123.455", "123.46")]
    [InlineData(ProfitPayRounding.Som, "123.49", "123")]
    [InlineData(ProfitPayRounding.Som, "123.5", "124")]
    public void RoundMoney_ByMode_AwayFromZero(ProfitPayRounding mode, string input, string expected) =>
        ProfitPayCalculator.RoundMoney(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture), mode)
            .Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Compute_RoundingSom_RoundsAccrualToWholeSom()
    {
        // маржа 1000 × 12,345 % = 123,45 → 123 сом
        var schemes = new[] { Scheme("a", ProfitPayScope.Shop, Rule(12.345m)) };
        var res = Compute(new ProfitPayShopSettings { Rounding = ProfitPayRounding.Som }, schemes, Figures(1_000m, 0m, 1_000m));
        res.Lines.Single().Amount.Should().Be(123m);
    }

    // ───────────── пул ─────────────

    private static ProfitPayShopSettings PoolShop(ProfitPayShare share, decimal pct = 10m, ProfitPayRounding rounding = ProfitPayRounding.Tyiyn) =>
        new() { Share = share, Pool = Rule(pct), Rounding = rounding };

    private static ProfitPayScheme[] PoolSchemes(int n) =>
        Enumerable.Range(1, n).Select(i => Scheme("p" + i, ProfitPayScope.Pool, Rule(0m))).ToArray();

    [Fact]
    public void Pool_Equal_RemainderGoesToFirstOnTie_SumEqualsPool()
    {
        // пул 10 % от маржи 1000 = 100,00; на троих: 33,34 + 33,33 + 33,33
        var res = Compute(PoolShop(ProfitPayShare.Equal), PoolSchemes(3), Figures(2_000m, 1_000m, 1_000m),
            Emp("p1", 0), Emp("p2", 0), Emp("p3", 0));

        res.PoolAmount.Should().Be(100.00m);
        res.Lines.Select(l => l.Amount).Should().Equal(33.34m, 33.33m, 33.33m);
        res.PoolUndistributed.Should().Be(0m);
    }

    [Fact]
    public void Pool_BySales_SplitsProportionally()
    {
        var res = Compute(PoolShop(ProfitPayShare.Sales), PoolSchemes(3), Figures(10_000m, 9_000m, 1_000m),
            Emp("p1", 5_000), Emp("p2", 3_000), Emp("p3", 2_000));

        res.Lines.Select(l => l.Amount).Should().Equal(50m, 30m, 20m);
    }

    [Fact]
    public void Pool_ByHours_UsesTimesheet_EmployeeWithoutHoursGetsZero()
    {
        var res = Compute(PoolShop(ProfitPayShare.Hours), PoolSchemes(3), Figures(10_000m, 9_000m, 1_000m),
            Emp("p1", 0, 6m), Emp("p2", 0, 2m), Emp("p3", 0, null));

        res.Lines.Select(l => l.Amount).Should().Equal(75m, 25m, 0m);
    }

    [Fact]
    public void Pool_NobodyToSplit_NothingDistributed_AllUndistributed()
    {
        var res = Compute(PoolShop(ProfitPayShare.Sales), PoolSchemes(2), Figures(10_000m, 9_000m, 1_000m),
            Emp("p1", 0), Emp("p2", 0));

        res.PoolAmount.Should().Be(100m);
        res.Lines.Should().OnlyContain(l => l.Amount == 0m && l.Zeroed);
        res.PoolUndistributed.Should().Be(100m);
    }

    [Fact]
    public void Pool_NegativeProfit_PoolIsZero()
    {
        var res = Compute(PoolShop(ProfitPayShare.Equal), PoolSchemes(2), Figures(1_000m, 1_200m, -200m), Emp("p1", 0), Emp("p2", 0));

        res.PoolAmount.Should().Be(0m);
        res.Lines.Should().OnlyContain(l => l.Amount == 0m);
    }

    [Fact]
    public void Property_Pool_SharesPlusUndistributed_EqualPool_ForRandomSplits() =>
        Gen.Select(Gen.Int[1, 200_000_00], Gen.Int[1, 6], Gen.Int[0, 1], Gen.Int[0, 2], Gen.Int[0, 1_000_000].Array[6])
            .Sample((marginCents, n, roundMode, shareMode, weights) =>
            {
                var margin = marginCents / 100m;
                var share = (ProfitPayShare)shareMode;
                var shop = PoolShop(share, 17m, (ProfitPayRounding)roundMode);
                var emps = Enumerable.Range(1, n).Select(i => Emp("p" + i, share == ProfitPayShare.Sales ? weights[i - 1] : 0, share == ProfitPayShare.Hours ? weights[i - 1] / 100m : null)).ToArray();

                var res = Compute(shop, PoolSchemes(n), Figures(margin, 0m, margin), emps);

                (res.Lines.Sum(l => l.Amount) + res.PoolUndistributed).Should().Be(res.PoolAmount, "копейка пула не теряется");
                res.Lines.Should().OnlyContain(l => l.Amount >= 0m);
                res.PoolUndistributed.Should().BeGreaterThanOrEqualTo(0m);
            }, iter: 1500);

    // ───────────── табель ─────────────

    [Fact]
    public void HoursInPeriod_ShiftThroughMidnight_CutByPeriodBounds()
    {
        var shifts = new[]
        {
            new ShiftHistoryEntry { Cashier = "Айгуль К.", OpenedAt = new DateTime(2026, 11, 1, 20, 0, 0), ClosedAt = new DateTime(2026, 11, 2, 2, 0, 0) },
        };

        ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 1))["айгуль к."].Should().Be(4.00m);
        ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 2), new DateTime(2026, 11, 2))["айгуль к."].Should().Be(2.00m);
        ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 2))["айгуль к."].Should().Be(6.00m);
    }

    [Fact]
    public void HoursInPeriod_OpenShiftAndOutsideShift_AreIgnored_NamesNormalized()
    {
        var shifts = new[]
        {
            new ShiftHistoryEntry { Cashier = "Бек", OpenedAt = new DateTime(2026, 11, 1, 9, 0, 0), ClosedAt = null },
            new ShiftHistoryEntry { Cashier = "Бек", OpenedAt = new DateTime(2026, 10, 1, 9, 0, 0), ClosedAt = new DateTime(2026, 10, 1, 18, 0, 0) },
            new ShiftHistoryEntry { Cashier = "  Нур   Тест ", OpenedAt = new DateTime(2026, 11, 1, 9, 0, 0), ClosedAt = new DateTime(2026, 11, 1, 12, 30, 0) },
            new ShiftHistoryEntry { Cashier = "нур тест", OpenedAt = new DateTime(2026, 11, 1, 13, 0, 0), ClosedAt = new DateTime(2026, 11, 1, 14, 0, 0) },
        };

        var hours = ProfitPayCalculator.HoursInPeriod(shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30));

        hours.Should().NotContainKey("бек");
        hours["нур тест"].Should().Be(4.50m, "имена без регистра и лишних пробелов — один сотрудник");
    }

    [Fact]
    public void NameKey_TrimsCollapsesAndLowercases()
    {
        ProfitPayCalculator.NameKey("  Нур   ТЕСТ ").Should().Be("нур тест");
        ProfitPayCalculator.NameKey(null).Should().BeEmpty();
    }
}
