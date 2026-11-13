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

    [Fact]
    public void Repair_Store_SurvivesCorruptFile()
    {
        File.WriteAllText(Path.Combine(_dir, "repair-orders.json"), "{не json");
        Repairs().All().Should().BeEmpty();
        Repairs().Create(Draft(), "К").Number.Should().Be(1);
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
