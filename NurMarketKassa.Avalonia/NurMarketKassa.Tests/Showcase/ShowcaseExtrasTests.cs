using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;

namespace NurMarketKassa.Tests.Showcase;

/// <summary>2026-11-08 (ответ Умара 08.11, п.37–39): отзывы с модерацией, коллекции витрины и «Примерить в магазине» против поддельного NurCRM (WireMock).
/// Настоящий app.nurcrm.kg не дёргается.</summary>
public sealed class ShowcaseExtrasTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();

    public void Dispose() => _server.Dispose();

    private async Task<ShowcaseExtrasApiService> LoginAsync()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new ShowcaseExtrasApiService(api);
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private string LastBody(string method) =>
        _server.Server.LogEntries.Last(e => e.RequestMessage.Method == method && e.RequestMessage.Path.Contains("/showcase/")).RequestMessage.Body!;

    private const string ReviewsBody = """
        {"count":2,"next":null,"pending_count":3,"results":[
         {"id":"r1","product":{"id":"p1","name":"Куртка"},"rating":5,"text":"Отлично","author_name":"Айбек","status":"pending","reply":"","created_at":"2026-11-07T10:00:00+06:00"},
         {"id":"r2","product":"p2","rating":2,"text":"Мал","author_name":"Нурия","status":"approved","reply":"Спасибо","created_at":"2026-11-08T09:00:00+06:00","author_phone":"+996700000000","extra":1}]}
        """;

    [Fact]
    public async Task ListReviews_ParsesWrapper_PendingCount_AndIgnoresUnknownFields()
    {
        _server.Json("GET", "/api/main/showcase/reviews/", 200, ReviewsBody);
        var api = await LoginAsync();
        var page = await api.ListReviewsAsync("pending", "p1");
        page.PendingCount.Should().Be(3);
        page.Items.Should().HaveCount(2);
        page.Items[0].Id.Should().Be("r2"); // новые сверху
        var r1 = page.Items.Single(r => r.Id == "r1");
        (r1.ProductId, r1.ProductName, r1.Rating, r1.AuthorName, r1.Status).Should().Be(("p1", "Куртка", 5, "Айбек", "pending"));
        page.Items.Single(r => r.Id == "r2").ProductId.Should().Be("p2");
        var req = _server.Server.LogEntries.Last(e => e.RequestMessage.Path == "/api/main/showcase/reviews/").RequestMessage;
        req.Query!["status"].Should().Contain("pending");
        req.Query["product"].Should().Contain("p1");
    }

    [Fact]
    public async Task ListReviews_PlainArray_Works_WithoutPendingCount()
    {
        _server.Json("GET", "/api/main/showcase/reviews/", 200, """[{"id":"r9","rating":4,"text":"ok","author_name":"A","status":"rejected"}]""");
        var page = await (await LoginAsync()).ListReviewsAsync(null, null);
        page.PendingCount.Should().BeNull();
        page.Items.Single().Status.Should().Be("rejected");
    }

    [Fact]
    public async Task ListReviews_403_ThrowsApiExceptionWith403()
    {
        _server.Error("GET", "/api/main/showcase/reviews/", 403, "forbidden");
        var api = await LoginAsync();
        var act = () => api.ListReviewsAsync("pending", null);
        (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task ModerateReview_SendsPatchWithStatusAndReply()
    {
        _server.Json("PATCH", "/api/main/showcase/reviews/r1/", 200, """{"id":"r1","status":"approved","reply":"Спасибо!","rating":5}""");
        var api = await LoginAsync();
        var updated = await api.ModerateReviewAsync("r1", "approved", "Спасибо!");
        updated!.Status.Should().Be("approved");
        var body = J(LastBody("PATCH"));
        body.GetProperty("status").GetString().Should().Be("approved");
        body.GetProperty("reply").GetString().Should().Be("Спасибо!");
    }

    [Fact]
    public async Task DeleteReview_SendsDelete_AndAcceptsEmpty204()
    {
        _server.Server.Given(Request.Create().WithPath("/api/main/showcase/reviews/r1/").UsingMethod("DELETE"))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(204));
        var api = await LoginAsync();
        await api.DeleteReviewAsync("r1");
        _server.Server.LogEntries.Should().Contain(e => e.RequestMessage.Method == "DELETE" && e.RequestMessage.Path == "/api/main/showcase/reviews/r1/");
    }

    [Fact]
    public async Task ListCollections_ParsesTitlesProductsOrderAndDates()
    {
        _server.Json("GET", "/api/main/showcase/collections/", 200, """
            {"results":[
             {"id":"c2","title":{"ru":"Лето"},"position":2,"is_active":false,"products":["a","b"]},
             {"id":"c1","slug":"spring","title":{"ru":"Весна","ky":"Жаз","en":"Spring"},"description":"Подборка","season":"Весна 2027","cover":{"id":"m1","url":"x"},
              "products":[{"id":"p3","name":"Кеды"},"p1","p2"],"is_active":true,"position":1,"starts_at":"2027-03-01T00:00:00+06:00","ends_at":"2027-05-31T23:59:59+06:00"}]}
            """);
        var list = await (await LoginAsync()).ListCollectionsAsync();
        list.Select(c => c.Id).Should().Equal("c1", "c2"); // по position
        var c = list[0];
        c.ProductIds.Should().Equal("p3", "p1", "p2");
        c.ProductNames["p3"].Should().Be("Кеды");
        c.TitleFor("ky").Should().Be("Жаз");
        c.TitleFor("tr").Should().Be("Весна"); // нет перевода — русский
        c.CoverId.Should().Be("m1");
        c.StartsAt.Should().NotBeNull();
        list[1].IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task CreateCollection_PostsBodyWithProductOrder()
    {
        _server.Json("POST", "/api/main/showcase/collections/", 201, """{"id":"c9","title":{"ru":"Весна"},"products":["b","a","c"]}""");
        var api = await LoginAsync();
        var created = await api.CreateCollectionAsync(new ShowcaseCollectionDraft
        {
            Titles = new Dictionary<string, string> { ["ru"] = " Весна ", ["ky"] = "" },
            Description = "Описание",
            Season = "Весна",
            ProductIds = new[] { "b", "a", "c" },
            IsActive = true,
            Position = 4,
        });
        created!.ProductIds.Should().Equal("b", "a", "c");
        var body = J(LastBody("POST"));
        body.GetProperty("title").GetProperty("ru").GetString().Should().Be("Весна");
        body.GetProperty("title").TryGetProperty("ky", out _).Should().BeFalse(); // пустые языки не шлются
        body.GetProperty("products").EnumerateArray().Select(x => x.GetString()).Should().Equal("b", "a", "c");
        body.GetProperty("position").GetInt32().Should().Be(4);
        body.TryGetProperty("cover", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ToggleAndDeleteCollection_UseCorrectMethods()
    {
        _server.Json("PATCH", "/api/main/showcase/collections/c1/", 200, """{"id":"c1","title":{"ru":"X"},"is_active":false}""");
        _server.Json("DELETE", "/api/main/showcase/collections/c1/", 200, "{}");
        var api = await LoginAsync();
        await api.SetCollectionActiveAsync("c1", false);
        J(LastBody("PATCH")).GetProperty("is_active").GetBoolean().Should().BeFalse();
        await api.DeleteCollectionAsync("c1");
        _server.Server.LogEntries.Should().Contain(e => e.RequestMessage.Method == "DELETE");
    }

    [Fact]
    public void MoveId_SwapsNeighbours_AndStaysInRange()
    {
        var ids = new List<string> { "a", "b", "c" };
        ShowcaseExtrasApiService.MoveId(ids, 2, -1);
        ids.Should().Equal("a", "c", "b");
        ShowcaseExtrasApiService.MoveId(ids, 0, -1); // за край — без изменений
        ShowcaseExtrasApiService.MoveId(ids, 2, 1);
        ids.Should().Equal("a", "c", "b");
        ShowcaseExtrasApiService.MoveId(ids, 0, 1);
        ids.Should().Equal("c", "a", "b");
    }

    [Fact]
    public void BuildBody_ClearCoverAndDates_SendNulls()
    {
        var body = ShowcaseExtrasApiService.BuildBody(new ShowcaseCollectionDraft
        {
            Titles = new Dictionary<string, string> { ["ru"] = "A" }, ClearCover = true, ClearDates = true, ProductIds = new[] { "x" },
        });
        body.ContainsKey("cover").Should().BeTrue();
        body["cover"].Should().BeNull();
        body.ContainsKey("starts_at").Should().BeTrue();
        body["ends_at"].Should().BeNull();
    }

    [Fact]
    public void ParseOrder_TryOn_ReadsHoldUntilFromDeliveryObject()
    {
        var order = ShowcaseApiService.ParseOrder(J("""
            {"id":"o1","order_number":"11","status":"new","total":"1500.00","delivery":{"type":"try_on","hold_until":"2026-11-09T18:30:00+06:00"},"items":[]}
            """));
        order.IsTryOn.Should().BeTrue();
        order.Delivery.Should().Be("try_on");
        order.HoldUntil.Should().Be(DateTimeOffset.Parse("2026-11-09T18:30:00+06:00"));
    }

    [Fact]
    public void ParseOrder_TopLevelHoldUntil_AndPlainOrdersUnchanged()
    {
        var staff = ShowcaseApiService.ParseOrder(J("""{"id":"o2","status":"ready","hold_until":"2026-11-09T18:30:00+06:00","delivery_type":"try_on"}"""));
        staff.IsTryOn.Should().BeTrue();
        staff.HoldUntil.Should().NotBeNull();
        var plain = ShowcaseApiService.ParseOrder(J("""{"id":"o3","status":"new","delivery":"pickup","hold_until":null}"""));
        plain.IsTryOn.Should().BeFalse();
        plain.Delivery.Should().Be("pickup");
        plain.HoldUntil.Should().BeNull();
    }
}
