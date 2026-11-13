using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-12 (ХМ15): общие настройки сфер и отметки «прораб» — разбор пустого, битого и слишком большого JSON даёт умолчания без исключений.</summary>
[Collection("ServerFeatures")]
public sealed class SphereSettingsTests : IDisposable
{
    private readonly string? _savedSettings = UserPreferences.Instance.SphereSettingsJson;
    private readonly string? _savedGroups = UserPreferences.Instance.ClientPriceGroupsJson;

    public void Dispose()
    {
        UserPreferences.Instance.SphereSettingsJson = _savedSettings;
        UserPreferences.Instance.ClientPriceGroupsJson = _savedGroups;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public void Parse_EmptyOrBroken_GivesEmptyObject(string? json)
    {
        SphereSettings.Parse(json).Count.Should().Be(0);
        SphereSettings.GetHardwareSize(json ?? "").Should().Be(HardwareSize.Shop);
        SphereSettings.GetHardwarePackScan(json ?? "").Should().BeFalse();
    }

    [Fact]
    public void Parse_Oversize_GivesDefaults()
    {
        var big = "{\"hardware\":{\"size\":\"market\",\"packScan\":true,\"pad\":\"" + new string('x', SphereSettings.MaxJsonBytes) + "\"}}";
        SphereSettings.Parse(big).Count.Should().Be(0);
        SphereSettings.GetHardwareSize(big).Should().Be(HardwareSize.Shop);
        SphereSettings.GetHardwarePackScan(big).Should().BeFalse();
    }

    [Fact]
    public void HardwareSection_ReadsSizeAndPackScan()
    {
        var json = "{\"hardware\":{\"size\":\"market\",\"packScan\":true}}";
        SphereSettings.GetHardwareSize(json).Should().Be(HardwareSize.Market);
        SphereSettings.GetHardwarePackScan(json).Should().BeTrue();
        SphereSettings.GetHardwareSize("{\"hardware\":{\"size\":\"???\"}}").Should().Be(HardwareSize.Shop);
    }

    [Fact]
    public void WithSection_KeepsOtherSections_AndRejectsOversize()
    {
        var json = "{\"electronics\":{\"a\":1}}";
        var next = SphereSettings.WithSection(json, "hardware", new System.Text.Json.Nodes.JsonObject { ["size"] = "market" });
        next.Should().NotBeNull();
        next!.Should().Contain("electronics").And.Contain("market");
        SphereSettings.WithSection(json, "hardware", new System.Text.Json.Nodes.JsonObject { ["pad"] = new string('x', SphereSettings.MaxJsonBytes) })
            .Should().BeNull("слишком большое значение не записывается");
    }

    [Fact]
    public void SetHardwareChoice_WritesAndReadsBack_NullIsNotSentToCloudWhenUntouched()
    {
        UserPreferences.Instance.SphereSettingsJson = null;
        SphereSettings.GetHardwareSize().Should().Be(HardwareSize.Shop);
        SphereSettings.SetHardwareChoice(HardwareSize.Market, packScan: true).Should().BeTrue();
        SphereSettings.GetHardwareSize().Should().Be(HardwareSize.Market);
        SphereSettings.GetHardwarePackScan().Should().BeTrue();
        SphereSettings.HardwareFeatures.ForemanPrice.Should().BeTrue("строительный маркет — цена прораба включена");
        SphereSettings.SetHardwareChoice(HardwareSize.Shop).Should().BeTrue();
        SphereSettings.GetHardwarePackScan().Should().BeTrue("галочку не трогали");
        SphereSettings.HardwareFeatures.ForemanPrice.Should().BeFalse();
    }

    [Fact]
    public void ClientPriceGroups_RoundTrip_AndBrokenJson()
    {
        ClientPriceGroups.Parse(null).Should().BeEmpty();
        ClientPriceGroups.Parse("{}").Should().BeEmpty();
        ClientPriceGroups.Parse("[1, null, \"a\", \"\"]").Should().BeEquivalentTo(new[] { "a" });
        ClientPriceGroups.Parse("[" + string.Join(",", Enumerable.Range(0, 5000).Select(i => "\"client-" + i + "\"")) + "]")
            .Should().BeEmpty("больше 16 КБ — умолчания");

        UserPreferences.Instance.ClientPriceGroupsJson = null;
        ClientPriceGroups.IsForeman("c1").Should().BeFalse();
        ClientPriceGroups.SetForeman("c1", true).Should().BeTrue();
        ClientPriceGroups.IsForeman("c1").Should().BeTrue();
        ClientPriceGroups.IsForeman("c2").Should().BeFalse();
        ClientPriceGroups.SetForeman("c1", false).Should().BeTrue();
        ClientPriceGroups.IsForeman("c1").Should().BeFalse();
        ClientPriceGroups.SetForeman(null, true).Should().BeFalse();
    }

    [Fact]
    public void CalcNorms_FromCompanySettings_SaveAndReset()
    {
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(null);
        UserPreferences.Instance.SphereSettingsJson = null;
        var def = NurMarketKassa.Services.Hardware.HardwareCalcSettings.Current;
        def.Norms.Single(n => n.Key == "paint").PerM2.Should().Be(0.12m, "типовая норма, пока владелец не менял");

        var custom = NurMarketKassa.Services.Hardware.HardwareCalcSettings.CreateDefault();
        custom.Norms.Single(n => n.Key == "paint").PerM2 = 0.2m;
        custom.ReservePercent = 12m;
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.TrySaveToCompany(custom).Should().BeTrue();
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Current.Norms.Single(n => n.Key == "paint").PerM2.Should().Be(0.2m);
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Current.ReservePercent.Should().Be(12m);
        SphereSettings.GetHardwareSize().Should().Be(HardwareSize.Shop, "соседние разделы не затронуты");

        // битый раздел → типовые, без исключения
        UserPreferences.Instance.SphereSettingsJson = "{\"calc\":{\"norms\":\"плохо\"}}";
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Current.Norms.Should().NotBeEmpty();

        UserPreferences.Instance.SphereSettingsJson = null;
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.TrySaveToCompany(custom).Should().BeTrue();
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.ResetCompany().Should().BeTrue();
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Current.Norms.Single(n => n.Key == "paint").PerM2.Should().Be(0.12m);
    }
}
