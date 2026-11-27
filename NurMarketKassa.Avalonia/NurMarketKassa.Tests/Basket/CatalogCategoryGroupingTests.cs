using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-27 (владелец: «Добавь линии, разделение по категориям»): группировка каталога кассы по категориям и страницы с заголовками групп.
/// Проверяется: порядок групп («Без категории» последней, внутри — прежний порядок), когда группировки нет (поиск, «Избранные», одна категория, выключено,
/// каталог без категорий), страницы — заголовок не остаётся «сиротой» в конце страницы, все товары попадают ровно на одну страницу, число страниц честное.</summary>
[Collection("Wave5Database")]
public sealed class CatalogCategoryGroupingTests
{
    private sealed class FakeCache : ICatalogCacheService
    {
        public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CatalogSyncResult { Success = true });

        public bool TryLoadFromDatabase() => true;

        public IReadOnlyList<CatalogProductTileVm> GetProducts() => [];
    }

    private sealed class Online : IConnectivityService
    {
        public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static CatalogProductTileVm Tile(string id, string title, string? category, bool weighted = false, bool favorite = false) =>
        new(id, title, "10.00 сом", weighted)
        {
            Barcode = "46" + Math.Abs(id.GetHashCode() % 100000000000).ToString("D11"),
            Category = category,
            IsFavorite = favorite,
            Unit = weighted ? "кг" : "шт",
            Quantity = 10,
        };

    private static CatalogPanelViewModel Publish(IEnumerable<CatalogProductTileVm> tiles)
    {
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        typeof(CatalogPanelViewModel).GetMethod("PublishProducts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [tiles.ToList(), false]);
        return vm;
    }

    private static void WithGrouping(bool on, Action body)
    {
        var prefs = UserPreferences.Instance;
        var before = prefs.CatalogGroupByCategory;
        var noBarcode = prefs.CatalogNoBarcodeFirst;
        prefs.CatalogGroupByCategory = on;
        prefs.CatalogNoBarcodeFirst = true;
        try { body(); }
        finally
        {
            prefs.CatalogGroupByCategory = before;
            prefs.CatalogNoBarcodeFirst = noBarcode;
        }
    }

    private static List<CatalogProductTileVm> Shop() =>
    [
        Tile("1", "Молоко", "Молочка"),
        Tile("2", "Кола", "Напитки"),
        Tile("3", "Гвозди", null),
        Tile("4", "Кефир", "Молочка", favorite: true),
        Tile("5", "Сок", "напитки "),        // регистр и пробел — та же группа «Напитки»
        Tile("6", "Сыр весовой", "Молочка", weighted: true),
        Tile("7", "Батарейки", "  "),         // пустая категория — «Без категории»
        Tile("8", "Айран", "Молочка"),
    ];

    // ─────────────────────────────── порядок и когда группировать

    [Fact]
    public void Browse_GroupsByCategory_UncategorizedLast_InsideGroupOldOrder()
    {
        WithGrouping(true, () =>
        {
            var vm = Publish(Shop());

            vm.IsGroupedByCategory.Should().BeTrue();
            // «★ Избранные» (Кефир) — отдельная первая группа; Молочка; Напитки; Без категории — последней.
            vm.Products.Select(p => p.Title).Should().Equal("Кефир", "Айран", "Молоко", "Сыр весовой", "Кола", "Сок", "Батарейки", "Гвозди");
            vm.CategoryHeader(CatalogCategoryGrouping.FavoritesKey).Should().Be((CatalogCategoryGrouping.FavoritesTitle, 1));
            vm.CategoryHeader("Молочка").Should().Be(("Молочка", 3));
            vm.CategoryHeader("НАПИТКИ").Count.Should().Be(2);
            vm.CategoryHeader("").Should().Be((CatalogCategoryGrouping.UncategorizedTitle, 2));
        });
    }

    // 2026-11-27 (владелец: «ты сбил избранные товары в каталоге»): избранные из любой категории — в начале каталога, а не в своей категории.
    [Fact]
    public void Favorites_FromAnyCategory_StandFirst()
    {
        WithGrouping(true, () =>
        {
            var tiles = Shop();
            tiles.Add(Tile("9", "Спрайт", "Напитки", favorite: true));
            tiles.Add(Tile("10", "Шуруп", null, favorite: true));
            var vm = Publish(tiles);

            vm.Products.Take(3).Select(p => p.Title).Should().Equal("Кефир", "Спрайт", "Шуруп");
            vm.Products.Skip(3).Any(p => p.IsFavorite).Should().BeFalse();
            vm.CategoryHeader(CatalogCategoryGrouping.FavoritesKey).Count.Should().Be(3);
        });
    }

    [Fact]
    public void WeightedTab_GroupsInsideTab()
    {
        WithGrouping(true, () =>
        {
            var tiles = Shop();
            tiles.Add(Tile("9", "Яблоки", "Фрукты", weighted: true));
            var vm = Publish(tiles);

            vm.SelectedTabIndex = 1; // «Весовые» (маленький каталог — отбор сразу)

            vm.IsGroupedByCategory.Should().BeTrue();
            vm.Products.Select(p => p.Title).Should().Equal("Сыр весовой", "Яблоки");
            vm.CategoryHeader("Фрукты").Count.Should().Be(1, "счётчик — по отбору вкладки, а не по всему каталогу");
        });
    }

    [Fact]
    public void Search_NotGrouped_KeepsMatchOrder()
    {
        WithGrouping(true, () =>
        {
            var vm = Publish(Shop());
            vm.RestoreState(new CatalogPanelState { SearchText = "ке" }); // поиск 2+ символа (сразу, без паузы набора)

            vm.IsGroupedByCategory.Should().BeFalse("при поиске порядок — по совпадению");
            vm.Products.Select(p => p.Title).Should().Contain("Кефир");
        });
    }

    [Fact]
    public void SettingOff_OldOrder_NoGroups()
    {
        WithGrouping(false, () =>
        {
            var vm = Publish(Shop());

            vm.IsGroupedByCategory.Should().BeFalse();
            vm.Products.Select(p => p.Title).Should().Equal("Кефир", "Айран", "Батарейки", "Гвозди", "Кола", "Молоко", "Сок", "Сыр весовой");
        });
    }

    [Fact]
    public void SettingToggle_RebuildsImmediately()
    {
        var prefs = UserPreferences.Instance;
        var before = prefs.CatalogGroupByCategory;
        try
        {
            prefs.CatalogGroupByCategory = false;
            var vm = Publish(Shop());
            vm.IsGroupedByCategory.Should().BeFalse();

            UserPreferences.SetCatalogGroupByCategory(true);

            vm.IsGroupedByCategory.Should().BeTrue();
            vm.Products.Last().Title.Should().Be("Гвозди");
            vm.Dispose();
        }
        finally
        {
            UserPreferences.SetCatalogGroupByCategory(before);
        }
    }

    [Fact]
    public void CatalogWithoutCategories_NotGrouped()
    {
        WithGrouping(true, () =>
        {
            var vm = Publish(Shop().Select(t => { t.Category = t.Id == "1" ? "Молочка" : null; return t; }));

            vm.IsGroupedByCategory.Should().BeFalse("одна категория на весь каталог — делить нечего");
        });
    }

    [Fact]
    public void FilterByOneCategory_Or_Favorites_NotGrouped()
    {
        WithGrouping(true, () =>
        {
            var vm = Publish(Shop());

            vm.ApplyAdvancedFilter(new CatalogFilterCriteria("", "", "Молочка", null, null, null, false));
            vm.IsGroupedByCategory.Should().BeFalse();

            vm.ApplyAdvancedFilter(new CatalogFilterCriteria("", CatalogFilterViewModel.FavoriteProducts, null, null, null, null, false));
            vm.IsGroupedByCategory.Should().BeFalse("«Избранные» — короткий список своих товаров, без разделителей");
        });
    }

    // ─────────────────────────────── страницы

    [Fact]
    public void PageStarts_Ungrouped_SameAsBefore()
    {
        CatalogCategoryGrouping.PageStarts(120, 50).Should().Equal(0, 50, 100);
        CatalogCategoryGrouping.PageStarts(100, 50).Should().Equal(0, 50);
        CatalogCategoryGrouping.PageStarts(0, 50).Should().Equal(0);
    }

    [Fact]
    public void PageStarts_HeaderNeverOrphan_SmallExample()
    {
        // страница 10 мест: заголовок A + 7 плиток = 8 мест; у B осталось 2 места — заголовок и одна плитка, а группа больше → B с новой страницы.
        var keys = Enumerable.Repeat("A", 7).Concat(Enumerable.Repeat("B", 5)).ToList();

        var starts = CatalogCategoryGrouping.PageStarts(keys, 10);

        starts.Should().Equal(0, 7);
    }

    [Fact]
    public void PageStarts_SmallGroupFitsAtPageEnd()
    {
        // группа из одной плитки помещается последней (заголовок + её плитка) — переноса нет.
        var keys = Enumerable.Repeat("A", 7).Append("B").Concat(Enumerable.Repeat("C", 3)).ToList();

        var starts = CatalogCategoryGrouping.PageStarts(keys, 10);

        starts.Should().Equal(0, 8);
    }

    [Theory]
    [InlineData(50, 1)]
    [InlineData(50, 2)]
    [InlineData(6, 3)]
    [InlineData(12, 4)]
    public void PageStarts_Invariants_OnRandomCatalogs(int pageSize, int seed)
    {
        var rnd = new Random(seed);
        for (var run = 0; run < 200; run++)
        {
            var keys = new List<string>();
            var groups = rnd.Next(1, 30);
            for (var g = 0; g < groups; g++)
                keys.AddRange(Enumerable.Repeat(g == groups - 1 ? "" : "K" + g, rnd.Next(1, 3 * pageSize)));

            var starts = CatalogCategoryGrouping.PageStarts(keys, pageSize);
            CheckPages(keys, starts, pageSize);
        }
    }

    private static void CheckPages(IReadOnlyList<string> keys, IReadOnlyList<int> starts, int pageSize)
    {
        starts[0].Should().Be(0);
        starts.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        for (var p = 0; p < starts.Count; p++)
        {
            var from = starts[p];
            var to = p + 1 < starts.Count ? starts[p + 1] : keys.Count;
            to.Should().BeGreaterThan(from, "пустых страниц нет");
            var slots = 0;
            for (var i = from; i < to; i++)
            {
                var groupStart = i == from || !CatalogCategoryGrouping.SameGroup(keys[i], keys[i - 1]);
                slots += groupStart ? 2 : 1;
                if (groupStart && i > from)
                {
                    // заголовок группы в середине страницы: под ним на этой же странице не меньше двух плиток (или вся группа, если она меньше)
                    var groupEnd = i;
                    while (groupEnd < keys.Count && CatalogCategoryGrouping.SameGroup(keys[groupEnd], keys[i]))
                        groupEnd++;
                    var onPage = Math.Min(groupEnd, to) - i;
                    onPage.Should().BeGreaterThanOrEqualTo(Math.Min(CatalogCategoryGrouping.MinTilesUnderHeader, groupEnd - i), "заголовок не остаётся сиротой в конце страницы");
                }
            }
            slots.Should().BeLessThanOrEqualTo(pageSize, "страница с заголовками не длиннее размера страницы");
        }
    }

    [Fact]
    public void Vm_Paging_EveryProductExactlyOnce_PageCountFromHeaders()
    {
        WithGrouping(true, () =>
        {
            var tiles = new List<CatalogProductTileVm>();
            for (var i = 0; i < 180; i++)
                tiles.Add(Tile("p" + i, $"Товар {i:D3}", i % 9 == 8 ? null : "Категория " + (i % 7)));
            var vm = Publish(tiles);

            vm.IsGroupedByCategory.Should().BeTrue();
            vm.TotalPages.Should().Be(vm.PageStartIndexes.Count);
            // 180 товаров + заголовки (по одному на группу и на каждое начало страницы) не меньше 4 полных страниц по 50 мест
            vm.TotalPages.Should().BeGreaterThanOrEqualTo(4);

            var seen = new List<string>();
            for (var page = 1; page <= vm.TotalPages; page++)
            {
                vm.GoToPageCommand.Execute(page);
                vm.CurrentPage.Should().Be(page);
                seen.AddRange(vm.Products.Select(p => p.Id));
                var keys = vm.Products.Select(CatalogCategoryGrouping.KeyOf).ToList();
                // на странице: заголовков = 1 (начало) + смен категории; мест ≤ 50
                var headers = 1 + Enumerable.Range(1, keys.Count - 1).Count(i => !CatalogCategoryGrouping.SameGroup(keys[i], keys[i - 1]));
                (keys.Count + headers).Should().BeLessThanOrEqualTo(50);
            }

            seen.Should().HaveCount(180).And.OnlyHaveUniqueItems();
            // «Без категории» — последние товары последней страницы
            vm.Products.Last().Category.Should().BeNull();
        });
    }

    [Fact]
    public void BigCatalog_15k_GroupingAndPagingCostLittle()
    {
        WithGrouping(true, () =>
        {
            var all = new List<CatalogProductTileVm>(15000);
            for (var i = 0; i < 15000; i++)
                all.Add(Tile("big" + i, "Товар номер " + (i * 7919 % 15000).ToString("D5"), i % 23 == 0 ? null : "Категория " + i % 40));
            var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
            var compute = typeof(CatalogPanelViewModel).GetMethod("ComputeFiltered", BindingFlags.NonPublic | BindingFlags.Instance)!;
            List<CatalogProductTileVm> Run() => (List<CatalogProductTileVm>)compute.Invoke(vm, [all, ""])!;

            var sorted = Run(); // прогрев
            sorted.Should().HaveCount(15000);
            CatalogCategoryGrouping.KeyOf(sorted[^1]).Should().BeEmpty("«Без категории» — в конце");

            var sw = Stopwatch.StartNew();
            sorted = Run();
            sw.Stop();
            sw.ElapsedMilliseconds.Should().BeLessThan(2000, "отбор с группировкой 15 000 товаров (делается в фоне)");

            sw.Restart();
            var starts = CatalogCategoryGrouping.PageStarts(sorted, 50, grouped: true);
            var counts = CatalogCategoryGrouping.CountByGroup(sorted);
            sw.Stop();
            sw.ElapsedMilliseconds.Should().BeLessThan(150, "страницы и счётчики считаются один раз на отбор");
            counts.Should().HaveCount(41);
            CheckPages(sorted.Select(CatalogCategoryGrouping.KeyOf).ToList(), starts, 50);
        });
    }
}
