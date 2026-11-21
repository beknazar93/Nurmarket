using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;
using Xunit;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-21 («Базар»): курсы валют (GET / PUT main/currencies/, ответ сервера 20.11 В.19) и ступени цены с сервера (GET products/&lt;id&gt;/prices/ → volume_prices) на поддельном сервере.
/// Файлы курсов и ступеней — во временной папке.</summary>
[Collection("Wave5Database")]
public sealed class BazaarServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-bazaar-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeNurCrmServer _fake = new();
    private NurMarketApiClient? _api;

    public BazaarServerTests()
    {
        Directory.CreateDirectory(_dir);
        BazaarCurrencies.FileOverride = Path.Combine(_dir, "rates.json");
        BazaarCurrencies.ResetForTests();
        BazaarCurrencies.ApiOverride = () => _api;
        BazaarVolumePrices.FileOverride = Path.Combine(_dir, "volume.json");
        BazaarVolumePrices.ResetForTests();
        BazaarVolumePrices.ApiOverride = () => _api;
    }

    public void Dispose()
    {
        BazaarCurrencies.ApiOverride = null;
        BazaarCurrencies.FileOverride = null;
        BazaarCurrencies.ResetForTests();
        BazaarVolumePrices.ApiOverride = null;
        BazaarVolumePrices.FileOverride = null;
        BazaarVolumePrices.ResetForTests();
        _fake.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    [Fact]
    public async Task Rates_Refresh_ReadsServer_AndKeepsThemOnDiskForOffline()
    {
        _fake.Json("GET", "/api/main/currencies/", 200, """{"base":"KGS","rates":{"USD":"87.5","RUB":"0.95","CNY":"12.1"},"enabled":["USD","RUB","CNY"],"version":"5"}""");
        _api = await LoggedInAsync(_fake);

        var rates = await BazaarCurrencies.RefreshAsync();
        rates!.Rates["USD"].Should().Be(87.5m);
        BazaarCurrencies.Current.Version.Should().Be("5");

        BazaarCurrencies.ResetForTests(); // «перезапуск кассы»: курсы читаются с диска
        BazaarCurrencies.Current.Rates["CNY"].Should().Be(12.1m);
        BazaarCurrencies.Accepted(BazaarCurrencies.Current).Should().Equal("USD", "RUB", "CNY");
    }

    [Fact]
    public async Task Rates_NoConnection_LeavesThePreviousOnes()
    {
        _fake.Json("GET", "/api/main/currencies/", 200, """{"rates":{"USD":"87.5"},"version":"1"}""");
        _api = await LoggedInAsync(_fake);
        await BazaarCurrencies.RefreshAsync();
        _fake.Dispose();

        (await BazaarCurrencies.RefreshAsync()).Should().BeNull();
        BazaarCurrencies.Current.Rates["USD"].Should().Be(87.5m, "связи нет — работаем по последним полученным курсам");
    }

    [Fact]
    public async Task Rates_Save_SendsVersionInIfMatch_AndEmptyRateRemovesTheCurrency()
    {
        _fake.Json("GET", "/api/main/currencies/", 200, """{"rates":{"USD":"87.5","RUB":"0.95"},"version":"7"}""");
        _fake.Json("PUT", "/api/main/currencies/", 200, """{"rates":{"USD":"88","CNY":"12.1"},"version":"8"}""");
        _api = await LoggedInAsync(_fake);
        await BazaarCurrencies.RefreshAsync();

        var (rates, error) = await BazaarCurrencies.SaveAsync(new Dictionary<string, string> { ["USD"] = "88", ["RUB"] = "", ["CNY"] = "12,1", ["XXX"] = "5" });

        error.Should().BeNull();
        rates!.Rates.Should().NotContainKey("RUB");
        BazaarCurrencies.Current.Version.Should().Be("8");
        var put = Requests(_fake, "PUT", "/currencies/").Single();
        Header(put, "If-Match").Should().Be("7");
        var sent = BodyOf(put)["rates"]!.AsObject();
        sent["USD"]!.ToString().Should().Be("88");
        sent["RUB"]!.ToString().Should().BeEmpty("пустое значение — убрать валюту");
        sent["CNY"]!.ToString().Should().Be("12.1");
        sent.ContainsKey("XXX").Should().BeFalse("неизвестные валюты не шлём");
    }

    [Fact]
    public async Task Rates_Save_VersionConflict_AndPermissionDenied_AreExplained()
    {
        _fake.Json("GET", "/api/main/currencies/", 200, """{"rates":{"USD":"90"},"version":"9"}""");
        _fake.Json("PUT", "/api/main/currencies/", 409, """{"code":"version_conflict","version":"9"}""");
        _api = await LoggedInAsync(_fake);
        var (rates, conflict) = await BazaarCurrencies.SaveAsync(new Dictionary<string, string> { ["USD"] = "88" });
        rates.Should().BeNull();
        conflict.Should().Contain("другой кассе");
        BazaarCurrencies.Current.Rates["USD"].Should().Be(90m, "после конфликта показаны свежие курсы");

        _fake.Json("PUT", "/api/main/currencies/", 403, """{"code":"permission_denied"}""");
        var (_, denied) = await BazaarCurrencies.SaveAsync(new Dictionary<string, string> { ["USD"] = "88" });
        denied.Should().Contain("владелец");
    }

    [Fact]
    public async Task Volume_LoadServer_TakesTiersWithoutLevel_AndRemembersEmptyOnes()
    {
        _fake.Json("GET", "/api/main/products/p1/prices/", 200, """{"prices":[{"level":"retail","price":"100"}],"volume_prices":[{"min_qty":"10","price":"80","level":null},{"min_qty":"3","price":"90","level":null},{"min_qty":"50","price":"60","level":"foreman"}]}""");
        _fake.Json("GET", "/api/main/products/p2/prices/", 200, """{"prices":[],"volume_prices":[]}""");
        _api = await LoggedInAsync(_fake);

        var need = BazaarVolumePrices.NeedServer(new[] { "p1", "p2", "p1" });
        need.Should().Equal("p1", "p2");
        BazaarVolumePrices.NeedServer(new[] { "p1" }).Should().BeEmpty("уже грузится — второй раз не спрашиваем");

        (await BazaarVolumePrices.LoadServerAsync(need)).Should().BeTrue();

        BazaarVolumePrices.ServerTiers("p1").Should().Equal(new VolumeTier(3m, 90m), new VolumeTier(10m, 80m));
        BazaarVolumePrices.ServerTiers("p2").Should().BeEmpty();
        BazaarVolumePrices.NeedServer(new[] { "p1", "p2" }).Should().BeEmpty("ответ сервера запомнен на 10 минут");
        BazaarVolumePrices.Effective("p1").Select(t => t.Price).Should().Equal(90m, 80m);
    }

    [Fact]
    public void Volume_ServerTiers_WinOverLocalOnes_AndLocalOnesSurviveARestart()
    {
        BazaarVolumePrices.SetLocalTiers("p1", new[] { new VolumeTier(5m, 95m) }, 100m).Should().BeTrue();
        BazaarVolumePrices.ResetForTests(); // «перезапуск»: местные ступени читаются с диска
        BazaarVolumePrices.LocalTiers("p1").Should().Equal(new VolumeTier(5m, 95m));
        BazaarVolumePrices.Effective("p1").Should().Equal(new VolumeTier(5m, 95m));

        BazaarVolumePrices.RememberServer("p1", new[] { new VolumeTier(10m, 80m) });
        BazaarVolumePrices.Effective("p1").Should().Equal(new VolumeTier(10m, 80m));

        BazaarVolumePrices.SetLocalTiers("p1", new[] { new VolumeTier(2m, 120m) }, 100m).Should().BeFalse("ступень дороже розницы не принимается");
        BazaarVolumePrices.SetLocalTiers("p1", Array.Empty<VolumeTier>()).Should().BeTrue("пустой набор убирает ступени");
        BazaarVolumePrices.LocalTiers("p1").Should().BeEmpty();
    }

    [Fact]
    public async Task Volume_NoLogin_NeverBlocksOrThrows()
    {
        _api = null;
        var need = BazaarVolumePrices.NeedServer(new[] { "p9" });
        (await BazaarVolumePrices.LoadServerAsync(need)).Should().BeFalse();
        BazaarVolumePrices.ServerTiers("p9").Should().BeEmpty();
        BazaarVolumePrices.NeedServer(new[] { "p9" }).Should().BeEmpty();
    }
}
