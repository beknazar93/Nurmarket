using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-26 (владелец: «при внесении и изъятии на аналитике покажется только изъятие, а внесение исчезает»): внесение и изъятие ящика
/// считаются в аналитике ПАРОЙ. Причина была не в «схлопывании по сумме», а в том, что отчёты владельца (журнал событий смены, «Прибыль и деньги»,
/// плитка «Расходы», выгрузка Word/Excel) знали только изъятие: событие писалось одному изъятию (вид expense), а блока внесений не было нигде.
/// Здесь: строки cashflows сервера (внесение shift_drawer_inflow и изъятие shift_drawer_outflow «Прочее 1 000» в одной смене), журнал событий смены,
/// «Внесение: Прочее 1 000» и «Изъятие: Прочее 1 000» не схлопываются, итого 0. Журнал — во временной папке данных тестов.</summary>
[Collection("ServerFeatures")]
public sealed class CashDrawerAnalyticsTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private static readonly DateTime From = DateTime.Today.AddDays(-1);
    private static readonly DateTime To = DateTime.Today;

    public CashDrawerAnalyticsTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    /// <summary>Строка cashflows сервера (как ответ api/construction/cashflows/): вид, название, сумма, статья, статус, когда записана.</summary>
    private static string Flow(string sourceKind, string name, string amount, string? categoryTitle = null, string status = "approved", DateTimeOffset? at = null) =>
        "{\"id\":\"" + Guid.NewGuid().ToString("N") + "\",\"source_kind\":\"" + sourceKind + "\",\"name\":\"" + name + "\",\"amount\":\"" + amount + "\","
        + (categoryTitle is null ? "" : "\"category_title\":\"" + categoryTitle + "\",")
        + "\"status\":\"" + status + "\",\"created_at\":\"" + (at ?? DateTimeOffset.Now).ToString("O") + "\"}";

    private static List<JsonElement> Rows(params string[] flows) =>
        JsonDocument.Parse("[" + string.Join(",", flows) + "]").RootElement.Clone().EnumerateArray().ToList();

    private static IReadOnlyList<CashArticles.ArticleTotal> Deposits(List<JsonElement> rows)
    {
        var groups = new CashArticles.DrawerGroups();
        CashArticles.AddDrawerRows(groups, rows, withdrawal: false, From, To);
        return groups.ToTotals();
    }

    private static IReadOnlyList<CashArticles.ArticleTotal> Withdrawals(List<JsonElement> rows)
    {
        var groups = new CashArticles.DrawerGroups();
        CashArticles.AddDrawerRows(groups, rows, withdrawal: true, From, To);
        return groups.ToTotals();
    }

    // ───────────── строки сервера: внесение и изъятие «Прочее 1 000» в одной смене ─────────────

    [Fact]
    public void SameShift_DepositAndWithdrawal_Other1000_BothInAnalytics_NetZero()
    {
        // Внесение уходит с категорией «Внесение» (общая статья сервера), изъятие со статьёй «Прочие» — ровно как их пишет ShiftCashFlowSync.
        var rows = Rows(
            Flow("shift_drawer_outflow", "Изъятие: Прочее — тест", "1000.00", "Прочие"),
            Flow("shift_drawer_inflow", "Внесение: Прочее — тест", "1000.00", "Внесение"));

        var deposits = Deposits(rows);
        var withdrawals = Withdrawals(rows);

        deposits.Should().ContainSingle().Which.Should().Match<CashArticles.ArticleTotal>(x => x.Title == "Прочее" && x.Amount == 1000d && x.Count == 1);
        withdrawals.Should().ContainSingle().Which.Should().Match<CashArticles.ArticleTotal>(x => x.Title == "Прочее" && x.Amount == 1000d && x.Count == 1);
        (deposits.Sum(x => x.Amount) - withdrawals.Sum(x => x.Amount)).Should().Be(0d, "внесено 1 000 и изъято 1 000 — итого ноль, а не «только изъятие −1 000»");
    }

    [Fact]
    public void DepositRow_IsNotCountedAsWithdrawal_AndTheOtherWayRound()
    {
        var onlyDeposit = Rows(Flow("shift_drawer_inflow", "Внесение: Размен", "500.00", "Внесение"));
        var onlyWithdrawal = Rows(Flow("shift_drawer_outflow", "Изъятие: Аренда", "700.00", "Аренда"));

        Withdrawals(onlyDeposit).Should().BeEmpty("внесение не должно попасть в изъятия");
        Deposits(onlyWithdrawal).Should().BeEmpty("изъятие не должно попасть во внесения");
    }

    [Fact]
    public void TwoDeposits_SameArticleAndAmount_AreSummed_NotCollapsed()
    {
        var rows = Rows(
            Flow("shift_drawer_inflow", "Внесение: Прочее — первое", "1000.00", "Внесение"),
            Flow("shift_drawer_inflow", "Внесение: Прочее — второе", "1000.00", "Внесение"));

        var deposits = Deposits(rows);

        deposits.Should().ContainSingle().Which.Should().Match<CashArticles.ArticleTotal>(x => x.Amount == 2000d && x.Count == 2);
    }

    [Fact]
    public void DepositArticle_ComesFromName_BecauseServerCategoryIsOneForAllDeposits()
    {
        var rows = Rows(
            Flow("shift_drawer_inflow", "Внесение: Размен — утро", "300.00", "Внесение"),
            Flow("shift_drawer_inflow", "Внесение: Возврат из инкассации", "200.00", "Внесение"),
            Flow("shift_drawer_inflow", "Внесение", "50.00", "Внесение"));

        var deposits = Deposits(rows).ToDictionary(x => x.Title, x => x.Amount);

        deposits.Should().ContainKey("Размен").WhoseValue.Should().Be(300d);
        deposits.Should().ContainKey("Возврат из инкассации").WhoseValue.Should().Be(200d);
        deposits.Should().ContainKey("Без статьи").WhoseValue.Should().Be(50d, "внесение без статьи в названии и с общей категорией — одной строкой «Без статьи»");
    }

    [Fact]
    public void DepositRows_RejectedZeroAndOutOfPeriod_AreSkipped()
    {
        var rows = Rows(
            Flow("shift_drawer_inflow", "Внесение: Прочее", "100.00", "Внесение", status: "rejected"),
            Flow("shift_drawer_inflow", "Внесение: Прочее", "0.00", "Внесение"),
            Flow("shift_drawer_inflow", "Внесение: Прочее", "100.00", "Внесение", at: DateTimeOffset.Now.AddDays(-30)),
            Flow("shift_drawer_inflow", "Внесение: Прочее", "100.00", "Внесение"));

        Deposits(rows).Should().ContainSingle().Which.Amount.Should().Be(100d);
    }

    // ───────────── журнал событий смены: внесение пишется так же, как изъятие ─────────────

    private static string NewShift() => "shift-" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public void ShiftEvents_DepositAndWithdrawal_Other1000_BothRecorded()
    {
        var shift = NewShift();

        ShiftEventsStore.RecordDrawerOperation(withdrawal: false, shift, ShiftEventsStore.OperationKey("dep"), 1000d, "Прочее — тест").Should().BeTrue();
        ShiftEventsStore.RecordDrawerOperation(withdrawal: true, shift, ShiftEventsStore.OperationKey("wd"), 1000d, "Прочее — тест").Should().BeTrue();

        var totals = ShiftEventsStore.TotalsForShift(shift);
        totals.Should().ContainKey(ShiftEventsStore.KindDeposit).WhoseValue.Should().Be(1000d, "внесение раньше в журнал событий не писалось вовсе");
        totals.Should().ContainKey(ShiftEventsStore.KindExpense).WhoseValue.Should().Be(1000d);
        (totals[ShiftEventsStore.KindDeposit] - totals[ShiftEventsStore.KindExpense]).Should().Be(0d);
    }

    [Fact]
    public void ShiftEvents_TwoDepositsSameAmountSameShift_BothCounted()
    {
        var shift = NewShift();

        ShiftEventsStore.RecordDrawerOperation(false, shift, ShiftEventsStore.OperationKey("dep-1"), 1000d, "Прочее");
        ShiftEventsStore.RecordDrawerOperation(false, shift, ShiftEventsStore.OperationKey("dep-2"), 1000d, "Прочее");

        ShiftEventsStore.TotalsForShift(shift)[ShiftEventsStore.KindDeposit].Should().Be(2000d, "две одинаковые операции — две строки журнала, не одна");
    }

    [Fact]
    public void ShiftEvents_Deposit_DoesNotChangeExpenseReturnsOrDebt()
    {
        var shift = NewShift();

        ShiftEventsStore.RecordDrawerOperation(false, shift, ShiftEventsStore.OperationKey("dep"), 250d);

        var totals = ShiftEventsStore.TotalsForShift(shift);
        totals.Should().ContainSingle().Which.Key.Should().Be(ShiftEventsStore.KindDeposit);
        totals.ContainsKey(ShiftEventsStore.KindExpense).Should().BeFalse("внесение не расход: Z-отчёт и плитка «Расход» его не видят");
    }

    [Fact]
    public void FromDepositFlow_GenericServerCategory_GivesNoArticle_OtherCategoryIsKept()
    {
        CashArticles.FromDepositFlow("Внесение", "Внесение").Should().BeNull();
        CashArticles.FromDepositFlow("Внесение: Размен — утро", "Внесение")!.Key.Should().Be("change");
        CashArticles.FromDepositFlow("Внесение", "Выручка соседа")!.Ru.Should().Be("Выручка соседа", "внесение, заведённое на сайте со своей категорией, сохраняет её");
    }
}
