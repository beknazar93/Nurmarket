using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-11-06, жалоба клиента (магазин одежды, 1.17.71): «Штрих-код и код не показывает» — в Складе колонка «Код / штрихкод» пустая, хотя в карточке товара
/// штрихкод есть. Список берёт данные из локальной базы, карточка — с сервера. Тесты без сети: настоящая база (NURMARKET_DATA_ROOT из test-safe.ps1).
/// Запускать только tools/qa/test-safe.ps1.
/// </summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class ProductCodesListTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;

    public ProductCodesListTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
    }

    public void Dispose() => _repo.ClearAll();

    // Полная карточка (products/list, products/{id}/) и сокращённая лента (9 полей) — как отдаёт сервер.
    private const string FullClothes = """
        {"id":"c-1","name":"Ансар Бантик (белый)","price":"850.00","purchase_price":"500.00","quantity":"7","unit":"шт","is_weight":false,"plu":null,
         "barcode":"5538198285770","alternate_barcodes":[],"kind":"simple","article":"","code":"","updated_at":"2026-11-06T01:00:00Z"}
        """;

    private const string ShortClothes = """
        {"id":"c-1","name":"Ансар Бантик (белый)","price":"900.00","purchase_price":"500.00","quantity":"6","discount_percent":"0","markup_percent":"80",
         "wholesale_price":"800.00","updated_at":"2026-11-06T02:00:00Z"}
        """;

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogProductTileVm Tile(string json) => ProductCatalogMapper.TryTile(J(json), ApiBase)!;

    private static CatalogFeedPage FeedPage(params string[] cards) => new()
    {
        Status = CatalogFeedStatus.Ok,
        Items = cards.Select(J).ToList(),
        NextCursor = "cursor-1",
    };

    [Fact]
    public void RowWithBarcodeInBase_IsShownInList_AfterReloadFromBase()
    {
        _repo.SyncReplaceAllWithDiff([Tile(FullClothes)]);

        var row = _repo.LoadTileCopy("c-1")!;
        row.Barcode.Should().Be("5538198285770");
        row.BarcodeDisplay.Should().Be("5538198285770", "нижняя строка колонки — штрихкод");
        ProductCodeBackfill.NeedsCodes(row).Should().BeFalse();
    }

    [Fact]
    public void FeedDelta_WithoutBarcode_DoesNotWipeBarcode_OfExistingRow()
    {
        _repo.SyncReplaceAllWithDiff([Tile(FullClothes)]);

        CatalogFeedSync.Apply(FeedPage(ShortClothes), ApiBase, allowStock: true).Changed.Should().Be(1);

        var after = _repo.LoadTileCopy("c-1")!;
        after.PriceLine.Should().Contain("900.00", "цену лента обновила");
        after.Barcode.Should().Be("5538198285770", "в карточке ленты нет ключа штрихкода — остаётся локальный");
        after.BarcodeDisplay.Should().Be("5538198285770");
    }

    [Fact]
    public void FeedDelta_WithoutName_PatchOnly_DoesNotWipeBarcode()
    {
        _repo.SyncReplaceAllWithDiff([Tile(FullClothes)]);

        CatalogFeedSync.Apply(FeedPage("""{"id":"c-1","price":"910.00","quantity":"5","updated_at":"2026-11-06T03:00:00Z"}"""), ApiBase, allowStock: true);

        _repo.LoadTileCopy("c-1")!.Barcode.Should().Be("5538198285770");
    }

    [Fact]
    public void FeedItem_WithBarcode_IsShown()
    {
        // Товара в базе не было, лента принесла карточку со штрихкодом — он виден в списке.
        CatalogFeedSync.Apply(FeedPage(FullClothes), ApiBase, allowStock: true).Added.Should().Be(1);

        _repo.LoadTileCopy("c-1")!.BarcodeDisplay.Should().Be("5538198285770");
    }

    [Fact]
    public void ApplyDetail_FillsEmptyCodes_InBaseAndOnRow_ButNeverOverwritesFilled()
    {
        // База, заполненная сокращённой лентой: у товара нет ни штрихкода, ни кода.
        _repo.SyncReplaceAllWithDiff([Tile(ShortClothes)]);
        var row = _repo.LoadTileCopy("c-1")!;
        ProductCodeBackfill.NeedsCodes(row).Should().BeTrue();
        row.CodeDisplay.Should().BeEmpty();
        row.BarcodeDisplay.Should().BeEmpty();

        // Пришла полная карточка (то, что видно в окне редактирования).
        ProductCodeBackfill.ApplyDetail(row, J(FullClothes), ApiBase).Should().BeTrue();

        row.Barcode.Should().Be("5538198285770", "строка списка получила штрихкод");
        row.BarcodeDisplay.Should().Be("5538198285770");
        var stored = _repo.LoadTileCopy("c-1")!;
        stored.Barcode.Should().Be("5538198285770", "и база тоже");
        stored.PriceLine.Should().Contain("900.00", "цена и остаток не тронуты");
        stored.Quantity.Should().Be(6);
        _repo.TryGetTileByBarcode("5538198285770")!.Id.Should().Be("c-1", "скан по штрихкоду находит товар");

        // Повтор с другим штрихкодом в карточке: уже заполненное не перезаписывается.
        ProductCodeBackfill.ApplyDetail(stored, J(FullClothes.Replace("5538198285770", "1111111111111")), ApiBase).Should().BeFalse();
        _repo.LoadTileCopy("c-1")!.Barcode.Should().Be("5538198285770");
    }

    [Fact]
    public void FillMissingCodes_EmptyFreshCard_ChangesNothing()
    {
        _repo.SyncReplaceAllWithDiff([Tile(ShortClothes)]);

        _repo.FillMissingCodes(Tile(ShortClothes)).Should().BeFalse("в карточке тоже нет кодов — записывать нечего");
        _repo.LoadTileCopy("c-1")!.Barcode.Should().BeNullOrEmpty();
    }

    [Fact]
    public void CodeDisplay_FallsBack_ArticleThenCodeThenPlu_AndBarcodeToAlternate()
    {
        var tile = new CatalogProductTileVm("x", "Товар", "1 сом", false, null);
        tile.CodeDisplay.Should().BeEmpty();

        tile.Plu = 12;
        tile.CodeDisplay.Should().Be("12");
        tile.ProductCode = "77";
        tile.CodeDisplay.Should().Be("77");
        tile.Article = "A-1";
        tile.CodeDisplay.Should().Be("A-1");

        tile.AlternateBarcodesRaw = "4600000000017\n4600000000024";
        tile.BarcodeDisplay.Should().Be("4600000000017", "основного штрихкода нет — первый дополнительный");
        tile.Barcode = "5538198285770";
        tile.BarcodeDisplay.Should().Be("5538198285770");
    }

    // ---------------------------------------------------------------- меню владельца: кнопка «»» не меняет высоту верха колонки

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RailTopSlot_ReservedSpace_DoesNotDependOnNavItemsOpen(bool phone, bool tabs)
    {
        // Нажатие на группу раскрывает колонку пунктов (open=true), выбор пункта сворачивает (open=false): место под кнопку (ReservesSpace) от этого не зависит —
        // меняется только её видимость, поэтому высота верха колонки и плотность меню не прыгают.
        OwnerRailTopSlot.ButtonShown(true, phone, tabs).Should().BeFalse("колонка пунктов раскрыта — кнопка не нужна");
        OwnerRailTopSlot.ButtonShown(false, phone, tabs).Should().Be(OwnerRailTopSlot.ReservesSpace(phone, tabs));
    }

    [Fact]
    public void RailTopSlot_NewMenuOnDesktop_KeepsSlotInBothStates()
    {
        OwnerRailTopSlot.ReservesSpace(phone: false, tabs: false).Should().BeTrue("в новом виде на ПК место под «»» занято всегда");
        OwnerRailTopSlot.ButtonShown(navItemsOpen: false, phone: false, tabs: false).Should().BeTrue();
        OwnerRailTopSlot.ButtonShown(navItemsOpen: true, phone: false, tabs: false).Should().BeFalse();
    }
}
