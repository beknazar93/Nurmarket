using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-06: живой тест Этапа 1, Б-1/Б-2 — сервер ceba67c на ЧАСТИЧНЫЙ возврат долгового чека отвечает 400 «method: Долг нельзя указывать в строках оплаты.», но возврат проводит.
/// Строки оплаты возврата касса не передаёт вовсе (тело: idempotency_key, cashbox_role, items, reason) — это зафиксировано здесь. Проверяется: распознавание этого отказа;
/// офлайн-возврат при автодосылке не уходит в «Требует проверки» (повтор тем же ключом, затем сверка по чеку); настоящий отказ по-прежнему идёт на проверку.</summary>
[Collection("ServerFeatures")]
public sealed class DebtReturnRefusalBodyTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private static readonly PosRefundLineRequest[] OneLine =
        [new PosRefundLineRequest { LineId = "line-1", Title = "Хлеб", Quantity = 1, OriginalQuantity = 2 }];

    [Fact]
    public async Task PartialReturn_BodyNeverCarriesPaymentLines_SoDebtCannotBeInThem()
    {
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);

        await sales.PosReturnSaleAsync("sale-d", OneLine, default, "key-1");
        await sales.PosReturnWithDetailsAsync("sale-d", OneLine, "брак упаковки", false, default, "key-2");

        var bodies = Requests(_fake, "POST", "/api/main/pos/sales/sale-d/return/").Select(BodyOf).ToList();
        bodies.Should().HaveCount(2);
        foreach (var body in bodies)
        {
            body.ContainsKey("payments").Should().BeFalse("долговую часть касса в строках оплаты не указывает — сервер собирает их сам");
            body.ContainsKey("payment").Should().BeFalse();
            body.ContainsKey("method").Should().BeFalse();
            body.Select(kv => kv.Key).Should().BeSubsetOf(["idempotency_key", "cashbox_role", "items", "reason"]);
        }
    }

    [Fact]
    public async Task PartialReturn_BodyCarriesOnlyTheReturnedQuantity_OneOfTwo()
    {
        // 2026-11-06 (Б-1, п. 2а): сервер на №2061 сделал canceled; касса при этом отправила ровно одну строку с количеством 1 (из 2) — лишнего не шлёт
        var api = await LoggedInAsync(_fake);

        await new SalesApiService(api).PosReturnSaleAsync("sale-q", OneLine, default, "key-q");

        var body = BodyOf(Requests(_fake, "POST", "/api/main/pos/sales/sale-q/return/").Single());
        var items = body["items"]!.AsArray();
        items.Should().HaveCount(1);
        items[0]!["sale_item_id"]!.GetValue<string>().Should().Be("line-1");
        double.Parse(items[0]!["quantity"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1d, "вернули 1 шт из 2");
    }

    [Fact]
    public void RefusalAnswer_ForgetsKey_WhenReceiptWasNotChanged_SoServerTextIsShownAndNextReturnIsNew()
    {
        // 2026-11-06 (Б-2): «400 + не записан» — окно вызывает PendingReturn.OnError(отказ): 4xx — отказ по существу, ключ забыт (следующий возврат — новая операция), исход не «неизвестен»
        var pending = new PendingIdempotencyKey(TimeSpan.FromMinutes(10));
        pending.Get("lines|sale-1|line-1:1");
        using var doc = JsonDocument.Parse("{\"method\":[\"Долг нельзя указывать в строках оплаты.\"]}");

        var unknown = pending.OnError(new ApiException("method: Долг нельзя указывать в строках оплаты.", 400, doc.RootElement.Clone()));

        unknown.Should().BeFalse();
        pending.PeekKey.Should().BeNull();
    }

    [Theory]
    [InlineData(400, "{\"method\":[\"Долг нельзя указывать в строках оплаты.\"]}", true)]
    [InlineData(400, "{\"method\":\"Долг нельзя указывать в строках оплаты.\"}", true)]
    [InlineData(422, "{\"detail\":\"method: Долг нельзя указывать в строках оплаты.\"}", true)]
    [InlineData(409, "{\"code\":\"refund_exceeds_paid\"}", false)]
    [InlineData(400, "{\"detail\":\"Смена закрыта\",\"code\":\"shift_closed\"}", false)]
    [InlineData(500, "{\"detail\":\"Долг нельзя указывать в строках оплаты.\"}", false)]
    public async Task ServerRefusal_IsRecognisedOnlyForTheDebtPaymentLineMessage(int status, string body, bool expected)
    {
        _fake.Json("POST", "/api/main/pos/sales/*/return/", status, body);
        var api = await LoggedInAsync(_fake);

        var act = async () => await new SalesApiService(api).PosReturnSaleAsync("sale-x", OneLine, default, "k");

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        OfflineRejection.IsDebtPaymentLineRefusal(ex).Should().Be(expected);
    }

    [Fact]
    public async Task OldFormatRefusal_ThenSameKeyRetryPasses_ServerRecordedResultIsReturned()
    {
        // сервер: первый запрос — 400 «Долг нельзя…» (но возврат проведён), повтор с тем же ключом — 200 с записанным итогом
        _fake.Json("POST", "/api/main/pos/sales/*/return/", 400, "{\"method\":[\"Долг нельзя указывать в строках оплаты.\"]}");
        var api = await LoggedInAsync(_fake);
        var sales = new SalesApiService(api);
        var pending = new PendingIdempotencyKey();
        var key = pending.Get("sale-7|line-1:1");

        var first = async () => await sales.PosReturnSaleAsync("sale-7", OneLine, default, key);
        var ex = (await first.Should().ThrowAsync<ApiException>()).Which;
        OfflineRejection.IsDebtPaymentLineRefusal(ex).Should().BeTrue();

        _fake.Json("POST", "/api/main/pos/sales/*/return/", 200, "{\"id\":\"ret-1\",\"replayed\":true}");
        var again = await sales.PosReturnSaleAsync("sale-7", OneLine, default, key);

        again.GetProperty("replayed").GetBoolean().Should().BeTrue();
        var keys = Requests(_fake, "POST", "/api/main/pos/sales/sale-7/return/").Select(r => BodyOf(r)["idempotency_key"]!.GetValue<string>()).ToList();
        keys.Should().Equal(key, key);
    }
}

/// <summary>Автодосылка офлайн-возврата долгового чека (OfflineOperationsReplayer): подставной сервер, очередь — во временном файле.</summary>
[Collection("Outbox")]
public sealed class DebtReturnReplayTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "nm-debtret-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _oldFile = OfflineOperationsStore.FilePath;
    private readonly Action<OfflineOperationEntry, string>? _oldNotifier = OfflineOperationsReplayer.OwnerNotifier;

    public DebtReturnReplayTests()
    {
        OfflineOperationsStore.FilePath = _file;
        OfflineOperationsReplayer.OwnerNotifier = (_, _) => { };
    }

    public void Dispose()
    {
        OfflineOperationsStore.FilePath = _oldFile;
        OfflineOperationsReplayer.OwnerNotifier = _oldNotifier;
        try { File.Delete(_file); } catch { /* временный файл */ }
    }

    private static ApiException DebtRefusal()
    {
        using var doc = JsonDocument.Parse("{\"method\":[\"Долг нельзя указывать в строках оплаты.\"]}");
        return new ApiException("method: Долг нельзя указывать в строках оплаты.", 400, doc.RootElement.Clone());
    }

    private static JsonElement Sale(double returnable)
    {
        using var doc = JsonDocument.Parse("{\"id\":\"sale-1\",\"status\":\"paid\",\"items\":[{\"id\":\"line-1\",\"product_id\":\"p1\",\"name\":\"Хлеб\",\"quantity\":\"2\",\"returnable_qty\":"
            + returnable.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}]}");
        return doc.RootElement.Clone();
    }

    private sealed class FakeOps(Func<int, JsonElement> returnCall, Func<int, JsonElement> saleCall) : IOfflineOpsApi
    {
        public readonly List<string> Keys = [];
        private int _sales;

        public Task<JsonElement> GetSaleAsync(string saleId, CancellationToken ct) => Task.FromResult(saleCall(++_sales));

        public Task<JsonElement> ReturnSaleAsync(string saleId, IReadOnlyList<PosRefundLineRequest>? lines, string idempotencyKey, string? reason, CancellationToken ct)
        {
            Keys.Add(idempotencyKey);
            return Task.FromResult(returnCall(Keys.Count));
        }

        public Task<PayDebtResult> PayDebtAsync(string clientId, double amount, string method, string? shiftId, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static OfflineOperationEntry NewRefund() => new()
    {
        Id = Guid.NewGuid().ToString(), Kind = OfflineOperationEntry.KindRefund, SaleRef = "sale-1", SaleRefIsLocal = false, Amount = 15m, DebtReduced = 15m,
        Lines = [new OfflineRefundLine { Key = "0", ProductId = "p1", Name = "Хлеб", Quantity = 1, OriginalQuantity = 2, UnitPrice = 15m, Amount = 15m }],
    };

    private static JsonElement Ok()
    {
        using var doc = JsonDocument.Parse("{\"id\":\"ret-1\"}");
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ServerRefusesWithDebtLineMessage_SameKeyRetryPasses_OperationIsSentFirstTime()
    {
        var op = NewRefund();
        OfflineOperationsStore.Add(op);
        var ops = new FakeOps(n => n == 1 ? throw DebtRefusal() : Ok(), _ => Sale(2));

        var outcome = await OfflineOperationsReplayer.RunAsync(ops, default);

        outcome.Sent.Should().Be(1);
        outcome.Review.Should().Be(0, "это не отказ по существу: сервер возврат провёл");
        ops.Keys.Should().Equal(op.Id, op.Id);
        var saved = OfflineOperationsStore.Get(op.Id)!;
        saved.Status.Should().Be(OfflineOperationEntry.Synced);
        saved.ReviewNeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ServerKeepsRefusing_ButReceiptShowsTheReturn_OperationIsSentWithoutDuplicate()
    {
        var op = NewRefund();
        OfflineOperationsStore.Add(op);
        // чек: до возврата 2 шт, после ошибки сервера (повторная проверка) — 1 шт: возврат проведён
        var ops = new FakeOps(_ => throw DebtRefusal(), n => n == 1 ? Sale(2) : Sale(1));

        var outcome = await OfflineOperationsReplayer.RunAsync(ops, default);

        outcome.Sent.Should().Be(1);
        outcome.Review.Should().Be(0);
        ops.Keys.Should().HaveCount(2, "второй раз — тот же ключ, третьего запроса возврата нет");
        ops.Keys.Distinct().Should().ContainSingle();
        OfflineOperationsStore.Get(op.Id)!.Status.Should().Be(OfflineOperationEntry.Synced);
    }

    [Fact]
    public async Task ServerKeepsRefusing_AndReceiptIsUnchanged_OperationGoesToReview()
    {
        var op = NewRefund();
        OfflineOperationsStore.Add(op);
        var ops = new FakeOps(_ => throw DebtRefusal(), _ => Sale(2));

        var outcome = await OfflineOperationsReplayer.RunAsync(ops, default);

        outcome.Sent.Should().Be(0);
        outcome.Review.Should().Be(1, "чек не изменился — возврат не проведён, решает человек");
        var saved = OfflineOperationsStore.Get(op.Id)!;
        saved.Status.Should().Be(OfflineOperationEntry.Failed);
        saved.ReviewNeeded.Should().BeTrue();
    }

    private static JsonElement ServerSale(string status, string returnStatus, double qty, string total, string debtRemaining)
    {
        using var doc = JsonDocument.Parse("{\"id\":\"sale-1\",\"status\":\"" + status + "\",\"return_status\":\"" + returnStatus + "\",\"total\":\"" + total + "\",\"debt_remaining\":\"" + debtRemaining
            + "\",\"items\":[{\"id\":\"line-1\",\"product_id\":\"p1\",\"name\":\"Хлеб\",\"quantity\":\"" + qty.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}]}");
        return doc.RootElement.Clone();
    }

    private static async Task<(List<string> Notices, List<string?> DebtEvents)> RunWithNoticesAsync(OfflineOperationEntry op, IOfflineOpsApi ops)
    {
        var notices = new List<string>();
        var debtEvents = new List<string?>();
        void OnNotice(string text, bool warning) => notices.Add(text);
        void OnDebt(string? clientId, string? key) => debtEvents.Add(clientId);
        ShiftStateService.ShiftNotice += OnNotice;
        PosDataEvents.ClientDebtChanged += OnDebt;
        try
        {
            await OfflineOperationsReplayer.RunAsync(ops, default);
        }
        finally
        {
            ShiftStateService.ShiftNotice -= OnNotice;
            PosDataEvents.ClientDebtChanged -= OnDebt;
        }
        return (notices, debtEvents);
    }

    [Fact]
    public async Task PartialReturn_ServerTurnsReceiptIntoCanceledWithZeroDebt_CashierGetsWarningAndDebtIsReread()
    {
        // №2061: вернули 1 из 2 (долг должен стать 7), сервер записал canceled / full и долг 0
        var op = NewRefund();
        op.ClientId = "client-1";
        OfflineOperationsStore.Add(op);
        var ops = new FakeOps(_ => Ok(), n => n == 1 ? ServerSale("debt", "none", 2, "14.00", "14.00") : ServerSale("canceled", "full", 1, "7.00", "0.00"));

        var (notices, debtEvents) = await RunWithNoticesAsync(op, ops);

        OfflineOperationsStore.Get(op.Id)!.Status.Should().Be(OfflineOperationEntry.Synced);
        notices.Should().ContainSingle().Which.Should().Be(ReturnSnapshot.DebtRecalcWarningText);
        debtEvents.Should().Contain("client-1", "долг клиента перечитывается с сервера");
    }

    [Fact]
    public async Task PartialReturn_ServerRecalculatesDebtCorrectly_NoWarning()
    {
        var op = NewRefund();
        op.ClientId = "client-1";
        OfflineOperationsStore.Add(op);
        var ops = new FakeOps(_ => Ok(), n => n == 1 ? ServerSale("debt", "none", 2, "30.00", "30.00") : ServerSale("partially_returned", "partial", 1, "15.00", "15.00"));

        var (notices, _) = await RunWithNoticesAsync(op, ops);

        notices.Should().BeEmpty();
    }

    [Fact]
    public async Task OrdinaryBusinessRefusal_StillGoesToReview_WithoutExtraRetry()
    {
        var op = NewRefund();
        OfflineOperationsStore.Add(op);
        using var doc = JsonDocument.Parse("{\"code\":\"refund_exceeds_paid\"}");
        var ops = new FakeOps(_ => throw new ApiException("refund_exceeds_paid", 409, doc.RootElement.Clone()), _ => Sale(2));

        var outcome = await OfflineOperationsReplayer.RunAsync(ops, default);

        outcome.Review.Should().Be(1);
        ops.Keys.Should().HaveCount(1, "обычный отказ по существу повторять не нужно");
    }
}

/// <summary>2026-11-06 (живой тест Этапа 1, Б-2, чек №2060): сервер ответил 400 «Долг нельзя…», но ПРОВЁЛ частичный возврат (статус partially_returned, строка 2 → 1, итог 14 → 7, долг остался 14).
/// Прежняя проверка сравнивала «можно вернуть» с количеством к возврату (1), а не с тем, что было (2), и говорила «не проведён». Теперь — сравнение со снимком чека ДО запроса.</summary>
public sealed class ReturnProbeTests
{
    private static JsonElement Sale(string status, string returnStatus, double qty, string total, string debt, double? returned = null, double? returnable = null)
    {
        var extra = (returned is null ? "" : ",\"returned_qty\":" + returned.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    + (returnable is null ? "" : ",\"returnable_qty\":" + returnable.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var doc = JsonDocument.Parse("{\"id\":\"sale-1\",\"status\":\"" + status + "\",\"return_status\":\"" + returnStatus + "\",\"total\":\"" + total + "\",\"debt_remaining\":\"" + debt
            + "\",\"items\":[{\"id\":\"line-1\",\"product_id\":\"p1\",\"name\":\"Крышка\",\"quantity\":\"" + qty.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"" + extra + "}]}");
        return doc.RootElement.Clone();
    }

    private static JsonElement Before() => Sale("debt", "none", 2, "14.00", "14.00");

    /// <summary>Как у сервера в живом тесте: количество в строке уменьшилось (2 → 1), итог 7, статус partially_returned, долг не уменьшился.</summary>
    private static JsonElement ServerApplied() => Sale("partially_returned", "partial", 1, "7.00", "14.00");

    private static readonly IReadOnlyDictionary<string, double> BuggyBefore = new Dictionary<string, double> { ["line-1"] = 1 }; // как было: количество К ВОЗВРАТУ, а не «было к возврату»

    private sealed class Waits
    {
        public readonly List<TimeSpan> Calls = [];
        public Task Wait(TimeSpan d) { Calls.Add(d); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Refused400_ReceiptChangedAgainstSnapshot_IsApplied_EvenWhenOldQuantityCheckWouldMissIt()
    {
        var before = ReturnSnapshot.From(Before());
        var waits = new Waits();

        var result = await ReturnProbe.RunAsync(_ => Task.FromResult(ServerApplied()), before, BuggyBefore, null, waits.Wait);

        result.Should().Be(ReturnProbeResult.Applied);
        ReturnProbe.LinesReturnedSince(ServerApplied(), BuggyBefore).Should().BeFalse("старая проверка по количеству к возврату промахивалась — это и был баг №2060");
        waits.Calls.Should().BeEmpty("изменение видно с первого чтения");
    }

    [Fact]
    public async Task Refused400_ReceiptNeverChanges_IsNotApplied_AfterReadsWithGrowingPausesUpTo15Seconds()
    {
        var before = ReturnSnapshot.From(Before());
        var waits = new Waits();

        var result = await ReturnProbe.RunAsync(_ => Task.FromResult(Before()), before, new Dictionary<string, double> { ["line-1"] = 2 }, null, waits.Wait);

        result.Should().Be(ReturnProbeResult.NotApplied);
        waits.Calls.Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5));
        waits.Calls.Sum(w => w.TotalSeconds).Should().Be(15);
    }

    [Fact]
    public async Task ServerWritesLate_ThirdRead_IsApplied()
    {
        var before = ReturnSnapshot.From(Before());
        var waits = new Waits();
        var reads = 0;

        var result = await ReturnProbe.RunAsync(_ => Task.FromResult(++reads >= 3 ? ServerApplied() : Before()), before, new Dictionary<string, double> { ["line-1"] = 2 }, null, waits.Wait);

        result.Should().Be(ReturnProbeResult.Applied);
        reads.Should().Be(3);
        waits.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReceiptCannotBeRead_IsUnknown_NotAClaimThatReturnWasNotMade()
    {
        var before = ReturnSnapshot.From(Before());

        var result = await ReturnProbe.RunAsync(_ => throw new HttpRequestException("нет связи"), before, BuggyBefore, null, new Waits().Wait);

        result.Should().Be(ReturnProbeResult.Unknown);
    }

    [Fact]
    public async Task LastReadFailsAfterUnchangedReads_IsUnknown()
    {
        var before = ReturnSnapshot.From(Before());
        var reads = 0;

        var result = await ReturnProbe.RunAsync(_ => ++reads < 6 ? Task.FromResult(Before()) : throw new HttpRequestException("обрыв"), before, new Dictionary<string, double> { ["line-1"] = 2 }, null, new Waits().Wait);

        result.Should().Be(ReturnProbeResult.Unknown, "сервер мог записать возврат, пока чек не читался");
    }

    [Fact]
    public void StatusUnchanged_ButReturnedQuantityGrew_IsChanged()
    {
        var before = ReturnSnapshot.From(Sale("partially_returned", "partial", 3, "30.00", "0.00", returned: 1));
        var after = ReturnSnapshot.From(Sale("partially_returned", "partial", 3, "30.00", "0.00", returned: 2));

        after.ChangedSince(before).Should().BeTrue();
        ReturnSnapshot.From(Sale("partially_returned", "partial", 3, "30.00", "0.00", returned: 1)).ChangedSince(before).Should().BeFalse();
    }

    [Fact]
    public void StatusChangedOnly_IsChanged()
    {
        var before = ReturnSnapshot.From(Before());

        ReturnSnapshot.From(Sale("partially_returned", "partial", 2, "14.00", "14.00")).ChangedSince(before).Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "partially_returned", "partial", "1", "14.00", true)]   // №2060: долг был 14, касса уменьшила на 7, остался 14
    [InlineData(true, "partially_returned", "partial", "1", "7.00", false)]   // сервер посчитал верно
    [InlineData(true, "canceled", "full", "1", "0.00", true)]                 // №2061: частичный возврат записан как полный
    [InlineData(false, "canceled", "full", "2", "14.00", false)]              // полный возврат — старый остаток у сервера нормален
    public void DebtRecalcSuspicious_MatchesLiveCases(bool partial, string status, string returnStatus, string qty, string debt, bool expected)
    {
        var before = ReturnSnapshot.From(Before());
        var after = ReturnSnapshot.From(Sale(status, returnStatus, double.Parse(qty, System.Globalization.CultureInfo.InvariantCulture), "7.00", debt));

        ReturnSnapshot.DebtRecalcSuspicious(before, after, partial, 7m).Should().Be(expected);
    }
}
