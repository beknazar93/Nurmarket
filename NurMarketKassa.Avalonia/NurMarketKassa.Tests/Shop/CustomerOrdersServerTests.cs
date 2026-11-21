using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20 (ответ Умара 20.11, В.4 «Заказ под клиента с предоплатой», этап 1 переноса на сервер): разбор заказа покупателя customer-orders/, копия серверных заказов в файле «Заказов под клиента» (местные не трогаются),
/// выдача через POST customer-orders/&lt;id&gt;/deliver/ и отмена через cancel/ с Idempotency-Key, понятные тексты ошибок (shift_not_open, order_waiting_supply, 403 на отмене с предоплатой и др.). Сервер — WireMock.</summary>
[Collection("ServerFeatures")]
public sealed class CustomerOrdersServerTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly FakeNurCrmServer _fake = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "custorders-" + Guid.NewGuid().ToString("N")[..8]);

    public CustomerOrdersServerTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        _fake.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private const string Order7 = """
        {"id":"9f2c","number":"CO-000007","status":"reserved","client":"c-1","customer_name":"Айбек","customer_phone":"+996700111222","due_date":"2026-11-25","note":"к пятнице",
         "items":[{"product":"p-1","product_name":"Фильтр масляный","qty":"2.000","price":"500.00","total":"1000.00","reserved_qty":"2.000","waiting_qty":"0","status":"reserved"},
                  {"product":"p-2","product_name":"Колодки","qty":1,"price":900,"total":900,"status":"waiting_supply"}],
         "prepayment":{"amount":"500.00","method":"cash","payments":[{"kind":"prepayment","amount":"500.00","method":"cash","shift":"s-1"}]},
         "total":"1900.00","left_to_pay":"1400.00","sale":null,"version":3}
        """;

    private PartsOrderStore Store() => new(Path.Combine(_dir, "orders-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 20, 10, 0, 0));

    private static CustomerOrderDto Dto(string json = Order7) => CustomerOrderDto.Parse(JsonDocument.Parse(json).RootElement)!;

    private async Task<(CustomerOrdersApi Api, NurMarketApiClient Client)> LoginAsync()
    {
        var client = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        await client.LoginAsync("cashier@test.kg", "12345678");
        return (new CustomerOrdersApi(client), client);
    }

    // ───────────── разбор ─────────────

    [Fact]
    public void Order_from_the_server_is_parsed_with_items_prepayment_left_to_pay_and_version()
    {
        var o = Dto();

        o.Id.Should().Be("9f2c");
        o.Number.Should().Be("CO-000007");
        o.Status.Should().Be("reserved");
        (o.CustomerName, o.CustomerPhone, o.ClientId).Should().Be(("Айбек", "+996700111222", "c-1"));
        o.DueDate.Should().Be(new DateTime(2026, 11, 25));
        o.Items.Should().HaveCount(2);
        o.Items[0].Should().Be(new CustomerOrderItem("p-1", "Фильтр масляный", 2m, 500m, 1000m, "reserved"));
        o.Items[1].Qty.Should().Be(1m, "число вместо строки тоже читается");
        o.PrepaymentAmount.Should().Be(500m);
        o.PrepaymentRefunded.Should().BeFalse();
        (o.Total, o.LeftToPay).Should().Be((1900m, 1400m));
        o.Version.Should().Be("3");
        o.SaleId.Should().BeNull();
        CustomerOrdersApi.NumberOf(o.Number).Should().Be(7);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{"id":""}""")]
    [InlineData("""null""")]
    public void Garbage_is_not_an_order_and_never_throws(string json) =>
        CustomerOrderDto.Parse(JsonDocument.Parse(json).RootElement).Should().BeNull();

    [Fact]
    public void Refund_in_payments_marks_the_prepayment_as_returned_and_sale_is_read_from_object_or_string()
    {
        var o = Dto("""{"id":"x","status":"canceled","prepayment":{"amount":"300","payments":[{"kind":"prepayment","amount":"300"},{"kind":"refund","amount":"300"}]},"sale":{"id":"sale-9"}}""");
        o.PrepaymentRefunded.Should().BeTrue();
        o.SaleId.Should().Be("sale-9");
        Dto("""{"id":"y","sale":"sale-1"}""").SaleId.Should().Be("sale-1");
    }

    [Theory]
    [InlineData("draft", PartsOrderStatus.New)]
    [InlineData("confirmed", PartsOrderStatus.New)]
    [InlineData("reserved", PartsOrderStatus.Ordered)]
    [InlineData("waiting_supply", PartsOrderStatus.Ordered)]
    [InlineData("ready", PartsOrderStatus.Arrived)]
    [InlineData("delivered", PartsOrderStatus.Issued)]
    [InlineData("closed", PartsOrderStatus.Issued)]
    [InlineData("canceled", PartsOrderStatus.Cancelled)]
    [InlineData("что-то новое", PartsOrderStatus.New)]
    public void Server_status_maps_to_the_local_status(string server, string local) => PartsOrderRules.FromServerStatus(server).Should().Be(local);

    // ───────────── копия в файле заказов ─────────────

    [Fact]
    public void Server_orders_are_added_updated_and_do_not_touch_local_orders()
    {
        var store = Store();
        var local = new PartsOrder { ClientName = "Местный", Lines = { new PartsOrderLine { Title = "Свеча", Qty = 4, Price = 100 } }, Prepayment = 200 };
        store.Save(local);

        store.UpsertFromServer(new[] { Dto() }).Should().Be(1);
        var copy = store.All().Single(o => o.IsServerOrder);
        copy.ServerId.Should().Be("9f2c");
        copy.Number.Should().Be(7);
        copy.Status.Should().Be(PartsOrderStatus.Ordered);
        copy.ClientName.Should().Be("Айбек");
        copy.Lines.Should().HaveCount(2);
        copy.Lines[1].OnOrder.Should().BeTrue("строка ждёт товар от поставщика");
        copy.Prepayment.Should().Be(500m);
        copy.ServerVersion.Should().Be("3");
        PartsOrderRules.Remainder(copy).Should().Be(1400m, "«к доплате» считает сервер");

        var untouched = store.Find(local.Id)!;
        untouched.IsServerOrder.Should().BeFalse();
        untouched.Prepayment.Should().Be(200m);
        PartsOrderRules.PrepaymentsOnHand(store.All()).Should().Be(200m, "предоплата серверного заказа лежит в кассе смены, а не в конверте");

        store.UpsertFromServer(new[] { Dto() }).Should().Be(0, "ничего не изменилось");
        var delivered = Dto(Order7.Replace("\"status\":\"reserved\",\"client\"", "\"status\":\"delivered\",\"client\"").Replace("\"sale\":null", "\"sale\":\"sale-5\""));
        store.UpsertFromServer(new[] { delivered }).Should().Be(1);
        var after = store.All().Single(o => o.IsServerOrder);
        after.Status.Should().Be(PartsOrderStatus.Issued);
        after.ServerSaleId.Should().Be("sale-5");
        store.All().Count(o => o.IsServerOrder).Should().Be(1, "повторная загрузка не плодит копии");
    }

    [Fact]
    public void Old_file_without_server_fields_is_read_and_server_fields_survive_a_round_trip()
    {
        var path = Path.Combine(_dir, "old-orders.json");
        File.WriteAllText(path, """{"LastNumber":1,"Orders":[{"Id":"a1","Number":1,"ClientName":"Старый","Lines":[],"Status":"new"}]}""");
        var store = new PartsOrderStore(path);
        store.All().Single().IsServerOrder.Should().BeFalse();

        store.UpsertFromServer(new[] { Dto() });
        new PartsOrderStore(path).All().Single(o => o.IsServerOrder).ServerId.Should().Be("9f2c");
        File.ReadAllText(path).Should().NotContain("IsServerOrder");
    }

    // ───────────── запросы ─────────────

    [Fact]
    public async Task List_asks_for_open_statuses_and_follows_next_pages()
    {
        _fake.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/main/customer-orders/").WithParam("page", "1").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\":2,\"next\":\"https://x/?page=2\",\"previous\":null,\"results\":[" + Order7 + "]}"));
        _fake.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/main/customer-orders/").WithParam("page", "2").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\":2,\"next\":null,\"previous\":null,\"results\":[{\"id\":\"b2\",\"number\":\"8\",\"status\":\"ready\",\"items\":[]}]}"));
        var (api, _) = await LoginAsync();

        var list = await api.ListOpenAsync();

        list.Select(o => o.Id).Should().Equal("9f2c", "b2");
        var query = _fake.Server.LogEntries.First(e => e.RequestMessage.Path == "/api/main/customer-orders/").RequestMessage.Query!;
        query["status"].ToString().Should().Contain("confirmed").And.Contain("reserved").And.Contain("ready");
    }

    [Fact]
    public async Task Deliver_posts_the_payment_with_a_fresh_idempotency_key_and_reads_sale_and_change()
    {
        _fake.Json("POST", "/api/main/customer-orders/9f2c/deliver/", 201, """{"id":"9f2c","status":"delivered","sale_id":"sale-77","change":"100.00","total":"1900.00","left_to_pay":"0.00","version":4,"items":[]}""");
        var (api, _) = await LoginAsync();

        var result = await api.DeliverAsync("9f2c", "cash", 1400m);
        await api.DeliverAsync("9f2c", "cash", 1400m);

        result.SaleId.Should().Be("sale-77");
        result.Change.Should().Be(100m);
        result.Order!.Status.Should().Be("delivered");
        var posts = _fake.Server.LogEntries.Where(e => e.RequestMessage.Path == "/api/main/customer-orders/9f2c/deliver/").ToList();
        posts.Should().HaveCount(2);
        var body = JsonNode.Parse(posts[0].RequestMessage.Body!)!;
        body["payment"]!["method"]!.GetValue<string>().Should().Be("cash");
        body["payment"]!["received"]!.GetValue<string>().Should().Be("1400");
        var keys = posts.Select(p => p.RequestMessage.Headers!["Idempotency-Key"].First()).ToList();
        keys.Should().OnlyContain(k => k.StartsWith("co-deliver-"));
        keys.Distinct().Should().HaveCount(2, "ключ на попытку: отказ 409 shift_not_open не залипает при повторе после открытия смены");
        _fake.Server.LogEntries.Count(e => e.RequestMessage.Path.EndsWith("/deliver/")).Should().Be(2);
        _fake.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Path == "/api/main/pos/checkout/", "выдача — не обычный чек кассы (иначе 409 reserved_stock)");
    }

    [Fact]
    public async Task Card_payment_has_no_received_field()
    {
        _fake.Json("POST", "/api/main/customer-orders/9f2c/deliver/", 201, """{"id":"9f2c","status":"delivered","sale_id":"s","items":[]}""");
        var (api, _) = await LoginAsync();

        await api.DeliverAsync("9f2c", "card", null);

        var body = JsonNode.Parse(_fake.Server.LogEntries.Single(e => e.RequestMessage.Path.EndsWith("/deliver/")).RequestMessage.Body!)!;
        body["payment"]!["method"]!.GetValue<string>().Should().Be("card");
        body["payment"]!.AsObject().ContainsKey("received").Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_posts_an_empty_body_with_a_key_and_returns_the_refunded_order()
    {
        _fake.Json("POST", "/api/main/customer-orders/9f2c/cancel/", 200, """{"id":"9f2c","status":"canceled","prepayment":{"amount":"500","payments":[{"kind":"prepayment","amount":"500"},{"kind":"refund","amount":"500"}]},"items":[]}""");
        var (api, _) = await LoginAsync();

        var order = await api.CancelAsync("9f2c");

        order!.Status.Should().Be("canceled");
        order.PrepaymentRefunded.Should().BeTrue();
        var entry = _fake.Server.LogEntries.Single(e => e.RequestMessage.Path.EndsWith("/cancel/"));
        entry.RequestMessage.Headers!["Idempotency-Key"].First().Should().StartWith("co-cancel-");
        JsonNode.Parse(entry.RequestMessage.Body!)!.AsObject().Count.Should().Be(0);
    }

    [Fact]
    public async Task Get_returns_null_for_a_missing_order_and_the_feature_gate_is_off_without_the_server_flag()
    {
        _fake.Json("GET", "/api/main/customer-orders/zzz/", 404, """{"detail":"Not found."}""");
        var (api, _) = await LoginAsync();
        (await api.GetAsync("zzz")).Should().BeNull();

        NurMarketKassa.Services.Api.ServerCapabilities.SetFeaturesForTests();
        CustomerOrdersApi.TryCreate().Should().BeNull("без признака customer_orders серверных заказов в кассе нет");
        NurMarketKassa.Services.Api.ServerCapabilities.ResetMemoryForTests();
    }

    // ───────────── тексты ошибок ─────────────

    private static ApiException Err(int status, string body) => new("srv", status, JsonDocument.Parse(body).RootElement.Clone());

    [Theory]
    [InlineData(409, """{"code":"shift_not_open"}""", false, "Откройте смену")]
    [InlineData(409, """{"code":"order_waiting_supply"}""", false, "ещё не пришёл")]
    [InlineData(409, """{"code":"order_not_confirmed"}""", false, "не подтверждён")]
    [InlineData(409, """{"code":"order_closed"}""", false, "уже закрыт")]
    [InlineData(409, """{"code":"order_already_delivered"}""", false, "уже выдан")]
    [InlineData(409, """{"code":"order_reserved"}""", false, "зарезервирован")]
    [InlineData(409, """{"code":"prepayment_exceeds_total"}""", false, "больше суммы заказа")]
    [InlineData(409, """{"code":"version_conflict"}""", false, "другом устройстве")]
    [InlineData(409, """{"code":"reserved_stock"}""", false, "«Выдать»")]
    [InlineData(403, """{"detail":"Нет прав"}""", true, "правом «Касса»")]
    [InlineData(404, """{"detail":"Not found."}""", false, "не найден")]
    public void Errors_are_explained_in_russian(int status, string body, bool cancel, string fragment) =>
        CustomerOrderErrors.Text(Err(status, body), cancel).Should().Contain(fragment).And.NotContain("shift_not_open").And.NotContain("order_");

    [Fact]
    public void Error_texts_exist_in_all_five_languages()
    {
        var codes = new[] { "shift_not_open", "order_not_confirmed", "order_waiting_supply", "order_closed", "order_already_delivered", "order_reserved", "prepayment_exceeds_total", "version_conflict", "reserved_stock" };
        foreach (var code in codes)
        {
            var texts = new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek }.Select(l =>
            {
                UserPreferences.Instance.Language = l;
                return CustomerOrderErrors.Text(Err(409, "{\"code\":\"" + code + "\"}"));
            }).ToList();
            texts.Should().OnlyHaveUniqueItems($"{code}: у каждого языка свой текст");
        }
        UserPreferences.Instance.Language = AppLanguage.Russian;
    }
}
