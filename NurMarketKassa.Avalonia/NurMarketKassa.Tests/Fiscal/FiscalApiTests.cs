using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Fiscal;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Fiscal;

/// <summary>2026-11-14 (П0-1): клиент сервера для записи фискальных реквизитов (200 replayed, 409 fiscal_already_set / sale_not_completed / return_voided / fiscal_mode_server /
/// fiscal_locked, 400 validation_error), настройки «Налоги и ККМ» и отчёт «Продажи без фискального чека». Только поддельный сервер WireMock — настоящий сервер Умара и eKassa не трогаются.
/// Запускать только tools/qa/test-safe.ps1.</summary>
[Collection(nameof(FiscalStateCollection))]
public sealed class FiscalApiTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-fiscal-api-" + Guid.NewGuid().ToString("N")[..8]);

    public FiscalApiTests()
    {
        Directory.CreateDirectory(_dir);
        FiscalSettings.DirOverride = _dir;
        FiscalSettings.AccountOverride = "acc-api|company-api";
        FiscalSettings.ResetMemoryForTests();
        ServerOutageMonitor.ResetForTests();
    }

    public void Dispose()
    {
        _server.Dispose();
        FiscalSettings.DirOverride = null;
        FiscalSettings.AccountOverride = null;
        FiscalSettings.ResetMemoryForTests();
        ServerOutageMonitor.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private async Task<NurMarketApiClient> LoginAsync()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return api;
    }

    private static FiscalRequisites Accepted() => new(FiscalStatus.Accepted, "FD-100", "FM-7", "RN-55", "SIGN-1", "https://ofd.example/q/1", new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(6)), "KS-3");

    private string LastBody(string method = "POST") => _server.Server.LogEntries.Last(e => e.RequestMessage.Method == method && e.RequestMessage.Path.Contains("fiscal")).RequestMessage.Body ?? "";

    [Fact]
    public async Task Accepted_requisites_are_posted_with_all_fields_and_iso_time()
    {
        _server.Json("POST", "/api/main/pos/sales/s-1/fiscal/", 200, "{\"id\":\"s-1\",\"fiscal_status\":\"accepted\"}");
        var api = await LoginAsync();

        var result = await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-1", Accepted());

        result.Outcome.Should().Be(FiscalWriteOutcome.Ok);
        result.IsDone.Should().BeTrue();
        using var body = JsonDocument.Parse(LastBody());
        var root = body.RootElement;
        root.GetProperty("fiscal_status").GetString().Should().Be("accepted");
        root.GetProperty("fiscal_doc_number").GetString().Should().Be("FD-100");
        root.GetProperty("fiscal_memory_number").GetString().Should().Be("FM-7");
        root.GetProperty("kkm_reg_number").GetString().Should().Be("RN-55");
        root.GetProperty("fiscal_sign").GetString().Should().Be("SIGN-1");
        root.GetProperty("fiscal_qr_url").GetString().Should().Be("https://ofd.example/q/1");
        root.GetProperty("kkm_shift_id").GetString().Should().Be("KS-3");
        root.GetProperty("fiscal_at").GetString().Should().StartWith("2026-11-14T10:00:00");
        root.TryGetProperty("fiscal_error", out _).Should().BeFalse("пустые поля не шлём");
    }

    [Fact]
    public async Task Return_requisites_go_to_returns_endpoint()
    {
        _server.Json("POST", "/api/main/pos/returns/r-9/fiscal/", 200, "{\"id\":\"r-9\"}");
        var api = await LoginAsync();
        var result = await FiscalApi.WriteAsync(api, FiscalDocKind.Return, "r-9", Accepted());
        result.IsDone.Should().BeTrue();
        _server.Server.LogEntries.Should().Contain(e => e.RequestMessage.Path == "/api/main/pos/returns/r-9/fiscal/");
    }

    [Fact]
    public async Task Replayed_200_is_done_and_marked_replayed()
    {
        _server.Json("POST", "/api/main/pos/sales/s-1/fiscal/", 200, "{\"replayed\":true}");
        var api = await LoginAsync();
        var result = await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-1", Accepted());
        result.Outcome.Should().Be(FiscalWriteOutcome.Replayed);
        result.IsDone.Should().BeTrue();
    }

    [Fact]
    public async Task Failed_and_pending_are_posted_with_error_text_trimmed_to_limit()
    {
        _server.Json("POST", "/api/main/pos/sales/s-2/fiscal/", 200, "{}");
        var api = await LoginAsync();
        var failed = new FiscalRequisites(FiscalStatus.Failed, Error: new string('x', 900));
        (await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-2", failed)).IsDone.Should().BeTrue();
        using var body = JsonDocument.Parse(LastBody());
        body.RootElement.GetProperty("fiscal_status").GetString().Should().Be("failed");
        body.RootElement.GetProperty("fiscal_error").GetString()!.Length.Should().Be(FiscalRequisites.MaxErrorLength);

        (await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-2", new FiscalRequisites(FiscalStatus.Pending))).IsDone.Should().BeTrue();
        using var pending = JsonDocument.Parse(LastBody());
        pending.RootElement.GetProperty("fiscal_status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Accepted_without_doc_number_is_rejected_before_any_request()
    {
        var api = await LoginAsync();
        _server.Server.ResetLogEntries();
        var result = await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-1", new FiscalRequisites(FiscalStatus.Accepted));
        result.Outcome.Should().Be(FiscalWriteOutcome.ValidationError);
        _server.Server.LogEntries.Should().BeEmpty("сервер ответил бы 400 — лишний запрос не шлём");
        (await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-1", FiscalRequisites.None)).Outcome.Should().Be(FiscalWriteOutcome.ValidationError);
    }

    [Fact]
    public async Task Conflict_already_set_carries_current_requisites()
    {
        _server.Json("POST", "/api/main/pos/sales/s-3/fiscal/", 409,
            "{\"code\":\"fiscal_already_set\",\"detail\":\"x\",\"current\":{\"fiscal_status\":\"accepted\",\"fiscal_doc_number\":\"FD-OTHER\",\"fiscal_memory_number\":\"FM-1\"}}");
        var api = await LoginAsync();
        var result = await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-3", Accepted());
        result.Outcome.Should().Be(FiscalWriteOutcome.AlreadySet);
        result.IsDone.Should().BeFalse();
        result.RetryLater.Should().BeFalse();
        result.Current!.DocNumber.Should().Be("FD-OTHER");
        result.Message.Should().Be(FiscalErrors.AlreadySetText);
    }

    [Theory]
    [InlineData("sale_not_completed", FiscalWriteOutcome.SaleNotCompleted, true)]
    [InlineData("return_voided", FiscalWriteOutcome.ReturnVoided, false)]
    [InlineData("fiscal_mode_server", FiscalWriteOutcome.ModeServer, false)]
    [InlineData("fiscal_locked", FiscalWriteOutcome.Locked, false)]
    [InlineData("something_new", FiscalWriteOutcome.Rejected, false)]
    public async Task Conflict_codes_map_to_outcomes(string code, FiscalWriteOutcome expected, bool retryLater)
    {
        _server.Json("POST", "/api/main/pos/sales/s-4/fiscal/", 409, "{\"code\":\"" + code + "\",\"detail\":\"server text\"}");
        var api = await LoginAsync();
        var result = await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-4", Accepted());
        result.Outcome.Should().Be(expected);
        result.RetryLater.Should().Be(retryLater);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Validation_error_400_lists_fields()
    {
        _server.Json("POST", "/api/main/pos/sales/s-5/fiscal/", 400, "{\"code\":\"validation_error\",\"detail\":\"bad\",\"fields\":{\"fiscal_doc_number\":[\"required\"],\"fiscal_sign\":\"too long\"}}");
        var api = await LoginAsync();
        var result = await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-5", Accepted());
        result.Outcome.Should().Be(FiscalWriteOutcome.ValidationError);
        result.Fields.Should().Contain("fiscal_doc_number: required").And.Contain("fiscal_sign: too long");
        result.Message.Should().Contain("fiscal_doc_number");
    }

    [Fact]
    public async Task Not_found_and_server_unavailable_are_distinguished()
    {
        _server.Json("POST", "/api/main/pos/sales/nope/fiscal/", 404, "{\"detail\":\"not found\"}");
        _server.Json("POST", "/api/main/pos/sales/s-6/fiscal/", 503, "{\"detail\":\"unavailable\"}");
        var api = await LoginAsync();
        (await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "nope", Accepted())).Outcome.Should().Be(FiscalWriteOutcome.NotFound);
        var throttled = await FiscalApi.WriteAsync(api, FiscalDocKind.Sale, "s-6", Accepted());
        throttled.Outcome.Should().Be(FiscalWriteOutcome.Unavailable);
        throttled.RetryLater.Should().BeTrue("503 — повторить позже");
    }

    [Fact]
    public async Task Locked_409_from_edit_and_delete_gets_a_friendly_message_in_every_place()
    {
        _server.Json("PATCH", "/api/main/pos/sales/s-7/", 409, "{\"code\":\"fiscal_locked\",\"detail\":\"locked\"}");
        _server.Json("DELETE", "/api/main/pos/sales/s-7/", 409, "{\"code\":\"fiscal_locked\"}");
        _server.Json("PATCH", "/api/main/pos/returns/r-7/", 409, "{\"code\":\"fiscal_locked\"}");
        _server.Json("POST", "/api/main/pos/sales/bulk-delete/", 409, "{\"detail\":\"locked\",\"locked_ids\":[\"a\",\"b\"]}");
        var api = await LoginAsync();

        var patch = await Assert.ThrowsAsync<ApiException>(() => api.RequestAsync(HttpMethod.Patch, "api/main/pos/sales/s-7/", new { comment = "x" }, null));
        patch.StatusCode.Should().Be(409);
        patch.Message.Should().Be(FiscalErrors.LockedSaleText);

        var delete = await Assert.ThrowsAsync<ApiException>(() => api.RequestAsync(HttpMethod.Delete, "api/main/pos/sales/s-7/", null, null));
        delete.Message.Should().Be(FiscalErrors.LockedSaleText);

        var ret = await Assert.ThrowsAsync<ApiException>(() => api.RequestAsync(HttpMethod.Patch, "api/main/pos/returns/r-7/", new { x = 1 }, null));
        ret.Message.Should().Be(FiscalErrors.LockedReturnText, "для возврата «оформите возврат» не подходит");

        var bulk = await Assert.ThrowsAsync<ApiException>(() => api.RequestAsync(HttpMethod.Post, "api/main/pos/sales/bulk-delete/", new { ids = new[] { "a" } }, null));
        bulk.Message.Should().Be(FiscalErrors.LockedBulkText);
    }

    [Fact]
    public void Locked_texts_exist_in_all_five_languages_and_say_what_to_do()
    {
        // Tr.T выбирает язык по настройке; здесь проверяем, что тексты непустые и сами себя объясняют (ru — язык по умолчанию тестов)
        FiscalErrors.LockedSaleText.Should().NotBeNullOrWhiteSpace();
        FiscalErrors.LockedReturnText.Should().NotBeNullOrWhiteSpace();
        FiscalErrors.LockedBulkText.Should().NotBeNullOrWhiteSpace();
        FiscalErrors.TryFriendly(409, JsonDocument.Parse("{\"code\":\"fiscal_locked\"}").RootElement, out var text).Should().BeTrue();
        text.Should().Be(FiscalErrors.LockedSaleText);
        FiscalErrors.TryFriendly(400, JsonDocument.Parse("{\"code\":\"fiscal_locked\"}").RootElement, out _).Should().BeFalse("только 409");
        FiscalErrors.TryFriendly(409, null, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Other_409_errors_keep_the_server_message()
    {
        _server.Json("PATCH", "/api/main/pos/sales/s-8/", 409, "{\"code\":\"sale_state_conflict\",\"detail\":\"Версия устарела\"}");
        var api = await LoginAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.RequestAsync(HttpMethod.Patch, "api/main/pos/sales/s-8/", new { x = 1 }, null));
        ex.Message.Should().Be("Версия устарела");
    }

    // ───────────── настройки «Налоги и ККМ» ─────────────

    [Fact]
    public async Task Settings_get_parses_block_and_put_sends_only_form_fields()
    {
        _server.Json("GET", "/api/main/settings/fiscal/", 200, "{\"mode\":\"kassa\",\"fiscal_enabled\":true,\"tax_regime\":\"simple\",\"vat_payer\":false,\"vat_rates\":[\"0.00\",\"12.00\"],\"nsp_rate\":\"2.00\"}");
        _server.Json("PUT", "/api/main/settings/fiscal/", 200, "{\"mode\":\"kassa\",\"fiscal_enabled\":false,\"tax_regime\":\"general\",\"vat_payer\":true,\"vat_rates\":[\"12.00\"],\"nsp_rate\":null}");
        var api = await LoginAsync();

        var got = await FiscalApi.GetSettingsAsync(api);
        got.Mode.Should().Be(FiscalMode.Kassa);
        got.Rates.Should().Equal(0m, 12m);

        var saved = await FiscalApi.PutSettingsAsync(api, new FiscalSettingsForm(false, " general ", true, new[] { 12m }, null));
        saved.TaxRegime.Should().Be("general");
        using var body = JsonDocument.Parse(LastBody("PUT"));
        var root = body.RootElement;
        root.TryGetProperty("mode", out _).Should().BeFalse("режим определяет сервер, форма его не шлёт");
        root.GetProperty("fiscal_enabled").GetBoolean().Should().BeFalse();
        root.GetProperty("tax_regime").GetString().Should().Be("general");
        root.GetProperty("vat_payer").GetBoolean().Should().BeTrue();
        root.GetProperty("vat_rates")[0].GetString().Should().Be("12.00");
        root.GetProperty("nsp_rate").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Settings_put_validates_form_without_a_request()
    {
        var api = await LoginAsync();
        _server.Server.ResetLogEntries();
        var bad = new FiscalSettingsForm(true, new string('a', 33), false, Array.Empty<decimal>(), null);
        FiscalApi.ValidateForm(bad).Should().NotBeNull();
        await Assert.ThrowsAsync<ApiException>(() => FiscalApi.PutSettingsAsync(api, bad));
        FiscalApi.ValidateForm(new FiscalSettingsForm(true, "ok", true, new[] { 101m }, null)).Should().NotBeNull();
        FiscalApi.ValidateForm(new FiscalSettingsForm(true, "ok", true, new[] { 12m }, -1m)).Should().NotBeNull();
        FiscalApi.ValidateForm(new FiscalSettingsForm(true, "ok", true, new[] { 12m }, 2m)).Should().BeNull();
        _server.Server.LogEntries.Where(e => e.RequestMessage.Method == "PUT").Should().BeEmpty();
    }

    [Fact]
    public async Task Settings_put_forbidden_403_is_an_api_exception()
    {
        _server.Json("PUT", "/api/main/settings/fiscal/", 403, "{\"detail\":\"Только владелец\"}");
        var api = await LoginAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => FiscalApi.PutSettingsAsync(api, new FiscalSettingsForm(true, "x", true, new[] { 12m }, null)));
        ex.StatusCode.Should().Be(403);
    }

    // ───────────── отчёт «Продажи без фискального чека» ─────────────

    [Fact]
    public void Report_period_is_limited_to_92_days()
    {
        FiscalApi.ValidateReportPeriod(new DateTime(2026, 8, 14), new DateTime(2026, 11, 13)).Should().BeNull("92 дня включительно — можно");
        FiscalApi.ValidateReportPeriod(new DateTime(2026, 8, 13), new DateTime(2026, 11, 13)).Should().NotBeNull("93 дня — нельзя");
        FiscalApi.ValidateReportPeriod(new DateTime(2026, 11, 14), new DateTime(2026, 11, 1)).Should().NotBeNull("«с» позже «по»");
        FiscalApi.ValidateReportPeriod(new DateTime(2026, 11, 14), new DateTime(2026, 11, 14)).Should().BeNull();
    }

    [Fact]
    public async Task Report_request_has_from_to_page_and_page_size_and_rows_are_parsed()
    {
        _server.Json("GET", "/api/main/reports/sales-without-fiscal/", 200,
            "{\"count\":3,\"next\":\"https://x/?page=2\",\"results\":[" +
            "{\"id\":\"s1\",\"number\":1001,\"created_at\":\"2026-11-10T08:30:00Z\",\"total\":\"250.50\",\"cashier_name\":\"Айгерим\",\"payment_method\":\"cash\",\"fiscal_status\":\"none\"}," +
            "{\"id\":\"s2\",\"number\":\"1002\",\"created_at\":\"2026-11-10T09:30:00Z\",\"total\":100,\"cashier\":\"u2\",\"fiscal_status\":\"failed\",\"fiscal_error\":\"нет связи с ККМ\"}]}");
        var api = await LoginAsync();

        var page = await FiscalApi.GetSalesWithoutFiscalAsync(api, new DateTime(2026, 11, 1), new DateTime(2026, 11, 14), 1, 50);

        page.Count.Should().Be(3);
        page.HasNext.Should().BeTrue();
        page.Rows.Should().HaveCount(2);
        page.Rows[0].Number.Should().Be("1001");
        page.Rows[0].Total.Should().Be(250.50m);
        page.Rows[0].Cashier.Should().Be("Айгерим");
        page.Rows[0].Status.Should().Be(FiscalStatus.None);
        page.Rows[1].Status.Should().Be(FiscalStatus.Failed);
        page.Rows[1].Error.Should().Be("нет связи с ККМ");
        page.Rows[1].Total.Should().Be(100m);
        var query = _server.Server.LogEntries.Last(e => e.RequestMessage.Path.Contains("sales-without-fiscal")).RequestMessage.Query!;
        query["from"].ToString().Should().Contain("2026-11-01");
        query["to"].ToString().Should().Contain("2026-11-14");
        query["page"].ToString().Should().Contain("1");
        query["page_size"].ToString().Should().Contain("50");
    }

    [Fact]
    public async Task Report_over_92_days_is_not_sent_and_server_400_is_surfaced()
    {
        var api = await LoginAsync();
        _server.Server.ResetLogEntries();
        await Assert.ThrowsAsync<ApiException>(() => FiscalApi.GetSalesWithoutFiscalAsync(api, new DateTime(2026, 1, 1), new DateTime(2026, 11, 14), 1, 50));
        _server.Server.LogEntries.Should().BeEmpty();

        _server.Json("GET", "/api/main/reports/sales-without-fiscal/", 400, "{\"code\":\"validation_error\",\"detail\":\"period too long\"}");
        var ex = await Assert.ThrowsAsync<ApiException>(() => FiscalApi.GetSalesWithoutFiscalAsync(api, new DateTime(2026, 11, 1), new DateTime(2026, 11, 14), 1, 50));
        ex.StatusCode.Should().Be(400);
    }

    [Fact]
    public void Report_plain_array_and_empty_answers_are_parsed()
    {
        FiscalApi.ParseReport(JsonDocument.Parse("[{\"id\":\"a\",\"number\":1}]").RootElement, 1, 50).Rows.Should().HaveCount(1);
        var empty = FiscalApi.ParseReport(JsonDocument.Parse("{\"count\":0,\"next\":null,\"results\":[]}").RootElement, 1, 50);
        empty.Rows.Should().BeEmpty();
        empty.HasNext.Should().BeFalse();
    }
}
