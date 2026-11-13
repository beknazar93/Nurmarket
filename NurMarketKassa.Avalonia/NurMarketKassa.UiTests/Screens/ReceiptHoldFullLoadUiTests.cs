using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-12 (аудит Fable, Т-12, пакет 1 «Остаток после прихода»): полная загрузка каталога — настоящим <c>AvaloniaCatalogCacheService</c> (через DI-хост), а не одним
/// <c>TouchedSince</c>. Приход товара стоит в очереди (защита «приход в пути»), а сервер уже отдаёт остаток с этим приходом: полная загрузка не должна класть его на плитку и в базу —
/// иначе очередь прибавила бы приход второй раз (Д-4: 20 вместо 10). Защита снята — серверное число применяется. Сервер — поддельный NurCRM (WireMock).</summary>
[Collection("UiScenarios")]
public sealed class ReceiptHoldFullLoadUiTests
{
    private const string Id = "rh-full-1";

    private static void StubCatalog(double quantity) =>
        UiFake.Server.Json("GET", "/api/main/products/list/", 200,
            "{\"count\":1,\"next\":null,\"previous\":null,\"results\":[{\"id\":\"" + Id + "\",\"name\":\"Проверка прихода\",\"barcode\":\"4600000009991\",\"price\":\"10.00\",\"quantity\":\""
            + quantity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "\",\"is_weight\":false}]}");

    // 2026-11-13: настоящий AvaloniaCatalogCacheService из DI-хоста (единственный ICatalogCacheService, как в программе): его конструктор ставит CatalogCacheService.SharedRefresh и т.п.
    // Раньше тест звал статический CatalogCacheService.SyncCatalogFullAsync: без хостовой службы (её никто не запрашивал из DI) он идёт запасным путём без защиты остатков
    // (там нет PrepareTilesForStoreAsync) — остаток 17 вместо 7 был ошибкой теста, а не кассы. Статические «ручки» после теста возвращаем, чтобы остальные сценарии шли как раньше.
    private static async Task<CatalogSyncResult> FullLoadAsync()
    {
        var service = TestHost.Services.GetRequiredService<ICatalogCacheService>();
        // «Свежая загрузка 15 с» пропустила бы вторую и третью загрузки подряд — забываем итог прежней.
        typeof(NurMarketKassa.AvaloniaHost.Services.AvaloniaCatalogCacheService).GetField("_lastSuccessfulResult", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, null);
        var result = await service.SyncCatalogFullAsync(CancellationToken.None);
        await UiDispatcherHolder.InvokeAsync(() => { }); // запись общего списка каталога идёт через UI-поток — дожидаемся её
        return result;
    }

    private static void ResetServer(double quantity)
    {
        var s = UiFake.Server;
        s.Server.Reset();
        s.StubLogin();
        s.StubProfile();
        s.StubCheckout();
        s.StubReturn();
        s.StubShiftOpen();
        StubCatalog(quantity);
    }

    [Fact]
    public async Task Full_catalog_load_does_not_put_the_server_stock_with_the_receipt_on_a_product_whose_receipt_is_still_queued()
    {
        TestHost.Ensure();
        await TestHost.WarmAsync();
        var owner = new object();
        var prevFull = CatalogCacheService.SharedRefresh;
        var prevPoint = CatalogCacheService.SharedRefreshProducts;
        var prevBackground = CatalogCacheService.RequestBackgroundFullReload;
        try
        {
            // 1. Каталог загружен: на сервере и на плитке 7 шт.
            ResetServer(7);
            (await FullLoadAsync()).Success.Should().BeTrue();
            CatalogCacheService.FindProductById(Id)!.Quantity.Should().Be(7, "исходное состояние: загрузка без защиты кладёт серверный остаток");
            LocalProductRepository.Instance.LoadTileCopy(Id)!.Quantity.Should().Be(7);

            // 2. Приход +10 принят сервером (17), а очередь ещё не прибавила его к плитке: товар под защитой «приход в пути».
            ResetServer(17);
            StockSyncService.SetReceiptHolds(owner, new[] { Id });
            (await FullLoadAsync()).Success.Should().BeTrue();

            CatalogCacheService.FindProductById(Id)!.Quantity.Should().Be(7, "под защитой полная загрузка не кладёт 17 на плитку: очередь прибавит 10 сама, иначе вышло бы 27");
            LocalProductRepository.Instance.LoadTileCopy(Id)!.Quantity.Should().Be(7, "и в базу");

            // 3. Очередь прибавила приход и сняла защиту: серверный остаток применяется как обычно.
            StockSyncService.SetReceiptHolds(owner, Array.Empty<string>());
            (await FullLoadAsync()).Success.Should().BeTrue();

            CatalogCacheService.FindProductById(Id)!.Quantity.Should().Be(17, "защита снята — полная загрузка снова применяет серверный остаток");
            LocalProductRepository.Instance.LoadTileCopy(Id)!.Quantity.Should().Be(17);
        }
        finally
        {
            StockSyncService.SetReceiptHolds(owner, Array.Empty<string>());
            // Возвращаем общий исходный каталог (p1, p2) для остальных сценариев.
            UiFake.Reset();
            await FullLoadAsync();
            CatalogCacheService.SharedRefresh = prevFull;
            CatalogCacheService.SharedRefreshProducts = prevPoint;
            CatalogCacheService.RequestBackgroundFullReload = prevBackground;
        }
    }

    /// <summary>2026-11-13: запасной путь полной загрузки (статический <c>CatalogCacheService.SyncCatalogFullAsync</c>, когда хостовой службы каталога нет — SharedRefresh пуст) тоже держит остаток
    /// товара с приходом в пути: общий <c>StockSyncService.KeepLocalStockForTouched</c>. Раньше этот путь клал серверное 17 поверх 7.</summary>
    [Fact]
    public async Task Fallback_full_load_without_host_service_also_keeps_the_stock_of_a_product_whose_receipt_is_still_queued()
    {
        TestHost.Ensure();
        await TestHost.WarmAsync();
        var owner = new object();
        var prevFull = CatalogCacheService.SharedRefresh;
        var prevPoint = CatalogCacheService.SharedRefreshProducts;
        var prevBackground = CatalogCacheService.RequestBackgroundFullReload;
        try
        {
            CatalogCacheService.SharedRefresh = null; // как без AvaloniaCatalogCacheService: идёт запасной путь
            ResetServer(7);
            (await CatalogCacheService.SyncCatalogFullAsync()).Success.Should().BeTrue();
            CatalogCacheService.FindProductById(Id)!.Quantity.Should().Be(7);

            ResetServer(17);
            StockSyncService.SetReceiptHolds(owner, new[] { Id });
            (await CatalogCacheService.SyncCatalogFullAsync()).Success.Should().BeTrue();
            CatalogCacheService.FindProductById(Id)!.Quantity.Should().Be(7, "приход в пути: запасной путь не кладёт серверные 17 на плитку");
            LocalProductRepository.Instance.LoadTileCopy(Id)!.Quantity.Should().Be(7, "и в базу");

            StockSyncService.SetReceiptHolds(owner, Array.Empty<string>());
            (await CatalogCacheService.SyncCatalogFullAsync()).Success.Should().BeTrue();
            CatalogCacheService.FindProductById(Id)!.Quantity.Should().Be(17, "защита снята — серверный остаток применяется");
        }
        finally
        {
            StockSyncService.SetReceiptHolds(owner, Array.Empty<string>());
            CatalogCacheService.SharedRefresh = prevFull;
            CatalogCacheService.SharedRefreshProducts = prevPoint;
            CatalogCacheService.RequestBackgroundFullReload = prevBackground;
            UiFake.Reset();
            await FullLoadAsync();
        }
    }
}
