using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-11 (Д-4, живая проверка «Склада» на NBS): приход упаковками 2 × 5 = 10 шт из «Склад → Приёмка» — в «Склад → Товары» остаток 20, а в карточке, кассе, ревизии 10.
/// Причина: очередь фоновых приходов после ответа сервера прибавляет принятое к ТЕКУЩЕМУ остатку плитки, а лента (опрос ~3 с) успевала положить на плитку серверный остаток,
/// уже включающий приход; потом защита «трогали недавно» 90 с не пускала верное число. Здесь поддельный сервер (WireMock) в момент записи прихода сразу «прогоняет ленту»
/// (CatalogFeedSync.Apply с настоящей защитой FeedStockGuard) — как живая лента в 22:55:31. Остаток в базе и на плитке должен быть «было + принято» один раз во всех путях прихода.
/// База — во временной папке test-safe.ps1, очередь — во временной папке теста; реальный сервер не трогаем.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class ReceivingStockCountedOnceTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-d4-" + Guid.NewGuid().ToString("N"));
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private readonly List<CatalogProductTileVm> _savedProducts = CatalogCacheService.Products.ToList();
    private readonly TimeSpan[] _oldDelays = BackgroundReceivingQueue.RetryDelays;
    private readonly TimeSpan _oldPoll = BackgroundReceivingQueue.ServerPoll;
    private readonly Func<Task>? _oldRefresher = BackgroundReceivingQueue.CatalogRefresher;
    private readonly List<BackgroundReceivingQueue> _queues = new();
    // 2026-11-12 (Л-6): сроки перечитывания остатка — поля StockSyncService для тестов; возвращаются в Dispose.
    private readonly TimeSpan _oldRereadMax = StockSyncService.RereadMaxAge, _oldRereadTick = StockSyncService.RereadTick, _oldRereadRetry = StockSyncService.RereadRetryDelay;
    private readonly object _holdOwner = new();
    // Свой id на каждый тест: перечитывание остатка (StockSyncService, через ~95 с) не должно попасть в товары соседних тестов.
    private readonly string _id = "d4-" + Guid.NewGuid().ToString("N")[..8];

    public ReceivingStockCountedOnceTests()
    {
        Directory.CreateDirectory(_dir);
        BackgroundReceivingQueue.DataDirOverride = _dir;
        BackgroundReceivingQueue.CompanyIdOverride = "co-d4";
        BackgroundReceivingQueue.RetryDelays = [TimeSpan.FromMilliseconds(100)];
        BackgroundReceivingQueue.ServerPoll = TimeSpan.FromMilliseconds(200);
        BackgroundReceivingQueue.CatalogRefresher = () => Task.CompletedTask; // полную загрузку каталога после прихода не делаем
        ServerOutageMonitor.ResetForTests();
        ServerClock.ResetForTests();
        ServerPriceContract.ResetForTests();
        ReceivingPackSizes.DataDirOverride = _dir;
        ReceivingPackSizes.Reset();
        _repo.EnsureSchema();
        _repo.ClearAll();
        NewProductCardBackfill.AutoRun = false;
        NewProductCardBackfill.ResetForTests();
        ProductCodeBackfill.ResetForTests();
    }

    public void Dispose()
    {
        lock (_queues)
            foreach (var q in _queues)
                q.Shutdown();
        StockSyncService.SetReceiptHolds(_holdOwner, Array.Empty<string>());
        StockSyncService.RereadMaxAge = _oldRereadMax;
        StockSyncService.RereadTick = _oldRereadTick;
        StockSyncService.RereadRetryDelay = _oldRereadRetry;
        ServerOutageMonitor.ResetForTests();
        ServerClock.ResetForTests();
        BackgroundReceivingQueue.DataDirOverride = null;
        BackgroundReceivingQueue.CompanyIdOverride = null;
        BackgroundReceivingQueue.RetryDelays = _oldDelays;
        BackgroundReceivingQueue.ServerPoll = _oldPoll;
        BackgroundReceivingQueue.CatalogRefresher = _oldRefresher;
        ReceivingPackSizes.DataDirOverride = null;
        ReceivingPackSizes.Reset();
        NewProductCardBackfill.AutoRun = true;
        NewProductCardBackfill.ResetForTests();
        _repo.ClearAll();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_savedProducts);
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------ поддельный сервер с состоянием товара

    private sealed class Srv : IDisposable
    {
        public readonly FakeNurCrmServer Fake = new();
        private readonly object _gate = new();
        public readonly string Id;
        public double Qty, Price = 80, Purchase = 60, PackSize;
        /// <summary>Сразу после записи остатка на сервере (PATCH с количеством / документ прихода) — «лента» кассы.</summary>
        public Action? OnCommit;
        /// <summary>Сколько первых PATCH с количеством записать, но ответить 500 (ответ потерян).</summary>
        public int LoseAnswers;
        /// <summary>2026-11-12 (Л-23): действие «во время запроса карточки» — ответ уже построен (остаток как был), а на этой кассе тем временем произошла продажа.</summary>
        public Action? OnGet;

        public Srv(string id, double qty)
        {
            Id = id;
            Qty = qty;
            var s = Fake.Server;
            s.ResetMappings();
            Fake.StubLogin();
            Fake.StubProfile();
            Fake.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
            // Приоритет 10: тест может перекрыть ответ своей заглушкой с приоритетом 1 (меньше — главнее).
            s.Given(Request.Create().WithPath($"/api/main/products/{id}/").UsingMethod("GET")).AtPriority(10).RespondWith(Response.Create().WithCallback(_ =>
            {
                var card = Card(); // ответ построен до действия кассы
                OnGet?.Invoke();
                return Reply(200, card);
            }));
            s.Given(Request.Create().WithPath($"/api/main/products/{id}/").UsingMethod("PATCH")).AtPriority(10).RespondWith(Response.Create().WithCallback(req => Patch(req.Body)));
            s.Given(Request.Create().WithPath($"/api/main/products/{id}/convert-to-packages/").UsingMethod("POST")).AtPriority(10).RespondWith(Response.Create().WithCallback(req => Convert(req.Body)));
            s.Given(Request.Create().WithPath("/api/main/suppliers/s1/receipt/").UsingMethod("POST")).AtPriority(10).RespondWith(Response.Create().WithCallback(req => Receipt(req.Body)));
        }

        private static WireMock.ResponseMessage Reply(int status, string json) => new()
        {
            StatusCode = status,
            BodyData = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = json },
            Headers = new Dictionary<string, WireMock.Types.WireMockList<string>> { ["Content-Type"] = new("application/json") },
        };

        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static double? D(JsonNode? n) => n is null ? null : double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        public string Card()
        {
            lock (_gate)
                return "{\"id\":\"" + Id + "\",\"name\":\"TEST-D4\",\"barcode\":\"4600000004041\",\"quantity\":\"" + N(Qty) + "\",\"price\":\"" + N(Price) + "\",\"purchase_price\":\"" + N(Purchase)
                       + "\",\"markup_percent\":\"33.33\",\"price_changed\":false" + (PackSize > 0 ? ",\"packages\":[{\"id\":\"k1\",\"name\":\"штука\",\"quantity_in_package\":" + N(PackSize) + ",\"piece_unit_price\":\"" + N(Price / PackSize) + "\"}]" : "") + "}";
        }

        /// <summary>Короткая запись ленты products/changes (без названия): цена, закупка, остаток.</summary>
        public string FeedRecord()
        {
            lock (_gate)
                return "{\"id\":\"" + Id + "\",\"price\":\"" + N(Price) + "\",\"purchase_price\":\"" + N(Purchase) + "\",\"quantity\":\"" + N(Qty) + "\",\"updated_at\":\"" + DateTime.UtcNow.ToString("O") + "\"}";
        }

        private WireMock.ResponseMessage Patch(string? body)
        {
            var j = JsonNode.Parse(body ?? "{}")!;
            var lose = false;
            lock (_gate)
            {
                if (D(j["purchase_price"]) is { } pp) Purchase = pp;
                if (D(j["price"]) is { } pr) Price = pr;
                if (D(j["quantity"]) is { } q)
                {
                    Qty = q;
                    lose = LoseAnswers-- > 0;
                }
            }
            if (j["quantity"] is not null)
                OnCommit?.Invoke();
            return lose ? Reply(500, "{\"detail\":\"Internal Server Error\"}") : Reply(200, Card());
        }

        private WireMock.ResponseMessage Receipt(string? body)
        {
            var j = JsonNode.Parse(body ?? "{}")!;
            double total = 0;
            lock (_gate)
            {
                foreach (var item in j["items"]!.AsArray())
                {
                    var qty = D(item!["qty"]) ?? 0;
                    Qty += qty;
                    total += qty * (D(item["purchase_price"]) ?? 0);
                }
            }
            OnCommit?.Invoke();
            return Reply(201, "{\"id\":\"r-1\",\"total_amount\":\"" + N(total) + "\"}");
        }

        private WireMock.ResponseMessage Convert(string? body)
        {
            var j = JsonNode.Parse(body ?? "{}")!;
            lock (_gate)
            {
                var per = D(j["quantity_in_package"]) ?? 1;
                Qty = Math.Round(Qty / per, 3);
                Price = Math.Round(Price * per, 2);
                Purchase = Math.Round(Purchase * per, 2);
                PackSize = per;
            }
            return Reply(200, Card());
        }

        public void Dispose() => Fake.Dispose();
    }

    // ------------------------------------------------------------------ обвязка

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CatalogFeedPage Page(string card) => new() { Status = CatalogFeedStatus.Ok, Items = [J(card)], NextCursor = "c" };

    /// <summary>Как программа после страницы ленты: плитки из памяти репозитория — в общий каталог (AvaloniaCatalogCacheService.TryLoadFromDatabase).</summary>
    private void Publish()
    {
        var tiles = _repo.LoadAllTiles();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(tiles);
    }

    /// <summary>Проход ленты с настоящей защитой остатка (как AvaloniaCatalogCacheService: FeedStockGuard.Create на каждый проход).</summary>
    private void FeedNow(Srv srv, bool guarded = true)
    {
        CatalogFeedSync.Apply(Page(srv.FeedRecord()), ApiBase, allowStock: true, guarded ? FeedStockGuard.Create() : null);
        Publish();
    }

    /// <summary>Товар склада уже в каталоге программы (полная карточка), остаток = как на сервере.</summary>
    private void Seed(Srv srv)
    {
        CatalogFeedSync.Apply(Page(srv.Card()), ApiBase, allowStock: true).Should().NotBeNull();
        Publish();
        Local().Should().Be(srv.Qty);
    }

    private double Local() => _repo.LoadTileCopy(_id)!.Quantity;

    private double Tile() => CatalogCacheService.FindProductById(_id)!.Quantity;

    private async Task<(NurMarketApiClient Api, IDisposable Restore)> UseApiAsync(Srv srv)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = srv.Fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return (api, new Restorer(() => PosApp.CatalogApi = previous));
    }

    private sealed class Restorer(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    private BackgroundReceivingQueue NewQueue(NurMarketApiClient api, bool resume = true)
    {
        var q = new BackgroundReceivingQueue();
        lock (_queues)
            _queues.Add(q);
        q.RegisterFinisher("purchases", (_, _, _) => Task.FromResult(new FinisherResult(true)));
        if (resume)
            q.Resume(api);
        return q;
    }

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

    /// <summary>Строка «Упаковками → на склад штуки»: 2 упак. × 5 шт по 300 за упаковку = 10 шт по 60 (цены как в карточке — без пересчёта каталога).</summary>
    private ReceivingLineVm PackLine()
    {
        var l = new ReceivingLineVm { ProductName = "TEST-D4", Barcode = "4600000004041", OriginalPurchasePrice = 60, OriginalSalePrice = 80 };
        l.ProductId = _id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = 1;
        l.PurchasePrice = 60;
        l.SalePrice = 80;
        l.SetPackMode(true);
        l.SetPackCount(2);
        l.SetPiecesPerPack(5);
        l.SetPackPriceText("300");
        l.SalePrice = 80;
        return l;
    }

    private static readonly PurchaseReceivingService.Supplier Supplier = new("s1", "Поставщик", null, null);

    private async Task<ReceivingJob> RunJobAsync(BackgroundReceivingQueue q, ReceivingJobRequest request)
    {
        var jobId = q.Enqueue(request);
        (await WaitAsync(() => q.Get(jobId)?.IsFinal == true)).Should().BeTrue("приход прошёл очередь");
        var job = q.Get(jobId)!;
        (await WaitAsync(() => !StockSyncService.IsReceiptHeld(_id), 5000)).Should().BeTrue("после прибавки защита «приход в пути» снята");
        return job;
    }

    // ------------------------------------------------------------------ «Склад → Приёмка», без поставщика (живой случай)

    [Fact]
    public async Task Warehouse_pack_receipt_is_counted_once_when_the_feed_brings_the_server_stock_first()
    {
        using var srv = new Srv(_id, 0);
        Seed(srv);
        srv.OnCommit = () => FeedNow(srv); // лента в момент записи остатка на сервере (как 22:55:31.032)
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);

        var line = PackLine();
        line.Quantity.Should().Be(10, "2 упак. × 5 шт — в очередь уходят штуки");
        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { line }, null, PaidNow: false, "тест"));

        job.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", job.Errors));
        srv.Qty.Should().Be(10, "на сервере 0 + 10");
        Local().Should().Be(10, "в базе программы — 10, а не 20 (Д-4)");
        Tile().Should().Be(10, "в списке «Склад → Товары» — 10");

        // «Обновить» / следующий опрос ленты: тот же серверный остаток ничего не меняет — ни под защитой «трогали недавно», ни после неё.
        FeedNow(srv);
        Local().Should().Be(10);
        Tile().Should().Be(10);
        FeedNow(srv, guarded: false);
        Local().Should().Be(10);
        Tile().Should().Be(10);
    }

    [Fact]
    public async Task Feed_and_point_refresh_do_not_apply_the_stock_of_a_product_whose_receipt_is_still_in_the_queue()
    {
        using var srv = new Srv(_id, 7);
        Seed(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true); // связи нет — приход ждёт в очереди
        var q = NewQueue(api);
        var jobId = q.Enqueue(new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { PackLine() }, null, PaidNow: false, "тест"));

        StockSyncService.IsReceiptHeld(_id).Should().BeTrue("приход стоит в очереди — остаток товара держим с постановки");
        StockSyncService.WasLocallyTouched(_id, FeedStockGuard.LocalTouchWindow).Should().BeTrue();
        StockSyncService.TouchedSince(_id, DateTime.UtcNow.AddMinutes(-10)).Should().BeTrue("полная загрузка тоже оставляет локальный остаток");
        FeedStockGuard.Create()(_id).Should().BeFalse("лента и точечное обновление остаток не применяют");
        FeedStockGuard.Create()("другой-товар").Should().BeTrue("остальные товары — как раньше");

        srv.Qty = 17; // «сервер уже с приходом» (ответ потерян в прошлой попытке) — до прибавки на плитку не кладём
        FeedNow(srv);
        Local().Should().Be(7);

        ServerOutageMonitor.ResetForTests(); // связь вернулась
        srv.Qty = 7;
        q.ForceNow(jobId).Should().BeTrue();
        (await WaitAsync(() => q.Get(jobId)?.Status == ReceivingJobStatus.Done)).Should().BeTrue();
        (await WaitAsync(() => !StockSyncService.IsReceiptHeld(_id), 5000)).Should().BeTrue();
        srv.Qty.Should().Be(17);
        Local().Should().Be(17, "офлайн-приход после досылки — один раз");
        Tile().Should().Be(17);
    }

    [Fact]
    public async Task Lost_answer_then_retry_counts_the_receipt_once_even_if_the_feed_ran_in_between()
    {
        using var srv = new Srv(_id, 5);
        Seed(srv);
        srv.LoseAnswers = 1; // первый PATCH записан, ответ потерян (500) — очередь повторит
        srv.OnCommit = () => FeedNow(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);

        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { PackLine() }, null, PaidNow: false, "тест"));

        job.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", job.Errors));
        srv.Qty.Should().Be(15, "повтор не прибавил второй раз на сервере");
        Local().Should().Be(15, "и в программе — один раз");
        Tile().Should().Be(15);
    }

    [Fact]
    public async Task Sale_on_this_pc_during_the_receipt_is_kept_and_the_receipt_is_added_once()
    {
        using var srv = new Srv(_id, 5);
        Seed(srv);
        srv.OnCommit = () =>
        {
            // Пока приход в пути: продажа 2 шт на этой кассе дошла до сервера, затем опрос ленты.
            StockSyncService.DecrementLocalStock(_id, 2);
            srv.Qty -= 2;
            FeedNow(srv);
        };
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);

        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { PackLine() }, null, PaidNow: false, "тест"));

        job.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", job.Errors));
        srv.Qty.Should().Be(13, "5 + 10 − 2");
        Local().Should().Be(13, "продажа не откатилась, приход прибавлен один раз");
        Tile().Should().Be(13);
    }

    // ------------------------------------------------------------------ с поставщиком: «Закупки → Приём товара», накладная, «Массовое добавление»

    [Theory]
    [InlineData(ReceivingJobSource.Purchases)]
    [InlineData(ReceivingJobSource.Invoice)]
    [InlineData(ReceivingJobSource.Warehouse)]
    public async Task Receipt_with_supplier_is_counted_once_in_every_source(string source)
    {
        using var srv = new Srv(_id, 4);
        Seed(srv);
        srv.OnCommit = () => FeedNow(srv); // лента сразу после документа прихода
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);

        var job = await RunJobAsync(q, new ReceivingJobRequest(source, new[] { PackLine() }, Supplier, PaidNow: true, "тест"));

        job.PostedCount.Should().Be(1, string.Join("; ", job.Errors));
        srv.Qty.Should().Be(14);
        Local().Should().Be(14, $"{source}: 4 + 10 один раз");
        Tile().Should().Be(14);
    }

    [Fact]
    public async Task Bulk_part_for_an_existing_product_is_counted_once()
    {
        using var srv = new Srv(_id, 3);
        Seed(srv);
        srv.OnCommit = () => FeedNow(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);
        var line = new ReceivingLineVm { ProductName = "TEST-D4", Barcode = "4600000004041", OriginalPurchasePrice = 60, OriginalSalePrice = 80 };
        line.ProductId = _id;
        line.Source = ReceivingSource.Warehouse;
        line.Quantity = 2;
        line.PurchasePrice = 60;
        line.SalePrice = 80;

        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Bulk, new[] { line }, null, PaidNow: false, "тест", BatchId: "b-d4", BatchPart: 1));

        job.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", job.Errors));
        srv.Qty.Should().Be(5);
        Local().Should().Be(5, "массовое добавление: 3 + 2 один раз");
    }

    // ------------------------------------------------------------------ «Упаковка с поштучной продажей»: остаток товара склада в упаковках

    [Fact]
    public async Task Pack_sale_receipt_of_a_warehouse_product_takes_the_stock_in_packs_from_the_server()
    {
        using var srv = new Srv(_id, 100); // 100 шт по 8 → упаковки по 10
        srv.Price = 8;
        srv.Purchase = 6;
        Seed(srv);
        srv.OnCommit = () => FeedNow(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);
        var line = new ReceivingLineVm { ProductName = "TEST-D4", Barcode = "4600000004041", StockBefore = 100, Quantity = 1, OriginalPurchasePrice = 6, OriginalSalePrice = 8 };
        line.ProductId = _id;
        line.Source = ReceivingSource.Warehouse;
        line.PurchasePrice = 6;
        line.SalePrice = 8;
        line.EnableReceivingPricing();
        line.SetPackSale(true, 10).Should().BeTrue();
        line.PackConvertExpected = 100;
        line.PackConvertKey = "d4-key";
        line.Quantity.Should().Be(1, "1 упаковка");

        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { line }, Supplier, PaidNow: true, "тест"));

        job.PostedCount.Should().Be(1, string.Join("; ", job.Errors));
        srv.Qty.Should().Be(11, "100 шт = 10 упак. + 1 упак.");
        Local().Should().Be(11, "остаток в упаковках с сервера, а не 100 шт + 1 упак. = 101 и не 11 + 1 = 12");
        Tile().Should().Be(11);
    }

    // ------------------------------------------------------------------ защита не застревает

    [Fact]
    public async Task Rejected_receipt_releases_the_hold_and_the_feed_applies_the_server_stock_again()
    {
        using var srv = new Srv(_id, 6);
        Seed(srv);
        srv.Fake.Server.Given(Request.Create().WithPath($"/api/main/products/{_id}/").UsingMethod("PATCH")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("{\"quantity\":[\"Неверное значение\"]}"));
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);

        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { PackLine() }, null, PaidNow: false, "тест"));

        job.IsFinal.Should().BeTrue();
        job.PostedCount.Should().Be(0);
        Local().Should().Be(6, "отказ сервера — остаток не прибавлен");
        srv.Qty = 4; // продажа на другой кассе
        FeedNow(srv);
        Local().Should().Be(4, "строка завершилась без прихода — защита «приход в пути» снята, лента работает как прежде");
    }

    [Fact]
    public void Holds_are_per_owner_and_cleared_by_an_empty_set()
    {
        var a = new object();
        var b = new object();
        StockSyncService.SetReceiptHolds(a, new[] { _id, " " + _id.ToUpperInvariant() + " ", null, "" });
        StockSyncService.SetReceiptHolds(b, new[] { _id + "-b" });
        StockSyncService.IsReceiptHeld(_id).Should().BeTrue();
        StockSyncService.IsReceiptHeld(_id.ToUpperInvariant()).Should().BeTrue("id без учёта регистра");
        StockSyncService.IsReceiptHeld(_id + "-b").Should().BeTrue();
        StockSyncService.SetReceiptHolds(a, Array.Empty<string>());
        StockSyncService.IsReceiptHeld(_id).Should().BeFalse();
        StockSyncService.IsReceiptHeld(_id + "-b").Should().BeTrue("набор другой очереди не тронут");
        StockSyncService.SetReceiptHolds(b, Array.Empty<string>());
        StockSyncService.IsReceiptHeld(_id + "-b").Should().BeFalse();
        StockSyncService.IsReceiptHeld(null).Should().BeFalse();
    }

    // ------------------------------------------------------------------ 2026-11-12 (аудит Fable, пакет 1): перезапуск, защита дольше срока, продажа во время запроса

    /// <summary>Задача, какой она лежит в queue.json после выключения программы: одна строка прихода 10 шт товара склада, без поставщика.</summary>
    private void WriteJobOnDisk(string outcome)
    {
        var job = new ReceivingJob
        {
            Id = "j-restart",
            CompanyId = "co-d4",
            Source = ReceivingJobSource.Warehouse,
            CreatedAt = DateTime.Now.AddMinutes(-2),
            Status = ReceivingJobStatus.Sending,
            Signature = "d4-restart",
            Lines =
            {
                new JobLine
                {
                    ProductId = _id, Barcode = "4600000004041", ProductName = "TEST-D4", Source = ReceivingSource.Warehouse, Quantity = 10, PurchasePrice = 60, SalePrice = 80,
                    OriginalPurchasePrice = 60, OriginalSalePrice = 80, Outcome = outcome, StockApplied = false,
                },
            },
        };
        File.WriteAllText(Path.Combine(_dir, "queue.json"), JsonSerializer.Serialize(new List<ReceivingJob> { job }));
    }

    private static int WritesTo(Srv srv) => srv.Fake.Server.LogEntries.Count(e =>
        e.RequestMessage?.Method is "PATCH" or "POST" && e.RequestMessage?.Path is { } p && (p.Contains("/products/") || p.Contains("/receipt/")));

    [Fact]
    public async Task Restart_with_an_accepted_but_not_added_line_takes_the_server_stock_not_tile_plus_received()
    {
        // Л-1: приход 10 шт принят сервером (5 → 15), программу выключили до прибавки к плитке (StockApplied не записан). После запуска каталог уже
        // загружен с сервера (15), и «плитка + принятое» дало бы 25.
        using var srv = new Srv(_id, 15);
        Seed(srv);
        Tile().Should().Be(15, "каталог после перезапуска — серверный остаток, уже с приходом");
        WriteJobOnDisk(ReceivingLineOutcome.Posted);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;

        var q = NewQueue(api); // Resume читает файл очереди

        (await WaitAsync(() => q.Get("j-restart")?.IsFinal == true)).Should().BeTrue("поднятая с диска задача дошла до конца");
        (await WaitAsync(() => !StockSyncService.IsReceiptHeld(_id), 5000)).Should().BeTrue();
        q.Get("j-restart")!.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", q.Get("j-restart")!.Errors));
        Local().Should().Be(15, "в базе — серверный остаток, а не 25 (Л-1)");
        Tile().Should().Be(15);
        srv.Qty.Should().Be(15);
        WritesTo(srv).Should().Be(0, "принятая строка заново не отправляется");
    }

    [Fact]
    public async Task Restart_with_a_line_that_was_not_yet_sent_posts_it_once_and_the_stock_is_counted_once()
    {
        using var srv = new Srv(_id, 5);
        Seed(srv);
        srv.OnCommit = () => FeedNow(srv);
        WriteJobOnDisk(ReceivingLineOutcome.Pending);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;

        var q = NewQueue(api);

        (await WaitAsync(() => q.Get("j-restart")?.IsFinal == true)).Should().BeTrue();
        (await WaitAsync(() => !StockSyncService.IsReceiptHeld(_id), 5000)).Should().BeTrue();
        q.Get("j-restart")!.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", q.Get("j-restart")!.Errors));
        srv.Qty.Should().Be(15, "5 + 10 один раз на сервере");
        Local().Should().Be(15, "и в программе — один раз");
        Tile().Should().Be(15);
    }

    [Fact]
    public async Task A_line_added_in_this_run_is_not_treated_as_restored()
    {
        // Контроль к Л-1: строка, поставленная в очередь в этом запуске, по-прежнему прибавляется к плитке (а не читается с сервера): продажа другой кассы
        // между записью и прибавкой не должна отменять прибавку — проверяется тем, что «остаток плитки + принятое» даёт число, отличное от серверного.
        using var srv = new Srv(_id, 3);
        Seed(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        srv.OnCommit = () => srv.Qty -= 2; // чужая продажа сразу после записи прихода; плитку лента не трогает (защита «приход в пути»)
        var q = NewQueue(api);
        var line = new ReceivingLineVm { ProductName = "TEST-D4", Barcode = "4600000004041", OriginalPurchasePrice = 60, OriginalSalePrice = 80 };
        line.ProductId = _id;
        line.Source = ReceivingSource.Warehouse;
        line.Quantity = 2;
        line.PurchasePrice = 60;
        line.SalePrice = 80;

        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { line }, null, PaidNow: false, "тест"));

        job.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", job.Errors));
        srv.Qty.Should().Be(3, "на сервере 3 + 2 − 2");
        Local().Should().Be(5, "новая строка: «плитка 3 + принятое 2» (чужую продажу подтянет перечитывание), а не серверное 3");
    }

    [Fact]
    public async Task After_the_stock_is_added_the_product_is_always_queued_for_a_reread_after_the_protection_window()
    {
        // Л-6: прибавка к плитке не конец — чужие продажи/приходы за время ожидания прихода подтянет перечитывание с сервера, когда окно защиты пройдёт.
        using var srv = new Srv(_id, 3);
        Seed(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        var q = NewQueue(api);
        var line = new ReceivingLineVm { ProductName = "TEST-D4", Barcode = "4600000004041", OriginalPurchasePrice = 60, OriginalSalePrice = 80 };
        line.ProductId = _id;
        line.Source = ReceivingSource.Warehouse;
        line.Quantity = 2;
        line.PurchasePrice = 60;
        line.SalePrice = 80;
        StockSyncService.IsRereadScheduled(_id).Should().BeFalse("до прихода перечитывания нет");

        var job = await RunJobAsync(q, new ReceivingJobRequest(ReceivingJobSource.Warehouse, new[] { line }, null, PaidNow: false, "тест"));

        job.Status.Should().Be(ReceivingJobStatus.Done, string.Join("; ", job.Errors));
        StockSyncService.IsRereadScheduled(_id).Should().BeTrue("после прибавки товар поставлен на перечитывание");
        StockSyncService.RereadDueIn(_id)!.Value.Should().BeGreaterThan(FeedStockGuard.LocalTouchWindow - TimeSpan.FromSeconds(5),
            "перечитывание — после окна защиты «трогали недавно» (90 с), а не раньше: иначе защита его отбросит");
    }

    private static long AbsDue(string id) => Environment.TickCount64 + (long)(StockSyncService.RereadDueIn(id)?.TotalMilliseconds ?? long.MinValue / 2);

    [Fact]
    public async Task Reread_of_a_product_whose_receipt_waits_is_not_dropped_by_age_and_runs_when_the_hold_is_lifted()
    {
        // Л-6 («защита 35 минут»): срок жизни записи перечитывания — 30 минут, приход мог ждать дольше. Здесь срок сжат до 0,6 с и приход ждёт втрое дольше;
        // раньше запись снималась, и чужая продажа за это время так и не подтягивалась.
        StockSyncService.RereadMaxAge = TimeSpan.FromMilliseconds(600);
        StockSyncService.RereadTick = TimeSpan.FromMilliseconds(50);
        StockSyncService.RereadRetryDelay = TimeSpan.FromMilliseconds(100);
        using var srv = new Srv(_id, 7);
        Seed(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;

        StockSyncService.SetReceiptHolds(_holdOwner, new[] { _id }); // приход этого товара ждёт в очереди
        StockSyncService.ScheduleStockReread(_id, TimeSpan.FromMilliseconds(100));
        var due0 = AbsDue(_id);
        srv.Qty = 4; // чужая продажа за время ожидания: лента её не применяет (защита), перечитывание — должно

        var clock = System.Diagnostics.Stopwatch.StartNew();
        (await WaitAsync(() => StockSyncService.IsRereadScheduled(_id) && AbsDue(_id) > due0 + 300, 8000)).Should().BeTrue("цикл перечитывания работал (откладывал запись), а не спал");
        await Task.Delay(Math.Max(0, 1800 - (int)clock.ElapsedMilliseconds));
        StockSyncService.IsRereadScheduled(_id).Should().BeTrue("пока приход ждёт, запись не стареет и не снимается (Л-6)");
        Local().Should().Be(7, "под защитой «приход в пути» серверное число на плитку не кладётся");

        StockSyncService.SetReceiptHolds(_holdOwner, Array.Empty<string>()); // приход прибавлен — защита снята

        (await WaitAsync(() => Local() == 4, 10000)).Should().BeTrue("перечитывание подтянуло чужую продажу: на плитке серверные 4");
        Tile().Should().Be(4);
        StockSyncService.IsRereadScheduled(_id).Should().BeFalse("выполненная запись снята");
    }

    [Fact]
    public async Task Server_stock_after_receipt_is_not_written_over_a_sale_made_on_this_pc_during_the_request()
    {
        // Л-23: остаток «с сервера после прихода» раньше писался без проверки «товар продавали здесь во время запроса».
        using var srv = new Srv(_id, 11);
        Seed(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        srv.OnGet = () => StockSyncService.DecrementLocalStock(_id, 2); // продажа 2 шт на этой кассе, пока шёл запрос; ответ — 11 (сервер о продаже ещё не знал)

        var written = await StockSyncService.ApplyServerStockAfterReceiptAsync(_id);

        written.Should().BeFalse("за время запроса остаток менялся здесь — серверное значение не записывается");
        Local().Should().Be(9, "продажа не затёрта устаревшим серверным 11");
        Tile().Should().Be(9);

        // Сервер продажу узнал, новый запрос без продаж во время него — записывается.
        srv.OnGet = null;
        srv.Qty = 9;
        await Task.Delay(80); // отметка продажи старше начала запроса (часы TickCount64 грубые — ~15 мс)
        (await StockSyncService.ApplyServerStockAfterReceiptAsync(_id)).Should().BeTrue();
        Local().Should().Be(9);
    }

    [Fact]
    public async Task Server_stock_after_receipt_is_written_when_nothing_changed_here_during_the_request()
    {
        using var srv = new Srv(_id, 4);
        Seed(srv);
        var (api, restore) = await UseApiAsync(srv);
        using var guard = restore;
        srv.Qty = 14; // приход пришёл на сервер, на плитке ещё старое

        (await StockSyncService.ApplyServerStockAfterReceiptAsync(_id)).Should().BeTrue();

        Local().Should().Be(14);
        (await WaitAsync(() => Tile() == 14, 3000)).Should().BeTrue();
    }
}
