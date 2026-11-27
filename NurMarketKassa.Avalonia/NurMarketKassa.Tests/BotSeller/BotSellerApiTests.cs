using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.BotSeller;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 1», docs/ТЗ/Умар — API бота-продавца Ф1 (26.11).md): разбор seller_config, частичный PATCH (только изменённые поля, часы по дням не затираются),
/// диалоги (список, переписка, «взять» / «вернуть»), отчёт по продажам, «Проверить бота» (as=customer, ответ без разметки Telegram) против поддельного сервера с примерами из ответа Умара.</summary>
public sealed class BotSellerConfigTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // кириллица в тексте, а не \uXXXX
    private static string Ser(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private const string Full =
        "{\"seller_config\":{\"seller_enabled\":true,\"orders_enabled\":false,\"tone\":\"short\",\"greeting\":\"Привет!\",\"signature\":\"NBS\",\"forbidden_words\":[\"дёшево\",\"даром\"],\"language\":\"ky\"," +
        "\"handoff_enabled\":false,\"human_hours\":{\"from\":\"10:00\",\"to\":\"18:30\"},\"off_hours_reply\":\"Ответим {when}\",\"handoff_return_minutes\":45,\"confirm_threshold\":50000," +
        "\"knowledge\":{\"rules\":\"Возврат 14 дней\",\"delivery\":\"По городу\",\"payment\":\"Наличные\",\"work_hours\":\"9–21\"}," +
        "\"bargain\":{\"enabled\":true,\"max_discount_percent\":5,\"min_margin_percent\":10,\"only_marked\":true}}}";

    [Fact]
    public void Parse_reads_every_field()
    {
        var c = ServerTelegramBotApi.ParseSellerConfig(J(Full))!;
        c.SellerEnabled.Should().BeTrue();
        c.OrdersEnabled.Should().BeFalse();
        c.Tone.Should().Be("short");
        c.Greeting.Should().Be("Привет!");
        c.Signature.Should().Be("NBS");
        c.ForbiddenWords.Should().Equal("дёшево", "даром");
        c.Language.Should().Be("ky");
        c.HandoffEnabled.Should().BeFalse();
        c.Hours.Should().Be(new BotSellerHours(false, false, "10:00", "18:30"));
        c.OffHoursReply.Should().Be("Ответим {when}");
        c.HandoffReturnMinutes.Should().Be(45);
        c.ConfirmThreshold.Should().Be(50000);
        c.Rules.Should().Be("Возврат 14 дней");
        c.Delivery.Should().Be("По городу");
        c.Payment.Should().Be("Наличные");
        c.WorkHours.Should().Be("9–21");
    }

    [Fact]
    public void Parse_server_defaults_and_old_server()
    {
        ServerTelegramBotApi.ParseSellerConfig(J("{\"mode\":\"server\"}")).Should().BeNull("старый сервер без seller_config");
        var c = ServerTelegramBotApi.ParseSellerConfig(J("{\"seller_config\":{}}"))!;
        c.SellerEnabled.Should().BeFalse("главный выключатель по умолчанию выключен");
        c.OrdersEnabled.Should().BeTrue();
        c.HandoffEnabled.Should().BeTrue();
        c.Tone.Should().Be("friendly");
        c.Language.Should().Be("auto");
        c.HandoffReturnMinutes.Should().Be(120);
        c.ConfirmThreshold.Should().Be(30000);
        c.Hours.Should().Be(BotSellerHours.Default);
    }

    [Fact]
    public void Parse_hours_all_day_and_per_day()
    {
        ServerTelegramBotApi.ParseSellerConfig(J("{\"seller_config\":{\"human_hours\":{}}}"))!.Hours.AllDay.Should().BeTrue("{} — круглосуточно");
        var perDay = ServerTelegramBotApi.ParseSellerConfig(J("{\"seller_config\":{\"human_hours\":{\"mon\":{\"from\":\"10:00\",\"to\":\"18:00\"},\"sun\":null}}}"))!.Hours;
        perDay.PerDay.Should().BeTrue();
        perDay.AllDay.Should().BeFalse();
    }

    [Fact]
    public void DiffBody_has_only_changed_fields()
    {
        var before = ServerTelegramBotApi.ParseSellerConfig(J(Full))!;
        before.DiffBody(before).Should().BeEmpty("ничего не менялось — PATCH не нужен");

        var body = (before with { Tone = "official", ConfirmThreshold = 0, Rules = "Новое", Hours = new BotSellerHours(true, false, "10:00", "18:30") }).DiffBody(before);
        Ser(body).Should().Be("{\"tone\":\"official\",\"human_hours\":{},\"confirm_threshold\":0,\"knowledge\":{\"rules\":\"Новое\"}}");
        Ser((before with { Hours = before.Hours with { From = "09:00" } }).DiffBody(before)).Should().Be("{\"human_hours\":{\"from\":\"09:00\",\"to\":\"18:30\"}}");
        Ser((before with { ForbiddenWords = ["один"] }).DiffBody(before)).Should().Be("{\"forbidden_words\":[\"один\"]}");
    }

    [Fact]
    public void DiffBody_does_not_overwrite_per_day_schedule_when_hours_untouched()
    {
        var perDay = ServerTelegramBotApi.ParseSellerConfig(J("{\"seller_config\":{\"human_hours\":{\"mon\":{\"from\":\"10:00\",\"to\":\"18:00\"}}}}"))!;
        Ser((perDay with { Greeting = "Здравствуйте" }).DiffBody(perDay)).Should().Be("{\"greeting\":\"Здравствуйте\"}");
    }

    [Theory]
    [InlineData("слово, другое;третье\nчетвёртое, СЛОВО", 4)]
    [InlineData("  ,, ;; ", 0)]
    [InlineData("", 0)]
    public void SplitWords_trims_and_removes_repeats(string text, int count) =>
        BotSellerConfig.SplitWords(text).Should().HaveCount(count);

    [Fact]
    public void Plain_removes_telegram_markup()
    {
        BotSellerText.Plain("Здравствуйте! Добро пожаловать в магазин <b>NBS</b>.\n\nЦена <b>52 сом</b> &amp; доставка").Should().Be("Здравствуйте! Добро пожаловать в магазин NBS.\n\nЦена 52 сом & доставка");
        BotSellerText.Plain(null).Should().BeEmpty();
    }
}

[Collection("ServerFeatures")]
public sealed class BotSellerApiTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private async Task<ServerTelegramBotApi> ApiAsync() => new(await LoggedInAsync(_fake));

    [Fact]
    public async Task Dialogs_list_parses_handoff_and_sends_period_status_and_page()
    {
        _fake.Json("GET", "/api/main/telegram-bot/dialogs/", 200,
            "{\"count\":3,\"next\":\"https://x/?page=2\",\"previous\":null,\"results\":[{\"chat_id\":\"770013812\",\"name\":\"Айбек\",\"username\":\"\",\"client_id\":null,\"messages_count\":4,\"orders_count\":0," +
            "\"last_at\":\"2026-11-26T10:00:00Z\",\"last_text\":\"хочу поговорить с менеджером\",\"status\":\"callback\",\"handoff\":{\"id\":\"h1\",\"reason\":\"human\",\"status\":\"callback\",\"question\":\"вопрос\"," +
            "\"phone\":\"0555123456\",\"note\":\"\",\"taken_by\":\"\",\"created_at\":\"2026-11-26T10:00:00Z\",\"taken_at\":null,\"first_answer_at\":null,\"until\":null,\"closed_at\":null,\"order\":null}}," +
            "{\"chat_id\":\"5\",\"name\":\"\",\"username\":\"u\",\"client_id\":null,\"messages_count\":1,\"orders_count\":0,\"last_at\":null,\"last_text\":\"\",\"status\":\"bot\",\"handoff\":null}]}");
        var api = await ApiAsync();

        var page = await api.GetDialogsAsync("callback", new DateTime(2026, 11, 1), new DateTime(2026, 11, 26), 2);

        page.Count.Should().Be(3);
        page.HasNext.Should().BeTrue();
        page.Rows.Should().HaveCount(2);
        page.Rows[0].Status.Should().Be("callback");
        page.Rows[0].Handoff!.Phone.Should().Be("0555123456");
        page.Rows[0].Handoff!.Reason.Should().Be("human");
        page.Rows[1].Handoff.Should().BeNull();
        var query = Requests(_fake, "GET", "/api/main/telegram-bot/dialogs/").Single().RequestMessage.Query!;
        query["status"].Single().Should().Be("callback");
        query["from"].Single().Should().Be("2026-11-01");
        query["to"].Single().Should().Be("2026-11-26");
        query["page"].Single().Should().Be("2");
    }

    [Fact]
    public async Task Dialogs_without_status_does_not_send_the_filter()
    {
        _fake.Json("GET", "/api/main/telegram-bot/dialogs/", 200, "{\"count\":0,\"next\":null,\"previous\":null,\"results\":[]}");
        var api = await ApiAsync();

        var page = await api.GetDialogsAsync(null, new DateTime(2026, 11, 1), new DateTime(2026, 11, 26), 1);

        page.Rows.Should().BeEmpty();
        page.HasNext.Should().BeFalse();
        Requests(_fake, "GET", "/api/main/telegram-bot/dialogs/").Single().RequestMessage.Query!.ContainsKey("status").Should().BeFalse();
    }

    [Fact]
    public async Task Dialog_detail_reads_messages_and_orders_with_exact_totals()
    {
        _fake.Json("GET", "/api/main/telegram-bot/dialogs/5729686885/", 200,
            "{\"chat_id\":\"5729686885\",\"name\":\"Мирлан\",\"username\":\"m\",\"messages_count\":2,\"orders_count\":1,\"last_at\":\"2026-10-28T13:20:49Z\",\"status\":\"bot\"," +
            "\"messages\":[{\"at\":\"2026-10-02T11:06:01Z\",\"text\":\"1 кг яблок\",\"reply\":\"Есть <b>яблоки</b>\",\"is_voice\":true,\"order_number\":null}]," +
            "\"handoffs\":[],\"orders\":[{\"id\":\"o1\",\"number\":\"A-17\",\"total\":\"1234.50\",\"status\":\"new\",\"created_at\":\"2026-10-02T11:10:00Z\"}]}");
        var api = await ApiAsync();

        var detail = (await api.GetDialogAsync("5729686885"))!;

        detail.Messages.Should().ContainSingle().Which.IsVoice.Should().BeTrue();
        detail.Orders.Should().ContainSingle().Which.Total.Should().Be(1234.50m);
        detail.Orders[0].Number.Should().Be("A-17");
        detail.Dialog.Name.Should().Be("Мирлан");
    }

    [Theory]
    [InlineData("take", "human")]
    [InlineData("return", "bot")]
    public async Task Dialog_action_posts_action_and_reads_status(string action, string status)
    {
        _fake.Json("POST", "/api/main/telegram-bot/dialogs/770013812/", 200, "{\"status\":\"" + status + "\",\"changed\":true,\"handoff\":null}");
        var api = await ApiAsync();

        var r = await api.DialogActionAsync("770013812", action);

        r.Status.Should().Be(status);
        r.Changed.Should().BeTrue();
        BodyOf(Requests(_fake, "POST", "/api/main/telegram-bot/dialogs/770013812/").Single())["action"]!.GetValue<string>().Should().Be(action);
    }

    [Fact]
    public async Task Sales_stats_parses_answer_from_the_doc()
    {
        _fake.Json("GET", "/api/main/telegram-bot/sales-stats/", 200,
            "{\"from\":\"2026-10-28\",\"to\":\"2026-11-26\",\"dialogs\":3,\"messages\":6,\"people\":3,\"orders\":{\"count\":2,\"total\":\"2500.50\",\"done\":1,\"done_total\":\"1000.25\",\"canceled\":1,\"avg_check\":\"1250.25\"}," +
            "\"conversion_percent\":66.7,\"funnel\":{\"dialogs\":3,\"handoffs\":2,\"orders\":2,\"done\":1},\"handoffs\":{\"total\":2,\"by_reason\":{\"human\":2},\"callbacks\":1,\"answered\":1,\"unanswered\":0,\"waiting_now\":0," +
            "\"avg_answer_seconds\":40,\"median_answer_seconds\":35},\"unknown_questions\":[{\"at\":\"2026-11-25T09:00:00Z\",\"chat_id\":\"7\",\"text\":\"Доставляете в Ош?\"}]}");
        var api = await ApiAsync();

        var s = await api.GetSalesStatsAsync(new DateTime(2026, 10, 28), new DateTime(2026, 11, 26));

        s.Dialogs.Should().Be(3);
        s.OrdersCount.Should().Be(2);
        s.OrdersTotal.Should().Be(2500.50m);
        s.OrdersDoneTotal.Should().Be(1000.25m);
        s.AvgCheck.Should().Be(1250.25m);
        s.ConversionPercent.Should().Be(66.7);
        s.FunnelHandoffs.Should().Be(2);
        s.HandoffsByReason["human"].Should().Be(2);
        s.AvgAnswerSeconds.Should().Be(40);
        s.UnknownQuestions.Should().ContainSingle().Which.Text.Should().Be("Доставляете в Ош?");
    }

    [Fact]
    public async Task Sales_stats_tolerates_null_seconds_and_missing_blocks()
    {
        _fake.Json("GET", "/api/main/telegram-bot/sales-stats/", 200, "{\"dialogs\":0,\"handoffs\":{\"avg_answer_seconds\":null,\"median_answer_seconds\":null}}");
        var api = await ApiAsync();

        var s = await api.GetSalesStatsAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 26));

        s.AvgAnswerSeconds.Should().BeNull();
        s.OrdersTotal.Should().Be(0m);
        s.UnknownQuestions.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_as_customer_sends_as_customer_and_strips_markup()
    {
        _fake.Json("POST", "/api/main/telegram-bot/test-ai/", 200,
            "{\"ok\":true,\"answer\":\"Здравствуйте! Цена <b>52 сом</b>\",\"model\":\"gemini-3.5-flash-lite\",\"functions_called\":[],\"handoff\":\"human\",\"order_draft\":{\"name\":\"Айбек\",\"phone\":\"0555\",\"items\":[{\"title\":\"Куртка\",\"variant_id\":\"v1\",\"size\":\"52\",\"color\":\"чёрный\",\"qty\":2},{\"title\":\"Шапка\"}],\"comment\":\"Позвонить\"},\"catalog_matches\":[\"a\",\"b\"]}");
        var api = await ApiAsync();

        var r = await api.TestAsCustomerAsync("Сколько стоят яблоки?");

        r.Answer.Should().Be("Здравствуйте! Цена 52 сом");
        r.Handoff.Should().Be("human");
        r.OrderDraft.Should().NotBeNull();
        r.OrderDraft!.Name.Should().Be("Айбек");
        r.OrderDraft.Phone.Should().Be("0555");
        r.OrderDraft.Comment.Should().Be("Позвонить");
        r.OrderDraft.Items.Should().HaveCount(2);
        r.OrderDraft.Items[0].Should().Be(new BotOrderDraftItem("Куртка", "v1", "52", "чёрный", "2"));
        r.OrderDraft.Items[1].Should().Be(new BotOrderDraftItem("Шапка", "", "", "", ""), "поля черновика могут отсутствовать");
        r.CatalogMatches.Should().Be(2);
        var body = BodyOf(Requests(_fake, "POST", "/api/main/telegram-bot/test-ai/").Single());
        body["as"]!.GetValue<string>().Should().Be("customer");
        body["question"]!.GetValue<string>().Should().Be("Сколько стоят яблоки?");
    }

    [Fact]
    public async Task Patch_sends_only_the_seller_config_part()
    {
        _fake.Json("PATCH", "/api/main/telegram-bot/settings/", 200, "{\"seller_config\":{\"tone\":\"short\"}}");
        var api = await ApiAsync();

        var saved = await api.PatchSellerConfigAsync(new Dictionary<string, object?> { ["tone"] = "short", ["knowledge"] = new Dictionary<string, object?> { ["rules"] = "x" } });

        saved!.Tone.Should().Be("short");
        var body = BodyOf(Requests(_fake, "PATCH", "/api/main/telegram-bot/settings/").Single());
        body.Count.Should().Be(1, "в теле только seller_config");
        body["seller_config"]!["tone"]!.GetValue<string>().Should().Be("short");
        body["seller_config"]!["knowledge"]!["rules"]!.GetValue<string>().Should().Be("x");
    }

    [Fact]
    public async Task Patch_400_gives_readable_error_text()
    {
        _fake.Json("PATCH", "/api/main/telegram-bot/settings/", 400, "{\"detail\":\"tone: official, friendly или short\",\"seller_config\":[\"tone: official, friendly или short\"]}");
        var api = await ApiAsync();
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.PatchSellerConfigAsync(new Dictionary<string, object?> { ["tone"] = "x" }));
        ServerTelegramBotApi.DescribeFields(ex).Should().Contain("tone");
    }

    [Fact]
    public async Task Test_without_draft_gives_null_draft()
    {
        _fake.Json("POST", "/api/main/telegram-bot/test-ai/", 200, "{\"ok\":true,\"answer\":\"Привет\",\"model\":\"m\",\"handoff\":null,\"order_draft\":null,\"catalog_matches\":[]}");
        var api = await ApiAsync();

        var r = await api.TestAsCustomerAsync("Привет");

        r.OrderDraft.Should().BeNull();
        r.Handoff.Should().BeNull();
    }

    [Fact]
    public async Task Reply_posts_action_reply_with_text_and_reads_sent()
    {
        _fake.Json("POST", "/api/main/telegram-bot/dialogs/770013812/", 200, "{\"status\":\"human\",\"sent\":true,\"handoff\":{\"status\":\"taken\"}}");
        var api = await ApiAsync();

        var r = await api.ReplyToDialogAsync("770013812", "Доставим завтра");

        r.Sent.Should().BeTrue();
        r.Status.Should().Be("human");
        var body = BodyOf(Requests(_fake, "POST", "/api/main/telegram-bot/dialogs/770013812/").Single());
        body["action"]!.GetValue<string>().Should().Be("reply");
        body["text"]!.GetValue<string>().Should().Be("Доставим завтра");
        BotDialogLimits.ReplyMaxLength.Should().Be(4000);
    }

    [Fact]
    public async Task Reply_502_telegram_error_gives_detail_text()
    {
        _fake.Json("POST", "/api/main/telegram-bot/dialogs/5/", 502, "{\"code\":\"telegram_error\",\"detail\":\"Forbidden: bot was blocked by the user\"}");
        var api = await ApiAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.ReplyToDialogAsync("5", "Привет"));

        ex.StatusCode.Should().Be(502);
        ServerTelegramBotApi.DescribeFields(ex).Should().Be("Forbidden: bot was blocked by the user");
    }

    [Fact]
    public async Task Dialogs_closed_filter_is_sent_and_returned_handoff_is_read()
    {
        _fake.Json("GET", "/api/main/telegram-bot/dialogs/", 200,
            "{\"count\":1,\"next\":null,\"previous\":null,\"results\":[{\"chat_id\":\"9\",\"name\":\"Нурлан\",\"username\":\"\",\"messages_count\":3,\"orders_count\":0,\"last_at\":null,\"last_text\":\"спасибо\",\"status\":\"bot\"," +
            "\"handoff\":{\"reason\":\"human\",\"status\":\"expired\",\"question\":\"\",\"phone\":\"\",\"note\":\"\",\"taken_by\":\"\",\"created_at\":\"2026-11-25T12:00:00Z\"}}]}");
        var api = await ApiAsync();

        var page = await api.GetDialogsAsync("closed", new DateTime(2026, 11, 1), new DateTime(2026, 11, 26), 1);

        page.Rows.Should().ContainSingle().Which.Handoff!.Status.Should().Be("expired");
        Requests(_fake, "GET", "/api/main/telegram-bot/dialogs/").Single().RequestMessage.Query!["status"].Single().Should().Be("closed");
    }

    [Fact]
    public async Task Sales_stats_unknown_questions_have_name_and_client_id_or_null()
    {
        _fake.Json("GET", "/api/main/telegram-bot/sales-stats/", 200,
            "{\"dialogs\":2,\"unknown_questions\":[{\"at\":\"2026-11-25T09:00:00Z\",\"chat_id\":\"7\",\"text\":\"Доставляете в Ош?\",\"name\":\"Бакыт\",\"client_id\":\"c1\"}," +
            "{\"at\":null,\"chat_id\":\"8\",\"text\":\"Гарантия?\",\"name\":null,\"client_id\":null}]}");
        var api = await ApiAsync();

        var s = await api.GetSalesStatsAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 26));

        s.UnknownQuestions[0].Name.Should().Be("Бакыт");
        s.UnknownQuestions[0].ClientId.Should().Be("c1");
        s.UnknownQuestions[1].Name.Should().BeEmpty();
        s.UnknownQuestions[1].ClientId.Should().BeNull();
    }
}
