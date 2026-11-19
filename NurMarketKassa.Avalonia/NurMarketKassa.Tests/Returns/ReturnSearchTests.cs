using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-13 (Л-5 «Возвраты»): единое поле поиска чека для возврата (номер, сумма, дата, клиент, телефон, карта) и отчёт «Возвраты по причинам и кассирам» — чистая логика.</summary>
public sealed class ReturnSearchTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 15, 0, 0);

    private static readonly ReturnSearchRow[] Rows =
    [
        new("id-1", "2101", 1, 3200m, new DateTime(2026, 11, 13, 10, 0, 0), null),
        new("id-2", "2102", 2, 260m, new DateTime(2026, 11, 12, 10, 0, 0), "Азамат Усупов", "+996 700 123 456"),
        new("id-3", "2103", 3, 3540m, new DateTime(2026, 11, 12, 18, 0, 0), "Бектур", "0555 99 88 77", "4417"),
        new("id-4", null, 4, 800m, new DateTime(2026, 5, 1, 9, 0, 0), null),
    ];

    private static List<string> Find(string text) => Rows.Where(r => new ReturnSearchQuery(text, Now).Matches(r)).Select(r => r.SaleId).ToList();

    [Theory]
    [InlineData("2101", "id-1")]
    [InlineData("№2102", "id-2")]
    [InlineData("#2103", "id-3")]
    [InlineData("002101", "id-1")]
    public void Number_FindsReceipt_WithSignsAndZeros(string text, string expected) => Find(text).Should().Contain(expected);

    [Fact]
    public void Position_FindsReceiptWithoutServerNumber() => Find("4").Should().Contain("id-4");

    [Theory]
    [InlineData("3200", "id-1")]
    [InlineData("3 200", "id-1")]
    [InlineData("260", "id-2")]
    [InlineData("3540,00", "id-3")]
    public void Amount_FindsByTotal(string text, string expected) => Find(text).Should().Contain(expected);

    [Fact]
    public void Amount_DoesNotMatchNeighbours() => Find("3201").Should().NotContain("id-1");

    [Theory]
    [InlineData("12.11", 2)]
    [InlineData("12.11.26", 2)]
    [InlineData("12.11.2026", 2)]
    [InlineData("2026-11-13", 1)]
    [InlineData("1.5", 1)] // 1 мая — прошлая дата этого года
    public void Date_FindsByDay(string text, int count) => Find(text).Should().HaveCount(count);

    [Fact]
    public void Date_ShortFutureDateMeansLastYear()
    {
        ReturnSearchQuery.ParseDay("14.11", Now).Should().Be(new DateTime(2025, 11, 14));
        ReturnSearchQuery.ParseDay("13.11", Now).Should().Be(new DateTime(2026, 11, 13));
        ReturnSearchQuery.ParseDay("абв", Now).Should().BeNull();
    }

    [Theory]
    [InlineData("3456", "id-2")]       // последние 4 цифры телефона +996 700 123 456
    [InlineData("8877", "id-3")]
    [InlineData("0555998877", "id-3")]
    [InlineData("4417", "id-3")]       // последние 4 цифры карты
    public void PhoneOrCard_FindsByLastDigits(string text, string expected) => Find(text).Should().Contain(expected);

    [Fact]
    public void PhoneDigits_AreNotMatchedInTheMiddle() => Find("1234").Should().NotContain("id-2");

    [Fact]
    public void ThreeDigits_AreNotPhoneSearch() => Find("456").Should().NotContain("id-2");

    [Theory]
    [InlineData("азамат", "id-2")]
    [InlineData("УСУПОВ азамат", "id-2")]
    [InlineData("бек", "id-3")]
    public void Client_FindsByNameWords(string text, string expected) => Find(text).Should().Contain(expected);

    [Fact]
    public void Empty_MatchesEverything() => Find("  ").Should().HaveCount(4);

    [Fact]
    public void LooksLikeNumbers_OnlyWithoutLetters()
    {
        new ReturnSearchQuery("3200", Now).LooksLikeNumbers.Should().BeTrue();
        new ReturnSearchQuery("12.11", Now).LooksLikeNumbers.Should().BeTrue();
        new ReturnSearchQuery("+996 700 123", Now).LooksLikeNumbers.Should().BeTrue();
        new ReturnSearchQuery("хомут 200", Now).LooksLikeNumbers.Should().BeFalse();
        new ReturnSearchQuery("", Now).LooksLikeNumbers.Should().BeFalse();
    }

    // ----- отчёт «Возвраты по причинам и кассирам»

    [Fact]
    public void Report_GroupsReasonsIgnoringCaseAndSpaces_SortedBySum()
    {
        var (byReason, byCashier) = ReturnReasonReport.Build(
        [
            new("Брак", "Айбек", 500m), new(" брак  ", "Нурлан", 300m), new("Передумал", "Айбек", 1200m), new(null, "Айбек", 100m), new("", null, 50m),
        ], "без причины", "не указан");
        byReason.Select(g => (g.Name, g.Count, g.Sum)).Should().Equal(("Передумал", 1, 1200m), ("Брак", 2, 800m), ("без причины", 2, 150m));
        byCashier.Select(g => (g.Name, g.Count, g.Sum)).Should().Equal(("Айбек", 3, 1800m), ("Нурлан", 1, 300m), ("не указан", 1, 50m));
    }

    [Fact]
    public void Report_UsesFirstLineOfReason_AndTrimsLongOnes()
    {
        ReturnReasonReport.Clean("Брак\nподробности", "—").Should().Be("Брак");
        ReturnReasonReport.Clean(new string('а', 80), "—").Should().HaveLength(61).And.EndWith("…");
        ReturnReasonReport.Clean("   ", "—").Should().Be("—");
    }

    [Fact]
    public void Report_SumsAbsoluteAmounts()
    {
        var (byReason, _) = ReturnReasonReport.Build([new("Брак", "А", -200m), new("Брак", "А", 100m)], "x", "y");
        byReason.Single().Sum.Should().Be(300m);
    }
}
