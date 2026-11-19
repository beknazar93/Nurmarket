using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-14 (Л-6 «Офлайн»): очередь складских операций без связи (повтор без дублей, порядок, конфликт остатка), подсказки «Неотправленного» (возраст, срок 7 суток,
/// совет при долгом офлайне), тело офлайн-чека (offline + sold_at), тело офлайн-прихода (offline + received_at). Сервер подставной (без сети), папка данных — временная.</summary>
[CollectionDefinition("StockOpsQueue", DisableParallelization = true)]
public sealed class StockOpsQueueCollection
{
}

[Collection("StockOpsQueue")]
public sealed class StockOpsQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-stockops-" + Guid.NewGuid().ToString("N"));

    public StockOpsQueueTests()
    {
        Directory.CreateDirectory(_dir);
        StockOpsQueue.DataDirOverride = _dir;
        StockOpsQueue.CompanyIdOverride = "co-l6";
        ServerOutageMonitor.ResetForTests();
    }

    public void Dispose()
    {
        StockOpsQueue.DataDirOverride = null;
        StockOpsQueue.CompanyIdOverride = null;
        ServerOutageMonitor.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------------------ подставной сервер остатков и документов

    private sealed class FakeInventory : IInventoryApiService
    {
        public readonly Dictionary<string, (double Target, bool Applied)> Sessions = new();
        public int Created;
        public int Applied;
        public Exception? FailApply;          // однократный сбой «провести»
        public Exception? FailCreate;
        public bool ApplyReachedServerBeforeFailure; // «провести» дошло до сервера, а ответ потерян

        public Task<string?> CreateSessionAsync(string note, IReadOnlyList<InventorySessionItem> items, CancellationToken ct = default)
        {
            if (FailCreate is { } e)
            {
                FailCreate = null;
                throw e;
            }
            var id = "s" + (++Created);
            Sessions[id] = (items[0].QuantityFact, false);
            return Task.FromResult<string?>(id);
        }

        public Task ApplySessionAsync(string sessionId, bool allowNegative, CancellationToken ct = default)
        {
            if (FailApply is { } e)
            {
                FailApply = null;
                if (ApplyReachedServerBeforeFailure)
                {
                    Sessions[sessionId] = (Sessions[sessionId].Target, true);
                    Applied++;
                }
                throw e;
            }
            Sessions[sessionId] = (Sessions[sessionId].Target, true);
            Applied++;
            return Task.CompletedTask;
        }

        public Task<InventorySessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<InventorySessionInfo?>(Sessions.TryGetValue(sessionId, out var s) ? new InventorySessionInfo(sessionId, s.Applied ? "applied" : "draft", s.Applied) : null);
    }

    private sealed class Server
    {
        public double Stock = 10;
        public bool Down;
        public readonly List<(string Body, string Key)> Transfers = new();
        public Func<string, string, Task<JsonElement>>? TransferHandler;
        public readonly List<(string Id, double Stock)> Posted = new();

        public StockOpsDeps Deps(FakeInventory inv) => new()
        {
            Inventory = inv,
            ReadStock = (_, _) => Down ? throw new HttpRequestException("нет сети") : Task.FromResult<double?>(Stock),
            PostTransfer = async (body, key, _) =>
            {
                if (Down)
                    throw new HttpRequestException("нет сети");
                Transfers.Add((body, key));
                return TransferHandler is { } h ? await h(body, key) : JsonDocument.Parse("{\"id\":\"t1\",\"number\":\"1\"}").RootElement.Clone();
            },
            OnWriteOffPosted = (job, stock) => Posted.Add((job.Id, stock)),
        };
    }

    private static StockOpJob WriteOff(StockOpsQueue q, double qty = 3, string product = "p1") => q.EnqueueWriteOff(product, "Товар " + product, qty, "порча", "Айгуль");

    // ------------------------------------------------------------------ списание

    [Fact]
    public async Task WriteOff_goes_once_as_stock_minus_quantity_and_leaves_the_queue()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory();
        var srv = new Server { Stock = 10 };
        WriteOff(q, 3);

        var done = await q.ProcessAsync(srv.Deps(inv));

        done.Should().Be(1);
        inv.Created.Should().Be(1);
        inv.Applied.Should().Be(1);
        inv.Sessions["s1"].Target.Should().Be(7, "документ задаёт итог: серверный остаток 10 минус 3");
        q.Snapshot().Should().BeEmpty();
        srv.Posted.Single().Stock.Should().Be(7);
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0, "второй проход ничего не шлёт");
        inv.Created.Should().Be(1);
    }

    [Fact]
    public async Task No_connection_keeps_the_item_and_a_later_pass_sends_it_exactly_once()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory();
        var srv = new Server { Down = true };
        WriteOff(q);

        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0);
        q.Snapshot().Single().Status.Should().Be(StockOpStatus.Queued, "без связи запись не теряется и не получает ошибку");
        inv.Created.Should().Be(0);

        srv.Down = false;
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(1);
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0);
        inv.Created.Should().Be(1);
        inv.Applied.Should().Be(1);
    }

    [Fact]
    public async Task Lost_apply_answer_does_not_write_off_twice_when_the_document_was_applied()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory { FailApply = new HttpRequestException("обрыв"), ApplyReachedServerBeforeFailure = true };
        var srv = new Server { Stock = 10 };
        WriteOff(q, 3);

        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0);
        q.Snapshot().Single().SessionId.Should().Be("s1", "номер документа записан на диск до «провести»");

        srv.Stock = 7; // сервер уже списал
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(1);
        inv.Created.Should().Be(1, "второго документа нет");
        inv.Applied.Should().Be(1, "второго проведения нет");
        srv.Posted.Single().Stock.Should().Be(7);
    }

    [Fact]
    public async Task Lost_apply_answer_with_unapplied_document_and_same_stock_applies_the_same_document()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory { FailApply = new HttpRequestException("обрыв"), ApplyReachedServerBeforeFailure = false };
        var srv = new Server { Stock = 10 };
        WriteOff(q, 3);

        await q.ProcessAsync(srv.Deps(inv));
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(1);

        inv.Created.Should().Be(1, "тот же документ доводится до конца");
        inv.Applied.Should().Be(1);
        inv.Sessions["s1"].Target.Should().Be(7);
    }

    [Fact]
    public async Task Stock_changed_since_the_unapplied_document_creates_a_fresh_one_from_the_current_stock()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory { FailApply = new HttpRequestException("обрыв") };
        var srv = new Server { Stock = 10 };
        WriteOff(q, 3);

        await q.ProcessAsync(srv.Deps(inv));
        srv.Stock = 8; // между попытками продали 2 шт
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(1);

        inv.Created.Should().Be(2, "старый итог (7) устарел");
        inv.Sessions["s2"].Target.Should().Be(5, "новый итог — от текущего остатка 8 минус 3");
        inv.Sessions["s1"].Applied.Should().BeFalse("старый документ не проводится — иначе откатились бы чужие продажи");
    }

    [Fact]
    public async Task Not_enough_stock_is_a_conflict_nothing_is_written_off_and_retry_reevaluates()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory();
        var srv = new Server { Stock = 2 };
        var job = WriteOff(q, 3);

        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0);
        var failed = q.Snapshot().Single();
        failed.Status.Should().Be(StockOpStatus.Failed);
        failed.StockConflict.Should().BeTrue();
        failed.LastError.Should().NotBeNullOrWhiteSpace("причина — простым языком");
        inv.Created.Should().Be(0, "при нехватке остатка документ не создаётся");

        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0, "запись с отказом автоматика не трогает");
        srv.Stock = 5; // пополнили
        q.Retry(job.Id).Should().BeTrue();
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(1);
        inv.Sessions["s1"].Target.Should().Be(2);
    }

    [Fact]
    public async Task Items_go_oldest_first_and_a_lost_connection_stops_the_ones_behind()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory();
        var srv = new Server { Stock = 10 };
        var first = WriteOff(q, 1, "pA");
        await Task.Delay(15);
        var second = WriteOff(q, 2, "pB");

        var order = new List<string>();
        var deps = new StockOpsDeps
        {
            Inventory = inv,
            ReadStock = (id, _) => { order.Add(id); return id == "pA" ? throw new HttpRequestException("нет сети") : Task.FromResult<double?>(10); },
            PostTransfer = (_, _, _) => Task.FromResult(default(JsonElement)),
        };

        (await q.ProcessAsync(deps)).Should().Be(0);
        order.Should().Equal("pA");
        q.Snapshot().Select(j => j.Id).Should().Equal(first.Id, second.Id);

        var good = srv.Deps(inv);
        (await q.ProcessAsync(good)).Should().Be(2);
        inv.Sessions["s1"].Target.Should().Be(9, "pA ушёл первым");
        inv.Sessions["s2"].Target.Should().Be(8);
    }

    [Fact]
    public async Task Queue_survives_a_restart_and_keeps_the_saved_document_number()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory { FailApply = new HttpRequestException("обрыв") };
        var srv = new Server { Stock = 10 };
        WriteOff(q, 3);
        await q.ProcessAsync(srv.Deps(inv));

        var after = new StockOpsQueue(); // перезапуск кассы: читает файл
        after.Snapshot().Single().SessionId.Should().Be("s1");
        (await after.ProcessAsync(srv.Deps(inv))).Should().Be(1);
        inv.Created.Should().Be(1);
    }

    [Fact]
    public async Task Parallel_passes_do_not_duplicate()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory();
        var srv = new Server { Stock = 10 };
        WriteOff(q, 3);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => q.ProcessAsync(srv.Deps(inv))));

        results.Sum().Should().Be(1);
        inv.Created.Should().Be(1);
    }

    // ------------------------------------------------------------------ перемещение

    [Fact]
    public async Task Transfer_is_sent_with_the_saved_key_and_body_and_the_same_key_after_a_failure()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory();
        var srv = new Server();
        var body = "{\"from_branch\":null,\"to_branch\":\"b1\",\"items\":[{\"product\":\"p1\",\"quantity\":2}]}";
        q.EnqueueTransfer("Главный → Филиал", "transfer-abc", body, 2, "Айгуль");
        q.EnqueueTransfer("Главный → Филиал", "transfer-abc", body, 2, "Айгуль").Should().NotBeNull();
        q.Snapshot().Should().HaveCount(1, "тот же ключ — та же операция");

        srv.Down = true;
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0);
        srv.Down = false;
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(1);

        srv.Transfers.Should().ContainSingle().Which.Key.Should().Be("transfer-abc");
        JsonNode.Parse(srv.Transfers[0].Body)!["to_branch"]!.GetValue<string>().Should().Be("b1");
        q.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Transfer_refused_by_the_server_becomes_an_error_not_a_loop()
    {
        var q = new StockOpsQueue();
        var inv = new FakeInventory();
        var srv = new Server
        {
            TransferHandler = (_, _) => throw new ApiException("Недостаточно товара на складе", 400),
        };
        q.EnqueueTransfer("Главный → Филиал", "transfer-x", "{\"items\":[]}", 5, null);

        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0);
        var item = q.Snapshot().Single();
        item.Status.Should().Be(StockOpStatus.Failed);
        item.LastError.Should().Contain("Недостаточно");
        (await q.ProcessAsync(srv.Deps(inv))).Should().Be(0);
        srv.Transfers.Should().HaveCount(1, "отказанная запись второй раз сама не уходит");
    }

    [Fact]
    public void Outbox_source_shows_write_offs_with_conflict_hint_and_allows_retry_and_remove_only_for_errors()
    {
        var q = new StockOpsQueue();
        var job = WriteOff(q, 3);
        var source = new StockOpsOutboxSource(q, () => null);

        var waiting = source.Snapshot().Single();
        waiting.Kind.Should().Be(OutboxKind.StockWriteOff);
        waiting.CanSend.Should().BeTrue();
        waiting.CanRetry.Should().BeFalse();
        waiting.CanRemove.Should().BeFalse();
        source.Remove(job.Id, "Владелец").Should().BeFalse("ждущую запись убрать нельзя");
    }

    // ------------------------------------------------------------------ подсказки «Неотправленного»

    private static OutboxItem Item(OutboxKind kind, TimeSpan age, OutboxStatus status = OutboxStatus.Waiting, decimal? amount = null) => new()
    {
        Source = "x", Id = Guid.NewGuid().ToString("N"), Kind = kind, CreatedAt = DateTimeOffset.Now - age, Status = status, Amount = amount,
    };

    [Fact]
    public void Age_text_is_human()
    {
        var now = DateTimeOffset.Now;
        OutboxInsight.AgeText(now.AddSeconds(-20), now).Should().NotBeNullOrWhiteSpace();
        OutboxInsight.AgeText(now.AddMinutes(-25), now).Should().Contain("25");
        OutboxInsight.AgeText(now.AddHours(-3).AddMinutes(-20), now).Should().Contain("3").And.Contain("20");
        OutboxInsight.AgeText(now.AddDays(-2).AddHours(-4), now).Should().Contain("2").And.Contain("4");
    }

    [Theory]
    [InlineData(1, OutboxDeadlineLevel.None)]
    [InlineData(4.9, OutboxDeadlineLevel.None)]
    [InlineData(5.1, OutboxDeadlineLevel.Soon)]
    [InlineData(6.9, OutboxDeadlineLevel.Soon)]
    [InlineData(7.1, OutboxDeadlineLevel.Expired)]
    [InlineData(89, OutboxDeadlineLevel.Expired)]
    [InlineData(91, OutboxDeadlineLevel.TooLate)]
    public void Sale_deadline_levels_follow_the_server_seven_day_limit(double days, OutboxDeadlineLevel expected)
    {
        var item = Item(OutboxKind.Sale, TimeSpan.FromDays(days));
        OutboxInsight.Deadline(item, DateTimeOffset.Now).Should().Be(expected);
        if (expected != OutboxDeadlineLevel.None)
            OutboxInsight.DeadlineText(item, DateTimeOffset.Now).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Expired_sale_text_names_the_action_and_other_kinds_have_no_deadline()
    {
        var sale = Item(OutboxKind.Sale, TimeSpan.FromDays(8));
        OutboxInsight.DeadlineText(sale, DateTimeOffset.Now).Should().Contain("PIN");
        OutboxInsight.Deadline(Item(OutboxKind.CashOperation, TimeSpan.FromDays(30)), DateTimeOffset.Now).Should().Be(OutboxDeadlineLevel.None);
        OutboxInsight.Deadline(Item(OutboxKind.StockWriteOff, TimeSpan.FromDays(30)), DateTimeOffset.Now).Should().Be(OutboxDeadlineLevel.None);
    }

    [Fact]
    public void Every_kind_has_a_consequence_text_and_summary_counts_sale_money_and_deadlines()
    {
        foreach (var kind in Enum.GetValues<OutboxKind>())
            OutboxInsight.IfNotSent(kind).Should().NotBeNullOrWhiteSpace(kind.ToString());

        var now = DateTimeOffset.Now;
        var items = new List<OutboxItem>
        {
            Item(OutboxKind.Sale, TimeSpan.FromHours(1), amount: 120.50m),
            Item(OutboxKind.Sale, TimeSpan.FromDays(6), amount: 79.50m),
            Item(OutboxKind.Sale, TimeSpan.FromDays(8), OutboxStatus.Error, 10m),
            Item(OutboxKind.CashOperation, TimeSpan.FromDays(1), amount: 500m),
        };
        var s = OutboxInsight.Summarize(items, now);
        s.Count.Should().Be(4);
        s.SalesCount.Should().Be(3);
        s.SalesMoney.Should().Be(210m, "деньги — только чеки продаж, decimal без потерь");
        s.Soon.Should().Be(1);
        s.Expired.Should().Be(1);
        s.Errors.Should().Be(1);
        s.Oldest.Should().Be(items[2].CreatedAt);
        OutboxInsight.DeadlineBanner(s).Should().NotBeNullOrWhiteSpace();
        OutboxInsight.DeadlineBanner(OutboxInsight.Summarize(items.Take(1).ToList(), now)).Should().BeNull();
    }

    [Fact]
    public void Long_offline_plaque_appears_after_the_threshold_and_is_urgent_near_the_limit()
    {
        var now = DateTimeOffset.Now;
        var none = OutboxInsight.Summarize(Array.Empty<OutboxItem>(), now);
        OutboxInsight.EvaluateLongOffline(null, none, now).Level.Should().Be(0, "связь есть, очередь пуста");
        OutboxInsight.EvaluateLongOffline(now.AddHours(-1), none, now).Level.Should().Be(0, "час — ещё не долго");

        var advice = OutboxInsight.EvaluateLongOffline(now.AddHours(-6), none, now);
        advice.Level.Should().Be(1);
        advice.Text.Should().Contain("6").And.Contain("7");

        var old = OutboxInsight.Summarize(new[] { Item(OutboxKind.Sale, TimeSpan.FromDays(6)) }, now);
        OutboxInsight.EvaluateLongOffline(now.AddDays(-6), old, now).Level.Should().Be(2, "до 7 суток осталось меньше двух");
        OutboxInsight.EvaluateLongOffline(null, OutboxInsight.Summarize(new[] { Item(OutboxKind.Sale, TimeSpan.FromHours(9)) }, now), now).Level.Should().BeGreaterThan(0,
            "связь вернулась, но очередь старше порога — плашка остаётся, пока всё не уйдёт");
    }

    // ------------------------------------------------------------------ тела запросов

    [Fact]
    public void Offline_sale_replay_body_carries_offline_and_the_real_sale_time()
    {
        var pid = Guid.NewGuid().ToString();
        var cart = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = "line-1", ["product_id"] = pid, ["product_name"] = "Товар", ["quantity"] = 1.0, ["unit_price"] = 100.0, ["line_total"] = 100.0,
            }),
        }.ToJsonString();
        var created = DateTimeOffset.Now.AddDays(-3);
        var entry = new OfflineSaleEntry { CartJson = cart, PaymentMethod = "cash", CashReceived = "100000", CreatedAt = created };

        var body = SyncService.BuildQuickReplayBody(entry);

        body.Should().NotBeNull();
        body!["offline"]!.GetValue<bool>().Should().BeTrue("Умар 13.11: без offline:true сервер берёт текущую дату");
        DateTimeOffset.Parse(body["sold_at"]!.GetValue<string>()).Should().BeCloseTo(created, TimeSpan.FromMinutes(5), "sold_at — настоящая дата продажи");
    }

    [Fact]
    public void Receipt_offline_stamp_only_for_receipts_taken_more_than_two_minutes_ago()
    {
        var fresh = new ReceivingPostState { ReceivedAt = DateTime.Now.AddSeconds(-30) };
        PurchaseReceivingService.ReceivedAtStamp(fresh).Should().BeNull("обычный приход — как раньше, без offline");
        PurchaseReceivingService.ReceivedAtStamp(new ReceivingPostState()).Should().BeNull();

        var late = new ReceivingPostState { ReceivedAt = DateTime.Now.AddHours(-5) };
        var stamp = PurchaseReceivingService.ReceivedAtStamp(late);
        stamp.Should().NotBeNull();
        DateTimeOffset.Parse(stamp!).LocalDateTime.Should().BeCloseTo(late.ReceivedAt!.Value, TimeSpan.FromSeconds(1));
        PurchaseReceivingService.ReceivedAtStamp(late).Should().Be(stamp, "повтор уходит с тем же значением");
    }

    [Fact]
    public void Offline_too_old_and_period_closed_have_human_texts_and_shift_closed_is_recognised()
    {
        ReceivingServerMessages.Humanize("offline_too_old").Should().Contain("7");
        ReceivingServerMessages.Humanize("period_closed").Should().NotBeNullOrWhiteSpace();
        var payload = JsonDocument.Parse("{\"code\":\"period_closed\",\"reason\":\"shift_closed\"}").RootElement.Clone();
        var ex = new ApiException("Период закрыт", 423, payload);
        PurchaseReceivingService.IsShiftClosed(ex).Should().BeTrue();
        PurchaseReceivingService.IsShiftClosed(new ApiException("Период закрыт", 423, JsonDocument.Parse("{\"closed_until\":\"2026-11-01\"}").RootElement.Clone())).Should().BeFalse();
        ReceivingServerMessages.Describe(new ApiException("x", 400, JsonDocument.Parse("{\"code\":\"offline_too_old\",\"max_days\":7}").RootElement.Clone())).Should().Contain("7");
    }
}
