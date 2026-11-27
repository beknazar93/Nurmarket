using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-26 (Д1, отчёт «Проверка сфер», autoparts): сервер на POST pos/sales/{id}/return/ отвечает 403 {"code":"pin_required"} — возврат нужно подтвердить PIN владельца/админа.
/// Проверяется: разбор кода и текст на 5 языках (не «Нет права»), запрос с X-Confirm-Token и тем же Idempotency-Key, повтор после PIN (ReturnPinRetry), отмена PIN, второй отказ, токен не в журнале.
/// Сервер — WireMock; окно PIN (AskReturnPinTokenAsync) покрыто UI-тестом ReturnPinPromptUiTests.</summary>
[Collection("ServerFeatures")]
public sealed class ReturnPinRequiredTests : IDisposable
{
    private const string Secret = "tok-SECRET-return-1126";
    private const string PinRequiredBody = "{\"detail\":\"Нужно подтвердить действие PIN-кодом.\",\"code\":\"pin_required\"}";
    private readonly FakeNurCrmServer _fake = new();
    private readonly List<string> _log = new();
    private readonly Action<string, string>? _oldMirror = PosLogger.Mirror;

    public ReturnPinRequiredTests() => PosLogger.Mirror = (cat, msg) => { lock (_log) _log.Add(cat + " " + msg); };

    public void Dispose()
    {
        PosLogger.Mirror = _oldMirror;
        _fake.Dispose();
    }

    private static readonly PosRefundLineRequest[] OneLine =
        [new PosRefundLineRequest { LineId = "line-1", Title = "Фильтр", Quantity = 1, OriginalQuantity = 2 }];

    private static ApiException PinRequired(int status = 403) => new("Нужно подтвердить действие PIN-кодом.", status, JsonDocument.Parse(PinRequiredBody).RootElement.Clone());

    /// <summary>Без X-Confirm-Token — 403 pin_required, с токеном — 200 (как на компании с правилом «возврат по PIN»).</summary>
    private void StubPinRule(string path = "/api/main/pos/sales/*/return/")
    {
        // приоритет у всех один (0, как у заглушки возврата по умолчанию): при равенстве выигрывает более поздняя — сначала общий 403, затем 200 для запроса с токеном
        _fake.Json("POST", path, 403, PinRequiredBody);
        _fake.Server.Given(Request.Create().WithPath(path).UsingMethod("POST").WithHeader("X-Confirm-Token", Secret))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"ret-1\",\"refund\":{\"amount\":\"6400.00\",\"cash\":\"6400.00\"}}"));
    }

    [Theory]
    [InlineData(403, "{\"code\":\"pin_required\"}", true)]
    [InlineData(403, "{\"code\":\"PIN_REQUIRED\",\"detail\":\"x\"}", true)]
    [InlineData(403, "{\"error\":\"pin_required\"}", true)]
    [InlineData(403, "{\"code\":\"permission_denied\"}", false)]
    [InlineData(403, "{\"detail\":\"Нужно подтвердить действие PIN-кодом.\"}", false)]
    [InlineData(400, "{\"code\":\"pin_required\"}", false)]
    [InlineData(403, "{\"code\":\"pin_invalid\"}", false)]
    public void IsPinRequired_OnlyFor403WithThatCode(int status, string body, bool expected)
    {
        var ex = new ApiException("raw", status, JsonDocument.Parse(body).RootElement.Clone());
        ServerErrorCodes.IsPinRequired(ex).Should().Be(expected);
    }

    [Fact]
    public void IsPinRequired_NullAndForeignExceptions_AreFalse()
    {
        ServerErrorCodes.IsPinRequired(null).Should().BeFalse();
        ServerErrorCodes.IsPinRequired(new InvalidOperationException("x")).Should().BeFalse();
        ServerErrorCodes.IsPinRequired(new ApiException("raw", 403)).Should().BeFalse("без тела кода нет");
    }

    [Fact]
    public async Task Confirmed_Return_SendsConfirmTokenHeader_SameKey_AndFullBody()
    {
        StubPinRule();
        var sales = new SalesApiService(await LoggedInAsync(_fake));

        var r = await sales.PosReturnConfirmedAsync("sale-pin", OneLine, " Передумал ", true, "key-1126", Secret);

        r.GetProperty("id").GetString().Should().Be("ret-1");
        var req = Requests(_fake, "POST", "/api/main/pos/sales/sale-pin/return/").Single();
        Header(req, "X-Confirm-Token").Should().Be(Secret);
        var body = BodyOf(req);
        body["idempotency_key"]!.GetValue<string>().Should().Be("key-1126", "в теле тот же ключ, что у первой отправки");
        body["cashbox_role"]!.GetValue<string>().Should().Be("pos_main");
        body["reason"]!.GetValue<string>().Should().Be("Передумал");
        body["is_defect"]!.GetValue<bool>().Should().BeTrue();
        body["items"]!.AsArray().Single()!["sale_item_id"]!.GetValue<string>().Should().Be("line-1");
    }

    [Fact]
    public async Task Confirmed_WholeReceipt_HasNoItems_NoReasonWhenEmpty()
    {
        StubPinRule();
        var sales = new SalesApiService(await LoggedInAsync(_fake));

        await sales.PosReturnConfirmedAsync("sale-whole", null, "  ", false, "key-w", Secret);

        var body = BodyOf(Requests(_fake, "POST", "/api/main/pos/sales/sale-whole/return/").Single());
        body.Select(kv => kv.Key).Should().BeEquivalentTo(["idempotency_key", "cashbox_role"], "тело — ровно как у сайта: два поля");
    }

    [Fact]
    public async Task Confirmed_Return_RequiresKeyAndToken()
    {
        var sales = new SalesApiService(await LoggedInAsync(_fake));

        await sales.Invoking(s => s.PosReturnConfirmedAsync("s", OneLine, null, false, "", Secret)).Should().ThrowAsync<ArgumentException>();
        await sales.Invoking(s => s.PosReturnConfirmedAsync("s", OneLine, null, false, "k", " ")).Should().ThrowAsync<ArgumentException>();
        Requests(_fake, "POST", "/return/").Should().BeEmpty("без ключа или токена запрос не уходит");
    }

    [Fact]
    public async Task Retry_FirstAttemptPinRequired_AsksPin_ThenSendsWithTokenAndSameKey()
    {
        StubPinRule();
        var sales = new SalesApiService(await LoggedInAsync(_fake));
        var asked = 0;
        const string key = "key-retry";

        var result = await ReturnPinRetry.RunAsync(
            token => token is null
                ? sales.PosReturnSaleAsync("sale-r", OneLine, default, key)
                : sales.PosReturnConfirmedAsync("sale-r", OneLine, null, false, key, token),
            () => { asked++; return Task.FromResult<string?>(Secret); });

        result.GetProperty("id").GetString().Should().Be("ret-1");
        asked.Should().Be(1, "PIN спрашивается один раз");
        var posts = Requests(_fake, "POST", "/api/main/pos/sales/sale-r/return/");
        posts.Should().HaveCount(2);
        Header(posts[0], "X-Confirm-Token").Should().BeNull("первая отправка — без токена");
        Header(posts[1], "X-Confirm-Token").Should().Be(Secret);
        posts.Select(p => BodyOf(p)["idempotency_key"]!.GetValue<string>()).Should().Equal(key, key);
        lock (_log)
            _log.Should().NotContain(l => l.Contains(Secret), "токен подтверждения нигде не пишется");
    }

    [Fact]
    public async Task Retry_WithoutPinRequired_NeverAsksPin()
    {
        _fake.Json("POST", "/api/main/pos/sales/*/return/", 200, "{\"id\":\"ret-2\"}");
        var sales = new SalesApiService(await LoggedInAsync(_fake));
        var asked = 0;

        await ReturnPinRetry.RunAsync(token => sales.PosReturnSaleAsync("sale-n", OneLine, default, "k"), () => { asked++; return Task.FromResult<string?>(Secret); });

        asked.Should().Be(0, "другим компаниям PIN не нужен — поведение прежнее");
        Requests(_fake, "POST", "/return/").Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Retry_PinCancelled_RethrowsOriginalPinRequired_NoSecondRequest(string? token)
    {
        StubPinRule();
        var sales = new SalesApiService(await LoggedInAsync(_fake));

        var act = async () => await ReturnPinRetry.RunAsync(t => sales.PosReturnSaleAsync("sale-c", OneLine, default, "k"), () => Task.FromResult(token));

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ServerErrorCodes.IsPinRequired(ex).Should().BeTrue();
        ServerOutageMonitor.FriendlyError(ex).Should().Contain("PIN");
        Requests(_fake, "POST", "/return/").Should().ContainSingle("после отмены PIN запрос не повторяется");
    }

    [Fact]
    public async Task Retry_ServerRejectsTokenToo_ErrorGoesOut_PinAskedOnlyOnce()
    {
        // сервер на любой запрос отвечает pin_required (токен не принят): повторной просьбы PIN нет — иначе цикл
        _fake.Json("POST", "/api/main/pos/sales/*/return/", 403, PinRequiredBody);
        var sales = new SalesApiService(await LoggedInAsync(_fake));
        var asked = 0;

        var act = async () => await ReturnPinRetry.RunAsync(
            token => token is null ? sales.PosReturnSaleAsync("sale-x", OneLine, default, "k") : sales.PosReturnConfirmedAsync("sale-x", OneLine, null, false, "k", token),
            () => { asked++; return Task.FromResult<string?>("bad-token"); });

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ServerErrorCodes.IsPinRequired(ex).Should().BeTrue();
        asked.Should().Be(1);
        Requests(_fake, "POST", "/return/").Should().HaveCount(2);
    }

    [Fact]
    public async Task Retry_OtherServerError_PassesThroughUntouched()
    {
        _fake.Json("POST", "/api/main/pos/sales/*/return/", 409, "{\"code\":\"refund_exceeds_paid\"}");
        var sales = new SalesApiService(await LoggedInAsync(_fake));
        var asked = 0;

        var act = async () => await ReturnPinRetry.RunAsync(t => sales.PosReturnSaleAsync("sale-e", OneLine, default, "k"), () => { asked++; return Task.FromResult<string?>(Secret); });

        (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(409);
        asked.Should().Be(0);
    }
}

/// <summary>Текст при pin_required (отмена PIN / повторный отказ) — на 5 языках; язык кассы общий, поэтому отдельная коллекция и возврат значения.</summary>
[Collection("UserPreferences")]
public sealed class ReturnPinRequiredTextTests : IDisposable
{
    private readonly AppLanguage _language = UserPreferences.Instance.Language;

    public void Dispose() => UserPreferences.Instance.Language = _language;

    private static ApiException PinRequired() => new("Нужно подтвердить действие PIN-кодом.", 403,
        JsonDocument.Parse("{\"detail\":\"Нужно подтвердить действие PIN-кодом.\",\"code\":\"pin_required\"}").RootElement.Clone());

    [Fact]
    public void PinRequired_Text_IsExplanationOnAllFiveLanguages_NotGenericNoRight()
    {
        var seen = new HashSet<string>();
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UserPreferences.Instance.Language = lang;
            var text = ServerOutageMonitor.FriendlyError(PinRequired());
            text.Should().Contain("PIN").And.NotContain("Нет права на это действие").And.NotContain("pin_required");
            seen.Add(text).Should().BeTrue("текст для " + lang + " должен отличаться");
        }
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ServerOutageMonitor.FriendlyError(PinRequired()).Should().Contain("владельца или администратора");
        ServerOutageMonitor.IsServerUnavailable(PinRequired()).Should().BeFalse("403 — отказ, а не «сервер недоступен»");
    }
}
