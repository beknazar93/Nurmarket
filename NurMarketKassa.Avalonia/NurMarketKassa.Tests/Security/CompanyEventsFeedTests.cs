using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Security;

/// <summary>2026-11-13 (ответ Умара: общая лента компании GET api/main/changes/): первый запрос без since запоминает курсор, пагинация has_more, 400 cursor_expired — всё заново,
/// ETag → 304 без реакций, события нужных типов будят AccessSync / ленту товаров, чужие типы игнорируются, ошибки ленты не бросают. Сервер — WireMock.</summary>
[Collection("CloudSyncSerial")]
public sealed class CompanyEventsFeedTests : IDisposable
{
    private const string Path = "/api/main/changes/";
    private readonly FakeNurCrmServer _server = new();
    private readonly CompanyDto? _previousCompany = CompanyInfoService.LastCompany;
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nm-ev-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _reacted = new();
    private DateTime _now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private NurMarketApiClient? _api;

    public CompanyEventsFeedTests()
    {
        Directory.CreateDirectory(_dir);
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, new CompanyDto { Id = "c-test" });
        CompanyEventsFeed.ResetForTests();
        CompanyEventsFeed.DirOverride = _dir;
        CompanyEventsFeed.NowUtcOverride = () => _now;
        CompanyEventsFeed.ApiProvider = () => _api;
        AccessSync.ResetForTests();
    }

    public void Dispose()
    {
        CompanyEventsFeed.ResetForTests();
        CompanyEventsFeed.DirOverride = null;
        CompanyEventsFeed.NowUtcOverride = null;
        AccessSync.ResetForTests();
        AccessSync.MarkStartedForTests(false);
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, _previousCompany);
        _server.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task LoginAsync()
    {
        _api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await _api.LoginAsync("cashier@test.kg", "12345678");
        CompanyEventsFeed.ReactOverride = kind => _reacted.Add(kind);
    }

    private void Page(string since, string body, int status = 200, string? etag = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
        if (etag is not null)
            response = response.WithHeader("ETag", "W/\"" + etag + "\"");
        _server.Server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("since", since)).AtPriority(1).RespondWith(response);
    }

    private void First(string cursor) =>
        _server.Server.Given(Request.Create().WithPath(Path).UsingGet()).AtPriority(9).RespondWith(
            Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"cursor\":\"" + cursor + "\",\"has_more\":false,\"events\":[]}"));

    private static string Ev(string type) => "{\"id\":1,\"type\":\"" + type + "\",\"event\":\"x\",\"at\":\"2026-11-13T12:00:00Z\",\"branch\":null,\"actor\":null,\"entity\":\"e\",\"entity_id\":\"1\",\"data\":{}}";

    private List<string> Urls() => _server.Server.LogEntries.Where(e => e.RequestMessage?.Path == Path).Select(e => e.RequestMessage!.Url).ToList();

    [Fact]
    public async Task First_request_has_no_since_asks_only_needed_types_and_remembers_the_cursor()
    {
        First("c1");
        await LoginAsync();

        var r = await CompanyEventsFeed.TickAsync();

        r.Requests.Should().Be(1);
        r.Reacted.Should().BeEmpty();
        var url = Urls().Single();
        url.Should().NotContain("since=");
        url.Should().Contain("types=").And.Contain("features_changed").And.Contain("staff_changed").And.Contain("app_settings_changed")
            .And.Contain("product_changed").And.Contain("stock_changed");
        url.Should().NotContain("cash_moved", "лишние типы касса не просит");
        CompanyEventsFeed.CursorForTests.Should().Be("c1");
        File.ReadAllText(System.IO.Path.Combine(_dir, "company-events-cursor.json")).Should().Contain("c1");
    }

    [Fact]
    public async Task Next_request_uses_since_and_reacts_only_to_known_types()
    {
        First("c1");
        await LoginAsync();
        await CompanyEventsFeed.TickAsync();
        Page("c1", "{\"cursor\":\"c2\",\"has_more\":false,\"events\":[" + Ev("features_changed") + "," + Ev("product_changed") + "," + Ev("sale_paid") + "]}");

        var r = await CompanyEventsFeed.TickAsync();

        r.Events.Should().Be(3);
        _reacted.Should().BeEquivalentTo("features_changed", "product_changed");
        CompanyEventsFeed.CursorForTests.Should().Be("c2");
    }

    [Fact]
    public async Task Has_more_reads_next_page_in_the_same_tick()
    {
        First("c1");
        await LoginAsync();
        await CompanyEventsFeed.TickAsync();
        Page("c1", "{\"cursor\":\"c2\",\"has_more\":true,\"events\":[" + Ev("staff_changed") + "]}");
        Page("c2", "{\"cursor\":\"c3\",\"has_more\":false,\"events\":[" + Ev("stock_changed") + "]}");

        var r = await CompanyEventsFeed.TickAsync();

        r.Requests.Should().Be(2);
        _reacted.Should().BeEquivalentTo("staff_changed", "stock_changed");
        CompanyEventsFeed.CursorForTests.Should().Be("c3");
    }

    [Fact]
    public async Task Cursor_expired_starts_over_and_rereads_everything_affected()
    {
        First("c1");
        await LoginAsync();
        await CompanyEventsFeed.TickAsync();
        Page("c1", "{\"code\":\"cursor_expired\",\"detail\":\"cursor expired\"}", status: 400);

        var r = await CompanyEventsFeed.TickAsync();

        r.Reset.Should().BeTrue();
        _reacted.Should().BeEquivalentTo(CompanyEventsFeed.Types, "затронутое перечитывается целиком");
        CompanyEventsFeed.CursorForTests.Should().BeNull();

        _server.Server.ResetMappings();
        First("c9");
        await CompanyEventsFeed.TickAsync();
        Urls().Last().Should().NotContain("since=", "после сброса — снова без since");
        CompanyEventsFeed.CursorForTests.Should().Be("c9");
    }

    [Fact]
    public async Task Invalid_cursor_is_handled_like_expired()
    {
        First("c1");
        await LoginAsync();
        await CompanyEventsFeed.TickAsync();
        Page("c1", "{\"code\":\"invalid_cursor\"}", status: 400);

        (await CompanyEventsFeed.TickAsync()).Reset.Should().BeTrue();
        CompanyEventsFeed.CursorForTests.Should().BeNull();
    }

    [Fact]
    public async Task Etag_gives_304_without_reactions()
    {
        First("c1");
        await LoginAsync();
        await CompanyEventsFeed.TickAsync();
        // пустая страница с ETag: курсор остаётся на месте (иначе ETag не совпал бы), следующий опрос идёт с If-None-Match и получает 304
        Page("c1", "{\"cursor\":\"c1b\",\"has_more\":false,\"events\":[]}", etag: "t1");
        await CompanyEventsFeed.TickAsync();
        CompanyEventsFeed.CursorForTests.Should().Be("c1");

        _server.Server.Given(Request.Create().WithPath(Path).UsingGet().WithParam("since", "c1").WithHeader("If-None-Match", "\"t1\"")).AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(304).WithHeader("ETag", "W/\"t1\""));
        var r = await CompanyEventsFeed.TickAsync();

        r.Note.Should().Be("304");
        r.Events.Should().Be(0);
        _reacted.Should().BeEmpty();
        _server.Server.LogEntries.Last(e => e.RequestMessage?.Path == Path).RequestMessage!.Headers!.Should().ContainKey("If-None-Match");
    }

    [Fact]
    public async Task Features_and_staff_events_kick_AccessSync_and_product_events_kick_the_product_feed()
    {
        First("c1");
        await LoginAsync();
        CompanyEventsFeed.ReactOverride = null; // настоящие реакции
        AccessSync.MarkStartedForTests(true);
        await CompanyEventsFeed.TickAsync();
        AccessSync.TakeWakeForTests().Should().BeFalse("пока событий нет — проверку не будим");

        Page("c1", "{\"cursor\":\"c2\",\"has_more\":false,\"events\":[" + Ev("features_changed") + "]}");
        await CompanyEventsFeed.TickAsync();
        AccessSync.TakeWakeForTests().Should().BeTrue("features_changed — права/функции/тариф перечитываются сразу");

        Page("c2", "{\"cursor\":\"c3\",\"has_more\":false,\"events\":[" + Ev("staff_changed") + "]}");
        await CompanyEventsFeed.TickAsync();
        AccessSync.TakeWakeForTests().Should().BeTrue("staff_changed — тоже");

        while (SyncCenter.FeedKick.WaitAsync(TimeSpan.Zero).GetAwaiter().GetResult()) { }
        Page("c3", "{\"cursor\":\"c4\",\"has_more\":false,\"events\":[" + Ev("stock_changed") + "]}");
        await CompanyEventsFeed.TickAsync();
        SyncCenter.FeedKick.WaitAsync(TimeSpan.Zero).GetAwaiter().GetResult().Should().BeTrue("stock_changed будит ленту товаров products/changes/");
    }

    [Fact]
    public async Task Server_without_feed_or_with_error_does_not_throw_and_pauses()
    {
        _server.Error("GET", Path, 404);
        await LoginAsync();

        var first = await CompanyEventsFeed.TickAsync();
        first.Note.Should().Be("не поддерживается");
        var requests = Urls().Count;

        var second = await CompanyEventsFeed.TickAsync();
        second.Note.Should().Be("пауза");
        Urls().Count.Should().Be(requests, "во время паузы запросов нет");

        _now = _now.AddMinutes(11);
        _server.Server.ResetMappings();
        _server.Error("GET", Path, 500);
        var third = await CompanyEventsFeed.TickAsync();
        third.Note.Should().Be("ошибка", "500 — пауза 60 с, исключения нет");
        (await CompanyEventsFeed.TickAsync()).Note.Should().Be("пауза");
    }
}
