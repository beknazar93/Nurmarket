using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>Отдельная коллекция БЕЗ параллельного запуска: тесты «Неотправленного» трогают общее состояние (монитор аварии и сессии, очереди приходов, общая база временной папки, журнал OutboxService.Audit).</summary>
[CollectionDefinition("Outbox", DisableParallelization = true)]
public sealed class OutboxCollection
{
}

/// <summary>2026-11-06: «Неотправленное» — единое окно очередей и ручная отправка (OutboxService). Проверяем: сборка записей из разных источников по времени СОЗДАНИЯ; права кассира и владельца;
/// «Отправить сейчас» уходит один раз при двойном нажатии и при параллельной автоотправке (счётчик запросов на сервере = 1); нет связи — запись остаётся, сообщение понятное, быстро;
/// сессия недействительна — запросов нет; отказ сервера — «Ошибка» с причиной, очередь идёт дальше; чек продажи убрать нельзя; убранная запись пишется в журнал.
/// Чеки — через настоящий SyncService (его замок общий с автоматикой), приходы — через настоящую очередь с WireMock-сервером, остальное — через подставные источники.</summary>
[Collection("Outbox")]
public sealed class OutboxServiceTests : IDisposable
{
    private static readonly OutboxAccess Admin = new(true, "Владелец");
    private static readonly OutboxAccess Cashier = new(false, "Айгуль");
    private readonly Action<string> _oldAudit = OutboxService.Audit;
    private readonly List<string> _audit = new();

    public OutboxServiceTests()
    {
        OutboxService.Audit = _audit.Add;
        ServerOutageMonitor.ResetForTests();
        SessionExpiryMonitor.ResetForTests();
    }

    public void Dispose()
    {
        OutboxService.Audit = _oldAudit;
        ServerOutageMonitor.ResetForTests();
        SessionExpiryMonitor.ResetForTests();
    }

    // ------------------------------------------------------------------ подставной источник

    private sealed class FakeSource(string name) : IOutboxSource
    {
        public string Name { get; } = name;
        public List<OutboxItem> Items { get; } = new();
        public List<string> SendLog { get; set; } = new();
        public int Sends;
        public int Removes;
        public int Retries;
        public TimeSpan SendDelay = TimeSpan.Zero;
        public Func<string, Task>? OnSend;
        public Action? Changed;

        public IReadOnlyList<OutboxItem> Snapshot() { lock (Items) return Items.ToList(); }

        public async Task SendAsync(string id, CancellationToken ct)
        {
            Interlocked.Increment(ref Sends);
            lock (SendLog) SendLog.Add(id);
            if (SendDelay > TimeSpan.Zero)
                await Task.Delay(SendDelay, ct);
            if (OnSend is not null)
            {
                await OnSend(id);
                return;
            }
            lock (Items) Items.RemoveAll(i => i.Id == id); // по умолчанию — ушла на сервер
        }

        public bool Retry(string id)
        {
            Interlocked.Increment(ref Retries);
            lock (Items)
            {
                var i = Items.FirstOrDefault(x => x.Id == id);
                if (i is null)
                    return false;
                Items[Items.IndexOf(i)] = Make(Name, id, i.Kind, i.CreatedAt, i.Owner, OutboxStatus.Waiting);
            }
            return true;
        }

        public bool Remove(string id, string who)
        {
            Interlocked.Increment(ref Removes);
            lock (Items) return Items.RemoveAll(i => i.Id == id) > 0;
        }

        public string? ExportText(string id) => "export " + id;
        public IDisposable Subscribe(Action changed) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private static OutboxItem Make(string source, string id, OutboxKind kind, DateTimeOffset created, string? owner = null, OutboxStatus status = OutboxStatus.Waiting, bool canRemove = false, string? reason = null) => new()
    {
        Source = source,
        Id = id,
        Kind = kind,
        Title = "т-" + id,
        CreatedAt = created,
        Status = status,
        Owner = owner,
        Reason = reason,
        Attempts = status == OutboxStatus.Error ? 3 : 0,
        CanSend = status != OutboxStatus.Error,
        CanRetry = status == OutboxStatus.Error,
        CanRemove = canRemove,
    };

    private static (OutboxService Svc, FakeSource A, FakeSource B) Pair()
    {
        var svc = new OutboxService();
        var a = new FakeSource("a");
        var b = new FakeSource("b");
        svc.Register(a);
        svc.Register(b);
        return (svc, a, b);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(6));

    // ------------------------------------------------------------------ сборка и права

    [Fact]
    public void Snapshot_merges_all_sources_in_creation_order_not_in_source_order()
    {
        var (svc, a, b) = Pair();
        a.Items.Add(Make("a", "sale-late", OutboxKind.Sale, T0.AddMinutes(30)));
        a.Items.Add(Make("a", "sale-early", OutboxKind.Sale, T0.AddMinutes(5)));
        b.Items.Add(Make("b", "recv", OutboxKind.Receiving, T0.AddMinutes(20)));
        b.Items.Add(Make("b", "ret", OutboxKind.Return, T0.AddMinutes(10)));

        svc.Snapshot(Admin).Select(i => i.Id).Should().Equal("sale-early", "ret", "recv", "sale-late");
        svc.Counts(Admin).Should().Be((4, 0));
    }

    [Fact]
    public async Task Cashier_sees_and_sends_only_own_and_shared_items_admin_sees_all()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "mine", OutboxKind.Receiving, T0, owner: "Айгуль"));
        a.Items.Add(Make("a", "theirs", OutboxKind.Receiving, T0.AddMinutes(1), owner: "Бакыт"));
        a.Items.Add(Make("a", "shared", OutboxKind.ShiftClose, T0.AddMinutes(2)));

        svc.Snapshot(Cashier).Select(i => i.Id).Should().Equal("mine", "shared");
        svc.Snapshot(Admin).Select(i => i.Id).Should().Equal("mine", "theirs", "shared");

        var denied = await svc.SendNowAsync("a:theirs", Cashier);
        denied.Kind.Should().Be(OutboxSendKind.Forbidden);
        a.Sends.Should().Be(0, "чужую запись кассир не отправляет");
        (await svc.SendNowAsync("a:theirs", Admin)).Kind.Should().Be(OutboxSendKind.Sent);
    }

    // ------------------------------------------------------------------ двойное нажатие и автоматика

    [Fact]
    public async Task Double_press_sends_once()
    {
        var (svc, a, _) = Pair();
        a.SendDelay = TimeSpan.FromMilliseconds(300);
        a.Items.Add(Make("a", "x", OutboxKind.Receiving, T0));

        var first = svc.SendNowAsync("a:x", Admin);
        var second = svc.SendNowAsync("a:x", Admin);
        var results = await Task.WhenAll(first, second);

        a.Sends.Should().Be(1, "второе нажатие на то, что уже отправляется, ничего не делает");
        results.Select(r => r.Kind).Should().BeEquivalentTo(new[] { OutboxSendKind.Sent, OutboxSendKind.AlreadySending });
    }

    [Fact]
    public async Task Item_that_automation_is_sending_right_now_is_not_sent_again()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "x", OutboxKind.Sale, T0, status: OutboxStatus.Sending));

        var r = await svc.SendNowAsync("a:x", Admin);

        r.Kind.Should().Be(OutboxSendKind.AlreadySending);
        a.Sends.Should().Be(0);
    }

    // ------------------------------------------------------------------ нет связи и нет входа

    [Fact]
    public async Task No_connection_keeps_the_item_and_answers_fast_even_if_the_probe_hangs()
    {
        var (svc, a, _) = Pair();
        svc.ReachTimeout = TimeSpan.FromMilliseconds(500);
        svc.ReachCheck = _ => new TaskCompletionSource<bool>().Task; // проверка связи «зависла» и токен не слушает
        a.Items.Add(Make("a", "x", OutboxKind.Sale, T0));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await svc.SendNowAsync("a:x", Admin);
        sw.Stop();

        r.Kind.Should().Be(OutboxSendKind.NoConnection);
        r.Message.Should().Be(OutboxText.NoConnection);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "без зависания на таймауте сети");
        a.Sends.Should().Be(0, "запросов к серверу нет");
        svc.Snapshot(Admin).Should().ContainSingle(i => i.Id == "x", "запись осталась в очереди");
    }

    [Fact]
    public async Task Server_outage_flag_means_no_connection_without_any_request()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "x", OutboxKind.Sale, T0));
        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true);

        var r = await svc.SendNowAsync("a:x", Admin);

        r.Kind.Should().Be(OutboxSendKind.NoConnection);
        a.Sends.Should().Be(0);
    }

    [Fact]
    public async Task Expired_session_sends_nothing_and_asks_to_sign_in()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "x", OutboxKind.Sale, T0));
        SessionExpiryMonitor.Report("тест");

        var one = await svc.SendNowAsync("a:x", Admin);
        var all = await svc.SendAllAsync(Admin);

        one.Kind.Should().Be(OutboxSendKind.SessionExpired);
        one.Message.Should().Be(OutboxText.SessionExpired);
        all.StoppedBy.Should().Be(OutboxSendKind.SessionExpired);
        a.Sends.Should().Be(0, "пока сессия недействительна, запросов нет — ни серии ошибок");
    }

    // ------------------------------------------------------------------ «Отправить всё», ошибки

    [Fact]
    public async Task SendAll_goes_in_creation_order_across_sources_and_stops_when_the_connection_is_lost()
    {
        var (svc, a, b) = Pair();
        var log = new List<string>();
        a.SendLog = log;
        b.SendLog = log;
        a.Items.Add(Make("a", "1", OutboxKind.Sale, T0));
        b.Items.Add(Make("b", "2", OutboxKind.Receiving, T0.AddMinutes(1)));
        a.Items.Add(Make("a", "3", OutboxKind.Sale, T0.AddMinutes(2)));
        a.Items.Add(Make("a", "4", OutboxKind.Sale, T0.AddMinutes(3)));
        a.OnSend = id =>
        {
            if (id == "3")
                throw new System.Net.Http.HttpRequestException("обрыв");
            lock (a.Items) a.Items.RemoveAll(i => i.Id == id);
            return Task.CompletedTask;
        };

        var r = await svc.SendAllAsync(Admin);

        log.Should().Equal("1", "2", "3");
        r.Sent.Should().Be(2);
        r.StoppedBy.Should().Be(OutboxSendKind.NoConnection, "обрыв остановил отправку — четвёртая не трогается");
        svc.Snapshot(Admin).Select(i => i.Id).Should().Equal("3", "4");
    }

    // ------------------------------------------------------------------ серия чеков одним проходом

    private sealed class FakeBatchSource(string name) : IOutboxBatchSource
    {
        public string Name { get; } = name;
        public List<OutboxItem> Items { get; } = new();
        public List<IReadOnlyList<string>> Batches { get; } = new();
        public IReadOnlyList<OutboxItem> Snapshot() { lock (Items) return Items.ToList(); }
        public Task SendAsync(string id, CancellationToken ct) => SendManyAsync(new[] { id }, ct);

        public Task SendManyAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            lock (Batches) Batches.Add(ids.ToList());
            lock (Items) Items.RemoveAll(i => ids.Contains(i.Id));
            return Task.CompletedTask;
        }

        public bool Retry(string id) => false;
        public bool Remove(string id, string who) => false;
        public string? ExportText(string id) => null;
        public IDisposable Subscribe(Action changed) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task SendAll_sends_a_run_of_receipts_of_one_source_in_one_pass_and_keeps_time_order_between_sources()
    {
        var svc = new OutboxService();
        var sales = new FakeBatchSource("sale");
        var other = new FakeSource("recv");
        svc.Register(sales);
        svc.Register(other);
        for (var i = 0; i < 4; i++)
            sales.Items.Add(Make("sale", "s" + i, OutboxKind.Sale, T0.AddMinutes(i)));
        other.Items.Add(Make("recv", "r1", OutboxKind.Receiving, T0.AddMinutes(10)));
        sales.Items.Add(Make("sale", "s9", OutboxKind.Sale, T0.AddMinutes(20)));

        var r = await svc.SendAllAsync(Admin);

        r.Sent.Should().Be(6);
        sales.Batches.Should().HaveCount(2, "четыре чека подряд — один проход (а не четыре) и ещё один проход для позднего чека после прихода");
        sales.Batches[0].Should().Equal("s0", "s1", "s2", "s3");
        other.SendLog.Should().Equal("r1");
        svc.Snapshot(Admin).Should().BeEmpty();
    }

    [Fact]
    public async Task Rejected_item_becomes_error_with_the_server_reason_and_the_queue_goes_on()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "bad", OutboxKind.Receiving, T0));
        a.Items.Add(Make("a", "good", OutboxKind.Receiving, T0.AddMinutes(1)));
        a.OnSend = id =>
        {
            lock (a.Items)
            {
                if (id == "bad")
                    a.Items[a.Items.FindIndex(i => i.Id == "bad")] = Make("a", "bad", OutboxKind.Receiving, T0, status: OutboxStatus.Error, canRemove: true, reason: "Период закрыт");
                else
                    a.Items.RemoveAll(i => i.Id == id);
            }
            return Task.CompletedTask;
        };

        var r = await svc.SendAllAsync(Admin);

        r.Sent.Should().Be(1);
        r.Rejected.Should().Be(1);
        var bad = svc.Snapshot(Admin).Single();
        bad.Status.Should().Be(OutboxStatus.Error);
        bad.Reason.Should().Be("Период закрыт");
        svc.Counts(Admin).Should().Be((0, 1));
        (await svc.SendAllAsync(Admin)).Sent.Should().Be(0, "«Отправить всё» ошибки не трогает — у них своя кнопка «Повторить»");
    }

    [Fact]
    public async Task Retry_returns_the_error_to_the_queue_with_the_same_record_and_sends_it()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "e", OutboxKind.Return, T0, status: OutboxStatus.Error, reason: "сбой"));

        var r = await svc.RetryAsync("a:e", Admin);

        a.Retries.Should().Be(1);
        a.Sends.Should().Be(1);
        r.Kind.Should().Be(OutboxSendKind.Sent);
        _audit.Should().Contain(l => l.Contains("Повторить") && l.Contains("Владелец"));
    }

    // ------------------------------------------------------------------ убрать из очереди

    [Fact]
    public void Sales_receipt_can_never_be_removed_even_if_a_source_allows_it()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "sale", OutboxKind.Sale, T0, status: OutboxStatus.Error, canRemove: true, reason: "отказ"));

        var r = svc.Remove("a:sale", Admin);

        r.Done.Should().BeFalse();
        r.Message.Should().Be(OutboxText.RemoveForbiddenSale);
        a.Removes.Should().Be(0);
        _audit.Should().BeEmpty();
    }

    [Fact]
    public void Only_owner_or_admin_removes_and_only_items_in_error_and_it_is_written_to_the_journal()
    {
        var (svc, a, _) = Pair();
        a.Items.Add(Make("a", "waiting", OutboxKind.Receiving, T0, owner: "Айгуль", canRemove: true));
        a.Items.Add(Make("a", "err", OutboxKind.Receiving, T0.AddMinutes(1), owner: "Айгуль", status: OutboxStatus.Error, canRemove: true, reason: "Период закрыт"));

        svc.Remove("a:err", Cashier).Done.Should().BeFalse("кассир убирать не может, даже свою запись");
        svc.Remove("a:waiting", Admin).Done.Should().BeFalse("ждущую запись не убирают — она ещё уйдёт");
        a.Removes.Should().Be(0);

        var r = svc.Remove("a:err", Admin);

        r.Done.Should().BeTrue();
        a.Removes.Should().Be(1);
        svc.Snapshot(Admin).Select(i => i.Id).Should().Equal("waiting");
        _audit.Should().ContainSingle(l => l.Contains("УБРАНО") && l.Contains("т-err") && l.Contains("Период закрыт") && l.Contains("Владелец"), "в журнале: кто, когда, что и почему");
    }

    // ------------------------------------------------------------------ настоящая очередь приходов + WireMock

    private static int Posts(FakeNurCrmServer s) =>
        s.Server.LogEntries.Count(e => e.RequestMessage.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && e.RequestMessage.Path.Contains("/receipt/", StringComparison.OrdinalIgnoreCase));

    private static ReceivingLineVm Line(string id)
    {
        var l = new ReceivingLineVm { OriginalSalePrice = 15, OriginalPurchasePrice = 10, ProductName = "Товар " + id };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = 2;
        l.PurchasePrice = 10;
        l.SalePrice = 15;
        return l;
    }

    private static ReceivingJobRequest Req(string supplier, params ReceivingLineVm[] lines) =>
        new(ReceivingJobSource.Warehouse, lines, new PurchaseReceivingService.Supplier(supplier, "Поставщик " + supplier, null, null), PaidNow: true, "Айгуль");

    private static void Stub(FakeNurCrmServer s, string supplier, int status, string body)
    {
        s.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        s.Json("POST", $"/api/main/suppliers/{supplier}/receipt/", status, body);
        s.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"15.00\",\"purchase_price\":\"10.00\"}");
        s.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\"}");
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

    private sealed class QueueScope : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-outbox-" + Guid.NewGuid().ToString("N"));
        private readonly TimeSpan[] _delays = BackgroundReceivingQueue.RetryDelays;
        private readonly TimeSpan _poll = BackgroundReceivingQueue.ServerPoll;
        private readonly ICatalogApiService? _catalog = PosApp.CatalogApi;

        public QueueScope()
        {
            Directory.CreateDirectory(_dir);
            BackgroundReceivingQueue.DataDirOverride = _dir;
            BackgroundReceivingQueue.CompanyIdOverride = "co-outbox";
            BackgroundReceivingQueue.RetryDelays = [TimeSpan.FromMinutes(5)]; // пауза повтора длинная: только «Отправить сейчас» её снимает
            BackgroundReceivingQueue.ServerPoll = TimeSpan.FromSeconds(30);
        }

        public void Dispose()
        {
            BackgroundReceivingQueue.DataDirOverride = null;
            BackgroundReceivingQueue.CompanyIdOverride = null;
            BackgroundReceivingQueue.RetryDelays = _delays;
            BackgroundReceivingQueue.ServerPoll = _poll;
            PosApp.CatalogApi = _catalog;
            try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
        }
    }

    private static async Task<NurMarketApiClient> LoginAsync(FakeNurCrmServer s)
    {
        s.StubLogin();
        s.StubProfile();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = s.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        PosApp.CatalogApi = new CatalogApiService(api);
        return api;
    }

    [Fact]
    public async Task Receiving_without_connection_stays_queued_then_double_press_creates_exactly_one_receipt()
    {
        using var scope = new QueueScope();
        using var server = new FakeNurCrmServer();
        Stub(server, "s1", 200, "{\"id\":\"r-1\",\"total_amount\":\"20.00\"}");
        var api = await LoginAsync(server);

        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true); // интернета нет
        var queue = new BackgroundReceivingQueue();
        queue.Resume(api);
        var id = queue.Enqueue(Req("s1", Line("pa")));
        (await WaitAsync(() => queue.Get(id)?.Status == ReceivingJobStatus.RetryWait && queue.Get(id)!.WaitingForServer)).Should().BeTrue();

        var svc = new OutboxService();
        svc.Register(new ReceivingOutboxSource(queue));
        var key = "recv:" + id;

        var offline = await svc.SendNowAsync(key, Admin);
        offline.Kind.Should().Be(OutboxSendKind.NoConnection);
        Posts(server).Should().Be(0, "связи нет — запроса нет, приход остался в очереди");
        var item = svc.Snapshot(Admin).Single();
        item.Kind.Should().Be(OutboxKind.Receiving);
        item.Title.Should().Contain("Поставщик s1");
        item.Status.Should().Be(OutboxStatus.Waiting);
        item.Lines.Should().HaveCount(1);

        ServerOutageMonitor.ResetForTests(); // связь вернулась, воркер спит (ServerPoll 30 с)
        var both = await Task.WhenAll(svc.SendNowAsync(key, Admin), svc.SendNowAsync(key, Admin));

        both.Select(r => r.Kind).Should().BeEquivalentTo(new[] { OutboxSendKind.Sent, OutboxSendKind.AlreadySending });
        Posts(server).Should().Be(1, "приход создан на сервере один раз — ручная отправка не даёт дубля");
        queue.Get(id)!.Status.Should().Be(ReceivingJobStatus.Done);
        svc.Snapshot(Admin).Should().BeEmpty();
        queue.Shutdown();
    }

    [Fact]
    public async Task Manual_send_during_a_retry_pause_does_not_duplicate_with_the_automatic_worker()
    {
        using var scope = new QueueScope();
        using var server = new FakeNurCrmServer();
        Stub(server, "s1", 503, "{\"detail\":\"down\"}"); // первая попытка не прошла: приход в паузе повтора (5 минут)
        var api = await LoginAsync(server);
        var queue = new BackgroundReceivingQueue();
        queue.Resume(api);
        var id = queue.Enqueue(Req("s1", Line("pa")));
        (await WaitAsync(() => queue.Get(id)?.Status == ReceivingJobStatus.RetryWait && Posts(server) >= 1)).Should().BeTrue("первая автоматическая попытка сделана");
        await Task.Delay(300); // воркер закончил разбор ответа (503 мог объявить аварию)
        ServerOutageMonitor.ResetForTests(); // для проверки нужна «связь есть»
        var before = Posts(server);

        server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-1\",\"total_amount\":\"20.00\"}");
        var svc = new OutboxService();
        svc.Register(new ReceivingOutboxSource(queue));
        var key = "recv:" + id;
        var racing = Task.Run(() => queue.ForceNow(id)); // «автоматика» будит воркер одновременно с рукой
        var r = await svc.SendNowAsync(key, Admin);
        await racing;

        (await WaitAsync(() => queue.Get(id)!.Status == ReceivingJobStatus.Done)).Should().BeTrue();
        Posts(server).Should().Be(before + 1, "после паузы уходит ровно одна успешная отправка");
        r.Kind.Should().Be(OutboxSendKind.Sent);
        queue.Shutdown();
    }

    [Fact]
    public async Task Rejected_receiving_shows_error_with_reason_does_not_block_the_next_and_removal_is_journaled()
    {
        using var scope = new QueueScope();
        using var server = new FakeNurCrmServer();
        Stub(server, "s1", 400, "{\"detail\":\"Период закрыт\"}");
        server.Json("POST", "/api/main/suppliers/s2/receipt/", 200, "{\"id\":\"r-2\",\"total_amount\":\"20.00\"}");
        var api = await LoginAsync(server);
        var queue = new BackgroundReceivingQueue();
        queue.Resume(api);
        var bad = queue.Enqueue(Req("s1", Line("pa")));
        var good = queue.Enqueue(Req("s2", Line("pa")));

        (await WaitAsync(() => queue.Get(good)?.Status == ReceivingJobStatus.Done && queue.Get(bad)?.IsFinal == true)).Should().BeTrue("отказ по существу очередь не блокирует");
        var svc = new OutboxService();
        svc.Register(new ReceivingOutboxSource(queue));

        var items = svc.Snapshot(Admin);
        var err = items.Should().ContainSingle().Subject;
        err.Id.Should().Be(bad);
        err.Status.Should().Be(OutboxStatus.Error);
        err.Reason.Should().NotBeNullOrWhiteSpace();
        err.CanRemove.Should().BeTrue();
        err.CanTakeBack.Should().BeTrue("строки можно вернуть в приёмку (кнопка ведёт в «Приходы в фоне»)");

        svc.Remove("recv:" + bad, Cashier).Done.Should().BeFalse("кассир убрать не может");
        var removed = svc.Remove("recv:" + bad, Admin);

        removed.Done.Should().BeTrue();
        queue.Get(bad).Should().BeNull();
        svc.Snapshot(Admin).Should().BeEmpty();
        _audit.Should().ContainSingle(l => l.Contains("УБРАНО") && l.Contains("Поставщик s1") && l.Contains("Владелец"));
        queue.Shutdown();
    }

    // ------------------------------------------------------------------ чеки продаж через настоящий SyncService

    public interface ITestSalesApi : ISalesApiService, IPosQuickCheckoutApi { }

    public class SalesProxy : DispatchProxy
    {
        public int QuickCalls;
        public readonly List<string> Keys = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "PosQuickCheckoutAsync" && args is { Length: >= 2 })
            {
                Interlocked.Increment(ref QuickCalls);
                lock (Keys) Keys.Add((string)args[1]!);
                Thread.Sleep(300); // сервер отвечает не мгновенно: именно в это окно приходит вторая отправка
                return Task.FromResult(JsonDocument.Parse("{\"id\":\"sale-1\"}").RootElement.Clone());
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class AuthProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name == "CanReachApiAsync")
                return Task.FromResult(true);
            var rt = targetMethod.ReturnType;
            if (rt == typeof(Task))
                return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
            }
            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }

    private static string CartJson() => new JsonObject
    {
        ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = "Хлеб", ["quantity"] = 2, ["unit_price"] = 30.0 }),
    }.ToJsonString();

    [Fact]
    public async Task Sales_receipt_double_press_and_parallel_automatic_pass_send_it_exactly_once()
    {
        var id = Guid.NewGuid().ToString("N");
        OfflinePendingSalesStore.Append(new OfflineSaleEntry
        {
            Id = id,
            PaymentMethod = "cash",
            CashReceived = "100",
            CartJson = CartJson(),
            QuickCheckoutAttempted = true, // отправка одним запросом с ключом = id записи
            CreatedAt = DateTimeOffset.Now.AddMinutes(-3),
        });
        var sales = DispatchProxy.Create<ITestSalesApi, SalesProxy>();
        var auth = DispatchProxy.Create<IAuthApiService, AuthProxy>();
        var sync = new SyncService(sales, auth, null!, null!, null!, null!);
        var previous = SyncService.Current;
        SyncService.Current = sync;
        try
        {
            var svc = new OutboxService();
            svc.Register(new SalesQueueOutboxSource());
            var key = "sale:" + id;
            var item = svc.Snapshot(Admin).Should().ContainSingle(i => i.Id == id).Subject;
            item.Kind.Should().Be(OutboxKind.Sale);
            item.Amount.Should().Be(60m);
            item.CanRemove.Should().BeFalse("чек продажи убрать нельзя");

            var auto = Task.Run(async () =>
            {
                try { await sync.ProbeNowAsync(); await sync.TriggerSyncNowAsync(); }
                catch (Exception) { /* у подставной службы нет каталога и т. п. — для проверки важна только отправка чека */ }
            });
            var results = await Task.WhenAll(svc.SendNowAsync(key, Admin), svc.SendNowAsync(key, Admin));
            await auto;

            ((SalesProxy)(object)sales).QuickCalls.Should().Be(1, "чек ушёл на сервер один раз: двойное нажатие и параллельная автоотправка дубля не дали");
            ((SalesProxy)(object)sales).Keys.Should().ContainSingle().Which.Should().Be(id, "ключ повтора — id записи, тот же, что у автоматики");
            results.Should().Contain(r => r.Kind == OutboxSendKind.Sent);
            svc.Snapshot(Admin).Where(i => i.Id == id).Should().BeEmpty();
            svc.Remove(key, Admin).Done.Should().BeFalse();
        }
        finally
        {
            SyncService.Current = previous;
            sync.Dispose();
            OfflinePendingSalesStore.RemoveSynced(id);
        }
    }

    // ------------------------------------------------------------------ настоящие хранилища: чек, возврат без связи, закрытие смены

    [Fact]
    public void Real_stores_are_merged_by_creation_time_and_the_sales_receipt_cannot_be_removed()
    {
        var saleId = Guid.NewGuid().ToString("N");
        var opId = Guid.NewGuid().ToString();
        var shiftId = Guid.NewGuid().ToString();
        var t = DateTimeOffset.Now.AddHours(-5);
        OfflinePendingSalesStore.Append(new OfflineSaleEntry
        {
            Id = saleId, CreatedAt = t.AddMinutes(30), PaymentMethod = "cash", CartJson = CartJson(), Status = OfflineSaleEntry.Failed, LastError = "Сервер: нет товара", ShiftId = shiftId,
        });
        OfflineOperationsStore.Add(new OfflineOperationEntry
        {
            Id = opId, Kind = OfflineOperationEntry.KindDebtPayment, CreatedAt = t, Amount = 500, ClientName = "Алия", CashierName = "Бакыт", Status = OfflineOperationEntry.Failed, LastError = "Клиент удалён", ReviewNeeded = true,
        });
        PendingShiftCloseStore.Enqueue(shiftId, "1500", "Сервер не принял");
        try
        {
            var svc = new OutboxService();
            svc.Register(new SalesQueueOutboxSource());
            svc.Register(new OfflineOperationsOutboxSource());
            svc.Register(new ShiftCloseOutboxSource());

            var mine = svc.Snapshot(Admin).Where(i => i.Id == saleId || i.Id == opId || i.Id == shiftId).ToList();

            mine.Select(i => i.Kind).Should().Equal(OutboxKind.DebtPayment, OutboxKind.Sale, OutboxKind.ShiftClose);
            mine.First().Id.Should().Be(opId, "самая ранняя по времени создания — первой");
            var sale = mine.Single(i => i.Id == saleId);
            sale.Status.Should().Be(OutboxStatus.Error);
            sale.Reason.Should().Contain("нет товара");
            sale.CanRemove.Should().BeFalse();
            svc.Remove("sale:" + saleId, Admin).Done.Should().BeFalse("деньги: убрать нельзя");
            svc.Export("sale:" + saleId, Admin).Should().Contain(saleId).And.Contain("Хлеб");
            var op = mine.Single(i => i.Id == opId);
            op.Kind.Should().Be(OutboxKind.DebtPayment);
            op.Status.Should().Be(OutboxStatus.Error);
            op.CanRemove.Should().BeTrue("возврат/оплату долга с отказом сервера владелец может закрыть с подтверждением");
            svc.Snapshot(Cashier).Where(i => i.Id == opId).Should().BeEmpty("операция создана другим кассиром — кассир её не видит");

            svc.Remove("op:" + opId, Admin).Done.Should().BeTrue();
            _audit.Should().ContainSingle(l => l.Contains("УБРАНО") && l.Contains("Алия"));
        }
        finally
        {
            OfflinePendingSalesStore.RemoveSynced(saleId);
            PendingShiftCloseStore.Remove(shiftId);
            OfflineOperationsStore.Update(opId, o => o.Status = OfflineOperationEntry.Synced);
        }
    }
}

/// <summary>Новые товары, заведённые без связи (PendingProductQueue): видны в «Неотправленном», «Отправить сейчас» создаёт товар ОДИН раз (тот же воркер и ключ), отказ — «Ошибка» с причиной.</summary>
[Collection("Outbox")]
public sealed class OutboxProductTests : IDisposable
{
    private static readonly OutboxAccess Admin = new(true, "Владелец");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-outbox-products-" + Guid.NewGuid().ToString("N"));
    private readonly TimeSpan[] _oldDelays = PendingProductQueue.RetryDelays;
    private readonly TimeSpan _oldPoll = PendingProductQueue.ServerPoll;
    private readonly List<PendingProductQueue> _queues = new();
    private readonly Action<string> _oldAudit = OutboxService.Audit;

    public OutboxProductTests()
    {
        Directory.CreateDirectory(_dir);
        PendingProductQueue.DataDirOverride = _dir;
        PendingProductQueue.CompanyIdOverride = "co-outbox-products";
        PendingProductQueue.RetryDelays = [TimeSpan.FromMinutes(5)];
        PendingProductQueue.ServerPoll = TimeSpan.FromSeconds(30);
        PendingProductQueue.DeleteLocalOverride = _ => { };
        PendingProductQueue.SaveLocalOverride = (_, realId) => realId;
        PendingProductQueue.ToastOverride = (_, _) => { };
        OutboxService.Audit = _ => { };
        ServerOutageMonitor.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var q in _queues)
        {
            try { Task.WhenAny(q.StopAsync(), Task.Delay(10000)).GetAwaiter().GetResult(); }
            catch { /* воркер уже остановлен */ }
        }
        ServerOutageMonitor.ResetForTests();
        OutboxService.Audit = _oldAudit;
        PendingProductQueue.DataDirOverride = null;
        PendingProductQueue.CompanyIdOverride = null;
        PendingProductQueue.RetryDelays = _oldDelays;
        PendingProductQueue.ServerPoll = _oldPoll;
        PendingProductQueue.DeleteLocalOverride = null;
        PendingProductQueue.SaveLocalOverride = null;
        PendingProductQueue.ToastOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ProductEditRequest Req(string name) => new()
    {
        Name = name, Barcode = "4601234567890", Unit = "шт", Quantity = 3, Price = 100, PurchasePrice = 60, IsNew = true, IdempotencyKey = Guid.NewGuid().ToString("N"),
    };

    private static int Creates(FakeNurCrmServer s) =>
        s.Server.LogEntries.Count(e => e.RequestMessage.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && e.RequestMessage.Path.Contains("create-manual", StringComparison.OrdinalIgnoreCase));

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

    [Fact]
    public async Task New_product_waits_without_connection_and_double_press_creates_it_exactly_once()
    {
        using var server = new FakeNurCrmServer();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/products/warehouse-barcode/*", 404, "{}");
        server.Json("POST", "/api/main/products/create-manual/", 201, "{\"id\":\"srv-1\",\"name\":\"Новый товар\"}");
        var apiClient = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await apiClient.LoginAsync("cashier@test.kg", "12345678");

        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true);
        var queue = new PendingProductQueue();
        _queues.Add(queue);
        queue.Resume(new CatalogApiService(apiClient));
        queue.Enqueue(Req("Новый товар"), "tmp-1");

        var svc = new OutboxService();
        svc.Register(new ProductOutboxSource(queue));
        var item = svc.Snapshot(Admin).Single();
        item.Kind.Should().Be(OutboxKind.NewProduct);
        item.Title.Should().Be("Новый товар");
        item.Status.Should().Be(OutboxStatus.Waiting);

        (await svc.SendNowAsync("product:tmp-1", Admin)).Kind.Should().Be(OutboxSendKind.NoConnection);
        Creates(server).Should().Be(0);

        ServerOutageMonitor.ResetForTests();
        var both = await Task.WhenAll(svc.SendNowAsync("product:tmp-1", Admin), svc.SendNowAsync("product:tmp-1", Admin));

        both.Select(r => r.Kind).Should().BeEquivalentTo(new[] { OutboxSendKind.Sent, OutboxSendKind.AlreadySending });
        (await WaitAsync(() => Creates(server) >= 1)).Should().BeTrue();
        Creates(server).Should().Be(1, "товар создан на сервере один раз");
        svc.Snapshot(Admin).Should().BeEmpty();
    }

    [Fact]
    public async Task Rejected_product_is_an_error_with_reason_and_can_be_removed_by_admin()
    {
        using var server = new FakeNurCrmServer();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/products/warehouse-barcode/*", 404, "{}");
        server.Json("POST", "/api/main/products/create-manual/", 400, "{\"detail\":\"Штрихкод уже занят\"}");
        var apiClient = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await apiClient.LoginAsync("cashier@test.kg", "12345678");
        var queue = new PendingProductQueue();
        _queues.Add(queue);
        queue.Resume(new CatalogApiService(apiClient));
        queue.Enqueue(Req("Плохой товар"), "tmp-2");
        (await WaitAsync(() => queue.Snapshot().FirstOrDefault(j => j.TempId == "tmp-2")?.Status == PendingProductStatus.Failed)).Should().BeTrue();

        var svc = new OutboxService();
        svc.Register(new ProductOutboxSource(queue));
        var item = svc.Snapshot(Admin).Single();
        item.Status.Should().Be(OutboxStatus.Error);
        item.Reason.Should().NotBeNullOrWhiteSpace();
        item.CanRemove.Should().BeTrue();

        svc.Remove("product:tmp-2", Admin).Done.Should().BeTrue();
        svc.Snapshot(Admin).Should().BeEmpty();
    }
}
