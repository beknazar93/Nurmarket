using System.Diagnostics;
using System.Net.Http;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-05 (аудит скорости, шаг 2, п. 12: Н-12, Н-13, Н-14, Н-19): внешние сервисы против ПОДДЕЛЬНОГО медленного сервера (WireMock на localhost).
/// Настоящие Open Food Facts, DuckDuckGo, Telegram, OpenRouter и Groq не вызываются: клиенты кассы подменены на клиентов с обработчиком,
/// который переписывает адрес на WireMock. Ключей и данных боевой кассы не трогаем (NURMARKET_DATA_ROOT выставляет test-safe.ps1).</summary>
[Collection("ExternalHttpStatics")]
public sealed class ExternalServicesTimeoutTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _oldResearch = ProductInfoResearch.Http;
    private readonly HttpClient _oldTelegram = TelegramBotService.Http;
    private readonly HttpClient _oldAi = AiProviders.Http;
    private readonly TimeSpan _oldOne = AiProviders.FallbackOneTimeout;
    private readonly TimeSpan _oldTotal = AiProviders.FallbackTotalTimeout;
    private readonly string? _oldGroq = UserPreferences.Instance.GroqApiKey;
    private readonly string? _oldRouter = UserPreferences.Instance.OpenRouterApiKey;

    public void Dispose()
    {
        ProductInfoResearch.Http = _oldResearch;
        TelegramBotService.Http = _oldTelegram;
        AiProviders.Http = _oldAi;
        AiProviders.FallbackOneTimeout = _oldOne;
        AiProviders.FallbackTotalTimeout = _oldTotal;
        UserPreferences.Instance.GroqApiKey = _oldGroq;
        UserPreferences.Instance.OpenRouterApiKey = _oldRouter;
        _server.Dispose();
    }

    /// <summary>Любой https://внешний-хост/путь уходит на WireMock с тем же путём и запросом.</summary>
    private sealed class RewriteToServer : DelegatingHandler
    {
        private readonly Uri _target;
        public RewriteToServer(string target) : base(new HttpClientHandler()) => _target = new Uri(target);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new UriBuilder(request.RequestUri!) { Scheme = _target.Scheme, Host = _target.Host, Port = _target.Port }.Uri;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private HttpClient FakeClient(TimeSpan timeout) => new(new RewriteToServer(_server.Url!)) { Timeout = timeout };

    private void Stub(string method, string pathPattern, int status, string body, TimeSpan? delay = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
        if (delay is { } d)
            response = response.WithDelay(d);
        _server.Given(Request.Create().WithPath(pathPattern).UsingMethod(method)).RespondWith(response);
    }

    /// <summary>WireMock пишет запрос в журнал, когда отдаст (задержанный) ответ, — ждём, пока все задержки отыграют, и считаем запросы к пути.</summary>
    private async Task<int> SettledRequestCount(string path)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        // 2026-11-05: на загруженном ПК (параллельные нагрузочные тесты) журнал WireMock заполняется позже — ждём ещё до 8 с, пока запись появится.
        for (var i = 0; i < 16 && !_server.LogEntries.Any(e => e.RequestMessage.Path == path); i++)
            await Task.Delay(500);
        return _server.LogEntries.Count(e => e.RequestMessage.Path == path);
    }

    // ---------- Н-13: срок источника не ломает ответ Советника ----------

    [Fact]
    public async Task OpenBase_SlowServer_TimesOutQuickly_AndReturnsNull_NotThrows()
    {
        Stub("GET", "/api/v2/product/*", 200, "{\"product\":{\"product_name\":\"Хлеб\"}}", TimeSpan.FromSeconds(3));
        ProductInfoResearch.Http = FakeClient(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        var facts = await ProductInfoResearch.OpenBaseAsync("world.openfoodfacts.org", "4600000000001", CancellationToken.None);

        facts.Should().BeNull("срок источника — «в этой базе нет», а не исключение");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.5), "оборвано по сроку 300 мс, а не после 3 с задержки сервера (запас на нагрузку при полном прогоне)");
        (await SettledRequestCount("/api/v2/product/4600000000001.json")).Should().Be(1);
    }

    [Fact]
    public async Task DuckDuckGo_SlowServer_TimesOutQuickly_AndReturnsEmpty_NotThrows()
    {
        Stub("GET", "/html/", 200, "<html></html>", TimeSpan.FromSeconds(5));
        ProductInfoResearch.Http = FakeClient(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        var (text, sources) = await ProductInfoResearch.DuckDuckGoAsync("хлеб", CancellationToken.None);

        text.Should().BeEmpty();
        sources.Should().BeEmpty();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task OpenBase_UserCancelled_StillPropagatesCancellation()
    {
        Stub("GET", "/api/v2/product/*", 200, "{}", TimeSpan.FromSeconds(5));
        ProductInfoResearch.Http = FakeClient(TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = async () => await ProductInfoResearch.OpenBaseAsync("world.openfoodfacts.org", "4600000000001", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("настоящая отмена пользователем по-прежнему прерывает ответ");
    }

    [Fact]
    public async Task ResearchAsync_AllSourcesTimeOut_StillReturnsFinding_NotException()
    {
        Stub("GET", "/html/", 200, "<html></html>", TimeSpan.FromSeconds(5));
        ProductInfoResearch.Http = FakeClient(TimeSpan.FromMilliseconds(300));
        var product = new CatalogProductTileVm("t13-1", "Тестовый товар для срока", "10 сом", false); // без штрихкода: справочник и открытые базы пропускаются

        var watch = Stopwatch.StartNew();
        var findings = await ProductInfoResearch.ResearchAsync(new[] { product }, CancellationToken.None);

        findings.Should().HaveCount(1, "источник пропущен, но ответ про товар остаётся (с пометкой «сведений не нашлось»)");
        findings[0].Text.Should().Contain("не нашлось");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    // ---------- Н-14: sendMessage не повторяется при 5xx ----------

    [Theory]
    [InlineData(502)]
    [InlineData(504)]
    [InlineData(500)]
    public async Task SendMessage_5xx_SingleRequest_NoDuplicate(int status)
    {
        Stub("POST", "/botTKN/sendMessage", status, "{\"ok\":false,\"description\":\"Bad Gateway\"}");
        TelegramBotService.Http = FakeClient(TimeSpan.FromSeconds(30)) /* 2026-11-09: запас на загруженном ПК — срок клиента не предмет проверки, флак в полном прогоне */;

        var (error, _, kind) = await TelegramBotService.SendPartAsync("TKN", "777001", "привет", null, CancellationToken.None);

        kind.Should().Be(TelegramBotService.PartResult.Failed);
        error.Should().NotBeNullOrWhiteSpace().And.Contain(status.ToString());
        _server.LogEntries.Count(e => e.RequestMessage.Path == "/botTKN/sendMessage").Should().Be(1, "повтор sendMessage при 5xx дал бы дубль сообщения");
    }

    [Fact]
    public async Task SendMessage_Timeout_SingleRequest_ClearMessage()
    {
        Stub("POST", "/botTKN/sendMessage", 200, "{\"ok\":true}", TimeSpan.FromSeconds(3));
        TelegramBotService.Http = FakeClient(TimeSpan.FromMilliseconds(300));

        var watch = Stopwatch.StartNew();
        var (error, _, kind) = await TelegramBotService.SendPartAsync("TKN", "777002", "привет", null, CancellationToken.None);

        kind.Should().Be(TelegramBotService.PartResult.Failed);
        error.Should().Contain("Telegram").And.Contain("20");
        error.Should().NotContain("TaskCanceled").And.NotContain("canceled");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2.5));
        (await SettledRequestCount("/botTKN/sendMessage")).Should().Be(1, "после срока повтора нет — иначе сообщение могло бы прийти дважды");
    }

    [Fact]
    public async Task SendMessage_Ok_SingleRequest()
    {
        Stub("POST", "/botTKN/sendMessage", 200, "{\"ok\":true,\"result\":{\"message_id\":1}}");
        TelegramBotService.Http = FakeClient(TimeSpan.FromSeconds(30)) /* 2026-11-09: запас на загруженном ПК — срок клиента не предмет проверки, флак в полном прогоне */;

        var (error, _, kind) = await TelegramBotService.SendPartAsync("TKN", "777003", "привет", null, CancellationToken.None);

        kind.Should().Be(TelegramBotService.PartResult.Ok);
        error.Should().BeNull();
        _server.LogEntries.Count(e => e.RequestMessage.Path == "/botTKN/sendMessage").Should().Be(1);
    }

    // ---------- Н-12: запасные модели ИИ — свой срок на каждую и общий ----------

    private const string OpenRouterPath = "/api/v1/chat/completions";
    private const string GroqPath = "/openai/v1/chat/completions";
    private const string OkAnswer = "{\"choices\":[{\"message\":{\"content\":\"Ответ запасной модели\"}}]}";

    [Fact]
    public async Task ChatFallback_BothSlow_StopsWithinTotalLimit_WithClearError()
    {
        UserPreferences.Instance.OpenRouterApiKey = "test-router-key";
        UserPreferences.Instance.GroqApiKey = "test-groq-key";
        Stub("POST", OpenRouterPath, 200, OkAnswer, TimeSpan.FromSeconds(6));
        Stub("POST", GroqPath, 200, OkAnswer, TimeSpan.FromSeconds(6));
        AiProviders.Http = FakeClient(TimeSpan.FromSeconds(30)); // сам клиент не оборвёт — оборвать должен срок ChatFallbackAsync
        AiProviders.FallbackOneTimeout = TimeSpan.FromMilliseconds(400);
        AiProviders.FallbackTotalTimeout = TimeSpan.FromMilliseconds(1500);

        var watch = Stopwatch.StartNew();
        var (answer, error) = await AiProviders.ChatFallbackAsync("система", Array.Empty<(string, string)>(), "вопрос", CancellationToken.None);

        answer.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "срок 400 мс на модель, 1,5 с на обе — а не 6 + 6 с задержки сервера");
    }

    [Fact]
    public async Task ChatFallback_FirstSlow_SecondAnswers()
    {
        UserPreferences.Instance.OpenRouterApiKey = "test-router-key";
        UserPreferences.Instance.GroqApiKey = "test-groq-key";
        Stub("POST", OpenRouterPath, 200, OkAnswer, TimeSpan.FromSeconds(6));
        Stub("POST", GroqPath, 200, OkAnswer);
        AiProviders.Http = FakeClient(TimeSpan.FromSeconds(30));
        AiProviders.FallbackOneTimeout = TimeSpan.FromMilliseconds(400);
        AiProviders.FallbackTotalTimeout = TimeSpan.FromSeconds(10);

        var (answer, error) = await AiProviders.ChatFallbackAsync("система", Array.Empty<(string, string)>(), "вопрос", CancellationToken.None);

        answer.Should().Be("Ответ запасной модели");
        error.Should().BeNull();
    }

    // ---------- Н-19: общая фабрика ----------

    [Fact]
    public void ExternalHttp_Create_SetsTimeout_AndUserAgent()
    {
        using var http = ExternalHttp.Create(TimeSpan.FromSeconds(7), "NurTest/1.0");
        http.Timeout.Should().Be(TimeSpan.FromSeconds(7));
        http.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("NurTest");
    }

    [Fact]
    public async Task ExternalHttp_Client_SendsAcceptEncoding_AndUnpacksGzip()
    {
        _server.Given(Request.Create().WithPath("/zip").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{\"ok\":true}"));
        using var http = ExternalHttp.Create(TimeSpan.FromSeconds(30)); // 2026-11-05: запас на загруженном ПК (проверяется заголовок, а не скорость)

        using var response = await http.GetAsync(_server.Url + "/zip");

        response.IsSuccessStatusCode.Should().BeTrue();
        var entry = _server.LogEntries.Last(e => e.RequestMessage.Path == "/zip");
        entry.RequestMessage.Headers!["Accept-Encoding"].ToString().Should().Contain("gzip");
    }
}
