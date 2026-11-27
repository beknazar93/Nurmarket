using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (живой проход, A — деньги): сервер не ответил на закрытие смены за 3 с, касса закрыла смену у себя, и Z сразу после закрытия
/// показал выручку 423 вместо 1 189,75, «Остаток долга 65» (долг оплачен), «Корректировку +838,98» — а у владельца тот же Z верный.
/// Проверяется: Z после таймаута строится из итогов сервера, полученных окном закрытия (= Z у владельца), помечен предварительным и после отчёта
/// закрытой смены совпадает с владельцем и по «в ящике должно быть»; без сервера — данные кассы без ложного «остатка долга»; срок ответа на закрытие 10 с;
/// смешанная + долг + оплата долга + возврат — цифры сходятся. (B) раскладка ящика X-отчёта не теряет возвраты сервера после возврата на кассе.
/// (E) «Смены → Сегодня → Закрытые» — по дню закрытия.</summary>
[Collection("UiScenarios")]
public sealed class ShiftCloseTimeoutZReportTests
{
    private const string ShiftId = "7d1c2b9e-5f4a-4c3b-9a8e-1f2e3d4c5b6a";

    /// <summary>Отчёт смены сервера (как в живом случае: 13 продаж, долг оплачен, возвраты 834,90). status — open (до закрытия) или closed (у владельца).</summary>
    private static ServerShiftReport Report(string status) => ServerShiftReport.TryParse(JsonDocument.Parse($$"""
        {"shift":"{{ShiftId}}","status":"{{status}}","opening_cash":"500.00","sales_count":13,"sales_total":"1189.75",
         "by_payment":{"cash":"880.00","transfer":"234.75","mixed_cash":"50.00","mixed_card":"25.00","debt":"0"},
         "cash_sales":"930.00","noncash_sales":"259.75","debt_payments_cash":"65.00","returns_total":"834.90",
         "deposits":"0","withdrawals":"0","expected_cash":"{{(status == "closed" ? "1163.98" : "1098.98")}}"{{(status == "closed" ? ",\"counted_cash\":\"1163.98\",\"difference\":\"0\"" : "")}}}
        """).RootElement)!;

    private static readonly LocalShiftTotals.Result PartialLocal = new(TotalSales: 423m, CashSales: 283m, NonCashSales: 75m, DebtSales: 65m, SalesCount: 6);

    private static readonly ShiftBalanceHelper.ShiftTotals BeforeTotals = new()
    {
        OpeningCash = 500m, TotalSales = 1189.75m, CashSales = 930m, NonCashSales = 259.75m, DebtSales = 0m, SalesCount = 13,
    };

    private static ShiftClosedReportBuilder.Result BuildAfterTimeout(ServerShiftReport? preClose, ShiftBalanceHelper.ShiftTotals? before, LocalShiftTotals.Result? local,
        string shiftId = ShiftId, decimal localDebtPaid = 0m) =>
        ShiftClosedReportBuilder.Build(shiftId, before, fresh: null, preClose, local, new DateTime(2026, 11, 26, 9, 0, 0), new DateTime(2026, 11, 27, 3, 10, 0),
            "Тест", actualClosingCash: 1163.98m, expectedForReport: 1163.98m, localOpening: null, localDebtPaid: localDebtPaid);

    [Fact]
    public void ZAfterTimeout_EqualsOwnerZ_NotPartialTillData()
    {
        var built = BuildAfterTimeout(Report("open"), BeforeTotals, PartialLocal);

        built.Provisional.Should().BeTrue("сервер не подтвердил закрытие — отчёт помечается «данные уточняются»");
        built.LocalReport.Should().BeFalse("итоги сервера до закрытия есть — журнал кассы (6 продаж из 13) не используется");
        built.PreCloseReport.Should().NotBeNull();

        // Окно Z применяет отчёт сервера до закрытия (ShiftDetailsDialog.BindShift, keepShiftExpected пока предварительно).
        var z = built.PreCloseReport!.ApplyTo(built.Shift, keepShiftExpected: true);
        var ownerShift = new ShiftModel { Id = ShiftId, ShiftNumber = ShiftId, Status = "Закрыта", ClosedAt = DateTime.Now };
        var owner = Report("closed").ApplyTo(ownerShift);

        z.Revenue.Should().Be(owner.Revenue).And.Be(1189.75m);
        z.RevenueNoDebt.Should().Be(owner.RevenueNoDebt);
        z.CashSales.Should().Be(owner.CashSales);
        z.NonCashSales.Should().Be(owner.NonCashSales);
        z.SalesCount.Should().Be(owner.SalesCount).And.Be(13);
        z.DebtSales.Should().Be(0m, "долг оплачен в смене — красного «Остаток долга 65» нет");
        z.OpeningCash.Should().Be(owner.OpeningCash);
        z.ExpectedCash.Should().Be(1163.98m, "«в ящике должно быть» — то, с чем кассир сверял пересчёт в окне закрытия");

        // Пришёл отчёт закрытой смены — окно перерисовано по нему: всё как у владельца, включая ящик.
        var refreshed = Report("closed").ApplyTo(z, keepShiftExpected: false);
        refreshed.ExpectedCash.Should().Be(owner.ExpectedCash);
        refreshed.ClosingCash.Should().Be(owner.ClosingCash);
        refreshed.Revenue.Should().Be(owner.Revenue);
    }

    [Fact]
    public void ZAfterTimeout_WithoutReport_TillData_NotStaleListSnapshot()
    {
        // Снимок списка смен мог устареть, пока не было связи (code-review) — без отчёта смены цифры продаж из данных кассы, отчёт предварительный.
        var built = BuildAfterTimeout(preClose: null, BeforeTotals, PartialLocal);

        built.LocalReport.Should().BeTrue();
        built.Provisional.Should().BeTrue();
        built.Shift.Revenue.Should().Be(423m);
        built.Shift.OpeningCash.Should().Be(500m, "начальная сумма — из итогов сервера");
    }

    [Fact]
    public void ZAfterTimeout_ReportOfAnotherShift_IsIgnored()
    {
        var other = ServerShiftReport.TryParse(JsonDocument.Parse("""{"shift":"aaaaaaaa-0000-0000-0000-000000000000","status":"open","sales_total":"1","by_payment":{}}""").RootElement)!;
        BuildAfterTimeout(other, BeforeTotals, PartialLocal).PreCloseReport.Should().BeNull("отчёт чужой смены в Z не подставляется");
    }

    [Fact]
    public void NoServerData_TillData_DebtPaidInShift_DebtBalanceUnknownNotFullDebt()
    {
        var built = BuildAfterTimeout(preClose: null, before: null, PartialLocal, localDebtPaid: 65m);

        built.LocalReport.Should().BeTrue();
        built.Provisional.Should().BeTrue("серверная смена — отчёт обновится, когда сервер отдаст отчёт закрытой смены");
        built.Shift.Revenue.Should().Be(423m);
        built.Shift.DebtSales.Should().BeNull("долг погашали в этой смене — без сервера остаток неизвестен, а не «65» красным");
        built.Shift.CreditSales.Should().Be(65m, "продажа в долг видна строкой «Продажи в долг»");

        BuildAfterTimeout(preClose: null, before: null, PartialLocal).Shift.DebtSales.Should().Be(65m, "оплат долга не было — остаток = продано в долг");
        BuildAfterTimeout(preClose: null, before: null, PartialLocal, shiftId: "offline-shift-1").Provisional.Should().BeFalse("офлайн-смены на сервере нет — ждать нечего");
    }

    [Fact]
    public void ServerAnswered_FreshTotals_NotProvisional()
    {
        var fresh = new CashShiftClosingTotals(500m, 1189.75m, 930m, 259.75m, 0m, 13);
        var built = ShiftClosedReportBuilder.Build(ShiftId, BeforeTotals, fresh, Report("open"), PartialLocal, null, DateTime.Now, "Тест", 1163.98m, 1163.98m, null, 0m);

        built.Provisional.Should().BeFalse();
        built.LocalReport.Should().BeFalse();
        built.PreCloseReport.Should().BeNull("ответ закрытия есть — окно само загрузит отчёт закрытой смены");
        built.Shift.Revenue.Should().Be(1189.75m);
    }

    [Fact]
    public void CloseShift_ServerAnswerBudget_IsTenSeconds()
    {
        CashShiftService.CloseShiftServerAnswerBudget.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(8)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TillData_MixedDebtDebtPaymentReturn_FiguresReconcile()
    {
        var ledger = new[]
        {
            new SaleLedgerEntry { SaleId = "s-cash", Method = "cash", Total = 150m, PaidCash = 150m },
            new SaleLedgerEntry { SaleId = "s-card", Method = "transfer", Total = 85m, PaidNonCash = 85m },
            new SaleLedgerEntry { SaleId = "s-mixed", Method = "mixed", Total = 75m, PaidCash = 50m, PaidNonCash = 25m },
            new SaleLedgerEntry { SaleId = "s-debt", Method = "debt", Total = 65m },
        };
        var t = LocalShiftTotals.Aggregate("shift-x", Array.Empty<OfflineSaleEntry>(), ledger)!;

        t.TotalSales.Should().Be(375m);
        t.CashSales.Should().Be(200m, "нал 150 + наличная часть смешанной 50");
        t.NonCashSales.Should().Be(110m, "карта 85 + безналичная часть смешанной 25");
        t.DebtSales.Should().Be(65m);
        (t.CashSales + t.NonCashSales + t.DebtSales).Should().Be(t.TotalSales, "выручка = нал + безнал + долг");

        // Ящик: начало 500 + нал 200 + оплата долга наличными 65 − возврат смешанного чека наличными 50 (25 ушло на карту, в ящик не входит).
        ShiftDrawerMath.Expected(500m, t.CashSales, 65m, 0m, 0m, 50m).Should().Be(715m);
    }

    [Fact]
    public void XReport_DrawerReturns_KeepServerReturns_AfterTillReturn()
    {
        var id = "b-ret-" + Guid.NewGuid().ToString("N")[..10];
        var shift = new ShiftModel { Id = id, ShiftNumber = id, Status = "Активна" };
        var server = ServerShiftReport.TryParse(JsonDocument.Parse("""{"shift":"x","status":"open","sales_total":"1573.88","by_payment":{},"returns_total":"834.90"}""").RootElement);

        // До возврата на кассе — журнала нет, возвраты сервера целиком (как было).
        ShiftDetailsDialog.ReturnsForExpected(shift, server, null).Should().Be(834.90m);

        // Возврат смешанного чека на кассе: 50 наличными + 25 на карту (сервер уже учёл его в 834,90).
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, id, ShiftEventsStore.OperationKey("sale-b"), 75, "cash=50;noncash=25;debt=0");

        // Было: брался только журнал кассы (−50), раскладка «прыгала». Теперь — возвраты сервера минус то, что ушло не наличными.
        ShiftDetailsDialog.ReturnsForExpected(shift, server, null).Should().Be(809.90m);
        // Сервера нет — полный журнал кассы.
        ShiftDetailsDialog.ReturnsForExpected(shift, null, null).Should().Be(50m);
    }

    [Fact]
    public void XReport_DrawerReturns_Exchange_GoodsPartNotCash()
    {
        var id = "b-exc-" + Guid.NewGuid().ToString("N")[..10];
        var shift = new ShiftModel { Id = id, ShiftNumber = id, Status = "Активна" };
        var server = ServerShiftReport.TryParse(JsonDocument.Parse("""{"shift":"x","status":"open","sales_total":"1","by_payment":{},"returns_total":"500"}""").RootElement);
        // Обмен: вернули на 500, новый товар 480, сдача 20 наличными (code-review: товарная часть — не наличные).
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, id, ShiftEventsStore.OperationKey("sale-e"), 500, "cash=20;noncash=0.00;debt=0.00;exchange=1");
        ShiftDetailsDialog.ReturnsForExpected(shift, server, null).Should().Be(20m);
    }

    [Theory]
    [InlineData(false, true)]  // смена открыта вчера, закрыта сегодня — видна в «Сегодня»
    [InlineData(true, false)]  // closed_at не прислан — по дню открытия (вчера), как раньше
    public void OwnerShifts_TodayFilter_ByCloseDay(bool noClosedAt, bool expectedVisible)
    {
        var today = new DateTime(2026, 11, 27);
        var card = new NurMarketKassa.AvaloniaHost.Views.FinanceWindow.ShiftCardVm
        {
            IsOpen = false,
            OpenedAtRaw = today.AddDays(-1).AddHours(9),
            ClosedAtRaw = noClosedAt ? DateTime.MinValue : today.AddHours(3),
        };
        NurMarketKassa.AvaloniaHost.Views.FinanceWindow.ShiftInPeriod(card, today, today).Should().Be(expectedVisible);
    }
}
