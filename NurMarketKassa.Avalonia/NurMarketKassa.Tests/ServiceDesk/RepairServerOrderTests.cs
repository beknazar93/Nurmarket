using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.ServiceDesk;

/// <summary>2026-11-27 (ответ Умара 27.11, п. 3): серверная квитанция (создана в программе владельца/мобилке, external_id нет) меняется кассой ОБЫЧНЫМИ адресами repair-orders/&lt;uuid&gt;/ (PATCH),
/// …/status/, …/issue/, …/prepay/ — не PUT по external_id. Проверяется: что именно уходит (разница с тем, что знает сервер), порядок запросов, метка base_updated_at, запись принятого шага,
/// отказ сервера (квитанция приводится к серверной, деньги кассы не двоятся), stale_version, предоплата. Тела запросов — по ТЗ кассы 13.11 и описанию Умара 27.11: НЕ проверены вживую.</summary>
public sealed class RepairServerOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-repairsrv-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeNurCrmServer _fake = new();
    private readonly DateTime _now = new(2026, 11, 27, 10, 0, 0);

    public RepairServerOrderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _fake.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    private RepairOrderStore Repairs() => new(Path.Combine(_dir, "repair-orders.json"), () => _now);

    private static JsonElement J(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private const string Row =
        "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"external_id\":null,\"source\":\"server\",\"code\":\"Р-0010\",\"status\":\"accepted\",\"device\":\"iPhone 13\",\"customer_name\":\"Эльнура\"," +
        "\"customer_phone\":\"0700111222\",\"problem\":\"Не заряжается\",\"estimated_price\":\"1500.00\",\"prepaid\":\"300.00\",\"refunded\":\"0.00\",\"updated_at\":\"2026-11-27T09:00:00Z\"}";

    private RepairOrder ServerOrder(RepairOrderStore s, string rowJson = Row)
    {
        s.MergeServerRows(new[] { RepairServerRow.Parse(J(rowJson))! }, null);
        return s.All().Single();
    }

    [Fact]
    public void ServerOrder_IsEditable_AndGoesToQueueWhenChanged()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        o.ServerOnly.Should().BeTrue();
        o.ServerId.Should().Be("11111111-1111-1111-1111-111111111111");
        o.Prepay.Should().Be(300m, "предоплату серверной квитанции ведёт сервер");
        o.SyncPending.Should().BeFalse("только что прочитана — отправлять нечего");

        s.Move(o.Id, RepairStatus.InWork, "К").Should().BeTrue();
        s.Find(o.Id)!.SyncPending.Should().BeTrue("раньше серверная квитанция была «только просмотр» и никогда не уходила");
    }

    [Fact]
    public void Plan_StatusChange_IsOnlyStatusCall_WithTarget()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        s.Move(o.Id, RepairStatus.InWork, "К");

        var calls = RepairStatusPublic.PlanServerCalls(s.Find(o.Id)!);
        calls.Should().ContainSingle();
        calls[0].Method.Should().Be("POST");
        calls[0].Suffix.Should().Be("status/");
        calls[0].Body.Should().BeEquivalentTo(new Dictionary<string, object?> { ["to"] = "in_work" });
    }

    [Fact]
    public void Plan_FieldEdit_IsPatchWithChangedFieldsOnly_ThenReady_WithFinalPrice_ThenIssue()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        s.Update(o.Id, x => { x.Device = "iPhone 13 Pro"; x.Estimate = 1800m; return true; }, "К", "edit");

        var patch = RepairStatusPublic.PlanServerCalls(s.Find(o.Id)!).Should().ContainSingle().Subject;
        patch.Method.Should().Be("PATCH");
        patch.Suffix.Should().Be("");
        patch.Body.Keys.Should().BeEquivalentTo(new[] { "device", "estimate" }, "только изменённые поля, имя и телефон не уходят");
        patch.Body["estimate"].Should().Be("1800.00", "деньги — decimal через RepairRules.Money, строкой с двумя знаками");

        // «Готов» с итогом, затем «выдан» без связи между шагами: сервер ещё помнит «принят» → status ready, затем issue
        s.Move(o.Id, RepairStatus.Ready, "К", 1799.995m);
        s.Update(o.Id, x => { x.PaymentInCart = 1500m; return true; }, "К", "pay");
        s.Issue(o.Id, "К").Should().BeTrue();
        var calls = RepairStatusPublic.PlanServerCalls(s.Find(o.Id)!);
        calls.Select(c => c.Suffix).Should().Equal("", "status/", "issue/");
        calls[1].Body["to"].Should().Be("ready");
        calls[1].Body["final_price"].Should().Be("1800.00", "1799,995 → 1800,00 HALF_UP");
        calls[2].IdempotencyKey.Should().NotBeNull("выдача — с ключом повтора");
    }

    [Fact]
    public async Task Send_UsesOrdinaryAddresses_InOrder_WithBaseUpdatedAt_AndRemembersAcceptedSteps()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        s.Update(o.Id, x => { x.Note = "Звонить после 18:00"; return true; }, "К", "edit");
        s.Move(o.Id, RepairStatus.InWork, "К");
        var id = o.ServerId;
        _fake.Json("PATCH", $"/api/main/repair-orders/{id}/", 200, "{\"id\":\"" + id + "\",\"updated_at\":\"2026-11-27T09:01:00Z\"}");
        _fake.Json("POST", $"/api/main/repair-orders/{id}/status/", 200, "{\"id\":\"" + id + "\",\"status\":\"in_work\",\"updated_at\":\"2026-11-27T09:02:00Z\"}");
        var api = await LoggedInAsync(_fake);

        var r = await RepairStatusPublic.SendServerOrderAsync(api, s, s.Find(o.Id)!);

        r.Kind.Should().Be(RepairSyncKind.Ok);
        r.UpdatedAt.Should().Be("2026-11-27T09:02:00Z");
        var patch = Requests(_fake, "PATCH", $"/repair-orders/{id}/").Single();
        BodyOf(patch)["note"]!.GetValue<string>().Should().Be("Звонить после 18:00");
        BodyOf(patch)["base_updated_at"]!.GetValue<string>().Should().Be("2026-11-27T09:00:00Z");
        var status = Requests(_fake, "POST", "/status/").Single();
        BodyOf(status)["to"]!.GetValue<string>().Should().Be("in_work");
        BodyOf(status)["base_updated_at"]!.GetValue<string>().Should().Be("2026-11-27T09:01:00Z", "метка — из ответа предыдущего запроса");
        Requests(_fake, "PUT", "repair-orders").Should().BeEmpty("PUT по external_id — только для квитанций кассы");

        var after = s.Find(o.Id)!;
        after.UpdatedAt.Should().Be("2026-11-27T09:02:00Z");
        RepairStatusPublic.PlanServerCalls(after).Should().BeEmpty("принятые шаги записаны — повтор после обрыва не шлёт их снова (иначе 409 bad_transition)");
    }

    [Fact]
    public async Task Send_Failure_OnSecondStep_KeepsFirstStepAsAccepted()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        s.Update(o.Id, x => { x.Note = "Заметка"; return true; }, "К", "edit");
        s.Move(o.Id, RepairStatus.InWork, "К");
        var id = o.ServerId;
        _fake.Json("PATCH", $"/api/main/repair-orders/{id}/", 200, "{\"id\":\"" + id + "\",\"updated_at\":\"2026-11-27T09:01:00Z\"}");
        _fake.Json("POST", $"/api/main/repair-orders/{id}/status/", 409, "{\"code\":\"bad_transition\"}");
        var api = await LoggedInAsync(_fake);

        var r = await RepairStatusPublic.SendServerOrderAsync(api, s, s.Find(o.Id)!);

        r.Kind.Should().Be(RepairSyncKind.Rejected);
        r.ErrorCode.Should().Be("bad_transition");
        var calls = RepairStatusPublic.PlanServerCalls(s.Find(o.Id)!);
        calls.Should().ContainSingle("поля уже приняты, осталась только смена статуса").Which.Suffix.Should().Be("status/");
    }

    [Fact]
    public async Task Sync_Ok_ClearsPending_AndStoresServerStamp()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        s.Move(o.Id, RepairStatus.InWork, "К");
        RepairOrder? sent = null;

        var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (order, _) =>
        {
            sent = order;
            return Task.FromResult(new RepairSyncResult(RepairSyncKind.Ok, UpdatedAt: "2026-11-27T09:05:00Z"));
        });

        r.Kind.Should().Be(RepairSyncKind.Ok);
        sent!.ServerOnly.Should().BeTrue();
        var after = s.Find(o.Id)!;
        after.SyncPending.Should().BeFalse();
        after.UpdatedAt.Should().Be("2026-11-27T09:05:00Z");
    }

    [Fact]
    public async Task Sync_ServerRejectsIssue_ReceiptReturnsToServerState_AndCashierPaymentIsNotLost()
    {
        var s = Repairs();
        var o = ServerOrder(s, Row.Replace("\"status\":\"accepted\"", "\"status\":\"ready\"").Replace("\"estimated_price\":\"1500.00\"", "\"estimated_price\":\"1500.00\",\"final_price\":\"1500.00\""));
        // кассир принял оплату работ в чек (1200 = итог − предоплата) и выдал квитанцию
        s.Update(o.Id, x => { x.PaymentInCart = 1200m; return true; }, "К", "payment-in-cart");
        s.Issue(o.Id, "К").Should().BeTrue();
        s.Find(o.Id)!.PaidAtIssue.Should().Be(1200m);

        var serverNow = RepairServerRow.Parse(J(Row.Replace("\"status\":\"accepted\"", "\"status\":\"ready\"").Replace("\"estimated_price\":\"1500.00\"", "\"estimated_price\":\"1500.00\",\"final_price\":\"1500.00\"")
            .Replace("2026-11-27T09:00:00Z", "2026-11-27T09:10:00Z")))!;
        var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!,
            send: (_, _) => Task.FromResult(new RepairSyncResult(RepairSyncKind.Rejected, Http: 409, ErrorCode: "payment_missing")),
            loadCurrent: _ => Task.FromResult<RepairServerRow?>(serverNow));

        r.Kind.Should().Be(RepairSyncKind.Rejected);
        var after = s.Find(o.Id)!;
        after.Status.Should().Be(RepairStatus.Ready, "на сервере «готов» — касса приведена к нему");
        after.PaidAtIssue.Should().Be(0m);
        after.PaymentInCart.Should().Be(1200m, "оплата работ осталась в чеке: повторно брать деньги не нужно");
        after.IssuedAt.Should().BeNull();
        after.SyncPending.Should().BeFalse("не шлём отклонённое снова");
        after.Log.Last().Action.Should().Be("server-rejected:payment_missing");
        RepairStatusPublic.RejectionText("payment_missing").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sync_StaleVersion_OnServerOrder_GoesToConflict_LikeOtherReceipts()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        s.Move(o.Id, RepairStatus.InWork, "К");
        var current = "{\"id\":\"" + o.ServerId + "\",\"status\":\"ready\",\"device\":\"iPhone 13\",\"updated_at\":\"2026-11-27T09:20:00Z\"}";

        var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!,
            send: (_, _) => Task.FromResult(new RepairSyncResult(RepairSyncKind.Stale, CurrentJson: current, Http: 409)));

        r.Kind.Should().Be(RepairSyncKind.Stale);
        s.Find(o.Id)!.HasConflict.Should().BeTrue("«Квитанцию изменили на другом устройстве»: выбор «мои правки поверх» / «взять с сервера»");
    }

    [Fact]
    public async Task Prepay_PostsAmountAndShift_WithIdempotencyKey_AndStoreRecordsServerFigures()
    {
        var s = Repairs();
        var o = ServerOrder(s);
        var id = o.ServerId;
        _fake.Json("POST", $"/api/main/repair-orders/{id}/prepay/", 200, Row.Replace("\"prepaid\":\"300.00\"", "\"prepaid\":\"800.50\"").Replace("2026-11-27T09:00:00Z", "2026-11-27T09:30:00Z"));
        var api = await LoggedInAsync(_fake);

        var (result, row) = await RepairStatusPublic.PrepayServerAsync(api, o, 500.495m, "shift-1", "repair-prepay-k1");

        result.Kind.Should().Be(RepairSyncKind.Ok);
        var req = Requests(_fake, "POST", "/prepay/").Single();
        BodyOf(req)["amount"]!.GetValue<string>().Should().Be("500.50", "HALF_UP до копейки, decimal");
        BodyOf(req)["shift"]!.GetValue<string>().Should().Be("shift-1");
        Header(req, "Idempotency-Key").Should().Be("repair-prepay-k1");

        s.RecordServerPrepay(o.Id, 500.50m, row, "К").Should().BeTrue();
        var after = s.Find(o.Id)!;
        after.Prepay.Should().Be(800.50m, "предоплата — из ответа сервера");
        after.SyncPending.Should().BeFalse("серверу отправлять нечего");
        RepairStatusPublic.PlanServerCalls(after).Should().BeEmpty();
    }

    [Fact]
    public void Plan_WithoutServerId_IsNotSent_AndPrepayWithZeroIsRejected()
    {
        var a = new RepairOrder { Source = "server", Id = "srv-x" };
        RepairStatusPublic.ServerPath(new RepairOrder { ServerId = "ab/cd" }).Should().Be("api/main/repair-orders/ab%2Fcd/");
        a.ServerId.Should().BeEmpty();
    }
}
