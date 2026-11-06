using NurMarketKassa.Models;
using NurMarketKassa.Services;
using VerifyXunit;

namespace NurMarketKassa.Tests.Wave1;

/// <summary>2026-11-06: В1 — снимки Verify итогов закрытия смены (Z): текст сводки закрытия смены (TelegramBotService.BuildShiftSummary — те же цифры, что и в окне
/// «Просмотр смены»): выручка, способы оплаты, возвраты/списания/долги из журнала смены, скидки и бонусы, внесения и изъятия, ожидаемо/фактически/расхождение
/// (недостача, излишек, без расхождений, поправка к ожидаемой сумме сервера).
/// ВАЖНО: бумажный текст X/Z-отчёта (ShiftDetailsDialog.BuildPrintableReport) закрытый (private static в окне Avalonia) — проектом тестов не достаётся;
/// см. docs/Отчеты/Волна 1 — В1 тесты, находки (06.11.2026).md, находка Н1-01. Эти снимки покрывают общее ядро: ожидаемую сумму (ShiftExpectedCash) и журнал смены.
/// Данные кладутся в базу во временной папке под уникальным id смены, статический источник поправок возвращается на место.</summary>
[Collection("ServerFeatures")]
public sealed class ShiftSummarySnapshotTests : IDisposable
{
    private readonly Func<string?, decimal> _savedProvider = ShiftExpectedCash.CorrectionProvider;

    public ShiftSummarySnapshotTests() => ShiftExpectedCash.CorrectionProvider = _ => 0m;

    public void Dispose() => ShiftExpectedCash.CorrectionProvider = _savedProvider;

    private static string NewShiftId() => "w1-shift-" + Guid.NewGuid().ToString("N")[..10];

    private static void Event(string kind, string shiftId, double amount) =>
        ShiftEventsStore.Record(kind, shiftId, kind + "-" + Guid.NewGuid().ToString("N")[..6], amount);

    [Fact]
    public Task Z_Shortage_WithJournalDiscountsAndCashOps()
    {
        var id = NewShiftId();
        Event(ShiftEventsStore.KindReturn, id, 150);
        Event(ShiftEventsStore.KindWriteOff, id, 40);
        Event(ShiftEventsStore.KindExpense, id, 100);
        Event(ShiftEventsStore.KindDebtPayment, id, 300);
        Event(ShiftEventsStore.KindDebtPaymentNonCash, id, 70);
        ClientLoyaltyStore.RecordSaleAdjustment("w1-sale-" + id, id, discountTotal: 80, pointsRedeemed: 20);
        var shift = new ShiftModel
        {
            Id = id, ShiftNumber = "17", Cashier = "Айгуль К.", Status = "Закрыта",
            OpenedAt = new DateTime(2026, 11, 6, 9, 0, 0), ClosedAt = new DateTime(2026, 11, 6, 21, 5, 0),
            Revenue = 7300m, SalesCount = 24, CashSales = 5000m, NonCashSales = 2000m, DebtSales = 300m,
            OpeningCash = 1000m, ExpectedCash = 6000m, ClosingCash = 5900m,
        };
        return Verifier.Verify(TelegramBotService.BuildShiftSummary(shift, deposits: 200m, withdrawals: 100m, shopName: "ТЕСТ МАРКЕТ")).UseDirectory("Verified");
    }

    [Fact]
    public Task Z_Surplus_FallbackFormula_ServerSentNoExpected()
    {
        // сервер ожидаемую сумму не дал, расхода сервера нет: начало + наличные + внесения − изъятия = 1000 + 5000 + 200 − 100 = 6100; факт 6150
        var shift = new ShiftModel
        {
            Id = NewShiftId(), ShiftNumber = "18", Cashier = "Бек", Status = "Закрыта",
            OpenedAt = new DateTime(2026, 11, 7, 9, 0, 0), ClosedAt = new DateTime(2026, 11, 7, 21, 0, 0),
            Revenue = 5000m, SalesCount = 10, CashSales = 5000m, NonCashSales = 0m,
            OpeningCash = 1000m, ClosingCash = 6150m,
        };
        return Verifier.Verify(TelegramBotService.BuildShiftSummary(shift, deposits: 200m, withdrawals: 100m, shopName: null)).UseDirectory("Verified");
    }

    [Fact]
    public Task Z_NoDiscrepancy_ServerExpenseFieldIgnoresDeposits()
    {
        // с полем расхода сервера (expense_total) запасная формула: начало + наличные − изъятия (внесения сервер в ожидаемую сумму не включает)
        var shift = new ShiftModel
        {
            Id = NewShiftId(), ShiftNumber = "19", Cashier = "Нур", Status = "Закрыта",
            OpenedAt = new DateTime(2026, 11, 8, 9, 0, 0), ClosedAt = new DateTime(2026, 11, 8, 20, 0, 0),
            Revenue = 3000m, SalesCount = 5, CashSales = 3000m, NonCashSales = 0m, ExpenseTotal = 500m,
            OpeningCash = 500m, ClosingCash = 3000m,
        };
        return Verifier.Verify(TelegramBotService.BuildShiftSummary(shift, deposits: 0m, withdrawals: 500m, shopName: "Магазин <Тест> & Ко")).UseDirectory("Verified");
    }

    [Fact]
    public Task Z_CorrectionOfServerExpectedCash_IsApplied()
    {
        var id = NewShiftId();
        ShiftExpectedCash.CorrectionProvider = s => s == id ? -250m : 0m;
        var shift = new ShiftModel
        {
            Id = id, ShiftNumber = "20", Cashier = "Айгуль К.", Status = "Закрыта",
            OpenedAt = new DateTime(2026, 11, 9, 9, 0, 0), ClosedAt = new DateTime(2026, 11, 9, 21, 0, 0),
            Revenue = 6000m, SalesCount = 12, CashSales = 5000m, NonCashSales = 1000m,
            OpeningCash = 1000m, ExpectedCash = 6000m, ClosingCash = 5750m,
        };
        return Verifier.Verify(TelegramBotService.BuildShiftSummary(shift, deposits: 0m, withdrawals: 0m, shopName: "ТЕСТ МАРКЕТ")).UseDirectory("Verified");
    }

    [Fact]
    public Task Z_NoCountedCash_NoReconciliationBlock()
    {
        var shift = new ShiftModel
        {
            Id = NewShiftId(), ShiftNumber = "21", Cashier = "Айгуль К.", Status = "Закрыта",
            OpenedAt = new DateTime(2026, 11, 10, 9, 0, 0),
            Revenue = 1200m, SalesCount = 3, CashSales = 1200m, NonCashSales = 0m, OpeningCash = 1000m,
        };
        return Verifier.Verify(TelegramBotService.BuildShiftSummary(shift, deposits: 0m, withdrawals: 0m, shopName: "ТЕСТ МАРКЕТ")).UseDirectory("Verified");
    }
}
