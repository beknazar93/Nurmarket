using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-07, жалоба владельца «товары, принятые в программе владельца, не отображаются в кассе»: путь «приход в владельце → сервер → лента products/changes → касса».
/// Живой журнал этого ПК (07.11, приходы 07:55 и 08:01) показывает: новый товар доходит до кассы за ~1 с, его количество — за ещё ~2 с. Тесты закрепляют звенья цепочки:
/// новый товар приходит карточкой с ценой, приход (количество, закупка, цена) на уже известный товар применяется короткой записью без названия, защита остатка не прячет цену,
/// неизвестный товар в короткой записи просит полную загрузку (а не пропадает). Лента — страницы в памяти, настоящая база — во временной папке test-safe.ps1.</summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class OwnerToKassaFeedTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;

    public OwnerToKassaFeedTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
        NewProductCardBackfill.AutoRun = false;
        NewProductCardBackfill.ResetForTests();
        ProductCodeBackfill.ResetForTests();
    }

    public void Dispose()
    {
        NewProductCardBackfill.AutoRun = true;
        NewProductCardBackfill.ResetForTests();
        _repo.ClearAll();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogFeedPage Page(params string[] cards) => new() { Status = CatalogFeedStatus.Ok, Items = cards.Select(J).ToList(), NextCursor = "c" };

    // Новый товар: владелец завёл (остаток 0), потом принял 3 шт. (закупка 40, продажа 60).
    private const string Created = """{"id":"n-1","name":"Новый товар","price":"60.00","purchase_price":"40.00","quantity":"0","markup_percent":"50","updated_at":"2026-11-07T01:55:27Z"}""";
    private const string AfterReceipt = """{"id":"n-1","price":"60.00","purchase_price":"40.00","quantity":"3","updated_at":"2026-11-07T01:55:29Z"}""";

    [Fact]
    public void New_product_from_owner_appears_in_the_kassa_with_price_and_then_gets_the_received_stock()
    {
        CatalogFeedSync.Apply(Page(Created), ApiBase, allowStock: true).Added.Should().Be(1);
        var first = _repo.LoadTileCopy("n-1")!;
        first.Title.Should().Be("Новый товар");
        first.PriceValue.Should().Be(60, "цена видна сразу, как только товар дошёл до кассы");
        first.Quantity.Should().Be(0);

        var outcome = CatalogFeedSync.Apply(Page(AfterReceipt), ApiBase, allowStock: true);
        outcome.NeedFullSync.Should().BeFalse();
        outcome.Changed.Should().Be(1);
        var after = _repo.LoadTileCopy("n-1")!;
        after.Quantity.Should().Be(3, "приход владельца доходит до кассы лентой (остаток из короткой записи без названия)");
        after.PriceValue.Should().Be(60);
        after.Title.Should().Be("Новый товар", "короткая запись не стирает карточку");
    }

    [Fact]
    public void Receipt_on_a_known_product_updates_stock_and_price_without_a_full_load()
    {
        CatalogFeedSync.Apply(Page("""{"id":"e-1","name":"Старый товар","price":"50.00","purchase_price":"30.00","quantity":"10","unit":"шт","kind":"product"}"""), ApiBase, allowStock: true);

        var outcome = CatalogFeedSync.Apply(Page("""{"id":"e-1","price":"60.00","purchase_price":"40.00","quantity":"13"}"""), ApiBase, allowStock: true);

        outcome.NeedFullSync.Should().BeFalse();
        var tile = _repo.LoadTileCopy("e-1")!;
        tile.Quantity.Should().Be(13);
        tile.PriceValue.Should().Be(60);
    }

    [Fact]
    public void Stock_guard_keeps_the_local_quantity_but_never_hides_the_new_price()
    {
        CatalogFeedSync.Apply(Page("""{"id":"g-1","name":"Товар под защитой","price":"50.00","purchase_price":"30.00","quantity":"10","unit":"шт","kind":"product"}"""), ApiBase, allowStock: true);

        // Защита остатка (недавняя продажа на этом ПК): количество из ленты не применяется, цена — применяется; остаток перечитается отдельно (StockSyncService.ScheduleStockReread).
        CatalogFeedSync.Apply(Page("""{"id":"g-1","price":"60.00","quantity":"13"}"""), ApiBase, allowStock: true, allowStockFor: _ => false);

        var tile = _repo.LoadTileCopy("g-1")!;
        tile.Quantity.Should().Be(10, "остаток держит локальная активность");
        tile.PriceValue.Should().Be(60, "цена не зависит от защиты остатка");
    }

    [Fact]
    public void Short_record_for_an_unknown_product_asks_for_a_full_load()
    {
        var outcome = CatalogFeedSync.Apply(Page("""{"id":"x-404","price":"60.00","quantity":"3"}"""), ApiBase, allowStock: true);

        outcome.NeedFullSync.Should().BeTrue("товара без карточки в базе нет — касса не теряет его, а просит полную загрузку");
        _repo.LoadTileCopy("x-404").Should().BeNull();
    }

    [Fact]
    public void Product_created_without_a_sale_price_is_still_shown_and_gets_its_price_later()
    {
        // «Оставить без цены» в приёмке владельца: товар в кассе виден (цена «—»), цена придёт, когда владелец её поставит.
        CatalogFeedSync.Apply(Page("""{"id":"z-1","name":"Без цены","price":"0.00","purchase_price":"40.00","quantity":"3","markup_percent":"0"}"""), ApiBase, allowStock: true);
        _repo.LoadTileCopy("z-1").Should().NotBeNull("товар без цены не пропадает из каталога кассы");

        CatalogFeedSync.Apply(Page("""{"id":"z-1","price":"60.00"}"""), ApiBase, allowStock: true);
        _repo.LoadTileCopy("z-1")!.PriceValue.Should().Be(60);
    }
}
