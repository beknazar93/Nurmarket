using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Tariff;

/// <summary>2026-11-20 (ответ Умара 20.11, В.2, тариф «Мини»): разбор capabilities.plan {code, names, limits, allowed_clients, upgrade_plan} (нет плана → null, без падения); 403 plan_client_not_allowed / plan_limit
/// разбираются в <see cref="PlanBlock"/> с текстом сервера (detail) и объявляются один раз в 5 минут на код; на входе, на обновлении входа и обычном запросе касса не падает. Сервер — WireMock.</summary>
[Collection("ServerFeatures")]
public sealed class PlanCapabilitiesTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly FakeNurCrmServer _fake = new();

    private const string MiniPlan = """
        {"code":"mini","name":"Мини","names":{"ru":"Мини","ky":"Мини","en":"Mini","tr":"Mini","uz":"Mini"},
         "descriptions":{"ru":"Только телефон: 1 пользователь (владелец), 1 устройство, 1 касса.","en":"Phone only."},
         "period":"month","limits":{"max_staff":1,"max_devices":1,"max_cashboxes":1},"allowed_clients":["mobile"],"upgrade_plan":"start"}
        """;

    public PlanCapabilitiesTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        PlanBlockMonitor.ResetForTests();
        SessionExpiryMonitor.ResetForTests();
        ServerCapabilities.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        PlanBlockMonitor.ResetForTests();
        SessionExpiryMonitor.ResetForTests();
        ServerCapabilities.Reset();
        _fake.Dispose();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ───────────── модель plan ─────────────

    [Fact]
    public void Mini_plan_is_parsed_with_names_limits_allowed_clients_and_upgrade()
    {
        var plan = PlanInfo.Parse(J("{\"features\":[\"a\"],\"plan\":" + MiniPlan + "}"))!;

        plan.Code.Should().Be("mini");
        plan.Name.Should().Be("Мини");
        plan.Names["en"].Should().Be("Mini");
        plan.LocalizedDescription().Should().Contain("Только телефон");
        plan.Period.Should().Be("month");
        plan.Limits.Should().Be(new PlanLimits(1, 1, 1));
        plan.AllowedClients.Should().Equal("mobile");
        plan.UpgradePlan.Should().Be("start");
        plan.RestrictsClients.Should().BeTrue();
        plan.Allows("mobile").Should().BeTrue();
        plan.Allows("desktop_pos").Should().BeFalse();
        plan.Allows("android_pos").Should().BeFalse();
        plan.Allows("desktop_owner").Should().BeFalse();
    }

    [Fact]
    public void Other_plans_have_no_limits_and_allow_all_clients()
    {
        var plan = PlanInfo.Parse(J("""{"plan":{"code":"start","name":"Старт","names":{"ru":"Старт"},"limits":{"max_staff":null,"max_devices":null,"max_cashboxes":null},"allowed_clients":null,"upgrade_plan":null}}"""))!;

        plan.Limits.Should().Be(new PlanLimits(null, null, null));
        plan.AllowedClients.Should().BeNull();
        plan.RestrictsClients.Should().BeFalse();
        plan.Allows("desktop_pos").Should().BeTrue();
        plan.UpgradePlan.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"plan":null}""")]
    [InlineData("""{"features":["a"]}""")]
    [InlineData("""{"plan":"mini"}""")]
    [InlineData("""{"plan":[]}""")]
    [InlineData("""{"plan":{}}""")]
    [InlineData("""{"plan":{"code":""}}""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public void No_plan_or_wrong_shape_is_null_and_never_throws(string json) =>
        FluentActions.Invoking(() => PlanInfo.Parse(J(json))).Should().NotThrow().Subject.Should().BeNull();

    [Fact]
    public void Wrong_field_types_inside_plan_do_not_break_the_parse()
    {
        var plan = PlanInfo.Parse(J("""{"plan":{"code":"mini","name":5,"names":["x"],"limits":{"max_staff":"1","max_devices":2.5},"allowed_clients":[1,"mobile",null],"upgrade_plan":7}}"""))!;

        plan.Code.Should().Be("mini");
        plan.Name.Should().Be("mini", "нет текстового названия — код");
        plan.Names.Should().BeEmpty();
        plan.Limits.Should().Be(new PlanLimits(null, null, null));
        plan.AllowedClients.Should().Equal("mobile");
        plan.UpgradePlan.Should().BeNull();
    }

    [Fact]
    public void Localized_name_follows_the_language_and_falls_back_to_russian()
    {
        var plan = PlanInfo.Parse(J("""{"plan":{"code":"mini","name":"Мини","names":{"ru":"Мини","en":"Mini"}}}"""))!;
        plan.LocalizedName().Should().Be("Мини");
        UserPreferences.Instance.Language = AppLanguage.English;
        plan.LocalizedName().Should().Be("Mini");
        UserPreferences.Instance.Language = AppLanguage.Uzbek;
        plan.LocalizedName().Should().Be("Мини", "узбекского названия нет — русское");
    }

    [Fact]
    public async Task Capabilities_request_fills_the_plan_and_a_null_plan_clears_it()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");

        _fake.Json("GET", "/api/main/capabilities/", 200, "{\"features\":[\"receipt_edit\"],\"plan\":" + MiniPlan + "}");
        var set = await ServerCapabilities.LoadAsync(api, force: true);
        set.Should().Contain("receipt_edit");
        ServerCapabilities.Plan.Should().NotBeNull();
        ServerCapabilities.Plan!.Code.Should().Be("mini");
        ServerCapabilities.Plan.Allows("desktop_pos").Should().BeFalse();

        _fake.Server.Reset();
        _fake.StubLogin();
        _fake.Json("GET", "/api/main/capabilities/", 200, """{"features":["receipt_edit"],"plan":null}""");
        await ServerCapabilities.LoadAsync(api, force: true);
        ServerCapabilities.Plan.Should().BeNull("нет компании или тарифа — plan: null, без падения");
    }

    // ───────────── отказ 403 по тарифу ─────────────

    private const string ClientBlockBody = """
        {"code":"plan_client_not_allowed","plan":"mini","client":"desktop_pos","allowed_clients":["mobile"],"upgrade_plan":"start",
         "detail":"Тариф «Мини» работает только с телефона. Чтобы пользоваться (касса на ПК), смените тариф на «Старт»."}
        """;

    private const string LimitBody = """
        {"code":"plan_limit","plan":"mini","limit":"max_staff","max":1,"current":1,"upgrade_plan":"start",
         "detail":"На тарифе «Мини» доступно: 1 пользователь (только владелец). Чтобы добавить ещё, смените тариф на «Старт»."}
        """;

    [Fact]
    public void Block_bodies_from_the_server_are_parsed_with_the_server_detail()
    {
        var client = PlanBlockMonitor.TryParse(ClientBlockBody)!;
        client.Code.Should().Be("plan_client_not_allowed");
        client.IsClientBlock.Should().BeTrue();
        client.Plan.Should().Be("mini");
        client.Client.Should().Be("desktop_pos");
        client.UpgradePlan.Should().Be("start");
        client.Detail.Should().Contain("смените тариф на «Старт»");

        var limit = PlanBlockMonitor.TryParse(LimitBody)!;
        limit.IsClientBlock.Should().BeFalse();
        limit.Limit.Should().Be("max_staff");
        (limit.Max, limit.Current).Should().Be((1, 1));
    }

    [Theory]
    [InlineData("""{"detail":"Нет прав"}""")]
    [InlineData("""{"code":"permission_denied"}""")]
    [InlineData("""{"code":"plan_other"}""")]
    [InlineData("<html>plan_limit</html>")]
    [InlineData("")]
    [InlineData("[]")]
    public void Other_403_bodies_are_not_plan_blocks(string body) => PlanBlockMonitor.TryParse(body).Should().BeNull();

    [Fact]
    public void The_same_block_is_announced_once_per_five_minutes_and_a_different_code_separately()
    {
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        PlanBlockMonitor.Clock = () => now;
        var raised = new List<PlanBlock>();
        void On(PlanBlock b) => raised.Add(b);
        PlanBlockMonitor.Blocked += On;
        try
        {
            PlanBlockMonitor.Note(403, ClientBlockBody).Should().BeTrue();
            PlanBlockMonitor.Note(403, ClientBlockBody).Should().BeFalse("фоновых запросов много, окно одно");
            PlanBlockMonitor.Note(403, LimitBody).Should().BeTrue("другой код — отдельное окно");
            PlanBlockMonitor.Note(401, ClientBlockBody).Should().BeFalse("только 403");
            PlanBlockMonitor.RecentClientBlock.Should().BeTrue();

            now = now.AddMinutes(6);
            PlanBlockMonitor.RecentClientBlock.Should().BeFalse();
            PlanBlockMonitor.Note(403, ClientBlockBody).Should().BeTrue("через 5 минут — снова");
            raised.Select(b => b.Code).Should().Equal("plan_client_not_allowed", "plan_limit", "plan_client_not_allowed");
            PlanBlockMonitor.Last!.Code.Should().Be("plan_client_not_allowed");
        }
        finally
        {
            PlanBlockMonitor.Blocked -= On;
        }
    }

    [Fact]
    public async Task Login_403_plan_client_not_allowed_gives_the_server_text_and_the_block_without_a_crash()
    {
        _fake.Json("POST", "/api/users/auth/login/", 403, ClientBlockBody);
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        var raised = new List<PlanBlock>();
        void On(PlanBlock b) => raised.Add(b);
        PlanBlockMonitor.Blocked += On;
        try
        {
            var ex = (await FluentActions.Awaiting(() => api.LoginAsync("cashier@test.kg", "12345678")).Should().ThrowAsync<ApiException>()).Which;

            ex.StatusCode.Should().Be(403);
            ex.Message.Should().Contain("Тариф «Мини» работает только с телефона", "окно входа показывает текст сервера");
            PlanBlockMonitor.FromException(ex)!.Code.Should().Be("plan_client_not_allowed");
            raised.Should().ContainSingle().Which.Detail.Should().Contain("«Старт»");
        }
        finally
        {
            PlanBlockMonitor.Blocked -= On;
        }
    }

    [Fact]
    public async Task Refresh_403_plan_client_not_allowed_after_the_plan_change_shows_the_plan_block_and_asks_for_login_without_a_crash()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        // владелец перевёл компанию на «Мини»: access больше не принимается, refresh тоже отвечает 403 по тарифу
        _fake.Server.Reset();
        _fake.Json("GET", "/api/users/profile/", 401, """{"detail":"Token expired"}""");
        _fake.Json("POST", "/api/users/auth/refresh/", 403, ClientBlockBody);
        var raised = new List<PlanBlock>();
        void On(PlanBlock b) => raised.Add(b);
        PlanBlockMonitor.Blocked += On;
        try
        {
            await FluentActions.Awaiting(() => api.GetProfileAsync()).Should().ThrowAsync<ApiException>();

            raised.Should().ContainSingle().Which.IsClientBlock.Should().BeTrue();
            SessionExpiryMonitor.IsExpired.Should().BeTrue("вход потерян — после смены тарифа нужен новый вход; очередь и данные не тронуты");
            PlanBlockMonitor.RecentClientBlock.Should().BeTrue("окно «Сессия истекла» поверх окна тарифа не показывается");
        }
        finally
        {
            PlanBlockMonitor.Blocked -= On;
        }
    }

    [Fact]
    public async Task Plan_limit_403_on_creating_an_employee_is_announced_with_the_limit_text()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        _fake.Json("POST", "/api/users/employees/create/", 403, LimitBody);
        var raised = new List<PlanBlock>();
        void On(PlanBlock b) => raised.Add(b);
        PlanBlockMonitor.Blocked += On;
        try
        {
            var ex = (await FluentActions.Awaiting(() => api.RequestAsync(HttpMethod.Post, "api/users/employees/create/", new { email = "a@b.kg" }, null)).Should().ThrowAsync<ApiException>()).Which;

            ex.Message.Should().Contain("1 пользователь (только владелец)");
            raised.Should().ContainSingle().Which.Limit.Should().Be("max_staff");
            SessionExpiryMonitor.IsExpired.Should().BeFalse("лимит тарифа — не потеря входа");
        }
        finally
        {
            PlanBlockMonitor.Blocked -= On;
        }
    }

    [Fact]
    public async Task An_ordinary_403_without_a_plan_code_is_not_a_plan_block()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        _fake.Json("GET", "/api/main/clients/", 403, """{"detail":"Недостаточно прав"}""");
        var raised = 0;
        void On(PlanBlock _) => raised++;
        PlanBlockMonitor.Blocked += On;
        try
        {
            await FluentActions.Awaiting(() => api.RequestAsync(HttpMethod.Get, "api/main/clients/", null, null)).Should().ThrowAsync<ApiException>();
            raised.Should().Be(0);
        }
        finally
        {
            PlanBlockMonitor.Blocked -= On;
        }
    }
}
