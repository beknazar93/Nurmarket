using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.BotSeller;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 2: торг», docs/ТЗ/Умар — API торга бота-продавца Ф2 (26.11).md): блок seller_config.bargain (разбор и частичный PATCH), правила по товарам (список, PUT с null / строками, DELETE),
/// симулятор «Проверить торг», журнал оферт, блок «Торг» отчёта, bargain в test-ai, ошибки 400 по-русски и строка «Цена по торгу» в заказе витрины. Поддельный сервер, примеры — из ответа Умара и живой проверки на NBS.</summary>
public sealed class BotBargainConfigTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Ser(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private const string Seller =
        "{\"seller_config\":{\"bargain\":{\"enabled\":true,\"max_discount_percent\":7,\"min_margin_percent\":12.5,\"only_marked\":false,\"steps\":4,\"allowed_categories\":[\"c1\",\"c2\"],\"handoff_on_decline\":true}}}";

    [Fact]
    public void Parse_reads_the_bargain_block()
    {
        var c = ServerTelegramBotApi.ParseSellerConfig(J(Seller))!.Bargain!;
        c.Enabled.Should().BeTrue();
        c.MaxDiscountPercent.Should().Be(7m);
        c.MinMarginPercent.Should().Be(12.5m);
        c.OnlyMarked.Should().BeFalse();
        c.Steps.Should().Be(4);
        c.AllowedCategories.Should().Equal("c1", "c2");
        c.HandoffOnDecline.Should().BeTrue();
    }

    [Fact]
    public void Parse_defaults_and_old_server_without_the_block()
    {
        ServerTelegramBotApi.ParseSellerConfig(J("{\"seller_config\":{}}"))!.Bargain.Should().BeNull("сервер без блока «торг» — блок в программе скрыт");
        var c = ServerTelegramBotApi.ParseSellerConfig(J("{\"seller_config\":{\"bargain\":{}}}"))!.Bargain!;
        c.Enabled.Should().BeFalse();
        c.MaxDiscountPercent.Should().Be(5m);
        c.MinMarginPercent.Should().Be(10m);
        c.OnlyMarked.Should().BeTrue();
        c.Steps.Should().Be(3);
    }

    [Fact]
    public void DiffBody_sends_only_changed_bargain_fields_and_ignores_category_order()
    {
        var before = ServerTelegramBotApi.ParseSellerConfig(J(Seller))!;
        before.DiffBody(before).Should().BeEmpty();
        (before with { Bargain = before.Bargain! with { AllowedCategories = ["c2", "c1"] } }).DiffBody(before).Should().BeEmpty("порядок категорий не изменение");

        var changed = before with { Bargain = before.Bargain! with { Enabled = false, Steps = 2, MaxDiscountPercent = 10m, AllowedCategories = ["c1"] } };
        Ser(changed.DiffBody(before)).Should().Be("{\"bargain\":{\"enabled\":false,\"max_discount_percent\":10,\"steps\":2,\"allowed_categories\":[\"c1\"]}}");
    }
}

[Collection("ServerFeatures")]
public sealed class BotBargainApiTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private async Task<ServerTelegramBotApi> ApiAsync() => new(await LoggedInAsync(_fake));

    [Fact]
    public async Task Products_list_parses_money_and_sends_filters()
    {
        _fake.Json("GET", "/api/main/telegram-bot/bargain/products/", 200,
            "{\"count\":2,\"next\":\"https://x/?page=2\",\"previous\":null,\"results\":[{\"product_id\":\"p1\",\"name\":\"Куртка\",\"price\":\"135.000\",\"purchase_price\":\"111.000\",\"category_id\":\"c1\",\"allowed\":true,\"min_price\":\"130.00\",\"max_discount_percent\":\"7.00\",\"effective_allowed\":true,\"floor\":\"130.00\"}," +
            "{\"product_id\":\"p2\",\"name\":\"Шапка\",\"price\":\"60\",\"purchase_price\":null,\"category_id\":null,\"allowed\":null,\"min_price\":null,\"max_discount_percent\":null,\"effective_allowed\":false,\"floor\":\"58.30\"}]}");
        var api = await ApiAsync();

        var page = await api.GetBargainProductsAsync("курт", "c1", true, 2);

        page.Count.Should().Be(2);
        page.HasNext.Should().BeTrue();
        var p = page.Rows[0];
        (p.Price, p.PurchasePrice, p.MinPrice, p.MaxDiscountPercent, p.Floor).Should().Be((135m, 111m, 130m, 7m, 130m));
        p.Allowed.Should().BeTrue();
        p.HasRule.Should().BeTrue();
        var q = page.Rows[1];
        q.Allowed.Should().BeNull();
        q.PurchasePrice.Should().BeNull("закупка не задана — не «0 сом»");
        q.HasRule.Should().BeFalse();
        var query = Requests(_fake, "GET", "/api/main/telegram-bot/bargain/products/").Single().RequestMessage.Query!;
        query["search"].Single().Should().Be("курт");
        query["category"].Single().Should().Be("c1");
        query["only_rules"].Single().Should().Be("1");
        query["page"].Single().Should().Be("2");
    }

    [Fact]
    public async Task Put_sends_allowed_null_and_strings_for_prices()
    {
        _fake.Json("PUT", "/api/main/telegram-bot/bargain/products/p1/", 200,
            "{\"product_id\":\"p1\",\"name\":\"Куртка\",\"price\":\"135.000\",\"purchase_price\":\"111.000\",\"category_id\":null,\"allowed\":null,\"min_price\":\"28000\",\"max_discount_percent\":\"7\",\"effective_allowed\":false,\"floor\":\"130.00\"}");
        var api = await ApiAsync();

        var saved = await api.SaveBargainProductAsync("p1", new BotBargainRule(null, 28000m, 7m));

        saved!.MinPrice.Should().Be(28000m);
        var body = BodyOf(Requests(_fake, "PUT", "/api/main/telegram-bot/bargain/products/p1/").Single());
        body["allowed"].Should().BeNull("«как в общих» — явный null");
        body["min_price"]!.GetValue<string>().Should().Be("28000");
        body["max_discount_percent"]!.GetValue<string>().Should().Be("7");
    }

    [Fact]
    public async Task Put_with_cleared_fields_sends_nulls_and_true_is_sent_as_bool()
    {
        _fake.Json("PUT", "/api/main/telegram-bot/bargain/products/p1/", 200, "{\"product_id\":\"p1\",\"name\":\"x\",\"price\":\"1\"}");
        var api = await ApiAsync();

        await api.SaveBargainProductAsync("p1", new BotBargainRule(true, null, null));

        var body = BodyOf(Requests(_fake, "PUT", "/api/main/telegram-bot/bargain/products/p1/").Single());
        body["allowed"]!.GetValue<bool>().Should().BeTrue();
        body["min_price"].Should().BeNull();
        body["max_discount_percent"].Should().BeNull();
    }

    [Fact]
    public async Task Put_400_is_described_in_russian_with_field_names()
    {
        _fake.Json("PUT", "/api/main/telegram-bot/bargain/products/p1/", 400,
            "{\"detail\":\"0…50\",\"code\":\"validation_error\",\"fields\":{\"max_discount_percent\":[\"0…50\"],\"min_price\":[\"Не выше цены товара.\"]}}");
        var api = await ApiAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => api.SaveBargainProductAsync("p1", new BotBargainRule(true, 999m, 70m)));

        var text = BotBargainText.Describe(ex, f => f == "min_price" ? "Минимальная цена" : f == "max_discount_percent" ? "Макс. скидка" : f);
        text.Should().Contain("Минимальная цена: Не выше цены товара.").And.Contain("Макс. скидка: 0…50").And.NotContain("min_price");
    }

    [Fact]
    public async Task Delete_resets_product_rules()
    {
        _fake.Json("DELETE", "/api/main/telegram-bot/bargain/products/p1/", 204, "");
        var api = await ApiAsync();

        await api.ResetBargainProductAsync("p1");

        Requests(_fake, "DELETE", "/api/main/telegram-bot/bargain/products/p1/").Should().HaveCount(1);
    }

    [Fact]
    public async Task Test_posts_product_offer_and_reset_and_reads_decision()
    {
        _fake.Json("POST", "/api/main/telegram-bot/bargain/test/", 200,
            "{\"decision\":\"counter\",\"price\":\"130.00\",\"list_price\":\"135.000\",\"expires_at\":\"2026-11-26T16:36:18Z\",\"text\":\"Могу предложить «123» за <b>130 сом</b> вместо 135 сом.\",\"button\":true,\"floor\":\"122.10\"}");
        var api = await ApiAsync();

        var r = await api.TestBargainAsync("p1", 100m, true);

        r.Decision.Should().Be("counter");
        (r.Price, r.ListPrice, r.Floor).Should().Be((130m, 135m, 122.10m));
        r.Button.Should().BeTrue();
        r.Text.Should().Be("Могу предложить «123» за 130 сом вместо 135 сом.");
        var body = BodyOf(Requests(_fake, "POST", "/api/main/telegram-bot/bargain/test/").Single());
        body["product_id"]!.GetValue<string>().Should().Be("p1");
        body["offer"]!.GetValue<string>().Should().Be("100");
        body["reset"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Test_without_offer_does_not_send_offer_or_reset()
    {
        _fake.Json("POST", "/api/main/telegram-bot/bargain/test/", 200, "{\"decision\":\"fixed\",\"price\":\"135.000\",\"list_price\":\"135.000\",\"expires_at\":null,\"text\":\"Цена фиксированная\",\"button\":false,\"floor\":\"130.00\"}");
        var api = await ApiAsync();

        await api.TestBargainAsync("p1", null, false);

        var body = BodyOf(Requests(_fake, "POST", "/api/main/telegram-bot/bargain/test/").Single());
        body.ContainsKey("offer").Should().BeFalse();
        body.ContainsKey("reset").Should().BeFalse();
    }

    [Fact]
    public async Task Offers_log_parses_the_live_row_and_sends_filters()
    {
        _fake.Json("GET", "/api/main/telegram-bot/bargain/offers/", 200,
            "{\"count\":1,\"next\":null,\"previous\":null,\"results\":[{\"id\":\"o1\",\"at\":\"2026-11-26T16:06:23Z\",\"chat_id\":\"test:47e1\",\"is_test\":true,\"product_id\":\"p1\",\"product_name\":\"123\",\"variant_id\":null,\"qty\":\"1.000\"," +
            "\"list_price\":\"135.00\",\"client_offer\":\"99.00\",\"decision\":\"decline\",\"price\":\"123.00\",\"step\":3,\"floor\":\"122.10\",\"limit_percent\":\"20.00\",\"expires_at\":null,\"accepted_at\":null,\"order\":{\"number\":\"A-5\"}}]}");
        var api = await ApiAsync();

        var page = await api.GetBargainOffersAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 26), "decline", 1);

        var o = page.Rows.Should().ContainSingle().Subject;
        (o.ListPrice, o.ClientOffer, o.Price, o.Floor, o.LimitPercent).Should().Be((135m, 99m, 123m, 122.10m, 20m));
        (o.Decision, o.Step, o.IsTest, o.ProductName, o.Order).Should().Be(("decline", 3, true, "123", "A-5"));
        var query = Requests(_fake, "GET", "/api/main/telegram-bot/bargain/offers/").Single().RequestMessage.Query!;
        query["decision"].Single().Should().Be("decline");
        query["from"].Single().Should().Be("2026-11-01");
        query["to"].Single().Should().Be("2026-11-26");
    }

    [Fact]
    public async Task Sales_stats_bargain_block_and_top_products_in_any_shape()
    {
        _fake.Json("GET", "/api/main/telegram-bot/sales-stats/", 200,
            "{\"dialogs\":3,\"bargain\":{\"dialogs\":4,\"proposals\":9,\"accepted_by_customer\":3,\"orders\":2,\"concessions_total\":\"1500.50\",\"avg_discount_percent\":4.5,\"decline_share_percent\":22.2," +
            "\"top_products\":[{\"product_id\":\"p1\",\"name\":\"Куртка\",\"proposals\":5,\"orders\":3},{\"product_id\":\"p2\",\"name\":\"Шапка\",\"proposals\":2,\"orders\":0},{\"name\":\"Жилет\",\"count\":1},\"Шарф\"]}}");
        var api = await ApiAsync();

        var b = (await api.GetSalesStatsAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 26))).Bargain!;

        (b.Dialogs, b.Proposals, b.AcceptedByCustomer, b.Orders, b.ConcessionsTotal).Should().Be((4, 9, 3, 2, 1500.50m));
        (b.AvgDiscountPercent, b.DeclineSharePercent).Should().Be((4.5, 22.2));
        // 2026-11-27 (ответ Умара, п. 4): {product_id, name, proposals, orders}; прежний «count» читается как proposals; число не пришло — null
        b.TopProducts.Should().Equal(new BotBargainTop("Куртка", 5, 3, "p1"), new BotBargainTop("Шапка", 2, 0, "p2"), new BotBargainTop("Жилет", 1), new BotBargainTop("Шарф", null));
    }

    [Fact]
    public async Task Sales_stats_without_bargain_block_gives_null()
    {
        _fake.Json("GET", "/api/main/telegram-bot/sales-stats/", 200, "{\"dialogs\":3}");
        var api = await ApiAsync();

        (await api.GetSalesStatsAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 26))).Bargain.Should().BeNull();
    }

    [Fact]
    public async Task Test_ai_shows_bargain_text_and_offer_from_tag()
    {
        _fake.Json("POST", "/api/main/telegram-bot/test-ai/", 200,
            "{\"ok\":true,\"answer\":\"Ответ\",\"model\":\"m\",\"handoff\":null,\"order_draft\":null,\"catalog_matches\":[],\"bargain\":{\"tag\":{\"product_id\":\"p\",\"variant_id\":null,\"qty\":1,\"offer\":100},\"text\":\"На «123» цена фиксированная: <b>123 сом</b>.\"}}");
        var api = await ApiAsync();

        var r = await api.TestAsCustomerAsync("скидка?");

        r.Bargain.Should().Be(new BotBargainNote("На «123» цена фиксированная: 123 сом.", 100m));
    }

    [Fact]
    public async Task Categories_are_read_by_pages_and_sorted()
    {
        _fake.Json("GET", "/api/main/categories/", 200, "{\"count\":2,\"next\":null,\"previous\":null,\"results\":[{\"id\":\"b\",\"name\":\"Яблоки\",\"parent\":null},{\"id\":\"a\",\"name\":\"бакалея\",\"parent\":null}]}");
        var api = await ApiAsync();

        (await api.GetCategoriesAsync()).Select(c => c.Name).Should().Equal("бакалея", "Яблоки");
    }
}

public sealed class SiteOrderBargainParseTests
{
    [Fact]
    public void Order_item_reads_bargain_or_null()
    {
        var order = ShowcaseApiService.ParseOrder(JsonDocument.Parse(
            "{\"id\":\"o1\",\"items\":[{\"product\":\"p1\",\"product_name\":\"Куртка\",\"quantity\":\"1\",\"price\":\"577500.00\",\"total\":\"548626.00\",\"bargain\":{\"list_price\":\"577500.00\",\"agreed_price\":\"548626.00\",\"discount_percent\":\"5.0\",\"step\":0}}," +
            "{\"product\":\"p2\",\"product_name\":\"Шапка\",\"quantity\":\"1\",\"price\":\"100\",\"total\":\"100\",\"bargain\":null}]}").RootElement.Clone());

        order.Items[0].Bargain.Should().Be(new SiteOrderBargain(577500, 548626, 5));
        order.Items[1].Bargain.Should().BeNull("обычная строка — без «Цены по торгу»");
    }
}
