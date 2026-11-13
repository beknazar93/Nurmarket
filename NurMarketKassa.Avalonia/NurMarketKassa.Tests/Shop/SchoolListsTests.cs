using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ2): «Списки к школе» — шаблон в строки чека, пропуск удалённых товаров, предупреждение об остатке,
/// хранение в файле кассы (атомарно, ≤ 16 КБ), доступность только в сфере «Канцтовары».</summary>
[Collection("UserPreferences")]
public sealed class SchoolListsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-school-" + Guid.NewGuid().ToString("N"));
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;

    public SchoolListsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CatalogProductTileVm Tile(string id, string title, string price, double stock, bool service = false) =>
        new(id, title, price + " сом", false) { Quantity = stock, Kind = service ? "service" : null };

    private static SchoolList TwelveItems()
    {
        var list = new SchoolList { Name = "1 класс" };
        for (var i = 1; i <= 12; i++)
            list.Items.Add(new SchoolListItem { ProductId = "p" + i, Name = "Товар " + i, Quantity = i % 3 + 1 });
        return list;
    }

    [Fact]
    public void Template_of_12_products_gives_12_lines_at_regular_prices()
    {
        var list = TwelveItems();
        var catalog = Enumerable.Range(1, 12).ToDictionary(i => "p" + i, i => Tile("p" + i, "Товар " + i, (10 * i).ToString() + ".50", 100));

        var plan = SchoolListPlanner.Plan(list, id => catalog.GetValueOrDefault(id));

        plan.Should().HaveCount(12);
        plan.Should().OnlyContain(l => l.Status == SchoolLineStatus.Ok && l.CanAdd);
        plan.Select(l => l.UnitPrice).Should().Equal(Enumerable.Range(1, 12).Select(i => 10m * i + 0.5m));
        SchoolListPlanner.Total(plan).Should().Be(plan.Sum(l => (10m * int.Parse(l.Tile!.Id[1..]) + 0.5m) * l.Item.Quantity));
    }

    [Fact]
    public void Product_removed_from_catalog_is_skipped_with_its_name_kept()
    {
        var list = TwelveItems();
        var plan = SchoolListPlanner.Plan(list, id => id == "p1" ? Tile("p1", "Товар 1", "5", 9) : null);

        plan.Count(l => l.Status == SchoolLineStatus.Missing).Should().Be(11);
        plan.Where(l => l.Status == SchoolLineStatus.Missing).Should().OnlyContain(l => !l.CanAdd && l.UnitPrice == 0m && l.Item.Name.StartsWith("Товар"));
        SchoolListPlanner.Total(plan).Should().Be(5m * list.Items[0].Quantity);
    }

    [Fact]
    public void Zero_stock_warns_and_is_not_checked_by_default_but_can_still_be_added()
    {
        var list = new SchoolList { Name = "Офис-старт", Items = { new SchoolListItem { ProductId = "a", Name = "Ручка", Quantity = 3 }, new SchoolListItem { ProductId = "b", Name = "Папка", Quantity = 5 }, new SchoolListItem { ProductId = "c", Name = "Печать", Quantity = 2 } } };
        var catalog = new Dictionary<string, CatalogProductTileVm>
        {
            ["a"] = Tile("a", "Ручка", "20", 0),
            ["b"] = Tile("b", "Папка", "30", 2),
            ["c"] = Tile("c", "Печать", "10", 0, service: true),
        };

        var plan = SchoolListPlanner.Plan(list, id => catalog.GetValueOrDefault(id));

        plan[0].Status.Should().Be(SchoolLineStatus.OutOfStock);
        plan[0].CanAdd.Should().BeTrue("кассир может отметить сам — обычное добавление спросит про остаток");
        SchoolListPlanner.CheckedByDefault(plan[0]).Should().BeFalse();
        plan[1].Status.Should().Be(SchoolLineStatus.NotEnough);
        SchoolListPlanner.CheckedByDefault(plan[1]).Should().BeTrue();
        plan[2].Status.Should().Be(SchoolLineStatus.Ok, "услуга без остатка");
    }

    [Fact]
    public void Store_round_trips_atomically_and_clamps_quantities()
    {
        var path = Path.Combine(_dir, "school-lists.json");
        var lists = new List<SchoolList> { TwelveItems() };
        SchoolListStore.Save(lists, path).Should().BeNull();
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();

        var loaded = SchoolListStore.Load(path);
        loaded.Should().HaveCount(1);
        loaded[0].Name.Should().Be("1 класс");
        loaded[0].Items.Select(i => (i.ProductId, i.Quantity)).Should().Equal(lists[0].Items.Select(i => (i.ProductId, i.Quantity)));

        var fixedUp = SchoolListStore.Parse("[{\"name\":\"X\",\"items\":[{\"productId\":\"p\",\"quantity\":-5},{\"productId\":\"q\",\"quantity\":100000},{\"productId\":\"\",\"quantity\":1}]},{\"name\":\"\"}]")!;
        fixedUp.Should().ContainSingle();
        fixedUp[0].Items.Select(i => i.Quantity).Should().Equal(1, SchoolListStore.MaxQuantity);
    }

    [Fact]
    public void Store_rejects_more_than_16_kb_and_keeps_the_old_file()
    {
        var path = Path.Combine(_dir, "school-lists.json");
        SchoolListStore.Save(new List<SchoolList> { TwelveItems() }, path).Should().BeNull();
        var before = File.ReadAllText(path);

        var big = new SchoolList { Name = "Большой" };
        for (var i = 0; i < SchoolListStore.MaxItemsPerList; i++)
            big.Items.Add(new SchoolListItem { ProductId = Guid.NewGuid().ToString("N"), Name = new string('я', 120), Quantity = 1 });
        var lists = Enumerable.Range(0, 6).Select(n => new SchoolList { Name = "L" + n, Items = big.Items.ToList() }).ToList();

        SchoolListStore.Save(lists, path).Should().NotBeNull("больше 16 КБ");
        File.ReadAllText(path).Should().Be(before);
    }

    [Fact]
    public void Broken_or_missing_file_gives_empty_lists()
    {
        SchoolListStore.Load(Path.Combine(_dir, "nothing.json")).Should().BeEmpty();
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        SchoolListStore.Load(bad).Should().BeEmpty();
    }

    [Theory]
    [InlineData("grocery", null, false)]
    [InlineData("clothing", null, false)]
    [InlineData("services", null, false)]
    [InlineData("grocery", "hardware", false)]
    [InlineData("grocery", "stationery", true)]
    public void School_lists_are_available_only_in_stationery(string sphere, string? detail, bool expected)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = sphere;
        p.MarketSphereDetail = detail;
        SchoolListStore.Available.Should().Be(expected);
    }
}
