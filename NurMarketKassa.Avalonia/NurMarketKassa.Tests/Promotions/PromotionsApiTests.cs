using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Promotions;

/// <summary>2026-11-27 (ответ Умара «Конструктор акций Ф1», раздел «Акции» программы владельца): API акций на заглушке WireMock — список, создание с Idempotency-Key (повтор тем же ключом,
/// replayed), PATCH без вида у запущенной, статусы, preview, отчёт; ошибки promotion_invalid{field} → шаг мастера, 409 и 403 → понятный текст; описание акций словами и пример расчёта.
/// Форма ответов — как у NBS 27.11. Запускать только через tools/qa/test-safe.ps1.</summary>
[Collection("UserPreferences")]
public sealed class PromotionsApiTests : IDisposable
{
    private const string PromoJson = """
        {"id":"p-1","title":"2+1 вода","status":"active","kind":"buy_n_get_m","params":{"per":"unit","buy":2,"get":1,"cheapest":true,"get_discount_percent":100},
         "scope":{"categories":["cat-1"],"exclude_products":["x-1"]},"starts_at":"2026-11-26T18:00:00+00:00","ends_at":"2026-11-28T17:59:00+00:00",
         "weekdays":[5,1],"time_from":"22:00","time_to":"02:00","audience":"all","levels":[],"priority":30,"stackable_with_manual":false,"stackable_with_promos":true,
         "allow_below_cost":false,"branches":[],"limit_per_receipt":null,"version":3,"description":"","limit_total":null,"limit_per_client":null,"used_total":5,
         "created_at":"2026-11-26T18:32:53.814445+00:00","updated_at":"2026-11-26T18:32:53.814452+00:00"}
        """;

    private readonly FakeNurCrmServer _server = new();
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public PromotionsApiTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        _server.Dispose();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private async Task<PromotionsApi> MakeAsync()
    {
        _server.StubLogin();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("owner@test.kg", "12345678");
        return new PromotionsApi(api);
    }

    private IEnumerable<WireMock.Logging.ILogEntry> Hits(string path, string method) =>
        _server.Server.LogEntries.Where(e => e.RequestMessage.Path == path && e.RequestMessage.Method == method);

    // ── Разбор ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Promotion_AllFields_UtcDatesToBishkek()
    {
        var p = PromotionsApi.Parse(J(PromoJson));
        p.Id.Should().Be("p-1");
        p.Kind.Should().Be(Promotion.KindBuyNGetM);
        p.Buy.Should().Be(2);
        p.Get.Should().Be(1);
        p.GetDiscountPercent.Should().Be(100m);
        p.Categories.Should().Equal("cat-1");
        p.ExcludeProducts.Should().Equal("x-1");
        p.ScopeAll.Should().BeFalse();
        p.Weekdays.Should().Equal(1, 5);
        p.TimeFrom.Should().Be("22:00");
        p.Priority.Should().Be(30);
        p.StackableWithPromos.Should().BeTrue();
        p.LimitPerReceipt.Should().BeNull();
        p.Version.Should().Be(3);
        p.UsedTotal.Should().Be(5);
        PromotionsApi.Local(p.StartsAt!.Value).Should().Be(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.FromHours(6)), "18:00 UTC = полночь по Бишкеку");
        p.KindLocked.Should().BeTrue("акция запускалась — вид не меняется");
    }

    [Fact]
    public void Parse_Params_MoneyAsStringsAndNumbers()
    {
        PromotionsApi.Parse(J("""{"id":"a","kind":"amount","params":{"amount":"20.00","per":"line"}}""")).Amount.Should().Be(20m);
        PromotionsApi.Parse(J("""{"id":"a","kind":"amount","params":{"amount":"20.00","per":"line"}}""")).Per.Should().Be("line");
        PromotionsApi.Parse(J("""{"id":"a","kind":"fixed_price","params":{"price":79}}""")).Price.Should().Be(79m);
        var pc = PromotionsApi.Parse(J("""{"id":"a","kind":"percent","params":{"percent":15,"min_amount":"300.00","max_qty":10},"scope":{"all":true}}"""));
        pc.Percent.Should().Be(15m);
        pc.MinAmount.Should().Be(300m);
        pc.MaxQty.Should().Be(10m);
        pc.ScopeAll.Should().BeTrue();
    }

    [Fact]
    public void ToBody_ByKind_AndScope_AndKindOmittedWhenLocked()
    {
        var p = new Promotion { Title = " Хлеб за 79 ", Kind = Promotion.KindFixedPrice, Price = 79m, Products = { "pr-1" }, Weekdays = { 7, 1, 1 },
            StartsAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(6)), TimeFrom = "22:00", TimeTo = "02:00", LimitPerReceipt = 0 };
        var body = JsonSerializer.Serialize(p.ToBody(status: "active"), Relaxed);
        body.Should().Contain("\"title\":\"Хлеб за 79\"").And.Contain("\"kind\":\"fixed_price\"").And.Contain("\"price\":\"79.00\"")
            .And.Contain("\"products\":[\"pr-1\"]").And.Contain("\"weekdays\":[1,7]").And.Contain("\"starts_at\":\"2026-12-01T00:00:00+06:00\"")
            .And.Contain("\"ends_at\":null").And.Contain("\"status\":\"active\"").And.Contain("\"limit_per_receipt\":null");
        body.Should().NotContain("categories", "пустые списки scope не отправляются");
        JsonSerializer.Serialize(new Promotion { Kind = Promotion.KindAmount, Amount = 20m, Per = "unit" }.ParamsBody()).Should().Be("{\"amount\":\"20.00\",\"per\":\"unit\"}");
        JsonSerializer.Serialize(new Promotion { ScopeAll = true, ExcludeProducts = { "x" } }.ScopeBody()).Should().Be("{\"all\":true,\"exclude_products\":[\"x\"]}");
        new Promotion { Id = "z", Status = "active" }.ToBody(includeKind: false).Should().NotContainKey("kind");
    }

    [Fact]
    public void Preview_And_Report_ParseTolerantly()
    {
        var r = PromotionsApi.ParsePreview(J("""
            {"subtotal":"180.00","total":"120.00","discount":"60.00","lines":[{"index":0,"product":"k","qty":"3","price":"60.00","discount":"60.00","line_total":"120.00",
             "applied":[{"kind":"promotion","id":"preview","version":1,"title":"2+1","qty":"1","amount":"60.00"}]}],
             "warnings":[{"code":"promotion_capped_by_cost","index":0,"cut":"10.00","purchase_price":"90.00"}],"hints":[{"promotion":"p","need_qty":1,"text":"Добавьте ещё 1 — будет бесплатно"}]}
            """));
        r.Total.Should().Be(120m);
        r.Discount.Should().Be(60m);
        r.Lines.Single().Applied.Single().Amount.Should().Be(60m);
        r.Warnings.Single().PurchasePrice.Should().Be(90m);
        r.Hints.Single().Text.Should().StartWith("Добавьте");
        PromotionText.Warning(r.Warnings[0], "Кефир").Should().Contain("Кефир").And.Contain("урезана до закупки").And.Contain("90 сом");

        PromotionsApi.ParseReport(J("""{"date_from":"2026-11-01","results":[]}""")).Should().BeEmpty();
        var rows = PromotionsApi.ParseReport(J("""
            {"results":[{"promotion":{"id":"p-1","title":"2+1"},"receipts":12,"units":"36.000","discount":"1500.00","revenue":"9000.00","margin":"2100.50"},
            {"promotion_id":"p-2","receipts":"1","discount":5}]}
            """));
        rows.Select(x => x.PromotionId).Should().Equal("p-1", "p-2");
        rows[0].Receipts.Should().Be(12);
        rows[0].Margin.Should().Be(2100.5m);
        rows[1].Discount.Should().Be(5m);
        PromotionText.Usage(rows[0]).Replace(' ', ' ').Should().Be("Сработала: 12 чек., скидка 1 500 сом");
    }

    // ── Запросы ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_SendsStatuses_AndParsesResults()
    {
        _server.Json("GET", "/api/main/promotions/", 200, "{\"version\":\"v1\",\"count\":1,\"results\":[" + PromoJson + "]}");
        var api = await MakeAsync();
        var list = await api.ListAsync();
        list.Count.Should().Be(1);
        list.Items.Single().Title.Should().Be("2+1 вода");
        Hits("/api/main/promotions/", "GET").Single().RequestMessage.Query!["status"].Should().Contain("active,paused,draft,finished");
    }

    [Fact]
    public async Task Create_SendsIdempotencyKey_SameKeyOnRepeat_ReadsReplayed()
    {
        _server.Json("POST", "/api/main/promotions/", 201, PromoJson.Replace("\"version\":3", "\"version\":1,\"replayed\":true"));
        var api = await MakeAsync();
        var draft = new Promotion { Title = "2+1 вода", Kind = Promotion.KindBuyNGetM, Categories = { "cat-1" } };

        var first = await api.CreateAsync(draft, "wizard-key-1");
        await api.CreateAsync(draft, "wizard-key-1");

        first.Replayed.Should().BeTrue();
        var hits = Hits("/api/main/promotions/", "POST").ToList();
        hits.Should().HaveCount(2);
        hits.Select(h => h.RequestMessage.Headers!["Idempotency-Key"].ToString()).Should().AllBe("wizard-key-1");
        hits[0].RequestMessage.Body.Should().Contain("\"kind\":\"buy_n_get_m\"").And.Contain("\"categories\":[\"cat-1\"]").And.NotContain("\"status\"");
        await FluentActions.Awaiting(() => api.CreateAsync(draft, " ")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Update_Patch_WithoutKindForStarted_StatusActions_Delete_Preview_Report_UseTheirPaths()
    {
        _server.Json("PATCH", "/api/main/promotions/p-1/", 200, PromoJson);
        _server.Json("POST", "/api/main/promotions/p-1/pause/", 200, PromoJson.Replace("\"status\":\"active\"", "\"status\":\"paused\""));
        _server.Json("DELETE", "/api/main/promotions/p-9/", 204, "");
        _server.Json("POST", "/api/main/promotions/preview/", 200, "{\"subtotal\":\"300.00\",\"total\":\"200.00\",\"discount\":\"100.00\",\"lines\":[],\"warnings\":[],\"hints\":[]}");
        _server.Json("GET", "/api/main/promotions/report/", 200, "{\"results\":[]}");
        var api = await MakeAsync();
        var started = PromotionsApi.Parse(J(PromoJson));

        await api.UpdateAsync(started);
        (await api.ChangeStatusAsync("p-1", "pause"))!.Status.Should().Be("paused");
        await api.DeleteAsync("p-9");
        (await api.PreviewAsync(new Promotion { Kind = Promotion.KindPercent, Percent = 10, ScopeAll = true }, new[] { new PromotionPreviewItem("k", 3, 100m) })).Total.Should().Be(200m);
        await api.ReportAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 27), "p-1");

        Hits("/api/main/promotions/p-1/", "PATCH").Single().RequestMessage.Body.Should().NotContain("\"kind\"", "у запущенной вид не отправляется (409 promotion_kind_locked)");
        Hits("/api/main/promotions/p-9/", "DELETE").Should().HaveCount(1);
        var preview = Hits("/api/main/promotions/preview/", "POST").Single().RequestMessage.Body!;
        var pv = J(preview);
        pv.GetProperty("promotion").GetProperty("title").GetString().Should().Be("Предпросмотр", "без названия сервер отвечает 400 — подставляется рабочее");
        pv.GetProperty("items").GetRawText().Should().Be("[{\"product\":\"k\",\"qty\":3,\"price\":\"100.00\"}]");
        var report = Hits("/api/main/promotions/report/", "GET").Single().RequestMessage.Query!;
        report["date_from"].Should().Contain("2026-11-01");
        report["promotion"].Should().Contain("p-1");
        await FluentActions.Awaiting(() => api.ChangeStatusAsync("p-1", "explode")).Should().ThrowAsync<ArgumentException>();
    }

    // ── Ошибки → понятный текст ──────────────────────────────────────────────────

    [Fact]
    public async Task PromotionInvalid_Title_GoesToStep1_WithFriendlyText()
    {
        _server.Json("POST", "/api/main/promotions/", 400, "{\"code\":\"promotion_invalid\",\"detail\":\"Укажите название акции.\",\"field\":\"title\",\"fields\":{\"title\":[\"Укажите название акции.\"]}}");
        var api = await MakeAsync();
        var ex = (await FluentActions.Awaiting(() => api.CreateAsync(new Promotion(), "k")).Should().ThrowAsync<ApiException>()).Which;
        var e = PromotionErrors.Describe(ex);
        e.Code.Should().Be("promotion_invalid");
        e.Field.Should().Be("title");
        e.Step.Should().Be(1);
        e.Text.Should().Contain("Укажите название акции").And.Contain("шаге «Название»");
    }

    [Theory]
    [InlineData("{\"code\":\"promotion_invalid\",\"detail\":\"percent от 0,01 до 100.\",\"field\":\"params\",\"fields\":{\"params\":[\"percent от 0,01 до 100.\"]}}", "params", 2)]
    [InlineData("{\"code\":\"promotion_invalid\",\"fields\":{\"ends_at\":[\"Окончание раньше начала.\"]}}", "ends_at", 4)]
    [InlineData("{\"code\":\"promotion_invalid\",\"field\":\"scope\",\"detail\":\"Пустой scope.\"}", "scope", 3)]
    [InlineData("{\"code\":\"promotion_invalid\",\"field\":\"priority\"}", "priority", 5)]
    public void PromotionInvalid_FieldToStep(string payload, string field, int step)
    {
        var e = PromotionErrors.Describe(new ApiException("x", 400, J(payload)));
        e.Field.Should().Be(field);
        e.Step.Should().Be(step);
        e.Text.Should().Contain("Исправьте на шаге «" + PromotionErrors.StepName(step) + "»");
    }

    [Theory]
    [InlineData(409, "promotion_kind_locked", "нельзя сменить вид")]
    [InlineData(409, "promotion_finished", "уже завершена")]
    [InlineData(409, "promotion_bad_status", "«Обновить»")]
    [InlineData(409, "promotion_not_draft", "только черновик")]
    [InlineData(403, "promotion_below_cost_forbidden", "только владелец")]
    public void Conflicts_AndForbidden_AreRussianWithAHint(int status, string code, string fragment)
    {
        var e = PromotionErrors.Describe(new ApiException("Request failed", status, J("{\"code\":\"" + code + "\",\"detail\":\"server text\"}")));
        e.Code.Should().Be(code);
        e.Text.Should().Contain(fragment).And.NotContain("Request failed");
    }

    [Fact]
    public void PermissionDenied_MentionsRequiredRight_AndOtherLanguages()
    {
        var ex = new ApiException("Forbidden", 403, J("{\"code\":\"permission_denied\",\"required\":\"can_manage_promotions\"}"));
        PromotionErrors.Describe(ex).Text.Should().Contain("Нет права управлять акциями").And.Contain("can_manage_promotions");
        UserPreferences.Instance.Language = AppLanguage.Kyrgyz;
        PromotionErrors.Describe(ex).Text.Should().Contain("укук");
        UserPreferences.Instance.Language = AppLanguage.Uzbek;
        PromotionErrors.Describe(ex).Text.Should().Contain("huquq");
        PromotionErrors.Describe(new ApiException("x", 403, null)).Code.Should().Be("permission_denied", "403 без кода — тоже нет права");
        PromotionErrors.Describe(new HttpRequestException("down")).Code.Should().Be("network");
        PromotionErrors.Describe(new ApiException("x", 502, null)).Code.Should().Be("server");
    }

    // ── Слова и пример ───────────────────────────────────────────────────────────

    [Fact]
    public void KindLabels_And_Example_InPlainWords()
    {
        PromotionText.KindLabel(new Promotion { Kind = Promotion.KindBuyNGetM, Buy = 2, Get = 1, GetDiscountPercent = 100 }).Should().Be("Купи 2 — получи 1");
        PromotionText.KindLabel(new Promotion { Kind = Promotion.KindPercent, Percent = 15 }).Should().Be("−15 %");
        PromotionText.KindLabel(new Promotion { Kind = Promotion.KindAmount, Amount = 20, Per = "unit" }).Should().Be("−20 сом за штуку");
        PromotionText.KindLabel(new Promotion { Kind = Promotion.KindFixedPrice, Price = 79 }).Should().Be("Цена по акции 79 сом");
        PromotionText.Example(new Promotion { Kind = Promotion.KindBuyNGetM, Buy = 2, Get = 1, GetDiscountPercent = 100 }).Should().Be("Вода 100 сом × 3 = 300 → 200, третья бесплатно");
        PromotionText.Example(new Promotion { Kind = Promotion.KindPercent, Percent = 15 }).Should().Be("Вода 100 сом × 3 = 300 → 255 (−15 %)");
        PromotionText.Example(new Promotion { Kind = Promotion.KindAmount, Amount = 20, Per = "line" }).Should().Be("Вода 100 сом × 3 = 300 → 280 (−20)");
        PromotionText.Example(new Promotion { Kind = Promotion.KindFixedPrice, Price = 79 }).Should().Be("Вода 100 сом → 79 сом; × 3 = 300 → 237");
    }

    [Fact]
    public void Period_DaysHours_Status_Scheduled()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(6));
        var p = PromotionsApi.Parse(J(PromoJson));
        PromotionText.Period(p).Should().Be("с 27.11.2026 по 28.11.2026");
        PromotionText.DaysHours(p).Should().Be("Пн, Пт · 22:00–02:00 (через полночь)");
        PromotionText.DaysHours(new Promotion()).Should().Be("каждый день");
        PromotionText.Period(new Promotion()).Should().Be("бессрочно");
        p.StartsAt = now.AddDays(2);
        p.IsScheduled(now).Should().BeTrue();
        PromotionText.StatusLabel(p, now).Should().Be("Запланирована");
    }

    [Fact]
    public void Access_FeatureAndRole()
    {
        PromotionsAccess.ShouldOffer(false, true).Should().BeFalse();
        PromotionsAccess.ShouldOffer(true, false).Should().BeFalse();
        PromotionsAccess.ShouldOffer(true, true).Should().BeTrue();
        PromotionsAccess.IsOwner(J("{\"role\":\"owner\"}")).Should().BeTrue();
        PromotionsAccess.IsOwner(J("{\"role\":\"admin\"}")).Should().BeFalse("«ниже закупки» — только владелец");
    }
}
