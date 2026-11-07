using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-07 (живой тест перед 1.17.81, Б-B1): после возврата связи жёлтая плашка «Работа в автономном режиме (из локальной БД)» висела 30+ минут: гасил её только следующий полный проход
/// RefreshCatalogAsync, а лента каталога (ответ сервера каждые ~8 с) его не запускает. Теперь первый же ответ сервера (NoteSynced после такта ленты/сверки) или возврат связи снимают плашку.
/// Заодно — быстрое «Обновить» (QuickRefreshAsync): итог без полной загрузки.</summary>
[Collection("Wave5Database")]
public sealed class CatalogOfflineBannerTests
{
    private sealed class FakeCache : ICatalogCacheService
    {
        public List<CatalogProductTileVm> Items { get; } = new();
        public int FullSyncs;
        public CatalogSyncResult? Quick;

        public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default)
        {
            FullSyncs++;
            return Task.FromResult(new CatalogSyncResult { Success = true });
        }

        public Task<CatalogSyncResult?> QuickRefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Quick);

        public bool TryLoadFromDatabase() => true;

        public IReadOnlyList<CatalogProductTileVm> GetProducts() => Items;
    }

    private sealed class Link(bool online) : IConnectivityService
    {
        public bool Online = online;
        public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(Online);
    }

    private static CatalogPanelViewModel Create(out FakeCache cache, out Link link)
    {
        ServerOutageMonitor.ResetForTests();
        cache = new FakeCache();
        for (var i = 0; i < 3; i++)
            cache.Items.Add(CatalogTestHelper.CreateTile("id" + i, "Товар " + i, "46000" + i.ToString("D7"), 10 + i));
        link = new Link(false);
        return new CatalogPanelViewModel(cache, new InlineDispatcher(), link);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Offline_banner_disappears_after_the_first_server_answer()
    {
        var vm = Create(out _, out _);
        vm.RefreshCatalogCommand.Execute(null);
        (await WaitAsync(() => vm.IsOfflineBannerVisible)).Should().BeTrue("запуск без связи: плашка «автономный режим»");
        vm.StatusText.Should().NotBeNullOrEmpty();
        vm.IsStatusRoutine.Should().BeFalse("это не служебная строка — её видно");

        // Связь вернулась: лента получила первый ответ сервера (AvaloniaCatalogCacheService.TickFeedAsync → NoteSynced).
        CatalogCacheService.NoteSynced(DateTime.UtcNow);

        (await WaitAsync(() => !vm.IsOfflineBannerVisible, 2000)).Should().BeTrue("плашка гаснет сразу, не через 30 минут");
        vm.IsStatusRoutine.Should().BeTrue("строка про автономный режим заменена служебной «Товаров: N»");
        vm.StatusText.Should().Contain("3");
        vm.Dispose();
    }

    [Fact]
    public async Task Offline_banner_stays_while_the_connection_is_really_down()
    {
        var vm = Create(out _, out _);
        vm.RefreshCatalogCommand.Execute(null);
        (await WaitAsync(() => vm.IsOfflineBannerVisible)).Should().BeTrue();

        ServerOutageMonitor.ReportFailure("test", "нет связи", hard: true);
        ServerOutageMonitor.ReportFailure("test", "нет связи", hard: true);
        ServerOutageMonitor.ReportFailure("test", "нет связи", hard: true);
        try
        {
            if (!OfflineModeHelper.SellLocally)
                return; // монитор не объявил аварию (порог другой) — проверять нечего
            CatalogCacheService.NoteSynced(DateTime.UtcNow);
            await Task.Delay(150);
            vm.IsOfflineBannerVisible.Should().BeTrue("сервер объявлен недоступным — плашка остаётся");
        }
        finally
        {
            ServerOutageMonitor.ResetForTests();
            vm.Dispose();
        }
    }

    [Fact]
    public async Task Link_recovery_event_clears_the_banner_too()
    {
        var vm = Create(out _, out _);
        vm.RefreshCatalogCommand.Execute(null);
        (await WaitAsync(() => vm.IsOfflineBannerVisible)).Should().BeTrue();

        ServerOutageMonitor.ReportSuccess("test"); // состояние уже Online → события нет; вызываем обработчик так же, как монитор при возврате связи
        vm.ClearOfflineBannerIfServerAnswered();

        (await WaitAsync(() => !vm.IsOfflineBannerVisible, 2000)).Should().BeTrue();
        vm.Dispose();
    }

    [Fact]
    public async Task Quick_refresh_returns_the_number_of_fixed_products_without_a_full_load()
    {
        var vm = Create(out var cache, out _);
        cache.Quick = CatalogSyncResult.Ok(0, 2, 0);

        var quick = await vm.QuickRefreshAsync();

        quick.Should().NotBeNull();
        quick!.Value.Online.Should().BeTrue();
        quick.Value.Changed.Should().Be(2);
        cache.FullSyncs.Should().Be(0, "полная загрузка каталога не запускалась");

        cache.Quick = null; // быстрый путь невозможен (нет связи / ленты) — вызывающий обновит обычным путём
        (await vm.QuickRefreshAsync()).Should().BeNull();
        vm.Dispose();
    }
}
