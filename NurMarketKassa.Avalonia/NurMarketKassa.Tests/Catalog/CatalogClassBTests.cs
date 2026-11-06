using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-11-06, отчёт «Аналогичные баги — прыгающие кнопки и пустые поля», класс Б («поле есть на сервере, а в кассе пустое или потерянное»):
/// Б-1 новый товар из ленты дочитывается полной карточкой (весовой не продаётся штучным), Б-2 скан неизвестного штрихкода не затирает полную запись
/// и находит товар по доп. штрихкоду, Б-3 PATCH шлёт только изменённые поля, Б-4 локальное сохранение не теряет единицу, вид, код и фото,
/// Б-7 самолечение товаров без кодов на уровне хранилища, Б-9 поиск по PLU и доп. штрихкодам, Б-10 повтор после сбоя дочитывания.
/// Настоящая база (NURMARKET_DATA_ROOT из test-safe.ps1) и поддельный сервер WireMock. Запускать только tools/qa/test-safe.ps1.
/// </summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class CatalogClassBTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private readonly ICatalogApiService? _previousApi;

    public CatalogClassBTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
        _previousApi = PosApp.CatalogApi;
        ServerOutageMonitor.ResetForTests(); // ответы 500 поддельного сервера объявляют «аварию» — без сброса соседние тесты видят «нет связи»
        NewProductCardBackfill.AutoRun = false; // тесты вызывают DrainAsync сами, без фоновой задачи
        NewProductCardBackfill.ResetForTests();
        ProductCodeBackfill.ResetForTests();
    }

    public void Dispose()
    {
        PosApp.CatalogApi = _previousApi!;
        NewProductCardBackfill.AutoRun = true;
        ServerOutageMonitor.ResetForTests();
        NewProductCardBackfill.ResetForTests();
        _repo.ClearAll();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogProductTileVm Tile(string json) => ProductCatalogMapper.TryTile(J(json), ApiBase)!;

    private static CatalogFeedPage FeedPage(params string[] cards) => new()
    {
        Status = CatalogFeedStatus.Ok,
        Items = cards.Select(J).ToList(),
        NextCursor = "cursor-1",
    };

    private static async Task<CatalogApiService> LoginAsync(FakeNurCrmServer server)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    // Сокращённая карточка ленты (9 полей, как отдаёт products/changes) и полная карточка того же товара.
    private const string ShortNewWeighed = """
        {"id":"w-1","name":"Яблоки новые","price":"120.00","purchase_price":"80.00","quantity":"5.5","discount_percent":"0","markup_percent":"50",
         "wholesale_price":"110.00","updated_at":"2026-11-06T02:00:00Z"}
        """;

    private const string FullNewWeighed = """
        {"id":"w-1","name":"Яблоки новые","price":"120.00","purchase_price":"80.00","quantity":"9","unit":"кг","is_weight":true,"plu":12,"kind":"product",
         "category":"Фрукты","brand":"Сад","barcode":"","article":"","code":"","alternate_barcodes":[],"updated_at":"2026-11-06T02:00:00Z"}
        """;

    // ---------------------------------------------------------------- Б-1: новый товар из ленты

    [Fact]
    public async Task B1_NewWeighedProduct_FromShortFeed_IsReadFully_AndNotSoldAsPiece()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/w-1/", 200, FullNewWeighed);
        var catalog = await LoginAsync(server);

        // ДО: лента принесла 9 полей — товар записан штучным, без вида, категории и PLU (как было до исправления до полной загрузки).
        CatalogFeedSync.Apply(FeedPage(ShortNewWeighed), ApiBase, allowStock: true).Added.Should().Be(1);
        var before = _repo.LoadTileCopy("w-1")!;
        before.MustWeigh.Should().BeFalse("сокращённая лента не несёт признак «весовой»");
        before.Unit.Should().Be("шт");
        before.Category.Should().BeNullOrEmpty();
        NewProductCardBackfill.Pending.Should().Be(1, "новый товар в сокращённой карточке поставлен в очередь дочитывания");

        // ПОСЛЕ дочитывания: полная карточка записана, остаток остался из ленты.
        (await NewProductCardBackfill.DrainAsync(catalog, ApiBase, CancellationToken.None, respectRate: false)).Should().BeTrue();
        var after = _repo.LoadTileCopy("w-1")!;
        after.MustWeigh.Should().BeTrue("весовой товар с сайта теперь продаётся с окном взвешивания");
        after.Unit.Should().Be("кг");
        after.Plu.Should().Be(12);
        after.Category.Should().Be("Фрукты");
        after.Brand.Should().Be("Сад");
        after.Quantity.Should().Be(5.5, "остаток вела лента и продажи этого ПК — карточка его не переписывает");
        NewProductCardBackfill.Pending.Should().Be(0);
    }

    [Fact]
    public async Task B1_ServiceKind_IsRestored_FromFullCard()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/s-1/", 200,
            """{"id":"s-1","name":"Стрижка","price":"500.00","quantity":"0","unit":"шт","kind":"service","updated_at":"2026-11-06T02:00:00Z"}""");
        var catalog = await LoginAsync(server);

        CatalogFeedSync.Apply(FeedPage("""{"id":"s-1","name":"Стрижка","price":"500.00","quantity":"0","updated_at":"2026-11-06T02:00:00Z"}"""), ApiBase, allowStock: true);
        _repo.LoadTileCopy("s-1")!.IsService.Should().BeFalse("до дочитывания услуга выглядит товаром");

        await NewProductCardBackfill.DrainAsync(catalog, ApiBase, CancellationToken.None, respectRate: false);
        _repo.LoadTileCopy("s-1")!.IsService.Should().BeTrue();
    }

    [Fact]
    public async Task B1_CardNotRead_StaysInQueue_ThenGivesUpAfterThreeAttempts_WithoutDamagingRecord()
    {
        using var server = new FakeNurCrmServer();
        server.Error("GET", "/api/main/products/w-1/", 500);
        server.Error("GET", "/api/main/products/list/w-1/", 500);
        var catalog = await LoginAsync(server);
        CatalogFeedSync.Apply(FeedPage(ShortNewWeighed), ApiBase, allowStock: true);

        (await NewProductCardBackfill.DrainAsync(catalog, ApiBase, CancellationToken.None, respectRate: false)).Should().BeFalse("сервер не ответил — нужна пауза");
        NewProductCardBackfill.Pending.Should().Be(1, "карточку попробуем позже");
        _repo.LoadTileCopy("w-1")!.PriceLine.Should().Contain("120.00", "запись не повреждена");

        await NewProductCardBackfill.DrainAsync(catalog, ApiBase, CancellationToken.None, respectRate: false);
        await NewProductCardBackfill.DrainAsync(catalog, ApiBase, CancellationToken.None, respectRate: false);
        NewProductCardBackfill.Pending.Should().Be(0, "после трёх попыток отстаём — остальное подтянет полная загрузка");
    }

    [Fact]
    public void B1_FeedFullCard_AndExistingProducts_AreNotQueued()
    {
        // Карточка с единицей — не сокращённая; существующий товар не дочитывается (его защищает KeepLocalForAbsentFields).
        CatalogFeedSync.Apply(FeedPage(FullNewWeighed), ApiBase, allowStock: true);
        NewProductCardBackfill.Pending.Should().Be(0);

        CatalogFeedSync.Apply(FeedPage("""{"id":"w-1","name":"Яблоки новые","price":"125.00","quantity":"8","updated_at":"2026-11-06T03:00:00Z"}"""), ApiBase, allowStock: true);
        NewProductCardBackfill.Pending.Should().Be(0);
        var kept = _repo.LoadTileCopy("w-1")!;
        kept.MustWeigh.Should().BeTrue("лента без единицы весовой признак не обнуляет");
        kept.Plu.Should().Be(12);
        kept.Category.Should().Be("Фрукты");
    }

    // ---------------------------------------------------------------- Б-2: скан неизвестного штрихкода

    private const string FullLocal = """
        {"id":"l-1","name":"Яблоки","price":"100.00","purchase_price":"60.00","quantity":"5","unit":"кг","is_weight":true,"plu":12,"barcode":"OLD111",
         "article":"ART-1","kind":"product","category":"Фрукты","brand":"Сад","wholesale_price":"90.00","updated_at":"2026-11-06T01:00:00Z"}
        """;

    [Fact]
    public void B2_Before_UpsertOfSearchTile_WipesFullRecord_Characterization()
    {
        // Воспроизведение прежнего пути: плитка из ответа поиска (ProductDto) записывается поверх полной записи.
        _repo.SyncReplaceAllWithDiff([Tile(FullLocal)]);
        var thin = ProductCatalogMapper.TryTile(new ProductDto { Id = "l-1", Title = "Яблоки", Price = 130m, Barcode = "NEW111" }, ApiBase)!;
        _repo.UpsertFromTiles([thin]);

        var wiped = _repo.LoadTileCopy("l-1")!;
        wiped.Plu.Should().BeNull("так стирались PLU, артикул и закупка");
        wiped.Article.Should().BeNullOrEmpty();
        wiped.PurchasePrice.Should().Be(0);
    }

    [Fact]
    public async Task B2_ScanFoundOnServer_DetailUnavailable_MergesOnlyEmptyFields_KeepsLocalData()
    {
        using var server = new FakeNurCrmServer();
        server.Error("GET", "/api/main/products/l-1/", 500);
        server.Error("GET", "/api/main/products/list/l-1/", 500);
        var catalog = await LoginAsync(server);
        _repo.SyncReplaceAllWithDiff([Tile(FullLocal)]);

        var results = new List<ProductDto> { new() { Id = "l-1", Title = "Яблоки", Price = 130m, Barcode = "NEW111" } };
        var resolved = await ProductCodeLookup.ResolveServerResultsAsync(catalog, results, ["NEW111"], ApiBase);

        resolved.Tile.Should().NotBeNull();
        var stored = _repo.LoadTileCopy("l-1")!;
        stored.Barcode.Should().Be("NEW111", "штрихкод и цена — от сервера");
        stored.PriceLine.Should().Contain("130.00");
        stored.Plu.Should().Be(12, "PLU не затёрт");
        stored.Article.Should().Be("ART-1");
        stored.PurchasePrice.Should().Be(60);
        stored.WholesalePrice.Should().Be(90);
        stored.MustWeigh.Should().BeTrue("в ответе поиска единицы не было — весовой остаётся весовым");
        stored.Unit.Should().Be("кг");
        stored.Category.Should().Be("Фрукты");
        stored.Quantity.Should().Be(5, "остаток существующего товара остаётся локальным");
        _repo.TryGetTileByBarcode("NEW111")!.Id.Should().Be("l-1");
    }

    [Fact]
    public async Task B2_ScanFoundOnServer_FullCardRead_ReplacesRecordWithServerTruth()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/l-1/", 200,
            """{"id":"l-1","name":"Яблоки","price":"130.00","purchase_price":"70.00","quantity":"4","unit":"кг","is_weight":true,"plu":15,"barcode":"NEW111","article":"ART-2","kind":"product","category":"Фрукты","brand":"Сад"}""");
        var catalog = await LoginAsync(server);
        _repo.SyncReplaceAllWithDiff([Tile(FullLocal)]);

        var results = new List<ProductDto> { new() { Id = "l-1", Title = "Яблоки", Price = 130m, Barcode = "NEW111" } };
        await ProductCodeLookup.ResolveServerResultsAsync(catalog, results, ["NEW111"], ApiBase);

        var stored = _repo.LoadTileCopy("l-1")!;
        stored.Plu.Should().Be(15, "в карточке сервера PLU сменили — берётся серверный");
        stored.Article.Should().Be("ART-2");
        stored.PurchasePrice.Should().Be(70);
        stored.Quantity.Should().Be(5, "остаток локальный");
    }

    [Fact]
    public async Task B2_ScanOfAlternateBarcode_NotMainBarcode_IsFoundOnServer_NotOfferedAsNew()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/a-1/", 200,
            """{"id":"a-1","name":"Вода","price":"30.00","quantity":"10","unit":"шт","barcode":"MAIN-1","alternate_barcodes":[{"barcode":"ALT-77","name":"большая","quantity":6}],"kind":"product"}""");
        var catalog = await LoginAsync(server);

        // Поиск вернул товар, но основной штрихкод у него другой (ProductDto доп. штрихкодов не несёт).
        var results = new List<ProductDto> { new() { Id = "a-1", Title = "Вода", Price = 30m, Barcode = "MAIN-1" } };
        var resolved = await ProductCodeLookup.ResolveServerResultsAsync(catalog, results, ["ALT-77"], ApiBase);

        resolved.Tile.Should().NotBeNull("доп. штрихкод найден по полной карточке — дубль создавать не нужно");
        _repo.TryGetTileByBarcode("ALT-77")!.Id.Should().Be("a-1", "дальше находится уже без сети");

        // Чужой код: карточка его не содержит — не найдено.
        (await ProductCodeLookup.ResolveServerResultsAsync(catalog, results, ["OTHER-9"], ApiBase)).Tile.Should().BeNull();
    }

    // ---------------------------------------------------------------- Б-3: PATCH только с изменёнными полями

    [Fact]
    public async Task B3_Patch_OmitsUnchangedKeys_ButSendsChangedPrice()
    {
        using var server = new FakeNurCrmServer();
        server.Json("PATCH", "/api/main/products/p-9/", 200, """{"id":"p-9"}""");
        var catalog = await LoginAsync(server);

        // ДО: прежнее поведение — ключи идентичности уходят всегда.
        await catalog.UpdateProductAsync("p-9", new ProductEditRequest { Name = "Яблоки", Unit = "шт", IsWeight = false, Price = 150 });
        var oldBody = LastPatchBody(server);
        oldBody.Should().Contain("\"unit\"").And.Contain("\"is_weight\"").And.Contain("\"category_name\"");

        // ПОСЛЕ: кассир поменял только цену — остальное не отправляется.
        server.Server.ResetLogEntries();
        var request = new ProductEditRequest
        {
            Name = "Яблоки", Unit = "шт", IsWeight = false, Price = 150, SendQuantity = false,
            UnchangedKeys = ["name", "article", "barcode", "category_name", "brand_name", "hotkey_group", "kind", "unit", "is_weight", "plu"],
        };
        await catalog.UpdateProductAsync("p-9", request);
        var body = LastPatchBody(server);
        body.Should().Contain("\"price\"");
        foreach (var key in new[] { "name", "article", "barcode", "category_name", "brand_name", "hotkey_group", "kind", "unit", "is_weight", "plu" })
            body.Should().NotContain($"\"{key}\"", $"поле {key} кассир не менял");
    }

    private static string LastPatchBody(FakeNurCrmServer server) =>
        server.Server.LogEntries.Last(e => e.RequestMessage.Method == "PATCH").RequestMessage.Body ?? "";

    // ---------------------------------------------------------------- Б-4: локальное сохранение

    [Fact]
    public void B4_SaveLocally_KeepsOwnUnit_Kind_Photo_AndProductCode_OnEdit()
    {
        var id = LocalProductEditor.SaveLocally(new ProductEditRequest { Name = "Лента", Unit = "м", Quantity = 20, Price = 15, IsNew = true }, existingId: null);
        _repo.LoadTileCopy(id)!.Unit.Should().Be("м", "своя единица не превращается в «шт»");

        // Товар получил фото и код товара (из ленты/сервера); офлайн-правка цены их не стирает.
        var withExtras = _repo.LoadTileCopy(id)!;
        withExtras.ImageUrl = "https://app.nurcrm.kg/media/rope.jpg";
        withExtras.ProductCode = "C-77";
        _repo.UpsertFromTiles([withExtras]);

        LocalProductEditor.SaveLocally(new ProductEditRequest { Name = "Лента", Unit = "м", Quantity = 20, Price = 18 }, id);
        var edited = _repo.LoadTileCopy(id)!;
        edited.ImageUrl.Should().Be("https://app.nurcrm.kg/media/rope.jpg", "фото не стёрто");
        edited.ProductCode.Should().Be("C-77");
        edited.Unit.Should().Be("м");
        edited.PriceLine.Should().Contain("18.00");
    }

    [Fact]
    public void B4_SaveLocally_KeepsServiceKind()
    {
        var id = LocalProductEditor.SaveLocally(new ProductEditRequest { Name = "Стрижка", Unit = "шт", Price = 500, Kind = "service", IsNew = true }, existingId: null);
        _repo.LoadTileCopy(id)!.IsService.Should().BeTrue("услуга остаётся услугой, а не товаром с остатком");
    }

    [Fact]
    public void B4_SaveLocally_MirrorAfterCreate_TakesProductCodeAndPluFromServerAnswer()
    {
        var created = J("""{"id":"srv-1","name":"Сыр","price":"400.00","unit":"кг","is_weight":true,"plu":31,"code":"C-31","kind":"product"}""");
        var id = LocalProductEditor.SaveLocally(new ProductEditRequest { Name = "Сыр", Unit = "кг", IsWeight = true, Price = 400, IsNew = true }, "srv-1", created);

        var mirror = _repo.LoadTileCopy(id)!;
        mirror.ProductCode.Should().Be("C-31", "код, который присвоил сервер");
        mirror.Plu.Should().Be(31, "PLU, присвоенный сервером, не теряется в локальной копии");
        mirror.Unit.Should().Be("кг");
    }

    // ---------------------------------------------------------------- Б-7 / Б-10: самолечение хранилища

    [Fact]
    public async Task B7_ScheduleFromStorage_FillsCodes_ForRowsWithoutAnyCode()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/c-1/", 200,
            """{"id":"c-1","name":"Ансар Бантик (белый)","price":"900.00","quantity":"6","unit":"шт","barcode":"5538198285770","kind":"simple"}""");
        PosApp.CatalogApi = await LoginAsync(server);
        _repo.SyncReplaceAllWithDiff([Tile("""{"id":"c-1","name":"Ансар Бантик (белый)","price":"900.00","quantity":"6","updated_at":"2026-11-06T02:00:00Z"}""")]);
        ProductCodeBackfill.NeedsCodes(_repo.LoadTileCopy("c-1")!).Should().BeTrue();

        ProductCodeBackfill.ScheduleFromStorage();
        await WaitAsync(() => !ProductCodeBackfill.IsRunning && _repo.LoadTileCopy("c-1")!.Barcode == "5538198285770");

        _repo.LoadTileCopy("c-1")!.Barcode.Should().Be("5538198285770", "ревизия, накладные и печать читают штрихкод из базы — он дописан без открытия страницы Склада");
    }

    [Fact]
    public async Task B10_CodesBackfill_FailedRead_IsRetriedLater_NotMarkedTriedForever()
    {
        using var server = new FakeNurCrmServer();
        server.Error("GET", "/api/main/products/c-2/", 500);
        server.Error("GET", "/api/main/products/list/c-2/", 500);
        PosApp.CatalogApi = await LoginAsync(server);
        var row = Tile("""{"id":"c-2","name":"Товар без кодов","price":"10.00","quantity":"1","updated_at":"2026-11-06T02:00:00Z"}""");
        _repo.SyncReplaceAllWithDiff([row]);

        ProductCodeBackfill.ScheduleForRows([_repo.LoadTileCopy("c-2")!]).Should().Be(1);
        await WaitAsync(() => !ProductCodeBackfill.IsRunning);

        ProductCodeBackfill.IsMarkedTried("c-2").Should().BeFalse("карточку не прочитали — отметка «пробовали» снята, повтор при следующем показе");
    }

    // ---------------------------------------------------------------- Б-9: поиск по PLU и доп. штрихкодам

    [Fact]
    public void B9_SearchKey_ContainsPlu_AndAlternateBarcodes_AndFollowsChanges()
    {
        var tile = new CatalogProductTileVm("p-1", "Яблоки", "1 сом", true, null) { Plu = 1234 };
        var query = SearchMatch.Prepare("1234");
        query.Matches(tile.SearchKey).Should().BeTrue("PLU весового товара ищется в общем поиске");

        tile.AlternateBarcodesRaw = "4600000000017\n4600000000024";
        SearchMatch.Prepare("4600000000024").Matches(tile.SearchKey).Should().BeTrue("доп. штрихкод ищется и целиком, и частью");
        SearchMatch.Prepare("46000000000").Matches(tile.SearchKey).Should().BeTrue();

        tile.Plu = 777;
        SearchMatch.Prepare("1234").Matches(tile.SearchKey).Should().BeFalse("ключ пересчитан после смены PLU");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
    }
}
