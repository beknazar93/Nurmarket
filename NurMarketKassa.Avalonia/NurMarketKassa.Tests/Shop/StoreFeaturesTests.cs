using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-05 (функции 9, 11, 13): модульные тесты чистой логики — себестоимость с доставкой, «Пересчёт дня», сверка банковской выписки.
/// Без сети, окон и боевых данных (файловые хранилища — во временной папке).</summary>
public sealed class StoreFeaturesTests
{
    // ───────────── функция 11: себестоимость с доставкой ─────────────

    [Fact]
    public void Landed_by_amount_splits_in_proportion_to_line_total()
    {
        // строка 1: 10 шт × 100 = 1000; строка 2: 5 шт × 200 = 1000 → на каждую по 100 сом расходов
        var per = LandedCost.PerUnit([(10, 100), (5, 200)], 200, LandedMethod.ByAmount);
        per[0].Should().Be(10);
        per[1].Should().Be(20);
    }

    [Fact]
    public void Landed_by_quantity_gives_same_extra_to_every_unit()
    {
        var per = LandedCost.PerUnit([(10, 100), (5, 200)], 150, LandedMethod.ByQuantity);
        per[0].Should().Be(10);
        per[1].Should().Be(10);
    }

    [Fact]
    public void Landed_preview_shows_purchase_before_and_after_with_four_decimals()
    {
        // закупка 100,00 → 103,45: расходы 345 на 100 штук
        var line = new ReceivingLineVm { Quantity = 100, PurchasePrice = 100 };
        var rows = LandedCost.Preview([line], 345, LandedMethod.ByAmount);
        rows.Should().ContainSingle();
        rows[0].OldCost.Should().Be(100);
        rows[0].NewCost.Should().Be(103.45);
        rows[0].ExtraTotal.Should().Be(345);
    }

    [Fact]
    public void Landed_apply_does_not_touch_invoice_price_or_line_total()
    {
        var line = new ReceivingLineVm { Quantity = 4, PurchasePrice = 50 };
        var before = line.LineTotal;
        LandedCost.Apply(LandedCost.Preview([line], 40, LandedMethod.ByQuantity)).Should().Be(1);
        line.LandedExtraPerUnit.Should().Be(10);
        line.CostPrice.Should().Be(60);
        line.PurchasePrice.Should().Be(50);      // накладная и долг поставщику не меняются
        line.LineTotal.Should().Be(before);
        LandedCost.DistributedTotal([line]).Should().Be(40);
        LandedCost.Clear([line]);
        line.CostPrice.Should().Be(50);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void Landed_without_positive_extra_changes_nothing(double extra)
    {
        LandedCost.PerUnit([(3, 10)], extra, LandedMethod.ByAmount).Should().AllBeEquivalentTo(0);
    }

    [Fact]
    public void Landed_by_amount_falls_back_to_quantity_when_all_prices_are_zero()
    {
        var per = LandedCost.PerUnit([(2, 0), (2, 0)], 40, LandedMethod.ByAmount);
        per.Should().Equal(10, 10);
    }

    [Fact]
    public void Landed_skips_lines_with_zero_quantity_and_keeps_total_close()
    {
        var per = LandedCost.PerUnit([(0, 100), (3, 100), (7, 100)], 100, LandedMethod.ByQuantity);
        per[0].Should().Be(0);
        (per[1] * 3 + per[2] * 7).Should().BeApproximately(100, 0.001);
    }

    // ───────────── функция 9: «Пересчёт дня» ─────────────

    private static readonly DateTime Today = new(2026, 11, 5);

    private static CycleCandidate C(string id, string abc = "", double stock = 10, int? daysAgo = null, int deletions = 0) =>
        new(id, "Товар " + id, abc, stock, daysAgo is { } d ? Today.AddDays(-d) : null, deletions);

    [Fact]
    public void Cycle_negative_stock_comes_first_then_deletions_then_long_uncounted()
    {
        var picks = CycleCountPlanner.Pick([
            C("old", "C", daysAgo: 200),
            C("del", "B", daysAgo: 5, deletions: 4),
            C("neg", "C", stock: -3, daysAgo: 1),
            C("fresh", "A", daysAgo: 1),
        ], 3, Today);
        picks.Select(p => p.Item.Id).Should().Equal("neg", "del", "old");
        picks[0].Reasons.Should().Contain(CycleCountPlanner.ReasonNegative);
        picks[1].Reasons.Should().Contain(CycleCountPlanner.ReasonDeletions);
        picks[2].Reasons.Should().Contain(CycleCountPlanner.ReasonOverdue);
    }

    [Fact]
    public void Cycle_respects_count_and_skips_dead_items()
    {
        var list = Enumerable.Range(0, 60).Select(i => C("p" + i, "C", daysAgo: 100 + i)).ToList();
        list.Add(C("dead", "", stock: 0));
        var picks = CycleCountPlanner.Pick(list, 25, Today);
        picks.Should().HaveCount(25);
        picks.Select(p => p.Item.Id).Should().NotContain("dead");
    }

    [Fact]
    public void Cycle_a_class_is_due_weekly_and_c_class_quarterly()
    {
        var picks = CycleCountPlanner.Pick([C("a", "A", daysAgo: 8), C("c", "C", daysAgo: 8), C("c2", "C", daysAgo: 95)], 3, Today);
        picks.Select(p => p.Item.Id).Should().Equal("a", "c2", "c");   // A (8 дн. > 7) и C (95 дн. > 90) просрочены, свежий C — в конце
        picks.Single(p => p.Item.Id == "a").Reasons.Should().Contain(CycleCountPlanner.ReasonOverdue);
        picks.Single(p => p.Item.Id == "c").Reasons.Should().NotContain(CycleCountPlanner.ReasonOverdue);
    }

    [Fact]
    public void Cycle_pick_is_deterministic()
    {
        var items = Enumerable.Range(0, 40).Select(i => C("p" + i, i % 3 == 0 ? "A" : "C", daysAgo: 30)).ToList();
        var first = CycleCountPlanner.Pick(items, 20, Today).Select(p => p.Item.Id).ToList();
        var second = CycleCountPlanner.Pick(Enumerable.Reverse(items), 20, Today).Select(p => p.Item.Id).ToList();
        second.Should().Equal(first);
    }

    [Fact]
    public void Cycle_pin_is_needed_when_total_or_single_shortage_reaches_threshold()
    {
        // 3 недостачи по 2000 (закупка) — по отдельности меньше порога 5000, вместе 6000
        CycleCountRules.NeedsOwnerPin([(-1, 2000), (-1, 2000), (-1, 2000)], 5000).Should().BeTrue();
        CycleCountRules.NeedsOwnerPin([(-1, 2000), (-1, 2000)], 5000).Should().BeFalse();
        CycleCountRules.NeedsOwnerPin([(-10, 600)], 5000).Should().BeTrue();   // одна позиция
        CycleCountRules.NeedsOwnerPin([(+50, 1000)], 5000).Should().BeFalse(); // излишек не считается
        CycleCountRules.NeedsOwnerPin([(-1000, 1000)], 0).Should().BeFalse();  // порог 0 — выключено
    }

    [Fact]
    public void Cycle_reasons_note_lists_names_grouped_by_reason_and_is_capped()
    {
        var note = CycleCountReasons.NoteSuffix([("Молоко", -3, "spoilage"), ("Сыр", -1, "spoilage"), ("Кофе", 2, "mixup"), ("Хлеб", -1, null)]);
        note.Should().Contain("Молоко (−3)").And.Contain("Сыр (−1)").And.Contain("Кофе (+2)").And.NotContain("Хлеб");
        CycleCountReasons.NoteSuffix([("Хлеб", -1, null)]).Should().BeEmpty();
        var capped = CycleCountReasons.NoteSuffix(Enumerable.Range(0, 100).Select(i => ("Очень длинное название товара " + i, -1.0, (string?)"theft")), 120);
        capped.Length.Should().BeLessOrEqualTo(120);
    }

    [Fact]
    public void Cycle_history_is_stored_and_shown_in_the_card()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-cycle-" + Guid.NewGuid().ToString("N")[..8]);
        CycleCountStore.DataDirOverride = dir;
        CycleCountStore.Reset();
        try
        {
            CycleCountStore.HistoryText("p1").Should().BeEmpty();
            CycleCountStore.Record([("p1", -2.0, "spoilage"), ("p2", 0.0, null)], new DateTime(2026, 10, 12));
            CycleCountStore.LastCounted("p1").Should().Be(new DateTime(2026, 10, 12));
            CycleCountStore.HistoryText("p1").Should().Contain("12.10.2026").And.Contain("2");
            CycleCountStore.Reset();   // перечитать с диска
            CycleCountStore.LastCounted("p2").Should().Be(new DateTime(2026, 10, 12));
        }
        finally
        {
            CycleCountStore.DataDirOverride = null;
            CycleCountStore.Reset();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ───────────── функция 13: сверка банковской выписки ─────────────

    private static string[] Row(params string[] cells) => cells;

    [Fact]
    public void Bank_parser_reads_amount_column_with_direction_column()
    {
        var statement = BankStatementParser.Parse([
            Row("Выписка по счёту 1234"),
            Row(),
            Row("Дата операции", "Тип операции", "Сумма", "Назначение платежа"),
            Row("03.11.2026", "Приход", "12 345,50", "Эквайринг за 02.11"),
            Row("04.11.2026", "Расход", "5 000,00", "Оплата ОсОО Бишкек Трейд по счёту 15"),
            Row("Итого", "", "17 345,50", ""),
        ]);
        statement.Lines.Should().HaveCount(2);
        statement.Lines[0].Amount.Should().Be(12345.5);
        statement.Lines[0].Date.Should().Be(new DateTime(2026, 11, 3));
        statement.Lines[1].Amount.Should().Be(-5000);
        statement.Lines[1].Description.Should().Contain("Бишкек Трейд");
    }

    [Fact]
    public void Bank_parser_reads_debit_credit_columns_and_signed_amounts()
    {
        var pair = BankStatementParser.Parse([
            Row("Дата", "Дебет", "Кредит", "Описание"),
            Row("2026-11-03", "", "1,500.25", "QR оплата"),
            Row("2026-11-04", "300.00", "", "Комиссия"),
        ]);
        pair.Lines.Select(l => l.Amount).Should().Equal(1500.25, -300);

        var signed = BankStatementParser.Parse([
            Row("Date", "Amount", "Details"),
            Row("05.11.2026", "-1 200,00", "Аренда"),
            Row("05.11.2026", "(250)", "Перевод"),
            Row("06.11.2026", "800", "Поступление"),
        ]);
        signed.Lines.Select(l => l.Amount).Should().Equal(-1200, -250, 800);
    }

    [Theory]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234", 1234)]
    [InlineData("0,500", 0.5)]
    [InlineData("12.5", 12.5)]
    [InlineData("500-", -500)]
    [InlineData("−75,5", -75.5)]
    [InlineData("1.234.567", 1234567)]
    public void Bank_amounts_are_parsed_in_all_common_formats(string text, double expected)
    {
        BankStatementParser.TryParseAmount(text, out var value).Should().BeTrue();
        value.Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void Bank_parser_without_header_returns_warning_not_exception()
    {
        var statement = BankStatementParser.Parse([Row("что-то"), Row("не то")]);
        statement.Lines.Should().BeEmpty();
        statement.Warnings.Should().NotBeEmpty();
    }

    private static ShiftCashless Shift(string id, int day, double amount, int receipts = 5) => new(id, new DateTime(2026, 11, day), amount, receipts, "Смена " + id);

    private static BankLine In(int row, int day, double amount, string text = "") => new(row, new DateTime(2026, 11, day), amount, text);

    [Fact]
    public void Bank_reconcile_matches_cashless_by_day_and_amount_and_reports_difference()
    {
        var shifts = new[] { Shift("s1", 1, 10000), Shift("s2", 2, 20000, 7), Shift("s3", 3, 5000, 3) };
        var bank = new[] { In(1, 2, 10000), In(2, 3, 19999.8), In(3, 5, 4000) };   // третья смена не найдена: 4000 не равно 5000 и не в пределах комиссии
        var r = BankReconciler.Reconcile(bank, shifts, [], [], []);
        r.Lines[0].Kind.Should().Be(BankLineKind.SalesMatched);
        r.Lines[1].Kind.Should().Be(BankLineKind.SalesMatched);
        r.Lines[2].Kind.Should().Be(BankLineKind.UnknownIncome);
        r.ShiftsNotFound.Select(s => s.Id).Should().Equal("s3");
        r.ReceiptsNotFound.Should().Be(3);
        r.KassaTotal.Should().Be(35000);
        r.BankTotal.Should().Be(33999.8);
        r.Difference.Should().Be(1000.2);
    }

    [Fact]
    public void Bank_reconcile_matches_a_lump_credit_for_several_days_and_a_fee()
    {
        var shifts = new[] { Shift("fri", 6, 3000), Shift("sat", 7, 4000), Shift("sun", 8, 1000), Shift("mon", 9, 10000) };
        var bank = new[] { In(1, 10, 8000, "зачисление за выходные"), In(2, 10, 9800, "эквайринг") };   // 9800 = 10000 − 2 % комиссии
        var r = BankReconciler.Reconcile(bank, shifts, [], [], []);
        r.Lines[0].Kind.Should().Be(BankLineKind.SalesMatched);
        r.Lines[0].ShiftIds.Should().BeEquivalentTo(["fri", "sat", "sun"]);
        r.Lines[1].Kind.Should().Be(BankLineKind.SalesWithFee);
        r.ShiftsNotFound.Should().BeEmpty();
    }

    [Fact]
    public void Bank_reconcile_matches_supplier_payments_by_amount_then_by_name()
    {
        var suppliers = new[] { new SupplierOutgoing(new DateTime(2026, 11, 1), 15000, "ОсОО Бишкек Трейд") };
        var bank = new[]
        {
            new BankLine(1, new DateTime(2026, 11, 4), -15000, "Оплата по счёту 15"),
            new BankLine(2, new DateTime(2026, 11, 5), -999, "Оплата ИП Бишкек Трейд за товар"),
            new BankLine(3, new DateTime(2026, 11, 6), -4200, "Аренда помещения ноябрь"),
        };
        var r = BankReconciler.Reconcile(bank, [], suppliers, ["ОсОО Бишкек Трейд"], []);
        r.Lines[0].Kind.Should().Be(BankLineKind.SupplierPayment);
        r.Lines[0].Supplier.Should().Contain("Бишкек");
        r.Lines[1].Kind.Should().Be(BankLineKind.SupplierByName);
        r.Lines[2].Kind.Should().Be(BankLineKind.UnknownExpense);
    }

    [Fact]
    public void Bank_rule_remembered_from_a_description_applies_to_similar_lines()
    {
        var pattern = BankRules.MakePattern("Оплата аренды помещения по договору №15 от 01.10.2026");
        pattern.Should().Be("аренды помещения");
        var rules = new[] { new BankRule(pattern, "rent") };
        var bank = new[]
        {
            new BankLine(1, new DateTime(2026, 11, 6), -4200, "Оплата аренды помещения по договору №17 от 01.11.2026"),
            new BankLine(2, new DateTime(2026, 11, 7), 500, "Перевод от учредителя"),
        };
        var r = BankReconciler.Reconcile(bank, [], [], [], [.. rules, new BankRule(BankRules.MakePattern("Перевод от учредителя"), "not_sales")]);
        r.Lines[0].Kind.Should().Be(BankLineKind.Rule);
        r.Lines[0].Category.Should().Be("rent");
        r.Lines[1].Category.Should().Be("not_sales");
        r.BankTotal.Should().Be(0); // «не от продаж» в сумму по банку не входит
    }

    [Fact]
    public void Bank_month_summary_counts_only_this_months_shifts_and_credits()
    {
        // смена 31.10 зачислена 02.11 — в итог ноября не входит; смена 03.11 зачислена; смена 04.11 не найдена; поступление 20.11 неопознанное — входит в банк
        var shifts = new[]
        {
            new ShiftCashless("old", new DateTime(2026, 10, 31), 1000, 4, "old"),
            Shift("a", 3, 5000, 5),
            Shift("b", 4, 2000, 3),
        };
        var bank = new[] { In(1, 2, 1000), In(2, 4, 5000), In(3, 20, 300, "перевод") };
        var result = BankReconciler.Reconcile(bank, shifts, [], [], []);
        var m = BankReconciler.Summarize(result, shifts, new DateTime(2026, 11, 1), new DateTime(2026, 11, 30));
        m.Kassa.Should().Be(7000);
        m.Bank.Should().Be(5300);
        m.Difference.Should().Be(1700);
        m.ShiftsNotFound.Should().Be(1);
        m.ReceiptsNotFound.Should().Be(3);
    }

    [Fact]
    public void Bank_rules_store_remembers_and_survives_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-bank-" + Guid.NewGuid().ToString("N")[..8]);
        BankRulesStore.DataDirOverride = dir;
        BankRulesStore.Reset();
        try
        {
            BankRulesStore.Remember("Оплата за интернет Нур Телеком", "utilities").Should().BeTrue();
            BankRulesStore.Remember("оплата", "other_expense").Should().BeFalse(); // одни служебные слова — запоминать нечего
            BankRulesStore.Reset();
            BankRulesStore.All.Should().ContainSingle().Which.Category.Should().Be("utilities");
        }
        finally
        {
            BankRulesStore.DataDirOverride = null;
            BankRulesStore.Reset();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
