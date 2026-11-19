using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Billing;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Billing;

/// <summary>2026-11-19 (ответ Умара 19.11, раздел 2): оплата подписки через ELQR — разбор config / счёта / строк / ошибок, Idempotency-Key, адреса, «без признака — экран скрыт»,
/// учебная оплата только в test, напоминания 7/3/1, секреты (QR-картинка и ссылка) не в журнале. Заглушка WireMock; форма ответов — как в описании сервера. Запускать только через tools/qa/test-safe.ps1.</summary>
[Collection("UserPreferences")]
public sealed class BillingApiTests : IDisposable
{
    private const string ConfigJson = """
        {"available":true,"mode":"test","bank_fee":"30.00","months":[1,3,6,12],"current_plan":"standard","valid_until":"2026-12-19",
         "plans":[{"code":"start","price_month":"1300.00"},{"code":"standard","price_month":"3600.00"},{"code":"custom","price_month":1500}],"qr_ttl_seconds":1200}
        """;

    private const string InvoiceJson = """
        {"invoice_id":"inv-77","status":"pending","provider":"eldik","test_mode":true,"plan":"standard","plan_name":"Стандарт","months":3,"price_month":"3600.00",
         "subtotal":"10800.00","discount":"0.00","bank_fee":"30.00","amount":"10830.00",
         "lines":[{"title":"Подписка NurCRM, 3 мес.","amount":"10800.00"},{"title":"Комиссия банка — 30 сом","amount":30}],
         "qr_image":"iVBORw0KGgo=","qr_link":"https://qr.example/pay/SECRET-QR-LINK","expires_at":"2026-11-19T20:20:00+06:00","paid_at":null,"valid_until":"2026-12-19","error":null,"created_at":"2026-11-19T20:00:00+06:00"}
        """;

    private readonly FakeNurCrmServer _server = new();
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public BillingApiTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        ServerCapabilities.ResetMemoryForTests();
        _server.Dispose();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<BillingApi> MakeAsync()
    {
        _server.StubLogin();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("owner@test.kg", "12345678");
        return new BillingApi(api);
    }

    private IEnumerable<WireMock.Logging.ILogEntry> Hits(string path, string method) => _server.Server.LogEntries.Where(e => e.RequestMessage.Path == path && e.RequestMessage.Method == method);

    // ── Разбор ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Config_IsParsed_ModeFeeMonthsPlansValidUntil()
    {
        var c = BillingParser.ParseConfig(J(ConfigJson));
        c.Available.Should().BeTrue();
        c.IsTest.Should().BeTrue();
        c.BankFee.Should().Be(30m);
        c.Months.Should().Equal(1, 3, 6, 12);
        c.CurrentPlan.Should().Be("standard");
        c.Plans.Select(p => p.Code).Should().Equal("start", "standard", "custom");
        c.Plans[2].PriceMonth.Should().Be(1500m, "цена числом, не строкой");
        c.QrTtlSeconds.Should().Be(1200);
        c.ValidUntil!.Value.Date.Should().Be(new DateTime(2026, 12, 19));
    }

    [Fact]
    public void Config_Defaults_WhenFieldsMissing_AndCurrentPlanObject()
    {
        var c = BillingParser.ParseConfig(J("{\"available\":false,\"current_plan\":{\"code\":\"start\",\"name\":\"Старт\"}}"));
        c.Available.Should().BeFalse();
        c.IsTest.Should().BeFalse("режим не назван — боевой");
        c.Months.Should().Equal(1, 3, 6, 12);
        c.BankFee.Should().Be(30m);
        c.CurrentPlan.Should().Be("start");
        c.CurrentPlanName.Should().Be("Старт");
        BillingParser.ParseConfig(J("{\"available\":true,\"mode\":\"live\"}")).IsTest.Should().BeFalse();
    }

    [Fact]
    public void Invoice_IsParsed_LinesAsIs_AmountsFromStringsAndNumbers()
    {
        var i = BillingParser.ParseInvoice(J(InvoiceJson));
        i.InvoiceId.Should().Be("inv-77");
        i.Status.Should().Be(BillingStatus.Pending);
        i.IsFinal.Should().BeFalse();
        i.TestMode.Should().BeTrue();
        i.Months.Should().Be(3);
        i.Amount.Should().Be(10830m);
        i.Lines.Select(l => l.Title).Should().Equal("Подписка NurCRM, 3 мес.", "Комиссия банка — 30 сом");
        i.Lines.Select(l => l.Amount).Should().Equal(10800m, 30m);
        i.ExpiresAt.Should().NotBeNull();
        i.ValidUntil!.Value.Date.Should().Be(new DateTime(2026, 12, 19));
        i.QrImageBase64.Should().NotBeNull();
        i.QrLink.Should().StartWith("https://");
        i.PaidAt.Should().BeNull();
    }

    [Theory]
    [InlineData("pending", BillingStatus.Pending, false)]
    [InlineData("processing", BillingStatus.Processing, false)]
    [InlineData("paid", BillingStatus.Paid, true)]
    [InlineData("expired", BillingStatus.Expired, true)]
    [InlineData("cancelled", BillingStatus.Cancelled, true)]
    [InlineData("error", BillingStatus.Error, true)]
    public void Statuses_MapAndFinality(string raw, BillingStatus status, bool final)
    {
        var i = BillingParser.ParseInvoice(J("{\"invoice_id\":\"x\",\"status\":\"" + raw + "\"}"));
        i.Status.Should().Be(status);
        i.IsFinal.Should().Be(final);
    }

    [Fact]
    public void InvoiceList_ArrayOrResultsPage()
    {
        BillingParser.ParseInvoiceList(J("[{\"invoice_id\":\"a\",\"status\":\"paid\"},{\"invoice_id\":\"b\",\"status\":\"expired\"}]")).Should().HaveCount(2);
        BillingParser.ParseInvoiceList(J("{\"count\":1,\"results\":[{\"invoice_id\":\"a\",\"status\":\"paid\"}]}")).Should().HaveCount(1);
        BillingParser.ParseInvoiceList(J("{}")).Should().BeEmpty();
    }

    [Fact]
    public void Invoice_ToString_DoesNotContainQrImageOrLink()
    {
        var i = BillingParser.ParseInvoice(J(InvoiceJson));
        var text = i.ToString();
        text.Should().NotContain("SECRET-QR-LINK").And.NotContain("iVBOR").And.NotContain("qr.example").And.Contain("inv-77");
    }

    [Fact]
    public void DecodeQr_AcceptsPlainAndDataUri_RejectsGarbage()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var b64 = Convert.ToBase64String(png);
        BillingParser.DecodeQr(b64).Should().Equal(png);
        BillingParser.DecodeQr("data:image/png;base64," + b64).Should().Equal(png);
        BillingParser.DecodeQr("это не base64!").Should().BeNull();
        BillingParser.DecodeQr("").Should().BeNull();
    }

    // ── Ошибки ───────────────────────────────────────────────────────────────────

    private static ApiException Err(int status, string body) => new("ошибка", status, JsonDocument.Parse(body).RootElement.Clone());

    [Theory]
    [InlineData(409, "{\"code\":\"billing_unavailable\"}", BillingErrorKind.Unavailable)]
    [InlineData(400, "{\"code\":\"invalid\"}", BillingErrorKind.InvalidMonths)]
    [InlineData(400, "{\"code\":\"plan_unknown\"}", BillingErrorKind.PlanUnknown)]
    [InlineData(429, "{\"code\":\"too_many_invoices\"}", BillingErrorKind.TooManyInvoices)]
    [InlineData(502, "{\"code\":\"bank_unavailable\"}", BillingErrorKind.BankUnavailable)]
    [InlineData(409, "{\"code\":\"invoice_final\"}", BillingErrorKind.InvoiceFinal)]
    [InlineData(429, "{}", BillingErrorKind.TooManyInvoices)]
    [InlineData(502, "{}", BillingErrorKind.BankUnavailable)]
    [InlineData(403, "{}", BillingErrorKind.NoRights)]
    [InlineData(401, "{}", BillingErrorKind.SessionExpired)]
    public void Errors_AreClassifiedByCodeThenStatus(int status, string body, BillingErrorKind kind) =>
        BillingApi.Classify(Err(status, body)).Kind.Should().Be(kind);

    [Fact]
    public void Error502_CarriesInvoiceId_FromDataOrRoot_AndNetworkIsNotApiError()
    {
        BillingApi.Classify(Err(502, "{\"code\":\"bank_unavailable\",\"data\":{\"invoice_id\":\"inv-err\"}}")).InvoiceId.Should().Be("inv-err");
        BillingApi.Classify(Err(502, "{\"code\":\"bank_unavailable\",\"invoice_id\":\"inv-root\"}")).InvoiceId.Should().Be("inv-root");
        BillingApi.Classify(new HttpRequestException("обрыв")).Kind.Should().Be(BillingErrorKind.Network);
    }

    [Fact]
    public void Describe_GivesPlainRussianTexts()
    {
        BillingApi.Describe(Err(409, "{\"code\":\"billing_unavailable\"}")).Should().Contain("ещё не подключена для вашей компании");
        BillingApi.Describe(Err(429, "{\"code\":\"too_many_invoices\"}")).Should().Contain("Слишком много счетов").And.Contain("подождите");
        BillingApi.Describe(Err(502, "{\"code\":\"bank_unavailable\"}")).Should().Contain("Банк").And.Contain("ещё раз");
        BillingApi.Describe(Err(409, "{\"code\":\"invoice_final\"}")).Should().Contain("закрыт");
    }

    // ── Запросы ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Config_RequestsConfigPath()
    {
        _server.Json("GET", "/api/users/billing/config/", 200, ConfigJson);
        var api = await MakeAsync();
        var c = await api.GetConfigAsync();
        c.Available.Should().BeTrue();
        Hits("/api/users/billing/config/", "GET").Should().HaveCount(1);
    }

    [Fact]
    public async Task CreateInvoice_SendsIdempotencyKeyAndBody_SameKeyOnRepeat()
    {
        _server.Json("POST", "/api/users/billing/invoices/", 201, InvoiceJson);
        var api = await MakeAsync();

        var first = await api.CreateInvoiceAsync(3, "standard", "key-attempt-1");
        await api.CreateInvoiceAsync(3, "standard", "key-attempt-1");

        first.InvoiceId.Should().Be("inv-77");
        var hits = Hits("/api/users/billing/invoices/", "POST").ToList();
        hits.Should().HaveCount(2);
        hits.Select(h => h.RequestMessage.Headers!["Idempotency-Key"].ToString()).Should().AllBe("key-attempt-1");
        hits[0].RequestMessage.Body.Should().Contain("\"months\":3").And.Contain("\"plan_code\":\"standard\"");
    }

    [Fact]
    public async Task CreateInvoice_WithoutPlan_OmitsPlanCode_AndRequiresKey()
    {
        _server.Json("POST", "/api/users/billing/invoices/", 201, InvoiceJson);
        var api = await MakeAsync();
        await api.CreateInvoiceAsync(1, null, "k1");
        Hits("/api/users/billing/invoices/", "POST").Single().RequestMessage.Body.Should().NotContain("plan_code");
        await FluentActions.Awaiting(() => api.CreateInvoiceAsync(1, null, " ")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateInvoice_Bank502_ThrowsWithInvoiceIdPayload()
    {
        _server.Json("POST", "/api/users/billing/invoices/", 502, "{\"code\":\"bank_unavailable\",\"data\":{\"invoice_id\":\"inv-err\"}}");
        var api = await MakeAsync();
        var ex = (await FluentActions.Awaiting(() => api.CreateInvoiceAsync(1, null, "k")).Should().ThrowAsync<ApiException>()).Which;
        var e = BillingApi.Classify(ex);
        e.Kind.Should().Be(BillingErrorKind.BankUnavailable);
        e.InvoiceId.Should().Be("inv-err");
    }

    [Fact]
    public async Task GetInvoice_Cancel_TestPay_List_UseTheirPaths()
    {
        _server.Json("GET", "/api/users/billing/invoices/inv-77/", 200, "{\"invoice_id\":\"inv-77\",\"status\":\"paid\",\"valid_until\":\"2027-01-19\"}");
        _server.Json("POST", "/api/users/billing/invoices/inv-77/cancel/", 200, "{\"invoice_id\":\"inv-77\",\"status\":\"cancelled\"}");
        _server.Json("POST", "/api/users/billing/invoices/inv-77/test-pay/", 200, "{\"invoice_id\":\"inv-77\",\"status\":\"processing\"}");
        _server.Json("GET", "/api/users/billing/invoices/", 200, "[{\"invoice_id\":\"a\",\"status\":\"paid\",\"months\":1,\"amount\":\"1330.00\"}]");
        var api = await MakeAsync();

        (await api.GetInvoiceAsync("inv-77")).Status.Should().Be(BillingStatus.Paid);
        (await api.CancelInvoiceAsync("inv-77"))!.Status.Should().Be(BillingStatus.Cancelled);
        (await api.TestPayAsync("inv-77", "processing"))!.Status.Should().Be(BillingStatus.Processing);
        (await api.ListInvoicesAsync("paid")).Should().HaveCount(1);

        Hits("/api/users/billing/invoices/inv-77/test-pay/", "POST").Single().RequestMessage.Body.Should().Contain("\"result\":\"processing\"");
        _server.Server.LogEntries.Single(e => e.RequestMessage.Path == "/api/users/billing/invoices/" && e.RequestMessage.Method == "GET")
            .RequestMessage.Query!["status"].Should().Contain("paid");
    }

    // ── Видимость экрана, учебная оплата, напоминания ───────────────────────────────

    [Fact]
    public void WithoutFeature_ScreenIsHidden_EvenForOwner_AndNotPayableWhenConfigUnavailable()
    {
        BillingAccess.ShouldOffer(featureOn: false, isOwnerOrAdmin: true).Should().BeFalse();
        BillingAccess.ShouldOffer(featureOn: true, isOwnerOrAdmin: false).Should().BeFalse("кассир видит только срок");
        BillingAccess.ShouldOffer(featureOn: true, isOwnerOrAdmin: true).Should().BeTrue();
        BillingAccess.CanPay(true, BillingParser.ParseConfig(J("{\"available\":false}"))).Should().BeFalse();
        BillingAccess.CanPay(false, BillingParser.ParseConfig(J(ConfigJson))).Should().BeFalse();
        BillingAccess.CanPay(true, BillingParser.ParseConfig(J(ConfigJson))).Should().BeTrue();
    }

    [Fact]
    public void FeatureFlag_BillingElqr_IsReadFromCapabilities_InAllShapes()
    {
        ServerCapabilities.Parse(J("{\"features\":[\"billing_elqr\",\"receipt_delete\"]}")).Should().Contain(ServerCapabilities.BillingElqr);
        ServerCapabilities.Parse(J("{\"billing_elqr\":true}")).Should().Contain("billing_elqr");
        ServerCapabilities.Parse(J("{\"capabilities\":{\"features\":{\"billing_elqr\":true}}}")).Should().Contain("billing_elqr", "вложенный features");
        ServerCapabilities.Parse(J("{\"features\":{\"billing_elqr\":false,\"receipt_delete\":true}}")).Should().NotContain("billing_elqr");

        ServerCapabilities.SetFeaturesForTests("receipt_delete");
        BillingAccess.FeatureOn().Should().BeFalse("признака нет — экран скрыт");
        ServerCapabilities.SetFeaturesForTests("receipt_delete", "billing_elqr");
        BillingAccess.FeatureOn().Should().BeTrue();
    }

    [Theory]
    [InlineData("test", true, true)]
    [InlineData("test", false, false)]
    [InlineData("live", true, false)]
    [InlineData("live", false, false)]
    public void TestPay_OnlyInTestMode_AndOnlyForOwnerOrAdmin(string mode, bool owner, bool expected) =>
        BillingAccess.CanTestPay(BillingParser.ParseConfig(J("{\"available\":true,\"mode\":\"" + mode + "\"}")), owner).Should().Be(expected);

    [Fact]
    public void TestPay_NoConfig_NotAllowed() => BillingAccess.CanTestPay(null, true).Should().BeFalse();

    [Fact]
    public void ReminderSteps_7_3_1_AndExpired()
    {
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset At(int days) => now.AddDays(days);
        BillingAccess.ReminderStep(At(30), now).Should().Be(0, "рано напоминать");
        BillingAccess.ReminderStep(At(8), now).Should().Be(0);
        BillingAccess.ReminderStep(At(7), now).Should().Be(7);
        BillingAccess.ReminderStep(At(4), now).Should().Be(7);
        BillingAccess.ReminderStep(At(3), now).Should().Be(3);
        BillingAccess.ReminderStep(At(2), now).Should().Be(3);
        BillingAccess.ReminderStep(At(1), now).Should().Be(1);
        BillingAccess.ReminderStep(At(0), now).Should().Be(1);
        BillingAccess.ReminderStep(At(-1), now).Should().Be(-1);
        BillingAccess.ReminderStep(null, now).Should().BeNull();

        BillingAccess.ExpiryState(At(10), now).Should().Be(BillingExpiry.Normal);
        BillingAccess.ExpiryState(At(7), now).Should().Be(BillingExpiry.Soon);
        BillingAccess.ExpiryState(At(-2), now).Should().Be(BillingExpiry.Expired);
        BillingAccess.ExpiryState(null, now).Should().Be(BillingExpiry.Unknown);

        BillingAccess.ReminderText(At(3), now).Should().Be("Подписка заканчивается через 3 дн.");
        BillingAccess.ReminderText(At(1), now).Should().Be("Подписка заканчивается завтра");
        BillingAccess.ReminderText(At(0), now).Should().Be("Подписка заканчивается сегодня");
        BillingAccess.ReminderText(At(-1), now).Should().Be("Подписка истекла");
        BillingAccess.ReminderText(At(20), now).Should().BeNull();
    }

    [Fact]
    public async Task Secrets_QrImageAndLink_AreNotWrittenToLog_OnFailure()
    {
        // Ответ с QR внутри ошибки: журнал окна пишет только код и статус; здесь проверяем, что разбор Classify/Describe не тянет тело в текст.
        var ex = Err(502, "{\"code\":\"bank_unavailable\",\"qr_image\":\"SECRETIMAGE\",\"qr_link\":\"https://qr.example/SECRET-QR-LINK\"}");
        BillingApi.Describe(ex).Should().NotContain("SECRET");
        BillingApi.Classify(ex).ToString().Should().NotContain("SECRET");
        await Task.CompletedTask;
    }
}
