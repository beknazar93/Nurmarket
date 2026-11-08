using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Wave11;

/// <summary>2026-11-08, ответ сервера Умара п. 31 и 35: согласия клиента (с null и пропавшими полями), PATCH только изменённого, отказ 403, нет связи,
/// каналы (403/404 — блок скрыт), тихие часы (по умолчанию / из ответа / тело PATCH), журнал сообщений. Настоящий сервер не трогаем — WireMock.</summary>
public sealed class ClientConsentsParseTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Parse_reads_full_answer()
    {
        var c = ClientConsents.Parse(J("{\"consents\":{\"service\":true,\"marketing\":false,\"updated_at\":\"2026-11-08T10:00:00+06:00\",\"source\":\"cashier\"},\"language\":\"ky\"}"));
        c.Service.Should().BeTrue();
        c.Marketing.Should().BeFalse();
        c.Source.Should().Be("cashier");
        c.UpdatedAt.Should().NotBeNull();
        c.Language.Should().Be("ky");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"consents\":null,\"language\":null}")]
    [InlineData("{\"consents\":{\"service\":null,\"marketing\":null,\"updated_at\":null,\"source\":null}}")]
    [InlineData("[]")]
    public void Parse_treats_null_and_missing_as_not_agreed(string json)
    {
        var c = ClientConsents.Parse(J(json));
        c.Service.Should().BeFalse("у прежних клиентов оба согласия выключены");
        c.Marketing.Should().BeFalse();
        c.UpdatedAt.Should().BeNull();
        c.Source.Should().BeNull();
        c.Language.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ignores_unknown_language()
    {
        ClientConsents.Parse(J("{\"language\":\"xx\"}")).Language.Should().BeEmpty();
    }

    [Fact]
    public void BuildPatch_contains_only_changed_fields()
    {
        var before = new ClientConsents(false, false, null, null, "ru");
        ClientConsents.BuildPatch(before, before).Should().BeNull();

        var onlyService = ClientConsents.BuildPatch(before, before with { Service = true })!;
        JsonSerializer.Serialize(onlyService).Should().Be("{\"consents\":{\"service\":true}}");

        var langOnly = ClientConsents.BuildPatch(before, before with { Language = "uz" })!;
        JsonSerializer.Serialize(langOnly).Should().Be("{\"language\":\"uz\"}");

        var both = ClientConsents.BuildPatch(new ClientConsents(true, true, null, null, "ru"), new ClientConsents(true, false, null, null, "ru"))!;
        JsonSerializer.Serialize(both).Should().Be("{\"consents\":{\"marketing\":false}}");
    }
}

public sealed class QuietHoursTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"quiet_hours\":null}")]
    [InlineData("{\"quiet_hours\":{\"from\":\"abc\",\"to\":\"09:00\"}}")]
    public void Missing_or_broken_field_gives_default(string json)
    {
        var q = QuietHours.FromCompany(J(json));
        q.IsDefault.Should().BeTrue();
        q.From.Should().Be("21:00");
        q.To.Should().Be("09:00");
        q.Tz.Should().Be("Asia/Bishkek");
    }

    [Fact]
    public void Value_from_server_is_not_default()
    {
        var q = QuietHours.FromCompany(J("{\"quiet_hours\":{\"from\":\"22:30\",\"to\":\"8:00\",\"tz\":\"Asia/Almaty\"}}"));
        q.IsDefault.Should().BeFalse();
        (q.From, q.To, q.Tz).Should().Be(("22:30", "08:00", "Asia/Almaty"));
    }

    [Theory]
    [InlineData("9:5", "09:05")]
    [InlineData("23:59", "23:59")]
    [InlineData("24:00", null)]
    [InlineData("12:60", null)]
    [InlineData("12", null)]
    [InlineData("", null)]
    public void NormalizeTime(string input, string? expected) => QuietHours.NormalizeTime(input).Should().Be(expected);

    [Fact]
    public void Patch_body_matches_the_server_contract()
    {
        JsonSerializer.Serialize(new QuietHours("21:00", "09:00", "Asia/Bishkek", false).ToPatchBody())
            .Should().Be("{\"quiet_hours\":{\"from\":\"21:00\",\"to\":\"09:00\",\"tz\":\"Asia/Bishkek\"}}");
    }
}

[Collection("ServerFeatures")]
public sealed class ClientMessagingApiTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task Patch_sends_only_the_changed_consent_and_returns_server_state()
    {
        _fake.Json("PATCH", "/api/main/clients/c-1/consents/", 200,
            "{\"consents\":{\"service\":true,\"marketing\":false,\"updated_at\":\"2026-11-08T10:00:00Z\",\"source\":\"cashier\"},\"language\":\"ru\"}");
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));
        var before = new ClientConsents(false, false, null, null, "ru");

        var saved = await api.PatchConsentsAsync("c-1", before, before with { Service = true });

        saved.Service.Should().BeTrue();
        saved.Source.Should().Be("cashier");
        var body = BodyOf(Requests(_fake, "PATCH", "/consents/").Single());
        body.ToJsonString().Should().Be("{\"consents\":{\"service\":true}}");
    }

    [Fact]
    public async Task Patch_without_changes_sends_nothing()
    {
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));
        var c = new ClientConsents(true, false, null, null, "ky");

        (await api.PatchConsentsAsync("c-1", c, c)).Should().Be(c);

        Requests(_fake, "PATCH", "/consents/").Should().BeEmpty();
    }

    [Fact]
    public async Task Get_consents_tolerates_empty_answer()
    {
        _fake.Json("GET", "/api/main/clients/c-2/consents/", 200, "{\"consents\":{\"service\":null},\"language\":\"\"}");
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));

        var c = await api.GetConsentsAsync("c-2");

        c.Service.Should().BeFalse();
        c.Marketing.Should().BeFalse();
    }

    [Fact]
    public async Task Forbidden_patch_is_an_ApiException_403_and_forbidden_channels_hide_the_block()
    {
        _fake.Error("PATCH", "/api/main/clients/c-3/consents/", 403, "no rights");
        _fake.Error("GET", "/api/main/clients/c-3/channels/", 403, "no rights");
        _fake.Error("GET", "/api/main/clients/c-4/channels/", 404, "not found");
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));
        var before = ClientConsents.Empty;

        var act = () => api.PatchConsentsAsync("c-3", before, before with { Marketing = true });
        (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(403);
        (await api.GetChannelsAsync("c-3")).Should().BeNull();
        (await api.GetChannelsAsync("c-4")).Should().BeNull();
    }

    [Fact]
    public async Task Channels_are_parsed_and_empty_list_is_not_hidden()
    {
        _fake.Json("GET", "/api/main/clients/c-5/channels/", 200,
            "[{\"channel\":\"telegram\",\"display_name\":\"Айбек\",\"username\":\"aibek\"},{\"kind\":\"whatsapp\",\"external_id\":\"+996555\"},{}]");
        _fake.Json("GET", "/api/main/clients/c-6/channels/", 200, "{\"results\":[]}");
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));

        var list = (await api.GetChannelsAsync("c-5"))!;
        list.Should().HaveCount(2);
        list[0].KindDisplay.Should().Be("Telegram");
        list[0].Title.Should().Contain("Айбек").And.Contain("@aibek");
        list[1].KindDisplay.Should().Be("WhatsApp");
        (await api.GetChannelsAsync("c-6")).Should().BeEmpty();
    }

    [Fact]
    public async Task Offline_request_fails_without_an_ApiException_status()
    {
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));
        _fake.Dispose(); // сервер пропал — как «нет связи»

        var act = () => api.GetConsentsAsync("c-7");

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        (ex is ApiException { StatusCode: 403 or 404 }).Should().BeFalse("пропажа сети не должна выглядеть как «нет прав» или «нет метода»");
    }

    [Fact]
    public async Task QuietHours_read_from_company_and_patched_to_company()
    {
        _fake.Json("GET", "/api/users/company/", 200, "{\"id\":\"x\",\"name\":\"Shop\"}");
        _fake.Json("PATCH", "/api/users/company/", 200, "{\"id\":\"x\"}");
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));

        (await api.GetQuietHoursAsync()).IsDefault.Should().BeTrue("в ответе нет quiet_hours — значение по умолчанию");
        await api.PatchQuietHoursAsync(new QuietHours("22:00", "08:00", "Asia/Almaty", false));

        BodyOf(Requests(_fake, "PATCH", "/api/users/company/").Single()).ToJsonString()
            .Should().Be("{\"quiet_hours\":{\"from\":\"22:00\",\"to\":\"08:00\",\"tz\":\"Asia/Almaty\"}}");
    }

    [Fact]
    public async Task Outbox_sends_only_filled_filters_and_parses_rows_tolerantly()
    {
        _fake.Json("GET", "/api/main/telegram-bot/outbox/", 200,
            "{\"count\":2,\"next\":\"http://x/?page=2\",\"results\":[" +
            "{\"created_at\":\"2026-11-08T21:30:00+06:00\",\"client\":{\"full_name\":\"Айбек\"},\"kind\":\"debt\",\"status\":\"deferred\",\"text\":\"Долг 500\",\"send_after\":\"2026-11-09T09:00:00+06:00\",\"new_field\":1}," +
            "{\"status\":\"skipped\",\"reason\":\"no_consent\"}]}");
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));

        var page = await api.GetOutboxAsync("  ", "deferred", null, 2);

        page.Rows.Should().HaveCount(2);
        page.HasNext.Should().BeTrue();
        page.Count.Should().Be(2);
        page.Rows[0].ClientName.Should().Be("Айбек");
        page.Rows[0].SendAfter.Should().NotBeNull();
        page.Rows[1].Reason.Should().Be("no_consent");
        page.Rows[1].Text.Should().BeEmpty();
        var url = Requests(_fake, "GET", "/outbox/").Single().RequestMessage.Url;
        url.Should().Contain("status=deferred").And.Contain("page=2").And.NotContain("client=").And.NotContain("kind=");
    }

    [Fact]
    public async Task Outbox_empty_list_and_forbidden()
    {
        _fake.Json("GET", "/api/main/telegram-bot/outbox/", 200, "[]");
        var api = new ClientMessagingApi(await LoggedInAsync(_fake));
        var page = await api.GetOutboxAsync(null, null, null);
        page.Rows.Should().BeEmpty();
        page.HasNext.Should().BeFalse();

        _fake.Error("GET", "/api/main/telegram-bot/outbox/", 403, "owner only");
        var act = () => api.GetOutboxAsync(null, null, null);
        (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(403);
    }
}
