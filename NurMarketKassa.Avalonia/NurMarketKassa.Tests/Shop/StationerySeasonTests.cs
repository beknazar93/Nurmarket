using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ4): сезон «к школе» в сводке для «Нур Советника» — только в сфере «Канцтовары».</summary>
[Collection("UserPreferences")]
public sealed class StationerySeasonTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;

    public void Dispose()
    {
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
    }

    [Theory]
    [InlineData(7, "school")]
    [InlineData(8, "school")]
    [InlineData(9, "school")]
    [InlineData(5, "prepare")]
    [InlineData(6, "prepare")]
    [InlineData(1, "second-half")]
    [InlineData(2, "regular")]
    [InlineData(11, "regular")]
    public void Season_by_month(int month, string expected) => StationerySeason.SeasonCode(month).Should().Be(expected);

    [Fact]
    public void July_text_names_the_sphere_the_month_and_school_assortment()
    {
        var text = StationerySeason.Text(new DateTime(2027, 7, 15));
        text.Should().Contain("stationery").And.Contain("месяц 7");
        text.Should().Contain("тетради").And.Contain("рюкзаки").And.Contain("К ШКОЛЕ");
    }

    [Theory]
    [InlineData("grocery", null, false)]
    [InlineData("clothing", null, false)]
    [InlineData("services", null, false)]
    [InlineData("grocery", "hardware", false)]
    [InlineData("grocery", "stationery", true)]
    public void Section_only_for_stationery(string sphere, string? detail, bool expected)
    {
        UserPreferences.Instance.MarketSphere = sphere;
        UserPreferences.Instance.MarketSphereDetail = detail;
        var section = StationerySeason.SectionFor(new DateTime(2027, 8, 1));
        (section is not null).Should().Be(expected);
        if (section is not null)
            section.Full.Should().Contain("stationery").And.Contain("месяц 8");
    }
}
