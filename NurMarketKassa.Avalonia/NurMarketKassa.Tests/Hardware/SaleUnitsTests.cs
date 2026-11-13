using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;
using NurMarketKassa.ViewModels.Main;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-13 (ответ Умара 13.11, п. 4, «Строймаркет»): единицы продажи sale_units (упаковка 1,44 м²). Разбор из карточки; строка чека в упаковках; на сервер уходит sale_unit и
/// количество в упаковках; остаток локально = −qty × factor (одна функция на списание, резерв, возврат и офлайн-очередь); старый сервер без sale_units — как раньше; скан штрихкода упаковки.</summary>
[Collection("ServerFeatures")]
public sealed class SaleUnitsTests : IDisposable
{
    private const string TileId = "11111111-1111-1111-1111-111111111111";
    private readonly string _file = Path.Combine(Path.GetTempPath(), "nm-saleunits-" + Guid.NewGuid().ToString("N")[..8] + ".json");
    private readonly string _sphere = UserPreferences.Instance.MarketSphere;
    private readonly string? _detail = UserPreferences.Instance.MarketSphereDetail;
    private readonly string? _settings = UserPreferences.Instance.SphereSettingsJson;
    private readonly FakeNurCrmServer _fake = new();

    public SaleUnitsTests()
    {
        ProductSaleUnits.FileOverride = _file;
        ProductSaleUnits.ResetForTests();
    }

    public void Dispose()
    {
        ProductSaleUnits.FileOverride = null;
        ProductSaleUnits.ResetForTests();
        UserPreferences.Instance.MarketSphere = _sphere;
        UserPreferences.Instance.MarketSphereDetail = _detail;
        UserPreferences.Instance.SphereSettingsJson = _settings;
        _fake.Dispose();
        try { File.Delete(_file); } catch { /* временный файл */ }
    }

    private static JsonElement Card(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string TileCard =
        "{\"id\":\"" + TileId + "\",\"name\":\"Плитка\",\"price\":\"520.00\",\"unit\":\"м²\",\"quantity\":\"100\"," +
        "\"sale_units\":[{\"name\":\"Упаковка\",\"unit_code\":\"pack\",\"factor\":1.44,\"barcode\":\"4600000001440\",\"price\":\"749.00\",\"wholesale_price\":\"700.00\"}]}";

    private static CatalogProductTileVm Tile()
    {
        var tile = new CatalogProductTileVm(TileId, "Плитка", "520.00 сом", false) { Unit = "м²", Quantity = 100, Barcode = "4600000000520", PurchasePrice = 400 };
        return tile;
    }

    private static CartService NewCart()
    {
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        return cart;
    }

    // ───────────── разбор ─────────────

    [Fact]
    public void Parse_ReadsUnit_Factor_Prices_Barcode()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));

        var unit = ProductSaleUnits.For(TileId).Single();
        unit.Key.Should().Be("pack");
        unit.Factor.Should().Be(1.44m);
        unit.Price.Should().Be(749m);
        unit.WholesalePrice.Should().Be(700m);
        ProductSaleUnits.FindByBarcode("4600000001440")!.Value.ProductId.Should().Be(TileId);
        ProductSaleUnits.Find(TileId, "PACK").Should().NotBeNull("ключ — без учёта регистра");
    }

    [Fact]
    public void Parse_IdBecomesKey_WhenServerSendsIt()
    {
        ProductSaleUnits.Remember(TileId, Card("{\"sale_units\":[{\"id\":\"su-7\",\"name\":\"Упаковка\",\"unit_code\":\"pack\",\"factor\":\"2.5\"}]}"));
        var unit = ProductSaleUnits.For(TileId).Single();
        unit.Key.Should().Be("su-7");
        ProductSaleUnits.Find(TileId, "pack").Should().NotBeNull("по коду единицу тоже находим");
    }

    [Fact]
    public void Parse_OldServerWithoutSaleUnits_ChangesNothing()
    {
        ProductSaleUnits.Remember(TileId, Card("{\"id\":\"" + TileId + "\",\"name\":\"Плитка\",\"price\":\"520.00\"}"));
        ProductSaleUnits.For(TileId).Should().BeEmpty();

        ProductSaleUnits.Remember(TileId, Card(TileCard));
        ProductSaleUnits.Remember(TileId, Card("{\"id\":\"" + TileId + "\",\"price\":\"521.00\"}")); // сокращённая карточка без поля
        ProductSaleUnits.For(TileId).Should().HaveCount(1, "нет поля — единицы не трогаем");
        ProductSaleUnits.Remember(TileId, Card("{\"sale_units\":[]}"));
        ProductSaleUnits.For(TileId).Should().BeEmpty("пустой список — сервер убрал упаковки");
        ProductSaleUnits.FindByBarcode("4600000001440").Should().BeNull();
    }

    [Theory]
    [InlineData("{\"sale_units\":[{\"name\":\"Упаковка\",\"unit_code\":\"pack\",\"factor\":0}]}")]
    [InlineData("{\"sale_units\":[{\"name\":\"Упаковка\",\"unit_code\":\"pack\",\"factor\":-2}]}")]
    [InlineData("{\"sale_units\":[{\"name\":\"Упаковка\",\"unit_code\":\"pack\"}]}")]
    [InlineData("{\"sale_units\":[{\"name\":\"Без кода\",\"factor\":2}]}")]
    [InlineData("{\"sale_units\":[{\"name\":\"Упаковка\",\"unit_code\":\"pack\",\"factor\":99999999}]}")]
    public void Parse_BrokenUnits_AreSkipped(string json)
    {
        ProductSaleUnits.Remember(TileId, Card(json));
        ProductSaleUnits.For(TileId).Should().BeEmpty("единица без множителя, кода или с абсурдным множителем не запоминается");
    }

    [Fact]
    public void Parse_CardMapperRemembersUnits_AndFileSurvivesRestart()
    {
        ProductCatalogMapper.TryTile(Card(TileCard), "https://app.nurcrm.kg/");
        ProductSaleUnits.For(TileId).Should().HaveCount(1, "products/list, products/changes и карточка идут через один разбор");

        ProductSaleUnits.SaveIfDirty();
        ProductSaleUnits.ResetForTests(); // как перезапуск кассы
        ProductSaleUnits.For(TileId).Single().Factor.Should().Be(1.44m);
        ProductSaleUnits.FindByBarcode("4600000001440").Should().NotBeNull();
    }

    // ───────────── строка чека и остаток ─────────────

    [Fact]
    public void Cart_PackLine_PriceInPacks_StockQtyTimesFactor()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var tile = Tile();
        var cart = NewCart();

        cart.AddSaleUnitItem(tile, "pack", 2, wholesale: false);

        var item = cart.Items.Single();
        item.Quantity.Should().Be(2);
        item.UnitPrice.Should().Be(749m);
        item.LineTotal.Should().Be(1498m);
        item.SalePackageId.Should().Be("sale_unit:pack", "в проекции строка видна как «условная упаковка»");
        var line = CartDisplayHelper.EnumerateItems(cart.Root).Single();
        ProductSaleUnits.LineKey(line).Should().Be("pack");
        CartDisplayHelper.SalePackageId(line).Should().BeNull("sale_package_id серверу не уходит — только sale_unit");
        // Остаток после продажи = 100 − 2 × 1,44 = 97,12.
        var sold = CartDisplayHelper.LineQuantityInStockUnits(line, tile);
        sold.Should().BeApproximately(2.88, 1e-9);
        (tile.Quantity - sold).Should().BeApproximately(97.12, 1e-9);
        cart.Dispose();
    }

    [Fact]
    public void Cart_SecondScanSameUnit_MergesPacks_BaseLineStaysSeparate()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var tile = Tile();
        var cart = NewCart();

        cart.AddSaleUnitItem(tile, "pack", 1, false);
        cart.AddSaleUnitItem(tile, "pack", 2, false);
        cart.Items.Should().ContainSingle().Which.Quantity.Should().Be(3);

        cart.AddItem(tile, 1); // метры основной единицей — отдельная строка
        cart.Items.Should().HaveCount(2);
        cart.Items.Count(i => i.SalePackageId is null).Should().Be(1);

        var total = cart.Items.Sum(i => CartDisplayHelper.LineQuantityInStockUnits(
            CartDisplayHelper.EnumerateItems(cart.Root).First(l => CartDisplayHelper.TryItemId(l) == i.Id), tile));
        total.Should().BeApproximately(3 * 1.44 + 1, 1e-9, "3 упаковки по 1,44 м² + 1 м²");
        cart.Dispose();
    }

    [Fact]
    public void Cart_WholesalePack_UsesPackWholesalePrice_AndCanReturnToRetail()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var cart = NewCart();

        cart.AddSaleUnitItem(Tile(), "pack", 1, wholesale: true);

        var line = CartDisplayHelper.EnumerateItems(cart.Root).Single();
        CartDisplayHelper.UnitPrice(line).Should().Be(700d);
        line.GetProperty("is_wholesale").GetBoolean().Should().BeTrue();
        line.GetProperty("retail_price").GetDouble().Should().Be(749d);
        cart.Dispose();
    }

    [Fact]
    public void Cart_PackPriceMissingOnServer_FallsBackToBasePriceTimesFactor()
    {
        ProductSaleUnits.Remember(TileId, Card("{\"sale_units\":[{\"name\":\"Упаковка\",\"unit_code\":\"pack\",\"factor\":2}]}"));
        var cart = NewCart();

        cart.AddSaleUnitItem(Tile(), "pack", 1, false);

        cart.Items.Single().UnitPrice.Should().Be(1040m, "цена упаковки не задана — цена м² × множитель, как посчитает сервер");
        cart.Dispose();
    }

    [Fact]
    public void Cart_UnknownUnit_Throws()
    {
        var cart = NewCart();
        var act = () => cart.AddSaleUnitItem(Tile(), "pack", 1, false);
        act.Should().Throw<ArgumentException>();
        cart.Dispose();
    }

    [Fact]
    public void Stock_ServerSaleLine_ReturnRestoresQtyTimesFactor()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var tile = Tile();
        // Строка продажи в ответе сервера: единица как объект с множителем.
        var withObject = Card("{\"product_id\":\"" + TileId + "\",\"quantity\":\"2\",\"sale_unit\":{\"unit_code\":\"pack\",\"factor\":\"1.44\"}}");
        CartDisplayHelper.LineQuantityInStockUnits(withObject, tile).Should().BeApproximately(2.88, 1e-9);
        // Только ключ единицы — множитель из карточки.
        var keyOnly = Card("{\"product_id\":\"" + TileId + "\",\"quantity\":\"1\",\"sale_unit\":\"pack\"}");
        CartDisplayHelper.LineQuantityInStockUnits(keyOnly, tile).Should().BeApproximately(1.44, 1e-9, "возврат одной упаковки возвращает 1,44 м²");
        // Обычная строка — как раньше.
        var plain = Card("{\"product_id\":\"" + TileId + "\",\"quantity\":\"2\"}");
        CartDisplayHelper.LineQuantityInStockUnits(plain, tile).Should().Be(2d);
    }

    [Fact]
    public void Quantity_PackLine_NeverGetsFractionalMeterRule()
    {
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = "hardware";
        QuantityRules.ServerAcceptsFractional = true;
        try
        {
            ProductSaleUnits.Remember(TileId, Card(TileCard));
            var cart = NewCart();
            cart.AddSaleUnitItem(Tile(), "pack", 2, false);
            var line = CartDisplayHelper.EnumerateItems(cart.Root).Single();
            QuantityRules.ForLine(line).Should().BeNull("упаковки — целые, шага метра у них нет");
            cart.Items.Single().SalePackageId.Should().StartWith(ProductSaleUnits.LinePrefix);
            cart.Dispose();
        }
        finally
        {
            QuantityRules.ServerAcceptsFractional = false;
        }
    }

    // ───────────── что уходит на сервер ─────────────

    private async Task<JsonObject> SentAddItemAsync(string snapshot)
    {
        _fake.Json("POST", "/api/main/pos/sales/*/add-item/", 200, "{}");
        var api = await LoggedInAsync(_fake);
        await StagingCartService.PushItemsFromSnapshotAsync(new SalesApiService(api), "cart-1", snapshot, default);
        return BodyOf(Requests(_fake, "POST", "/add-item/").Last());
    }

    [Fact]
    public async Task Send_PackLine_SendsSaleUnitAndPackQuantity_NoUnitPrice()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var cart = NewCart();
        cart.AddSaleUnitItem(Tile(), "pack", 2, false);

        var body = await SentAddItemAsync(cart.Root.GetRawText());

        body["product_id"]!.ToString().Should().Be(TileId);
        body["quantity"]!.ToString().Should().Be("2", "количество — в упаковках; сервер сам спишет 2 × 1,44");
        body["sale_unit"]!.ToString().Should().Be("pack");
        body.ContainsKey("unit_price").Should().BeFalse("цену упаковки ставит сервер");
        body.ContainsKey("sale_package_id").Should().BeFalse();
        cart.Dispose();
    }

    [Fact]
    public async Task Send_WholesalePack_SendsPackWholesalePrice()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var cart = NewCart();
        cart.AddSaleUnitItem(Tile(), "pack", 1, true);

        var body = await SentAddItemAsync(cart.Root.GetRawText());

        body["sale_unit"]!.ToString().Should().Be("pack");
        body["unit_price"]!.ToString().Should().Be("700.00");
        cart.Dispose();
    }

    [Fact]
    public async Task Send_OfflineQueueSnapshot_SendsTheSameBody()
    {
        // Офлайн-очередь хранит снимок чека (как есть) и при повторе проходит ту же выгрузку (SyncService → PushItemsFromSnapshotAsync).
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var cart = NewCart();
        cart.AddSaleUnitItem(Tile(), "pack", 3, false);
        var queued = cart.Root.GetRawText();
        ProductSaleUnits.ResetForTests(); // касса перезапущена — в памяти упаковок нет, всё берётся из строки

        var body = await SentAddItemAsync(queued);

        body["sale_unit"]!.ToString().Should().Be("pack");
        body["quantity"]!.ToString().Should().Be("3");
        CartDisplayHelper.LineQuantityInStockUnits(CartDisplayHelper.EnumerateItems(cart.Root).Single(), Tile())
            .Should().BeApproximately(3 * 1.44, 1e-9, "множитель записан в самой строке — остаток офлайн считается верно и без каталога");
        cart.Dispose();
    }

    [Fact]
    public async Task Send_OldServerLine_WithoutSaleUnit_IsSentAsBefore()
    {
        var cart = NewCart();
        cart.AddItem(Tile(), 2);

        var body = await SentAddItemAsync(cart.Root.GetRawText());

        body.ContainsKey("sale_unit").Should().BeFalse();
        body["quantity"]!.ToString().Should().Be("2");
        body["unit_price"]!.ToString().Should().Be("520.00");
        cart.Dispose();
    }

    [Fact]
    public void QuickCheckout_PackLine_IsNotQuick_ThenUsualPath()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var cart = NewCart();
        cart.AddSaleUnitItem(Tile(), "pack", 1, false);

        var body = QuickCheckoutBody.TryBuild(cart.Root.GetRawText(), "cash", "1000", null, null, Guid.NewGuid().ToString(), null, false, null, false, out var reason);

        body.Should().BeNull("быстрый путь sale_unit не знает — чек идёт обычным путём");
        reason.Should().Contain("упаковка");
        cart.Dispose();
    }

    // ───────────── скан штрихкода упаковки ─────────────

    [Fact]
    public void PackScanRules_ProductWithServerUnits_NoLocalBoxMultiplier()
    {
        UserPreferences.Instance.MarketSphere = "grocery";
        UserPreferences.Instance.MarketSphereDetail = "hardware";
        UserPreferences.Instance.SphereSettingsJson = "{\"hardware\":{\"size\":\"shop\",\"packScan\":true}}";
        var tile = new CatalogProductTileVm(TileId, "Саморез", "1.50 сом", false)
        {
            Unit = "шт",
            Barcode = "4600000000011",
            AlternateBarcodeVariants = new List<AlternateBarcodeVariant> { new() { Barcode = "4600000000200", Quantity = 200 } },
        };
        PackScanRules.Find(tile, "4600000000200", out var n).Should().NotBeNull("пока серверных упаковок нет — коробка ×200 как раньше");
        n.Should().Be(200);

        ProductSaleUnits.Remember(TileId, Card(TileCard));
        PackScanRules.Find(tile, "4600000000200", out _).Should().BeNull("есть sale_units — остаток считает сервер, местное «коробка = N» не включается: нет двойного пересчёта");
    }

    [Fact]
    public async Task Scan_PackBarcode_AddsPackLine_AtPackPrice()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var tile = Tile();
        var cart = NewCart();
        var vm = new BasketPanelViewModel(cart, new FakePrompts(), new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), new FakeWindows(), new FakeDialogs(),
            new InlineDispatcher(), catalogLookup: id => id == TileId || id == tile.Barcode ? tile : null, stockLimitLookup: (_, _) => tile.Quantity);

        vm.EnqueueBarcode("4600000001440");

        await WaitAsync(() => vm.Lines.Count == 1);
        var line = vm.Lines.Single();
        line.Quantity.Should().Be(1);
        line.UnitPrice.Should().Be(749d, "цена упаковки, а не 520 за м²");
        line.SalePackageId.Should().Be("sale_unit:pack");
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public async Task Scan_PackBarcode_NotEnoughStock_AddsNothing()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var tile = Tile();
        tile.Quantity = 1; // 1 м² < 1,44 м² упаковки
        var cart = NewCart();
        var prompts = new FakePrompts();
        var vm = new BasketPanelViewModel(cart, prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), new FakeWindows(), new FakeDialogs(),
            new InlineDispatcher(), catalogLookup: id => id == TileId ? tile : null, stockLimitLookup: (_, _) => tile.Quantity);

        vm.EnqueueBarcode("4600000001440");

        await WaitAsync(() => prompts.Errors.Count > 0);
        vm.Lines.Should().BeEmpty();
        prompts.Errors.Should().ContainSingle();
        vm.Dispose();
        cart.Dispose();
    }

    [Fact]
    public async Task Scan_BaseBarcode_StillAddsMeters()
    {
        ProductSaleUnits.Remember(TileId, Card(TileCard));
        var tile = Tile();
        var cart = NewCart();
        var vm = new BasketPanelViewModel(cart, new FakePrompts(), new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), new FakeWindows(), new FakeDialogs(),
            new InlineDispatcher(), catalogLookup: id => id == tile.Barcode || id == TileId ? tile : null, stockLimitLookup: (_, _) => tile.Quantity);

        vm.EnqueueBarcode(tile.Barcode!);

        await WaitAsync(() => vm.Lines.Count == 1);
        vm.Lines.Single().UnitPrice.Should().Be(520d);
        vm.Lines.Single().SalePackageId.Should().BeNull();
        vm.Dispose();
        cart.Dispose();
    }

    private static async Task WaitAsync(Func<bool> condition, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until && !condition())
            await Task.Delay(50);
    }
}
