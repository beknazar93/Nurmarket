using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-11-11, жалоба владельца: «штрихкоды неправильно показывает в кассе — при добавлении в программе владельца одно, в кассе другое».
/// Разбор — docs/Отчеты/«Штрихкод: касса и программа владельца расходятся — разбор.md». Здесь:
/// (1) правка ТОЛЬКО штрихкода у существующего товара приходит в ленте products/changes сокращённой карточкой без штрихкода — касса держала старый штрихкод
///     до сверки недавних (до 2,5 мин) или полной загрузки (сутки); теперь такой товар распознаётся и его карточка читается по id;
/// (2) повтор того же изменения из запаса курсора, своя продажа и обычная смена остатка карточку не читают (без лишней нагрузки на сервер);
/// (3) поведение маппера и дочитывания, на которое опирается разбор (код товара как штрихкод при пустом barcode; дочитывание не подменяет непустой штрихкод; 12 ↔ 13 цифр).
/// Настоящая база (NURMARKET_DATA_ROOT из test-safe.ps1) и поддельный сервер WireMock. Запускать только tools/qa/test-safe.ps1.
/// </summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class BarcodeOwnerKassaTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;

    public BarcodeOwnerKassaTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
        ServerOutageMonitor.ResetForTests();
        NewProductCardBackfill.AutoRun = false;
        NewProductCardBackfill.ResetForTests();
        ProductCodeBackfill.ResetForTests();
        CatalogFeedSync.ResetHiddenSeenForTests();
    }

    public void Dispose()
    {
        NewProductCardBackfill.AutoRun = true;
        NewProductCardBackfill.ResetForTests();
        CatalogFeedSync.ResetHiddenSeenForTests();
        ServerOutageMonitor.ResetForTests();
        _repo.ClearAll();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogFeedPage Page(params string[] cards) => new() { Status = CatalogFeedStatus.Ok, Items = cards.Select(J).ToList(), NextCursor = "c-1" };

    private static async Task<CatalogApiService> LoginAsync(FakeNurCrmServer server)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    // Полная карточка (как products/list и products/{id}) и сокращённая карточка ленты (9 полей) того же товара.
    private static string Full(string barcode, string name = "Сок яблочный 1 л") =>
        "{\"id\":\"b-1\",\"name\":\"" + name + "\",\"price\":\"120.00\",\"purchase_price\":\"80.00\",\"quantity\":\"10\",\"unit\":\"шт\",\"is_weight\":false,\"kind\":\"product\","
        + "\"barcode\":\"" + barcode + "\",\"article\":\"15521\",\"code\":\"15521\",\"alternate_barcodes\":[],\"discount_percent\":\"0\",\"markup_percent\":\"50\",\"wholesale_price\":\"0\","
        + "\"updated_at\":\"2026-11-11T05:00:00Z\"}";

    private static string Short(string updatedAt, string quantity = "10", string name = "Сок яблочный 1 л", string price = "120.00") =>
        "{\"id\":\"b-1\",\"name\":\"" + name + "\",\"price\":\"" + price + "\",\"purchase_price\":\"80.00\",\"quantity\":\"" + quantity + "\",\"discount_percent\":\"0\","
        + "\"markup_percent\":\"50\",\"wholesale_price\":\"0\",\"updated_at\":\"" + updatedAt + "\"}";

    private void SeedLocal(string barcode) =>
        CatalogFeedSync.Apply(Page(Full(barcode)), ApiBase, allowStock: true).Added.Should().Be(1);

    [Fact]
    public async Task Barcode_changed_in_owner_app_reaches_kassa_by_card_read_not_after_a_day()
    {
        SeedLocal("4600000000017");

        // Владелец поменял только штрихкод: PATCH {barcode} → сервер двинул updated_at, лента принесла сокращённую карточку (штрихкода в ней нет).
        var outcome = CatalogFeedSync.Apply(Page(Short("2026-11-11T05:10:00Z")), ApiBase, allowStock: true);
        outcome.Changed.Should().Be(0, "все 9 полей ленты те же — запись в базе не меняется");
        _repo.LoadTileCopy("b-1")!.Barcode.Should().Be("4600000000017", "воспроизведение: одна лента не может донести новый штрихкод — в ней его нет");
        outcome.HiddenChangeIds.Should().BeEquivalentTo(["b-1"], "товар пришёл с новым updated_at без видимых изменений — изменилось поле, которого в ленте нет");

        // Касса читает карточку по id (как AvaloniaCatalogCacheService.TickFeedAsync) — штрихкод тот же, что ввёл владелец.
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/b-1/", 200, Full("4870000000019"));
        var api = await LoginAsync(server);
        var fetched = await CatalogPointRefresh.ApplyByIdsAsync(api, outcome.HiddenChangeIds!, ApiBase, _ => true);
        fetched.Changed.Should().Be(1);

        var tile = _repo.LoadTileCopy("b-1")!;
        tile.Barcode.Should().Be("4870000000019");
        tile.BarcodeDisplay.Should().Be("4870000000019", "колонка «Код / штрихкод» Склада и плитка кассы показывают новый штрихкод");
        _repo.TryGetTileByBarcode("4870000000019")!.Id.Should().Be("b-1", "скан нового штрихкода находит товар");
        _repo.TryGetTileByBarcode("4600000000017").Should().BeNull("старый штрихкод больше не находит товар");
    }

    [Fact]
    public void Same_change_repeated_by_cursor_lookback_does_not_read_the_card_again()
    {
        SeedLocal("4600000000017");
        CatalogFeedSync.Apply(Page(Short("2026-11-11T05:10:00Z")), ApiBase, allowStock: true).HiddenChangeIds.Should().NotBeNull();
        CatalogFeedSync.Apply(Page(Short("2026-11-11T05:10:00Z")), ApiBase, allowStock: true).HiddenChangeIds
            .Should().BeNull("тот же updated_at — это повтор из запаса курсора (8 с), а не новое изменение");

        CatalogFeedSync.ForgetHiddenSeen(["b-1"]); // карточку не прочитали (сеть) — следующий повтор пробует снова
        CatalogFeedSync.Apply(Page(Short("2026-11-11T05:10:00Z")), ApiBase, allowStock: true).HiddenChangeIds.Should().BeEquivalentTo(["b-1"]);
    }

    [Fact]
    public void Stock_change_and_own_sale_do_not_read_cards()
    {
        SeedLocal("4600000000017");

        // Продажа на другой кассе: изменился остаток — это видно в самой ленте, карточку читать незачем.
        var sold = CatalogFeedSync.Apply(Page(Short("2026-11-11T05:20:00Z", quantity: "9")), ApiBase, allowStock: true);
        sold.Changed.Should().Be(1);
        sold.HiddenChangeIds.Should().BeNull();

        // Своя продажа (защита остатка FeedStockGuard): остаток из ленты не применяется — тоже не правка карточки.
        CatalogFeedSync.Apply(Page(Short("2026-11-11T05:21:00Z", quantity: "8")), ApiBase, allowStock: true, allowStockFor: _ => false)
            .HiddenChangeIds.Should().BeNull();

        // Цена изменилась (видно в ленте) — применяется сразу, карточку не читаем.
        CatalogFeedSync.Apply(Page(Short("2026-11-11T05:22:00Z", quantity: "9", price: "130.00")), ApiBase, allowStock: true)
            .HiddenChangeIds.Should().BeNull();
    }

    [Fact]
    public void Renamed_card_is_read_because_barcode_may_have_changed_with_the_name()
    {
        SeedLocal("4600000000017");
        var outcome = CatalogFeedSync.Apply(Page(Short("2026-11-11T05:30:00Z", name: "Сок яблочный 1 л (новый)")), ApiBase, allowStock: true);
        outcome.Changed.Should().Be(1, "название применяется сразу");
        outcome.HiddenChangeIds.Should().BeEquivalentTo(["b-1"], "правка карточки (название) — штрихкод могли поменять в том же сохранении");
    }

    [Fact]
    public void Full_cards_from_recent_sync_and_card_reads_never_trigger_extra_reads()
    {
        SeedLocal("4600000000017");
        // Полная карточка (сверка недавних, точечное обновление) несёт штрихкод сама — новых чтений не нужно (и нет петли «прочитали → снова читаем»).
        CatalogFeedSync.Apply(Page(Full("4870000000019")), ApiBase, allowStock: true).HiddenChangeIds.Should().BeNull();
        _repo.LoadTileCopy("b-1")!.Barcode.Should().Be("4870000000019");
    }

    // ---------------------------------------------------------------- поведение, на которое опирается разбор

    [Fact]
    public void Server_card_without_barcode_shows_product_code_as_barcode_in_kassa()
    {
        // Товар заведён без штрихкода (приёмка, загрузка): на сервере barcode пуст, код товара 15521. Сайт и мобилка штрихкод не показывают, касса — показывает «15521».
        var tile = ProductCatalogMapper.TryTile(J(Full("")), ApiBase)!;
        tile.Barcode.Should().Be("15521", "маппер берёт штрихкод из barcode → sku → article → code → ean");
        tile.CodeDisplay.Should().Be("15521");
        tile.BarcodeDisplay.Should().Be("15521", "в колонке «Код / штрихкод» одно и то же число дважды");
    }

    [Fact]
    public void Code_backfill_never_replaces_a_non_empty_barcode()
    {
        var target = new CatalogProductTileVm("b-1", "Сок", "120.00 сом", false) { Barcode = "4600000000017" };
        var fresh = new CatalogProductTileVm("b-1", "Сок", "120.00 сом", false) { Barcode = "15521", Article = "15521", ProductCode = "15521" };
        ProductCodeBackfill.MergeCodes(target, fresh).Should().BeTrue("артикул и код дописаны");
        target.Barcode.Should().Be("4600000000017", "дочитывание дописывает только пустое — настоящий штрихкод кодом товара не подменяется");
        target.ProductCode.Should().Be("15521");
    }

    [Fact]
    public void Upc_and_ean_forms_find_the_same_product_without_changing_the_stored_barcode()
    {
        CatalogFeedSync.Apply(Page(Full("0762497741537")), ApiBase, allowStock: true);
        _repo.TryGetTileByBarcode("762497741537")!.Id.Should().Be("b-1", "UPC-A (12 цифр) находит товар с EAN-13 «0…»");
        _repo.LoadTileCopy("b-1")!.Barcode.Should().Be("0762497741537", "ведущий ноль в базе не теряется — показывается как ввели");
        ProductCodeLookup.Normalize(" 0160 ").Should().Be("0160", "короткий код с нулём впереди не превращается в число");
        BarcodeForms.Alternate("4600000000017").Should().BeNull("значимые цифры обычного EAN-13 не меняются");
    }
}
