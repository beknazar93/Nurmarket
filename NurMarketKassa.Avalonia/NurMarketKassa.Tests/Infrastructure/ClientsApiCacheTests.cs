using FluentAssertions;
using NurMarketKassa.Services.Api;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-06: В9, шаг 4 (Н-2) — общий кэш полного списка клиентов в <see cref="ClientsApiService"/> против поддельного NurCRM (WireMock):
/// один запрос вместо десятка, слияние одинаковых запросов «в полёте», сброс при записи, «свежий» запрос с индикатором и поиск мимо кэша.
/// Общая статика (счётчик сбросов кэша) — коллекция «ServerFeatures» (тесты идут по очереди).</summary>
[Collection("ServerFeatures")]
public sealed class ClientsApiCacheTests : IDisposable
{
    private const string TwoClients =
        "{\"count\":2,\"next\":null,\"previous\":null,\"results\":[" +
        "{\"id\":\"c1\",\"full_name\":\"Асан\",\"phone\":\"+996555000111\",\"type\":\"client\"}," +
        "{\"id\":\"c2\",\"full_name\":\"Айгуль\",\"phone\":\"+996555000222\",\"type\":\"client\"}]}";

    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private async Task<ClientsApiService> ServiceAsync(TimeSpan? delay = null)
    {
        _fake.Json("GET", "/api/main/clients/", 200, TwoClients, delay);
        return new ClientsApiService(await LoggedInAsync(_fake));
    }

    private int Gets() => Requests(_fake, "GET", "/api/main/clients/").Count;

    [Fact]
    public async Task FullList_SecondCallWithin60s_DoesNotHitServer_AndReturnsOwnCopy()
    {
        var service = await ServiceAsync();
        ClientsApiService.InvalidateCache();

        var first = await service.GetClientsAsync(null);
        var second = await service.GetClientsAsync(null);

        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
        Gets().Should().Be(1, "второй вызов взят из общего кэша");
        first.Clear();
        (await service.GetClientsAsync(null)).Should().HaveCount(2, "вызывающий получает свою копию — очистка списка не портит кэш");
    }

    [Fact]
    public async Task FullList_ParallelCalls_ShareOneRequest()
    {
        var service = await ServiceAsync(TimeSpan.FromMilliseconds(400));
        ClientsApiService.InvalidateCache();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetClientsAsync(null)));

        results.Should().OnlyContain(r => r.Count == 2);
        Gets().Should().Be(1, "одинаковые запросы «в полёте» слиты");
    }

    [Fact]
    public async Task FullList_CancelledWaiter_DoesNotCancelSharedLoad()
    {
        var service = await ServiceAsync(TimeSpan.FromMilliseconds(400));
        ClientsApiService.InvalidateCache();
        using var cts = new CancellationTokenSource();

        var first = service.GetClientsAsync(null, cts.Token);
        var second = service.GetClientsAsync(null);
        cts.Cancel();

        await first.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        (await second).Should().HaveCount(2);
        Gets().Should().Be(1);
    }

    [Fact]
    public async Task Search_IsNotCached()
    {
        var service = await ServiceAsync();
        ClientsApiService.InvalidateCache();

        await service.GetClientsAsync("996555");
        await service.GetClientsAsync("996555");

        Gets().Should().Be(2, "поиск идёт на сервер каждый раз");
    }

    [Fact]
    public async Task ProgressCall_IsAlwaysFresh_AndRefillsCache()
    {
        var service = await ServiceAsync();
        ClientsApiService.InvalidateCache();

        await service.GetClientsAsync(null);                                   // 1 запрос, кэш
        await service.GetClientsAsync(null, new Progress<int>(), default);     // окно «Клиенты» / «Обновить»: свежий запрос
        await service.GetClientsAsync(null);                                   // снова из кэша

        Gets().Should().Be(2);
    }

    [Fact]
    public async Task Write_InvalidatesCache()
    {
        var service = await ServiceAsync();
        _fake.Json("POST", "/api/main/clients/", 201, "{\"id\":\"c3\",\"full_name\":\"Новый\",\"phone\":\"+996555000333\"}");
        ClientsApiService.InvalidateCache();

        await service.GetClientsAsync(null);
        await service.CreateClientAsync("Новый", "+996555000333", null);
        await service.GetClientsAsync(null);

        Gets().Should().Be(2, "после создания клиента список перечитан");
    }

    [Fact]
    public async Task InvalidateCache_ForcesReload()
    {
        var service = await ServiceAsync();
        ClientsApiService.InvalidateCache();

        await service.GetClientsAsync(null);
        ClientsApiService.InvalidateCache();
        await service.GetClientsAsync(null);

        Gets().Should().Be(2);
    }

    [Fact]
    public async Task Failure_IsNotCached()
    {
        _fake.Error("GET", "/api/main/clients/", 500);
        var service = new ClientsApiService(await LoggedInAsync(_fake));
        ClientsApiService.InvalidateCache();

        await service.Invoking(s => s.GetClientsAsync(null)).Should().ThrowAsync<Exception>();

        _fake.Json("GET", "/api/main/clients/", 200, TwoClients);
        (await service.GetClientsAsync(null)).Should().HaveCount(2, "ошибка в кэш не попала — следующий вызов спрашивает сервер снова");
    }
}
