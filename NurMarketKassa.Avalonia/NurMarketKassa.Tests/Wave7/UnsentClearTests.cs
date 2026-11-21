using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Fiscal;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-21, просьба владельца «Очистить неотправленные чеки» (деньги!): очистка только с правом руководителя и подтверждённым PIN; резервная копия (JSON + CSV) пишется и проверяется
/// ДО закрытия чеков — нет копии, нет очистки; чеки, которые прямо сейчас отправляются, и уже проведённые на сервере не трогаются; фискально пробитые помечаются; запись в журнал действий и в журнал кассы;
/// чек не удаляется физически, а закрывается без отправки (очередь, счётчик «В очереди» и итоги смены его больше не считают). Настоящая база не нужна: тесты идут на временной папке данных
/// (tools/qa/test-safe.ps1), свои чеки — с уникальными id.</summary>
[Collection("Outbox")]
public sealed class UnsentClearTests : IDisposable
{
    private static readonly OutboxAccess Admin = new(true, "Владелец");
    private static readonly OutboxAccess Cashier = new(false, "Айгуль");
    private readonly Action<string> _oldAudit = OutboxService.Audit;
    private readonly List<string> _audit = new();
    private readonly List<string> _ids = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nm-unsent-clear-" + Guid.NewGuid().ToString("N"));

    public UnsentClearTests() => OutboxService.Audit = _audit.Add;

    public void Dispose()
    {
        OutboxService.Audit = _oldAudit;
        foreach (var id in _ids)
            OfflinePendingSalesStore.RemoveSynced(id);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { /* временная папка */ }
    }

    private static string Cart(string name, int qty = 2, double price = 30.0) => new JsonObject
    {
        ["items"] = new JsonArray(new JsonObject { ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = name, ["quantity"] = qty, ["unit_price"] = price }),
    }.ToJsonString();

    private OfflineSaleEntry Add(string status = OfflineSaleEntry.PendingSync, int daysOld = 10, Action<OfflineSaleEntry>? tune = null)
    {
        var e = new OfflineSaleEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            PaymentMethod = "cash",
            CashReceived = "100",
            CartJson = Cart("Хлеб"),
            CreatedAt = DateTimeOffset.Now.AddDays(-daysOld),
            Status = status,
            LastError = status == OfflineSaleEntry.Failed ? "Старше 7 суток" : null,
        };
        tune?.Invoke(e);
        OfflinePendingSalesStore.Append(e);
        _ids.Add(e.Id);
        return e;
    }

    [Fact]
    public void Clear_writes_backup_json_and_csv_then_closes_receipts_without_deleting_them()
    {
        var a = Add(daysOld: 12);
        var b = Add(OfflineSaleEntry.Failed, daysOld: 9);
        var c = Add(daysOld: 1);
        var before = OfflinePendingSalesStore.PendingCount;

        var r = UnsentReceiptsCleaner.Clear(new[] { a.Id, b.Id, c.Id }, Admin, pinVerified: true, approvedBy: "Владелец", backupRoot: _root);

        r.Outcome.Should().Be(UnsentClearOutcome.Done, r.Message);
        r.Cleared.Should().Be(3);
        r.BackupFolder.Should().StartWith(_root);
        r.Message.Should().Contain(r.BackupFolder!, "путь к копии показывается владельцу");
        var jsonPath = Path.Combine(r.BackupFolder!, "receipts.json");
        var csvPath = Path.Combine(r.BackupFolder!, "receipts.csv");
        File.Exists(jsonPath).Should().BeTrue();
        File.Exists(csvPath).Should().BeTrue();
        using (var doc = JsonDocument.Parse(File.ReadAllText(jsonPath)))
        {
            doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
            var ids = doc.RootElement.GetProperty("receipts").EnumerateArray().Select(x => x.GetProperty("Id").GetString()).ToList();
            ids.Should().BeEquivalentTo(new[] { a.Id, b.Id, c.Id });
        }
        var csv = File.ReadAllLines(csvPath);
        csv.Should().HaveCount(4, "заголовок + 3 чека");
        csv.Should().Contain(l => l.StartsWith(a.Id, StringComparison.Ordinal) && l.Contains("60.00"), "сумма 2 × 30");

        // чек не удалён физически: запись есть, закрыта без отправки, очередь и счётчик её не видят
        foreach (var id in new[] { a.Id, b.Id, c.Id })
        {
            var e = OfflinePendingSalesStore.TryGetById(id)!;
            e.Status.Should().Be(OfflineSaleEntry.Synced);
            e.ClosedWithoutSendAt.Should().NotBeNull();
            e.ClosedReason.Should().Contain("Очищено");
            OfflinePendingSalesStore.LoadAllUnsynced().Should().NotContain(x => x.Id == id);
        }
        (before - OfflinePendingSalesStore.PendingCount).Should().Be(2, "ждущих было 2 (третий — «Ошибка», в «В очереди» не входит): счётчик уменьшился");

        // журнал кассы и журнал действий
        _audit.Should().Contain(l => l.Contains("УБРАНЫ неотправленные чеки") && l.Contains("Владелец") && l.Contains(r.BackupFolder!));
        var journal = File.ReadAllLines(Path.Combine(_root, "clear-journal.jsonl"));
        journal.Should().ContainSingle();
        using var j = JsonDocument.Parse(journal[0]);
        j.RootElement.GetProperty("action").GetString().Should().Be("clear_unsent_receipts");
        j.RootElement.GetProperty("cleared").GetInt32().Should().Be(3);
    }

    [Fact]
    public void Backup_exists_on_disk_BEFORE_any_receipt_is_closed()
    {
        var a = Add();
        var seen = new List<(bool BackupExists, string Status)>();
        // isSendingNow зовётся перед закрытием каждого чека — в этот момент копия уже должна лежать на диске, а чек ещё быть ждущим
        bool Probe(string key)
        {
            var id = key[(key.IndexOf(':') + 1)..];
            if (id == a.Id)
                seen.Add((Directory.Exists(_root) && Directory.GetFiles(_root, "receipts.json", SearchOption.AllDirectories).Length == 1, OfflinePendingSalesStore.TryGetById(id)!.Status));
            return false;
        }

        UnsentReceiptsCleaner.Clear(new[] { a.Id }, Admin, true, "Владелец", _root, Probe);

        seen.Should().NotBeEmpty();
        seen.Last().BackupExists.Should().BeTrue("копия записана до закрытия");
        seen.Last().Status.Should().Be(OfflineSaleEntry.PendingSync, "на момент записи копии чек ещё не закрыт");
    }

    [Fact]
    public void No_backup_no_clearing_receipts_stay_untouched()
    {
        var a = Add();
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "это файл, а не папка");

        var r = UnsentReceiptsCleaner.Clear(new[] { a.Id }, Admin, true, "Владелец", backupRoot: blocker); // в файле папку не создать

        r.Outcome.Should().Be(UnsentClearOutcome.BackupFailed);
        r.Message.Should().Contain("НЕ очищены");
        OfflinePendingSalesStore.TryGetById(a.Id)!.Status.Should().Be(OfflineSaleEntry.PendingSync, "нет копии — нет очистки");
    }

    [Fact]
    public void Cashier_cannot_clear_even_with_pin()
    {
        var a = Add();
        var r = UnsentReceiptsCleaner.Clear(new[] { a.Id }, Cashier, pinVerified: true, "Айгуль", backupRoot: _root);
        r.Outcome.Should().Be(UnsentClearOutcome.NotAllowed);
        OfflinePendingSalesStore.TryGetById(a.Id)!.Status.Should().Be(OfflineSaleEntry.PendingSync);
        Directory.Exists(_root).Should().BeFalse("копия не делалась");
    }

    [Fact]
    public void Admin_without_verified_pin_cannot_clear()
    {
        var a = Add();
        var r = UnsentReceiptsCleaner.Clear(new[] { a.Id }, Admin, pinVerified: false, approvedBy: null, backupRoot: _root);
        r.Outcome.Should().Be(UnsentClearOutcome.PinRequired);
        OfflinePendingSalesStore.TryGetById(a.Id)!.Status.Should().Be(OfflineSaleEntry.PendingSync);
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public void Receipts_being_sent_right_now_are_not_touched()
    {
        var syncing = Add(OfflineSaleEntry.Syncing);
        var manual = Add();
        var free = Add();
        var done = Add(tune: e => e.CheckoutCompleted = true);

        var plan = UnsentReceiptsCleaner.Plan(new[] { syncing.Id, manual.Id, free.Id, done.Id }, key => key == "sale:" + manual.Id);
        plan.Ids.Should().BeEquivalentTo(new[] { free.Id }, "отправляемый автоматикой, отправляемый вручную и уже проведённый на сервере — пропускаются");
        plan.SkippedIds.Should().BeEquivalentTo(new[] { syncing.Id, manual.Id, done.Id });

        var r = UnsentReceiptsCleaner.Clear(new[] { syncing.Id, manual.Id, free.Id, done.Id }, Admin, true, "Владелец", _root, key => key == "sale:" + manual.Id);
        r.Cleared.Should().Be(1);
        r.Skipped.Should().Be(3);
        OfflinePendingSalesStore.TryGetById(syncing.Id)!.Status.Should().Be(OfflineSaleEntry.Syncing);
        OfflinePendingSalesStore.TryGetById(manual.Id)!.Status.Should().Be(OfflineSaleEntry.PendingSync);
        OfflinePendingSalesStore.TryGetById(done.Id)!.Status.Should().Be(OfflineSaleEntry.PendingSync);
        OfflinePendingSalesStore.TryGetById(free.Id)!.Status.Should().Be(OfflineSaleEntry.Synced);
    }

    [Fact]
    public void Only_selected_receipts_are_cleared_and_the_rest_stay_in_the_queue()
    {
        var a = Add();
        var b = Add();
        var r = UnsentReceiptsCleaner.Clear(new[] { a.Id }, Admin, true, "Владелец", _root);
        r.Cleared.Should().Be(1);
        OfflinePendingSalesStore.TryGetById(a.Id)!.Status.Should().Be(OfflineSaleEntry.Synced);
        OfflinePendingSalesStore.TryGetById(b.Id)!.Status.Should().Be(OfflineSaleEntry.PendingSync);
    }

    [Fact]
    public void Plan_reports_count_total_oldest_fiscal_and_maybe_on_server()
    {
        var old = Add(daysOld: 20);
        var fiscal = Add(daysOld: 5);
        var maybe = Add(OfflineSaleEntry.Failed, daysOld: 3, tune: e => e.CheckoutSubmittedAt = DateTimeOffset.Now.AddDays(-3));
        var store = new FiscalQueueStore(Path.Combine(_root, "fiscal-queue.json"));
        Directory.CreateDirectory(_root);
        var f = store.Enqueue(FiscalDocKind.Sale, fiscal.Id, "R-1", 60m);
        store.RecordResult(f.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "12345", "FM-1")));

        var plan = UnsentReceiptsCleaner.Plan(new[] { old.Id, fiscal.Id, maybe.Id }, fiscal: store);

        plan.Count.Should().Be(3);
        plan.Total.Should().Be(180m);
        plan.Oldest.Should().BeCloseTo(old.CreatedAt, TimeSpan.FromSeconds(1));
        plan.FiscalCount.Should().Be(1, "чек уже пробит фискально");
        plan.Items.Single(i => i.Fiscal).Id.Should().Be(fiscal.Id);
        plan.MaybeOnServerCount.Should().Be(1, "отправка уже начиналась");
    }

    [Fact]
    public void Clear_counts_fiscal_receipts_in_the_journal()
    {
        var a = Add();
        var store = new FiscalQueueStore(Path.Combine(_root, "fiscal-queue.json"));
        Directory.CreateDirectory(_root);
        var f = store.Enqueue(FiscalDocKind.Sale, a.Id, "R-2", 60m);
        store.RecordResult(f.Key, FiscalResult.Ok(new FiscalRequisites(FiscalStatus.Accepted, "777", "FM-2")));

        var r = UnsentReceiptsCleaner.Clear(new[] { a.Id }, Admin, true, "Владелец", _root, fiscal: store);

        r.Done.Should().BeTrue();
        _audit.Should().Contain(l => l.Contains("фискальных 1"));
        File.ReadAllText(Path.Combine(r.BackupFolder!, "receipts.csv")).Should().Contain(";yes;", "в копии отмечено «пробит фискально»");
    }

    [Fact]
    public void Csv_cells_that_look_like_formulas_are_neutralised()
    {
        var a = Add(OfflineSaleEntry.Failed, tune: e => e.LastError = "=HYPERLINK(\"http://x\")");
        var r = UnsentReceiptsCleaner.Clear(new[] { a.Id }, Admin, true, "Владелец", _root);
        r.Done.Should().BeTrue();
        File.ReadAllText(Path.Combine(r.BackupFolder!, "receipts.csv")).Should().NotContain(";=HYPERLINK").And.NotContain(";\"=HYPERLINK");
    }
}
