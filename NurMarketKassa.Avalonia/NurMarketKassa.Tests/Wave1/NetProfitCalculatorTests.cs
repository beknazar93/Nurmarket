using System.Text.Json;
using CsCheck;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — описательные тесты NetProfitCalculator (единая функция чистой прибыли и маржи для «Прибыли и денег», «Сводки»,
/// «Окупаемости» и зарплаты от прибыли): валовая = выручка − закупочная; чистая = валовая − расходы или operating_profit сервера;
/// маржа = прибыль ÷ выручка × 100; разбор ответа GET api/main/analytics/pnl/.</summary>
public sealed class NetProfitCalculatorTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void GrossAndNet_AreSimpleDifferences()
    {
        NetProfitCalculator.Gross(1000m, 600m).Should().Be(400m);
        NetProfitCalculator.Net(400m, 150m).Should().Be(250m);
        NetProfitCalculator.Gross(100m, 150m).Should().Be(-50m, "убыток не обрезается нулём");
    }

    [Fact]
    public void Net_ServerOperatingProfit_WinsWhenPresent()
    {
        NetProfitCalculator.Net(serverOperatingProfit: 240m, gross: 400m, expenses: 150m).Should().Be(240m);
        NetProfitCalculator.Net(serverOperatingProfit: null, gross: 400m, expenses: 150m).Should().Be(250m);
        NetProfitCalculator.Net(serverOperatingProfit: 0m, gross: 400m, expenses: 150m).Should().Be(0m, "ноль сервера — тоже ответ сервера");
    }

    [Fact]
    public void MarginPercent_RevenueZeroOrNegative_IsNull()
    {
        NetProfitCalculator.MarginPercent(250m, 1000m).Should().Be(25m);
        NetProfitCalculator.MarginPercent(-100m, 1000m).Should().Be(-10m);
        NetProfitCalculator.MarginPercent(100m, 0m).Should().BeNull();
        NetProfitCalculator.MarginPercent(100m, -5m).Should().BeNull();
    }

    [Fact]
    public void AfterAmortization_SubtractsFromNet() =>
        NetProfitCalculator.AfterAmortization(250m, 40m).Should().Be(210m);

    [Fact]
    public void FromPnl_FullReport()
    {
        var pnl = NetProfitCalculator.FromPnl(Json("""
            {"revenue":"1000.00","cogs":600,"gross_profit":"400","opex":{"total":"150","salaries":"100","rent":"50"},"operating_profit":"250"}
            """));

        pnl.Should().NotBeNull();
        var p = pnl!.Value;
        p.Revenue.Should().Be(1000m);
        p.Cogs.Should().Be(600m);
        p.Gross.Should().Be(400m);
        p.Expenses.Should().Be(150m);
        p.Net.Should().Be(250m);
        p.NetMarginPercent.Should().Be(25m);
        p.GrossMarginPercent.Should().Be(40m);
        p.OpexParts.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["salaries"] = 100m, ["rent"] = 50m });
    }

    [Fact]
    public void FromPnl_NoGrossProfit_ComputedFromRevenueAndCogs_NoServerNet_ComputedFromExpenses()
    {
        var p = NetProfitCalculator.FromPnl(Json("{\"revenue\":1000,\"cogs\":600,\"opex\":{\"total\":150}}"))!.Value;

        p.Gross.Should().Be(400m);
        p.Net.Should().Be(250m);
    }

    [Fact]
    public void FromPnl_ServerNetOverridesComputed()
    {
        var p = NetProfitCalculator.FromPnl(Json("{\"revenue\":1000,\"cogs\":600,\"gross_profit\":400,\"opex\":{\"total\":150},\"operating_profit\":200}"))!.Value;

        p.Net.Should().Be(200m, "цифра сервера (operating_profit) важнее собственной разности");
        p.Expenses.Should().Be(150m);
    }

    [Fact]
    public void FromPnl_NoServerNetAndNoExpenses_IsNull()
    {
        NetProfitCalculator.FromPnl(Json("{\"revenue\":1000,\"cogs\":600,\"gross_profit\":400}")).Should().BeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("42")]
    public void FromPnl_NotAnObject_IsNull(string json) =>
        NetProfitCalculator.FromPnl(Json(json)).Should().BeNull();

    [Fact]
    public void FromPnl_ServerNetOnly_StillGivesNet()
    {
        NetProfitCalculator.FromPnl(Json("{\"operating_profit\":\"250\"}"))!.Value.Net.Should().Be(250m);
    }

    [Fact]
    public void FromPnl_BrokenNumbers_AreTreatedAsMissing()
    {
        // «abc» не число — как отсутствующее поле: gross берётся из выручки и закупки
        var p = NetProfitCalculator.FromPnl(Json("{\"revenue\":1000,\"cogs\":600,\"gross_profit\":\"abc\",\"opex\":{\"total\":150}}"))!.Value;
        p.Gross.Should().Be(400m);
    }

    // ───────────── BaseFor: от чего считается доля инвестора / зарплата ─────────────

    private static NetProfitCalculator.Pnl Sample() =>
        new(Revenue: 1000m, Cogs: 600m, Gross: 400m, Expenses: 150m, Net: 250m,
            OpexParts: new Dictionary<string, decimal> { ["salaries"] = 100m, ["rent"] = 50m });

    [Fact]
    public void BaseFor_Revenue_Gross_Net()
    {
        NetProfitCalculator.BaseFor(Sample(), "revenue").Should().Be(1000m);
        NetProfitCalculator.BaseFor(Sample(), "gross").Should().Be(400m);
        NetProfitCalculator.BaseFor(Sample(), "net").Should().Be(250m);
        NetProfitCalculator.BaseFor(Sample(), "что-то неизвестное").Should().Be(250m, "неизвестный вид — как чистая прибыль");
    }

    [Fact]
    public void BaseFor_Net_ExcludedOpexItems_AreNotSubtracted()
    {
        NetProfitCalculator.BaseFor(Sample(), "net", new[] { "salaries" }).Should().Be(350m);
        NetProfitCalculator.BaseFor(Sample(), "net", new[] { "salaries", "rent" }).Should().Be(400m);
        NetProfitCalculator.BaseFor(Sample(), "net", new[] { "taxes" }).Should().Be(250m, "статьи, которой нет в отчёте, не меняют результат");
        NetProfitCalculator.BaseFor(Sample(), "net", Array.Empty<string>()).Should().Be(250m);
    }

    [Fact]
    public void BaseFor_Net_NoOpexParts_ExclusionIsIgnored()
    {
        var pnl = new NetProfitCalculator.Pnl(1000m, 600m, 400m, 150m, 250m);
        NetProfitCalculator.BaseFor(pnl, "net", new[] { "salaries" }).Should().Be(250m);
    }

    [Fact]
    public void Property_NetEqualsGrossMinusExpenses_AndMarginSignFollowsProfit() =>
        Gen.Select(Gen.Int[0, 9_000_000], Gen.Int[0, 9_000_000], Gen.Int[0, 9_000_000]).Sample((rev, cogs, exp) =>
        {
            var gross = NetProfitCalculator.Gross(rev / 100m, cogs / 100m);
            var net = NetProfitCalculator.Net(gross, exp / 100m);
            net.Should().Be((rev - cogs - exp) / 100m);
            var margin = NetProfitCalculator.MarginPercent(net, rev / 100m);
            if (rev == 0)
                margin.Should().BeNull();
            else
                Math.Sign(margin!.Value).Should().Be(Math.Sign(net));
        }, iter: 1000);
}
