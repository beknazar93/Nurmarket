using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-10 (владелец: «и фильтр в возврате!», «поиск по товару!», «функцию возврата добавь в продажи»): общий фильтр списков чеков
/// (окно возврата и «Продажи») и поиск чеков по товару — чистая логика без окон.</summary>
public sealed class SaleListFilterTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 15, 0, 0);

    private static SaleListFilter.Row Row(int daysAgo = 0, decimal total = 100m, string? method = "cash", string? status = "paid", string? returnStatus = "none",
        string? cashier = "Айбек", string? client = null) =>
        new(Now.AddDays(-daysAgo).AddHours(-1), total, method, status, returnStatus, cashier, client);

    // Список с фото владельца: 3 200, 260, 3 540 (долг), 800, 200, 300, 2 150, 480.
    private static readonly List<SaleListFilter.Row> PhotoList =
    [
        Row(0, 3200m), Row(0, 260m, "card"), Row(1, 3540m, "debt", "debt", "none", "Нурлан", "Азамат"), Row(2, 800m, "qr"),
        Row(3, 200m, "cash", "partially_returned", "partial"), Row(8, 300m, "mixed"), Row(20, 2150m, "cash", "canceled", "full"), Row(40, 480m, "transfer", cashier: "Нурлан"),
    ];

    private static List<decimal> Totals(SaleListFilter f) => PhotoList.Where(r => f.Matches(r, Now)).Select(r => r.Total).ToList();

    [Fact]
    public void NoFilters_EverythingPasses_AndNothingActive()
    {
        var f = new SaleListFilter();
        Totals(f).Should().HaveCount(8);
        f.IsActive.Should().BeFalse();
        f.ActiveCount.Should().Be(0);
    }

    [Fact]
    public void Period_TodayWeekMonthDate()
    {
        var f = new SaleListFilter { Period = SaleListFilter.PeriodKind.Today };
        Totals(f).Should().BeEquivalentTo(new[] { 3200m, 260m });
        f.Period = SaleListFilter.PeriodKind.Week;
        Totals(f).Should().BeEquivalentTo(new[] { 3200m, 260m, 3540m, 800m, 200m });
        f.Period = SaleListFilter.PeriodKind.Month;
        Totals(f).Should().HaveCount(7, "40 дней назад — уже не месяц");
        f.Period = SaleListFilter.PeriodKind.Date;
        f.Date = Now.AddDays(-1);
        Totals(f).Should().Equal(3540m);
        f.PeriodRange(Now)!.Value.ToExclusive.Should().Be(Now.Date, "«до» не включает следующий день — как date_to сервера");
    }

    [Fact]
    public void Amount_FromTo_Inclusive()
    {
        var f = new SaleListFilter { AmountFrom = 300m, AmountTo = 3200m };
        Totals(f).Should().BeEquivalentTo(new[] { 3200m, 800m, 300m, 2150m, 480m });
        SaleListFilter.ParseAmount("1 500,50").Should().Be(1500.50m);
        SaleListFilter.ParseAmount("abc").Should().BeNull();
        SaleListFilter.ParseAmount("").Should().BeNull();
    }

    [Theory]
    [InlineData(SaleListFilter.PaymentKind.Cash, new[] { 3200, 200, 2150 })]
    [InlineData(SaleListFilter.PaymentKind.Card, new[] { 260, 480 })]
    [InlineData(SaleListFilter.PaymentKind.Qr, new[] { 800 })]
    [InlineData(SaleListFilter.PaymentKind.Debt, new[] { 3540 })]
    [InlineData(SaleListFilter.PaymentKind.Mixed, new[] { 300 })]
    public void Payment_EachMethod(SaleListFilter.PaymentKind kind, int[] expected)
    {
        Totals(new SaleListFilter { Payment = kind }).Select(t => (int)t).Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(SaleListFilter.StatusKind.Normal, new[] { 3200, 260, 800, 300, 480 })]
    [InlineData(SaleListFilter.StatusKind.Debt, new[] { 3540 })]
    [InlineData(SaleListFilter.StatusKind.PartlyReturned, new[] { 200 })]
    [InlineData(SaleListFilter.StatusKind.Returned, new[] { 2150 })]
    public void Status_Each(SaleListFilter.StatusKind kind, int[] expected)
    {
        Totals(new SaleListFilter { Status = kind }).Select(t => (int)t).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Cashier_Client_And_Combination_ThenReset()
    {
        var f = new SaleListFilter { Cashier = "нурлан" };
        Totals(f).Should().BeEquivalentTo(new[] { 3540m, 480m });
        f.Client = "азам";
        Totals(f).Should().Equal(3540m);
        f.Status = SaleListFilter.StatusKind.Debt;
        f.Period = SaleListFilter.PeriodKind.Week;
        f.AmountFrom = 1000m;
        Totals(f).Should().Equal(3540m);
        f.ActiveCount.Should().Be(5);
        f.Payment = SaleListFilter.PaymentKind.Cash;
        Totals(f).Should().BeEmpty("фильтры работают вместе: долговой чек не наличный");

        f.Product = "масло";
        f.Reset();
        f.IsActive.Should().BeFalse();
        f.Product.Should().BeNull();
        Totals(f).Should().HaveCount(8);
    }

    [Fact]
    public void ReturnState_ByReturnStatusOrStatus()
    {
        SaleListFilter.ReturnStateOf("paid", "partial").Should().Be(SaleListFilter.ReturnState.Partial);
        SaleListFilter.ReturnStateOf("partially_returned", null).Should().Be(SaleListFilter.ReturnState.Partial);
        SaleListFilter.ReturnStateOf("canceled", null).Should().Be(SaleListFilter.ReturnState.Full);
        SaleListFilter.ReturnStateOf("paid", "full").Should().Be(SaleListFilter.ReturnState.Full);
        SaleListFilter.ReturnStateOf("debt", "none").Should().Be(SaleListFilter.ReturnState.None);
        SaleListFilter.IsDebtSale(Row(method: "cash", status: "debt")).Should().BeTrue();
    }

    // ───────── поиск по товару ─────────

    private static readonly (string? Id, string? Name, IEnumerable<string?> Codes)[] Catalog =
    [
        ("p-oil", "S-OIL SEVEN ATF III", new string?[] { "8801234567890", "ATF-3", null, "4600000000011, 4600000000028" }),
        ("p-hub", "Ступица подшипник передний Ласетти WNQ", new string?[] { "2000000000015", "WNQ-550", "550", null }),
    ];

    [Fact]
    public void Product_ByNameWords_AnyOrder_CaseAndLayout()
    {
        var q = new SaleProductQuery("подшипник ласетти", Catalog);
        q.MatchesLine("Ступица подшипник передний Ласетти WNQ").Should().BeTrue();
        q.MatchesLine("Глушитель").Should().BeFalse();
        new SaleProductQuery("", Catalog).IsEmpty.Should().BeTrue();
        new SaleProductQuery("", Catalog).MatchesLine("Глушитель").Should().BeFalse();
    }

    [Theory]
    [InlineData("8801234567890")]
    [InlineData("atf-3")]
    [InlineData("4600000000028")]
    public void Product_ByBarcodeOrSku_FindsLineByIdOrName(string code)
    {
        var q = new SaleProductQuery(code, Catalog);
        q.MatchesLine("S-OIL SEVEN ATF III", "p-oil").Should().BeTrue("по id товара");
        q.MatchesLine("S-OIL SEVEN ATF III").Should().BeTrue("по названию товара, найденного по коду (строка без id)");
        q.MatchesText("Подшипник, S-OIL SEVEN ATF III ×4").Should().BeTrue("состав чека строкой («Продажи»)");
        q.MatchesLine("Подшипник", "p-x").Should().BeFalse();
    }

    [Fact]
    public void Product_FirstMatch_ForHighlight()
    {
        var q = new SaleProductQuery("wnq", Catalog);
        q.FirstMatch([("Подшипник", "p-1"), ("Ступица подшипник передний Ласетти WNQ", "p-hub"), ("Глушитель", null)])
            .Should().Be("Ступица подшипник передний Ласетти WNQ");
    }

    [Fact]
    public void Product_FastOn1000PlusReceipts()
    {
        var q = new SaleProductQuery("масло", Catalog);
        var receipts = Enumerable.Range(0, 3000).Select(i => new List<(string?, string?)> { ($"Товар {i}", $"p{i}"), (i % 100 == 0 ? "Масло подсолнечное" : "Хлеб", null) }).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = receipts.Count(lines => q.FirstMatch(lines) is not null);
        sw.Stop();
        found.Should().Be(30);
        sw.ElapsedMilliseconds.Should().BeLessThan(1500, "локальный поиск по 3000 чекам — мгновенно");
    }
}
