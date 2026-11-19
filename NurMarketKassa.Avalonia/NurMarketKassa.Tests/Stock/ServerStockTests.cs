using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.Stock;

/// <summary>2026-11-19 (склад «10 из 10», К-4/К-5/К-8): разбор ответов партий, резервов, allocations, 409, настроек склада; и «без признаков — без изменений».</summary>
[Collection("ServerFeatures")]
public sealed class ServerStockTests : IDisposable
{
    public ServerStockTests()
    {
        ServerCapabilities.SetFeaturesForTests();
        ServerStock.SetPoliciesForTests(null);
        ServerStock.ReservationIdsForSale = Array.Empty<string>();
    }

    public void Dispose()
    {
        ServerCapabilities.SetFeaturesForTests();
        ServerStock.SetPoliciesForTests(null);
        ServerStock.ReservationIdsForSale = Array.Empty<string>();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ───────────── партии ─────────────

    [Fact]
    public void ParseBatches_SortsByExpiry_ReadsAllFields_AndStates()
    {
        var d = J("""
            {"results":[
              {"id":"b3","qty_in":"10","qty_left":"0","unit_cost":"5.5","lot_no":"C","supplier":"Без срока"},
              {"id":"b2","qty_in":10,"qty_left":4,"unit_cost":12.5,"expires_at":"2026-12-20","lot_no":"B","supplier":{"name":"ОсОО Ала"}},
              {"id":"b1","qty_in":"20.000","qty_left":"6.000","unit_cost":"10.0000","expires_at":"2026-11-10","produced_at":"2026-10-01","lot_no":"A-1","supplier":"Поставщик 1","created_at":"2026-10-02T09:00:00+06:00"}
            ]}
            """);
        var batches = ServerStock.ParseBatches(d);
        batches.Select(b => b.Id).Should().Equal("b1", "b2", "b3");
        batches[0].QtyIn.Should().Be(20);
        batches[0].QtyLeft.Should().Be(6);
        batches[0].UnitCost.Should().Be(10);
        batches[0].LotNo.Should().Be("A-1");
        batches[0].Supplier.Should().Be("Поставщик 1");
        batches[1].Supplier.Should().Be("ОсОО Ала");
        batches[2].ExpiresAt.Should().BeNull();

        var today = new DateTime(2026, 11, 19);
        batches[0].IsExpired(today).Should().BeTrue("срок 10.11 прошёл, остаток 6");
        batches[1].IsExpiring(today).Should().BeFalse("до 20.12 больше 30 дней");
        batches[1].IsExpiring(new DateTime(2026, 11, 25)).Should().BeTrue();
        batches[2].IsExpired(today).Should().BeFalse("срока нет");
        ServerStock.BatchState(batches[0], today).Should().NotBeEmpty();
        ServerStock.BatchState(batches[2], today).Should().BeEmpty();
    }

    [Fact]
    public void ParseBatches_EmptyOrOldAnswers_GiveNothing()
    {
        ServerStock.ParseBatches(J("[]")).Should().BeEmpty();
        ServerStock.ParseBatches(J("{}")).Should().BeEmpty();
        ServerStock.ParseBatches(J("{\"results\":[]}")).Should().BeEmpty();
        ServerStock.ParseBatches(J("\"nope\"")).Should().BeEmpty();
    }

    [Fact]
    public void ParseBatches_ExpiredBatchWithZeroLeft_IsNotExpired()
    {
        var b = ServerStock.ParseBatches(J("[{\"id\":\"x\",\"qty_in\":5,\"qty_left\":0,\"expires_at\":\"2026-01-01\"}]")).Single();
        b.IsExpired(new DateTime(2026, 11, 19)).Should().BeFalse("партия продана — просрочки нет");
    }

    // ───────────── резервы ─────────────

    [Fact]
    public void ParseReservations_ReadsProductQtyCommentAndExpiry()
    {
        var list = ServerStock.ParseReservations(J("""
            [{"id":"r1","product":{"id":"p1","name":"Молоко"},"qty":"3.000","comment":"для Аиды","expires_at":"2026-11-25","created_at":"2026-11-19T10:00:00+06:00","status":"active"},
             {"id":"r2","product_id":"p2","product_name":"Хлеб","quantity":1,"comment":""}]
            """));
        list.Should().HaveCount(2);
        list[0].ProductId.Should().Be("p1");
        list[0].ProductName.Should().Be("Молоко");
        list[0].Qty.Should().Be(3);
        list[0].Comment.Should().Be("для Аиды");
        list[0].ExpiresAt.Should().Be(new DateTime(2026, 11, 25));
        list[1].ProductId.Should().Be("p2");
        list[1].Qty.Should().Be(1);
        list[1].ExpiresAt.Should().BeNull();
        ServerStock.ParseReservations(J("{}")).Should().BeEmpty();
    }

    [Fact]
    public void TryParseInsufficient_RecognizesOnly409WithCode_AndReadsAvailable()
    {
        var ok = new ApiException("конфликт", 409, J("{\"code\":\"insufficient_available\",\"available\":\"2.5\"}"));
        ServerStock.TryParseInsufficient(ok, out var free).Should().BeTrue();
        free.Should().Be(2.5);

        var noNumber = new ApiException("insufficient_available", 409, null);
        ServerStock.TryParseInsufficient(noNumber, out var none).Should().BeTrue();
        none.Should().BeNull();

        ServerStock.TryParseInsufficient(new ApiException("x", 409, J("{\"code\":\"other\"}")), out _).Should().BeFalse();
        ServerStock.TryParseInsufficient(new ApiException("insufficient_available", 400, null), out _).Should().BeFalse();
    }

    // ───────────── allocations ─────────────

    [Fact]
    public void ParseAllocations_AndCostByBatches()
    {
        var line = J("""{"name":"Сыр","qty":5,"allocations":[{"batch_id":"b1","qty":"2","unit_cost":"10.00","expires_at":"2026-12-01","lot_no":"A"},{"batch_id":"b2","qty":3,"unit_cost":12,"lot_no":""}]}""");
        var a = ServerStock.ParseAllocations(line);
        a.Should().HaveCount(2);
        a[0].LotNo.Should().Be("A");
        a[0].ExpiresAt.Should().Be(new DateTime(2026, 12, 1));
        ServerStock.AllocationsCost(a).Should().Be(2 * 10 + 3 * 12);

        ServerStock.ParseAllocations(J("{\"name\":\"Старый чек\",\"qty\":1}")).Should().BeEmpty("старый сервер или партии выключены");
        ServerStock.AllocationsCost(Array.Empty<SaleAllocation>()).Should().BeNull();
    }

    // ───────────── stock/expiring ─────────────

    [Fact]
    public void ParseExpiring_GroupsByProduct_NearestDate()
    {
        var today = new DateTime(2026, 11, 19);
        var rows = ServerStock.ParseExpiring(J("""
            {"results":[{"product_id":"p1","name":"Йогурт","expires_at":"2026-11-25","qty_left":"4"},
                        {"product_id":"p1","name":"Йогурт","expires_at":"2026-11-10","qty_left":"2"},
                        {"product_id":"p2","name":"Кефир","expires_at":"2026-12-05","qty_left":"7"}]}
            """), today)!;
        rows.Should().HaveCount(2);
        var p1 = rows.Single(r => r.ProductId == "p1");
        p1.Date.Should().Be(new DateTime(2026, 11, 10));
        p1.Expired.Should().BeTrue();
        p1.Quantity.Should().Be(6);
        rows.Single(r => r.ProductId == "p2").Expired.Should().BeFalse();
        ServerStock.ParseExpiring(J("{\"foo\":1}"), today).Should().BeNull("форма ответа не распознана — берём прежний отчёт");
    }

    // ───────────── признаки и «без признаков — без изменений» ─────────────

    [Fact]
    public void Capabilities_Parse_RecognizesNewNames()
    {
        var set = ServerCapabilities.Parse(J("{\"features\":[\"stock_batches\",\"costing_fifo\",\"expiry_batches\",\"expired_sale_policy\",\"reservations\"]}"));
        set.Should().Contain(new[] { ServerCapabilities.StockBatches, ServerCapabilities.CostingFifo, ServerCapabilities.ExpiryBatches, ServerCapabilities.ExpiredSalePolicy, ServerCapabilities.Reservations });
    }

    [Fact]
    public void WithoutFeatures_NothingChanges()
    {
        ServerStock.BatchesOn.Should().BeFalse();
        ServerStock.ExpiryFieldsOn.Should().BeFalse();
        ServerStock.FifoOn.Should().BeFalse();
        ServerStock.ReservationsOn.Should().BeFalse();
        ServerStock.CachedPolicies.Should().BeNull();

        // тело строки прихода — прежнее, даже если срок и партия заполнены
        var item = new Dictionary<string, object?> { ["product"] = "p1", ["qty"] = 2 };
        ServerStock.ApplyReceiptBatchFields(item, new DateTime(2026, 12, 1), null, null, "A-1");
        item.Keys.Should().Equal("product", "qty");

        // тело оплаты — прежнее, даже если резерв выбран
        ServerStock.ReservationIdsForSale = new[] { "r1" };
        var body = new JsonObject { ["total"] = "10.00" };
        ServerStock.ApplyReservationIds(body);
        body.ContainsKey("reservation_ids").Should().BeFalse();
    }

    [Fact]
    public void PoliciesFromServerAreIgnored_WithoutFeature()
    {
        ServerStock.SetPoliciesForTests(new StockPolicies("fifo", true, StockPolicies.Block, StockPolicies.Block));
        ServerStock.CachedPolicies.Should().BeNull("признака политик нет — настройки не применяются");
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.ExpiredSalePolicy);
        ServerStock.SetPoliciesForTests(new StockPolicies("fifo", true, StockPolicies.Block, StockPolicies.Warn));
        ServerStock.CachedPolicies!.ExpiredSalePolicy.Should().Be(StockPolicies.Block);
    }

    [Fact]
    public void ReceiptBatchFields_WithFeature_ExpiryBeatsProducedPlusShelfLife()
    {
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.ExpiryBatches);
        var withExpiry = new Dictionary<string, object?> { ["product"] = "p1" };
        ServerStock.ApplyReceiptBatchFields(withExpiry, new DateTime(2026, 12, 1), new DateTime(2026, 10, 1), 60, " A-1 ");
        withExpiry["expires_at"].Should().Be("2026-12-01");
        withExpiry["lot_no"].Should().Be("A-1");
        withExpiry.ContainsKey("produced_at").Should().BeFalse();

        var withShelf = new Dictionary<string, object?>();
        ServerStock.ApplyReceiptBatchFields(withShelf, null, new DateTime(2026, 10, 1), 60, "");
        withShelf["produced_at"].Should().Be("2026-10-01");
        withShelf["shelf_life_days"].Should().Be(60);
        withShelf.ContainsKey("expires_at").Should().BeFalse();
        withShelf.ContainsKey("lot_no").Should().BeFalse();

        var nothing = new Dictionary<string, object?>();
        ServerStock.ApplyReceiptBatchFields(nothing, null, new DateTime(2026, 10, 1), null, null);
        nothing.Should().BeEmpty("дата выпуска без срока хранения не отправляется");
    }

    [Fact]
    public void ReservationIds_WithFeature_GoToCheckoutBody()
    {
        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.Reservations);
        ServerStock.ReservationIdsForSale = new[] { "r1", "r2" };
        var body = new JsonObject();
        ServerStock.ApplyReservationIds(body);
        body["reservation_ids"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("r1", "r2");
    }

    // ───────────── правила продажи ─────────────

    [Theory]
    [InlineData("allow", true, StockSaleDecision.Ok)]
    [InlineData("warn", true, StockSaleDecision.Warn)]
    [InlineData("block", true, StockSaleDecision.Block)]
    [InlineData("block", false, StockSaleDecision.Ok)]
    [InlineData(null, true, StockSaleDecision.Ok)]
    public void ExpiredSale_FollowsPolicy(string? policy, bool expired, StockSaleDecision expected) =>
        ServerStock.ExpiredSale(policy, expired).Should().Be(expected);

    [Theory]
    [InlineData("warn", 5.0, 3.0, StockSaleDecision.Ok)]
    [InlineData("warn", 5.0, 5.0, StockSaleDecision.Ok)]
    [InlineData("warn", 5.0, 6.0, StockSaleDecision.Warn)]
    [InlineData("block", 5.0, 6.0, StockSaleDecision.Block)]
    [InlineData("block", null, 6.0, StockSaleDecision.Ok)]
    public void OverAvailable_FollowsPolicy(string policy, double? free, double wanted, StockSaleDecision expected) =>
        ServerStock.OverAvailable(policy, free, wanted).Should().Be(expected);

    [Fact]
    public void ParsePolicies_ReadsSettings()
    {
        var p = ServerStock.ParsePolicies(J("{\"costing_method\":\"fifo\",\"batches\":true,\"expired_sale_policy\":\"WARN\",\"reserved_sale_policy\":\"block\"}"))!;
        p.CostingMethod.Should().Be("fifo");
        p.Batches.Should().BeTrue();
        p.ExpiredSalePolicy.Should().Be("warn");
        p.ReservedSalePolicy.Should().Be("block");
        var odd = ServerStock.ParsePolicies(J("{\"expired_sale_policy\":\"???\"}"))!;
        odd.ExpiredSalePolicy.Should().Be("allow");
        odd.ReservedSalePolicy.Should().Be("warn");
        ServerStock.ParsePolicies(J("[]")).Should().BeNull();
    }

    // ───────────── запросы к серверу (WireMock) ─────────────

    private static NurMarketApiClient Client(WireMockServer server) => new(new AppSettings { ApiBaseUrl = server.Url! });

    private static async Task<NurMarketApiClient> LoggedIn(WireMockServer server)
    {
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        var api = Client(server);
        await api.LoginAsync("a@b.kg", "12345678");
        return api;
    }

    [Fact]
    public async Task Api_ReadsBatchesAndReservations_ReserveSendsIdempotencyKey_And409IsRecognized()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/main/products/p1/batches/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[{\"id\":\"b1\",\"qty_in\":5,\"qty_left\":5,\"lot_no\":\"A\"}]"));
        server.Given(Request.Create().WithPath("/api/main/reservations/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[{\"id\":\"r1\",\"product\":{\"id\":\"p1\",\"name\":\"Молоко\"},\"qty\":2}]"));
        server.Given(Request.Create().WithPath("/api/main/reservations/").UsingMethod("POST").WithBody(b => b != null && !b.Contains("allow_over"))).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(409).WithHeader("Content-Type", "application/json").WithBody("{\"code\":\"insufficient_available\",\"available\":\"1\"}"));
        server.Given(Request.Create().WithPath("/api/main/reservations/").UsingMethod("POST")).AtPriority(2)
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"r9\"}"));
        server.Given(Request.Create().WithPath("/api/main/reservations/r1/").UsingMethod("DELETE"))
            .RespondWith(Response.Create().WithStatusCode(204));
        var api = await LoggedIn(server);

        (await StockServerApi.BatchesAsync("p1", false, api)).Should().ContainSingle().Which.LotNo.Should().Be("A");
        (await StockServerApi.ReservationsAsync(api)).Should().ContainSingle().Which.ProductName.Should().Be("Молоко");

        var act = async () => await StockServerApi.ReserveAsync("p1", 5, "тест", null, false, "key-1", api);
        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ServerStock.TryParseInsufficient(ex, out var free).Should().BeTrue();
        free.Should().Be(1);

        await StockServerApi.ReserveAsync("p1", 5, "тест", new DateTime(2026, 11, 30), true, "key-2", api);
        var posts = server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path == "/api/main/reservations/").ToList();
        posts.Should().HaveCount(2);
        posts[0].RequestMessage.Headers!["Idempotency-Key"].Single().Should().Be("key-1");
        posts[1].RequestMessage.Headers!["Idempotency-Key"].Single().Should().Be("key-2");
        using var sent = JsonDocument.Parse(posts[1].RequestMessage.Body!);
        sent.RootElement.GetProperty("allow_over").GetBoolean().Should().BeTrue();
        sent.RootElement.GetProperty("product").GetString().Should().Be("p1");
        sent.RootElement.GetProperty("expires_at").GetString().Should().Be("2026-11-30");

        await StockServerApi.ReleaseAsync("r1", api);
        server.LogEntries.Should().Contain(e => e.RequestMessage.Method == "DELETE" && e.RequestMessage.Path == "/api/main/reservations/r1/");
        server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "PUT", "настройки склада касса не пишет");
    }

    // ───────────── ответы бэкенда 19.11 (вечер): форма stock/expiring, settings/stock, 409 ─────────────

    [Fact]
    public void ParseExpiring_ServerShape_RemainingAndExpiredFlag()
    {
        var rows = ServerStock.ParseExpiring(J("""
            {"days":30,"today":"2026-11-19","count":2,"page":1,"page_size":50,"source":"batches","results":[
              {"batch_id":"b1","product_id":"p1","name":"Йогурт","remaining":"4.000","expires_at":"2026-11-25","days_left":6,"expired":false,"received_at":"2026-11-01","lot_no":"L1"},
              {"batch_id":"b2","product_id":"p2","name":"Кефир","remaining":"7","expires_at":"2026-11-15","days_left":-4,"expired":true,"received_at":"2026-10-20","lot_no":""}]}
            """), new DateTime(2026, 11, 19))!;
        rows.Single(r => r.ProductId == "p1").Quantity.Should().Be(4);
        rows.Single(r => r.ProductId == "p1").DaysLeft.Should().Be(6);
        rows.Single(r => r.ProductId == "p2").Expired.Should().BeTrue();
        rows.Single(r => r.ProductId == "p2").Quantity.Should().Be(7);
    }

    [Fact]
    public void ParsePolicies_ReadsNegativeStockPolicy_AndLists()
    {
        var p = ServerStock.ParsePolicies(J("""
            {"negative_stock_policy":"warn","costing_method":"fifo","batches":true,"expired_sale_policy":"block","reserved_sale_policy":"warn",
             "negative_stock_policies":["allow","warn","block"],"costing_methods":["avg","fifo"]}
            """))!;
        p.NegativeStockPolicy.Should().Be("warn");
        p.CostingMethod.Should().Be("fifo");
        p.ExpiredSalePolicy.Should().Be(StockPolicies.Block);
    }

    [Fact]
    public void TryParseInsufficient_ServerShape_TopLevelNumbersAsStrings()
    {
        var ex = new ApiException("detail", 409, J("{\"code\":\"insufficient_available\",\"detail\":\"Свободного не хватает\",\"available\":\"3.000\",\"reserved\":\"2.000\",\"quantity\":\"5.000\"}"));
        ServerStock.TryParseInsufficient(ex, out var free).Should().BeTrue();
        free.Should().Be(3);
        var asNumbers = new ApiException("detail", 409, J("{\"code\":\"insufficient_available\",\"available\":3,\"reserved\":2,\"quantity\":5}"));
        ServerStock.TryParseInsufficient(asNumbers, out var free2).Should().BeTrue();
        free2.Should().Be(3);
    }

    [Fact]
    public async Task Api_Expiring_SendsDaysAndPaging_Reservations_SendFilters()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/main/stock/expiring/").UsingMethod("GET").WithParam("page", "1"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                "{\"days\":14,\"today\":\"2026-11-19\",\"count\":2,\"page\":1,\"page_size\":1,\"source\":\"batches\",\"results\":[{\"batch_id\":\"b1\",\"product_id\":\"p1\",\"name\":\"А\",\"remaining\":\"1\",\"expires_at\":\"2026-11-25\",\"days_left\":6,\"expired\":false}]}"));
        server.Given(Request.Create().WithPath("/api/main/stock/expiring/").UsingMethod("GET").WithParam("page", "2"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(
                "{\"days\":14,\"today\":\"2026-11-19\",\"count\":2,\"page\":2,\"page_size\":1,\"source\":\"batches\",\"results\":[{\"batch_id\":\"b2\",\"product_id\":\"p2\",\"name\":\"Б\",\"remaining\":\"2\",\"expires_at\":\"2026-11-28\",\"days_left\":9,\"expired\":false}]}"));
        server.Given(Request.Create().WithPath("/api/main/reservations/").UsingMethod("GET"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"results\":[]}"));
        var api = await LoggedIn(server);

        var rows = (await StockServerApi.ExpiringAsync(api, default, days: 14))!;
        rows.Select(r => r.ProductId).Should().Equal("p1", "p2");
        var first = server.LogEntries.First(e => e.RequestMessage.Path == "/api/main/stock/expiring/").RequestMessage;
        first.Query!["days"].Single().Should().Be("14");

        await StockServerApi.ReservationsAsync(api, default, product: "p1", source: "order", sourceId: "o5", status: "active");
        var q = server.LogEntries.Last(e => e.RequestMessage.Path == "/api/main/reservations/").RequestMessage.Query!;
        q["product"].Single().Should().Be("p1");
        q["source"].Single().Should().Be("order");
        q["source_id"].Single().Should().Be("o5");
        q["status"].Single().Should().Be("active");
    }

    // ───────────── карточка товара: reserved / available ─────────────

    [Fact]
    public void ProductMapper_ReservedInfo_OnlyWhenServerGivesReserved()
    {
        var plain = ProductCatalogMapper.TryTile(J("{\"id\":\"1\",\"name\":\"Молоко\",\"price\":\"50\",\"quantity\":\"10\",\"unit\":\"шт\"}"), "https://x/");
        plain.Should().NotBeNull();
        plain!.Reserved.Should().BeNull();
        plain.HasReservedInfo.Should().BeFalse("у старой компании строка остатка прежняя");

        var withReserve = ProductCatalogMapper.TryTile(J("{\"id\":\"2\",\"name\":\"Молоко\",\"price\":\"50\",\"quantity\":\"10\",\"unit\":\"шт\",\"reserved\":\"3\",\"available\":\"7\"}"), "https://x/");
        withReserve!.Reserved.Should().Be(3);
        withReserve.Available.Should().Be(7);
        withReserve.ReservedInfo.Should().Contain("7").And.Contain("3");

        var zero = ProductCatalogMapper.TryTile(J("{\"id\":\"3\",\"name\":\"Молоко\",\"price\":\"50\",\"quantity\":\"10\",\"unit\":\"шт\",\"reserved\":\"0\",\"available\":\"10\"}"), "https://x/");
        zero!.Available.Should().Be(10);
        zero.HasReservedInfo.Should().BeFalse("резервов нет — подпись не нужна");
    }
}
