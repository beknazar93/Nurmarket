using System.IO;
using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-21 (владелец, касса): «Убрать поисковик в фильтре! Фильтр должен пользоваться поисковиком каталога», «В фильтре "Сбросить" сливается с фоном»,
/// «Список отложенных нужно, чтобы все отложенные показывал!». Логика: у окна «Фильтр» своего поиска нет, условия фильтра работают вместе с поиском каталога и
/// не затирают его; «Сбросить» доступна только когда есть что сбрасывать; очередь отложенных чеков отдаёт все записи (30+) без отсечения.</summary>
[Collection("Wave5Database")]
public sealed class CatalogFilterAndHeldListTests : IDisposable
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

    private static CatalogProductTileVm Tile(string id, string title, string category, string? brand = null) =>
        new(id, title, "10.00 сом", false) { Barcode = "46" + id.PadLeft(11, '0'), Category = category, Brand = brand, Unit = "шт", Quantity = 5 };

    private static List<CatalogProductTileVm> Tiles() =>
    [
        Tile("1", "Молоко 1 л", "Молочка", "Айналайын"),
        Tile("2", "Молоко топлёное", "Молочка", "Нурсут"),
        Tile("3", "Хлеб белый", "Выпечка"),
        Tile("4", "Молоко соевое", "Бакалея"),
    ];

    private static CatalogPanelViewModel Publish(IEnumerable<CatalogProductTileVm> tiles)
    {
        var vm = new CatalogPanelViewModel(new FakeCache(), new InlineDispatcher(), new Online());
        typeof(CatalogPanelViewModel).GetMethod("PublishProducts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [tiles.ToList(), false]);
        return vm;
    }

    private static void SetSearch(CatalogPanelViewModel vm, string text)
    {
        // Поле «Поиск» каталога; фильтрация с задержкой ввода обходится — вызываем её сразу.
        typeof(CatalogPanelViewModel).GetField("_searchText", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, text);
        typeof(CatalogPanelViewModel).GetMethod("ApplyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
    }

    [Fact]
    public void FilterWindowModel_HasNoOwnSearch_AndCriteriaSearchIsEmpty()
    {
        typeof(CatalogFilterViewModel).GetProperty("SearchText").Should().BeNull("у окна «Фильтр» нет своего поля поиска");

        var vm = Publish(Tiles());
        var filter = vm.CreateFilterViewModel();
        filter.SelectedCategory = "Молочка";

        filter.BuildCriteria().SearchText.Should().BeEmpty();
    }

    [Fact]
    public void Reset_IsEnabledOnlyWhenSomethingToReset()
    {
        var filter = Publish(Tiles()).CreateFilterViewModel();
        filter.CanReset.Should().BeFalse();

        var changed = new List<string?>();
        filter.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        filter.PriceMin = "5";
        filter.CanReset.Should().BeTrue();
        changed.Should().Contain(nameof(CatalogFilterViewModel.CanReset));

        filter.Reset();
        filter.CanReset.Should().BeFalse();
        filter.PriceMin.Should().BeEmpty();
        filter.SelectedCategory.Should().Be(CatalogFilterViewModel.AllCategories);
    }

    [Fact]
    public void Filter_WorksTogetherWithCatalogSearch_AndKeepsTheSearchText()
    {
        var vm = Publish(Tiles());
        SetSearch(vm, "Молоко");
        vm.Products.Select(p => p.Title).Should().BeEquivalentTo("Молоко 1 л", "Молоко топлёное", "Молоко соевое");

        var filter = vm.CreateFilterViewModel();
        filter.SelectedCategory = "Молочка";
        vm.ApplyAdvancedFilter(filter.BuildCriteria());

        vm.SearchText.Should().Be("Молоко", "фильтр не затирает общий поиск каталога");
        vm.Products.Select(p => p.Title).Should().BeEquivalentTo("Молоко 1 л", "Молоко топлёное");

        vm.ResetAdvancedFilter();
        vm.SearchText.Should().Be("Молоко");
        vm.Products.Should().HaveCount(3);
    }

    [Fact]
    public void ApplyingTheSameFilterTwice_DoesNotRedrawTheList()
    {
        var vm = Publish(Tiles());
        var criteria = vm.CreateFilterViewModel();
        criteria.SelectedCategory = "Молочка";
        vm.ApplyAdvancedFilter(criteria.BuildCriteria());

        var redraws = 0;
        vm.StateChanged += (_, _) => redraws++;
        vm.ApplyAdvancedFilter(criteria.BuildCriteria());                                      // те же условия — ничего не перерисовываем
        vm.ApplyAdvancedFilter(vm.CreateFilterViewModel().BuildCriteria() with { Category = "Молочка" });

        redraws.Should().Be(0);
        vm.ActiveFilterCount.Should().Be(1);
    }

    [Fact]
    public void EmptyFilter_OnCleanCatalog_IsNoOp()
    {
        var vm = Publish(Tiles());
        var redraws = 0;
        vm.StateChanged += (_, _) => redraws++;

        vm.ApplyAdvancedFilter(vm.CreateFilterViewModel().BuildCriteria());

        redraws.Should().Be(0);
        vm.HasActiveFilter.Should().BeFalse();
    }

    // ---- отложенные чеки: очередь отдаёт все записи ----

    private static string DeferredFile => Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, NurMarketKassa.Services.AppMode.DataFolderName, "deferred_carts.json");

    private readonly bool _fileExisted = File.Exists(DeferredFile);
    private readonly byte[]? _fileBytes = File.Exists(DeferredFile) ? File.ReadAllBytes(DeferredFile) : null;

    public void Dispose()
    {
        if (_fileExisted && _fileBytes != null)
            File.WriteAllBytes(DeferredFile, _fileBytes);
        else if (File.Exists(DeferredFile))
            File.Delete(DeferredFile);
    }

    [Fact]
    public void DeferredQueue_Returns_All35Entries_WithoutLimitOrDateCutoff()
    {
        var ids = new List<string>();
        for (var i = 0; i < 35; i++)
        {
            var id = "test-held-" + i;
            ids.Add(id);
            DeferredCartsStore.Add(new DeferredCartEntry
            {
                Id = id,
                Label = "Чек " + i,
                SavedAt = DateTimeOffset.Now.AddDays(-i * 3), // самые старые — больше месяца назад: по дате не отсекаются
                CartJson = "{\"items\":[]}",
            });
        }
        try
        {
            var all = DeferredCartsStore.LoadAll();

            all.Count(x => x.Id.StartsWith("test-held-", StringComparison.Ordinal)).Should().Be(35);
            DeferredCartsStore.Count().Should().BeGreaterThanOrEqualTo(35);
        }
        finally
        {
            DeferredCartsStore.RemoveIds(ids);
        }
    }
}
