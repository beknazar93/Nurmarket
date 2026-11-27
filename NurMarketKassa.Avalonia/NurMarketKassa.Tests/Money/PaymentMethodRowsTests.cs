using FluentAssertions;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.Tests.Money;

// 2026-11-27 (живой проход на NBS): «Сводка» владельца показывала «В долг 65 · В долг 65», итог способов 478 при выручке 413 —
// сервер отдаёт «debt» второй строкой «продано в долг целиком» (= cards.credit_sales_total).
public class PaymentMethodRowsTests
{
    [Fact]
    public void TwoDebtRows_CreditSalesRowDropped_SumEqualsRevenue()
    {
        var rows = new List<(string Method, double Count, double Total)>
        {
            ("cash", 3, 263), ("transfer", 1, 85), ("debt", 1, 65), ("debt", 1, 65),
        };

        var result = PaymentMethodRows.DropCreditSalesDuplicate(rows, 65);

        result.Count(r => r.Method == "debt").Should().Be(1);
        result.Sum(r => r.Total).Should().Be(413);
    }

    [Fact]
    public void DifferentDebtRows_DropsOnlyTheCreditSalesOne()
    {
        var rows = new List<(string Method, double Count, double Total)>
        {
            ("cash", 55, 206718.98), ("debt", 8, 1095), ("offset", 2, 3), ("debt", 12, 7637),
        };

        var result = PaymentMethodRows.DropCreditSalesDuplicate(rows, 7637);

        result.Should().ContainSingle(r => r.Method == "debt").Which.Total.Should().Be(1095);
    }

    // «Продажи и финансы» → «Безнал»: 413 − 263 наличные − 65 погашенный наличными долг = 85 (было 150).
    [Fact]
    public void NonCash_ExcludesRepaidDebt()
    {
        PaymentMethodRows.NonCash(413m, 263m, 0m, debtRowsTotal: 130m, creditSalesTotal: 65m).Should().Be(85m);
        PaymentMethodRows.NonCash(213470.07m, 206718.98m, 0m, 8732m, 7637m).Should().Be(5656.09m);
        PaymentMethodRows.NonCash(413m, 263m, 0m, 65m, creditSalesTotal: null).Should().Be(150m, "старый сервер — как раньше");
    }

    [Fact]
    public void SingleDebtRow_IsKept()
    {
        var rows = new List<(string Method, double Count, double Total)> { ("cash", 1, 100), ("debt", 1, 65) };

        PaymentMethodRows.DropCreditSalesDuplicate(rows, 65).Should().HaveCount(2);
    }
}
