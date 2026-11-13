using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-12 (ХМ8, ТЗ хозмаг): цена прораба — клиент, отмеченный «Покупает по оптовой цене (прораб)», выбран в окне оплаты: окно закрывается,
/// чек пересчитывается по опту (только строки с оптовой ценой), окно открывается снова. Клиент без отметки, товар без оптовой цены и другие сферы — как раньше.</summary>
[Collection("Wave5Database")]
public sealed class ForemanPriceTests : IDisposable
{
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _groups = UserPreferences.Instance.ClientPriceGroupsJson;

    public ForemanPriceTests()
    {
        UserPreferences.Instance.ClientPriceGroupsJson = null;
        ClientPriceGroups.SetForeman("c-foreman", true);
    }

    public void Dispose()
    {
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        UserPreferences.Instance.ClientPriceGroupsJson = _groups;
    }

    /// <summary>Окна: на первом показе окна оплаты «кассир выбирает» клиента (если задан); окно закрывается отменой.</summary>
    private sealed class PickClientWindows : IWindowService
    {
        public ClientOption? PickOnFirst { get; set; }
        public int PaymentDialogs { get; private set; }

        public Task<TResult?> ShowDialogAsync<TViewModel, TResult>(TViewModel viewModel) where TViewModel : class
        {
            PaymentDialogs++;
            if (PaymentDialogs == 1 && PickOnFirst is { } client && viewModel is CheckoutViewModel checkout)
                checkout.PreselectClient(client);
            return Task.FromResult(default(TResult));
        }

        public void ShowWindow<TViewModel>(TViewModel viewModel) where TViewModel : class { }

        public void Close(object viewModel, bool? dialogResult = null) { }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static CatalogProductTileVm Tile(string id, double wholesale)
    {
        var tile = CartTestHelper.CreateProduct(id, "Саморез " + id, 100m);
        tile.WholesalePrice = wholesale;
        return tile;
    }

    private static (BasketPanelViewModel Vm, CartService Cart, PickClientWindows Windows) Create(params CatalogProductTileVm[] products)
    {
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        var windows = new PickClientWindows();
        var byId = products.ToDictionary(p => p.Id);
        var vm = new BasketPanelViewModel(cart, new FakePrompts(), new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), windows, new FakeDialogs(),
            new InlineDispatcher(), catalogLookup: id => byId.TryGetValue(id, out var t) ? t : null);
        foreach (var p in products)
            vm.AddProductFromCatalogWithOverride(p, 2, 100);
        return (vm, cart, windows);
    }

    private static ClientOption Foreman() => new() { Id = "c-foreman", DisplayName = "Прораб Бек · +996555000111" };

    [Fact]
    public async Task Hardware_ForemanChosenInPaymentWindow_RepricesToWholesale_AndReopensWindow()
    {
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = "hardware";
        var (vm, cart, windows) = Create(Tile("p1", 80), Tile("p2", 0));
        windows.PickOnFirst = Foreman();

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs == 2)).Should().BeTrue("окно оплаты открыто снова после пересчёта по цене прораба");
        vm.Lines.Single(l => l.ProductId == "p1").IsWholesale.Should().BeTrue("у товара есть оптовая цена");
        vm.Lines.Single(l => l.ProductId == "p2").IsWholesale.Should().BeFalse("у товара нет оптовой цены — розница");
        var line = CartDisplayHelper.EnumerateItems(cart.Root).First(it => CartDisplayHelper.TryProductId(it) == "p1");
        CartDisplayHelper.UnitPrice(line).Should().Be(80d, "цена строки — оптовая");
        windows.PaymentDialogs.Should().Be(2, "переоткрытие один раз, без зацикливания");
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public async Task Hardware_ClientWithoutMark_NoReprice()
    {
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = "hardware";
        var (vm, cart, windows) = Create(Tile("p1", 80));
        windows.PickOnFirst = new ClientOption { Id = "c-ordinary", DisplayName = "Обычный · +996555000222" };

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs >= 1)).Should().BeTrue();
        await Task.Delay(400);
        windows.PaymentDialogs.Should().Be(1);
        vm.Lines.Single().IsWholesale.Should().BeFalse();
        vm.Dispose();
        cart.Dispose();
    }

    [Theory]
    [InlineData("grocery")]
    [InlineData("clothing")]
    [InlineData("services")]
    public async Task OtherSpheres_MarkedClient_NoReprice(string sphere)
    {
        UserPreferences.Instance.MarketSphere = sphere;
        UserPreferences.Instance.MarketSphereDetail = null;
        var (vm, cart, windows) = Create(Tile("p1", 80));
        windows.PickOnFirst = Foreman();

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs >= 1)).Should().BeTrue();
        await Task.Delay(400);
        windows.PaymentDialogs.Should().Be(1, "в других сферах цены прораба нет");
        vm.Lines.Single().IsWholesale.Should().BeFalse();
        vm.Dispose();
        cart.Dispose();
    }
}
