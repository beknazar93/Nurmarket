using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-27 (ответ Умара 27.11, п. 2, refund_calc.py): сумма возврата части строки = округл( округл(цена × кол-во − округл(скидка строки × кол-во / кол-во строки)) × (итог чека / сумма строк после скидок) ),
/// каждый шаг до копейки HALF_UP; последний возврат — ровно остаток. Раньше касса считала одной дробью без промежуточных округлений и на «половине тыйына» расходилась с сервером на 0,01
/// (жёсткий тест 26.11: 94,28 против 94,27).</summary>
public sealed class RefundRoundingUmar2711Tests
{
    [Fact]
    public void Step_Rounding_Differs_From_Single_Fraction_And_Follows_The_Server_Rule()
    {
        // строка 3 × 47,14 со скидкой 1,00 (сумма 140,42), рядом строка 160,88, скидка на чек: итог 300,00 при сумме строк 301,30. Вернуть 1 шт. первой строки.
        var factor = 300m / 301.30m;
        // шаги: скидка на 1 шт = округл(1,00 × 1 / 3) = 0,33; база = округл(47,14 − 0,33) = 46,81; × 0,99569… = 46,608… → 46,61
        RefundMath.LineRefund(47.14m, 3, 140.42m, 1, factor).Should().Be(46.61m);
        // одна дробь без промежуточных округлений: 140,42 / 3 × 0,99569… = 46,604… → 46,60 — на копейку меньше сервера
        RefundMath.Round2(140.42m * (1m / 3m) * factor).Should().Be(46.60m);
    }

    [Fact]
    public void Half_Kopeck_Goes_Up_At_Every_Step()
    {
        // цена с тысячными (94,275): половина тыйына вверх — 94,28, а не «банковское» 94,28/94,27 по чётности
        RefundMath.LineRefund(94.275m, 2, 188.55m, 1, 1m).Should().Be(94.28m);
        // скидка строки 0,005 на 1 шт. (0,01 на 2 шт.): округл(0,01 × 1 / 2) = округл(0,005) = 0,01 вверх
        RefundMath.LineRefund(10.00m, 2, 19.99m, 1, 1m).Should().Be(9.99m, "10,00 − 0,01 = 9,99");
        // множитель чека: 33,333… × 0,5 = 16,67 (16,6666…)
        RefundMath.LineRefund(33.33m, 1, 33.33m, 1, 0.5m).Should().Be(16.67m, "16,665 → 16,67 HALF_UP");
    }

    [Fact]
    public void Unknown_Unit_Price_Uses_Line_Total_Proportion_With_Step_Rounding()
    {
        RefundMath.LineRefund(0m, 3, 100m, 1, 1m).Should().Be(33.33m);
        RefundMath.LineRefund(0m, 3, 100m, 2, 1m).Should().Be(66.67m);
        RefundMath.LineRefund(5m, 0, 100m, 1, 1m).Should().Be(0m, "количество строки не задано");
    }

    [Fact]
    public void Planner_Partial_Return_Matches_Server_Rule_And_Last_Return_Is_Exact_Remainder()
    {
        var sale = new OfflineSaleSnapshot { SaleRef = "S", Method = "cash", IsLocal = true, Total = 300m, LinesTotal = 301.30m, PaidCash = 300m };
        sale.Lines.Add(new OfflineSaleLine { Key = "0", Name = "Шнур", Quantity = 3, UnitPrice = 47.14m, LineTotal = 140.42m });
        sale.Lines.Add(new OfflineSaleLine { Key = "1", Name = "Розетка", Quantity = 1, UnitPrice = 160.88m, LineTotal = 160.88m });

        var first = OfflineRefundPlanner.Plan(sale, Array.Empty<OfflinePriorRefund>(), new[] { ("0", 1.0) }, clientDebtKnown: 0m);
        first.Ok.Should().BeTrue(first.Error);
        first.Amount.Should().Be(46.61m, "по правилу сервера: скидка строки, база, множитель чека — каждый шаг HALF_UP");
        first.Last.Should().BeFalse();

        var prior = new OfflinePriorRefund { Id = "r1", Amount = first.Amount, Cash = first.Cash, Lines = { ("0", "Шнур", 1.0) } };
        var rest = OfflineRefundPlanner.Plan(sale, new[] { prior }, new[] { ("0", 2.0), ("1", 1.0) }, clientDebtKnown: 0m);
        rest.Ok.Should().BeTrue(rest.Error);
        rest.Last.Should().BeTrue();
        (first.Amount + rest.Amount).Should().Be(300m, "последний возврат — ровно остаток оплаченного, копейки округления не теряются");
        rest.Lines.Sum(l => l.Amount).Should().Be(rest.Amount);
    }

    [Fact]
    public void Online_Partial_Quantity_Uses_The_Same_Exact_Rule()
    {
        var factor = 300m / 301.30m;
        var line = new ReturnSaleLineVm
        {
            LineId = "L1", Title = "Шнур", SubLine = "", LineSumText = "", Quantity = 3, OriginalQuantity = 3, CanReturn = true,
            RefundSum = RefundMath.LineRefund(47.14m, 3, 140.42m, 3, factor),
            RefundPerUnit = 140.42m / 3m * factor,
            ExactRefund = q => RefundMath.LineRefund(47.14m, 3, 140.42m, q, factor),
        };
        line.ReturnQuantity = 1;
        line.ReturnRefundSum.Should().Be(46.61m, "частичный возврат в окне — по тем же шагам, что у сервера");
        line.ReturnQuantity = 3;
        line.ReturnRefundSum.Should().Be(line.RefundSum, "вся строка — как есть");
    }
}
