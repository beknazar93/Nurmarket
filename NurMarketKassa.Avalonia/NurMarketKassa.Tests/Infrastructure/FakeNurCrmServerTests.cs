using System.Net.Http;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-05 (аудит №4, волна 0-A): касса против поддельного NurCRM (WireMock). Клиент настоящий (NurMarketApiClient),
/// сервер — заглушка на localhost; данные — во временной NURMARKET_DATA_ROOT.</summary>
public sealed class FakeNurCrmServerTests : IDisposable
{
    private readonly FakeNurCrmServer _server = new();

    public void Dispose() => _server.Dispose();

    private NurMarketApiClient NewClient() => new(new AppSettings { ApiBaseUrl = _server.Url });

    [Fact]
    public async Task Login_ThenProfile_Works()
    {
        var api = NewClient();
        await api.LoginAsync("cashier@test.kg", "12345678");
        api.AccessToken.Should().Be("test-access");

        var profile = await api.GetProfileAsync();
        profile.GetProperty("email").GetString().Should().Be("cashier@test.kg");
    }

    [Fact]
    public async Task Checkout_SendsIdempotencyKey_AndGetsSale()
    {
        var api = NewClient();
        await api.LoginAsync("cashier@test.kg", "12345678");

        var sale = await api.PostWithIdempotencyKeyAsync("api/main/pos/checkout/", new { payment_method = "cash" }, "key-123");

        sale.GetProperty("id").GetString().Should().Be("sale-1");
        var checkout = _server.Server.LogEntries.Last(e => e.RequestMessage.Path == "/api/main/pos/checkout/");
        checkout.RequestMessage.Headers!["Idempotency-Key"].Should().ContainSingle().Which.Should().Be("key-123");
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task ServerError_BecomesApiException_WithStatus(int status)
    {
        _server.Error("GET", "/api/users/profile/", status);
        var api = NewClient();
        await api.LoginAsync("cashier@test.kg", "12345678");

        var act = async () => await api.GetProfileAsync();

        (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task SlowServer_RequestTimeout_DoesNotHang()
    {
        _server.Slow("GET", "/api/main/products/list/", TimeSpan.FromSeconds(5));
        var api = NewClient();
        await api.LoginAsync("cashier@test.kg", "12345678");

        var started = DateTime.UtcNow;
        var act = async () => await api.RequestAsync(HttpMethod.Get, "api/main/products/list/", null, null, default, TimeSpan.FromMilliseconds(400));

        await act.Should().ThrowAsync<Exception>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(4), "касса не ждёт медленный сервер дольше своего срока");
    }
}
