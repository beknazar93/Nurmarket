using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-05 (волна 2-А): модульные тесты новой логики — просрочка, автозаказ v2, XYZ, подсказки цены, правила оповещений. Без сети и без боевых данных.</summary>
public sealed class ShopAutomationTests
{
    private static readonly DateTime Now = DateTime.Today.AddHours(15);

    private static DateTime DayUtc(int daysBack, int hour = 12) => DateTime.Today.AddDays(-daysBack).AddHours(hour).ToUniversalTime();

    private static PlannerProduct Product(string id = "p1", double stock = 10, double price = 100, bool weighted = false) =>
        new(id, "Товар " + id, "", "", "шт", stock, price, weighted);

    private static List<PlannerSale> Daily(string id, double qty, int fromBack, int toBack)
    {
        var list = new List<PlannerSale>();
        for (var d = fromBack; d <= toBack; d++)
            list.Add(new PlannerSale(id, qty, DayUtc(d)));
        return list;
    }

    private static readonly IReadOnlyDictionary<string, (DateTime At, double Qty)> NoLast = new Dictionary<string, (DateTime, double)>();

    // ───────────── просрочка ─────────────

    private static ProductExpiryIndex.Mark Mark(string id, int daysLeft, double qty = 5) =>
        new(id, "Молоко", DateTime.Today.AddDays(daysLeft), daysLeft, daysLeft < 0, qty);

    [Fact]
    public void Expiry_decision_depends_on_mode_and_date()
    {
        var expired = Mark("a", -3);
        var fresh = Mark("b", 10);
        ExpiryRules.Decide(ExpiryModes.Off, expired, DateTime.Today).Should().Be(ExpirySaleDecision.Allow);
        ExpiryRules.Decide(ExpiryModes.Warn, expired, DateTime.Today).Should().Be(ExpirySaleDecision.Warn);
        ExpiryRules.Decide(ExpiryModes.Block, expired, DateTime.Today).Should().Be(ExpirySaleDecision.NeedOwnerPin);
        ExpiryRules.Decide(ExpiryModes.Warn, fresh, DateTime.Today).Should().Be(ExpirySaleDecision.Allow);
        ExpiryRules.Decide(ExpiryModes.Warn, null, DateTime.Today).Should().Be(ExpirySaleDecision.Allow);
        ExpiryRules.Decide("мусор", expired, DateTime.Today).Should().Be(ExpirySaleDecision.Warn, "неизвестный режим = предупреждать");
    }

    [Fact]
    public void Expiry_date_in_the_past_counts_even_if_server_flag_is_off()
    {
        var m = new ProductExpiryIndex.Mark("a", "Сыр", DateTime.Today.AddDays(-1), 0, false, 1);
        ExpiryRules.IsExpired(m, DateTime.Today).Should().BeTrue();
        ExpiryRules.IsExpired(Mark("b", 0), DateTime.Today).Should().BeFalse("срок сегодня — ещё годен");
    }

    [Fact]
    public void Expired_summary_counts_products_and_cost_by_purchase_price()
    {
        var marks = new[] { Mark("a", -3, 4), Mark("b", -1, 0), Mark("c", 5, 100) };
        var s = ExpiryRules.Summarize(marks, DateTime.Today, id => id == "a" ? 50 : 10);
        s.Count.Should().Be(2);
        s.Cost.Should().Be(200, "4 шт × 50; товар без остатка в сумму не входит");
        ExpiryRules.SummaryText(s).Should().NotBeNullOrEmpty();
        ExpiryRules.SummaryText(new ExpiredSummary(0, 0, Array.Empty<ProductExpiryIndex.Mark>())).Should().BeEmpty();
    }

    // ───────────── автозаказ v2 ─────────────

    private static PlannerV2 V2(PlannerV2Options o, int lead = 0, IReadOnlyList<PlannerReceipt>? receipts = null,
        IReadOnlyDictionary<string, PlannerOrderRule>? rules = null, IReadOnlyDictionary<string, string>? classes = null) =>
        new(o, lead, receipts ?? Array.Empty<PlannerReceipt>(), rules ?? new Dictionary<string, PlannerOrderRule>(), classes ?? new Dictionary<string, string>());

    [Fact]
    public void Planner_without_v2_or_with_everything_off_is_the_same_as_before()
    {
        var sales = Daily("p1", 2, 0, 29);
        var settings = new PlannerSettings(30, 14, 3);
        var plain = PurchasePlanner.Build(new[] { Product() }, sales, NoLast, settings, Now, null);
        var off = PurchasePlanner.Build(new[] { Product() }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options()));
        plain[0].PerDay.Should().BeApproximately(2, 1e-9);
        plain[0].Recommended.Should().Be(24, "2 в день × (14 + 3) − остаток 10");
        off[0].Recommended.Should().Be(plain[0].Recommended);
        off[0].Status.Should().Be(plain[0].Status);
        off[0].PerDay.Should().Be(plain[0].PerDay);
    }

    [Fact]
    public void Lost_demand_ignores_days_without_stock()
    {
        // Приход 30 шт 15 дней назад, продано по 2 в день за 15 дней, остаток 0 — остальные 14 дней товара не было.
        var sales = Daily("p1", 2, 0, 14);
        var receipts = new[] { new PlannerReceipt("p1", 30, DayUtc(15)) };
        var settings = new PlannerSettings(30, 14, 3);
        var plain = PurchasePlanner.Build(new[] { Product(stock: 0) }, sales, NoLast, settings, Now, null);
        var v2 = PurchasePlanner.Build(new[] { Product(stock: 0) }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(LostDemand: true), receipts: receipts));
        plain[0].PerDay.Should().BeApproximately(1.0, 1e-9);
        v2[0].PerDay.Should().BeApproximately(1.5, 1e-9, "по дням на полке вышло бы 2 в день, но поправка ограничена в 1,5 раза (ревью 05.11, С3)");
        v2[0].Recommended.Should().BeGreaterThan(plain[0].Recommended);
    }

    [Fact]
    public void Stockout_days_counted_from_reconstructed_stock()
    {
        var sales = Daily("p1", 2, 0, 14);
        var receipts = new[] { new PlannerReceipt("p1", 30, DayUtc(15)) };
        PlannerV2Math.StockoutDays(0, sales, receipts, Now, 30, null).Should().Be(14);
        // остаток есть — пустых дней нет
        PlannerV2Math.StockoutDays(500, sales, receipts, Now, 30, null).Should().Be(0);
    }

    [Fact]
    public void Lost_demand_never_divides_by_less_than_a_third_of_the_period()
    {
        var sales = new[] { new PlannerSale("p1", 3, DayUtc(0)) };
        var rows = PurchasePlanner.Build(new[] { Product(stock: 0) }, sales, NoLast, new PlannerSettings(30, 14, 3), Now, null,
            V2(new PlannerV2Options(LostDemand: true)));
        rows[0].PerDay.Should().BeLessOrEqualTo(3.0 / 30 * PlannerV2Math.LostDemandMaxBoost + 1e-9, "не больше чем в 1,5 раза выше обычной");
    }

    [Theory]
    [InlineData(24, 10, 0, 30)]   // кратно упаковке 10: 24 → 30
    [InlineData(24, 0, 50, 50)]   // минимальная партия 50
    [InlineData(24, 12, 0, 24)]   // уже кратно
    [InlineData(0, 10, 50, 0)]    // заказывать нечего — минимум не навязывается
    public void Order_rule_rounds_to_pack_and_min_batch(double qty, double pack, double min, double expected)
    {
        PlannerV2Math.ApplyRule(qty, new PlannerOrderRule(pack, min), false).Should().Be(expected);
    }

    [Fact]
    public void Planner_applies_pack_rule_only_when_enabled()
    {
        var sales = Daily("p1", 2, 0, 29);
        var rules = new Dictionary<string, PlannerOrderRule> { ["p1"] = new(10, 0) };
        var settings = new PlannerSettings(30, 14, 3);
        var on = PurchasePlanner.Build(new[] { Product() }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(PackRules: true), rules: rules));
        var off = PurchasePlanner.Build(new[] { Product() }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(), rules: rules));
        on[0].Recommended.Should().Be(30);
        off[0].Recommended.Should().Be(24);
    }

    [Fact]
    public void Order_rules_apply_to_server_rows_too()
    {
        var row = PurchasePlanner.Build(new[] { Product() }, Daily("p1", 2, 0, 29), NoLast, new PlannerSettings(), Now, null)[0];
        var fixedRows = PlannerV2Math.ApplyOrderRules(new[] { row }, new Dictionary<string, PlannerOrderRule> { ["p1"] = new(0, 100) });
        fixedRows[0].Recommended.Should().Be(100);
        fixedRows[0].Status.Should().Be(row.Status);
    }

    [Fact]
    public void Lead_time_adds_days_of_demand()
    {
        var sales = Daily("p1", 2, 0, 29);
        var settings = new PlannerSettings(30, 14, 3);
        var none = PurchasePlanner.Build(new[] { Product() }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(LeadTime: true), lead: 0));
        var week = PurchasePlanner.Build(new[] { Product() }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(LeadTime: true), lead: 5));
        (week[0].Recommended - none[0].Recommended).Should().Be(10, "5 дней × 2 в день");
    }

    [Fact]
    public void Lead_time_is_ignored_when_option_is_off()
    {
        var sales = Daily("p1", 2, 0, 29);
        var settings = new PlannerSettings(30, 14, 3);
        var rows = PurchasePlanner.Build(new[] { Product() }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(), lead: 5));
        rows[0].Recommended.Should().Be(24);
    }

    [Fact]
    public void Tsb_of_constant_demand_is_that_demand_and_decays_after_silence()
    {
        PlannerV2Math.Tsb(Enumerable.Repeat(2.0, 30).ToList()).Should().BeApproximately(2.0, 1e-9);
        PlannerV2Math.Tsb(new List<double>()).Should().Be(0);
        PlannerV2Math.Tsb(new double[30]).Should().Be(0);
        var spikeOld = new double[30];
        spikeOld[0] = 5;
        var spikeNew = new double[30];
        spikeNew[29] = 5;
        PlannerV2Math.Tsb(spikeOld).Should().BeLessThan(PlannerV2Math.Tsb(spikeNew), "недавний спрос весит больше давнего");
    }

    [Fact]
    public void Rare_product_uses_tsb_and_regular_product_does_not()
    {
        var rare = new List<PlannerSale> { new("p1", 5, DayUtc(2)), new("p1", 5, DayUtc(12)), new("p1", 5, DayUtc(22)) };
        var settings = new PlannerSettings(30, 14, 3);
        var plain = PurchasePlanner.Build(new[] { Product(stock: 0) }, rare, NoLast, settings, Now, null);
        var tsb = PurchasePlanner.Build(new[] { Product(stock: 0) }, rare, NoLast, settings, Now, null, V2(new PlannerV2Options(Tsb: true)));
        tsb[0].PerDay.Should().BeGreaterThan(0).And.NotBe(plain[0].PerDay);
        var regular = Daily("p1", 2, 0, 29);
        var same = PurchasePlanner.Build(new[] { Product() }, regular, NoLast, settings, Now, null, V2(new PlannerV2Options(Tsb: true)));
        same[0].PerDay.Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public void Weekday_weights_average_to_one_and_favour_busy_days()
    {
        var sales = new List<PlannerSale>();
        for (var d = 0; d < 56; d++)
        {
            var day = DateTime.Today.AddDays(-d);
            sales.Add(new PlannerSale("p1", day.DayOfWeek == DayOfWeek.Saturday ? 6 : 2, day.AddHours(12).ToUniversalTime()));
        }
        var w = PlannerV2Math.WeekdayWeights(sales, Now);
        w.Average().Should().BeApproximately(1, 1e-9);
        w[(int)DayOfWeek.Saturday].Should().BeGreaterThan(w[(int)DayOfWeek.Monday] * 2);
        PlannerV2Math.HorizonDays(null, Now, 17).Should().Be(17);
        PlannerV2Math.HorizonDays(w, Now, 7).Should().BeApproximately(7, 1e-6, "полная неделя = 7 средних дней");
    }

    [Fact]
    public void Weekday_weights_are_flat_without_enough_history()
    {
        PlannerV2Math.WeekdayWeights(Daily("p1", 2, 0, 5), Now).Should().OnlyContain(x => x == 1.0);
    }

    [Fact]
    public void Xyz_safety_changes_recommendation_by_class()
    {
        var sales = Daily("p1", 2, 0, 29);
        var settings = new PlannerSettings(30, 14, 3);
        Dictionary<string, string> Cls(string c) => new() { ["p1"] = c };
        var ax = PurchasePlanner.Build(new[] { Product(stock: 0) }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(XyzSafety: true), classes: Cls("AX")))[0];
        var cz = PurchasePlanner.Build(new[] { Product(stock: 0) }, sales, NoLast, settings, Now, null, V2(new PlannerV2Options(XyzSafety: true), classes: Cls("CZ")))[0];
        var plain = PurchasePlanner.Build(new[] { Product(stock: 0) }, sales, NoLast, settings, Now, null)[0];
        ax.Recommended.Should().BeGreaterThan(plain.Recommended);
        cz.Recommended.Should().BeLessThan(plain.Recommended);
        cz.Recommended.Should().Be(28, "без страхового запаса: 2 × 14");
    }

    [Fact]
    public void Auto_order_pick_respects_supplier_minimum_sum()
    {
        var rows = PurchasePlanner.Build(new[] { Product(stock: 0, price: 10) }, Daily("p1", 2, 0, 29), NoLast, new PlannerSettings(), Now, null);
        var supplier = new NurMarketKassa.Services.Api.SupplierInfo("s1", "Поставщик", "", "", "", "", "", "", "", "", null);
        var data = new ShopAutomationData();
        AutoOrderCheck.Pick(supplier, rows, data, out var waiting).Should().NotBeNull();
        waiting.Should().BeFalse();
        data.Suppliers["s1"] = new SupplierPlanRule { MinOrderSum = 100000 };
        AutoOrderCheck.Pick(supplier, rows, data, out waiting).Should().BeNull();
        waiting.Should().BeTrue("сумма ниже минимальной — ждёт");
    }

    [Fact]
    public void Ask_prices_text_lists_products_without_purchase_prices()
    {
        var text = AutoOrderCheck.AskPricesText("Мой магазин", new[] { ("Молоко 1 л", 24.0, "шт"), ("Сыр", 0.0, "кг") });
        text.Should().Contain("Молоко 1 л").And.Contain("24").And.Contain("Мой магазин");
        text.Should().NotContain("Сыр", "строки с нулевым количеством не спрашиваем");
    }

    // ───────────── XYZ ─────────────

    private static IEnumerable<(string, string, double, double, DateTime)> Weekly(string key, IEnumerable<double> perWeek, double price = 10)
    {
        var w = 0;
        foreach (var qty in perWeek)
        {
            if (qty > 0)
                yield return (key, "Товар " + key, qty, qty * price, DateTime.Today.AddDays(-(7 * w + 3)));
            w++;
        }
    }

    [Fact]
    public void Xyz_classifies_stable_and_erratic_demand()
    {
        var lines = Weekly("stable", Enumerable.Repeat(10.0, 12))
            .Concat(Weekly("erratic", new double[] { 60, 0, 0, 0, 60, 0, 0, 0, 60, 0, 0, 0 }))
            .Concat(Weekly("medium", new double[] { 6, 14, 6, 14, 6, 14, 6, 14, 6, 14, 6, 14 })).ToList();
        var r = AbcXyz.Compute(lines, DateTime.Today);
        r.Items.First(i => i.Key == "stable").Xyz.Should().Be("X");
        r.Items.First(i => i.Key == "erratic").Xyz.Should().Be("Z");
        r.Items.First(i => i.Key == "medium").Xyz.Should().Be("Y", "CV = 0,4");
        r.Items.Sum(i => r.Count(i.Cell)).Should().BeGreaterThan(0);
        r.CellCounts.Values.Sum().Should().Be(3);
    }

    [Fact]
    public void Xyz_does_not_punish_new_products_and_marks_short_history()
    {
        var lines = new List<(string, string, double, double, DateTime)> { ("new", "Новинка", 5, 50, DateTime.Today.AddDays(-4)), ("new", "Новинка", 5, 50, DateTime.Today.AddDays(-10)) };
        var r = AbcXyz.Compute(lines, DateTime.Today);
        r.Items.Single().Xyz.Should().BeNull();
        r.Items.Single().Cell.Should().EndWith("?");
        AbcXyz.Hint(r.Items.Single().Cell).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Abc_groups_follow_cumulative_share_before_the_item()
    {
        var lines = Weekly("big", Enumerable.Repeat(100.0, 12), 10)
            .Concat(Weekly("mid", Enumerable.Repeat(10.0, 12), 10))
            .Concat(Weekly("tiny", Enumerable.Repeat(1.0, 12), 10)).ToList();
        var r = AbcXyz.Compute(lines, DateTime.Today);
        r.Items.First(i => i.Key == "big").Abc.Should().Be("A");
        r.Items.First(i => i.Key == "mid").Abc.Should().Be("B");
        r.Items.First(i => i.Key == "tiny").Abc.Should().Be("C");
    }

    [Theory]
    [InlineData(0.0, "X")]
    [InlineData(0.25, "X")]
    [InlineData(0.26, "Y")]
    [InlineData(0.5, "Y")]
    [InlineData(0.51, "Z")]
    public void Xyz_limits(double cv, string expected) => AbcXyz.XyzClass(cv).Should().Be(expected);

    [Fact]
    public void Safety_multiplier_is_highest_for_ax_and_zero_for_cz()
    {
        AbcXyz.SafetyMultiplier("AX").Should().BeGreaterThan(AbcXyz.SafetyMultiplier("BX"));
        AbcXyz.SafetyMultiplier("CZ").Should().Be(0);
        AbcXyz.SafetyMultiplier(null).Should().Be(1);
        AbcXyz.SafetyMultiplier("A?").Should().Be(1);
    }

    // ───────────── подсказки цены ─────────────

    [Theory]
    [InlineData(93.4, 5, 95)]
    [InlineData(95.0, 5, 95)]
    [InlineData(95.01, 5, 100)]
    [InlineData(91, 10, 100)]
    [InlineData(7.3, 10, 8)]       // дешёвый товар — до целого, а не до 10
    [InlineData(7.3, 0, 7.3)]
    [InlineData(0, 5, 0)]
    public void Nice_price_rounds_up_to_step(double price, int step, double expected) => PriceHints.NiceUp(price, step).Should().Be(expected);

    [Fact]
    public void Price_hint_keeps_old_markup_and_rounds_nicely()
    {
        var data = new ShopAutomationData { PriceNiceStep = 5 };
        // закупка 80 → 90, продажа 104 (наценка 30 %) осталась прежней → 90 × 1,3 = 117 → 120
        var hint = PriceHints.For("p", "Молоко", null, 80, 90, 104, 104, data, new ReceivingMarkupSettings());
        hint.Should().NotBeNull();
        hint!.SuggestedSale.Should().Be(120);
        hint.SuggestedMarkup.Should().BeApproximately(33.33, 0.01);
        hint.Text.Should().Contain("104").And.Contain("120");
    }

    [Fact]
    public void Price_hint_prefers_category_target_markup()
    {
        var data = new ShopAutomationData { PriceNiceStep = 0 };
        data.CategoryTargetMarkup["Молочка"] = 50;
        PriceHints.For("p", "Молоко", "молочка", 80, 90, 104, 104, data, new ReceivingMarkupSettings())!.SuggestedSale.Should().Be(135);
    }

    [Fact]
    public void Price_hint_is_silent_when_nothing_to_raise()
    {
        var data = new ShopAutomationData();
        var m = new ReceivingMarkupSettings();
        PriceHints.For("p", "x", null, 80, 80, 104, 104, data, m).Should().BeNull("закупка не выросла");
        PriceHints.For("p", "x", null, 80, 90, 104, 120, data, m).Should().BeNull("цену уже поправили");
        PriceHints.For("p", "x", null, 0, 90, 104, 104, data, m).Should().BeNull("прежней закупки нет");
    }

    // ───────────── оповещения ─────────────

    [Theory]
    [InlineData(23, true)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(12, false)]
    [InlineData(21, false)]
    [InlineData(22, true)]
    public void Quiet_hours_wrap_over_midnight(int hour, bool quiet)
    {
        var rules = new ShopAlertRules { QuietFromHour = 22, QuietToHour = 8 };
        ShopAlerts.InQuietHours(rules, DateTime.Today.AddHours(hour)).Should().Be(quiet);
    }

    [Fact]
    public void Equal_quiet_hours_means_no_quiet_time()
    {
        ShopAlerts.InQuietHours(new ShopAlertRules { QuietFromHour = 5, QuietToHour = 5 }, DateTime.Today.AddHours(3)).Should().BeFalse();
    }

    [Fact]
    public void Shortage_alert_only_above_threshold_and_only_when_enabled()
    {
        var on = new ShopAlertRules { Enabled = true, ShiftShortage = true, ShiftShortageAmount = 500 };
        ShopAlerts.ShiftShortageText(-600m, on, "Айгуль").Should().Contain("Айгуль");
        ShopAlerts.ShiftShortageText(-500m, on, null).Should().BeNull("ровно порог — не оповещаем");
        ShopAlerts.ShiftShortageText(-100m, on, null).Should().BeNull();
        ShopAlerts.ShiftShortageText(900m, on, null).Should().BeNull("излишек — не недостача");
        ShopAlerts.ShiftShortageText(null, on, null).Should().BeNull();
        ShopAlerts.ShiftShortageText(-600m, new ShopAlertRules { Enabled = false, ShiftShortage = true }, null).Should().BeNull();
        ShopAlerts.ShiftShortageText(-600m, new ShopAlertRules { Enabled = true, ShiftShortage = false }, null).Should().BeNull();
    }

    [Fact]
    public void Revenue_behind_plan_compares_with_typical_weekday()
    {
        var rules = new ShopAlertRules { Enabled = true, RevenueBehindPlan = true, RevenueBehindPercent = 30, RevenueShareByCheck = 70 };
        // обычный день 10 000, к 18:00 ждём 70 % = 7 000, «ниже на 30 %» = ниже 4 900
        ShopAlerts.RevenueBehind(4000, 10000, rules).Should().BeTrue();
        ShopAlerts.RevenueBehind(5000, 10000, rules).Should().BeFalse();
        ShopAlerts.RevenueBehind(0, 0, rules).Should().BeFalse("нет обычного дня — нечем сравнить");
        ShopAlerts.RevenueBehind(100, 10000, new ShopAlertRules { Enabled = true, RevenueBehindPlan = false }).Should().BeFalse();
    }

    [Fact]
    public void Typical_day_revenue_uses_same_weekday_of_the_last_four_weeks()
    {
        var today = DateTime.Today;
        var byDay = new List<(DateTime, double)>
        {
            (today.AddDays(-7), 10000), (today.AddDays(-14), 12000), (today.AddDays(-21), 0), (today.AddDays(-3), 99999), (today.AddDays(-35), 50000),
        };
        ShopAlerts.TypicalDayRevenue(byDay, today).Should().Be(11000, "только тот же день недели, не старше 4 недель, нулевые дни не считаются");
        ShopAlerts.TypicalDayRevenue(new List<(DateTime, double)> { (today.AddDays(-7), 5) }, today).Should().Be(0, "одного дня мало");
    }

    [Fact]
    public void Settings_normalize_fixes_garbage_and_keeps_defaults_safe()
    {
        var d = new ShopAutomationData { ExpiryMode = "x", PriceNiceStep = 7 };
        d.Alerts.QuietFromHour = 99;
        d.Alerts.RevenueBehindPercent = 0;
        d.Suppliers["s"] = new SupplierPlanRule { LeadDays = 500, MinOrderSum = -5 };
        d.Normalize();
        d.ExpiryMode.Should().Be(ExpiryModes.Warn);
        d.PriceNiceStep.Should().Be(5);
        d.Alerts.QuietFromHour.Should().Be(23);
        d.Alerts.RevenueBehindPercent.Should().Be(1);
        d.Suppliers["s"].LeadDays.Should().Be(90);
        d.Suppliers["s"].MinOrderSum.Should().Be(0);
        new ShopAutomationData().Alerts.Enabled.Should().BeFalse("оповещения по умолчанию выключены");
        new ShopAutomationData().PlanLostDemand.Should().BeFalse("автозаказ v2 по умолчанию выключен");
    }

    // ───────────── тексты, файл настроек, состояние оповещений, черновики заказов ─────────────

    [Fact]
    public void Reserve_suppliers_and_markups_parse_and_format()
    {
        var reserve = ShopAutomationText.ParseReserve("Алтын; 0555 123 456\n\n  Бета  \nалтын; 1\n;нет имени");
        reserve.Select(r => r.Name).Should().Equal("Алтын", "Бета");
        reserve[0].Phone.Should().Be("0555 123 456");
        ShopAutomationText.ParseReserve(ShopAutomationText.FormatReserve(reserve)).Select(r => r.Phone).Should().Equal("0555 123 456", "");

        var marks = ShopAutomationText.ParseMarkups("Молочка = 35\nХлеб=12,5 %\nМусор\nНоль = 0\n= 5");
        marks.Should().HaveCount(2);
        marks["молочка"].Should().Be(35);
        marks["Хлеб"].Should().Be(12.5);
        ShopAutomationText.ParseMarkups(ShopAutomationText.FormatMarkups(marks)).Should().BeEquivalentTo(marks);
        ShopAutomationText.ParseNumber("1 250,5").Should().Be(1250.5);
        ShopAutomationText.ParseNumber("abc").Should().BeNull();
        ShopAutomationText.ParseNumber("").Should().BeNull();
    }

    [Fact]
    public void Settings_survive_a_restart_and_a_broken_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "shopauto-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ShopAutomationSettings.DataDirOverride = dir;
            ShopAutomationSettings.Reset();
            ShopAutomationSettings.Current.PlanTsb.Should().BeFalse();
            ShopAutomationSettings.Update(d =>
            {
                d.PlanTsb = true;
                d.Suppliers["s1"] = new SupplierPlanRule { LeadDays = 4, MinOrderSum = 1500 };
                d.Products["p1"] = new ProductPlanRule { Pack = 12, MinQty = 24 };
                d.Alerts.Enabled = true;
            });
            ShopAutomationSettings.Reset(); // «перезапуск»: читаем с диска
            var again = ShopAutomationSettings.Current;
            again.PlanTsb.Should().BeTrue();
            again.Suppliers["S1"].LeadDays.Should().Be(4, "ключи без учёта регистра");
            again.Products["p1"].Pack.Should().Be(12);
            again.Alerts.Enabled.Should().BeTrue();
            PlannerV2Math.RulesFrom(again)["p1"].MinQty.Should().Be(24);
            PlannerV2Math.LeadDaysOf(again, "s1").Should().Be(4);
            PlannerV2Math.LeadDaysOf(again, "нет").Should().Be(0);

            File.WriteAllText(Path.Combine(dir, "shop-automation.json"), "{ это не json");
            ShopAutomationSettings.Reset();
            ShopAutomationSettings.Current.PlanTsb.Should().BeFalse("испорченный файл — умолчания");
            Directory.GetFiles(dir, "shop-automation.bad-*.json").Should().NotBeEmpty("испорченный файл отложен рядом");
        }
        finally
        {
            ShopAutomationSettings.DataDirOverride = null;
            ShopAutomationSettings.Reset();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Alert_keys_are_sent_once_per_day()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "shopalerts-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ShopAlerts.DataDirOverride = dir;
            ShopAlerts.ResetForTests();
            var now = new DateTime(2026, 11, 5, 10, 0, 0);
            ShopAlerts.WasSentToday("expired", now).Should().BeFalse();
            ShopAlerts.TryMarkSentToday("expired", now).Should().BeTrue();
            ShopAlerts.TryMarkSentToday("expired", now.AddHours(3)).Should().BeFalse();
            ShopAlerts.WasSentToday("expired", now).Should().BeTrue();
            ShopAlerts.TryMarkSentToday("expired", now.AddDays(1)).Should().BeTrue("новый день — снова можно");
            ShopAlerts.ResetForTests(); // с диска
            ShopAlerts.WasSentToday("expired", now.AddDays(1)).Should().BeTrue();
        }
        finally
        {
            ShopAlerts.DataDirOverride = null;
            ShopAlerts.ResetForTests();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Draft_orders_are_made_per_supplier_with_recommended_quantities()
    {
        var rows = PurchasePlanner.Build(new[] { Product("a", 0, 10), Product("b", 0, 20) }, Daily("a", 2, 0, 29).Concat(Daily("b", 1, 0, 29)).ToList(), NoLast, new PlannerSettings(), Now, null);
        var s1 = new NurMarketKassa.Services.Api.SupplierInfo("s1", "Первый", "0700", "", "", "", "", "", "", "", null);
        var s2 = new NurMarketKassa.Services.Api.SupplierInfo("s2", "Второй", "", "", "", "", "", "", "", "", null);
        var result = new AutoOrderResult(new[] { new AutoOrderSupplier(s1, rows.Where(r => r.Product.Id == "a").ToList()), new AutoOrderSupplier(s2, rows.Where(r => r.Product.Id == "b").ToList()) }, 0, 0);
        result.Count.Should().Be(2);
        result.Total.Should().Be(Math.Round(34 * 10 + 17 * 20.0, 2), "(2 × 17) × 10 + (1 × 17) × 20");
        AutoOrderCheck.SummaryText(result).Should().Contain("2");
        var made = AutoOrderCheck.MakeDrafts(result, "тест");
        made.Should().HaveCount(2);
        made[0].State.Should().Be("draft");
        made[0].Lines.Should().ContainSingle().Which.Qty.Should().Be(34);
        made[0].Total.Should().Be(340);
        made[1].SupplierName.Should().Be("Второй");
        AutoOrderCheck.SummaryText(new AutoOrderResult(Array.Empty<AutoOrderSupplier>(), 0, 0)).Should().BeEmpty();
    }

    [Fact]
    public void Draft_button_from_telegram_works_once_and_refuses_stale_data()
    {
        AutoOrderCheck.Latest = null;
        AutoOrderCheck.MakeDraftsFromLatest("x").Should().NotBeNullOrEmpty("нет данных — понятный отказ, ничего не создаётся");
        var rows = PurchasePlanner.Build(new[] { Product("a", 0, 10) }, Daily("a", 2, 0, 29), NoLast, new PlannerSettings(), Now, null);
        var s1 = new NurMarketKassa.Services.Api.SupplierInfo("s9", "Тестовый", "", "", "", "", "", "", "", "", null);
        AutoOrderCheck.Latest = new AutoOrderResult(new[] { new AutoOrderSupplier(s1, rows) }, 0, 0);
        AutoOrderCheck.LatestAt = DateTime.Now.AddHours(-30);
        AutoOrderCheck.MakeDraftsFromLatest("x");
        AutoOrderCheck.Latest.Should().NotBeNull("старше суток — отказ без создания заказов");
        AutoOrderCheck.LatestAt = DateTime.Now;
        AutoOrderCheck.MakeDraftsFromLatest("x");
        AutoOrderCheck.Latest.Should().BeNull("после нажатия список сброшен — второе нажатие второй раз заказы не создаст");
    }

    // ───────────── ревью волн 0–3 (05.11.2026) ─────────────

    private static string SharedSettingsFile => Path.Combine(AppPaths.Local, "NurMarketKassa", "shop-automation.json");

    [Fact]
    public void Settings_are_one_shared_file_that_merges_old_kassa_and_owner_files()
    {
        // В2: раньше у кассы и владельца были разные файлы — запрет просрочки владельца не действовал на кассе.
        AppPaths.Roaming.Should().StartWith(NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path, "тест не должен трогать настоящие данные");
        var kassaOld = Path.Combine(AppPaths.Roaming, "NurMarketKassa", "shop-automation.json");
        var ownerOld = Path.Combine(AppPaths.Roaming, "NurMarketOwner", "shop-automation.json");
        try
        {
            ShopAutomationSettings.DataDirOverride = null;
            if (File.Exists(SharedSettingsFile))
                File.Delete(SharedSettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(kassaOld)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ownerOld)!);
            File.WriteAllText(kassaOld, "{\"ExpiryMode\":\"warn\",\"Suppliers\":{\"k1\":{\"LeadDays\":3,\"MinOrderSum\":0}}}");
            File.WriteAllText(ownerOld, "{\"ExpiryMode\":\"block\",\"Alerts\":{\"Enabled\":true},\"Suppliers\":{\"o1\":{\"LeadDays\":5,\"MinOrderSum\":100}}}");
            File.SetLastWriteTimeUtc(kassaOld, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(ownerOld, DateTime.UtcNow.AddHours(-1));
            ShopAutomationSettings.Reset();

            var merged = ShopAutomationSettings.Current;
            merged.ExpiryMode.Should().Be(ExpiryModes.Block, "защита владельца не ослабляется");
            merged.Alerts.Enabled.Should().BeTrue();
            merged.Suppliers.Should().ContainKey("k1").And.ContainKey("o1", "правила поставщиков из обоих файлов сохранены");
            File.Exists(SharedSettingsFile).Should().BeTrue("общий файл записан");
            File.Exists(kassaOld).Should().BeTrue("старые файлы не удаляются");

            // Другая программа изменила общий файл — следующая правка начинается с её содержимого, а не затирает его.
            var other = System.Text.Json.JsonSerializer.Serialize(new ShopAutomationData { PlanTsb = true, ExpiryMode = ExpiryModes.Block });
            File.WriteAllText(SharedSettingsFile, other);
            File.SetLastWriteTimeUtc(SharedSettingsFile, DateTime.UtcNow.AddMinutes(5));
            ShopAutomationSettings.Update(d => d.PlanWeekday = true);
            ShopAutomationSettings.Current.PlanTsb.Should().BeTrue("правка другой программы не потеряна");
            ShopAutomationSettings.Current.PlanWeekday.Should().BeTrue();
        }
        finally
        {
            foreach (var f in new[] { SharedSettingsFile, kassaOld, ownerOld })
                try { File.Delete(f); } catch (IOException) { }
            ShopAutomationSettings.Reset();
        }
    }

    [Fact]
    public void Settings_update_swaps_the_object_so_background_readers_keep_a_consistent_copy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "shopauto-cow-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ShopAutomationSettings.DataDirOverride = dir;
            ShopAutomationSettings.Reset();
            var before = ShopAutomationSettings.Current;
            ShopAutomationSettings.Update(d => d.Suppliers["x"] = new SupplierPlanRule { LeadDays = 2 });
            before.Suppliers.Should().NotContainKey("x", "старый объект не меняется под читателем");
            ShopAutomationSettings.Current.Suppliers.Should().ContainKey("x");
        }
        finally
        {
            ShopAutomationSettings.DataDirOverride = null;
            ShopAutomationSettings.Reset();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Alert_state_is_shared_and_old_personal_queues_are_merged()
    {
        // В1: очередь до утра, созданная кассой, не должна лежать в личной папке кассы, которую никто не досылает.
        var shared = Path.Combine(AppPaths.Local, "NurMarketKassa", "shop-alerts-state.json");
        var kassaOld = Path.Combine(AppPaths.Roaming, "NurMarketKassa", "shop-alerts-state.json");
        var ownerOld = Path.Combine(AppPaths.Roaming, "NurMarketOwner", "shop-alerts-state.json");
        try
        {
            ShopAlerts.DataDirOverride = null;
            if (File.Exists(shared))
                File.Delete(shared);
            Directory.CreateDirectory(Path.GetDirectoryName(kassaOld)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ownerOld)!);
            File.WriteAllText(kassaOld, "{\"Sent\":{\"k|1\":\"2026-11-05\"},\"Pending\":[{\"Title\":\"Недостача\",\"Details\":\"400\",\"At\":\"2026-11-05T22:30:00\"}]}");
            File.WriteAllText(ownerOld, "{\"Sent\":{\"o|1\":\"2026-11-05\"},\"Pending\":[{\"Title\":\"Просрочка\",\"Details\":\"3\",\"At\":\"2026-11-05T23:00:00\"}]}");
            ShopAlerts.ResetForTests();
            ShopAlerts.PendingCount.Should().Be(2, "ночные сообщения из обеих программ собраны в общую очередь");
            ShopAlerts.WasSentToday("k|1", new DateTime(2026, 11, 5, 12, 0, 0)).Should().BeTrue();
            File.Exists(shared).Should().BeTrue();
        }
        finally
        {
            foreach (var f in new[] { shared, kassaOld, ownerOld })
                try { File.Delete(f); } catch (IOException) { }
            ShopAlerts.ResetForTests();
        }
    }

    [Fact]
    public void Latest_order_check_survives_in_a_shared_file()
    {
        // С14: кнопку «Сформировать заказы» в Telegram может принять другая программа.
        var file = Path.Combine(AppPaths.Local, "NurMarketKassa", "auto-order-latest.json");
        try
        {
            var rows = PurchasePlanner.Build(new[] { Product("a", 0, 10) }, Daily("a", 2, 0, 29), NoLast, new PlannerSettings(), Now, null);
            var s1 = new NurMarketKassa.Services.Api.SupplierInfo("s77", "Общий", "", "", "", "", "", "", "", "", null);
            AutoOrderCheck.Latest = new AutoOrderResult(new[] { new AutoOrderSupplier(s1, rows) }, 1, 0);
            AutoOrderCheck.LatestAt = DateTime.Now;
            File.Exists(file).Should().BeTrue();
            // «Другая программа» записала файл позже — читаем из файла, а не из памяти.
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(10));
            var again = AutoOrderCheck.Latest;
            again.Should().NotBeNull();
            again!.Suppliers.Should().ContainSingle().Which.Supplier.Id.Should().Be("s77");
            again.Suppliers[0].Rows.Should().ContainSingle().Which.Recommended.Should().BeGreaterThan(0);
            again.Waiting.Should().Be(1);
            AutoOrderCheck.LatestAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
            AutoOrderCheck.Latest = null;
            File.Exists(file).Should().BeFalse("после «Сформировать» список сброшен и в файле");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
            AutoOrderCheck.Latest = null;
        }
    }

    [Fact]
    public void Planner_v2_options_know_which_functions_need_the_till_calculation()
    {
        new PlannerV2Options(PackRules: true, LeadTime: true).NeedsLocalCalc.Should().BeFalse("упаковка и срок поставки работают и с сервером");
        new PlannerV2Options(LostDemand: true).NeedsLocalCalc.Should().BeTrue();
        new PlannerV2Options(Tsb: true).NeedsLocalCalc.Should().BeTrue();
        new PlannerV2Options(Weekday: true).NeedsLocalCalc.Should().BeTrue();
        new PlannerV2Options(XyzSafety: true).NeedsLocalCalc.Should().BeTrue();
    }
}
