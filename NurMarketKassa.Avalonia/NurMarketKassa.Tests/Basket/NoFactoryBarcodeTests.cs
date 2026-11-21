using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-21 (просьба владельца): явная отметка «Без заводского штрихкода» — у хлеба на реальной базе внутренние 13-значные коды, по коду их не отличить.
/// Хранение (общие настройки магазина + облачный ключ), автоотметка по коду нашего генератора, порядок каталога «отметка ИЛИ прежняя эвристика».</summary>
[Collection("Wave5Database")]
public sealed class NoFactoryBarcodeTests : IDisposable
{
    private readonly string? _json = UserPreferences.Instance.NoFactoryBarcodeIdsJson;
    private readonly bool _first = UserPreferences.Instance.CatalogNoBarcodeFirst;

    public NoFactoryBarcodeTests()
    {
        UserPreferences.Instance.NoFactoryBarcodeIdsJson = null;
        UserPreferences.Instance.CatalogNoBarcodeFirst = true;
        NoFactoryBarcodes.ResetForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.NoFactoryBarcodeIdsJson = _json;
        UserPreferences.Instance.CatalogNoBarcodeFirst = _first;
        NoFactoryBarcodes.ResetForTests();
    }

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

    private static CatalogProductTileVm Tile(string id, string title, string? barcode) =>
        new(id, title, "10.00 сом", false) { Barcode = barcode, Unit = "шт", Quantity = 10 };

    private static CatalogPanelViewModel Publish(IEnumerable<CatalogProductTileVm> tiles)
    {
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        typeof(CatalogPanelViewModel).GetMethod("PublishProducts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, [tiles.ToList(), false]);
        return vm;
    }

    [Fact]
    public void Parse_IsSafe_BadJsonEmptyOrHuge_GivesEmptySet()
    {
        NoFactoryBarcodes.Parse(null).Should().BeEmpty();
        NoFactoryBarcodes.Parse("{это не json").Should().BeEmpty();
        NoFactoryBarcodes.Parse("{\"a\":1}").Should().BeEmpty();
        NoFactoryBarcodes.Parse("[\"a\",\" b \",\"\",null,\"" + new string('x', 70) + "\"]").Should().BeEquivalentTo(new[] { "a", "b" });
        NoFactoryBarcodes.Parse("[\"" + new string('1', NoFactoryBarcodes.MaxJsonBytes) + "\"]").Should().BeEmpty("больше лимита");
    }

    [Fact]
    public void TrySet_MarksAndUnmarks_WritesSharedSettingAndRaisesChanged()
    {
        var raised = 0;
        void Handler() => raised++;
        NoFactoryBarcodes.Changed += Handler;
        try
        {
            NoFactoryBarcodes.TrySet(new[] { "b1", "b2" }, true).Should().BeTrue();
            NoFactoryBarcodes.IsMarked("b1").Should().BeTrue();
            NoFactoryBarcodeMarks.IsMarked("b2").Should().BeTrue();
            NoFactoryBarcodes.Parse(UserPreferences.Instance.NoFactoryBarcodeIdsJson).Should().BeEquivalentTo(new[] { "b1", "b2" });
            raised.Should().Be(1);

            NoFactoryBarcodes.TrySet(new[] { "b1" }, true).Should().BeTrue();
            raised.Should().Be(1, "ничего не изменилось — события нет");

            NoFactoryBarcodes.TrySet(new[] { "b1" }, false).Should().BeTrue();
            NoFactoryBarcodes.IsMarked("b1").Should().BeFalse();
            NoFactoryBarcodes.IsMarked("b2").Should().BeTrue();

            NoFactoryBarcodes.TrySet(new[] { "b2" }, false).Should().BeTrue();
            UserPreferences.Instance.NoFactoryBarcodeIdsJson.Should().BeNull("последняя отметка снята — ключ пуст");
            raised.Should().Be(3);
        }
        finally
        {
            NoFactoryBarcodes.Changed -= Handler;
        }
    }

    [Fact]
    public void TrySet_TooBigList_IsRejected_AndOldValueStays()
    {
        NoFactoryBarcodes.TrySet(new[] { "keep" }, true).Should().BeTrue();
        var many = Enumerable.Range(0, 20000).Select(i => "id-" + new string('9', 24) + i);

        NoFactoryBarcodes.TrySet(many, true).Should().BeFalse("список больше 256 КБ не пишем");

        NoFactoryBarcodes.IsMarked("keep").Should().BeTrue();
        NoFactoryBarcodes.IsMarked("id-" + new string('9', 24) + "5").Should().BeFalse();
    }

    [Fact]
    public void CloudChange_IsPickedUp_ByRefresh()
    {
        NoFactoryBarcodes.IsMarked("c1").Should().BeFalse();

        UserPreferences.Instance.NoFactoryBarcodeIdsJson = "[\"c1\",\"c2\"]"; // так значение приходит из облака (SettingsCloudSync)

        NoFactoryBarcodes.Refresh().Should().BeTrue();
        NoFactoryBarcodes.IsMarked("c2").Should().BeTrue();
        NoFactoryBarcodes.Refresh().Should().BeFalse("без изменений");
    }

    [Fact]
    public void CloudKey_IsInCompanyKeys_AndPropertyExists()
    {
        typeof(UserPreferences).GetProperty(NoFactoryBarcodes.CloudKey).Should().NotBeNull();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NurMarketKassa.Infrastructure", "Services", "SettingsCloudSync.cs")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        File.ReadAllText(Path.Combine(dir!.FullName, "NurMarketKassa.Infrastructure", "Services", "SettingsCloudSync.cs")).Should().Contain("NoFactoryBarcodes.CloudKey");
    }

    [Fact]
    public void Tile_HasNoFactoryBarcode_IsMarkOrHeuristic()
    {
        var bread = Tile("bread", "Хлеб", "5852439227269"); // внутренний 13-значный код
        var milk = Tile("milk", "Молоко", "4600000000011");
        var roll = Tile("roll", "Булочка", "0160");
        roll.Article = "0160";
        var empty = Tile("empty", "Нан", null);
        bread.HasNoFactoryBarcode.Should().BeFalse("по коду не отличить");

        NoFactoryBarcodes.TrySet(new[] { "bread" }, true);

        bread.HasNoFactoryBarcode.Should().BeTrue("отметка");
        milk.HasNoFactoryBarcode.Should().BeFalse();
        roll.HasNoFactoryBarcode.Should().BeTrue("прежняя эвристика: запасной код");
        empty.HasNoFactoryBarcode.Should().BeTrue("прежняя эвристика: пустой код");
        bread.LooksWithoutFactoryBarcode.Should().BeFalse();
    }

    [Fact]
    public void Catalog_MarkedBreadStandsFirst_ThenUnmark_ThenOldOrder()
    {
        var tiles = new[]
        {
            Tile("a", "Яблоки", "4600000000011"), Tile("b", "Хлеб", "5852439227269"), Tile("c", "Арбуз", "4600000000028"), Tile("d", "Нан", "6108616231581"),
        };
        using var vm = Publish(tiles);
        vm.Products.Select(p => p.Title).Should().Equal(new[] { "Арбуз", "Нан", "Хлеб", "Яблоки" }, "ничего не отмечено — по названию");

        NoFactoryBarcodes.TrySet(new[] { "b", "d" }, true); // событие пересобирает каталог сразу
        vm.Products.Select(p => p.Title).Should().Equal(new[] { "Нан", "Хлеб", "Арбуз", "Яблоки" }, "отмеченные первыми, внутри — по названию");

        NoFactoryBarcodes.TrySet(new[] { "b", "d" }, false);
        vm.Products.Select(p => p.Title).Should().Equal(new[] { "Арбуз", "Нан", "Хлеб", "Яблоки" });
    }

    [Fact]
    public async Task Search_IgnoresMarks()
    {
        var tiles = new[] { Tile("1", "Мультихлеб", "4600000000011"), Tile("2", "Хлеб белый", "4600000000028"), Tile("3", "Хлеб чёрный", "5852439227269") };
        NoFactoryBarcodes.TrySet(new[] { "1", "3" }, true);
        using var vm = Publish(tiles);
        vm.Products.Select(p => p.Id).Should().Equal(new[] { "1", "3", "2" });

        vm.SearchText = "хлеб";

        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !vm.Products.Select(p => p.Id).SequenceEqual(new[] { "2", "3", "1" }))
            await Task.Delay(30);
        vm.Products.Select(p => p.Id).Should().Equal(new[] { "2", "3", "1" });
    }

    [Fact]
    public void Generator_RemembersCode_AndCatalogResolvesItToAMark()
    {
        var code = BarcodeGenerator.GenerateForCatalog();
        NoFactoryBarcodes.PendingCount.Should().BeGreaterThan(0);
        var bread = Tile("newbread", "Лепёшка", code);
        var milk = Tile("milk2", "Сметана", "4600000000011");

        NoFactoryBarcodes.ResolvePending(new[] { milk, bread });

        NoFactoryBarcodes.IsMarked("newbread").Should().BeTrue("товар с кодом нашего генератора отмечен");
        NoFactoryBarcodes.IsMarked("milk2").Should().BeFalse();
        var again = NoFactoryBarcodes.PendingCount;
        NoFactoryBarcodes.ResolvePending(new[] { bread });
        NoFactoryBarcodes.PendingCount.Should().Be(again, "код уже использован");
    }

    [Fact]
    public void Generator_WithNoteFalse_DoesNotRemember()
    {
        var before = NoFactoryBarcodes.PendingCount;

        BarcodeGenerator.Generate(null, null, note: false);

        NoFactoryBarcodes.PendingCount.Should().Be(before);
    }

    [Fact]
    public async Task Generator_ServerCodes_AreRemembered_Too()
    {
        var fromServer = Enumerable.Range(0, 3).Select(_ => BarcodeGenerator.Generate(null, null, note: false)).ToList();
        ServerBarcodes.NextOverride = (_, _) => Task.FromResult<IReadOnlyList<string>?>(fromServer);
        IReadOnlyList<string> codes;
        try { codes = await BarcodeGenerator.GenerateManyForCatalogAsync(3); }
        finally { ServerBarcodes.NextOverride = null; }
        codes.Should().Equal(fromServer);
        var tiles = codes.Select((c, i) => Tile("g" + i, "Товар " + i, c)).ToList();

        NoFactoryBarcodes.ResolvePending(tiles);

        tiles.Should().OnlyContain(t => NoFactoryBarcodes.IsMarked(t.Id));
    }

    [Fact]
    public async Task BulkGeneration_MarksTheProductsItGaveCodes()
    {
        BulkBarcodeGenerator.ReadBarcodeOverride = (_, _) => Task.FromResult<string?>("");
        BulkBarcodeGenerator.SaveOverride = (_, _, _) => Task.FromResult<string?>(null);
        BulkBarcodeGenerator.RefreshOverride = _ => Task.CompletedTask;
        BulkBarcodeGenerator.DelayOverride = (_, _) => Task.CompletedTask;
        try
        {
            var items = new List<CatalogProductTileVm> { Tile("n1", "Хлеб 1", null), Tile("n2", "Хлеб 2", null) };

            var result = await BulkBarcodeGenerator.RunAsync(items, null, "тест", CancellationToken.None);

            result.Created.Should().HaveCount(2);
            NoFactoryBarcodes.IsMarked("n1").Should().BeTrue();
            NoFactoryBarcodes.IsMarked("n2").Should().BeTrue();
        }
        finally
        {
            BulkBarcodeGenerator.ReadBarcodeOverride = null;
            BulkBarcodeGenerator.SaveOverride = null;
            BulkBarcodeGenerator.RefreshOverride = null;
            BulkBarcodeGenerator.DelayOverride = null;
        }
    }

    [Fact]
    public void BigCatalog_15k_WithManyMarks_StaysFast()
    {
        var all = new List<CatalogProductTileVm>(15000);
        for (var i = 0; i < 15000; i++)
            all.Add(Tile("m" + i, "Товар " + (i * 7919 % 15000).ToString("D5"), "46" + i.ToString("D11")));
        NoFactoryBarcodes.TrySet(Enumerable.Range(0, 15000).Where(i => i % 10 == 0).Select(i => "m" + i), true);
        using var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        var compute = typeof(CatalogPanelViewModel).GetMethod("ComputeFiltered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        List<CatalogProductTileVm> Run() => (List<CatalogProductTileVm>)compute.Invoke(vm, [all, ""])!;
        Run();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = Run();
        sw.Stop();

        result.Take(1500).Should().OnlyContain(p => p.HasNoFactoryBarcode);
        sw.ElapsedMilliseconds.Should().BeLessThan(1500);
    }
}
