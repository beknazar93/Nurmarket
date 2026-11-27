using FluentAssertions;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-26 (сервер Умара: analytics/market/?tab=sales принимает include_stock=0 — без tables.products_stock, 2,8 МБ → 4 КБ): параметр уходит только
/// запросам «Продаж» для сводки ИИ (withoutStock: true); обычный запрос и «Товары» без него; ответ без таблицы остатков — те же cards и charts.</summary>
[Collection("ServerFeatures")]
public sealed class SalesReportIncludeStockTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    [Fact]
    public async Task WithoutStock_sends_include_stock_0_only_for_ai_requests()
    {
        _fake.Json("GET", "/api/main/analytics/market/", 200, "{\"cards\":{\"revenue\":\"1000\",\"transactions\":4},\"charts\":{\"sales_dynamics\":[]}}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        // период в прошлом, у каждого свой — кэш отчётов не склеит запросы
        var lean = await sales.MarketSalesReportAsync(new DateTime(2026, 3, 1), new DateTime(2026, 3, 1), default, withoutStock: true);
        var full = await sales.MarketSalesReportAsync(new DateTime(2026, 3, 2), new DateTime(2026, 3, 2));

        lean.GetProperty("cards").GetProperty("revenue").GetString().Should().Be("1000", "ответ без products_stock разбирается как обычный");
        full.GetProperty("cards").GetProperty("transactions").GetInt32().Should().Be(4);
        var queries = Requests(_fake, "GET", "/analytics/market/").Select(r => r.RequestMessage.Url).ToList();
        queries.Should().ContainSingle(u => u.Contains("period_start=2026-03-01") && u.Contains("include_stock=0"));
        queries.Should().ContainSingle(u => u.Contains("period_start=2026-03-02") && !u.Contains("include_stock"));
    }

    [Fact]
    public async Task WithoutStock_reuses_ready_full_report_of_same_period()
    {
        _fake.Json("GET", "/api/main/analytics/market/", 200, "{\"cards\":{\"revenue\":\"5\"}}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var d = new DateTime(2026, 3, 5);
        await sales.MarketSalesReportAsync(d, d);
        await sales.MarketSalesReportAsync(d, d, default, withoutStock: true);
        Requests(_fake, "GET", "/analytics/market/").Should().ContainSingle("готовый полный ответ шире — второй запрос не нужен");
    }
}
