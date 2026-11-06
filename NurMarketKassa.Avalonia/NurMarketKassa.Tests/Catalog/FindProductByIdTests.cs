using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>2026-11-06 (В8, шаг 17): поиск плитки по id через индекс должен давать тот же ответ, что прежний <c>Products.FirstOrDefault</c>, при любых правках списка.</summary>
[Collection("Wave5Database")]
public sealed class FindProductByIdTests : IDisposable
{
    private readonly List<NurMarketKassa.Models.Pos.CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();

    public FindProductByIdTests() => CatalogCacheService.Products.Clear();

    public void Dispose()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    private static NurMarketKassa.Models.Pos.CatalogProductTileVm Tile(string id) => CatalogTestHelper.CreateTile(id, "Товар " + id, "460" + id, 10);

    private static NurMarketKassa.Models.Pos.CatalogProductTileVm? Old(string id) =>
        CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Finds_ByExactAndIgnoreCase_AndReturnsNullForUnknown()
    {
        CatalogCacheService.Products.AddRange(new[] { Tile("a1"), Tile("B2"), Tile("c3") });

        CatalogCacheService.FindProductById("a1").Should().BeSameAs(Old("a1"));
        CatalogCacheService.FindProductById("b2").Should().BeSameAs(CatalogCacheService.Products[1]);
        CatalogCacheService.FindProductById("zzz").Should().BeNull();
        CatalogCacheService.FindProductById("").Should().BeNull();
        CatalogCacheService.FindProductById(null).Should().BeNull();
    }

    [Fact]
    public void SeesTileAddedAfterIndexWasBuilt()
    {
        CatalogCacheService.Products.Add(Tile("a1"));
        CatalogCacheService.FindProductById("a1").Should().NotBeNull();

        var added = Tile("late");
        CatalogCacheService.Products.Add(added);

        CatalogCacheService.FindProductById("late").Should().BeSameAs(added);
    }

    [Fact]
    public void SeesTileReplacedInPlace_AsNewObject()
    {
        CatalogCacheService.Products.AddRange(new[] { Tile("a1"), Tile("b2") });
        CatalogCacheService.FindProductById("b2").Should().NotBeNull();

        var replacement = Tile("b2");
        CatalogCacheService.Products[1] = replacement;

        CatalogCacheService.FindProductById("b2").Should().BeSameAs(replacement, "в индексе не остаётся старая плитка");
    }

    [Fact]
    public void ForgetsRemovedTile_AndSeesReplacedWholeCatalogOfSameSize()
    {
        CatalogCacheService.Products.AddRange(new[] { Tile("a1"), Tile("b2") });
        CatalogCacheService.FindProductById("a1").Should().NotBeNull();

        CatalogCacheService.Products.Clear();
        var x = Tile("x9");
        var y = Tile("y8");
        CatalogCacheService.Products.AddRange(new[] { x, y }); // то же число плиток, другие id

        CatalogCacheService.FindProductById("a1").Should().BeNull("такого товара в каталоге больше нет");
        CatalogCacheService.FindProductById("y8").Should().BeSameAs(y);
        CatalogCacheService.FindProductById("x9").Should().BeSameAs(x);
    }

    [Fact]
    public void SameAnswerAsOldSearch_ForManyTilesAndRandomEdits()
    {
        var rnd = new Random(7);
        for (var i = 0; i < 400; i++)
            CatalogCacheService.Products.Add(Tile("p" + i));

        for (var step = 0; step < 300; step++)
        {
            switch (rnd.Next(4))
            {
                case 0: CatalogCacheService.Products.Add(Tile("n" + step)); break;
                case 1: CatalogCacheService.Products[rnd.Next(CatalogCacheService.Products.Count)] = Tile("r" + step); break;
                case 2 when CatalogCacheService.Products.Count > 10: CatalogCacheService.Products.RemoveAt(rnd.Next(CatalogCacheService.Products.Count)); break;
            }

            var probe = CatalogCacheService.Products[rnd.Next(CatalogCacheService.Products.Count)].Id;
            CatalogCacheService.FindProductById(probe).Should().BeSameAs(Old(probe));
            var maybe = "p" + rnd.Next(500); // часть id уже заменена или убрана — ответ всё равно тот же, что у прежнего поиска
            CatalogCacheService.FindProductById(maybe).Should().BeSameAs(Old(maybe));
        }
    }
}
