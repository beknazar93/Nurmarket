using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-07 (решение владельца «в приёмке надо добавить функцию — при приёме принять упаковками»): режим «Принять упаковками» в «Закупки → Приём товара».
/// Упаковки × штук в упаковке → штуки, цена упаковки ÷ штук → цена за штуку (4 знака, как хранит сервер); в проведение, очередь «Неотправленное», накладную и этикетки
/// идут штуки и цена за штуку. Сервер — WireMock, очередь — настоящая во временной папке; боевые данные не трогаются.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingByPacksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-packs-" + Guid.NewGuid().ToString("N"));
    private readonly TimeSpan[] _oldDelays = BackgroundReceivingQueue.RetryDelays;
    private readonly TimeSpan _oldPoll = BackgroundReceivingQueue.ServerPoll;
    private readonly List<BackgroundReceivingQueue> _queues = new();

    public ReceivingByPacksTests()
    {
        Directory.CreateDirectory(_dir);
        BackgroundReceivingQueue.DataDirOverride = _dir;
        BackgroundReceivingQueue.CompanyIdOverride = "co-packs";
        BackgroundReceivingQueue.RetryDelays = [TimeSpan.FromMilliseconds(100)];
        BackgroundReceivingQueue.ServerPoll = TimeSpan.FromMilliseconds(200);
        ServerOutageMonitor.ResetForTests();
        ReceivingPackSizes.DataDirOverride = _dir;
        ReceivingPackSizes.Reset();
    }

    public void Dispose()
    {
        lock (_queues)
            foreach (var q in _queues)
                q.Shutdown();
        ServerOutageMonitor.ResetForTests();
        BackgroundReceivingQueue.DataDirOverride = null;
        BackgroundReceivingQueue.CompanyIdOverride = null;
        BackgroundReceivingQueue.RetryDelays = _oldDelays;
        BackgroundReceivingQueue.ServerPoll = _oldPoll;
        ReceivingPackSizes.DataDirOverride = null;
        ReceivingPackSizes.Reset();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------ обвязка

    private static ReceivingLineVm ExistingLine(string id = "pa", double cardPurchase = 0, double cardSale = 0)
    {
        var l = new ReceivingLineVm { OriginalSalePrice = cardSale, OriginalPurchasePrice = cardPurchase, ProductName = "Вода " + id, Barcode = "4600" + id };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = 1;
        l.PurchasePrice = cardPurchase;
        l.SalePrice = cardSale;
        return l;
    }

    /// <summary>Строка принята упаковками: 5 упаковок по 24 шт, упаковка 600 → 120 шт по 25.</summary>
    private static ReceivingLineVm PackLine(string id = "pa", double packs = 5, double perPack = 24, string packPrice = "600", double sale = 30)
    {
        var l = ExistingLine(id);
        l.SetPackMode(true);
        l.SetPackCount(packs);
        l.SetPiecesPerPack(perPack);
        l.SetPackPriceText(packPrice);
        l.SalePrice = sale;
        return l;
    }

    private static PurchaseReceivingService.Supplier Sup(string id) => new(id, "Поставщик " + id, null, null);

    private static void StubBase(FakeNurCrmServer s, string supplierId)
    {
        s.Server.ResetMappings();
        s.StubLogin();
        s.StubProfile();
        s.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        s.Json("POST", $"/api/main/suppliers/{supplierId}/receipt/", 200, "{\"id\":\"r-1\",\"total_amount\":\"3000.00\"}");
        s.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"30.00\",\"purchase_price\":\"25.00\",\"markup_percent\":\"20\"}");
        s.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"price\":\"30.00\",\"purchase_price\":\"25.00\"}");
    }

    private static async Task<(NurMarketApiClient Api, IDisposable Restore)> UseApiAsync(FakeNurCrmServer s)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return (api, new Restorer(() => PosApp.CatalogApi = previous));
    }

    private sealed class Restorer(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    private static List<WireMock.Logging.ILogEntry> Posts(FakeNurCrmServer s) =>
        s.Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/receipt/")).ToList();

    private static JsonObject BodyOf(WireMock.Logging.ILogEntry e) => (JsonObject)JsonNode.Parse(e.RequestMessage.Body!)!;

    private static async Task<bool> WaitAsync(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond())
                return true;
            await Task.Delay(50);
        }
        return cond();
    }

    // ------------------------------------------------------------------ расчёт упаковки → штуки, цена за штуку

    [Fact]
    public void Packs_times_pieces_give_pieces_and_pack_price_divided_gives_piece_price()
    {
        var l = PackLine();

        l.Quantity.Should().Be(120, "5 упаковок × 24 шт");
        l.PurchasePrice.Should().Be(25, "600 за упаковку ÷ 24");
        l.LineTotal.Should().Be(3000, "сумма строки = 5 × 600, ничего не потеряно");
        l.PackNote.Should().StartWith("5 × 24 = 120 шт");
        l.PackLossText.Should().BeEmpty("упаковка делится ровно");
        PurchaseReceivingService.Validate(new[] { l }).Should().BeEmpty("строка принята упаковками, всё указано");
    }

    [Fact]
    public void Order_of_typing_does_not_matter()
    {
        var a = ExistingLine("a");
        a.SetPackMode(true);
        a.SetPackPriceText("600"); // цена упаковки вписана ПЕРВОЙ, размера упаковки ещё нет
        a.PackNeedsSize.Should().BeTrue();
        a.SetPackCount(5);
        a.SetPiecesPerPack(24);

        a.Quantity.Should().Be(120);
        a.PurchasePrice.Should().Be(25, "размер упаковки появился — цена за штуку считается от введённой цены упаковки");
        a.PackNeedsSize.Should().BeFalse();

        a.SetPiecesPerPack(12); // поправили размер: упаковка по-прежнему 600
        a.Quantity.Should().Be(60);
        a.PurchasePrice.Should().Be(50);
        a.LineTotal.Should().Be(3000, "5 упаковок × 600 — сумма не поменялась от правки размера");
    }

    [Theory]
    [InlineData(100, 3, 33.3333)]
    [InlineData(100, 7, 14.2857)]
    [InlineData(1, 8, 0.125)]
    [InlineData(600, 24, 25)]
    [InlineData(0, 24, 0)]
    [InlineData(600, 0, 0)]
    public void Piece_price_is_rounded_to_four_decimals_like_the_server_stores(double pack, double perPack, double expected) =>
        ReceivingPacks.PiecePrice(pack, perPack).Should().Be(expected);

    [Fact]
    public void Pack_that_does_not_divide_evenly_keeps_four_decimals_and_warns_only_when_the_total_moves()
    {
        var small = PackLine("small", packs: 10, perPack: 3, packPrice: "100", sale: 40);
        small.PurchasePrice.Should().Be(33.3333);
        small.Quantity.Should().Be(30);
        small.LineTotal.Should().Be(1000, "30 × 33,3333 = 999,999 → 1000,00 — копейки сходятся");
        small.PackLossText.Should().BeEmpty("сумма сошлась до копейки — пугать нечем");
        PurchaseReceivingService.Validate(new[] { small }).Should().BeEmpty("4 знака — то, что принимает сервер");

        var big = PackLine("big", packs: 1000, perPack: 3, packPrice: "100", sale: 40);
        big.Quantity.Should().Be(3000);
        big.PurchasePrice.Should().Be(33.3333, "цену за штуку НЕ подгоняем под сумму молча");
        big.LineTotal.Should().Be(99999.90, "3000 × 33,3333 — то, что запишет сервер");
        big.PackLossText.Should().Contain("99").And.Contain("вместо", "сумма строки отличается от 1000 × 100 — кассиру об этом говорит красная пометка");
        ReceivingPacks.RoundingDelta(1000, 3, 100).Should().Be(0.10);
        PurchaseReceivingService.Validate(new[] { big }).Should().BeEmpty("предупреждение не блокирует: цена за штуку допустимой точности");
    }

    [Fact]
    public void Quantity_with_more_than_three_decimals_is_not_rounded_silently_but_blocked()
    {
        var l = PackLine("p", packs: 1.2345, perPack: 1, packPrice: "10");

        l.Quantity.Should().Be(1.2345, "не округляем молча");
        PurchaseReceivingService.Validate(new[] { l }).Should().Contain(p => p.Contains("3"), "больше 3 знаков — строку не проводят, как и в штуках");
    }

    [Fact]
    public void Missing_pack_size_blocks_posting_with_a_clear_message()
    {
        var l = ExistingLine("p");
        l.SetPackMode(true);
        l.SetPackCount(2);
        l.SetPackPriceText("100");
        l.Quantity.Should().BeGreaterThan(0, "строка остаётся активной — проверка перед проведением скажет, чего не хватает");

        PurchaseReceivingService.Validate(new[] { l }).Should().Contain(p => p.Contains("упаков"), "«укажите, сколько штук в упаковке»");
    }

    // ------------------------------------------------------------------ переключатель и внешние правки

    [Fact]
    public void Switching_on_turns_the_scan_default_one_into_one_pack_and_keeps_the_piece_price()
    {
        var l = ExistingLine("p", cardPurchase: 25, cardSale: 30);
        ReceivingPackSizes.Remember(PackLine("p")); // в прошлый раз упаковка была по 24

        l.SetPackMode(true, ReceivingPackSizes.Suggest(l));

        l.PiecesPerPack.Should().Be(24, "размер подставлен из памяти кассы");
        l.PackCount.Should().Be(1, "«1» от сканирования — одна упаковка");
        l.Quantity.Should().Be(24);
        l.PackPrice.Should().Be(600, "цена упаковки = цена за штуку 25 × 24");
        l.PurchasePrice.Should().Be(25, "цена за штуку при включении режима не меняется");
    }

    [Fact]
    public void Switching_off_keeps_pieces_and_piece_price_and_back_on_restores_the_pack_fields()
    {
        var l = PackLine();

        l.SetPackMode(false);
        l.PackMode.Should().BeFalse();
        l.Quantity.Should().Be(120);
        l.PurchasePrice.Should().Be(25);

        l.SetPackMode(true);
        l.PiecesPerPack.Should().Be(24, "размер упаковки помнится");
        l.PackCount.Should().Be(5, "120 шт ÷ 24");
        l.PackPrice.Should().Be(600);
    }

    [Fact]
    public void Outside_changes_of_pieces_and_piece_price_keep_the_pack_fields_consistent()
    {
        var l = PackLine();

        l.Quantity += 1; // повторный скан +1 шт
        l.Quantity.Should().Be(121, "штуки — главное значение");
        l.PackCount.Should().BeApproximately(121.0 / 24, 1e-6);

        l.Quantity = 0; // «Обнулить кол-во»
        l.PackCount.Should().Be(0);

        l.PurchasePrice = 30; // цена пришла снаружи (поставщик / заказ): за штуку
        l.PackPrice.Should().Be(720, "30 × 24");
    }

    [Fact]
    public void Packs_are_only_an_input_method_pieces_and_piece_price_are_what_everything_else_sees()
    {
        var l = PackLine(packs: 2, perPack: 10, packPrice: "55", sale: 8);

        l.Quantity.Should().Be(20);
        l.PurchasePrice.Should().Be(5.5);
        l.MarkupPercent.Should().BeApproximately(45.45, 0.01, "наценка — за штуку: 8 / 5,5");
        l.IsBelowCost.Should().BeFalse();
        ReceivingPricing.LineTotal(l.Quantity, l.SalePrice).Should().Be(160, "продажа на 20 шт × 8");
        l.ProfitText.Should().Contain("2,5", "прибыль с одной штуки 8 − 5,5");
    }

    // ------------------------------------------------------------------ запоминание размера упаковки (локально)

    [Fact]
    public void Pack_size_is_remembered_locally_by_product_and_barcode_and_survives_restart()
    {
        var l = PackLine("p1", perPack: 12);
        ReceivingPackSizes.Remember(l);
        File.Exists(Path.Combine(_dir, "receiving-pack-sizes.json")).Should().BeTrue();

        ReceivingPackSizes.Reset(); // как после перезапуска
        ReceivingPackSizes.Suggest(ExistingLine("p1")).Should().Be(12, "по id товара");
        var byCode = new ReceivingLineVm { Barcode = l.Barcode, ProductName = "Новый" };
        ReceivingPackSizes.Suggest(byCode).Should().Be(12, "новая строка с тем же штрихкодом — по штрихкоду");
        ReceivingPackSizes.Suggest(ExistingLine("other")).Should().Be(0);

        ReceivingPackSizes.Remember(ExistingLine("plain")); // не упаковками — не запоминается
        ReceivingPackSizes.Suggest(ExistingLine("plain")).Should().Be(0);
    }

    // ------------------------------------------------------------------ в очереди — уже штуки и цена за штуку

    [Fact]
    public void Queue_job_line_stores_pieces_and_piece_price_and_survives_serialization()
    {
        var line = PackLine();
        var stored = JobLine.From(line);

        stored.Quantity.Should().Be(120);
        stored.PurchasePrice.Should().Be(25);
        stored.LineTotal.Should().Be(3000);

        var json = JsonSerializer.Serialize(stored);
        json.Should().NotContain("Pack", "в очереди нет режима упаковок — только штуки и цена за штуку");
        var back = JsonSerializer.Deserialize<JobLine>(json)!.ToVm();
        back.Quantity.Should().Be(120);
        back.PurchasePrice.Should().Be(25);
        back.SalePrice.Should().Be(30);
        back.PackMode.Should().BeFalse("после возврата из очереди строка в штуках");
    }

    // ------------------------------------------------------------------ проведение: штуки, цена за штуку, цена продажи за штуку

    [Fact]
    public async Task Receipt_with_supplier_carries_pieces_piece_price_and_the_entered_sale_price_per_piece()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var line = PackLine(sale: 30);

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, Sup("s1"), true, "тест");

        result.PostedLines.Should().HaveCount(1);
        var posts = Posts(server);
        posts.Should().HaveCount(1);
        var item = BodyOf(posts[0])["items"]!.AsArray()[0]!.AsObject();
        item["qty"]!.GetValue<double>().Should().Be(120, "на склад уходят штуки, а не упаковки");
        item["purchase_price"]!.GetValue<double>().Should().Be(25, "цена за штуку, а не за упаковку");
        item["sale_price"]!.GetValue<double>().Should().Be(30, "цена продажи — за штуку, как введена (баг «40/60 → 48» не вернулся)");
        item["markup_percent"]!.GetValue<double>().Should().Be(20, "наценка 30 / 25 за штуку");
    }

    [Fact]
    public async Task Receipt_without_supplier_writes_pieces_and_piece_price_into_the_product()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var line = PackLine(sale: 30);

        await PurchaseReceivingService.Instance.PostAsync(new[] { line }, null, false, "тест");

        Posts(server).Should().BeEmpty("без поставщика документа прихода нет");
        var patches = server.Server.LogEntries.Where(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Path.Contains("/products/pa/")).Select(BodyOf).ToList();
        patches.Should().NotBeEmpty();
        patches.Should().Contain(b => b["quantity"] != null && b["quantity"]!.ToString() == "130", "остаток 10 + 120 шт");
        patches.Should().Contain(b => b["purchase_price"] != null && b["purchase_price"]!.GetValue<double>() == 25, "закупка — за штуку");
    }

    [Fact]
    public async Task Queue_posts_a_pack_line_as_pieces_and_a_piece_price_and_the_stored_job_is_in_pieces()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1");
        var (api, restore) = await UseApiAsync(server);
        using var guard = restore;
        var q = new BackgroundReceivingQueue();
        lock (_queues)
            _queues.Add(q);
        q.Resume(api);

        var id = q.Enqueue(new ReceivingJobRequest(ReceivingJobSource.Purchases, new[] { PackLine() }, Sup("s1"), PaidNow: true, "тест"));

        (await WaitAsync(() => q.Get(id)?.Status == ReceivingJobStatus.Done)).Should().BeTrue("приход отправлен из очереди");
        var item = BodyOf(Posts(server).Single())["items"]!.AsArray()[0]!.AsObject();
        item["qty"]!.GetValue<double>().Should().Be(120);
        item["purchase_price"]!.GetValue<double>().Should().Be(25);
        q.Get(id)!.Total.Should().Be(3000, "итог накладной — от пересчитанных штук");
        File.ReadAllText(Path.Combine(_dir, "queue.json")).Should().NotContain("PackMode", "на диск очереди режим упаковок не пишется — только штуки и цена за штуку");
    }

    [Fact]
    public async Task Offline_pack_receipt_waits_on_disk_in_pieces_and_is_sent_once_after_restart()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1");
        var (api, restore) = await UseApiAsync(server);
        using var guard = restore;

        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true); // интернета нет
        var q1 = new BackgroundReceivingQueue();
        lock (_queues)
            _queues.Add(q1);
        q1.Resume(api);
        var id = q1.Enqueue(new ReceivingJobRequest(ReceivingJobSource.Purchases, new[] { PackLine() }, Sup("s1"), PaidNow: true, "тест"));
        await Task.Delay(500);
        Posts(server).Should().BeEmpty("связи нет — приход ждёт на диске");
        q1.Shutdown();

        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "queue.json")))!.ToJsonString();
        stored.Should().NotContain("PackMode");

        var q2 = new BackgroundReceivingQueue(); // перезапуск
        lock (_queues)
            _queues.Add(q2);
        var restored = q2.Get(id)!.Lines.Single();
        restored.Quantity.Should().Be(120, "после перезапуска в очереди штуки");
        restored.PurchasePrice.Should().Be(25, "и цена за штуку");

        ServerOutageMonitor.ResetForTests(); // связь вернулась
        q2.Resume(api);
        (await WaitAsync(() => q2.Get(id)?.Status == ReceivingJobStatus.Done)).Should().BeTrue();
        var posts = Posts(server);
        posts.Should().HaveCount(1, "приход ушёл один раз");
        BodyOf(posts[0])["items"]!.AsArray()[0]!["qty"]!.GetValue<double>().Should().Be(120);
    }
}

/// <summary>2026-11-07: автонаценка и цена продажи в режиме упаковок — за штуку (отдельный класс: общие настройки наценки).</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class ReceivingByPacksMarkupTests
{
    private static T WithMarkup<T>(double percent, Func<T> body)
    {
        var previous = ReceivingMarkupSettingsStore.Current.Clone();
        try
        {
            ReceivingMarkupSettingsStore.Override(new ReceivingMarkupSettings { AutoEnabled = true, DefaultPercent = percent, Rounding = SaleRounding.WholeUp });
            return body();
        }
        finally
        {
            ReceivingMarkupSettingsStore.Override(previous);
        }
    }

    [Fact]
    public void Auto_sale_price_follows_the_piece_price_and_manual_price_is_kept()
    {
        WithMarkup(20, () =>
        {
            var line = new ReceivingLineVm { ProductName = "Новый", Barcode = "4601" };
            line.Source = ReceivingSource.Unknown;
            line.Quantity = 1;
            line.EnableAutoSale();
            line.SetPackMode(true);
            line.SetPackCount(5);
            line.SetPiecesPerPack(24);

            line.SetPackPriceText("600"); // 25 за штуку
            line.PurchasePrice.Should().Be(25);
            line.SalePrice.Should().Be(30, "25 × 1,2 — наценка считается от цены за штуку, а не за упаковку (600 × 1,2 дало бы 720)");

            line.SetSaleText("40"); // продажу вписали руками
            line.SetPackPriceText("720"); // 30 за штуку
            line.PurchasePrice.Should().Be(30);
            line.SalePrice.Should().Be(40, "ручная цена продажи не пересчитывается");
            return 0;
        });
    }
}

/// <summary>2026-11-07: товар с «поштучной продажей из упаковки» (PieceOption) склад ведёт упаковками — режим «Принять упаковками» ему не предлагается.</summary>
[Collection("Wave5Database")]
public sealed class ReceivingByPacksCardTests : IDisposable
{
    private readonly List<CatalogProductTileVm> _saved = CatalogCacheService.Products.ToList();

    public ReceivingByPacksCardTests() => CatalogCacheService.Products.Clear();

    public void Dispose()
    {
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_saved);
    }

    [Fact]
    public void Product_with_piece_option_reports_its_pack_size_and_plain_products_do_not()
    {
        var withOption = CatalogTestHelper.CreateTile("po1", "Пачка", "46001", 5);
        withOption.PieceOption = new ProductPackageOption { Id = "pk1", Name = "упаковка", QuantityInPackage = 10, PieceUnitPrice = 3 };
        var plain = CatalogTestHelper.CreateTile("pl1", "Бутылка", "46002", 5);
        CatalogCacheService.Products.AddRange(new[] { withOption, plain });

        ReceivingPacks.PieceOptionSize(new ReceivingLineVm { ProductId = "po1" }).Should().Be(10);
        ReceivingPacks.PieceOptionSize(new ReceivingLineVm { ProductId = "pl1" }).Should().Be(0);
        ReceivingPacks.PieceOptionSize(new ReceivingLineVm { ProductId = null }).Should().Be(0, "новый товар — карточки нет");
    }
}
