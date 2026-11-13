using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;
using NurMarketKassa.ViewModels.Main;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-13 (ответ Умара 13.11, п. 4, «Строймаркет»; находка проверяющего): «цена прораба» — серверный уровень цен клиента (price_level). Адреса price-levels/, products/&lt;id&gt;/prices/,
/// price-levels/quote/, clients/&lt;id&gt;/objects/ (WireMock); расчёт: онлайн — quote, без связи — последние полученные цены, нет цены — обычная; в чек цена уровня идёт явной ценой строки;
/// локальная отметка «прораб» без права «Изменение цены» цен не меняет и не ставится; цены уровня сервера применяются без этого права (их назначил владелец на сервере).</summary>
[Collection("Wave5Database")]
public sealed class PriceLevelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-levels-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _groups = UserPreferences.Instance.ClientPriceGroupsJson;
    private readonly string _clientsFile = OfflineClientCache.FilePath;
    private readonly FakeNurCrmServer _fake = new();
    private NurMarketApiClient? _api;

    public PriceLevelTests()
    {
        Directory.CreateDirectory(_dir);
        PriceLevelCache.FileOverride = Path.Combine(_dir, "levels.json");
        PriceLevelCache.ResetForTests();
        OfflineClientCache.FilePath = Path.Combine(_dir, "clients.json");
        OfflineClientCache.ResetMemory();
        UserPreferences.Instance.ClientPriceGroupsJson = null;
        PriceLevelPricing.OnlineOverride = () => true;
        PriceLevelPricing.ApiOverride = () => _api;
    }

    public void Dispose()
    {
        PriceLevelPricing.ApiOverride = null;
        PriceLevelPricing.OnlineOverride = null;
        PriceLevelCache.FileOverride = null;
        PriceLevelCache.ResetForTests();
        OfflineClientCache.FilePath = _clientsFile;
        OfflineClientCache.ResetMemory();
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        UserPreferences.Instance.ClientPriceGroupsJson = _groups;
        _fake.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private async Task<PriceLevelsApi> ApiAsync()
    {
        _api = await LoggedInAsync(_fake);
        return new PriceLevelsApi(_api);
    }

    private static void RememberClient(string id, string rowExtra)
    {
        using var doc = JsonDocument.Parse("{\"id\":\"" + id + "\",\"full_name\":\"Прораб Бек\",\"phone\":\"+996555000111\",\"type\":\"client\"" + rowExtra + "}");
        OfflineClientCache.RememberClients(new[] { doc.RootElement.Clone() });
    }

    // ───────────── адреса сервера ─────────────

    [Fact]
    public async Task Api_Levels_ProductPrices_Quote_Objects()
    {
        _fake.Json("GET", "/api/main/price-levels/", 200, "{\"results\":[{\"id\":\"l1\",\"name\":\"Прораб\"},{\"id\":\"l2\",\"name\":\"Опт\"}]}");
        _fake.Json("GET", "/api/main/products/p1/prices/", 200, "[{\"level\":\"l1\",\"price\":\"480.00\",\"min_qty\":\"10\"},{\"level\":{\"id\":\"l2\"},\"price\":\"450\"}]");
        _fake.Json("POST", "/api/main/price-levels/quote/", 200, "{\"items\":[{\"product\":\"p1\",\"qty\":\"2.5\",\"unit_price\":\"480.00\"},{\"product\":\"p2\",\"unit_price\":\"95.5\"}],\"total\":\"1391.00\"}");
        _fake.Json("GET", "/api/main/clients/c1/objects/", 200, "[{\"id\":\"o1\",\"name\":\"Дом на Ленина\",\"address\":\"Ленина 5\"}]");
        _fake.Json("POST", "/api/main/clients/c1/objects/", 201, "{\"id\":\"o2\",\"name\":\"Склад\",\"address\":\"Промзона\"}");
        var api = await ApiAsync();

        (await api.GetLevelsAsync()).Select(l => l.Name).Should().Equal("Прораб", "Опт");
        var prices = await api.GetProductPricesAsync("p1");
        prices.Should().HaveCount(2);
        prices[0].Price.Should().Be(480m);
        prices[0].MinQty.Should().Be(10m);
        prices[1].LevelId.Should().Be("l2");

        var quote = await api.QuoteAsync("c1", new[] { ("p1", 2.5), ("p2", 1d) });
        quote["p1"].Should().Be(480m);
        quote["p2"].Should().Be(95.5m);
        var body = BodyOf(Requests(_fake, "POST", "/price-levels/quote/").Single());
        body["client"]!.ToString().Should().Be("c1");
        var items = body["items"]!.AsArray();
        items[0]!["product"]!.ToString().Should().Be("p1");
        items[0]!["qty"]!.ToString().Should().Be("2.5");

        (await api.GetObjectsAsync("c1")).Single().Name.Should().Be("Дом на Ленина");
        var created = await api.CreateObjectAsync("c1", " Склад ", "Промзона");
        created!.Id.Should().Be("o2");
        BodyOf(Requests(_fake, "POST", "/clients/c1/objects/").Single())["name"]!.ToString().Should().Be("Склад");
    }

    // ───────────── клиент и память ─────────────

    [Theory]
    [InlineData(",\"price_level\":\"l1\"", "l1")]
    [InlineData(",\"price_level\":7", "7")]
    [InlineData(",\"price_level\":{\"id\":\"l9\",\"name\":\"Прораб\"}", "l9")]
    [InlineData(",\"price_level\":null", null)]
    [InlineData("", null)]
    public void Client_PriceLevel_IsRemembered(string extra, string? expected)
    {
        RememberClient("c1", extra);
        PriceLevelPricing.LevelOf("c1").Should().Be(expected);
    }

    [Fact]
    public void Client_LevelRemoved_OnNextList()
    {
        RememberClient("c1", ",\"price_level\":\"l1\"");
        RememberClient("c1", ",\"price_level\":null");
        PriceLevelPricing.LevelOf("c1").Should().BeNull("владелец снял уровень на сервере — касса его больше не применяет");
    }

    // ───────────── расчёт цен ─────────────

    private static PriceLevelLine[] Lines() => new[] { new PriceLevelLine("i1", "p1", 2), new PriceLevelLine("i2", "p2", 1) };

    [Fact]
    public async Task Pricing_Online_UsesQuote_AndRemembersForOffline()
    {
        _fake.Json("POST", "/api/main/price-levels/quote/", 200, "{\"items\":[{\"product\":\"p1\",\"unit_price\":\"480\"},{\"product\":\"p2\",\"unit_price\":\"95.5\"}]}");
        await ApiAsync();

        var online = await PriceLevelPricing.ResolveAsync("c1", "l1", Lines());
        online.FromCache.Should().BeFalse();
        online.Missing.Should().Be(0);
        online.PriceByItem["i1"].Should().Be(480m);
        online.PriceByItem["i2"].Should().Be(95.5m);

        // Без связи — последние полученные цены того же уровня.
        PriceLevelPricing.OnlineOverride = () => false;
        PriceLevelCache.ResetForTests(); // из файла, как после перезапуска
        var offline = await PriceLevelPricing.ResolveAsync("c1", "l1", Lines());
        offline.FromCache.Should().BeTrue();
        offline.PriceByItem["i1"].Should().Be(480m);
        offline.Missing.Should().Be(0);
        Requests(_fake, "POST", "/price-levels/quote/").Should().HaveCount(1, "офлайн запросов нет");
    }

    [Fact]
    public async Task Pricing_OfflineWithoutMemory_NoPrices_Missing()
    {
        await ApiAsync();
        PriceLevelPricing.OnlineOverride = () => false;

        var result = await PriceLevelPricing.ResolveAsync("c1", "l1", Lines());

        result.PriceByItem.Should().BeEmpty("цены уровня нет нигде — строки останутся по обычной цене");
        result.Missing.Should().Be(2);
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Pricing_ServerError_FallsBackToMemory_NoMemory_NoPrice()
    {
        _fake.Json("POST", "/api/main/price-levels/quote/", 200, "{\"items\":[{\"product\":\"p1\",\"unit_price\":\"480\"}]}");
        await ApiAsync();
        await PriceLevelPricing.ResolveAsync("c1", "l1", new[] { new PriceLevelLine("i1", "p1", 1) });

        _fake.Error("POST", "/api/main/price-levels/quote/", 500);
        var result = await PriceLevelPricing.ResolveAsync("c1", "l1", Lines());

        result.PriceByItem["i1"].Should().Be(480m, "сервер не ответил — последняя полученная цена товара");
        result.PriceByItem.ContainsKey("i2").Should().BeFalse();
        result.Missing.Should().Be(1);
        result.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task Pricing_OnlineQuoteWithoutProduct_DoesNotTakeStaleMemory()
    {
        PriceLevelCache.Remember("l1", new Dictionary<string, decimal> { ["p2"] = 90m });
        _fake.Json("POST", "/api/main/price-levels/quote/", 200, "{\"items\":[{\"product\":\"p1\",\"unit_price\":\"480\"}]}");
        await ApiAsync();

        var result = await PriceLevelPricing.ResolveAsync("c1", "l1", Lines());

        result.PriceByItem.Should().ContainKey("i1");
        result.PriceByItem.ContainsKey("i2").Should().BeFalse("при живой связи ответ сервера главный: товара нет в ответе — обычная цена, а не старая");
        result.Missing.Should().Be(1);
    }

    // ───────────── строка чека ─────────────

    [Fact]
    public void Cart_LinePriceLevel_SetsExplicitPrice_AndSkipsPacks_AndRepeat()
    {
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        var tile = CartTestHelper.CreateProduct("p1", "Саморез", 100m);
        cart.AddItem(tile, 2);
        var lineId = cart.Items.Single().Id!;

        ReceiptSnapshotCartEditor.HasLinesWithoutPriceLevel(cart, "l1").Should().BeTrue();
        ReceiptSnapshotCartEditor.SetLinePriceLevel(cart, lineId, "l1", 80m).Should().BeTrue();

        var item = cart.Items.Single();
        item.UnitPrice.Should().Be(80m);
        item.LineTotal.Should().Be(160m);
        var json = CartDisplayHelper.EnumerateItems(cart.Root).Single();
        json.GetProperty("retail_price").GetDouble().Should().Be(100d, "обычная цена запомнена — снятие опта вернёт её");
        json.GetProperty("is_wholesale").GetBoolean().Should().BeTrue("явная цена: сервер не считает её устаревшей");
        QuickCheckoutBody.IsIntentionalPrice(json).Should().BeTrue();
        ReceiptSnapshotCartEditor.HasLinesWithoutPriceLevel(cart, "l1").Should().BeFalse();
        ReceiptSnapshotCartEditor.SetLinePriceLevel(cart, lineId, "l1", 70m).Should().BeFalse("повторно ту же метку не ставим");
        ReceiptSnapshotCartEditor.SetLinePriceLevel(cart, lineId, "l1", 0m).Should().BeFalse("нулевая цена не годится");
        cart.Dispose();
    }

    // ───────────── касса: цена по уровню и права ─────────────

    private sealed class Permissions(bool canEditPrice) : IPermissionService
    {
        public bool HasPermission(string permission) => permission != PosPermissions.EditPrice || canEditPrice;

        public void Demand(string permission)
        {
            if (!HasPermission(permission))
                throw new UnauthorizedAccessException(permission);
        }
    }

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

    private static (BasketPanelViewModel Vm, CartService Cart, PickClientWindows Windows, FakePrompts Prompts) Create(bool canEditPrice, double wholesale)
    {
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        var windows = new PickClientWindows();
        var prompts = new FakePrompts();
        var tile = CartTestHelper.CreateProduct("p1", "Саморез", 100m);
        tile.WholesalePrice = wholesale;
        var vm = new BasketPanelViewModel(cart, prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), windows, new FakeDialogs(),
            new InlineDispatcher(), catalogLookup: id => id == "p1" ? tile : null, permissions: new Permissions(canEditPrice));
        vm.AddProductFromCatalogWithOverride(tile, 2, 100);
        return (vm, cart, windows, prompts);
    }

    private static void HardwareSphere()
    {
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = "hardware";
    }

    /// <summary>Клиент вкладки чека (как после скана QR клиента): сессия закрытая, ставим через рефлексию.</summary>
    private static void SetReceiptClient(BasketPanelViewModel vm, ClientOption client)
    {
        var session = typeof(BasketPanelViewModel).GetMethod("GetActiveSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, null)!;
        session.GetType().GetProperty("Client")!.SetValue(session, client);
    }

    private static ClientOption Foreman() => new() { Id = "c1", DisplayName = "Прораб Бек · +996555000111" };

    [Fact]
    public async Task Basket_ClientWithPriceLevel_RepricesByQuote_AndReopensOnce()
    {
        HardwareSphere();
        RememberClient("c1", ",\"price_level\":\"l1\"");
        _fake.Json("POST", "/api/main/price-levels/quote/", 200, "{\"items\":[{\"product\":\"p1\",\"unit_price\":\"80.00\"}]}");
        await ApiAsync();
        var (vm, cart, windows, _) = Create(canEditPrice: false, wholesale: 0); // у кассира права на цену нет — уровень назначил владелец на сервере
        windows.PickOnFirst = Foreman();

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs == 2)).Should().BeTrue("окно оплаты открыто снова после пересчёта по уровню цен");
        CartDisplayHelper.UnitPrice(CartDisplayHelper.EnumerateItems(cart.Root).Single()).Should().Be(80d, "цена уровня с сервера (quote), а не 100");
        Requests(_fake, "POST", "/price-levels/quote/").Should().HaveCount(1);
        BodyOf(Requests(_fake, "POST", "/price-levels/quote/").Single())["client"]!.ToString().Should().Be("c1");
        windows.PaymentDialogs.Should().Be(2, "без зацикливания");
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public async Task Basket_PriceLevel_Offline_NoMemory_KeepsPrice_AndWarns()
    {
        HardwareSphere();
        RememberClient("c1", ",\"price_level\":\"l1\"");
        PriceLevelPricing.OnlineOverride = () => false;
        var (vm, cart, windows, prompts) = Create(canEditPrice: true, wholesale: 0);
        windows.PickOnFirst = Foreman();

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs == 2)).Should().BeTrue();
        CartDisplayHelper.UnitPrice(CartDisplayHelper.EnumerateItems(cart.Root).Single()).Should().Be(100d, "цены уровня нет — обычная цена");
        prompts.Errors.Should().NotBeEmpty("кассиру сказано, что цена уровня недоступна");
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public async Task Basket_PriceLevel_Offline_UsesLastReceivedPrices()
    {
        HardwareSphere();
        RememberClient("c1", ",\"price_level\":\"l1\"");
        PriceLevelCache.Remember("l1", new Dictionary<string, decimal> { ["p1"] = 82m });
        PriceLevelPricing.OnlineOverride = () => false;
        var (vm, cart, windows, prompts) = Create(canEditPrice: true, wholesale: 0);
        windows.PickOnFirst = Foreman();

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs == 2)).Should().BeTrue();
        CartDisplayHelper.UnitPrice(CartDisplayHelper.EnumerateItems(cart.Root).Single()).Should().Be(82d);
        prompts.Errors.Should().NotBeEmpty("предупреждение: цены без связи — последние полученные");
        vm.Dispose();
        cart.Dispose();
    }

    // ───────────── находка проверяющего: права на «цену прораба» ─────────────

    [Fact]
    public async Task LocalMark_CashierWithoutPriceRight_GetsNoWholesale()
    {
        HardwareSphere();
        ClientPriceGroups.SetForeman("c1", true);
        var (vm, cart, windows, _) = Create(canEditPrice: false, wholesale: 80);
        windows.PickOnFirst = Foreman();

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs >= 1)).Should().BeTrue();
        await Task.Delay(400);
        windows.PaymentDialogs.Should().Be(1, "без права на цену отметка «прораб» чек не пересчитывает и окно не переоткрывает");
        vm.Lines.Single().IsWholesale.Should().BeFalse();
        CartDisplayHelper.UnitPrice(CartDisplayHelper.EnumerateItems(cart.Root).Single()).Should().Be(100d);
        vm.IsWholesaleMode.Should().BeFalse("режим «Оптовый» без права не включается");
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public async Task LocalMark_CashierWithoutPriceRight_QrClientAlsoGetsNoWholesale()
    {
        HardwareSphere();
        ClientPriceGroups.SetForeman("c1", true);
        var (vm, cart, windows, _) = Create(canEditPrice: false, wholesale: 80);
        // клиент уже выбран в чеке (скан QR) — путь до открытия окна оплаты
        SetReceiptClient(vm, Foreman());

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs >= 1)).Should().BeTrue();
        await Task.Delay(400);
        vm.Lines.Single().IsWholesale.Should().BeFalse();
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public async Task LocalMark_Owner_KeepsWorking()
    {
        HardwareSphere();
        ClientPriceGroups.SetForeman("c1", true);
        var (vm, cart, windows, _) = Create(canEditPrice: true, wholesale: 80);
        windows.PickOnFirst = Foreman();

        vm.PayCommand.Execute(null);

        (await WaitAsync(() => windows.PaymentDialogs == 2)).Should().BeTrue("с правом на цену (владелец, админ) отметка работает как раньше");
        vm.Lines.Single().IsWholesale.Should().BeTrue();
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public void LocalMark_CannotBeSetWithoutPriceRight()
    {
        ClientPriceGroups.CanEdit(new Permissions(false)).Should().BeFalse();
        ClientPriceGroups.TrySetForeman("c1", true, new Permissions(false)).Should().BeFalse("переключатель в карточке клиента без права цены не меняет отметку");
        ClientPriceGroups.IsForeman("c1").Should().BeFalse();
        UserPreferences.Instance.ClientPriceGroupsJson.Should().BeNull("в общие настройки компании ничего не записано");

        ClientPriceGroups.TrySetForeman("c1", true, new Permissions(true)).Should().BeTrue();
        ClientPriceGroups.IsForeman("c1").Should().BeTrue();
        ClientPriceGroups.TrySetForeman("c1", false, new Permissions(false)).Should().BeFalse("и снять без права нельзя");
        ClientPriceGroups.IsForeman("c1").Should().BeTrue();
        ClientPriceGroups.CanEdit(null).Should().BeTrue("нет сервиса прав (автономный режим) — не ограничиваем, как остальные проверки цены");
    }
}
