using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-06: очередь создания новых товаров на диске (PendingProductQueue). Жалоба клиента: товар, заведённый без интернета, на сервер не попадал — одна попытка и всё.
/// Сервер — WireMock, настоящая очередь с папкой во временной папке; замена временного товара в локальной базе подменена записью вызовов (базу кассы не трогаем).
/// Проверяем: нет сети → задача на диске за &lt;3 с и ничего не отправлено; перезапуск → задача читается и уходит один раз; ответ потерян → повтор с тем же ключом, второй товар не создаётся;
/// отказ 4xx → «Ошибка», очередь идёт дальше; обрыв связи не убивает задачу; временный id в приходе заменяется настоящим (или приход ждёт).</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class PendingProductQueueTests : IDisposable
{
    private const string Create = "/api/main/products/create-manual/";
    private const string Search = "/api/main/products/warehouse-barcode/*";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-pending-products-" + Guid.NewGuid().ToString("N"));
    private readonly TimeSpan[] _oldDelays = PendingProductQueue.RetryDelays;
    private readonly TimeSpan _oldPoll = PendingProductQueue.ServerPoll;
    private readonly TimeSpan _oldMaybe = PendingProductQueue.MaybeSentDelay;
    private readonly List<string> _deleted = new();
    private readonly List<(string TempOrName, string RealId)> _saved = new();
    private readonly List<(string Text, bool Error)> _toasts = new();
    private readonly List<PendingProductQueue> _queues = new();

    public PendingProductQueueTests()
    {
        Directory.CreateDirectory(_dir);
        PendingProductQueue.DataDirOverride = _dir;
        PendingProductQueue.CompanyIdOverride = "co-products";
        PendingProductQueue.RetryDelays = [TimeSpan.FromMilliseconds(400)];
        PendingProductQueue.ServerPoll = TimeSpan.FromMilliseconds(200);
        PendingProductQueue.MaybeSentDelay = TimeSpan.FromMilliseconds(300);
        PendingProductQueue.DeleteLocalOverride = id => { lock (_deleted) _deleted.Add(id); };
        PendingProductQueue.SaveLocalOverride = (req, realId) => { lock (_saved) _saved.Add((req.Name, realId)); return realId; };
        PendingProductQueue.ToastOverride = (text, error) => { lock (_toasts) _toasts.Add((text, error)); };
        ServerOutageMonitor.ResetForTests();
    }

    public void Dispose()
    {
        // Воркеры должны закончиться ДО сброса состояния связи: опоздавший ответ сервера иначе меняет его для следующих тестов.
        foreach (var q in _queues.Append(PendingProductQueue.Instance))
        {
            try { Task.WhenAny(q.StopAsync(), Task.Delay(10000)).GetAwaiter().GetResult(); }
            catch { /* воркер уже остановлен */ }
        }
        ServerOutageMonitor.ResetForTests();
        PendingProductQueue.DataDirOverride = null;
        PendingProductQueue.CompanyIdOverride = null;
        PendingProductQueue.RetryDelays = _oldDelays;
        PendingProductQueue.ServerPoll = _oldPoll;
        PendingProductQueue.MaybeSentDelay = _oldMaybe;
        PendingProductQueue.DeleteLocalOverride = null;
        PendingProductQueue.SaveLocalOverride = null;
        PendingProductQueue.ToastOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------ обвязка

    private PendingProductQueue NewQueue()
    {
        var q = new PendingProductQueue();
        _queues.Add(q);
        return q;
    }

    private static async Task<ICatalogApiService> LoginAsync(FakeNurCrmServer s)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    private static ProductEditRequest Req(string name, string? barcode = "4601234567890", string key = "") => new()
    {
        Name = name,
        Barcode = barcode,
        Unit = "шт",
        Quantity = 3,
        Price = 100,
        PurchasePrice = 60,
        IsNew = true,
        IdempotencyKey = key.Length > 0 ? key : null,
    };

    private static int Count(FakeNurCrmServer s, string method, string pathPart) =>
        s.Server.LogEntries.Count(e => e.RequestMessage.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && e.RequestMessage.Path.Contains(pathPart, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> WaitAsync(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond())
                return true;
            await Task.Delay(40);
        }
        return cond();
    }

    /// <summary>Читать файл очереди, пока воркер может его заменять: общий доступ на чтение/запись/удаление.</summary>
    private static string ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static PendingProductJob? Job(PendingProductQueue q, string tempId) =>
        q.Snapshot().FirstOrDefault(j => j.TempId == tempId);

    private static string? KeyOf(WireMock.Logging.ILogEntry e) =>
        e.RequestMessage.Headers is { } h && h.TryGetValue("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;

    private static void StubPlain(FakeNurCrmServer s, int searchStatus, string searchBody, int createStatus, string createBody)
    {
        s.Server.ResetMappings();
        s.StubLogin();
        s.StubProfile();
        s.Json("GET", Search, searchStatus, searchBody);
        s.Json("POST", Create, createStatus, createBody);
    }

    // ------------------------------------------------------------------ нет сети → диск за <3 с

    [Fact]
    public async Task No_network_puts_the_task_on_disk_fast_and_sends_nothing()
    {
        using var server = new FakeNurCrmServer();
        StubPlain(server, 404, "{}", 201, "{\"id\":\"srv-1\"}");
        var api = await LoginAsync(server);

        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true); // интернета нет
        var q = NewQueue();
        q.Resume(api);
        var started = System.Diagnostics.Stopwatch.StartNew();
        q.Enqueue(Req("Новый товар", key: "key-1"), "tmp-1");
        started.ElapsedMilliseconds.Should().BeLessThan(3000, "сохранение товара без сети не ждёт сервер");

        var file = Path.Combine(_dir, "pending-products.json");
        File.Exists(file).Should().BeTrue("задача лежит на диске сразу");
        var text = ReadShared(file);
        text.Should().Contain("tmp-1").And.Contain("key-1", "ключ повтора записан на диск вместе с задачей");
        await Task.Delay(800);
        Count(server, "POST", "create-manual").Should().Be(0, "связи нет — на сервер ничего не ушло");
        Job(q, "tmp-1")!.Status.Should().NotBe(PendingProductStatus.Failed);
        q.ActiveCountAll.Should().Be(1);
    }

    // ------------------------------------------------------------------ перезапуск → читается и уходит один раз

    [Fact]
    public async Task After_restart_the_task_is_read_from_disk_and_goes_once()
    {
        using var server = new FakeNurCrmServer();
        StubPlain(server, 404, "{}", 201, "{\"id\":\"srv-1\",\"name\":\"Новый товар\"}");
        var api = await LoginAsync(server);

        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true);
        var q1 = NewQueue();
        q1.Resume(api);
        q1.Enqueue(Req("Новый товар", key: "key-restart"), "tmp-2");
        q1.Shutdown();
        await Task.Delay(300);

        // перезапуск программы: новый экземпляр читает файл
        var q2 = NewQueue();
        q2.Snapshot().Should().ContainSingle(j => j.TempId == "tmp-2" && j.Request.IdempotencyKey == "key-restart", "задача и её ключ пережили перезапуск");

        ServerOutageMonitor.ResetForTests(); // связь вернулась
        q2.Resume(api);
        (await WaitAsync(() => Job(q2, "tmp-2")?.Status == PendingProductStatus.Sent)).Should().BeTrue("товар создан после перезапуска");
        await Task.Delay(500);

        Count(server, "POST", "create-manual").Should().Be(1, "ушёл ровно один запрос создания");
        KeyOf(server.Server.LogEntries.Single(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("create-manual")))
            .Should().Be("key-restart", "тот же ключ повтора, что был записан на диск");
        Job(q2, "tmp-2")!.RealId.Should().Be("srv-1");
        _deleted.Should().Equal("tmp-2");
        _saved.Should().ContainSingle(s => s.RealId == "srv-1", "временный товар заменён настоящим в локальной базе");
        q2.ActiveCountAll.Should().Be(0);
    }

    // ------------------------------------------------------------------ ответ потерян → повтор, второй товар не создаётся

    [Fact]
    public async Task Lost_answer_with_barcode_finds_the_created_product_and_does_not_create_a_second()
    {
        using var server = new FakeNurCrmServer();
        StubPlain(server, 404, "{}", 504, "{}");
        var api = await LoginAsync(server);
        var q = NewQueue();
        q.Resume(api);
        q.Enqueue(Req("Новый товар", barcode: "4601234567890"), "tmp-3");

        (await WaitAsync(() => Count(server, "POST", "create-manual") >= 1 && Job(q, "tmp-3")?.Status == PendingProductStatus.RetryWait)).Should().BeTrue("ответа нет — задача ждёт повтора, а не падает");

        // на самом деле сервер первый запрос обработал: товар уже есть, ответ потерялся
        ServerOutageMonitor.ResetForTests();
        var postsBefore = Count(server, "POST", "create-manual");
        StubPlain(server, 200, "{\"id\":\"srv-9\",\"name\":\"Новый товар\",\"barcode\":\"4601234567890\"}", 201, "{\"id\":\"SECOND-PRODUCT\"}");

        (await WaitAsync(() => Job(q, "tmp-3")?.Status == PendingProductStatus.Sent)).Should().BeTrue();
        Job(q, "tmp-3")!.RealId.Should().Be("srv-9", "взят уже созданный товар, а не новый");
        Count(server, "POST", "create-manual").Should().Be(postsBefore, "после того как сервер ожил, второй запрос создания не ушёл — товар найден по штрихкоду");
        _saved.Should().ContainSingle(s => s.RealId == "srv-9");
    }

    [Fact]
    public async Task Lost_answer_without_barcode_repeats_with_the_same_idempotency_key()
    {
        using var server = new FakeNurCrmServer();
        StubPlain(server, 404, "{}", 504, "{}");
        var api = await LoginAsync(server);
        var q = NewQueue();
        q.Resume(api);
        q.Enqueue(Req("Товар без штрихкода", barcode: null, key: "key-lost"), "tmp-4");

        (await WaitAsync(() => Count(server, "POST", "create-manual") >= 1 && Job(q, "tmp-4")?.Status == PendingProductStatus.RetryWait)).Should().BeTrue();
        ServerOutageMonitor.ResetForTests();
        server.Json("POST", Create, 201, "{\"id\":\"srv-5\"}");

        (await WaitAsync(() => Job(q, "tmp-4")?.Status == PendingProductStatus.Sent)).Should().BeTrue();
        var posts = server.Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("create-manual")).ToList();
        posts.Count.Should().BeGreaterThanOrEqualTo(2, "первый запрос без ответа и повтор");
        posts.Select(KeyOf).Distinct().Should().Equal(new[] { "key-lost" }, "все запросы создания несут один и тот же ключ — сервер не заведёт второй товар");
        Job(q, "tmp-4")!.RealId.Should().Be("srv-5");
    }

    // ------------------------------------------------------------------ отказ 4xx → «Ошибка», очередь идёт дальше

    [Fact]
    public async Task Server_refusal_marks_error_and_the_queue_goes_on()
    {
        using var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", Search, 404, "{}");
        server.Server.Given(Request.Create().WithPath(Create).UsingPost().WithBody(new WildcardMatcher("*BadProduct*")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("{\"barcode\":[\"Товар с таким штрихкодом уже есть\"]}"));
        server.Server.Given(Request.Create().WithPath(Create).UsingPost())
            .AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody("{\"id\":\"srv-good\"}"));
        var api = await LoginAsync(server);

        var q = NewQueue();
        q.Resume(api);
        q.Enqueue(Req("BadProduct", barcode: "111"), "tmp-bad");
        q.Enqueue(Req("GoodProduct", barcode: "222"), "tmp-good");

        (await WaitAsync(() => Job(q, "tmp-good")?.Status == PendingProductStatus.Sent)).Should().BeTrue("плохая задача очередь не блокирует");
        Job(q, "tmp-bad")!.Status.Should().Be(PendingProductStatus.Failed);
        Job(q, "tmp-bad")!.LastError.Should().NotBeNullOrWhiteSpace("причина отказа показана");
        await Task.Delay(1200);
        server.Server.LogEntries.Count(e => e.RequestMessage.Method == "POST" && (e.RequestMessage.Body ?? "").Contains("BadProduct")).Should().Be(1, "отказ по существу не повторяется");
        q.FailedCount.Should().Be(1);
        q.ActiveCountAll.Should().Be(0);
        lock (_toasts)
            _toasts.Should().Contain(t => t.Error && t.Text.Contains("BadProduct"), "владелец видит, что сервер не принял товар");
        _saved.Should().ContainSingle(s => s.RealId == "srv-good");
    }

    // ------------------------------------------------------------------ обрыв связи не убивает задачу

    [Fact]
    public async Task Connection_refused_keeps_the_task_waiting_and_never_fails_it()
    {
        using var server = new FakeNurCrmServer();
        StubPlain(server, 404, "{}", 201, "{\"id\":\"srv-1\"}");
        var api = await LoginAsync(server);
        server.Server.Stop(); // сервер пропал: соединение отказано

        var q = NewQueue();
        q.Resume(api);
        q.Enqueue(Req("Новый товар"), "tmp-6");
        // клиент API сам пробует несколько раз с паузами — ждём, пока первая попытка закончится обрывом
        (await WaitAsync(() => Job(q, "tmp-6")?.Status == PendingProductStatus.RetryWait && Job(q, "tmp-6")!.LastError is not null, 40000)).Should().BeTrue();
        await Task.Delay(1500);

        var job = Job(q, "tmp-6")!;
        job.Status.Should().Be(PendingProductStatus.RetryWait, "обрыв — не отказ по существу: задача ждёт связь");
        job.LastError.Should().NotBeNullOrWhiteSpace();
        q.FailedCount.Should().Be(0);
        ReadShared(Path.Combine(_dir, "pending-products.json")).Should().Contain("tmp-6");
    }

    // ------------------------------------------------------------------ временный id в приходе → настоящий

    [Fact]
    public async Task Temporary_id_in_a_receiving_is_replaced_by_the_real_one_and_waits_while_the_product_is_not_created()
    {
        using var server = new FakeNurCrmServer();
        StubPlain(server, 404, "{}", 201, "{\"id\":\"real-1\",\"name\":\"Новый товар\"}");
        server.Json("GET", "/api/main/products/real-1/", 200, "{\"id\":\"real-1\",\"quantity\":3,\"price\":\"100.00\",\"purchase_price\":\"60.00\"}");
        server.Json("PATCH", "/api/main/products/real-1/", 200, "{\"id\":\"real-1\"}");
        server.Json("GET", "/api/main/products/tmp-7/", 404, "{}");
        server.Json("PATCH", "/api/main/products/tmp-7/", 404, "{}");
        var api = await LoginAsync(server);
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = api;
        var queue = PendingProductQueue.Instance; // приход смотрит именно в общую очередь
        queue.DiscardAllForAccountSwitch();
        try
        {
            ReceivingLineVm Line() => new()
            {
                ProductId = "tmp-7", ProductName = "Новый товар", Barcode = "4601234567890", Source = ReceivingSource.Warehouse,
                Quantity = 2, PurchasePrice = 60, SalePrice = 100, OriginalPurchasePrice = 60, OriginalSalePrice = 100,
            };

            // 1) товар ещё не создан и связи нет → приход ждёт (исключение «нет ответа»), на сервер с чужим id не ходит
            ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true);
            queue.Enqueue(Req("Новый товар"), "tmp-7");
            var waiting = async () => await PurchaseReceivingService.Instance.PostAsync(new[] { Line() }, null, false, "тест");
            await waiting.Should().ThrowAsync<System.Net.Http.HttpRequestException>("товара ещё нет на сервере — приход не уходит с несуществующим id");
            server.Server.LogEntries.Count(e => e.RequestMessage.Path.Contains("/tmp-7/")).Should().Be(0);

            // 2) связь вернулась → товар создаётся, приход идёт под настоящим id
            ServerOutageMonitor.ResetForTests();
            var line = Line();
            var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, null, false, "тест");
            result.Errors.Should().BeEmpty();
            line.ProductId.Should().Be("real-1", "временный id заменён настоящим");
            Count(server, "POST", "create-manual").Should().Be(1, "товар создан один раз, без дубля");
            server.Server.LogEntries.Any(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Path == "/api/main/products/real-1/").Should().BeTrue("остаток прибавлен у настоящего товара");
            server.Server.LogEntries.Any(e => e.RequestMessage.Path.Contains("/tmp-7/")).Should().BeFalse();

            // 3) приход, поставленный в очередь раньше, чем товар создан, тоже получит настоящий id (задача хранится после успеха)
            (await queue.ResolveAsync("tmp-7")).Should().Be(new PendingProductResolve(PendingProductResolveKind.Resolved, "real-1"));
            (await queue.ResolveAsync("обычный-товар")).Kind.Should().Be(PendingProductResolveKind.NotPending);
        }
        finally
        {
            queue.DiscardAllForAccountSwitch();
            await Task.WhenAny(queue.StopAsync(), Task.Delay(10000));
            PosApp.CatalogApi = previous;
        }
    }
}
