using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-12 (аудит «Возвраты/Владелец/Тесты» 12.11, К-1, В-1, В-3, В-4, В-5, С-8, Н-1): после правки и аннулирования возврата от клиента журнал возвратов кассы
/// (SaleReturnLedger) и события смены (ShiftEventsStore) обновляются: потолок нового возврата и смена сходятся с сервером. Журнал и база смен — во временной папке данных тестов.
/// Расчёт правки накладной поставщику: нетронутая строка уходит с исходными значениями до последнего знака, строка без единицы не мешает сохранению, 409 и «скорее всего, сохранено».</summary>
[Collection("ServerFeatures")]
public sealed class CustomerReturnJournalsTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public CustomerReturnJournalsTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Чек на 1000 (наличные) и полный возврат на этой кассе (документ pr-…), как их записывает окно возврата: журнал + событие смены «return».</summary>
    private static (string SaleId, string DocId, string ShiftId) FullReturnRecorded(decimal total = 1000m, decimal refund = 1000m, decimal cash = 1000m, decimal nonCash = 0m, decimal debtReduced = 0m, string method = "cash")
    {
        var saleId = NewId("sale-");
        var docId = NewId("pr-");
        var shiftId = NewId("shift-");
        SaleReturnLedger.RememberPayment(saleId, total, method, method == "cash" ? total : 0m, method == "cash" ? 0m : total, null, null, total, DateTimeOffset.Now);
        SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund
        {
            At = DateTimeOffset.Now, Amount = refund, Cash = cash, NonCash = nonCash, DebtReduced = debtReduced, ServerAmount = refund, ServerDocId = docId, Whole = refund >= total,
            Lines = new() { new SaleLedgerLine { LineId = "si-1", Name = "Молоко", Quantity = 2, UnitPrice = refund / 2, Total = refund } },
        });
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, shiftId, ShiftEventsStore.OperationKey(saleId), (double)refund,
            $"cash={SaleReturnLedger.Inv(cash)};noncash={SaleReturnLedger.Inv(nonCash)};debt={SaleReturnLedger.Inv(debtReduced)}");
        return (saleId, docId, shiftId);
    }

    private static double ShiftReturns(string shiftId) => ShiftEventsStore.TotalsForShift(shiftId).GetValueOrDefault(ShiftEventsStore.KindReturn);

    private static (double Cash, double NonCash, double Debt) ShiftSplit(string shiftId)
    {
        var events = ShiftEventsStore.ListForShift(shiftId, ShiftEventsStore.KindReturn);
        return (events.Sum(e => CloseShiftNote(e.Note, "cash") ?? e.Amount), events.Sum(e => CloseShiftNote(e.Note, "noncash") ?? 0), events.Sum(e => CloseShiftNote(e.Note, "debt") ?? 0));
    }

    // тот же разбор примечания, что у окна закрытия смены (CloseShiftDialog.ParseNote в проекте приложения недоступен тестам)
    private static double? CloseShiftNote(string? note, string key)
    {
        foreach (var part in (note ?? "").Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == key && double.TryParse(kv[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
        }
        return null;
    }

    [Fact]
    public void Void_RemovesLedgerRefund_NewReturnUsesFullRemaining()
    {
        var (saleId, docId, shiftId) = FullReturnRecorded();
        SaleReturnLedger.Get(saleId)!.Remaining.Should().Be(0m, "до аннулирования чек возвращён целиком — новый возврат был бы 0");

        var change = CustomerReturnJournals.Void(saleId, docId, 1000m, shiftId);

        var entry = SaleReturnLedger.Get(saleId)!;
        entry.Refunds.Should().BeEmpty("запись возврата с этим id документа убрана из журнала кассы");
        entry.Remaining.Should().Be(1000m, "новый возврат по чеку считается от полного остатка (как на сервере после аннулирования)");
        change.InLedger.Should().BeTrue();
        change.Voided.Should().BeTrue();
        change.DeltaCash.Should().Be(-1000m);
    }

    [Fact]
    public void Void_WritesShiftStorno()
    {
        var (saleId, docId, shiftId) = FullReturnRecorded(total: 600m, refund: 400m, cash: 250m, nonCash: 150m);
        ShiftReturns(shiftId).Should().Be(400, "возврат записан в смену");

        var change = CustomerReturnJournals.Void(saleId, docId, 400m, shiftId);

        change.ShiftStornoWritten.Should().BeTrue();
        ShiftReturns(shiftId).Should().Be(0, "сторно гасит возврат: итог возвратов смены сходится с сервером");
        var split = ShiftSplit(shiftId);
        split.Cash.Should().Be(0, "наличная часть («в ящике должно быть») после сторно тоже нулевая");
        split.NonCash.Should().Be(0);
        ShiftEventsStore.ListForShift(shiftId, ShiftEventsStore.KindReturn).Should().HaveCount(2, "возврат и сторно — две записи, ничего не стёрто");
        ShiftEventsStore.ListForShift(shiftId, ShiftEventsStore.KindReturn)[1].Amount.Should().Be(-400);
    }

    [Fact]
    public void Void_NoShift_OwnerProgram_LedgerKept_NoStorno_NoteExplainsNoDrawer()
    {
        var (saleId, docId, _) = FullReturnRecorded();

        var change = CustomerReturnJournals.Void(saleId, docId, 1000m, shiftId: null);

        change.ShiftStornoWritten.Should().BeFalse("у программы владельца смены нет");
        // 2026-11-13 (проверка денег, п. 3): без смены журнал кассы НЕ меняется — иначе сторно уже никто не запишет; поправку сделает сверка кассы с сервером (ReturnJournalReconcileTests)
        change.Deferred.Should().BeTrue();
        SaleReturnLedger.Get(saleId)!.Remaining.Should().Be(0m, "запись возврата осталась: её увидит сверка кассы");
        CustomerReturnJournals.MoneyNote(change, ownerProgram: true).Should().Contain("программа владельца").And.Contain("Заберите у клиента").And.Contain("Касса сама заметит");
    }

    [Fact]
    public void Void_RefundNotInLedger_NoStorno_NoteStillTellsAmount()
    {
        var shiftId = NewId("shift-");
        var change = CustomerReturnJournals.Void(NewId("sale-"), NewId("pr-"), 250m, shiftId);

        change.InLedger.Should().BeFalse("возврат сделан не этой кассой — журнал его не знает");
        change.ShiftStornoWritten.Should().BeFalse();
        ShiftReturns(shiftId).Should().Be(0);
        var note = CustomerReturnJournals.MoneyNote(change, ownerProgram: false);
        note.Should().Contain("Заберите у клиента").And.Contain("250").And.Contain("касса не помнит");
    }

    [Fact]
    public void Void_RefundWithoutDocId_MatchedBySingleSameAmount()
    {
        var saleId = NewId("sale-");
        SaleReturnLedger.RememberPayment(saleId, 500m, "cash", 500m, 0m, null, null, 500m, DateTimeOffset.Now);
        SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund { At = DateTimeOffset.Now, Amount = 200m, Cash = 200m, ServerAmount = 200m, ServerDocId = null });

        var change = CustomerReturnJournals.Void(saleId, "pr-late-" + Guid.NewGuid().ToString("N")[..6], 200m, NewId("shift-")); // 2026-11-13: со сменой (без смены журнал не меняется)

        change.InLedger.Should().BeTrue("единственный возврат без id с той же суммой документа — он");
        SaleReturnLedger.Get(saleId)!.Refunds.Should().BeEmpty();
    }

    [Fact]
    public void Void_OtherRefundOfSameSale_Stays()
    {
        var (saleId, docId, shiftId) = FullReturnRecorded(total: 1000m, refund: 300m, cash: 300m);
        SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund { At = DateTimeOffset.Now, Amount = 200m, Cash = 200m, ServerAmount = 200m, ServerDocId = "pr-other" });

        CustomerReturnJournals.Void(saleId, docId, 300m, shiftId);

        var entry = SaleReturnLedger.Get(saleId)!;
        entry.Refunds.Should().ContainSingle().Which.ServerDocId.Should().Be("pr-other", "аннулирован только свой документ");
        entry.Remaining.Should().Be(800m);
    }

    [Fact]
    public void Edit_UpdatesLedgerAmount()
    {
        var (saleId, docId, shiftId) = FullReturnRecorded(total: 600m, refund: 300m, cash: 300m);
        var lines = new List<CustomerReturnEditLine> { new("si-1", "Молоко", "шт", 1, 3, 100) };

        var change = CustomerReturnJournals.Edit(saleId, docId, 300m, 200m, lines, shiftId);

        var refund = SaleReturnLedger.Get(saleId)!.Refunds.Should().ContainSingle().Subject;
        refund.Amount.Should().Be(200m, "сумма записи — по новой сумме документа сервера");
        refund.Cash.Should().Be(200m);
        refund.ServerAmount.Should().Be(200m);
        refund.Lines.Single().Quantity.Should().Be(1, "строка журнала — по новому количеству");
        refund.Whole.Should().BeFalse();
        SaleReturnLedger.Get(saleId)!.Remaining.Should().Be(400m, "потолок нового возврата вырос на уменьшенную сумму");
        change.DeltaAmount.Should().Be(-100m);
        ShiftReturns(shiftId).Should().Be(200, "в смене остался возврат 200: сторно на разницу 100");
        ShiftSplit(shiftId).Cash.Should().Be(200);
        CustomerReturnJournals.MoneyNote(change, ownerProgram: false).Should().Contain("Заберите у клиента 100").And.Contain("наличными 100");
    }

    [Fact]
    public void Edit_Increase_CappedByPaid_NoteSaysPayMore()
    {
        var (saleId, docId, shiftId) = FullReturnRecorded(total: 500m, refund: 200m, cash: 200m);

        var change = CustomerReturnJournals.Edit(saleId, docId, 200m, 700m, new List<CustomerReturnEditLine> { new("si-1", "Молоко", "шт", 5, 5, 100) }, shiftId);

        SaleReturnLedger.Get(saleId)!.Refunds.Single().Amount.Should().Be(500m, "все возвраты чека вместе не больше оплаченного");
        change.DeltaAmount.Should().Be(300m);
        ShiftReturns(shiftId).Should().Be(500);
        CustomerReturnJournals.MoneyNote(change, ownerProgram: false).Should().Contain("Выдайте клиенту ещё 300");
    }

    [Fact]
    public void Edit_DebtSale_SplitScaledProportionally_NoteMentionsDebt()
    {
        var (saleId, docId, shiftId) = FullReturnRecorded(total: 1000m, refund: 400m, cash: 100m, nonCash: 0m, debtReduced: 300m, method: "debt");

        var change = CustomerReturnJournals.Edit(saleId, docId, 400m, 200m, new List<CustomerReturnEditLine> { new("si-1", "Молоко", "шт", 1, 2, 200) }, shiftId);

        var refund = SaleReturnLedger.Get(saleId)!.Refunds.Single();
        refund.Cash.Should().Be(50m);
        refund.DebtReduced.Should().Be(150m);
        var note = CustomerReturnJournals.MoneyNote(change, ownerProgram: false);
        note.Should().Contain("Заберите у клиента 50").And.Contain("Долг клиента по чеку снова вырос на 150");
    }

    [Fact]
    public void Edit_SameAmount_NoStorno_NoteSaysNothingToSettle()
    {
        var (saleId, docId, shiftId) = FullReturnRecorded(total: 600m, refund: 300m, cash: 300m);

        var change = CustomerReturnJournals.Edit(saleId, docId, 300m, 300m, new List<CustomerReturnEditLine> { new("si-1", "Молоко", "шт", 2, 3, 150) }, shiftId);

        change.ShiftStornoWritten.Should().BeFalse();
        ShiftEventsStore.ListForShift(shiftId, ShiftEventsStore.KindReturn).Should().HaveCount(1);
        CustomerReturnJournals.MoneyNote(change, ownerProgram: false).Should().Contain("не изменилась");
    }

    // ───────────── 409: товар и числа (С-8) ─────────────

    private static ReturnEditException Refusal(int status, string json) =>
        ReturnEditErrors.Classify(new ApiException("отказ", status, JsonDocument.Parse(json).RootElement.Clone()));

    [Fact]
    public void Text_NegativeStock_NamesProductAndAvailable()
    {
        var e = Refusal(409, "{\"code\":\"negative_stock\",\"product_id\":\"p1\",\"available\":\"3\"}");

        e.Failure.Should().Be(ReturnEditFailure.NegativeStock);
        e.ProductId.Should().Be("p1");
        e.Available.Should().Be(3m);
        ReturnEditErrors.Text(e, id => id == "p1" ? "Молоко 1 л" : null).Should().Contain("«Молоко 1 л»").And.Contain("на складе 3");
        ReturnEditErrors.Text(e).Should().Contain("на складе 3", "без названия — хотя бы число");
    }

    [Fact]
    public void Text_ExceedsReturnable_NamesProductAndMax()
    {
        var e = Refusal(409, "{\"code\":\"exceeds_returnable\",\"product_id\":\"p2\",\"max_qty\":3}");

        ReturnEditErrors.Text(e, id => id == "p2" ? "Сыр" : null).Should().Contain("«Сыр»").And.Contain("можно не больше 3");
    }

    [Fact]
    public void Text_NegativeStock_WithoutFields_StaysAsBefore()
    {
        var e = Refusal(409, "{\"code\":\"negative_stock\"}");

        ReturnEditErrors.Text(e).Should().Be(ReturnEditErrors.Text(new ReturnEditException(ReturnEditFailure.NegativeStock, "negative_stock", "")));
    }

    // ───────────── «исход неизвестен» — только обрыв и 5xx (В-3, В-2) ─────────────

    [Theory]
    [InlineData(409, "{\"code\":\"negative_stock\"}", false)]
    [InlineData(409, "{\"code\":\"exceeds_returnable\"}", false)]
    [InlineData(412, "{\"code\":\"version_conflict\"}", false)]
    [InlineData(403, "{\"code\":\"confirm_invalid\"}", false)]
    [InlineData(400, "{\"code\":\"validation_error\"}", false)]
    [InlineData(409, "{}", true)]
    [InlineData(500, "{\"code\":\"internal\"}", true)]
    [InlineData(504, "{}", true)]
    public void IsOutcomeUnknown_OnlyConnectionAnd5xx(int status, string json, bool unknown)
    {
        ReturnEditErrors.IsOutcomeUnknown(Refusal(status, json)).Should().Be(unknown);
    }

    [Fact]
    public void IsOutcomeUnknown_ConnectionDrop_True()
    {
        ReturnEditErrors.IsOutcomeUnknown(ReturnEditErrors.Classify(new HttpRequestException("обрыв"))).Should().BeTrue();
    }

    // ───────────── накладная поставщику: цена до 4 знаков, строка без единицы (В-4, В-5) ─────────────

    private static List<SupplierReturnEditLine> SupplierLines() => new()
    {
        new("l0", "p0", "Масло упаковка", "", 3, 33.3333, 0, ""),
        new("l1", "p1", "Сыр весовой (без единицы в ответе сервера)", "", 1.255, 150.5, 0, ""),
        new("l2", "p2", "Молоко", "шт", 2, 70, 0, ""),
    };

    [Fact]
    public void Patch_UntouchedLine_KeepsFourDecimals()
    {
        var original = SupplierLines();
        var edited = original.ToList();
        edited[2] = edited[2] with { Qty = 1 }; // трогали только третью строку

        var items = (List<Dictionary<string, object?>>)SupplierReturnEdit.BuildPatchBody(edited, "причина правки", "", original)["items"]!;

        items[0]["purchase_price"].Should().Be("33.3333", "нетронутая строка — исходная цена до последнего знака, не 33,33");
        items[1]["qty"].Should().Be("1.255");
        items[2]["qty"].Should().Be("1");
        SupplierReturnEdit.HasChanges(original, original.ToList(), "", "").Should().BeFalse("«Изменений нет» срабатывает при цене 33,3333");
    }

    [Fact]
    public void Patch_UntouchedLine_LongerThanFourDecimals_SentExactly()
    {
        var original = new List<SupplierReturnEditLine> { new("l0", "p0", "Масло", "шт", 3, 33.33333333, 0, ""), new("l1", "p1", "Сыр", "шт", 1, 10, 0, "") };
        var edited = original.ToList();
        edited[1] = edited[1] with { Qty = 2 };

        var items = (List<Dictionary<string, object?>>)SupplierReturnEdit.BuildPatchBody(edited, "причина правки", "", original)["items"]!;

        items[0]["purchase_price"].Should().Be("33.33333333", "цена без единой потери знаков у нетронутой строки");
    }

    [Fact]
    public void Validate_FractionalNoUnit_UnchangedLine_Passes()
    {
        var original = SupplierLines();
        var edited = original.ToList();
        edited[2] = edited[2] with { Qty = 1 };

        SupplierReturnEdit.Validate(edited, "причина правки", original).Should().BeNull("весовая строка 1,255 без единицы не мешает сохранить правку другой строки");
        SupplierReturnEdit.Validate(edited, "причина правки").Should().Contain("целое", "без исходных строк все строки считаются изменёнными (прежнее поведение)");
        // тронутая строка без единицы с дробным исходным количеством — не штучная: 1,5 можно
        var touched = original.ToList();
        touched[1] = touched[1] with { Qty = 1.5 };
        SupplierReturnEdit.Validate(touched, "причина правки", original).Should().BeNull();
        // а штучная с целым исходным количеством при дробном вводе — нельзя
        var piece = original.ToList();
        piece[0] = piece[0] with { Qty = 2.5 };
        SupplierReturnEdit.Validate(piece, "причина правки", original, out var bad).Should().Contain("целое");
        bad.Should().Be(0, "номер строки с ошибкой — чтобы окно раскрыло и подсветило её");
    }

    [Fact]
    public void CustomerValidate_FractionalNoUnit_UnchangedLine_Passes()
    {
        var original = new List<CustomerReturnEditLine> { new("si-1", "Сыр", "", 1.255, 2, 100), new("si-2", "Молоко", "шт", 2, 3, 70) };
        var edited = original.ToList();
        edited[1] = edited[1] with { Qty = 1 };

        CustomerReturnEdit.Validate(edited, "причина правки", original).Should().BeNull();
        CustomerReturnEdit.Validate(edited, "причина правки").Should().Contain("целое");
    }

    [Fact]
    public void SameComposition_ServerCardVsSent_IgnoresZeroLinesAndRounding()
    {
        var sent = new List<SupplierReturnEditLine> { new("l0", "p0", "М", "шт", 1, 150.5, 0, ""), new("l1", "p1", "С", "шт", 0, 10, 0, ""), new("l2", "p2", "Т", "шт", 3, 20, 0, "") };
        var server = new List<SupplierReturnEditLine> { new("l0", "p0", "М", "шт", 1, 150.50004, 0, ""), new("l2", "p2", "Т", "шт", 3, 20, 0, "") };
        SupplierReturnEdit.SameComposition(server, sent).Should().BeTrue("строка с 0 отправлена как «убрана» — на сервере её нет");
        SupplierReturnEdit.SameComposition(server.Select(l => l.Id == "l2" ? l with { Qty = 4 } : l).ToList(), sent).Should().BeFalse("количество на сервере другое — правил другой сотрудник");
        SupplierReturnEdit.SameComposition(server.Where(l => l.Id != "l2").ToList(), sent).Should().BeFalse();
    }

    [Fact]
    public void FromCards_NoReturnableFields_MaxCappedBySoldQuantity()
    {
        var ret = JsonDocument.Parse("{\"id\":\"pr1\",\"items\":[{\"sale_item_id\":\"si-1\",\"name\":\"Молоко\",\"quantity\":\"2\",\"price\":\"100\"}]}").RootElement.Clone();
        var sale = JsonDocument.Parse("{\"id\":\"s1\",\"items\":[{\"id\":\"si-1\",\"quantity\":\"5\"}]}").RootElement.Clone();

        var line = CustomerReturnEdit.FromCards(ret, sale).Single();

        line.Max.Should().Be(5, "сервер не прислал returnable_qty и «возвращено» — потолок не больше проданного (раньше 5 + 2 = 7)");
    }

    [Fact]
    public void DocAmount_ReadsAmountThenTotals()
    {
        CustomerReturnEdit.DocAmount(JsonDocument.Parse("{\"amount\":\"250.50\"}").RootElement).Should().Be(250.50m);
        CustomerReturnEdit.DocAmount(JsonDocument.Parse("{\"total_amount\":120}").RootElement).Should().Be(120m);
        CustomerReturnEdit.DocAmount(JsonDocument.Parse("{\"id\":\"x\"}").RootElement).Should().BeNull();
    }
}
