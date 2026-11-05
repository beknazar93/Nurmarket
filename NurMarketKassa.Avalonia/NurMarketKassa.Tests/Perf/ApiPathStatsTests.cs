using FluentAssertions;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Perf;

/// <summary>2026-11-05 (аудит скорости, Н-1 и 18.1): форматирование и агрегация замеров API — чистые функции, без сети.</summary>
public sealed class ApiPathStatsTests
{
    [Theory]
    [InlineData("/api/products/", "/api/products/")]
    [InlineData("/api/products/123/", "/api/products/{id}/")]
    [InlineData("/api/clients/4f8c2c1e-9a53-4f1b-8f7d-0123456789ab/debts/", "/api/clients/{id}/debts/")]
    [InlineData("/api/products/barcode/4870001234567/", "/api/products/barcode/{id}/")]
    [InlineData("/api/clients/phone/+996555123456/", "/api/clients/phone/{id}/")]
    [InlineData("/api/users/ivan@mail.kg/", "/api/users/{id}/")]
    [InlineData("/api/sales/?client=777&phone=0555123456", "/api/sales/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void NormalizePath_DropsValuesAndQuery(string? input, string expected)
        => ApiPathStats.NormalizePath(input).Should().Be(expected);

    [Fact]
    public void NormalizePath_KeepsOrdinaryWords()
        => ApiPathStats.NormalizePath("/api/stock-moves/warehouses/").Should().Be("/api/stock-moves/warehouses/");

    [Fact]
    public void BuildReport_NoData_ReturnsNull()
        => ApiPathStats.BuildReport(Array.Empty<KeyValuePair<string, (long Count, long TotalMs)>>(), TimeSpan.FromMinutes(5)).Should().BeNull();

    [Fact]
    public void BuildReport_TakesTenMostFrequent_InDescendingOrder_WithTotals()
    {
        var snapshot = new List<KeyValuePair<string, (long Count, long TotalMs)>>();
        for (var i = 1; i <= 14; i++)
            snapshot.Add(new($"GET /api/path{i:00}/", (i, i * 100L)));

        var line = ApiPathStats.BuildReport(snapshot, TimeSpan.FromMinutes(5))!;

        line.Should().StartWith("за 5 мин: 105 запр., путей 14; топ-10: ");
        line.Should().Contain("GET /api/path14/ ×14 (ср. 100 мс)");
        line.Should().Contain("GET /api/path05/ ×5");
        line.Should().NotContain("GET /api/path04/");
        line.IndexOf("path14", StringComparison.Ordinal).Should().BeLessThan(line.IndexOf("path13", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildReport_SameCount_OrderIsStableByName()
    {
        var snapshot = new List<KeyValuePair<string, (long Count, long TotalMs)>>
        {
            new("GET /b/", (3, 30)), new("GET /a/", (3, 90)),
        };
        var line = ApiPathStats.BuildReport(snapshot, TimeSpan.FromMinutes(5))!;
        line.IndexOf("GET /a/", StringComparison.Ordinal).Should().BeLessThan(line.IndexOf("GET /b/", StringComparison.Ordinal));
        line.Should().Contain("GET /a/ ×3 (ср. 30 мс)");
    }

    [Fact]
    public void FormatServerTimingSuffix_BothHeaders()
        => ApiPathStats.FormatServerTimingSuffix("req-7f3a9c", "5400").Should().Be(" (сервер 5400 мс, x-request-id req-7f3a9c)");

    [Fact]
    public void FormatServerTimingSuffix_OnlyOne_OrNone()
    {
        ApiPathStats.FormatServerTimingSuffix(null, "120").Should().Be(" (сервер 120 мс)");
        ApiPathStats.FormatServerTimingSuffix("abc", null).Should().Be(" (x-request-id abc)");
        ApiPathStats.FormatServerTimingSuffix(null, null).Should().BeEmpty();
        ApiPathStats.FormatServerTimingSuffix("  ", "").Should().BeEmpty();
    }

    [Fact]
    public void FormatServerTimingSuffix_StripsControlCharacters_AndLimitsLength()
    {
        var evil = "ab\r\ncd\"; drop" + new string('x', 200);
        var s = ApiPathStats.FormatServerTimingSuffix(evil, "12 3");
        s.Should().NotContain("\r").And.NotContain("\n").And.NotContain("\"");
        s.Length.Should().BeLessThan(120);
    }

    [Fact]
    public void FormatServerTimingSuffix_FromResponse_ReadsHeaders()
    {
        using var resp = new System.Net.Http.HttpResponseMessage();
        resp.Headers.TryAddWithoutValidation("x-request-id", "r-1");
        resp.Headers.TryAddWithoutValidation("x-response-time-ms", "250");
        ApiPathStats.FormatServerTimingSuffix(resp).Should().Be(" (сервер 250 мс, x-request-id r-1)");
        ApiPathStats.FormatServerTimingSuffix((System.Net.Http.HttpResponseMessage?)null).Should().BeEmpty();
    }
}
