using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-06: офлайн-приёмка («при приёмке, если интернет исчезнет, пусть фоново всё сохранить и при появлении интернета отправить», «со старым временем приёма»).
/// Сервер — WireMock, настоящая очередь фоновых приходов с папкой во временной папке. Проверяем: нет сети → приход на диске и окно не ждёт; сеть вернулась → один приход;
/// ответ потерян → второго прихода нет (сверка по ключу); падение между шагами → дозавершается недоделанное; перезапуск → порядок FIFO; отказ сервера очередь не блокирует;
/// время приёма (в теле прихода — приёма, а не отправки; перезапуск его не меняет; поправка часов применяется один раз).</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class OfflineReceivingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-offline-recv-" + Guid.NewGuid().ToString("N"));
    private readonly TimeSpan[] _oldDelays = BackgroundReceivingQueue.RetryDelays;
    private readonly TimeSpan _oldPoll = BackgroundReceivingQueue.ServerPoll;

    public OfflineReceivingTests()
    {
        Directory.CreateDirectory(_dir);
        BackgroundReceivingQueue.DataDirOverride = _dir;
        BackgroundReceivingQueue.CompanyIdOverride = "co-offline";
        BackgroundReceivingQueue.RetryDelays = [TimeSpan.FromMilliseconds(100)];
        BackgroundReceivingQueue.ServerPoll = TimeSpan.FromMilliseconds(200);
        ServerOutageMonitor.ResetForTests();
        ServerClock.ResetForTests();
    }

    // 2026-11-06: очереди теста — все останавливаются в Dispose, даже если проверка упала (иначе забытый воркер шлёт чужие приходы на сервер следующего теста).
    private readonly List<BackgroundReceivingQueue> _queues = new();

    private BackgroundReceivingQueue NewQueue()
    {
        var q = new BackgroundReceivingQueue();
        lock (_queues)
            _queues.Add(q);
        return q;
    }

    public void Dispose()
    {
        lock (_queues)
            foreach (var q in _queues)
                q.Shutdown();
        ServerOutageMonitor.ResetForTests();
        ServerClock.ResetForTests();
        BackgroundReceivingQueue.DataDirOverride = null;
        BackgroundReceivingQueue.CompanyIdOverride = null;
        BackgroundReceivingQueue.RetryDelays = _oldDelays;
        BackgroundReceivingQueue.ServerPoll = _oldPoll;
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------ обвязка

    private static ReceivingLineVm Line(string id, double qty = 2, double purchase = 10, double cardSale = 15, double? sale = null)
    {
        var l = new ReceivingLineVm { OriginalSalePrice = cardSale, OriginalPurchasePrice = purchase, ProductName = "Товар " + id };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = qty;
        l.PurchasePrice = purchase;
        l.SalePrice = sale ?? cardSale;
        return l;
    }

    private static PurchaseReceivingService.Supplier Sup(string id) => new(id, "Поставщик " + id, null, null);

    private static void StubBase(FakeNurCrmServer s, string supplierId, int receiptStatus, string receiptBody, int patchStatus = 200, string[]? products = null)
    {
        s.Server.ResetMappings();
        s.StubLogin();
        s.StubProfile();
        s.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        s.Json("POST", $"/api/main/suppliers/{supplierId}/receipt/", receiptStatus, receiptBody);
        foreach (var p in products ?? new[] { "pa" })
        {
            s.Json("GET", $"/api/main/products/{p}/", 200, "{\"id\":\"" + p + "\",\"quantity\":10,\"price\":\"15.00\",\"purchase_price\":\"10.00\"}");
            s.Json("PATCH", $"/api/main/products/{p}/", patchStatus, "{\"id\":\"" + p + "\"}");
        }
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

    private static int Count(FakeNurCrmServer s, string method, string pathPart) =>
        s.Server.LogEntries.Count(e => e.RequestMessage.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && e.RequestMessage.Path.Contains(pathPart, StringComparison.OrdinalIgnoreCase));

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

    private static ReceivingPostState Unknown(string key, Func<string, CancellationToken, Task<IdempotencyVerdict>>? check)
    {
        var st = new ReceivingPostState { ReceiptKey = key, ReceiptOutcome = ReceivingReceiptOutcome.Unknown, ReceiptFirstSentUtc = DateTime.UtcNow, IdempotencyCheck = check };
        st.KeyOf = vm => vm.ProductId ?? "";
        return st;
    }

    private static ReceivingJobRequest Req(string supplier, params ReceivingLineVm[] lines) =>
        new(ReceivingJobSource.Warehouse, lines, Sup(supplier), PaidNow: true, "тест");

    // ------------------------------------------------------------------ (в) ответ потерян — второго прихода нет

    [Fact]
    public async Task Lost_answer_with_journal_done_does_not_send_the_receipt_again()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1", 200, "{\"id\":\"r-dup\"}");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var st = Unknown("key-lost", (_, _) => Task.FromResult(IdempotencyVerdict.Done));

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, Sup("s1"), true, "тест", default, null, st);

        Count(server, "POST", "/receipt/").Should().Be(0, "журнал сервера сказал «проведён» — второй приход не создаём");
        st.ReceiptOutcome.Should().Be(ReceivingReceiptOutcome.Posted, "переходим к дозавершению шагов");
        result.PostedLines.Should().HaveCount(1);
    }

    [Fact]
    public async Task Lost_answer_with_journal_in_progress_waits_and_does_not_send()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1", 200, "{\"id\":\"r-dup\"}");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var st = Unknown("key-progress", (_, _) => Task.FromResult(IdempotencyVerdict.InProgress));

        var result = await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, Sup("s1"), true, "тест", default, null, st);

        Count(server, "POST", "/receipt/").Should().Be(0, "сервер ещё выполняет — ждём, не повторяем");
        st.Interrupted.Should().BeTrue();
        st.LastStatusCode.Should().Be(0, "очередь сочтёт это «нет ответа» и подождёт");
        result.PostedLines.Should().BeEmpty();
    }

    [Fact]
    public async Task Lost_answer_with_journal_failed_resends_with_the_same_key()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1", 200, "{\"id\":\"r-1\",\"total_amount\":\"20.00\"}");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var st = Unknown("key-failed", (_, _) => Task.FromResult(IdempotencyVerdict.FailedRetry));

        await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, Sup("s1"), true, "тест", default, null, st);

        var posts = server.Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/receipt/")).ToList();
        posts.Should().HaveCount(1);
        BodyOf(posts[0])["idempotency_key"]!.GetValue<string>().Should().Be("key-failed", "ключ у одного и того же прихода всегда один");
        st.ReceiptOutcome.Should().Be(ReceivingReceiptOutcome.Posted);
    }

    // ------------------------------------------------------------------ время приёма в запросе

    [Fact]
    public async Task Receipt_body_carries_the_receiving_time_not_the_sending_time_and_stays_identical_on_retry()
    {
        using var server = new FakeNurCrmServer();
        var received = DateTime.Now.AddHours(-3);
        StubBase(server, "s1", 503, "{\"detail\":\"down\"}");
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var st = new ReceivingPostState { ReceivedAt = received };
        st.KeyOf = vm => vm.ProductId ?? "";

        await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, Sup("s1"), true, "тест", default, null, st);
        st.Interrupted = false;
        StubBase(server, "s1", 200, "{\"id\":\"r-1\",\"total_amount\":\"20.00\"}");
        await PurchaseReceivingService.Instance.PostAsync(new[] { Line("pa") }, Sup("s1"), true, "тест", default, null, st);

        var posts = server.Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/receipt/")).ToList();
        posts.Should().HaveCount(2);
        var first = BodyOf(posts[0]);
        var second = BodyOf(posts[1]);
        first["comment"]!.GetValue<string>().Should().Contain("Принято " + received.ToString("dd.MM.yyyy HH:mm"), "в документе — время приёма, а не отправки");
        second["comment"]!.GetValue<string>().Should().Be(first["comment"]!.GetValue<string>(), "тот же ключ — то же тело");
        second["idempotency_key"]!.GetValue<string>().Should().Be(first["idempotency_key"]!.GetValue<string>());
        foreach (var field in new[] { "created_at", "received_at", "sold_at" })
            first.ContainsKey(field).Should().BeFalse("сервер не принимает дату прихода — не придумываем поле (вопрос Умару)");
    }

    [Fact]
    public void Receiving_time_comes_from_the_saved_clock_skew_once_and_survives_restart()
    {
        var created = new DateTime(2026, 11, 6, 14, 32, 0, DateTimeKind.Local);
        var job = new ReceivingJob { Id = "j", CreatedAt = created, ClockSkewSeconds = 600 };
        job.ReceivedAt.Should().Be(created.AddMinutes(10), "поправка запомнена в момент приёма");

        ServerClock.ResetForTests();
        var restored = job.Clone(); // перезапуск = чтение из файла
        restored.ReceivedAt.Should().Be(job.ReceivedAt, "перезапуск и новая поправка часов время приёма не меняют");

        var noSkew = new ReceivingJob { Id = "k", CreatedAt = created };
        noSkew.ReceivedAt.Should().Be(created);
        noSkew.SentAt = created.AddMinutes(38);
        noSkew.Status = ReceivingJobStatus.Done;
        noSkew.WasDelayed.Should().BeTrue();
        noSkew.StatusText.Should().Contain("14:32").And.Contain("15:10", "в списке видны оба времени: принят · отправлен");
    }

    // ------------------------------------------------------------------ (г) падение между шагами

    [Fact]
    public async Task Failure_after_the_receipt_is_created_resumes_only_the_unfinished_steps()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1", 200, "{\"id\":\"r-1\",\"total_amount\":\"20.00\"}", patchStatus: 503);
        var (_, restore) = await UseApiAsync(server);
        using var guard = restore;
        var st = new ReceivingPostState { ReceivedAt = DateTime.Now };
        st.KeyOf = vm => vm.ProductId ?? "";
        ReceivingLineVm Changed() => Line("pa", cardSale: 15, sale: 20);

        await PurchaseReceivingService.Instance.PostAsync(new[] { Changed() }, Sup("s1"), true, "тест", default, null, st);
        st.ReceiptOutcome.Should().Be(ReceivingReceiptOutcome.Posted);
        st.PostStepsPending.Should().BeTrue("PATCH цены оборвала сеть — шаг не сделан");
        Count(server, "POST", "/receipt/").Should().Be(1);

        StubBase(server, "s1", 200, "{\"id\":\"r-1\"}", patchStatus: 200);
        st.Interrupted = false;
        await PurchaseReceivingService.Instance.PostAsync(new[] { Changed() }, Sup("s1"), true, "тест", default, null, st);

        Count(server, "POST", "/receipt/").Should().Be(1, "приход второй раз не создаётся");
        st.PostStepsPending.Should().BeFalse("недоделанный PATCH выполнен");
        st.PriceStepDone.Should().Contain("pa");
        st.HistoryDone.Should().HaveCount(1, "строка истории закупок записана один раз");
    }

    // ------------------------------------------------------------------ очередь: нет сети → диск → перезапуск → одна отправка в порядке FIFO

    [Fact]
    public async Task No_network_saves_to_disk_and_after_restart_sends_each_receipt_once_in_the_same_order()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1", 200, "{\"id\":\"r-1\"}");
        foreach (var s in new[] { "s2", "s3" })
            server.Json("POST", $"/api/main/suppliers/{s}/receipt/", 200, "{\"id\":\"r-" + s + "\"}");
        var (api, restore) = await UseApiAsync(server);
        using var guard = restore;

        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true); // интернета нет
        var q1 = NewQueue();
        q1.Resume(api);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var ids = new[] { "s1", "s2", "s3" }.Select(s => q1.Enqueue(Req(s, Line("pa", qty: 1 + s.Length)))).ToList();
        started.ElapsedMilliseconds.Should().BeLessThan(3000, "«Оприходовать» без сети не вешает окно: приход только записывается на диск");
        await Task.Delay(700);
        Count(server, "POST", "/receipt/").Should().Be(0, "связи нет — ничего не отправлено");
        File.Exists(Path.Combine(_dir, "queue.json")).Should().BeTrue();
        BackgroundReceivingQueue.NoConnectionNow.Should().BeTrue();
        q1.Shutdown();
        await Task.Delay(300);
        var receivedBefore = q1.Get(ids[0])!.ReceivedAt;

        // перезапуск программы: новый экземпляр читает файл
        var q2 = NewQueue();
        q2.Snapshot(true).Reverse().Select(j => j.Id).Should().Equal(ids, "порядок FIFO сохранён на диске");
        q2.Get(ids[0])!.ReceivedAt.Should().Be(receivedBefore, "время приёма после перезапуска прежнее");

        ServerOutageMonitor.ResetForTests(); // связь вернулась
        q2.Resume(api);
        (await WaitAsync(() => ids.All(i => q2.Get(i)?.Status == ReceivingJobStatus.Done))).Should().BeTrue("все приходы отправлены");
        q2.Shutdown();

        var posts = server.Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/receipt/")).Select(e => e.RequestMessage.Path).ToList();
        posts.Should().Equal("/api/main/suppliers/s1/receipt/", "/api/main/suppliers/s2/receipt/", "/api/main/suppliers/s3/receipt/");
        q2.Get(ids[0])!.SentAt.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ очередь: сеть пропала на отправке — приход не теряется и не убивается

    [Fact]
    public async Task Network_failures_never_kill_the_receipt_and_it_is_sent_when_the_server_answers()
    {
        using var server = new FakeNurCrmServer();
        // сервер «молчит» дольше срока попытки: исход неизвестен, приход остаётся в очереди
        server.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-1\"}", delay: TimeSpan.FromSeconds(4));
        server.Json("GET", "/api/main/products/pa/", 200, "{\"id\":\"pa\",\"quantity\":10,\"price\":\"15.00\"}");
        server.Json("PATCH", "/api/main/products/pa/", 200, "{\"id\":\"pa\"}");
        var (api, restore) = await UseApiAsync(server);
        using var guard = restore;
        var oldTimeout = BackgroundReceivingQueue.AttemptTimeout;
        BackgroundReceivingQueue.AttemptTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            var q = NewQueue();
            q.Resume(api);
            var id = q.Enqueue(Req("s1", Line("pa")));

            // больше попыток, чем MaxRetries (6): прежде приход после них становился «Ошибка»
            (await WaitAsync(() => q.Get(id)!.Attempts >= BackgroundReceivingQueue.MaxRetries, 30000)).Should().BeTrue();
            q.Get(id)!.Status.Should().NotBe(ReceivingJobStatus.Failed).And.NotBe(ReceivingJobStatus.NeedsCheck);
            q.Get(id)!.IsActive.Should().BeTrue("обрыв сети приход не убивает");

            // сеть восстановилась
            BackgroundReceivingQueue.AttemptTimeout = oldTimeout;
            server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-1\"}");
            (await WaitAsync(() => q.Get(id)!.Status == ReceivingJobStatus.Done, 30000)).Should().BeTrue();
            q.Shutdown();
        }
        finally
        {
            BackgroundReceivingQueue.AttemptTimeout = oldTimeout;
        }
    }

    // ------------------------------------------------------------------ отказ по существу не блокирует очередь

    [Fact]
    public async Task Rejected_receipt_needs_attention_but_does_not_block_the_next_one()
    {
        using var server = new FakeNurCrmServer();
        StubBase(server, "s1", 400, "{\"detail\":\"Период закрыт\"}");
        server.Json("POST", "/api/main/suppliers/s2/receipt/", 200, "{\"id\":\"r-2\"}");
        var (api, restore) = await UseApiAsync(server);
        using var guard = restore;

        var q = NewQueue();
        q.Resume(api);
        var bad = q.Enqueue(Req("s1", Line("pa", qty: 3)));
        var good = q.Enqueue(Req("s2", Line("pa", qty: 5)));

        (await WaitAsync(() => q.Get(good)?.Status == ReceivingJobStatus.Done && (q.Get(bad)?.IsFinal ?? false))).Should().BeTrue();
        q.Shutdown();
        q.Get(bad)!.Status.Should().Be(ReceivingJobStatus.Failed, "причина видна кассиру, кнопки «Открыть / Повторить / Удалить» работают");
        q.Get(bad)!.LastError.Should().NotBeNullOrEmpty();
        Count(server, "POST", "/suppliers/s1/receipt/").Should().Be(1, "отказ по существу не повторяется сам");
        var order = server.Server.LogEntries.Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("/receipt/")).Select(e => e.RequestMessage.Path).ToList();
        order.Should().Equal("/api/main/suppliers/s1/receipt/", "/api/main/suppliers/s2/receipt/");
    }

    // ------------------------------------------------------------------ время приёма: поправка часов один раз

    [Fact]
    public void Enqueue_remembers_the_clock_skew_at_receiving_time()
    {
        // сервер впереди ПК на 12 минут (три согласных замера) — поправка запоминается в задаче при приёме
        var pc = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
            ServerClock.Observe(new DateTimeOffset(pc.AddMinutes(12)), pc, pc.AddMilliseconds(200));
        ServerClock.Skew.Should().BeCloseTo(TimeSpan.FromMinutes(12), TimeSpan.FromSeconds(3));

        var q = NewQueue();
        ServerOutageMonitor.ReportFailure("тест", "обрыв", hard: true); // воркер не отправляет, нам нужен только Enqueue
        var id = q.Enqueue(Req("s1", Line("pa")));
        q.Shutdown();
        var job = q.Get(id)!;
        job.ClockSkewSeconds.Should().BeApproximately(720, 3);
        var received = job.ReceivedAt;

        ServerClock.ResetForTests(); // позже поправка стала другой — время приёма не двигается
        job.ReceivedAt.Should().Be(received);
    }
}
