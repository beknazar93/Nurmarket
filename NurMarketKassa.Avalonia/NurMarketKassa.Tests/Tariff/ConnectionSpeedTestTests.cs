using System.Net;
using System.Net.Http;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Tariff;

/// <summary>2026-11-08 (ТЗ п. 34, Б-51, ответ Умара 08.11): замер скорости связи — заглушка сервера WireMock, настоящий app.nurcrm.kg не трогаем.
/// Пути без «/» на конце, Bearer в каждом запросе, 429 + Retry-After — «повторите через N с» без повторных запросов.</summary>
[Collection("UserPreferences")]
public sealed class ConnectionSpeedTestTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();
    private readonly HttpClient _http = new();
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public ConnectionSpeedTestTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        _http.Dispose();
        _server.Dispose();
    }

    private ConnectionSpeedTest Make(bool signedIn = true) =>
        new(_http, new Uri(_server.Url + "/"), req =>
        {
            if (signedIn)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-access");
        });

    private void StubPing() => _server.Server.Given(Request.Create().WithPath("/api/speed/ping").UsingGet()).RespondWith(Response.Create().WithStatusCode(204));
    private void StubIp() => _server.Json("GET", "/api/speed/ip", 200, "{\"ip\":\"203.0.113.5\"}");
    private void StubDown(int bytes) => _server.Server.Given(Request.Create().WithPath("/api/speed/down").UsingGet())
        .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/octet-stream").WithBody(new byte[bytes]));
    private void StubUp(int received) => _server.Json("POST", "/api/speed/up", 200, "{\"received\":" + received + "}");

    private int Hits(string path) => _server.Server.LogEntries.Count(e => e.RequestMessage.Path == path);

    [Fact]
    public async Task FullRun_ReadsAllFour_PathsWithoutTrailingSlash_WithBearer()
    {
        StubPing(); StubIp(); StubDown(200_000); StubUp(100_000);

        var r = await Make().RunAsync(downBytes: 200_000, upBytes: 100_000);

        r.Status.Should().Be(SpeedTestStatus.Ok);
        r.Ip.Should().Be("203.0.113.5");
        r.LatencyMs.Should().NotBeNull();
        r.DownMbps.Should().BeGreaterThan(0);
        r.UpMbps.Should().BeGreaterThan(0);
        r.Verdict.Should().Be(SpeedVerdict.Good, "локальная заглушка быстрая");
        Hits("/api/speed/ping").Should().Be(4, "три замера + прогрев");
        Hits("/api/speed/ip").Should().Be(1);
        Hits("/api/speed/down").Should().Be(1);
        Hits("/api/speed/up").Should().Be(1);
        foreach (var e in _server.Server.LogEntries.Where(e => e.RequestMessage.Path.StartsWith("/api/speed/", StringComparison.Ordinal)))
        {
            e.RequestMessage.Path.Should().NotEndWith("/");
            e.RequestMessage.Headers!["Authorization"].Single().Should().StartWith("Bearer ");
        }
        _server.Server.LogEntries.Single(e => e.RequestMessage.Path == "/api/speed/down").RequestMessage.Query!["bytes"].Single().Should().Be("200000");
    }

    [Fact]
    public async Task DownloadThrottled_429_RetryAfterShown_PartialNumbersKept_NoSecondRequest()
    {
        StubPing(); StubIp();
        _server.Server.Given(Request.Create().WithPath("/api/speed/down").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "37").WithBody("{\"detail\":\"throttled\"}"));
        var test = Make();

        var r = await test.RunAsync();

        r.Status.Should().Be(SpeedTestStatus.RateLimited);
        r.RetryAfterSeconds.Should().Be(37);
        r.FailedStep.Should().Be("down");
        r.LatencyMs.Should().NotBeNull("задержка уже снята");
        r.Ip.Should().Be("203.0.113.5");
        r.DownMbps.Should().BeNull();
        SpeedTestText.Format(r).Should().Contain("через 37 с").And.Contain("Задержка:");
        Hits("/api/speed/up").Should().Be(0, "после 429 отдачу не запускаем");

        // Второй запуск сразу: сервер не долбим — отвечает сама касса.
        var again = await test.RunAsync();
        again.Status.Should().Be(SpeedTestStatus.RateLimited);
        again.RetryAfterSeconds.Should().BeInRange(30, 37);
        Hits("/api/speed/down").Should().Be(1);
        Hits("/api/speed/ping").Should().Be(4);
    }

    [Fact]
    public async Task UploadThrottled_429_KeepsDownload()
    {
        StubPing(); StubIp(); StubDown(50_000);
        _server.Server.Given(Request.Create().WithPath("/api/speed/up").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "12"));

        var r = await Make().RunAsync(downBytes: 50_000, upBytes: 10_000);

        r.Status.Should().Be(SpeedTestStatus.RateLimited);
        r.FailedStep.Should().Be("up");
        r.RetryAfterSeconds.Should().Be(12);
        r.DownMbps.Should().NotBeNull();
        r.UpMbps.Should().BeNull();
        r.Verdict.Should().Be(SpeedVerdict.Unknown, "без отдачи вывода «хватит» нет");
    }

    [Fact]
    public void RetryAfter_HttpDate_And_Missing_Header()
    {
        using var withDate = new HttpResponseMessage((HttpStatusCode)429);
        withDate.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(45));
        ConnectionSpeedTest.RetryAfterSeconds(withDate).Should().BeInRange(43, 46);
        using var none = new HttpResponseMessage((HttpStatusCode)429);
        ConnectionSpeedTest.RetryAfterSeconds(none).Should().Be(60, "заголовка нет — окно сервера для down/up (минута)");
    }

    [Fact]
    public async Task Unauthorized_401_And_NoToken_GiveNotSignedIn()
    {
        _server.Error("GET", "/api/speed/ping", 401);
        var r = await Make().RunAsync();
        r.Status.Should().Be(SpeedTestStatus.NotSignedIn);
        SpeedTestText.Format(r).Should().Contain("Войдите");

        var before = _server.Server.LogEntries.Count();
        var noToken = await Make(signedIn: false).RunAsync();
        noToken.Status.Should().Be(SpeedTestStatus.NotSignedIn);
        _server.Server.LogEntries.Count().Should().Be(before, "без входа запрос не уходит");
    }

    [Fact]
    public async Task OldServer_404_IsReadableFailure()
    {
        _server.Error("GET", "/api/speed/ping", 404);
        var r = await Make().RunAsync();
        r.Status.Should().Be(SpeedTestStatus.Failed);
        r.HttpStatus.Should().Be(404);
        SpeedTestText.Format(r).Should().Contain("ещё не умеет");
    }

    [Fact]
    public async Task NoConnection_IsFailed_NotException()
    {
        var test = new ConnectionSpeedTest(_http, new Uri("http://127.0.0.1:1/"), req => req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "t"));
        var r = await test.RunAsync();
        r.Status.Should().Be(SpeedTestStatus.Failed);
        r.FailedStep.Should().Be("ping");
        SpeedTestText.Format(r).Should().Contain("Нет связи");
    }

    [Theory]
    [InlineData(40, 20, 8, SpeedVerdict.Good)]
    [InlineData(900, 20, 8, SpeedVerdict.Weak)]
    [InlineData(40, 0.6, 8, SpeedVerdict.Weak)]
    [InlineData(40, 20, 0.2, SpeedVerdict.Weak)]
    public void Verdict_Thresholds(double latency, double down, double up, SpeedVerdict expected) =>
        new SpeedTestResult(SpeedTestStatus.Ok, latency, down, up).Verdict.Should().Be(expected);

    [Fact]
    public void Mbps_IsBitsPerSecond_AndOkText_HasAllLines()
    {
        ConnectionSpeedTest.Mbps(1_250_000, TimeSpan.FromSeconds(1)).Should().Be(10.0);
        var text = SpeedTestText.Format(new SpeedTestResult(SpeedTestStatus.Ok, 45, 23.4, 8.1, "1.2.3.4"));
        text.Should().Contain("Задержка: 45 мс").And.Contain("Скачивание: 23,4 Мбит/с").And.Contain("Отдача: 8,1 Мбит/с").And.Contain("Внешний IP: 1.2.3.4").And.Contain("хватит для кассы");
        SpeedTestText.Format(new SpeedTestResult(SpeedTestStatus.Ok, 45, 0.4, 8.1)).Should().Contain("связь слабая");
    }
}
