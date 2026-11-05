using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-05 (аудит №4, волна 0-A): первые property-тесты денег (CsCheck, случайные чеки).
/// Правило сервера (опыты на NBS 01.11.2026): подытог = ВНИЗ до 0,01 от суммы весовых строк + ВВЕРХ (половина вверх) до 0,01 от суммы остальных;
/// скидка чека процентом — от (подытог − скидки строк), округляется отдельно; итог = подытог − скидки. Проверяем свойства, а не примеры:
/// формулу против независимого расчёта в decimal, независимость от порядка строк, границы скидки, сдачу, цепочку возвратов.
/// Числа строятся целыми (тысячные доли количества, копейки цены), поэтому точное значение известно заранее.</summary>
public sealed class MoneyPropertyTests
{
    private readonly record struct Line(int QtyMilli, int PriceCents, bool Weighed)
    {
        public double Qty => QtyMilli / 1000.0;
        public double Price => PriceCents / 100.0;
        public decimal Exact => QtyMilli / 1000m * (PriceCents / 100m);
    }

    private static readonly Gen<Line> LineGen =
        Gen.Select(Gen.Int[1, 20000], Gen.Int[1, 500000], Gen.Bool, (q, p, w) => new Line(q, p, w));

    private static readonly Gen<List<Line>> LinesGen = LineGen.List[1, 8];

    private static CartTotalsCalculator.CartTotals Calc(IEnumerable<Line> lines, int? orderDiscountPercent = null)
    {
        var items = new JsonArray();
        var i = 0;
        foreach (var line in lines)
        {
            items.Add(new JsonObject
            {
                ["product_id"] = "p" + i++,
                ["quantity"] = line.Qty,
                ["unit_price"] = line.Price,
                ["server_is_weight"] = line.Weighed,
            });
        }
        var cart = new JsonObject { ["items"] = items };
        if (orderDiscountPercent is { } pct)
            cart["order_discount_percent"] = pct;
        using var doc = JsonDocument.Parse(cart.ToJsonString());
        return CartTotalsCalculator.Calculate(doc.RootElement);
    }

    /// <summary>Независимая модель правила сервера (без скидок).</summary>
    private static decimal ServerSubtotal(IReadOnlyCollection<Line> lines)
    {
        var weighed = lines.Where(l => l.Weighed).Sum(l => l.Exact);
        var other = lines.Where(l => !l.Weighed).Sum(l => l.Exact);
        return Math.Floor(weighed * 100m) / 100m + Math.Round(other, 2, MidpointRounding.AwayFromZero);
    }

    [Fact]
    public void Total_MatchesServerRule_FloorOfWeighedSum_PlusHalfUpOfOthers() =>
        LinesGen.Sample(lines =>
        {
            var totals = Calc(lines);
            var expected = ServerSubtotal(lines);
            ((decimal)totals.Subtotal).Should().Be(expected);
            ((decimal)totals.TotalDue).Should().Be(expected, "без скидок итог равен подытогу");
        }, iter: 300);

    [Fact]
    public void Total_HasAtMostTwoDecimals_AndIsNeverNegative() =>
        LinesGen.Sample(lines =>
        {
            var due = (decimal)Calc(lines).TotalDue;
            due.Should().BeGreaterThanOrEqualTo(0m);
            (due * 100m).Should().Be(Math.Round(due * 100m), "копейки — целое число");
        }, iter: 300);

    [Fact]
    public void Total_DoesNotDependOnLineOrder() =>
        LinesGen.Sample(lines =>
        {
            var forward = Calc(lines).TotalDue;
            var reversed = Calc(Enumerable.Reverse(lines)).TotalDue;
            reversed.Should().Be(forward);
        }, iter: 300);

    [Fact]
    public void WeighedOnly_RoundsDown_NotHalfUp() =>
        Gen.Select(Gen.Int[1, 20000], Gen.Int[1, 500000], (q, p) => new Line(q, p, true)).List[1, 6].Sample(lines =>
        {
            var exactSum = lines.Sum(l => l.Exact);
            ((decimal)Calc(lines).TotalDue).Should().Be(Math.Floor(exactSum * 100m) / 100m);
        }, iter: 300);

    [Fact]
    public void NonWeighedOnly_RoundsHalfUp() =>
        Gen.Select(Gen.Int[1, 20000], Gen.Int[1, 500000], (q, p) => new Line(q, p, false)).List[1, 6].Sample(lines =>
        {
            var exactSum = lines.Sum(l => l.Exact);
            ((decimal)Calc(lines).TotalDue).Should().Be(Math.Round(exactSum, 2, MidpointRounding.AwayFromZero));
        }, iter: 300);

    [Fact]
    public void OrderDiscountPercent_StaysWithinBounds_AndEqualsSubtotalMinusRoundedDiscount() =>
        Gen.Select(LinesGen, Gen.Int[0, 100]).Sample((lines, pct) =>
        {
            var totals = Calc(lines, pct);
            var subtotal = ServerSubtotal(lines);
            var discount = Math.Round(subtotal * pct / 100m, 2, MidpointRounding.AwayFromZero);

            ((decimal)totals.TotalDue).Should().BeInRange(0m, subtotal);
            ((decimal)totals.OrderDiscount).Should().Be(discount, "скидка чека округляется отдельно, половина вверх");
            ((decimal)totals.TotalDue).Should().Be(subtotal - discount);
        }, iter: 300);

    [Fact]
    public void BiggerDiscountPercent_NeverIncreasesTotal() =>
        Gen.Select(LinesGen, Gen.Int[0, 99]).Sample((lines, pct) =>
        {
            Calc(lines, pct + 1).TotalDue.Should().BeLessThanOrEqualTo(Calc(lines, pct).TotalDue);
        }, iter: 300);

    [Fact]
    public void FullDiscount_MakesTotalZero() =>
        LinesGen.Sample(lines => Calc(lines, 100).TotalDue.Should().Be(0), iter: 200);

    [Fact]
    public void Change_IsCashMinusTotal_WhenCashEnough_AndZeroOtherwise() =>
        Gen.Select(Gen.Int[0, 5_000_000], Gen.Int[0, 5_000_000]).Sample((dueCents, cashCents) =>
        {
            var due = dueCents / 100.0;
            var cash = cashCents / 100.0;
            var change = CartTotalsCalculator.CalculateChange(cash, due);
            if (cashCents > dueCents)
            {
                ((decimal)change).Should().Be((cashCents - dueCents) / 100m);
                (cash - change).Should().BeApproximately(due, 0.0051, "оплачено ровно на сумму чека");
            }
            else
            {
                change.Should().Be(0, "сдачи нет, если денег не больше суммы чека");
            }
        }, iter: 500);

    // ---------------------------------------------------------------- возвраты

    private static OfflineSaleSnapshot MakeSale(IReadOnlyList<(int Qty, int PriceCents)> lines)
    {
        var sale = new OfflineSaleSnapshot { SaleRef = "S1", Method = "cash", IsLocal = true };
        for (var i = 0; i < lines.Count; i++)
        {
            var total = lines[i].Qty * lines[i].PriceCents / 100m;
            sale.Lines.Add(new OfflineSaleLine
            {
                Key = i.ToString(), Name = "Товар " + i, ProductId = "p" + i, Quantity = lines[i].Qty,
                UnitPrice = lines[i].PriceCents / 100m, LineTotal = total,
            });
            sale.LinesTotal += total;
        }
        sale.Total = sale.LinesTotal;
        sale.PaidCash = sale.Total;
        return sale;
    }

    [Fact]
    public void Refunds_OneUnitAtATime_NeverExceedPaid_AndAddUpToTotal() =>
        Gen.Select(Gen.Int[1, 4], Gen.Int[1, 100000]).List[1, 4].Sample(spec =>
        {
            var sale = MakeSale(spec);
            var priors = new List<OfflinePriorRefund>();
            var returnedTotal = 0m;
            var step = 0;
            foreach (var line in sale.Lines)
            {
                for (var unit = 0; unit < (int)line.Quantity; unit++)
                {
                    var plan = OfflineRefundPlanner.Plan(sale, priors, new[] { (line.Key, 1.0) }, clientDebtKnown: 0m);
                    plan.Ok.Should().BeTrue(plan.Error);
                    plan.Amount.Should().BeGreaterThan(0m);
                    (plan.Cash + plan.NonCash + plan.DebtReduced).Should().Be(plan.Amount, "возврат делится без потерь");
                    returnedTotal += plan.Amount;
                    returnedTotal.Should().BeLessThanOrEqualTo(sale.Total, "возвращать больше оплаченного нельзя");
                    priors.Add(new OfflinePriorRefund
                    {
                        Id = "r" + step++, Amount = plan.Amount, Cash = plan.Cash, NonCash = plan.NonCash, DebtReduced = plan.DebtReduced,
                        Lines = { (line.Key, line.Name, 1.0) },
                    });
                }
            }
            returnedTotal.Should().Be(sale.Total, "последний возврат добирает копейки округления — всего возвращено ровно оплаченное");
            priors.Sum(p => p.Cash).Should().Be(sale.PaidCash);
        }, iter: 150);

    [Fact]
    public void Refund_AfterEverythingReturned_IsRejected() =>
        Gen.Select(Gen.Int[1, 3], Gen.Int[1, 100000]).List[1, 3].Sample(spec =>
        {
            var sale = MakeSale(spec);
            var whole = OfflineRefundPlanner.Plan(sale, new List<OfflinePriorRefund>(),
                sale.Lines.Select(l => (l.Key, l.Quantity)).ToList(), clientDebtKnown: 0m);
            whole.Ok.Should().BeTrue(whole.Error);
            whole.Amount.Should().Be(sale.Total);
            var prior = new OfflinePriorRefund
            {
                Id = "r1", Amount = whole.Amount, Cash = whole.Cash, NonCash = whole.NonCash,
                Lines = sale.Lines.Select(l => ((string?)l.Key, l.Name, l.Quantity)).ToList(),
            };

            var again = OfflineRefundPlanner.Plan(sale, new[] { prior }, new[] { (sale.Lines[0].Key, 1.0) }, clientDebtKnown: 0m);

            again.Ok.Should().BeFalse("по чеку уже возвращено всё, что оплачено");
        }, iter: 100);
}
