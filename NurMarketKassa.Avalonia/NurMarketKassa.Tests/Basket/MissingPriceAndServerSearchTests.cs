using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-21 (жалоба магазина по видео: «существующие товары не находит — „такого товара нет“, через время находит, но без цены»; «Кирешки Лайт 33г» в чеке по 0,00):
/// 1) товар без цены продажи распознаётся (<see cref="ZeroPriceGuard"/>) и не кладётся в чек молча;
/// 2) поиск по названию, не нашедший товар в каталоге кассы, спрашивает сервер и до его ответа пишет «Ищем…», а не «ничего не найдено»;
/// 3) сервер сообщил «всего M», а пришло меньше — статус «Загружено N из M», а не «Каталог обновлён».</summary>
[Collection("Wave5Database")]
public sealed class MissingPriceAndServerSearchTests
{
    private static CatalogProductTileVm Tile(string priceLine, string? kind = null, double? piecePrice = null)
    {
        var t = new CatalogProductTileVm("p1", "Кирешки Лайт 33г", priceLine, false) { Unit = "шт", Quantity = 5, Kind = kind };
        if (piecePrice is { } pp)
            t.PieceOption = new ProductPackageOption { Id = "pk", Name = "пачка", QuantityInPackage = 10, Unit = "шт", PieceUnitPrice = pp };
        return t;
    }

    [Theory]
    [InlineData("—", true)]
    [InlineData("", true)]
    [InlineData("0.00 сом", true)]
    [InlineData("0,00 сом", true)]
    [InlineData("85.00 сом", false)]
    [InlineData("0.50 сом", false)]
    public void Price_line_without_a_price_is_missing(string priceLine, bool missing) =>
        ZeroPriceGuard.IsPriceMissing(Tile(priceLine)).Should().Be(missing);

    [Fact]
    public void Service_and_piece_priced_products_are_not_blocked()
    {
        ZeroPriceGuard.IsPriceMissing(Tile("0.00 сом", kind: "service")).Should().BeFalse("цену услуги называют при продаже");
        ZeroPriceGuard.IsPriceMissing(Tile("0.00 сом", piecePrice: 12)).Should().BeFalse("поштучная цена из упаковки есть");
        ZeroPriceGuard.IsPriceMissing(null).Should().BeFalse();
    }

    [Fact]
    public void Texts_name_the_product_and_exist_in_all_languages()
    {
        ZeroPriceGuard.CashierText("Кирешки").Should().Contain("Кирешки").And.Contain("цена");
        ZeroPriceGuard.OwnerText("Кирешки").Should().Contain("Кирешки");
        ZeroPriceGuard.SetPriceButton.Should().NotBeNullOrWhiteSpace();
    }

    // ── поиск по названию → сервер ───────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeCache : ICatalogCacheService
    {
        public List<CatalogProductTileVm> Items { get; } = new();

        public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CatalogSyncResult { Success = true });

        public bool TryLoadFromDatabase() => true;

        public IReadOnlyList<CatalogProductTileVm> GetProducts() => Items;
    }

    private sealed class Online : IConnectivityService
    {
        public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
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

    private static async Task<(CatalogPanelViewModel Vm, FakeCache Cache)> LoadedAsync()
    {
        ServerOutageMonitor.ResetForTests();
        var cache = new FakeCache();
        for (var i = 0; i < 3; i++)
            cache.Items.Add(CatalogTestHelper.CreateTile("id" + i, "Товар " + i, "46000" + i.ToString("D7"), 10 + i));
        var vm = new CatalogPanelViewModel(cache, new InlineDispatcher(), new Online());
        vm.RefreshCatalogCommand.Execute(null);
        (await WaitAsync(() => !vm.IsLoading && vm.HasProducts)).Should().BeTrue();
        return (vm, cache);
    }

    [Fact]
    public async Task Empty_local_search_asks_the_server_and_says_searching_until_it_answers()
    {
        var (vm, _) = await LoadedAsync();
        var gate = new TaskCompletionSource<(int, bool)>();
        string? asked = null;
        vm.ServerTextSearch = (q, _) => { asked = q; return gate.Task; };

        vm.SearchText = "кирешки";

        (await WaitAsync(() => asked != null)).Should().BeTrue("локально пусто — спросили сервер");
        asked.Should().Be("кирешки");
        vm.IsNothingFound.Should().BeTrue();
        vm.EmptyStateText.Should().Contain("Ищем", "пока сервер не ответил — «Ищем…», а не «ничего не найдено»");
        vm.EmptyStateText.Should().NotContain("Ничего не найдено");

        gate.SetResult((0, true));
        (await WaitAsync(() => vm.EmptyStateText.Contains("Ничего не найдено"))).Should().BeTrue();
        vm.EmptyStateText.Should().Contain("Проверили и на сервере");
        vm.Dispose();
    }

    [Fact]
    public async Task Server_unreachable_is_not_reported_as_no_such_product()
    {
        var (vm, _) = await LoadedAsync();
        vm.ServerTextSearch = (_, _) => Task.FromResult((0, false));

        vm.SearchText = "кирешки";

        (await WaitAsync(() => vm.EmptyStateText.Contains("Сервер не ответил"))).Should().BeTrue("не дозвонились — честно говорим, что товар мог не загрузиться");
        vm.Dispose();
    }

    [Fact]
    public async Task Product_found_on_the_server_appears_in_the_catalog()
    {
        var (vm, cache) = await LoadedAsync();
        vm.ServerTextSearch = async (q, _) =>
        {
            cache.Items.Add(CatalogTestHelper.CreateTile("new1", "Кирешки Лайт 33г", "4600000009999", 40));
            await vm.RepublishFromLocalAsync();
            return (1, true);
        };

        vm.SearchText = "кирешки";

        (await WaitAsync(() => vm.Products.Any(p => p.Id == "new1"))).Should().BeTrue("найденный на сервере товар показан с ценой");
        vm.Products.First(p => p.Id == "new1").PriceLine.Should().Contain("40");
        vm.Dispose();
    }

    [Fact]
    public async Task Local_hit_does_not_ask_the_server_and_short_query_neither()
    {
        var (vm, _) = await LoadedAsync();
        var calls = 0;
        vm.ServerTextSearch = (_, _) => { calls++; return Task.FromResult((0, true)); };

        vm.SearchText = "Товар 1";
        await Task.Delay(500);
        vm.HasProducts.Should().BeTrue();
        vm.SearchText = "яя"; // меньше трёх символов — сервер не дёргаем
        await Task.Delay(500);

        calls.Should().Be(0);
        vm.Dispose();
    }

    // ── «Загружено N из M» ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Short_full_load_shows_loaded_n_of_m_instead_of_catalog_updated()
    {
        ServerOutageMonitor.ResetForTests();
        var cache = new FakeCache();
        for (var i = 0; i < 3; i++)
            cache.Items.Add(CatalogTestHelper.CreateTile("id" + i, "Товар " + i, "46000" + i.ToString("D7"), 10 + i));
        CatalogApiService.LastFullLoadReport = new CatalogApiService.FullLoadReport(100, 40, false, DateTime.UtcNow.AddMinutes(5));
        try
        {
            var vm = new CatalogPanelViewModel(cache, new InlineDispatcher(), new Online());
            vm.RefreshCatalogCommand.Execute(null);

            (await WaitAsync(() => !vm.IsLoading && vm.StatusText.Contains("из"))).Should().BeTrue();
            vm.StatusText.Should().Contain("40").And.Contain("100");
            vm.IsStatusRoutine.Should().BeFalse("это предупреждение, его должно быть видно");
            vm.Dispose();
        }
        finally
        {
            CatalogApiService.LastFullLoadReport = null;
        }
    }

    [Fact]
    public async Task Complete_or_resumed_load_is_a_normal_catalog_updated()
    {
        ServerOutageMonitor.ResetForTests();
        var cache = new FakeCache();
        cache.Items.Add(CatalogTestHelper.CreateTile("id0", "Товар 0", "4600000000000", 10));
        CatalogApiService.LastFullLoadReport = new CatalogApiService.FullLoadReport(100, 40, true, DateTime.UtcNow.AddMinutes(5)); // докачка — Seen неполный
        try
        {
            var vm = new CatalogPanelViewModel(cache, new InlineDispatcher(), new Online());
            vm.RefreshCatalogCommand.Execute(null);

            (await WaitAsync(() => !vm.IsLoading && vm.IsStatusRoutine)).Should().BeTrue();
            vm.StatusText.Should().Contain("Каталог обновлён");
            vm.Dispose();
        }
        finally
        {
            CatalogApiService.LastFullLoadReport = null;
        }
    }
}
