using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-06 (В8): фильтр каталога на большом списке считается в фоне (шаг 8), служебная строка статуса отделена от видимой (Д4-07).</summary>
[Collection("Wave5Database")]
public sealed class CatalogPanelTests
{
    private sealed class FakeCache : ICatalogCacheService
    {
        public List<CatalogProductTileVm> Items { get; } = new();

        public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CatalogSyncResult { Success = true });

        public bool TryLoadFromDatabase() => true;

        public IReadOnlyList<CatalogProductTileVm> GetProducts() => Items;
    }

    private sealed class Online : IConnectivityService
    {
        public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private static CatalogPanelViewModel Create(int count, out FakeCache cache)
    {
        cache = new FakeCache();
        for (var i = 0; i < count; i++)
            cache.Items.Add(CatalogTestHelper.CreateTile("id" + i, (i % 50 == 0 ? "Яблоко " : "Товар ") + i, "46000" + i.ToString("D7"), 10 + i % 90));
        var vm = new CatalogPanelViewModel(cache, new InlineDispatcher(), new Online());
        typeof(CatalogPanelViewModel).GetMethod("PublishProducts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(vm, new object[] { cache.Items.ToList(), false });
        return vm;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 8000)
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
    public async Task BigCatalog_Search_ShowsSameResultAsTitleOrder_AfterBackgroundFilter()
    {
        var vm = Create(CatalogPanelViewModel.BackgroundFilterThreshold + 500, out _);
        var expected = vm.Products.Count;
        expected.Should().BeGreaterThan(0);

        vm.SearchText = "яблоко";

        (await WaitAsync(() => vm.Products.Count > 0 && vm.Products.All(p => p.Title.StartsWith("Яблоко")))).Should().BeTrue("в окне только найденные товары");
        vm.Products.Select(p => p.Title).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        vm.ProductCountText.Should().Contain("из");
    }

    [Fact]
    public async Task BigCatalog_FastTyping_ShowsResultOfLastQueryOnly()
    {
        var vm = Create(CatalogPanelViewModel.BackgroundFilterThreshold + 500, out _);

        vm.SearchText = "тов";
        vm.SearchText = "яблоко 2450";

        (await WaitAsync(() => vm.Products.Count > 0 && vm.Products.All(p => p.Title.StartsWith("Яблоко 2450")))).Should().BeTrue();
        await Task.Delay(500); // старый результат («тов») не должен прийти позже и затереть последний
        vm.Products.Should().OnlyContain(p => p.Title.StartsWith("Яблоко 2450"));
    }

    [Fact]
    public async Task SmallCatalog_Search_StillWorks_Immediately()
    {
        var vm = Create(100, out _);

        vm.SearchText = "яблоко";

        (await WaitAsync(() => vm.Products.Count > 0 && vm.Products.All(p => p.Title.StartsWith("Яблоко")))).Should().BeTrue();
    }

    [Fact]
    public void RoutineStatus_IsNotVisible_ButGoesToServiceText()
    {
        var vm = Create(10, out _);

        vm.SetRoutineStatus("Каталог обновлён. Товаров: 10.");

        vm.StatusText.Should().Be("Каталог обновлён. Товаров: 10.");
        vm.IsStatusRoutine.Should().BeTrue();
        vm.VisibleStatusText.Should().BeEmpty("служебная строка не занимает место над каталогом");
        vm.HasVisibleStatus.Should().BeFalse();
        vm.ServiceStatusText.Should().Be("Каталог обновлён. Товаров: 10.");

        vm.StatusText = "Сервер недоступен — показан локальный каталог.";

        vm.IsStatusRoutine.Should().BeFalse("предупреждение — обычная запись, его видно");
        vm.VisibleStatusText.Should().Be("Сервер недоступен — показан локальный каталог.");
        vm.ServiceStatusText.Should().BeEmpty();
    }
}
