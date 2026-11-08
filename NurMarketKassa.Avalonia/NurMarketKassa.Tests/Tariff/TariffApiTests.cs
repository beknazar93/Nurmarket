using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Tariff;

/// <summary>2026-11-08 (ТЗ п. 32, Б-39, ответ Умара 08.11): тариф «Своё» — каталог (кэш 10 минут), расчёт, заявка с Idempotency-Key, ошибки. Заглушка WireMock, форма ответов — как у сервера
/// (образец GET public/tariffs/ снят с настоящего сервера 08.11, чтение без входа).</summary>
[Collection("UserPreferences")]
public sealed class TariffApiTests : IDisposable
{
    private const string CatalogJson = """
        {"currency":"KGS","plans":[{"code":"start","name":{"ru":"Старт"},"price":"1300.00","period":"month","includes":["pos","stock"]},
        {"code":"standard","name":{"ru":"Стандарт"},"price":"3600.00","period":"month","includes":"all"},
        {"code":"custom","base":"start","name":{"ru":"Своё"}}],
        "packs":[{"code":"ai","name":{"en":"AI assistant","ru":"ИИ-помощник"},"description":{"ru":"Нур Советник"},"group":"ai","activation_fee":"1500.00","monthly_fee":"400.00","included_in":["standard"],"trial_days":0},
        {"code":"scales","name":{"en":"Scales"},"description":{"ru":"Подключение весов"},"group":"devices","activation_fee":1500,"monthly_fee":"450.50","included_in":["standard"],"trial_days":0}]}
        """;

    private readonly FakeNurCrmServer _server = new();
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public TariffApiTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        TariffApi.ResetCacheForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        TariffApi.ResetCacheForTests();
        _server.Dispose();
    }

    private async Task<TariffApi> MakeAsync()
    {
        _server.StubLogin();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("owner@test.kg", "12345678");
        return new TariffApi(api);
    }

    private int Hits(string path, string method) => _server.Server.LogEntries.Count(e => e.RequestMessage.Path == path && e.RequestMessage.Method == method);

    [Fact]
    public async Task Catalog_ParsesPlansByCode_Packs_NamesByLanguage_AndIsCached()
    {
        _server.Json("GET", "/api/public/tariffs/", 200, CatalogJson);
        var api = await MakeAsync();

        var catalog = await api.GetCatalogAsync();

        catalog.Plans.Select(p => p.Code).Should().Equal("start", "standard", "custom");
        catalog.Plan("custom")!.BaseCode.Should().Be("start");
        catalog.Plan("start")!.Price.Should().Be(1300m);
        catalog.Plan("standard")!.Name.Should().Be("Стандарт");
        catalog.Packs.Should().HaveCount(2);
        var ai = catalog.Packs[0];
        ai.Name.Should().Be("ИИ-помощник");
        ai.ActivationFee.Should().Be(1500m);
        ai.MonthlyFee.Should().Be(400m);
        ai.IncludedIn.Should().Equal("standard");
        catalog.Packs[1].Name.Should().Be("Scales", "на языке интерфейса нет — английское");
        catalog.Packs[1].ActivationFee.Should().Be(1500m, "число вместо строки");
        catalog.Packs[1].MonthlyFee.Should().Be(450.50m);

        await api.GetCatalogAsync();
        Hits("/api/public/tariffs/", "GET").Should().Be(1, "кэш 10 минут");
    }

    [Fact]
    public async Task Catalog_NameFollowsInterfaceLanguage()
    {
        _server.Json("GET", "/api/public/tariffs/", 200, "{\"plans\":[],\"packs\":[{\"code\":\"ai\",\"name\":{\"ru\":\"ИИ\",\"ky\":\"ЖИ\",\"en\":\"AI\"},\"activation_fee\":\"1\",\"monthly_fee\":\"2\",\"included_in\":[]}]}");
        UserPreferences.Instance.Language = AppLanguage.Kyrgyz;
        var catalog = await (await MakeAsync()).GetCatalogAsync();
        catalog.Packs.Single().Name.Should().Be("ЖИ");
    }

    [Fact]
    public async Task Quote_SendsPacksAndMonthPeriod_ParsesAlreadyActive_AndStandardCheaper()
    {
        _server.Json("POST", "/api/users/company/tariff/quote/", 200,
            """{"monthly":"400.00","once":"1500.00","base":"1300.00","packs":[{"code":"ai","monthly":"400.00","once":"1500.00","already_active":false},{"code":"clients","monthly":"0.00","once":"0.00","already_active":true}],"standard_cheaper":true,"standard_price":"3600.00"}""");
        var api = await MakeAsync();

        var q = await api.QuoteAsync(["ai", "clients"]);

        q.Monthly.Should().Be(400m);
        q.Once.Should().Be(1500m);
        q.Base.Should().Be(1300m);
        q.StandardCheaper.Should().BeTrue();
        q.StandardPrice.Should().Be(3600m);
        q.Packs.Should().HaveCount(2);
        q.Packs[1].Should().Be(new TariffQuotePack("clients", 0m, 0m, true));
        var body = JsonDocument.Parse(_server.Server.LogEntries.Single(e => e.RequestMessage.Path == "/api/users/company/tariff/quote/").RequestMessage.Body!).RootElement;
        body.GetProperty("period").GetString().Should().Be("month");
        body.GetProperty("packs").EnumerateArray().Select(x => x.GetString()).Should().Equal("ai", "clients");
    }

    [Theory]
    [InlineData(400, "{\"detail\":\"pack_unknown\"}", "не знает")]
    [InlineData(409, "{\"detail\":\"plan_not_allowed\"}", "недоступно")]
    [InlineData(403, "{\"detail\":\"forbidden\"}", "только владелец")]
    public async Task Quote_Errors_AreMappedToReadableText(int status, string body, string expected)
    {
        _server.Json("POST", "/api/users/company/tariff/quote/", status, body);
        var api = await MakeAsync();

        var act = () => api.QuoteAsync(["ai"]);

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ex.StatusCode.Should().Be(status);
        TariffApi.Describe(ex).Should().Contain(expected);
    }

    [Fact]
    public async Task Custom_Created_201_ParsesOrder_NoPayUrl_AndSendsIdempotencyKey_SameOnRetry()
    {
        _server.Json("POST", "/api/users/company/tariff/custom/", 201,
            """{"order_id":"o-77","status":"pending","amount_monthly":"400.00","amount_once":"1500.00","pay_url":null}""");
        var api = await MakeAsync();
        var key = Guid.NewGuid().ToString("D");

        var first = await api.SubmitCustomAsync(["ai"], key);
        var again = await api.SubmitCustomAsync(["ai"], key);
        var other = await api.SubmitCustomAsync(["ai"], Guid.NewGuid().ToString("D"));

        first.OrderId.Should().Be("o-77");
        first.Status.Should().Be("pending");
        first.AmountMonthly.Should().Be(400m);
        first.AmountOnce.Should().Be(1500m);
        first.PayUrl.Should().BeNull("платёжной ссылки нет — заявку ведёт менеджер");
        again.OrderId.Should().Be(first.OrderId);
        var keys = _server.Server.LogEntries.Where(e => e.RequestMessage.Path == "/api/users/company/tariff/custom/")
            .Select(e => e.RequestMessage.Headers!["Idempotency-Key"].Single()).ToList();
        keys.Should().HaveCount(3);
        keys[0].Should().Be(key);
        keys[1].Should().Be(key, "повтор той же попытки уходит с тем же ключом");
        keys[2].Should().NotBe(key);
    }

    [Theory]
    [InlineData(409, "{\"detail\":\"plan_not_allowed\"}", "недоступно")]
    [InlineData(403, "{}", "только владелец")]
    [InlineData(400, "{\"code\":\"pack_unknown\"}", "не знает")]
    public async Task Custom_Errors_AreMappedToReadableText(int status, string body, string expected)
    {
        _server.Json("POST", "/api/users/company/tariff/custom/", status, body);
        var api = await MakeAsync();

        var act = () => api.SubmitCustomAsync(["ai"], Guid.NewGuid().ToString("D"));

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        TariffApi.Describe(ex).Should().Contain(expected);
    }

    [Fact]
    public void Describe_429_And_401_AreReadable()
    {
        // Сам 429 с сервера не гоняем: он включает общую паузу «Сервер занят» для всех тестов процесса (ApiThrottle).
        TariffApi.Describe(new ApiException("x", 429)).Should().Contain("Слишком частые");
        TariffApi.Describe(new ApiException("x", 401)).Should().Contain("войдите заново");
    }
}
