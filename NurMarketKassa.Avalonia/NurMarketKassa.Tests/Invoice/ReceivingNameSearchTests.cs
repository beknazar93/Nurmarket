using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Invoice;

/// <summary>2026-11-06 (Б-12): «Приём товара» без связи не находил существующий товар по названию (список памяти пуст, штрихкод искался ещё и в базе кассы) — риск дубля.
/// Тесты чистые: каталог памяти и «локальная база» подставляются делегатами, сеть и настоящая база не нужны.</summary>
public sealed class ReceivingNameSearchTests
{
    private static CatalogProductTileVm[] Catalog() =>
    [
        CatalogTestHelper.CreateTile("n1", "Сахар-рафинад 1кг", "4702154000023", 95),
        CatalogTestHelper.CreateTile("n2", "Молоко Простоквашино 1 литр", "4600000000011", 90),
        CatalogTestHelper.CreateTile("n3", "Печенье юбилейное 200 г", "4600000000028", 60),
    ];

    [Fact]
    public void Empty_memory_catalog_falls_back_to_the_local_database()
    {
        var local = Catalog();
        var outcome = ReceivingNameSearch.Find("Сахар-рафинад", Array.Empty<CatalogProductTileVm>(), () => local);

        outcome.Tiles.Select(t => t.Id).Should().ContainSingle().Which.Should().Be("n1");
        outcome.UsedLocalDatabase.Should().BeTrue();
        outcome.CatalogChecked.Should().BeTrue();
    }

    [Fact]
    public void Memory_catalog_is_used_first_and_the_database_is_not_read()
    {
        var calls = 0;
        var outcome = ReceivingNameSearch.Find("молоко", Catalog(), () => { calls++; return Catalog(); });

        outcome.Tiles.Select(t => t.Id).Should().Contain("n2");
        outcome.UsedLocalDatabase.Should().BeFalse();
        calls.Should().Be(0);
    }

    [Fact]
    public void Unavailable_database_and_empty_memory_do_not_claim_the_product_is_missing()
    {
        var outcome = ReceivingNameSearch.Find("Сахар-рафинад", Array.Empty<CatalogProductTileVm>(), () => throw new InvalidOperationException("база занята"));

        outcome.Tiles.Should().BeEmpty();
        outcome.CatalogChecked.Should().BeFalse("каталога на ПК нет — «товара нет» утверждать нельзя, новый не предлагаем");
    }

    [Fact]
    public void Missing_product_in_a_real_catalog_is_reported_as_checked_and_not_found()
    {
        var outcome = ReceivingNameSearch.Find("Огурцы маринованные", Catalog(), () => Catalog());

        outcome.Tiles.Should().BeEmpty();
        outcome.CatalogChecked.Should().BeTrue();
    }

    [Fact]
    public void Smart_match_finds_abbreviation_and_unit_variants_from_the_local_database()
    {
        var local = Catalog();
        var outcome = ReceivingNameSearch.Find("Мол. Простоквашино 1л", Array.Empty<CatalogProductTileVm>(), () => local, smart: true);

        outcome.UsedSmartMatch.Should().BeTrue();
        outcome.Tiles.Select(t => t.Id).Should().Contain("n2");
    }

    [Fact]
    public void Smart_match_is_off_by_default_so_typing_stays_cheap()
    {
        var outcome = ReceivingNameSearch.Find("Мол. Простоквашино 1л", Catalog(), () => Catalog());

        outcome.UsedSmartMatch.Should().BeFalse();
    }

    [Fact]
    public void Supplier_products_come_first()
    {
        var catalog = new[]
        {
            CatalogTestHelper.CreateTile("a1", "Хлеб белый", "1", 30),
            CatalogTestHelper.CreateTile("a2", "Хлеб черный", "2", 30),
        };
        var outcome = ReceivingNameSearch.Find("хлеб", catalog, null, new HashSet<string> { "a2" });

        outcome.Tiles.Select(t => t.Id).Should().Equal("a2", "a1");
    }

    [Fact]
    public void Too_short_query_returns_nothing()
    {
        ReceivingNameSearch.Find("с", Catalog(), () => Catalog()).Tiles.Should().BeEmpty();
    }
}
