using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.Tests.Fiscal;

/// <summary>2026-11-14: очередь фискальных чеков (каркас без настоящего провайдера): статусы pending/failed/accepted, повтор, «Требует внимания», проход очереди с поддельным провайдером
/// и поддельной записью на сервер. В сеть ничего не уходит. Запускать только tools/qa/test-safe.ps1.</summary>
[Collection(nameof(FiscalStateCollection))]
public sealed class FiscalQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-fiscal-q-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours(6));
    private readonly FiscalQueueStore _store;

    public FiscalQueueTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new FiscalQueueStore(Path.Combine(_dir, FiscalQueueStore.FileName), () => _now);
    }

    public void Dispose()
    {
        FiscalProviders.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static string Request(string docId, FiscalDocKind kind = FiscalDocKind.Sale) => FiscalQueueRunner.SerializeRequest(new FiscalReceiptRequest(
        kind, "key-" + docId, docId, "№" + docId, new DateTimeOffset(2026, 11, 14, 11, 0, 0, TimeSpan.FromHours(6)),
        new[] { new FiscalLine("Хлеб", 2m, 45.50m, 91m, 12m) }, new[] { new FiscalPayment("cash", 91m) }, 91m));

    private sealed class FakeProvider : IFiscalProvider
    {
        public bool Connected = true;
        public Func<FiscalReceiptRequest, FiscalResult> OnReceipt = r => FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "FD-" + r.DocId, "FM-1", "RN-1", "SIGN", "https://q/" + r.DocId));
        public readonly List<FiscalReceiptRequest> Calls = new();
        public string Name => "fake";
        public bool IsConnected => Connected;
        public Task<FiscalResult> SaleAsync(FiscalReceiptRequest request, CancellationToken ct = default) { Calls.Add(request); return Task.FromResult(OnReceipt(request)); }
        public Task<FiscalResult> ReturnAsync(FiscalReceiptRequest request, CancellationToken ct = default) { Calls.Add(request); return Task.FromResult(OnReceipt(request)); }
        public Task<FiscalShiftResult> ShiftAsync(FiscalShiftAction action, CancellationToken ct = default) => Task.FromResult(new FiscalShiftResult(true, "1", null, null));
        public Task<FiscalReportResult> ReportAsync(FiscalReportKind kind, CancellationToken ct = default) => Task.FromResult(new FiscalReportResult(true, "X", 0m, null, null));
    }

    private FiscalQueueRunner Runner(FakeProvider provider, FiscalQueueRunner.ServerWriter? writer = null, bool mustFiscalize = true) =>
        new(_store, writer ?? ((_, _, _, _) => Task.FromResult(new FiscalWriteResult(FiscalWriteOutcome.Ok, ""))), () => provider, () => mustFiscalize);

    [Fact]
    public void Enqueue_is_idempotent_per_document_and_keeps_the_retry_key()
    {
        var first = _store.Enqueue(FiscalDocKind.Sale, "s-1", "1001", 91m, Request("s-1"));
        var again = _store.Enqueue(FiscalDocKind.Sale, "S-1", "1001", 91m, Request("s-1"));
        again.Key.Should().Be(first.Key, "повтор постановки не создаёт вторую запись — значит, и второй фискальный чек");
        _store.All().Should().HaveCount(1);
        _store.Enqueue(FiscalDocKind.Return, "s-1", "1001", 10m).Key.Should().NotBe(first.Key, "возврат с тем же id — отдельный документ");
        _store.All().Should().HaveCount(2);
        first.Status.Should().Be(FiscalStatus.Pending);
    }

    [Fact]
    public void Result_statuses_pending_failed_accepted_are_stored()
    {
        var a = _store.Enqueue(FiscalDocKind.Sale, "a", "1", 1m);
        var b = _store.Enqueue(FiscalDocKind.Sale, "b", "2", 1m);
        var c = _store.Enqueue(FiscalDocKind.Sale, "c", "3", 1m);

        _store.RecordResult(a.Key, FiscalResult.Fail("timeout", "ККМ не ответила", retryable: true));
        _store.RecordResult(b.Key, FiscalResult.Fail("rejected", "Неверная ставка НДС", retryable: false));
        _store.RecordResult(c.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Pending, "FD-3")));

        var ra = _store.FindByKey(a.Key)!;
        ra.Status.Should().Be(FiscalStatus.Pending);
        ra.Attempts.Should().Be(1);
        ra.LastError.Should().Be("ККМ не ответила");
        _store.FindByKey(b.Key)!.Status.Should().Be(FiscalStatus.Failed);
        var rc = _store.FindByKey(c.Key)!;
        rc.Status.Should().Be(FiscalStatus.Accepted);
        rc.Requisites!.Status.Should().Be(FiscalStatus.Accepted);
        rc.Requisites.DocNumber.Should().Be("FD-3");
        _store.UnfinishedCount.Should().Be(2, "pending + failed");
    }

    [Fact]
    public void Requeue_returns_only_failed_and_resets_attempts()
    {
        var a = _store.Enqueue(FiscalDocKind.Sale, "a", "1", 1m);
        var b = _store.Enqueue(FiscalDocKind.Sale, "b", "2", 1m);
        _store.RecordResult(a.Key, FiscalResult.Fail("x", "bad", retryable: false));
        _store.RecordResult(b.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "FD")));

        _store.Requeue(b.Key).Should().BeFalse("принятый чек повторно не пробивается");
        _store.Requeue(a.Key).Should().BeTrue();
        var ra = _store.FindByKey(a.Key)!;
        ra.Status.Should().Be(FiscalStatus.Pending);
        ra.Attempts.Should().Be(0);
        ra.LastError.Should().BeNull();
        ra.Key.Should().Be(a.Key, "ключ повтора тот же");
        _store.RequeueAllFailed().Should().Be(0);
    }

    [Fact]
    public void Needs_attention_rules()
    {
        var failed = _store.Enqueue(FiscalDocKind.Sale, "f", "1", 1m, createdAt: _now);
        _store.RecordResult(failed.Key, FiscalResult.Fail("x", "bad", retryable: false));
        var waiting = _store.Enqueue(FiscalDocKind.Sale, "w", "2", 1m, createdAt: _now);
        var tried = _store.Enqueue(FiscalDocKind.Sale, "t", "3", 1m, createdAt: _now);
        for (var i = 0; i < FiscalQueueStore.AttemptsAttention; i++)
            _store.RecordResult(tried.Key, FiscalResult.Fail("net", "нет связи", retryable: true));
        var notWritten = _store.Enqueue(FiscalDocKind.Sale, "n", "4", 1m, createdAt: _now);
        _store.RecordResult(notWritten.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "FD-4")));
        var good = _store.Enqueue(FiscalDocKind.Sale, "g", "5", 1m, createdAt: _now);
        _store.RecordResult(good.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "FD-5")));
        _store.MarkServerWritten(good.Key);

        var keys = _store.NeedsAttention().Select(e => e.DocId).ToList();
        keys.Should().BeEquivalentTo(new[] { "f", "t" }, "новый ждущий и только что принятый чек пока не тревожат");

        _now = _now.AddMinutes(11);
        _store.NeedsAttention().Select(e => e.DocId).Should().BeEquivalentTo(new[] { "f", "w", "t", "n" }, "ждёт дольше 10 минут — внимание; записанный на сервер — нет");
        _store.AcceptedNotWritten().Select(e => e.DocId).Should().BeEquivalentTo(new[] { "n" });
    }

    [Fact]
    public void Old_written_receipts_are_purged_and_accepted_unwritten_are_never_removed()
    {
        var old = _store.Enqueue(FiscalDocKind.Sale, "old", "1", 1m);
        _store.RecordResult(old.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "FD-1")));
        _store.MarkServerWritten(old.Key);
        var keep = _store.Enqueue(FiscalDocKind.Sale, "keep", "2", 1m);
        _store.RecordResult(keep.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "FD-2")));

        _store.Remove(keep.Key).Should().BeFalse("принятый, но не записанный — реквизиты потеряются");
        _now = _now.AddDays(FiscalQueueStore.KeepAcceptedDays + 1);
        _store.Enqueue(FiscalDocKind.Sale, "new", "3", 1m); // любая запись чистит старьё
        _store.All().Select(e => e.DocId).Should().BeEquivalentTo(new[] { "keep", "new" });
    }

    [Fact]
    public void Corrupt_file_reads_as_empty_queue()
    {
        File.WriteAllText(Path.Combine(_dir, FiscalQueueStore.FileName), "{ not json");
        _store.All().Should().BeEmpty();
        _store.Enqueue(FiscalDocKind.Sale, "s", "1", 1m).Status.Should().Be(FiscalStatus.Pending);
    }

    // ───────────── проход очереди ─────────────

    [Fact]
    public async Task Run_fiscalizes_pending_and_writes_requisites_to_server()
    {
        var provider = new FakeProvider();
        var written = new List<(FiscalDocKind Kind, string Id, FiscalRequisites Req)>();
        var runner = Runner(provider, (kind, id, req, _) => { written.Add((kind, id, req)); return Task.FromResult(new FiscalWriteResult(FiscalWriteOutcome.Ok, "")); });
        _store.Enqueue(FiscalDocKind.Sale, "s-1", "1001", 91m, Request("s-1"));
        _store.Enqueue(FiscalDocKind.Return, "r-1", "R1", 45m, Request("r-1", FiscalDocKind.Return));

        var summary = await runner.RunOnceAsync();

        summary.Fiscalized.Should().Be(2);
        summary.WrittenToServer.Should().Be(2);
        summary.Skipped.Should().BeFalse();
        written.Should().HaveCount(2);
        written[0].Req.DocNumber.Should().Be("FD-s-1");
        written[1].Kind.Should().Be(FiscalDocKind.Return);
        _store.All().Should().OnlyContain(e => e.Status == FiscalStatus.Accepted && e.ServerWritten);
        provider.Calls[0].IdempotencyKey.Should().Be("key-s-1", "ключ повтора из запроса");
        // второй проход ничего не делает — повторного чека нет
        (await runner.RunOnceAsync()).Fiscalized.Should().Be(0);
        provider.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Not_connected_provider_leaves_receipts_pending_without_errors()
    {
        FiscalProviders.ResetForTests(); // заглушка «ЭККМ не подключена»
        var runner = new FiscalQueueRunner(_store, (_, _, _, _) => throw new InvalidOperationException("на сервер не пишем"), mustFiscalize: () => true);
        var entry = _store.Enqueue(FiscalDocKind.Sale, "s-1", "1", 1m, Request("s-1"));

        var summary = await runner.RunOnceAsync();

        summary.Waiting.Should().Be(1);
        summary.Failed.Should().Be(0);
        var stored = _store.FindByKey(entry.Key)!;
        stored.Status.Should().Be(FiscalStatus.Pending);
        stored.Attempts.Should().Be(0, "ККМ не подключена — попытка не засчитывается");
        FiscalProviders.Current.IsConnected.Should().BeFalse();
        (await FiscalProviders.Current.SaleAsync(null!)).Success.Should().BeFalse();
        NotConnectedFiscalProvider.NotConnectedText.Should().Contain("Telemedia");
    }

    [Fact]
    public async Task Server_and_off_modes_skip_the_whole_pass()
    {
        var provider = new FakeProvider();
        _store.Enqueue(FiscalDocKind.Sale, "s-1", "1", 1m, Request("s-1"));
        var summary = await Runner(provider, mustFiscalize: false).RunOnceAsync();
        summary.Skipped.Should().BeTrue();
        provider.Calls.Should().BeEmpty("при режиме server/off касса не пробивает");

        // по умолчанию (режим не kassa, провайдера нет) — тоже пропуск
        var def = new FiscalQueueRunner(_store, (_, _, _, _) => Task.FromResult(new FiscalWriteResult(FiscalWriteOutcome.Ok, "")));
        (await def.RunOnceAsync()).Skipped.Should().BeTrue();
    }

    [Fact]
    public async Task Retryable_provider_error_keeps_pending_and_final_error_becomes_failed()
    {
        var provider = new FakeProvider { OnReceipt = r => r.DocId == "a" ? FiscalResult.Fail("timeout", "таймаут", true) : FiscalResult.Fail("rejected", "ККМ отклонила", false) };
        _store.Enqueue(FiscalDocKind.Sale, "a", "1", 1m, Request("a"));
        _store.Enqueue(FiscalDocKind.Sale, "b", "2", 1m, Request("b"));

        var summary = await Runner(provider).RunOnceAsync();

        summary.Waiting.Should().Be(1);
        summary.Failed.Should().Be(1);
        _store.Find(FiscalDocKind.Sale, "a")!.Status.Should().Be(FiscalStatus.Pending);
        _store.Find(FiscalDocKind.Sale, "a")!.Attempts.Should().Be(1);
        _store.Find(FiscalDocKind.Sale, "b")!.Status.Should().Be(FiscalStatus.Failed);

        // «Повторить» возвращает отказ в очередь и следующий проход пробивает тем же ключом
        provider.OnReceipt = r => FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "FD-" + r.DocId));
        _store.RequeueAllFailed().Should().Be(1);
        (await Runner(provider).RunOnceAsync()).Fiscalized.Should().Be(2);
        provider.Calls.Where(c => c.DocId == "b").Select(c => c.IdempotencyKey).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public async Task Server_write_failure_is_retried_without_a_second_fiscal_receipt()
    {
        var provider = new FakeProvider();
        var calls = 0;
        var runner = Runner(provider, (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? new FiscalWriteResult(FiscalWriteOutcome.Unavailable, "нет связи") : new FiscalWriteResult(FiscalWriteOutcome.Replayed, ""));
        });
        var entry = _store.Enqueue(FiscalDocKind.Sale, "s-1", "1", 1m, Request("s-1"));

        var first = await runner.RunOnceAsync();
        first.Fiscalized.Should().Be(1);
        first.WrittenToServer.Should().Be(0);
        first.Waiting.Should().Be(1);
        _store.FindByKey(entry.Key)!.ServerWritten.Should().BeFalse();

        var second = await runner.RunOnceAsync();
        second.Fiscalized.Should().Be(0, "ККМ второй раз не вызывается");
        second.WrittenToServer.Should().Be(1);
        provider.Calls.Should().HaveCount(1);
        _store.FindByKey(entry.Key)!.ServerWritten.Should().BeTrue();
    }

    [Fact]
    public async Task Server_conflict_marks_problem_keeps_requisites_and_needs_attention()
    {
        var provider = new FakeProvider();
        var runner = Runner(provider, (_, _, _, _) => Task.FromResult(new FiscalWriteResult(FiscalWriteOutcome.AlreadySet, "other")));
        var entry = _store.Enqueue(FiscalDocKind.Sale, "s-1", "1", 1m, Request("s-1"));

        var summary = await runner.RunOnceAsync();

        summary.Failed.Should().Be(1);
        var stored = _store.FindByKey(entry.Key)!;
        stored.Status.Should().Be(FiscalStatus.Accepted, "чек уже пробит — реквизиты не теряем");
        stored.Requisites!.DocNumber.Should().Be("FD-s-1");
        stored.ServerProblem.Should().Contain("AlreadySet");
        _store.NeedsAttention().Should().ContainSingle();
        (await runner.RunOnceAsync()).WrittenToServer.Should().Be(0, "запись с проблемой повторно не шлётся");
    }

    [Fact]
    public async Task Sale_not_completed_waits_and_missing_request_fails()
    {
        var provider = new FakeProvider();
        var runner = Runner(provider, (_, _, _, _) => Task.FromResult(new FiscalWriteResult(FiscalWriteOutcome.SaleNotCompleted, "wait")));
        _store.Enqueue(FiscalDocKind.Sale, "s-1", "1", 1m, Request("s-1"));
        _store.Enqueue(FiscalDocKind.Sale, "s-2", "2", 1m, requestJson: null);

        var summary = await runner.RunOnceAsync();

        summary.Waiting.Should().Be(1);
        summary.Failed.Should().Be(1);
        _store.Find(FiscalDocKind.Sale, "s-1")!.ServerProblem.Should().BeNull("продажа ещё в офлайн-очереди — реквизиты запишутся позже");
        _store.Find(FiscalDocKind.Sale, "s-2")!.Status.Should().Be(FiscalStatus.Failed);
    }
}
