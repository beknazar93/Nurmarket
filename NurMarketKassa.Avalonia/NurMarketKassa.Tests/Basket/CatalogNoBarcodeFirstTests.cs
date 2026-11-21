using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-21 (просьба владельца): в каталоге кассы сначала товары без заводского штрихкода (хлеб и т. п.), потом остальные.
/// Только обычный просмотр; поиск идёт по совпадению; внутри группы прежний порядок (избранные → название); переключатель выключает правило.</summary>
[Collection("Wave5Database")]
public sealed class CatalogNoBarcodeFirstTests
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

    private static CatalogProductTileVm Tile(string id, string title, string? barcode, bool favorite = false, string? article = null, string? alt = null) =>
        new(id, title, "10.00 сом", false)
        {
            Barcode = barcode,
            Article = article,
            ProductCode = article,
            AlternateBarcodesRaw = alt,
            IsFavorite = favorite,
            Unit = "шт",
            Quantity = 10,
        };

    private static CatalogPanelViewModel Publish(IEnumerable<CatalogProductTileVm> tiles)
    {
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        typeof(CatalogPanelViewModel).GetMethod("PublishProducts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [tiles.ToList(), false]);
        return vm;
    }

    private static void WithSetting(bool on, Action body)
    {
        var prefs = UserPreferences.Instance;
        var before = prefs.CatalogNoBarcodeFirst;
        prefs.CatalogNoBarcodeFirst = on;
        try { body(); }
        finally { prefs.CatalogNoBarcodeFirst = before; }
    }

    private static List<CatalogProductTileVm> Mixed() =>
    [
        Tile("a", "Яблоки", "4600000000011"),
        Tile("b", "Хлеб", null),
        Tile("c", "Баранки", "4600000000028", favorite: true),
        Tile("d", "Булочка", "", favorite: true),
        Tile("e", "Нан", "0160", article: "0160"),   // «запасной» код из артикула — штрихкода нет
        Tile("f", "Арбуз", null),
        Tile("g", "Молоко", "4600000000035"),
    ];

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("", null, null, true)]
    [InlineData("   ", null, null, true)]
    [InlineData("4600000000011", null, null, false)]
    [InlineData("0160", "0160", null, true)]              // код из артикула
    [InlineData("0160", null, null, false)]               // короткий свой код без совпадения с артикулом — считаем штрихкодом
    [InlineData("12345678", "12345678", null, false)]     // EAN-8, даже если совпал с артикулом
    [InlineData(null, null, "4600000000042", false)]      // только доп. штрихкод — скан есть
    [InlineData("", "ABC", null, true)]
    public void HasNoFactoryBarcode_Signs(string? barcode, string? article, string? alt, bool expected)
    {
        var tile = Tile("x", "Товар", barcode, article: article, alt: alt);

        tile.HasNoFactoryBarcode.Should().Be(expected);
    }

    [Fact]
    public void Browse_NoBarcodeFirst_ThenFavoritesThenTitle()
    {
        WithSetting(true, () =>
        {
            var vm = Publish(Mixed());

            // без кода: Арбуз, Булочка★, Нан, Хлеб → избранные выше: Булочка, затем Арбуз, Нан, Хлеб; со штрихкодом: Баранки★, затем Молоко, Яблоки
            vm.Products.Select(p => p.Title).Should().Equal("Булочка", "Арбуз", "Нан", "Хлеб", "Баранки", "Молоко", "Яблоки");
        });
    }

    [Fact]
    public void Browse_SettingOff_KeepsOldOrder_FavoritesThenTitle()
    {
        WithSetting(false, () =>
        {
            var vm = Publish(Mixed());

            vm.Products.Select(p => p.Title).Should().Equal("Баранки", "Булочка", "Арбуз", "Молоко", "Нан", "Хлеб", "Яблоки");
        });
    }

    [Fact]
    public void Browse_IsStable_ForEqualTitles()
    {
        WithSetting(true, () =>
        {
            var tiles = new List<CatalogProductTileVm>
            {
                Tile("1", "Хлеб", null), Tile("2", "Хлеб", null), Tile("3", "Хлеб", "4600000000011"), Tile("4", "Хлеб", "4600000000028"),
            };
            var vm = Publish(tiles);

            vm.Products.Select(p => p.Id).Should().Equal("1", "2", "3", "4");
        });
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(30);
        }
        return condition();
    }

    [Fact]
    public async Task Search_IsNotAffected_RankingByMatch()
    {
        var before = UserPreferences.Instance.CatalogNoBarcodeFirst;
        UserPreferences.Instance.CatalogNoBarcodeFirst = true;
        try
        {
            var tiles = new List<CatalogProductTileVm>
            {
                Tile("1", "Мультихлеб", null),               // без кода, «хлеб» внутри слова
                Tile("2", "Хлеб белый", "4600000000011"),   // со штрихкодом, «хлеб» — начало названия
                Tile("3", "Хлеб чёрный", null),
            };
            var vm = Publish(tiles);
            vm.Products.Select(p => p.Id).Should().Equal(new[] { "1", "3", "2" }, "без поиска — сначала без штрихкода");

            vm.SearchText = "хлеб"; // поиск с паузой 220 мс

            (await WaitAsync(() => vm.Products.Select(p => p.Id).SequenceEqual(new[] { "2", "3", "1" }))).Should().BeTrue(
                "при поиске порядок по совпадению (начало слова выше), а не «без штрихкода»; получилось: " + string.Join(",", vm.Products.Select(p => p.Id)));
        }
        finally { UserPreferences.Instance.CatalogNoBarcodeFirst = before; }
    }

    [Fact]
    public async Task OneLetterQuery_IsStillBrowse_RuleApplies()
    {
        var before = UserPreferences.Instance.CatalogNoBarcodeFirst;
        UserPreferences.Instance.CatalogNoBarcodeFirst = true;
        try
        {
            var vm = Publish(Mixed());

            vm.SearchText = "я"; // поиск начинается с 2 символов
            await Task.Delay(500);

            vm.Products.Select(p => p.Title).Should().Equal("Булочка", "Арбуз", "Нан", "Хлеб", "Баранки", "Молоко", "Яблоки");
        }
        finally { UserPreferences.Instance.CatalogNoBarcodeFirst = before; }
    }

    [Fact]
    public void Toggle_ThroughPreferences_ResortsAtOnce()
    {
        var before = UserPreferences.Instance.CatalogNoBarcodeFirst;
        try
        {
            UserPreferences.Instance.CatalogNoBarcodeFirst = true;
            using var vm = Publish(Mixed());
            vm.Products.First().Title.Should().Be("Булочка");

            UserPreferences.SetCatalogNoBarcodeFirst(false);
            vm.Products.First().Title.Should().Be("Баранки", "правило выключено — прежний порядок, ★ выше");

            UserPreferences.SetCatalogNoBarcodeFirst(true);
            vm.Products.First().Title.Should().Be("Булочка");
        }
        finally
        {
            UserPreferences.SetCatalogNoBarcodeFirst(before);
        }
    }

    [Fact]
    public void Favorites_For1C_NoBarcodeFirst()
    {
        WithSetting(true, () =>
        {
            var vm = Publish(Mixed());

            vm.FavoriteProducts.Select(p => p.Title).Should().Equal("Булочка", "Баранки");
        });
    }

    [Fact]
    public void Category_Filter_StaysInsideCategory_NoBarcodeFirst()
    {
        WithSetting(true, () =>
        {
            var tiles = Mixed();
            foreach (var t in tiles.Where(t => t.Id is "a" or "b" or "g"))
                t.Category = "Еда";
            var vm = Publish(tiles);

            vm.ApplyAdvancedFilter(new CatalogFilterCriteria("", "", "Еда", null, null, null, false));

            vm.Products.Select(p => p.Title).Should().Equal("Хлеб", "Молоко", "Яблоки");
        });
    }

    [Fact]
    public void BigCatalog_15k_RuleCostsLittle()
    {
        var all = new List<CatalogProductTileVm>(15000);
        for (var i = 0; i < 15000; i++)
            all.Add(Tile("big" + i, "Товар номер " + (i * 7919 % 15000).ToString("D5"), i % 50 == 0 ? null : "46" + i.ToString("D11"), favorite: i % 97 == 0));
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        var compute = typeof(CatalogPanelViewModel).GetMethod("ComputeFiltered", BindingFlags.NonPublic | BindingFlags.Instance)!;
        List<CatalogProductTileVm> Run() => (List<CatalogProductTileVm>)compute.Invoke(vm, [all, ""])!;

        long Median(bool on)
        {
            long result = 0;
            WithSetting(on, () =>
            {
                Run(); // прогрев
                var times = new List<long>();
                for (var k = 0; k < 5; k++)
                {
                    var sw = Stopwatch.StartNew();
                    Run();
                    times.Add(sw.ElapsedMilliseconds);
                }
                times.Sort();
                result = times[2];
            });
            return result;
        }

        var off = Median(false);
        var on = Median(true);

        on.Should().BeLessThan(Math.Max(off * 2, off + 150), $"правило не должно заметно замедлять отбор (выкл. {off} мс, вкл. {on} мс)");
        on.Should().BeLessThan(1500);
        WithSetting(true, () => Run().Take(300).Should().OnlyContain(p => p.HasNoFactoryBarcode));
    }
}
