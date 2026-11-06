using CsCheck;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave2;

/// <summary>2026-11-06: В2 Л4-16 / АР-1 — единая формула доли «наличные / безнал» возврата (RefundSplit). Окно возврата и офлайн-план
/// теперь считают одним кодом; тесты фиксируют правило и сверяют его со СТАРОЙ формулой окна (скопирована ниже как эталон).</summary>
public sealed class RefundSplitTests
{
    /// <summary>Эталон: формулы, которые стояли в ReturnSaleDialog.ComputeRefundSplit до переноса (ветка «смешанная оплата»).</summary>
    private static (decimal Cash, decimal NonCash) OldMixed(decimal amount, decimal cashLeft, decimal nonCashLeft) =>
        cashLeft + nonCashLeft > 0m
            ? (Math.Min(amount, Math.Round(amount * cashLeft / (cashLeft + nonCashLeft), 2, MidpointRounding.AwayFromZero)),
               amount - Math.Min(amount, Math.Round(amount * cashLeft / (cashLeft + nonCashLeft), 2, MidpointRounding.AwayFromZero)))
            : (amount, 0m);

    [Theory]
    [InlineData(100, 60, 40, 60, 40)]       // пропорционально остаткам
    [InlineData(10, 1, 2, 3.33, 6.67)]       // 3,333… → 3,33, безнал — остаток (сумма сходится)
    [InlineData(10, 1, 1, 5, 5)]
    [InlineData(0.01, 1, 1, 0.01, 0)]        // 0,005 → вверх «от нуля»
    [InlineData(50, 0, 80, 0, 50)]           // наличных не осталось — всё безналом
    [InlineData(50, 80, 0, 50, 0)]           // безнала не осталось — всё наличными
    [InlineData(50, 0, 0, 50, 0)]            // нечего делить — всё наличными
    public void Split_Proportional_SumsToAmount(double amount, double cashLeft, double nonCashLeft, double cash, double nonCash)
    {
        var (c, n) = RefundSplit.Split((decimal)amount, (decimal)cashLeft, (decimal)nonCashLeft);
        c.Should().Be((decimal)cash);
        n.Should().Be((decimal)nonCash);
        (c + n).Should().Be((decimal)amount);
    }

    [Fact]
    public void Split_NegativeLeftovers_AreTreatedAsZero()
    {
        RefundSplit.Split(40m, -10m, 20m).Should().Be((0m, 40m));
        RefundSplit.Split(40m, -10m, -5m).Should().Be((40m, 0m));
    }

    [Fact]
    public void Split_MatchesOldWindowFormula_OnRandomMoney() =>
        Gen.Select(Gen.Int[1, 2_000_000], Gen.Int[0, 1_000_000], Gen.Int[0, 1_000_000]).Sample((a, c, n) =>
        {
            var amount = a / 100m;
            var cashLeft = c / 100m;
            var nonCashLeft = n / 100m;
            var expected = OldMixed(amount, cashLeft, nonCashLeft);
            var actual = RefundSplit.Split(amount, cashLeft, nonCashLeft);
            actual.Should().Be(expected);
            (actual.Cash + actual.NonCash).Should().Be(amount);
            actual.Cash.Should().BeInRange(0m, amount);
        }, iter: 5000);

    [Fact]
    public void OfflinePlanner_MixedSale_UsesTheSameSplit()
    {
        // чек 100 сом: 70 наличными + 30 безналом; возврат одной из двух позиций по 50 → доля 35/15
        var sale = new OfflineSaleSnapshot
        {
            SaleRef = "s1", Method = "mixed", Total = 100m, LinesTotal = 100m, PaidCash = 70m, PaidNonCash = 30m,
            Lines =
            {
                new OfflineSaleLine { Key = "0", Name = "A", Quantity = 1, UnitPrice = 50m, LineTotal = 50m },
                new OfflineSaleLine { Key = "1", Name = "B", Quantity = 1, UnitPrice = 50m, LineTotal = 50m },
            },
        };

        var plan = OfflineRefundPlanner.Plan(sale, new List<OfflinePriorRefund>(), new[] { ("0", 1.0) }, clientDebtKnown: 0m);

        plan.Ok.Should().BeTrue(plan.Error);
        var expected = RefundSplit.Split(plan.Amount, 70m, 30m);
        plan.Cash.Should().Be(expected.Cash);
        plan.NonCash.Should().Be(expected.NonCash);
        plan.Cash.Should().Be(35m);
        plan.NonCash.Should().Be(15m);
    }

    [Fact]
    public void OfflinePlanner_DebtSale_PrepaidMoneySplitByCashShare()
    {
        // чек 100: предоплата 40 наличными, долг 60; клиент долг не гасил (известен 60); возврат всего чека 100 → долг −60, деньгами 40 — все наличные
        var sale = new OfflineSaleSnapshot
        {
            SaleRef = "s2", Method = "debt", Total = 100m, LinesTotal = 100m, PaidCash = 40m, PaidNonCash = 0m,
            Lines = { new OfflineSaleLine { Key = "0", Name = "A", Quantity = 1, UnitPrice = 100m, LineTotal = 100m } },
        };
        var plan = OfflineRefundPlanner.Plan(sale, new List<OfflinePriorRefund>(), new[] { ("0", 1.0) }, clientDebtKnown: 60m);

        plan.Ok.Should().BeTrue(plan.Error);
        (plan.Cash + plan.NonCash + plan.DebtReduced).Should().Be(plan.Amount);
        plan.DebtReduced.Should().Be(60m);
        plan.Cash.Should().Be(40m);
    }
}
