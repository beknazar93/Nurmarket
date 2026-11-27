using System.Net.Http;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.ServiceDesk;

/// <summary>2026-11-13: «Ремонт и сервис» и «Trade-in / скупка» — статусы, суммы (decimal), предоплата и переплата, зачёт trade-in в чек, защита денег (выдача без оплаты, отмена без возврата),
/// склад (закупка = выкуп), печать, сообщение клиенту, доступ по ключу и серверу, реестр данных компании. Файлы — во временной папке (настоящие данные кассы не трогаются).</summary>
public sealed class ServiceDeskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-servicedesk-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTime _now = new(2026, 11, 13, 10, 0, 0);
    private const string ValidImei = "490154203237518";

    public ServiceDeskTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    private RepairOrderStore Repairs() => new(Path.Combine(_dir, "repair-orders.json"), () => _now);
    private TradeInStore Trades() => new(Path.Combine(_dir, "trade-in-records.json"), () => _now);

    private static RepairOrder Draft(decimal estimate = 0m, decimal prepay = 0m) => new()
    {
        ClientName = "Айгуль", ClientPhone = "+996 700 123 456", Device = "Samsung A54", Serial = ValidImei, Fault = "Не включается",
        Equipment = "Зарядка", Estimate = estimate, Prepay = prepay,
    };

    private static TradeInRecord Buyback(decimal amount = 10000m) => new()
    {
        ClientName = "Бакыт", ClientPhone = "0700123456", ClientDocument = "ID 1234567, МКК 1234, 01.01.2020", Device = "iPhone 11", Serial = ValidImei,
        Condition = "Царапины", Amount = amount, Appraisal = amount,
    };

    // ───────────────────────── ремонт ─────────────────────────

    [Fact]
    public void Repair_Numbers_AreSequential_AndCodeIsStable()
    {
        var s = Repairs();
        var a = s.Create(Draft(), "Кассир");
        var b = s.Create(Draft(), "Кассир");
        a.Number.Should().Be(1);
        b.Number.Should().Be(2);
        RepairRules.Code(b).Should().Be("Р-0002");
        s.All().Select(o => o.Number).Should().Equal(2, 1);
        s.Find(a.Id)!.Status.Should().Be(RepairStatus.Accepted);
        s.Find(a.Id)!.AcceptedBy.Should().Be("Кассир");
    }

    [Fact]
    public void Repair_Statuses_FollowTheOrder_AndFinalOnesAreFinal()
    {
        var s = Repairs();
        var o = s.Create(Draft(estimate: 1500m), "К");
        s.Move(o.Id, RepairStatus.InWork, "К").Should().BeTrue();
        s.Move(o.Id, RepairStatus.Accepted, "К").Should().BeFalse("назад в «Принят» нельзя");
        s.Move(o.Id, RepairStatus.Ready, "К", 1700m).Should().BeTrue();
        var ready = s.Find(o.Id)!;
        ready.FinalPrice.Should().Be(1700m);
        ready.ReadyAt.Should().NotBeNull();
        s.Move(o.Id, RepairStatus.Issued, "К").Should().BeFalse("выдача — только отдельным методом с деньгами");
        RepairRules.CanMove(RepairStatus.Issued, RepairStatus.InWork).Should().BeFalse();
        RepairRules.CanMove(RepairStatus.Cancelled, RepairStatus.Ready).Should().BeFalse();
        RepairRules.CanMove(RepairStatus.Ready, RepairStatus.InWork).Should().BeTrue("вернули в работу");
    }

    [Fact]
    public void Repair_ReadyWithoutPrice_UsesEstimate()
    {
        var s = Repairs();
        var o = s.Create(Draft(estimate: 800m), "К");
        s.Move(o.Id, RepairStatus.Ready, "К").Should().BeTrue();
        s.Find(o.Id)!.FinalPrice.Should().Be(800m);
    }

    [Theory]
    [InlineData(1700, 500, 1200, 0)]
    [InlineData(1000, 1000, 0, 0)]
    [InlineData(1000, 1500, 0, 500)]
    [InlineData(0, 300, 0, 300)]
    [InlineData(1000.555, 0.005, 1000.55, 0)]
    public void Repair_AmountDueAndOverpaid_UseDecimalMath(double final, double prepay, double due, double over)
    {
        RepairRules.AmountDue((decimal)final, (decimal)prepay).Should().Be((decimal)due);
        RepairRules.Overpaid((decimal)final, (decimal)prepay).Should().Be((decimal)over);
    }

    [Fact]
    public void Repair_Issue_NeedsPaymentLine_ThenIsFinal()
    {
        var s = Repairs();
        var o = s.Create(Draft(), "К");
        s.Update(o.Id, x => { x.Prepay = 500m; return true; }, "К", "prepay");
        s.Move(o.Id, RepairStatus.Ready, "К", 1700m);

        s.Issue(o.Id, "К").Should().BeFalse("доплата 1200 ещё не проведена продажей услуги");
        s.MarkPaymentInCart(o.Id, 1200m, "К").Should().BeTrue();
        s.Cancel(o.Id, "К").Should().BeFalse("пока строка оплаты в чеке и предоплата не возвращена — отмена запрещена");
        s.Issue(o.Id, "К").Should().BeTrue();

        var done = s.Find(o.Id)!;
        done.Status.Should().Be(RepairStatus.Issued);
        done.PaidAtIssue.Should().Be(1200m);
        done.PaymentInCart.Should().Be(0m);
        done.IssuedAt.Should().NotBeNull();
        s.Move(o.Id, RepairStatus.InWork, "К").Should().BeFalse("выданное не меняется");
        s.Issue(o.Id, "К").Should().BeFalse("повторная выдача невозможна");
    }

    [Fact]
    public void Repair_Issue_WithOverpaidPrepay_RequiresRefundFirst()
    {
        var s = Repairs();
        var o = s.Create(Draft(), "К");
        s.Update(o.Id, x => { x.Prepay = 1500m; return true; }, "К", "prepay");
        s.Move(o.Id, RepairStatus.Ready, "К", 1000m);

        s.Issue(o.Id, "К").Should().BeFalse("переплата 500 ещё не возвращена");
        s.RecordRefund(o.Id, 500m, "К").Should().BeTrue();
        s.RecordRefund(o.Id, 1100m, "К").Should().BeFalse("нельзя вернуть больше предоплаты");
        s.Issue(o.Id, "К").Should().BeTrue();
        s.Find(o.Id)!.PaidAtIssue.Should().Be(0m);
    }

    [Fact]
    public void Repair_Cancel_RequiresPrepayRefund()
    {
        var s = Repairs();
        var o = s.Create(Draft(), "К");
        s.Update(o.Id, x => { x.Prepay = 300m; return true; }, "К", "prepay");
        RepairRules.PrepayToRefund(s.Find(o.Id)!).Should().Be(300m);
        s.Cancel(o.Id, "К").Should().BeFalse("предоплата 300 ещё у нас");
        s.RecordRefund(o.Id, 300m, "К");
        s.Cancel(o.Id, "К").Should().BeTrue();
        s.Find(o.Id)!.Status.Should().Be(RepairStatus.Cancelled);
    }

    [Fact]
    public void Repair_PaymentLineCanBeReleased_AndFinalPriceLockedWhileInCart()
    {
        var s = Repairs();
        var o = s.Create(Draft(), "К");
        s.Move(o.Id, RepairStatus.Ready, "К", 900m);
        s.MarkPaymentInCart(o.Id, 900m, "К");
        s.SetFinalPrice(o.Id, 100m, "К").Should().BeFalse("строка уже в чеке");
        s.CancelPaymentInCart(o.Id, "К").Should().BeTrue();
        s.SetFinalPrice(o.Id, 950m, "К").Should().BeTrue();
        s.Find(o.Id)!.FinalPrice.Should().Be(950m);
    }

    [Fact]
    public void Repair_Validation_ChecksRequiredFieldsAndImei()
    {
        RepairRules.ValidateNew(Draft()).Should().BeEmpty();
        RepairRules.ValidateNew(new RepairOrder()).Should().HaveCountGreaterThanOrEqualTo(4);
        var badImei = Draft();
        badImei.Serial = "490154203237519";
        RepairRules.ValidateNew(badImei).Should().ContainSingle(p => p.Contains("IMEI"));
        var noSerial = Draft();
        noSerial.Serial = "";
        RepairRules.ValidateNew(noSerial).Should().BeEmpty("серийный номер не обязателен (обувь, часы)");
        RepairRules.ValidateNew(Draft(estimate: 100m, prepay: 200m)).Should().ContainSingle(p => p.Contains("Предоплата больше"));
    }

    [Theory]
    [InlineData("", 0, true)]
    [InlineData("1 500,50", 1500.50, true)]
    [InlineData("1500.5", 1500.5, true)]
    [InlineData("abc", 0, false)]
    [InlineData("-5", 0, false)]
    public void ParseMoney_AcceptsSpacesCommaAndDot(string text, double expected, bool ok)
    {
        var parsed = RepairRules.TryParseMoney(text);
        if (ok)
            parsed.Should().Be((decimal)expected);
        else
            parsed.Should().BeNull();
    }

    [Fact]
    public void Repair_Search_FindsByCodeNameDeviceAndImeiTail()
    {
        var s = Repairs();
        s.Create(Draft(), "К");
        var other = Draft();
        other.ClientName = "Нурлан";
        other.Device = "Casio";
        other.Serial = "";
        s.Create(other, "К");
        var all = s.All();
        RepairOrderStore.Filter(all, "Р-0001", null).Should().ContainSingle().Which.Device.Should().Be("Samsung A54");
        RepairOrderStore.Filter(all, "нурлан", null).Should().ContainSingle();
        RepairOrderStore.Filter(all, "7518", null).Should().ContainSingle().Which.Number.Should().Be(1);
        RepairOrderStore.Filter(all, "casio", RepairStatus.Accepted).Should().ContainSingle();
        RepairOrderStore.Filter(all, "casio", RepairStatus.Ready).Should().BeEmpty();
    }

    [Fact]
    public void Repair_Overdue_OnlyForOpenOrdersPastDue()
    {
        var o = Draft();
        o.DueDate = new DateTime(2026, 11, 10);
        o.Status = RepairStatus.InWork;
        RepairRules.IsOverdue(o, new DateTime(2026, 11, 13)).Should().BeTrue();
        o.Status = RepairStatus.Ready;
        RepairRules.IsOverdue(o, new DateTime(2026, 11, 13)).Should().BeFalse("готов — не просрочен");
        o.Status = RepairStatus.Accepted;
        o.DueDate = new DateTime(2026, 11, 13);
        RepairRules.IsOverdue(o, new DateTime(2026, 11, 13)).Should().BeFalse("срок — сегодня");
    }

    [Fact]
    public void Repair_PrintForms_ContainKeyFacts_AndFitWidth()
    {
        var s = Repairs();
        var o = s.Create(Draft(estimate: 1500m), "К");
        s.Update(o.Id, x => { x.Prepay = 500m; return true; }, "К", "prepay");
        var receipt = RepairRules.BuildReceipt(s.Find(o.Id)!, 32);
        receipt.Should().Contain("Р-0001").And.Contain("Samsung A54").And.Contain(ValidImei).And.Contain("Не включается").And.Contain("500");
        receipt.Split('\n').Should().OnlyContain(l => l.Length <= 32, "лента 58 мм — 32 знака");
        s.Move(o.Id, RepairStatus.Ready, "К", 1700m);
        var act = RepairRules.BuildIssueAct(s.Find(o.Id)!, 42);
        act.Should().Contain("Р-0001").And.Contain("1").And.Contain("700");
    }

    [Fact]
    public void Repair_ReadyMessage_HasDeviceCodeAndAmountDue()
    {
        var o = Draft();
        o.Number = 7;
        o.Prepay = 500m;
        o.FinalPrice = 1700m;
        var text = RepairRules.ReadyMessage(o);
        text.Should().Contain("Samsung A54").And.Contain("Р-0007").And.Contain("1200");
        DebtWhatsAppLink.BuildUrl("+996 700 123 456", text).Should().StartWith("https://wa.me/996700123456?text=");
    }

    // 2026-11-26: статус ремонта на витрине — ссылка в квитанции, честная подсказка без сервера, тело отправки без лишних данных.

    [Fact]
    public void Repair_StatusLink_IsUnderCatalogAndUsesNumberOnly()
    {
        var o = Draft();
        o.Number = 12;
        RepairStatusPublic.Url("shop-x", o).Should().Be("https://market.nurcrm.kg/catalog/shop-x/repair?code=12");
        RepairStatusPublic.Url("", o).Should().BeNull();
        RepairStatusPublic.Url(null, o).Should().BeNull();
    }

    [Fact]
    public void Repair_Receipt_WithStatusUrl_PrintsLinkAndCode_AndFitsWidth()
    {
        var s = Repairs();
        var o = s.Create(Draft(estimate: 1500m), "К");
        var url = RepairStatusPublic.Url("shop-with-a-long-name", s.Find(o.Id)!);
        var receipt = RepairRules.BuildReceipt(s.Find(o.Id)!, 32, url);
        // Длинная ссылка переносится по 32 знака — склеиваем строки, чтобы проверить, что она напечатана целиком.
        receipt.Replace("\n", "").Should().Contain("market.nurcrm.kg/catalog/shop-with-a-long-name/repair?code=1");
        receipt.Should().Contain("Р-0001");
        receipt.Split('\n').Should().OnlyContain(l => l.Length <= 32, "лента 58 мм — 32 знака");
    }

    [Fact]
    public void Repair_ReceiptLines_WithoutServer_AskToCallShop_OrStaySilent()
    {
        var o = Draft();
        o.Number = 3;
        RepairStatusPublic.ReceiptLines(o, null, "0555 123 456", 32).Should().NotBeEmpty()
            .And.OnlyContain(l => l.Length <= 32)
            .And.Contain(l => l.Contains("0555") || l.Contains("123"));
        string.Join(" ", RepairStatusPublic.ReceiptLines(o, null, "0555 123 456", 42)).Should().Contain("Р-0003");
        RepairStatusPublic.ReceiptLines(o, null, null, 32).Should().BeEmpty("нет ни ссылки, ни телефона магазина — ничего не печатаем");
        RepairStatusPublic.ReceiptLines(o, null, "  ", 32).Should().BeEmpty();
    }

    [Fact]
    public void Repair_StatusPayload_FollowsServerContract()
    {
        var s = Repairs();
        var draft = Draft(estimate: 1500m, prepay: 500m);
        draft.Note = "Царапина на корпусе";
        var o = s.Create(draft, "К");
        var accepted = RepairStatusPublic.BuildPayload(s.Find(o.Id)!);
        accepted.Keys.Should().BeEquivalentTo("number", "code", "status", "device", "client_name", "client_phone", "accepted_at", "due_date", "ready_at",
            "estimate", "prepay", "final_price", "serial", "problem", "accessories", "note");
        accepted.Keys.Should().NotContain(new[] { "external_id", "amount_due", "log" }, "external_id — в адресе, сумму к оплате считает сервер");
        accepted["status"].Should().Be("accepted");
        accepted["code"].Should().Be("Р-0001");
        accepted["number"].Should().Be(1);
        accepted["problem"].Should().Be("Не включается");
        accepted["accessories"].Should().Be("Зарядка");
        accepted["final_price"].Should().BeNull();
        accepted["estimate"].Should().Be("1500.00");
        accepted["prepay"].Should().Be("500.00");

        s.Move(o.Id, RepairStatus.Ready, "К", 1700m);
        var ready = RepairStatusPublic.BuildPayload(s.Find(o.Id)!);
        ready["status"].Should().Be("ready");
        ready["final_price"].Should().Be("1700.00");
        ready["ready_at"].Should().NotBeNull();
        RepairStatusPublic.StatusCode(RepairStatus.InWork).Should().Be("in_work");
        RepairStatusPublic.StatusCode(RepairStatus.Issued).Should().Be("issued");
        RepairStatusPublic.StatusCode(RepairStatus.Cancelled).Should().Be("cancelled");

        var bare = Draft();
        bare.Serial = "";
        bare.Equipment = "";
        var b = RepairStatusPublic.BuildPayload(s.Create(bare, "К"));
        b.Keys.Should().NotContain(new[] { "serial", "accessories", "note" }, "пустые необязательные поля не шлём");
    }

    [Fact]
    public void Repair_ServerResponse_Created_Unchanged_AndSafePublicUrl()
    {
        static System.Text.Json.JsonElement J(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        var created = RepairStatusPublic.ParseResponse(J("{\"id\":\"a\",\"number\":45,\"code\":\"Р-0001\",\"created\":true,\"public_url\":\"https://market.nurcrm.kg/catalog/shop/repair\"}"));
        created.Created.Should().BeTrue();
        created.Unchanged.Should().BeFalse();
        created.ServerNumber.Should().Be("45");
        created.PublicUrl.Should().Be("https://market.nurcrm.kg/catalog/shop/repair");
        RepairStatusPublic.ParseResponse(J("{\"unchanged\":true}")).Unchanged.Should().BeTrue();
        RepairStatusPublic.ParseResponse(J("{\"public_url\":\"https://evil.example.com/x\"}")).PublicUrl.Should().BeNull("чужой домен не принимаем");
        RepairStatusPublic.ParseResponse(J("{\"public_url\":\"http://market.nurcrm.kg/x\"}")).PublicUrl.Should().BeNull("только https");
        RepairStatusPublic.ParseResponse(J("[]")).Kind.Should().Be(RepairSyncKind.Ok);
    }

    [Theory]
    [InlineData("{\"id\":\"a\",\"notified\":\"sent\"}", "sent")]
    [InlineData("{\"id\":\"a\",\"notified\":\"no_chat\"}", "no_chat")]
    [InlineData("{\"id\":\"a\",\"notified\":\"Deferred\"}", "deferred")]
    [InlineData("{\"id\":\"a\",\"notified\":\"failed\"}", "failed")]
    [InlineData("{\"id\":\"a\",\"notified\":null}", null)]
    [InlineData("{\"id\":\"a\"}", null)]
    public void Repair_ServerResponse_ReadsNotifiedAboutTelegram(string json, string? expected)
    {
        var r = RepairStatusPublic.ParseResponse(System.Text.Json.JsonDocument.Parse(json).RootElement.Clone());
        r.Notified.Should().Be(expected);
    }

    [Fact]
    public void Repair_MarkSynced_StoresNotified_SentSetsNotifiedAt_AndEmptyAnswerDoesNotErase()
    {
        // 2026-11-26 (Умар 16:20): PUT при «Готов» — сервер сам пишет клиенту в Telegram, ответ notified сохраняется в заказе
        var s = Repairs();
        var o = s.Create(Draft(), "Кассир");
        s.Move(o.Id, RepairStatus.InWork, "Кассир");
        s.Move(o.Id, RepairStatus.Ready, "Кассир", 500m);
        var rev = s.Find(o.Id)!.Rev;
        s.Find(o.Id)!.NotifiedStatus.Should().BeEmpty("до ответа сервера ничего не знаем");

        s.MarkSynced(o.Id, rev, null, null, "no_chat");
        var afterNoChat = s.Find(o.Id)!;
        afterNoChat.NotifiedStatus.Should().Be("no_chat");
        afterNoChat.NotifiedAt.Should().BeNull("no_chat — клиенту никто не писал");
        RepairStatusPublic.ServerNotifiedClient(afterNoChat).Should().BeFalse();
        RepairStatusPublic.ServerNotifyHint(afterNoChat).Should().NotBeNullOrWhiteSpace("подсказка и прежние кнопки WhatsApp/Telegram");

        s.MarkSynced(o.Id, rev, null, null, "sent");
        var sent = s.Find(o.Id)!;
        sent.NotifiedStatus.Should().Be("sent");
        sent.NotifiedAt.Should().Be(_now, "sent — клиенту написали, отметка «Клиенту написали»");
        RepairStatusPublic.ServerNotifiedClient(sent).Should().BeTrue();
        RepairStatusPublic.ServerNotifyHint(sent).Should().BeNull("клиент оповещён — подсказки не нужно");

        s.MarkSynced(o.Id, rev, null, null, null);
        s.Find(o.Id)!.NotifiedStatus.Should().Be("sent", "ответ без notified (повторный PUT) статус не стирает");
    }

    [Fact]
    public void Repair_ServerErrors_AreClassified()
    {
        RepairStatusPublic.Classify(new ApiException("x", 403)).Should().Be(RepairSyncKind.Forbidden);
        RepairStatusPublic.Classify(new ApiException("x", 429)).Should().Be(RepairSyncKind.RateLimited);
        RepairStatusPublic.Classify(new ApiException("x", 404)).Should().Be(RepairSyncKind.Missing);
        RepairStatusPublic.Classify(new ApiException("x", 400)).Should().Be(RepairSyncKind.Rejected);
        RepairStatusPublic.Classify(new ApiException("x", 502)).Should().Be(RepairSyncKind.Retry);
        RepairStatusPublic.Classify(new HttpRequestException("нет сети")).Should().Be(RepairSyncKind.Retry);
    }

    [Fact]
    public void Repair_Queue_EveryEditWaitsForServer_AndMarkSyncedOnlyIfUnchanged()
    {
        var s = Repairs();
        var o = s.Create(Draft(estimate: 1500m), "К");
        var first = s.Find(o.Id)!;
        first.SyncPending.Should().BeTrue("новый заказ ещё не отправлен");
        first.Rev.Should().Be(1);

        s.MarkSynced(o.Id, first.Rev, "77", "https://market.nurcrm.kg/catalog/shop/repair/77");
        var synced = s.Find(o.Id)!;
        synced.SyncPending.Should().BeFalse();
        synced.ServerNumber.Should().Be("77");
        synced.PublicUrl.Should().EndWith("/77");

        s.Move(o.Id, RepairStatus.InWork, "К");
        s.Find(o.Id)!.SyncPending.Should().BeTrue("правка после отправки — снова в очередь");

        // Правка прошла, пока шёл запрос: отметка «отправлено» для старой правки не ставится.
        var rev = s.Find(o.Id)!.Rev;
        s.Move(o.Id, RepairStatus.Ready, "К", 1700m);
        s.MarkSynced(o.Id, rev, null, null);
        s.Find(o.Id)!.SyncPending.Should().BeTrue("за время запроса заказ изменился");
        s.Find(o.Id)!.PublicUrl.Should().EndWith("/77", "адрес страницы не теряется");
    }

    [Fact]
    public async Task Repair_Flush_DoesNothingWithoutServerFeature()
    {
        var s = Repairs();
        s.Create(Draft(), "К");
        RepairStatusPublic.Enabled.Should().BeFalse("признака repair_status_public нет — касса в сеть не ходит");
        (await RepairStatusPublic.FlushAsync(s)).Should().Be(0);
        s.All().Single().SyncPending.Should().BeTrue();
    }

    [Fact]
    public void Repair_Store_SurvivesCorruptFile()
    {
        File.WriteAllText(Path.Combine(_dir, "repair-orders.json"), "{не json");
        Repairs().All().Should().BeEmpty();
        Repairs().Create(Draft(), "К").Number.Should().Be(1);
    }

    // ───────────────────────── этап 2: номер от сервера, защита от затирания, чтение изменений (Умар, 26.11) ─────────────────────────

    private static System.Text.Json.JsonElement J2(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();

    private static ApiException Err(int status, string json) => new("x", status, J2(json));

    [Fact]
    public void Repair2_Payload_TempNumber_AsksServerForNumber_AndSendsBaseUpdatedAt()
    {
        var s = Repairs();
        var o = s.Create(Draft(estimate: 1500m), "К", serverNumber: true);
        o.TempNumber.Should().BeTrue();
        var p = RepairStatusPublic.BuildPayload(s.Find(o.Id)!);
        p["server_number"].Should().Be(true);
        p.Keys.Should().NotContain(new[] { "number", "code", "base_updated_at" }, "своего номера нет, метки сервера ещё нет");

        var legacy = RepairStatusPublic.BuildPayload(s.Find(o.Id)!, legacyNumber: true);
        legacy.Keys.Should().Contain(new[] { "number", "code" }).And.NotContain("server_number");

        s.ApplySyncResult(o.Id, 1, new RepairSyncResult(RepairSyncKind.Ok, true, UpdatedAt: "2026-11-26T10:00:00.123456Z", Number: 5, Code: "Р-0005"), "{}", askedServerNumber: true);
        var after = s.Find(o.Id)!;
        after.Number.Should().Be(5);
        RepairRules.Code(after).Should().Be("Р-0005");
        after.TempNumber.Should().BeFalse();
        var next = RepairStatusPublic.BuildPayload(after);
        next["base_updated_at"].Should().Be("2026-11-26T10:00:00.123456Z");
        next["number"].Should().Be(5);
        next["code"].Should().Be("Р-0005");
        next.Keys.Should().NotContain("server_number");
    }

    [Fact]
    public void Repair2_Create_TempNumber_IsOffByDefaultWithoutServerFeature()
    {
        Repairs().Create(Draft(), "К").TempNumber.Should().BeFalse("признака repair_status_public нет — прежнее поведение");
    }

    [Fact]
    public void Repair2_ParseResponse_ReadsServerNumberCodeUpdatedAtAndId()
    {
        var r = RepairStatusPublic.ParseResponse(J2("{\"id\":\"b1\",\"number\":5,\"code\":\"Р-0005\",\"created\":true,\"updated_at\":\"2026-11-26T10:00:00.5Z\"}"));
        r.Kind.Should().Be(RepairSyncKind.Ok);
        r.Number.Should().Be(5);
        r.Code.Should().Be("Р-0005");
        r.UpdatedAt.Should().Be("2026-11-26T10:00:00.5Z");
        r.ServerId.Should().Be("b1");
        // этап 1: number — внутренний номер сервера (45), код наш — номером считаем код
        RepairStatusPublic.ParseResponse(J2("{\"id\":\"a\",\"number\":45,\"code\":\"Р-0001\"}")).Number.Should().Be(1);
        RepairStatusPublic.ParseResponse(J2("{\"id\":\"a\",\"unchanged\":true}")).Number.Should().BeNull();
    }

    [Fact]
    public void Repair2_ParseError_NumberConflict_StaleVersion_AndOthers()
    {
        var nc = RepairStatusPublic.ParseError(Err(409, "{\"code\":\"number_conflict\",\"suggested_number\":7,\"suggested_code\":\"Р-0007\"}"));
        nc.Kind.Should().Be(RepairSyncKind.NumberConflict);
        nc.SuggestedNumber.Should().Be(7);
        nc.SuggestedCode.Should().Be("Р-0007");

        var stale = RepairStatusPublic.ParseError(Err(409, "{\"code\":\"stale_version\",\"current\":{\"id\":\"b1\",\"status\":\"in_work\",\"updated_at\":\"2026-11-26T11:00:00Z\"}}"));
        stale.Kind.Should().Be(RepairSyncKind.Stale);
        stale.CurrentJson.Should().Contain("in_work");

        RepairStatusPublic.ParseError(Err(409, "{\"code\":\"other\"}")).Kind.Should().Be(RepairSyncKind.Rejected);
        var bad = RepairStatusPublic.ParseError(Err(400, "{\"code\":\"validation_error\"}"));
        bad.Kind.Should().Be(RepairSyncKind.Rejected);
        bad.Http.Should().Be(400);
        RepairStatusPublic.ParseError(new HttpRequestException("нет сети")).Kind.Should().Be(RepairSyncKind.Retry);
    }

    [Fact]
    public async Task Repair2_Sync_TempNumber_GetsServerNumber_AndPrintedCodeIsKept()
    {
        RepairStatusPublic.ResetForTests();
        var s = Repairs();
        s.Create(Draft(), "К"); // №1
        var o = s.Create(Draft(), "К", serverNumber: true); // временный №2
        RepairRules.Code(o).Should().Be("Р-0002");
        s.MarkPrinted(o.Id, "Р-0002"); // напечатали без связи

        var sent = new List<Dictionary<string, object?>>();
        var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (order, legacy) =>
        {
            sent.Add(RepairStatusPublic.BuildPayload(order, legacy));
            return Task.FromResult(new RepairSyncResult(RepairSyncKind.Ok, true, Code: "Р-0005", Number: 5, ServerId: "srv-1", UpdatedAt: "2026-11-26T10:00:00Z"));
        });
        r.Kind.Should().Be(RepairSyncKind.Ok);
        sent.Single()["server_number"].Should().Be(true);
        var after = s.Find(o.Id)!;
        after.Id.Should().Be(o.Id, "связь по external_id не ломается");
        RepairRules.Code(after).Should().Be("Р-0005");
        after.PrintedCode.Should().Be("Р-0002", "на бумаге у клиента временный номер — карточка покажет оба");
        after.TempNumber.Should().BeFalse();
        after.SyncPending.Should().BeFalse();
        after.ServerId.Should().Be("srv-1");
        after.UpdatedAt.Should().Be("2026-11-26T10:00:00Z");
        RepairOrderStore.Filter(s.All(), "Р-0002", null).Should().ContainSingle("клиент может назвать код с бумаги").Which.Id.Should().Be(o.Id);
        s.Create(Draft(), "К").Number.Should().Be(6, "следующий локальный номер — после серверного");
    }

    [Fact]
    public async Task Repair2_Sync_OldServer_Rejects400_FallsBackToOwnNumber()
    {
        RepairStatusPublic.ResetForTests();
        try
        {
            var s = Repairs();
            var o = s.Create(Draft(), "К", serverNumber: true);
            var modes = new List<bool>();
            var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (order, legacy) =>
            {
                modes.Add(legacy);
                return Task.FromResult(legacy ? new RepairSyncResult(RepairSyncKind.Ok, true, ServerNumber: "45", Code: "Р-0001", UpdatedAt: "2026-11-26T10:00:00Z") : new RepairSyncResult(RepairSyncKind.Rejected, Http: 400));
            });
            r.Kind.Should().Be(RepairSyncKind.Ok);
            r.LegacyNumber.Should().BeTrue();
            modes.Should().Equal(false, true);
            var after = s.Find(o.Id)!;
            after.TempNumber.Should().BeFalse();
            after.Number.Should().Be(1, "наш номер остаётся: прежнее поведение");
            after.SyncPending.Should().BeFalse();
            RepairStatusPublic.LegacyNumbers.Should().BeTrue("до перезапуска сразу шлём номер сами");
            s.Create(Draft(), "К").TempNumber.Should().BeFalse("после «старого» ответа новые квитанции без временного номера");
        }
        finally { RepairStatusPublic.ResetForTests(); }
    }

    [Fact]
    public async Task Repair2_Sync_NumberConflict_TakesSuggestedAndRetries_OldCodeIsRemembered()
    {
        RepairStatusPublic.ResetForTests();
        var s = Repairs();
        var o = s.Create(Draft(), "К", serverNumber: false); // номер 1, свой
        var seen = new List<string>();
        var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (order, legacy) =>
        {
            seen.Add(RepairRules.Code(order));
            return Task.FromResult(RepairRules.Code(order) == "Р-0001"
                ? new RepairSyncResult(RepairSyncKind.NumberConflict, SuggestedNumber: 7, SuggestedCode: "Р-0007", Http: 409)
                : new RepairSyncResult(RepairSyncKind.Ok, UpdatedAt: "2026-11-26T10:00:00Z"));
        });
        r.Kind.Should().Be(RepairSyncKind.Ok);
        seen.Should().Equal("Р-0001", "Р-0007");
        var after = s.Find(o.Id)!;
        RepairRules.Code(after).Should().Be("Р-0007");
        after.PrintedCode.Should().Be("Р-0001");
        after.SyncPending.Should().BeFalse();
    }

    [Fact]
    public async Task Repair2_Sync_StaleVersion_ServerContentUnchanged_RetriesWithNewStamp()
    {
        RepairStatusPublic.ResetForTests();
        var s = Repairs();
        var o = s.Create(Draft(estimate: 1500m), "К", serverNumber: false);
        // первая отправка принята: сервер знает «Принят», метка t1
        await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (_, _) => Task.FromResult(new RepairSyncResult(RepairSyncKind.Ok, UpdatedAt: "2026-11-26T10:00:00Z")));
        s.Move(o.Id, RepairStatus.InWork, "К");
        // сервер сам поменял метку (дописал служебное поле), содержимое то же
        var current = "{\"id\":\"b1\",\"status\":\"accepted\",\"device\":\"Samsung A54\",\"customer_name\":\"Айгуль\",\"customer_phone\":\"+996 700 123 456\",\"estimate\":\"1500.00\",\"serial\":\"" + ValidImei + "\",\"problem\":\"Не включается\",\"accessories\":\"Зарядка\",\"updated_at\":\"2026-11-26T10:05:00Z\"}";
        var bases = new List<string?>();
        var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (order, _) =>
        {
            var b = RepairStatusPublic.BuildPayload(order).GetValueOrDefault("base_updated_at") as string;
            bases.Add(b);
            return Task.FromResult(b == "2026-11-26T10:00:00Z"
                ? new RepairSyncResult(RepairSyncKind.Stale, CurrentJson: current, Http: 409)
                : new RepairSyncResult(RepairSyncKind.Ok, UpdatedAt: "2026-11-26T10:06:00Z"));
        });
        r.Kind.Should().Be(RepairSyncKind.Ok, "чужой правки нет — кассира не беспокоим");
        bases.Should().Equal("2026-11-26T10:00:00Z", "2026-11-26T10:05:00Z");
        var after = s.Find(o.Id)!;
        after.HasConflict.Should().BeFalse();
        after.SyncPending.Should().BeFalse();
        after.UpdatedAt.Should().Be("2026-11-26T10:06:00Z");
    }

    [Fact]
    public async Task Repair2_Sync_StaleVersion_RealChange_KeepsMyEditsUntilDecision()
    {
        RepairStatusPublic.ResetForTests();
        var s = Repairs();
        var o = s.Create(Draft(estimate: 1500m), "К", serverNumber: false);
        await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (_, _) => Task.FromResult(new RepairSyncResult(RepairSyncKind.Ok, UpdatedAt: "2026-11-26T10:00:00Z")));
        s.Move(o.Id, RepairStatus.InWork, "К"); // моя правка, не отправлена
        // другое устройство перевело в «Готов» с ценой 1700.50
        var current = "{\"id\":\"b1\",\"status\":\"ready\",\"device\":\"Samsung A54\",\"final_price\":\"1700.50\",\"estimate\":\"1500.00\",\"updated_at\":\"2026-11-26T10:05:00Z\"}";
        var calls = 0;
        var r = await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (_, _) =>
        {
            calls++;
            return Task.FromResult(new RepairSyncResult(RepairSyncKind.Stale, CurrentJson: current, Http: 409));
        });
        r.Kind.Should().Be(RepairSyncKind.Stale);
        calls.Should().Be(1, "содержимое на сервере изменилось — без вопроса не повторяем");
        var conflicted = s.Find(o.Id)!;
        conflicted.HasConflict.Should().BeTrue();
        conflicted.Status.Should().Be(RepairStatus.InWork, "правка кассира не потеряна");
        conflicted.SyncPending.Should().BeFalse("пока кассир не решил, не отправляем");
        RepairRules.Money(1700.50m).Should().Be(1700.50m);

        // «Применить мои правки поверх»: метка сервера как основа, снова в очередь
        var keep = Repairs();
        keep.KeepMineOverServer(o.Id, "К").Should().BeTrue();
        var mine = keep.Find(o.Id)!;
        mine.HasConflict.Should().BeFalse();
        mine.Status.Should().Be(RepairStatus.InWork);
        mine.UpdatedAt.Should().Be("2026-11-26T10:05:00Z");
        mine.SyncPending.Should().BeTrue();

        // «Взять с сервера» (на втором заказе с тем же конфликтом): данные сервера, очередь пуста, деньги кассы целы
        var o2 = s.Create(Draft(estimate: 1500m, prepay: 0m), "К", serverNumber: false);
        s.Update(o2.Id, x => { x.Prepay = 300m; return true; }, "К", "prepay");
        s.MarkConflict(o2.Id, current);
        s.TakeServerVersion(o2.Id, "К").Should().BeTrue();
        var theirs = s.Find(o2.Id)!;
        theirs.HasConflict.Should().BeFalse();
        theirs.Status.Should().Be(RepairStatus.Ready);
        theirs.FinalPrice.Should().Be(1700.50m, "копейки не потеряны");
        theirs.Prepay.Should().Be(300m, "предоплата — деньги кассы, сервер её не меняет");
        theirs.SyncPending.Should().BeFalse();
        theirs.UpdatedAt.Should().Be("2026-11-26T10:05:00Z");
    }

    [Fact]
    public void Repair2_ParsePage_ResultsCursorAndHasMore_AndMoneyKeepsKopecks()
    {
        var page = RepairServerSync.ParsePage(J2("{\"results\":[{\"id\":\"a\",\"external_id\":null,\"source\":\"server\",\"code\":\"Р-0009\",\"status\":\"in_work\",\"device\":\"Часы\",\"customer_name\":\"Нурлан\",\"customer_phone\":\"0555\",\"prepaid\":\"250.75\",\"estimate\":1500.5,\"updated_at\":\"2026-11-26T10:00:00Z\"}],\"next_since\":\"2026-11-26T10:00:00Z\",\"has_more\":true}"));
        page.HasMore.Should().BeTrue();
        page.NextSince.Should().Be("2026-11-26T10:00:00Z");
        var row = page.Rows.Single();
        row.NumberValue.Should().Be(9);
        row.Status.Should().Be(RepairStatus.InWork);
        var o = new RepairOrder();
        row.Apply(o, newOrder: true);
        o.Prepay.Should().Be(250.75m);
        o.Estimate.Should().Be(1500.5m);
        o.ClientName.Should().Be("Нурлан");
        RepairServerSync.ParsePage(J2("[]")).Rows.Should().BeEmpty();
        RepairServerSync.ParsePage(J2("{\"results\":[{\"foo\":1}]}")).Rows.Should().BeEmpty("строка без id и external_id не годится");
    }

    [Fact]
    public async Task Repair2_Pull_Cursor_Pagination_NoDuplicates_AndServerOrdersAreReadOnly()
    {
        var s = Repairs();
        var mine = s.Create(Draft(estimate: 1500m), "К", serverNumber: false);
        await RepairStatusPublic.SyncOrderAsync(s, s.Find(mine.Id)!, send: (_, _) => Task.FromResult(new RepairSyncResult(RepairSyncKind.Ok, ServerId: "k1", UpdatedAt: "2026-11-26T09:00:00Z")));

        var requests = new List<Dictionary<string, string>>();
        var pages = new Queue<string>(new[]
        {
            // страница 1: моя квитанция сменила статус на другом устройстве + чужая с другой кассы; has_more
            "{\"results\":[{\"id\":\"k1\",\"external_id\":\"" + mine.Id + "\",\"source\":\"kassa\",\"code\":\"Р-0001\",\"status\":\"in_work\",\"updated_at\":\"2026-11-26T10:00:00Z\"},"
            + "{\"id\":\"k2\",\"external_id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"source\":\"kassa\",\"code\":\"Р-0004\",\"status\":\"accepted\",\"device\":\"Casio\",\"customer_name\":\"Бакыт\",\"updated_at\":\"2026-11-26T10:01:00Z\"}],\"next_since\":\"2026-11-26T10:01:00Z\",\"has_more\":true}",
            // страница 2: созданная на сервере (external_id null)
            "{\"results\":[{\"id\":\"s1\",\"external_id\":null,\"source\":\"server\",\"code\":\"Р-0010\",\"status\":\"ready\",\"device\":\"iPhone\",\"customer_name\":\"Эльнура\",\"final_price\":\"2300.00\",\"updated_at\":\"2026-11-26T10:02:00Z\"}],\"next_since\":\"2026-11-26T10:02:00Z\",\"has_more\":false}",
        });
        Task<System.Text.Json.JsonElement> Fetch(Dictionary<string, string> q, CancellationToken _)
        {
            requests.Add(new Dictionary<string, string>(q));
            return Task.FromResult(pages.Count > 0 ? J2(pages.Dequeue()) : J2("{\"results\":[],\"next_since\":\"2026-11-26T10:02:00Z\",\"has_more\":false}"));
        }

        (await RepairServerSync.PullAsync(s, Fetch)).Should().Be(3);
        requests.Should().HaveCount(2);
        requests[0]["updated_since"].Should().Be(RepairServerSync.FirstSince);
        requests[0]["page_size"].Should().Be("200");
        requests[1]["updated_since"].Should().Be("2026-11-26T10:01:00Z");
        s.Load().NextSince.Should().Be("2026-11-26T10:02:00Z", "курсор хранится в файле компании");

        var all = s.All();
        all.Should().HaveCount(3);
        s.Find(mine.Id)!.Status.Should().Be(RepairStatus.InWork, "обновление статуса на лету");
        s.Find(mine.Id)!.Device.Should().Be("Samsung A54");
        var foreignKassa = s.Find("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")!;
        foreignKassa.Source.Should().Be("kassa");
        foreignKassa.ServerOnly.Should().BeFalse();
        RepairRules.Code(foreignKassa).Should().Be("Р-0004");
        var serverOnly = s.Find("srv-s1")!;
        serverOnly.ServerOnly.Should().BeTrue();
        serverOnly.SyncPending.Should().BeFalse("только что прочитана с сервера — отправлять нечего (правки уйдут обычными адресами repair-orders/<uuid>/, см. RepairServerOrderTests)");
        serverOnly.FinalPrice.Should().Be(2300m);
        RepairRules.Code(serverOnly).Should().Be("Р-0010");

        // повтор: тот же курсор, те же строки — без дублей и без изменений
        requests.Clear();
        pages.Enqueue("{\"results\":[{\"id\":\"s1\",\"external_id\":null,\"source\":\"server\",\"code\":\"Р-0010\",\"status\":\"ready\",\"updated_at\":\"2026-11-26T10:02:00Z\"}],\"next_since\":\"2026-11-26T10:02:00Z\",\"has_more\":false}");
        (await RepairServerSync.PullAsync(s, Fetch)).Should().Be(0);
        requests.Single()["updated_since"].Should().Be("2026-11-26T10:02:00Z");
        s.All().Should().HaveCount(3);
    }

    [Fact]
    public async Task Repair2_Pull_DoesNotOverwriteLocalPendingEdit_AndIgnoresOlderRows()
    {
        var s = Repairs();
        var o = s.Create(Draft(), "К", serverNumber: false);
        await RepairStatusPublic.SyncOrderAsync(s, s.Find(o.Id)!, send: (_, _) => Task.FromResult(new RepairSyncResult(RepairSyncKind.Ok, ServerId: "k1", UpdatedAt: "2026-11-26T10:00:00Z")));
        s.Move(o.Id, RepairStatus.InWork, "К"); // неотправленная правка
        var row = "{\"results\":[{\"id\":\"k1\",\"external_id\":\"" + o.Id + "\",\"status\":\"ready\",\"updated_at\":\"2026-11-26T10:05:00Z\"}],\"next_since\":\"2026-11-26T10:05:00Z\",\"has_more\":false}";
        (await RepairServerSync.PullAsync(s, (_, _) => Task.FromResult(J2(row)))).Should().Be(0);
        s.Find(o.Id)!.Status.Should().Be(RepairStatus.InWork, "локальная правка ждёт отправки — её защитит stale_version");
        s.Find(o.Id)!.UpdatedAt.Should().Be("2026-11-26T10:00:00Z", "метка не меняется, иначе защита сломалась бы");

        // без локальных правок более старая строка ничего не меняет, более новая — меняет
        var s2 = Repairs();
        var q = s2.Create(Draft(), "К", serverNumber: false);
        await RepairStatusPublic.SyncOrderAsync(s2, s2.Find(q.Id)!, send: (_, _) => Task.FromResult(new RepairSyncResult(RepairSyncKind.Ok, ServerId: "k9", UpdatedAt: "2026-11-26T10:00:00Z")));
        var older = "{\"results\":[{\"id\":\"k9\",\"external_id\":\"" + q.Id + "\",\"status\":\"cancelled\",\"updated_at\":\"2026-11-26T09:00:00Z\"}],\"has_more\":false}";
        await RepairServerSync.PullAsync(s2, (_, _) => Task.FromResult(J2(older)));
        s2.Find(q.Id)!.Status.Should().Be(RepairStatus.Accepted);
        var newer = "{\"results\":[{\"id\":\"k9\",\"external_id\":\"" + q.Id + "\",\"status\":\"in_work\",\"updated_at\":\"2026-11-26T10:30:00Z\"}],\"has_more\":false}";
        await RepairServerSync.PullAsync(s2, (_, _) => Task.FromResult(J2(newer)));
        s2.Find(q.Id)!.Status.Should().Be(RepairStatus.InWork);
        s2.Find(q.Id)!.SyncPending.Should().BeFalse("то, что взяли с сервера, обратно не отправляем");
    }

    [Fact]
    public async Task Repair2_Pull_ErrorsDoNotThrow_AndCursorStaysPut()
    {
        var s = Repairs();
        (await RepairServerSync.PullAsync(s, (_, _) => throw new HttpRequestException("нет сети"))).Should().Be(0);
        s.Load().NextSince.Should().BeEmpty();
        RepairStatusPublic.ResetForTests();
        (await RepairServerSync.PullAsync(s, (_, _) => throw new ApiException("x", 400, J2("{\"code\":\"validation_error\"}")))).Should().Be(0);
        RepairStatusPublic.ResetForTests();
    }

    [Fact]
    public void Repair2_TempNumber_SurvivesFileRoundTrip_AndConflictBlocksQueue()
    {
        var s = Repairs();
        var o = s.Create(Draft(), "К", serverNumber: true);
        s.MarkConflict(o.Id, "{\"id\":\"x\",\"updated_at\":\"2026-11-26T10:00:00Z\"}");
        var again = Repairs().Find(o.Id)!;
        again.TempNumber.Should().BeTrue();
        again.HasConflict.Should().BeTrue();
        again.SyncPending.Should().BeFalse("конфликт — в очередь отправки не идёт");
    }

    // ───────────────────────── trade-in ─────────────────────────

    [Fact]
    public void TradeIn_PayoutThenSettled_BecomesBought()
    {
        var s = Trades();
        var r = s.Create(Buyback(10000m), "К");
        r.Status.Should().Be(TradeInStatus.Appraised);
        TradeInRules.Outstanding(r).Should().Be(10000m);
        TradeInRules.PayoutLimit(r).Should().Be(10000m);

        s.RecordPayout(r.Id, 12000m, "К").Should().BeFalse("нельзя выплатить больше суммы выкупа");
        s.RecordPayout(r.Id, 10000m, "К").Should().BeTrue();
        var done = s.Find(r.Id)!;
        done.Status.Should().Be(TradeInStatus.Bought);
        done.PaidOut.Should().Be(10000m);
        TradeInRules.Outstanding(done).Should().Be(0m);
        s.RecordPayout(r.Id, 1m, "К").Should().BeFalse("второй раз платить нечего");
    }

    [Fact]
    public void TradeIn_Credit_LimitedByCartTotal_AndByOutstanding()
    {
        var r = Buyback(10000m);
        TradeInRules.CreditLimit(r, 7000m).Should().Be(7000m, "итог чека не должен уйти в минус");
        TradeInRules.CreditLimit(r, 15000m).Should().Be(10000m, "не больше суммы выкупа");
        TradeInRules.CreditLimit(r, 0m).Should().Be(0m, "пустой чек — зачесть нечего");
        r.CreditAdded = 1000m;
        TradeInRules.CreditLimit(r, 15000m).Should().Be(0m, "зачёт уже ждёт оплаты чека");
    }

    [Fact]
    public void TradeIn_CreditThenConfirm_ThenRemainderPaidFromTill()
    {
        var s = Trades();
        var r = s.Create(Buyback(10000m), "К");
        s.RecordCreditAdded(r.Id, 7000m, "К").Should().BeTrue();
        var mid = s.Find(r.Id)!;
        TradeInRules.Outstanding(mid).Should().Be(3000m);
        mid.Status.Should().Be(TradeInStatus.Appraised, "зачёт ждёт оплаты чека — расчёт не закрыт");
        TradeInRules.PayoutLimit(mid).Should().Be(0m, "пока зачёт не подтверждён, выплачивать нельзя — иначе можно переплатить");
        s.RecordCreditAdded(r.Id, 100m, "К").Should().BeFalse("второй зачёт, пока первый не подтверждён");

        s.ConfirmCredit(r.Id, "К").Should().BeTrue();
        var afterConfirm = s.Find(r.Id)!;
        afterConfirm.CreditConfirmed.Should().Be(7000m);
        afterConfirm.CreditAdded.Should().Be(0m);
        TradeInRules.PayoutLimit(afterConfirm).Should().Be(3000m);

        s.RecordPayout(r.Id, 3000m, "К").Should().BeTrue();
        s.Find(r.Id)!.Status.Should().Be(TradeInStatus.Bought);
    }

    [Fact]
    public void TradeIn_CreditCancelled_ReturnsAmountToOutstanding()
    {
        var s = Trades();
        var r = s.Create(Buyback(5000m), "К");
        s.RecordCreditAdded(r.Id, 5000m, "К");
        s.CancelCredit(r.Id, "К").Should().BeTrue();
        var back = s.Find(r.Id)!;
        TradeInRules.Outstanding(back).Should().Be(5000m);
        TradeInRules.CanCancel(back).Should().BeTrue("деньги не двигались");
    }

    [Fact]
    public void TradeIn_CannotCancelAfterMoneyMoved_OrStockBeforeSettled()
    {
        var s = Trades();
        var r = s.Create(Buyback(1000m), "К");
        s.RecordPayout(r.Id, 400m, "К");
        s.Cancel(r.Id, "К").Should().BeFalse("часть денег уже выплачена");
        s.MarkStocked(r.Id, 1500m, "К").Should().BeFalse("расчёт не закрыт — на склад нельзя");
        s.RecordPayout(r.Id, 600m, "К");
        s.MarkStocked(r.Id, 1500m, "К").Should().BeTrue();
        var stocked = s.Find(r.Id)!;
        stocked.Status.Should().Be(TradeInStatus.Stocked);
        stocked.ResalePrice.Should().Be(1500m);
        s.MarkStocked(r.Id, 1600m, "К").Should().BeFalse("повторное оприходование — нельзя");
    }

    [Fact]
    public void TradeIn_StockLine_PurchaseEqualsBuyback_AndBarcodeIsSerial()
    {
        var r = Buyback(8500.5m);
        var line = TradeInRules.StockLine(r, 11000m);
        line.ProductName.Should().EndWith("iPhone 11").And.StartWith("Б/у");
        line.Barcode.Should().Be(ValidImei);
        line.Quantity.Should().Be(1);
        line.PurchasePrice.Should().Be(8500.5);
        line.SalePrice.Should().Be(11000);
        line.ProductId.Should().BeNull("новый товар");
        line.Source.Should().Be(ReceivingSource.Unknown);
    }

    [Fact]
    public void TradeIn_Validation_RequiresDocumentSerialAndAmount()
    {
        TradeInRules.ValidateNew(Buyback()).Should().BeEmpty();
        var empty = new TradeInRecord();
        TradeInRules.ValidateNew(empty).Should().HaveCountGreaterThanOrEqualTo(5);
        var noDoc = Buyback();
        noDoc.ClientDocument = "";
        TradeInRules.ValidateNew(noDoc).Should().ContainSingle(p => p.Contains("паспорта"));
        var noSerial = Buyback();
        noSerial.Serial = "";
        TradeInRules.ValidateNew(noSerial).Should().ContainSingle(p => p.Contains("IMEI"));
        var zero = Buyback(0m);
        TradeInRules.ValidateNew(zero).Should().ContainSingle(p => p.Contains("сумму выкупа"));
    }

    [Fact]
    public void TradeIn_Act_ShowsSettlementParts_AndFitsWidth()
    {
        var s = Trades();
        var r = s.Create(Buyback(10000m), "К");
        s.RecordCreditAdded(r.Id, 7000m, "К");
        s.ConfirmCredit(r.Id, "К");
        s.RecordPayout(r.Id, 3000m, "К");
        var act = TradeInRules.BuildAct(s.Find(r.Id)!, 32);
        act.Should().Contain("Т-0001").And.Contain("iPhone 11").And.Contain(ValidImei).And.Contain("ID 1234567");
        act.Should().Contain("10").And.Contain("7").And.Contain("3");
        act.Split('\n').Should().OnlyContain(l => l.Length <= 32);
    }

    [Fact]
    public void TradeIn_Photos_AreLimitedAndDeduplicated()
    {
        var s = Trades();
        var r = s.Create(Buyback(), "К");
        s.Update(r.Id, x =>
        {
            foreach (var p in new[] { "a.jpg", "A.JPG", "b.jpg" })
                if (!x.Photos.Contains(p, StringComparer.OrdinalIgnoreCase))
                    x.Photos.Add(p);
            return true;
        }, "К", "photo");
        s.Find(r.Id)!.Photos.Should().Equal("a.jpg", "b.jpg");
    }

    [Fact]
    public void Concurrent_Creates_GiveUniqueNumbers()
    {
        var s = Repairs();
        Parallel.For(0, 12, _ => s.Create(Draft(), "К"));
        var numbers = s.All().Select(o => o.Number).ToList();
        numbers.Should().HaveCount(12);
        numbers.Distinct().Should().HaveCount(12);
    }

    // ───────────────────────── доступ и данные компании ─────────────────────────

    [Fact]
    public void Features_AreOffByDefault_AndKnownToServerAddons()
    {
        var p = new UserPreferences();
        p.RepairUnlocked.Should().BeFalse();
        p.TradeInUnlocked.Should().BeFalse();
        ServerAddons.Slugs.Should().Contain(new[] { "repair", "trade_in" });
        p.IsKeyUnlocked("repair").Should().BeFalse();
        p.RepairUnlocked = true;
        p.IsKeyUnlocked("repair").Should().BeTrue();
        p.IsKeyUnlocked("trade_in").Should().BeFalse("флаги независимы");
    }

    [Fact]
    public void LicenseKeys_RepairAndTradeIn_NotAcceptedWithWrongKeys_AndMasterWorks()
    {
        LicenseKeys.IsPermanentSerial("repair", "NMK-REPAIR-AAAA-BBBB-PERM").Should().BeFalse();
        LicenseKeys.IsPermanentSerial("trade_in", "").Should().BeFalse();
        LicenseKeys.IsMasterSerial(LicenseKeys.MasterTestSerial).Should().BeTrue();
    }

    [Fact]
    public void DataFiles_AreInCompanyStoreRegistry()
    {
        CompanyStoreRegistry.Classify("repair-orders.json").Should().NotBe(CompanyStoreRegistry.StoreKind.Unknown);
        CompanyStoreRegistry.Classify("trade-in-records.json.bak").Should().NotBe(CompanyStoreRegistry.StoreKind.Unknown);
    }

    [Fact]
    public void SectionVisibility_HasEntriesForBothFeatures()
    {
        var all = SectionVisibility.Catalog.Select(c => c.Key).ToList();
        // каталог зависит от режима программы (касса/владелец) — проверяем тот, что активен, по любому из двух ключей
        (all.Contains("kassa.repair") || all.Contains("repair")).Should().BeTrue();
        (all.Contains("kassa.tradein") || all.Contains("tradein")).Should().BeTrue();
    }
}
