using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Returns;

/// <summary>2026-11-13 (проверка денег перед 1.17.86, п. 2, п. 3, п. 7 и «возврат из программы владельца не виден журналу кассы»): журнал возвратов кассы и смена сходятся с сервером, когда возврат
/// изменили вне окна кассы (правка дошла без ответа, программа владельца, сайт, другая касса); сторно возврата прошлой смены — отдельной строкой; возврат без связи знает о чужих возвратах.
/// Каждый случай — суммы смены до и после. Журнал и база смен — во временной папке данных тестов.</summary>
[Collection("ServerFeatures")]
public sealed class ReturnJournalReconcileTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly string? _oldShift = PosApp.ActiveShiftId;

    public ReturnJournalReconcileTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ReturnJournalSync.ResetForTests();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        PosApp.ActiveShiftId = _oldShift;
        CustomerReturnJournals.ShiftOpenedAt = id => NurMarketKassa.Core.Contracts.ShiftAgeWarning.Get(id);
        ReturnJournalSync.ResetForTests();
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Чек на 1000 наличными и возврат на <paramref name="refund"/> (документ pr-…) в смене <paramref name="shiftId"/>, как их пишет окно возврата. <paramref name="at"/> — когда сделан возврат.</summary>
    private static (string SaleId, string DocId) Return(string shiftId, decimal refund = 300m, DateTimeOffset? at = null, decimal total = 1000m)
    {
        var saleId = NewId("sale-");
        var docId = NewId("pr-");
        SaleReturnLedger.RememberPayment(saleId, total, "cash", total, 0m, null, null, total, DateTimeOffset.Now);
        SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund { At = at ?? DateTimeOffset.Now, Amount = refund, Cash = refund, ServerAmount = refund, ServerDocId = docId, Source = SaleLedgerRefund.SourceReturn, Whole = refund >= total });
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, shiftId, ShiftEventsStore.OperationKey(saleId), (double)refund, $"cash={SaleReturnLedger.Inv(refund)};noncash=0.00;debt=0.00");
        return (saleId, docId);
    }

    private static decimal CashOut(string shiftId) => ShiftEventsStore.ReturnTotalsForShift(shiftId).TotalCash;

    // ───────────── п. 2: правка дошла до сервера, ответ потерялся ─────────────

    [Fact]
    public void LostReply_EditArrived_ReconcileWritesDifference_ShiftBeforeAfter()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);
        CashOut(shift).Should().Be(300m, "до сверки смена знает возврат 300");

        // сервер уже 100 (правка 300 → 100 дошла), окно записать её не успело
        var change = CustomerReturnReconciler.ApplyDoc(docId, saleId, 100m, voided: false, shift);

        change.Should().NotBeNull();
        change!.DeltaAmount.Should().Be(-200m);
        CashOut(shift).Should().Be(100m, "в ящике должно быть на 200 больше: 200 вернулись от клиента");
        ShiftEventsStore.ReturnTotalsForShift(shift).Amount.Should().Be(100m);
        SaleReturnLedger.Get(saleId)!.Refunds.Single().ServerAmount.Should().Be(100m);
        SaleReturnLedger.Get(saleId)!.Remaining.Should().Be(900m, "потолок нового возврата — по новой сумме");
        CustomerReturnReconciler.ApplyDoc(docId, saleId, 100m, false, shift).Should().BeNull("повтор сверки ничего не пишет");
        CashOut(shift).Should().Be(100m);
    }

    [Fact]
    public void LostReply_WindowThenWritesSameEdit_NoDoubleStorno()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);
        CustomerReturnReconciler.ApplyDoc(docId, saleId, 100m, false, shift);

        // окно после «повтора» тоже применяет правку 300 → 100: сумма записи уже 100, второго сторно быть не должно
        var late = CustomerReturnJournals.Edit(saleId, docId, 300m, 100m, Array.Empty<CustomerReturnEditLine>(), shift);

        late.ShiftStornoWritten.Should().BeFalse();
        CashOut(shift).Should().Be(100m);
        ShiftEventsStore.ListForShift(shift, ShiftEventsStore.KindReturn).Should().HaveCount(2, "возврат и одно сторно");
    }

    [Fact]
    public async Task LostReply_BackgroundTick_FindsEditAndWritesStorno()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);
        PosApp.ActiveShiftId = shift;
        ReturnJournalSync.DocsProvider = (_, _) => Task.FromResult<IReadOnlyList<ServerReturnDoc>?>(new[] { new ServerReturnDoc(docId, saleId, 100m, false) });

        var report = await ReturnJournalSync.TickAsync(force: true);

        report.Should().NotBeNull();
        report!.Any.Should().BeTrue();
        report.DeltaAmount.Should().Be(-200m);
        CashOut(shift).Should().Be(100m);
        report.NoticeText().Should().Contain("другом устройстве").And.Contain("200");
    }

    [Fact]
    public async Task Tick_WhileEditWindowHolds_DoesNothing()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);
        PosApp.ActiveShiftId = shift;
        ReturnJournalSync.DocsProvider = (_, _) => Task.FromResult<IReadOnlyList<ServerReturnDoc>?>(new[] { new ServerReturnDoc(docId, saleId, 100m, false) });

        using (ReturnJournalSync.Hold())
            (await ReturnJournalSync.TickAsync(force: true)).Should().BeNull("окно правки само пишет журнал");

        CashOut(shift).Should().Be(300m);
        (await ReturnJournalSync.TickAsync(force: true))!.Any.Should().BeTrue("после закрытия окна сверка догоняет");
        CashOut(shift).Should().Be(100m);
    }

    // ───────────── п. 3: аннулировали/правили в программе владельца или на другой кассе ─────────────

    [Fact]
    public void OwnerVoid_LedgerKeptUntilKassaReconciles_ThenShiftCashBack()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 500m);

        // программа владельца: смены нет — запись возврата в журнале остаётся, сторно не пишется
        var owner = CustomerReturnJournals.Void(saleId, docId, 500m, shiftId: null);
        owner.Deferred.Should().BeTrue();
        SaleReturnLedger.Get(saleId)!.Refunds.Should().ContainSingle();
        CashOut(shift).Should().Be(500m, "смена кассы пока не знает");

        // касса сверяется с сервером: документ аннулирован
        var report = CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(docId, saleId, 500m, Voided: true) }, shift, TimeSpan.FromDays(3));

        report.Any.Should().BeTrue();
        SaleReturnLedger.Get(saleId)!.Refunds.Should().BeEmpty();
        SaleReturnLedger.Get(saleId)!.Remaining.Should().Be(1000m, "потолок нового возврата — полный");
        CashOut(shift).Should().Be(0m, "500 вернулись в ящик: «в ящике должно быть» и X/Z сходятся, излишка нет");
    }

    [Fact]
    public void OwnerEdit_NoShift_DryRun_ThenKassaReconciles()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 500m);

        var owner = CustomerReturnJournals.Edit(saleId, docId, 500m, 200m, Array.Empty<CustomerReturnEditLine>(), shiftId: null);

        owner.Deferred.Should().BeTrue();
        owner.DeltaAmount.Should().Be(-300m, "сообщение владельцу считается на копии записи");
        SaleReturnLedger.Get(saleId)!.Refunds.Single().Amount.Should().Be(500m, "журнал не тронут");
        CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(docId, saleId, 200m, false) }, shift, TimeSpan.FromDays(3));
        SaleReturnLedger.Get(saleId)!.Refunds.Single().Amount.Should().Be(200m);
        CashOut(shift).Should().Be(200m);
    }

    [Fact]
    public void Reconcile_NoShift_TouchesNothing_UntilShiftOpens()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);
        var docs = new[] { new ServerReturnDoc(docId, saleId, 100m, false) };

        CustomerReturnReconciler.Reconcile(docs, shiftId: null, TimeSpan.FromDays(3)).Any.Should().BeFalse();
        SaleReturnLedger.Get(saleId)!.Refunds.Single().Amount.Should().Be(300m);
        CashOut(shift).Should().Be(300m);

        CustomerReturnReconciler.Reconcile(docs, shift, TimeSpan.FromDays(3)).Any.Should().BeTrue();
        CashOut(shift).Should().Be(100m);
    }

    [Fact]
    public void Reconcile_ForeignDoc_DocMissingInList_OfflineRefund_AreNotTouched()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);
        SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund { At = DateTimeOffset.Now, Amount = 100m, Cash = 100m, ServerAmount = 100m, ServerDocId = "offline:" + NewId("op-") });

        // в списке сервера — только чужой документ (другого чека); наш документ в список не попал (не знаем) — журнал не трогаем
        var report = CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(NewId("pr-other-"), NewId("sale-x-"), 50m, false) }, shift, TimeSpan.FromDays(3));

        report.Any.Should().BeFalse();
        SaleReturnLedger.Get(saleId)!.Refunds.Should().HaveCount(2);
        CashOut(shift).Should().Be(300m);
    }

    // ───────────── обмен товара: сверка с сервером его не трогает ─────────────

    /// <summary>Возврат-часть обмена, как её пишет ExchangeWindow: сумма — засчитано за возвращённое, в ящик ушла только сдача (<paramref name="drawerOut"/>), событие смены — с exchange=1.</summary>
    private static (string SaleId, string DocId) Exchange(string shiftId, decimal returned, decimal drawerOut, string? source = SaleLedgerRefund.SourceExchange)
    {
        var saleId = NewId("sale-");
        var docId = NewId("pr-ex-");
        SaleReturnLedger.RememberPayment(saleId, 1000m, "cash", 1000m, 0m, null, null, 1000m, DateTimeOffset.Now);
        SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund { At = DateTimeOffset.Now, Amount = returned, Cash = drawerOut, ServerAmount = returned, ServerDocId = docId, Source = source });
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, shiftId, ShiftEventsStore.OperationKey(NewId("ex-")), (double)returned, $"cash={SaleReturnLedger.Inv(drawerOut)};noncash=0.00;debt=0.00;exchange=1");
        return (saleId, docId);
    }

    [Fact]
    public void Exchange_500To700_WithSurcharge_ReconcileWritesNothing()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Exchange(shift, returned: 500m, drawerOut: 0m); // вернули на 500, взяли на 700, клиент доплатил 200 — из ящика ничего не ушло
        var before = ShiftEventsStore.ReturnTotalsForShift(shift);

        // сервер в списке возвратов может показать документ обмена и 500, и 700 (другая величина) — ни то ни другое сверка не трогает
        foreach (var listed in new[] { 500m, 700m })
            CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(docId, saleId, listed, false) }, shift, TimeSpan.FromDays(3)).Any.Should().BeFalse();

        ShiftEventsStore.ReturnTotalsForShift(shift).Should().Be(before);
        SaleReturnLedger.Get(saleId)!.Refunds.Single().Amount.Should().Be(500m);
        ShiftEventsStore.ListForShift(shift, ShiftEventsStore.KindReturn).Should().HaveCount(1);
    }

    [Fact]
    public void Exchange_700To500_ChangeBackToCustomer_ReconcileWritesNothing_EvenIfVoidedInList()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Exchange(shift, returned: 700m, drawerOut: 200m); // вернули на 700, взяли на 500 — клиенту выдана сдача 200
        var cashBefore = CashOut(shift);

        CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(docId, saleId, 700m, false) }, shift, TimeSpan.FromDays(3)).Any.Should().BeFalse();
        CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(docId, saleId, 700m, true) }, shift, TimeSpan.FromDays(3)).Any.Should().BeFalse("аннулирование обмена — не возврат: сверка молчит");

        CashOut(shift).Should().Be(cashBefore).And.Be(200m);
        SaleReturnLedger.Get(saleId)!.Refunds.Should().ContainSingle();
    }

    [Fact]
    public async Task Exchange_BackgroundTick_WritesNothing_ButOrdinaryReturnEditedOnServerIs()
    {
        var shift = NewId("shift-");
        PosApp.ActiveShiftId = shift;
        var (exSale, exDoc) = Exchange(shift, returned: 500m, drawerOut: 0m);
        var (saleId, docId) = Return(shift, 300m); // обычный возврат, на сервере изменён на 100
        ReturnJournalSync.DocsProvider = (_, _) => Task.FromResult<IReadOnlyList<ServerReturnDoc>?>(new[]
        {
            new ServerReturnDoc(exDoc, exSale, 700m, false),
            new ServerReturnDoc(docId, saleId, 100m, false),
        });

        var report = await ReturnJournalSync.TickAsync(force: true);

        report!.Changes.Should().ContainSingle().Which.SaleId.Should().Be(saleId, "поправлен только обычный возврат");
        SaleReturnLedger.Get(exSale)!.Refunds.Single().Amount.Should().Be(500m);
        CashOut(shift).Should().Be(100m, "обмен 0 + обычный возврат 100 после сверки");
    }

    [Fact]
    public void LegacyRefundWithoutSource_NotTouchedByReconcile()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Exchange(shift, returned: 400m, drawerOut: 400m, source: null); // запись без пометки (до этой версии) — происхождение неизвестно

        CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(docId, saleId, 100m, false) }, shift, TimeSpan.FromDays(3)).Any.Should().BeFalse();

        SaleReturnLedger.Get(saleId)!.Refunds.Single().Amount.Should().Be(400m);
    }

    // ───────────── п. 7: сторно возврата прошлой смены ─────────────

    [Fact]
    public void VoidOfPreviousShiftReturn_WrittenToday_SeparateRow_DrawerExpectedCorrect()
    {
        var yesterday = NewId("shift-prev-");
        var today = NewId("shift-today-");
        var openedToday = DateTime.Now.AddHours(-2);
        CustomerReturnJournals.ShiftOpenedAt = id => id == today ? openedToday : null;
        var (saleId, docId) = Return(yesterday, 400m, at: DateTimeOffset.Now.AddDays(-1));

        var change = CustomerReturnJournals.Void(saleId, docId, 400m, today);

        change.PreviousShift.Should().BeTrue();
        change.ShiftStornoWritten.Should().BeTrue();
        var t = ShiftEventsStore.ReturnTotalsForShift(today);
        t.Count.Should().Be(0, "обычных возвратов в этой смене нет");
        t.Amount.Should().Be(0m, "«Возвраты» смены не уходят в минус");
        t.PrevCount.Should().Be(1);
        t.PrevAmount.Should().Be(-400m);
        t.PrevCash.Should().Be(-400m, "наличные вернулись в ящик — отдельная строка «Отмена возврата прошлой смены +400»");
        // «в ящике должно быть»: начало 1000 + продажи 0 − возвраты (−400) = 1400
        ShiftDrawerMath.Expected(1000m, 0m, 0m, 0m, 0m, t.TotalCash).Should().Be(1400m);
        ShiftEventsStore.ReturnTotalsForShift(yesterday).Amount.Should().Be(400m, "прошлая смена не переписывается");
        CustomerReturnJournals.MoneyNote(change, ownerProgram: false).Should().Contain("Отмена возврата прошлой смены");
    }

    [Fact]
    public void VoidOfSameShiftReturn_NotMarkedPrevious_ReturnsGoBackToZero()
    {
        var shift = NewId("shift-");
        CustomerReturnJournals.ShiftOpenedAt = id => id == shift ? DateTime.Now.AddHours(-3) : null;
        var (saleId, docId) = Return(shift, 250m, at: DateTimeOffset.Now.AddHours(-1));

        var change = CustomerReturnJournals.Void(saleId, docId, 250m, shift);

        change.PreviousShift.Should().BeFalse();
        var t = ShiftEventsStore.ReturnTotalsForShift(shift);
        t.Amount.Should().Be(0m);
        t.PrevAmount.Should().Be(0m);
        t.TotalCash.Should().Be(0m);
    }

    [Fact]
    public void EditOfPreviousShiftReturn_Down_PrevRowCarriesOnlyDifference()
    {
        var yesterday = NewId("shift-prev-");
        var today = NewId("shift-today-");
        CustomerReturnJournals.ShiftOpenedAt = id => id == today ? DateTime.Now.AddHours(-1) : null;
        var (saleId, docId) = Return(yesterday, 300m, at: DateTimeOffset.Now.AddHours(-20));

        CustomerReturnJournals.Edit(saleId, docId, 300m, 100m, Array.Empty<CustomerReturnEditLine>(), today);

        var t = ShiftEventsStore.ReturnTotalsForShift(today);
        t.PrevAmount.Should().Be(-200m);
        t.PrevCash.Should().Be(-200m);
        t.Amount.Should().Be(0m);
    }

    [Fact]
    public void ReconcileOfPreviousShiftReturn_BackgroundPath_MarksPrevious()
    {
        var yesterday = NewId("shift-prev-");
        var today = NewId("shift-today-");
        CustomerReturnJournals.ShiftOpenedAt = id => id == today ? DateTime.Now.AddHours(-1) : null;
        var (saleId, docId) = Return(yesterday, 500m, at: DateTimeOffset.Now.AddHours(-10));

        var report = CustomerReturnReconciler.Reconcile(new[] { new ServerReturnDoc(docId, saleId, 500m, true) }, today, TimeSpan.FromDays(3));

        report.Changes.Should().ContainSingle().Which.PreviousShift.Should().BeTrue();
        ShiftEventsStore.ReturnTotalsForShift(today).PrevCash.Should().Be(-500m);
        report.NoticeText().Should().Contain("прошлой смене");
    }

    [Fact]
    public void LegacyStornoWithoutPrevMark_StaysInRegularReturns()
    {
        var shift = NewId("shift-");
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, shift, ShiftEventsStore.OperationKey("a"), 300, "cash=300.00;noncash=0.00;debt=0.00");
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, shift, ShiftEventsStore.OperationKey("b"), -300, "cash=-300.00;noncash=0.00;debt=0.00;storno=1");

        var t = ShiftEventsStore.ReturnTotalsForShift(shift);

        t.Count.Should().Be(2);
        t.Amount.Should().Be(0m);
        t.PrevCount.Should().Be(0);
    }

    // ───────────── возврат без связи: чужие возвраты ─────────────

    private static OfflineSaleSnapshot Snapshot(string saleId, decimal total = 1000m) => new()
    {
        SaleRef = saleId, IsLocal = false, Method = "cash", Total = total, LinesTotal = total, PaidCash = total, CreatedAt = DateTimeOffset.Now.AddHours(-2),
        Lines = { new OfflineSaleLine { Key = "k1", Name = "Молоко", Quantity = 10, UnitPrice = total / 10, LineTotal = total } },
    };

    [Fact]
    public void ForeignReturns_NoteWritten_FromServerList_OnlyOthersCount()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);
        var docs = new[]
        {
            new ServerReturnDoc(docId, saleId, 300m, false),
            new ServerReturnDoc(NewId("pr-owner-"), saleId, 200m, false), // сделан в программе владельца
            new ServerReturnDoc(NewId("pr-void-"), saleId, 100m, true), // аннулирован — не считается
        };

        CustomerReturnReconciler.NoteForeignReturns(docs, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now);

        var entry = SaleReturnLedger.Get(saleId)!;
        entry.ForeignReturns.Should().Be(200m);
        entry.ServerReturnsCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public void ForeignReturns_OwnReturnOnly_ZeroEvenWithServerDiscountDifference()
    {
        var shift = NewId("shift-");
        var (saleId, docId) = Return(shift, 300m);

        // сервер записал на 5 больше (скидка на чек): это не чужой возврат
        CustomerReturnReconciler.NoteForeignReturns(new[] { new ServerReturnDoc(docId, saleId, 305m, false) }, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now);

        SaleReturnLedger.Get(saleId)!.ForeignReturns.Should().Be(0m);
    }

    [Fact]
    public void OfflineRefund_KnownForeignReturn_DeductedAndWarned()
    {
        var saleId = NewId("sale-");
        SaleReturnLedger.RememberPayment(saleId, 1000m, "cash", 1000m, 0m, null, null, 1000m, DateTimeOffset.Now.AddHours(-2));
        SaleReturnLedger.NoteServerReturns(saleId, DateTimeOffset.Now.AddMinutes(-5), 400m); // в программе владельца уже вернули 400

        var plan = OfflineOperationsService.PlanRefund(Snapshot(saleId), new[] { ("k1", 10.0) });

        plan.Ok.Should().BeTrue();
        plan.Amount.Should().Be(600m, "возврат без связи — не больше остатка с учётом чужого возврата 400");
        plan.Warning.Should().NotBeNull().And.Contain("400").And.Contain("не на этой кассе");
    }

    [Fact]
    public void OfflineRefund_NeverChecked_RecentSale_WarnsNotChecked()
    {
        var saleId = NewId("sale-");
        SaleReturnLedger.RememberPayment(saleId, 1000m, "cash", 1000m, 0m, null, null, 1000m, DateTimeOffset.Now.AddHours(-2));

        var plan = OfflineOperationsService.PlanRefund(Snapshot(saleId), new[] { ("k1", 3.0) });

        plan.Ok.Should().BeTrue();
        plan.Amount.Should().Be(300m);
        plan.Warning.Should().Contain("не сверены");
    }

    [Fact]
    public void OfflineRefund_CheckedRecently_NoForeign_NoWarning()
    {
        var saleId = NewId("sale-");
        SaleReturnLedger.RememberPayment(saleId, 1000m, "cash", 1000m, 0m, null, null, 1000m, DateTimeOffset.Now.AddHours(-2));
        SaleReturnLedger.NoteServerReturns(saleId, DateTimeOffset.Now.AddMinutes(-10), 0m);

        var plan = OfflineOperationsService.PlanRefund(Snapshot(saleId), new[] { ("k1", 3.0) });

        plan.Ok.Should().BeTrue();
        plan.Warning.Should().BeNull();
    }

    [Fact]
    public void OfflineRefund_SaleStillInQueue_NoServerWarning()
    {
        var snap = Snapshot(NewId("local-"));
        snap.IsLocal = true;

        var plan = OfflineOperationsService.PlanRefund(snap, new[] { ("k1", 3.0) });

        plan.Warning.Should().BeNull("чека ещё нет на сервере — чужих возвратов быть не может");
    }
}
