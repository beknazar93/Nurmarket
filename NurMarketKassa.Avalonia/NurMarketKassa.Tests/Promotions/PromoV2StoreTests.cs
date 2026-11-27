using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Promotions;

/// <summary>2026-11-27 (акции v2): набор акций кассы pos/promotions/active/ — ETag/If-None-Match (304 ничего не меняет), кэш на диске (касса считает без сети),
/// справочники категорий/брендов для scope. Запускать только через tools/qa/test-safe.ps1.</summary>
[Collection("UserPreferences")]
public sealed class PromoV2StoreTests : IDisposable
{
    private const string Active = """
        {"version":"2026-11-26T18:29:02Z","timezone":"Asia/Bishkek","promotions":[{"id":"p1","title":"2+1 вода","status":"active","kind":"buy_n_get_m",
         "params":{"buy":2,"get":1,"cheapest":true,"get_discount_percent":100},"scope":{"categories":["cat-1"]},"starts_at":null,"ends_at":null,"weekdays":[],
         "time_from":null,"time_to":null,"audience":"all","priority":10,"stackable_with_manual":false,"stackable_with_promos":false,"allow_below_cost":false,"branches":[],"limit_per_receipt":null,"version":1}]}
        """;

    private readonly FakeNurCrmServer _server = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), "promo-v2-test-" + Guid.NewGuid().ToString("N") + ".json");

    public PromoV2StoreTests()
    {
        PromoV2Store.FileOverride = _file;
        PromoV2Store.SetForTests(null);
    }

    public void Dispose()
    {
        PromoV2Store.FileOverride = null;
        PromoV2Store.SetForTests(null);
        _server.Dispose();
        try { File.Delete(_file); } catch (IOException) { }
    }

    [Fact]
    public async Task ETag_304_KeepsSet_And_DiskCacheWorksOffline()
    {
        // Без If-None-Match — 200 с ETag; с тем же ETag (без W/) — 304.
        _server.Server.Given(Request.Create().WithPath("/api/main/pos/promotions/active/").UsingGet().WithHeader("If-None-Match", "\"v1\""))
            .AtPriority(1).RespondWith(Response.Create().WithStatusCode(304).WithHeader("ETag", "\"v1\""));
        _server.Server.Given(Request.Create().WithPath("/api/main/pos/promotions/active/").UsingGet())
            .AtPriority(5).RespondWith(Response.Create().WithStatusCode(200).WithHeader("ETag", "W/\"v1\"").WithHeader("Content-Type", "application/json").WithBody(Active));
        _server.Json("GET", "/api/main/categories/", 200, """{"count":2,"next":null,"results":[{"id":"cat-1","name":"Напитки","parent":null},{"id":"cat-2","name":"Вода","parent":"cat-1"}]}""");
        _server.Json("GET", "/api/main/brands/", 200, """{"count":0,"next":null,"results":[]}""");

        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("owner@test.kg", "12345678");

        await PromoV2Store.RefreshAsync(api);
        PromoV2Store.Current.Rules.Should().ContainSingle(r => r.Id == "p1");
        PromoV2Store.Current.Categories.Should().ContainKey("cat-2").WhoseValue.Parent.Should().Be("cat-1");

        await PromoV2Store.RefreshAsync(api);
        var hits = _server.Server.LogEntries.Where(e => e.RequestMessage.Path == "/api/main/pos/promotions/active/").ToList();
        hits.Should().HaveCount(2);
        hits[1].RequestMessage.Headers!["If-None-Match"].Single().Should().Be("\"v1\"", "W/ снимается — иначе сервер NurCRM не отвечает 304");
        hits[1].ResponseMessage.StatusCode.Should().Be(304);
        PromoV2Store.Current.Rules.Should().ContainSingle("304 — набор прежний");

        // «Перезапуск без сети»: память сброшена — набор с диска.
        PromoV2Store.SetForTests(null);
        File.Exists(_file).Should().BeTrue();
        PromoV2Store.Current.Rules.Should().ContainSingle(r => r.Id == "p1");
        PromoV2Store.Current.Categories.Should().HaveCount(2);
    }
}
