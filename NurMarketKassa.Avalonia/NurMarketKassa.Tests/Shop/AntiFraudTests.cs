using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-05 (волна 2-Б): тесты «Подозрительных операций», «Прав кассира (PIN старшего)» и «Чеков в Telegram». Без сети и без боевых данных:
/// файлы — во временной папке, имя бота подставляется и возвращается. Один класс — тесты идут по очереди (общие статические пути).
/// 2026-11-05: коллекция «UserPreferences» общая со снимками печати: раньше классы шли параллельно, и включённая здесь «ссылка на бумаге» (LinkOnPaper)
/// попадала в снимок чека SaleReceipt_Debt (общие статические настройки и имя бота).</summary>
[Collection("UserPreferences")]
public sealed class AntiFraudTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-antifraud-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string? _oldBot;

    public AntiFraudTests()
    {
        Directory.CreateDirectory(_dir);
        AntiFraudSettingsStore.DataDirOverride = _dir;
        AntiFraudSettingsStore.Reset();
        FraudEventStore.PathOverride = Path.Combine(_dir, "events.jsonl");
        TelegramReceiptLinks.ResetForTests();
        _oldBot = UserPreferences.Instance.TelegramBotUsername;
        UserPreferences.Instance.TelegramBotUsername = "TestShopBot";
    }

    public void Dispose()
    {
        UserPreferences.Instance.TelegramBotUsername = _oldBot;
        AntiFraudSettingsStore.DataDirOverride = null;
        AntiFraudSettingsStore.Reset();
        FraudEventStore.PathOverride = null;
        TelegramReceiptLinks.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка — не страшно */ }
    }

    private static CartRemovalRecord Removal(double total, string kind = CartRemovalLogStore.KindLine, string shift = "S1", int minutesAgo = 5, string title = "Товар") => new()
    {
        At = DateTimeOffset.Now.AddMinutes(-minutesAgo), Kind = kind, ShiftId = shift, Cashier = "Айдай", LineTotal = total, Title = title, ProductId = "p-" + title,
    };

    // ───────── правила ─────────

    [Fact]
    public void Everything_is_off_by_default()
    {
        var d = AntiFraudSettingsStore.Current;
        d.Fraud.Enabled.Should().BeFalse();
        d.Guard.Enabled.Should().BeFalse();
        d.Receipts.Enabled.Should().BeFalse();
        d.Receipts.LinkOnPaper.Should().BeFalse("бумага как раньше");
    }

    [Fact]
    public void Removal_threshold_works()
    {
        var r = new FraudRules { Removal = true, RemovalAmount = 500 };
        FraudAnalyzer.IsRemovalSuspicious(500, r).Should().BeTrue();
        FraudAnalyzer.IsRemovalSuspicious(499.99, r).Should().BeFalse();
        FraudAnalyzer.IsRemovalSuspicious(10000, new FraudRules { Removal = false }).Should().BeFalse();
    }

    [Fact]
    public void Return_is_suspicious_by_amount_or_without_receipt()
    {
        var r = new FraudRules { Return = true, ReturnAmount = 2000, ReturnNoReceipt = true };
        FraudAnalyzer.IsReturnSuspicious(2000, true, r).Should().BeTrue();
        FraudAnalyzer.IsReturnSuspicious(100, true, r).Should().BeFalse();
        FraudAnalyzer.IsReturnSuspicious(100, false, r).Should().BeTrue("возврат без чека — всегда");
        FraudAnalyzer.IsReturnSuspicious(100, false, new FraudRules { ReturnNoReceipt = false, Return = true, ReturnAmount = 2000 }).Should().BeFalse();
    }

    [Fact]
    public void Discount_percent_is_computed_from_percent_or_sum()
    {
        FraudAnalyzer.DiscountPercentOf("percent", 15, 1000).Should().Be(15);
        FraudAnalyzer.DiscountPercentOf("percent", 150, 1000).Should().Be(100, "не больше 100");
        FraudAnalyzer.DiscountPercentOf("sum", 250, 1000).Should().Be(25);
        FraudAnalyzer.DiscountPercentOf("sum", 250, 0).Should().Be(0, "база неизвестна — не выдумываем");
        FraudAnalyzer.DiscountPercentOf("percent", -5, 1000).Should().Be(0);
        FraudAnalyzer.IsDiscountSuspicious(20, new FraudRules { Discount = true, DiscountPercent = 20 }).Should().BeTrue();
        FraudAnalyzer.IsDiscountSuspicious(19.9, new FraudRules { Discount = true, DiscountPercent = 20 }).Should().BeFalse();
    }

    [Fact]
    public void Shortage_threshold_works()
    {
        var r = new FraudRules { Shortage = true, ShortageAmount = 300 };
        FraudAnalyzer.IsShortageSuspicious(300, r).Should().BeTrue();
        FraudAnalyzer.IsShortageSuspicious(299, r).Should().BeFalse();
        FraudAnalyzer.IsShortageSuspicious(0, r).Should().BeFalse("излишек и ноль — не недостача");
    }

    // ───────── источники ─────────

    [Fact]
    public void Removals_big_lines_and_many_per_shift_become_events()
    {
        var r = new FraudRules { Removal = true, RemovalAmount = 500, RemovalMany = true, RemovalPerShift = 10 };
        var records = new List<CartRemovalRecord> { Removal(600, title: "Дорогой"), Removal(100, title: "Дешёвый"), Removal(900, CartRemovalLogStore.KindShiftClose, title: "Сброс") };
        for (var i = 0; i < 12; i++)
            records.Add(Removal(10, shift: "S2", minutesAgo: i, title: "М" + i));
        var events = FraudAnalyzer.FromRemovals(records, r);
        events.Count(e => e.Kind == FraudKinds.Removal).Should().Be(1, "только строка на 600; сброс при закрытии смены и мелочь не считаются");
        var many = events.Single(e => e.Kind == FraudKinds.RemovalMany);
        many.Value.Should().Be(12);
        many.Id.Should().Be("many|S2");
        FraudAnalyzer.FromRemovals(records, new FraudRules { Removal = false, RemovalMany = false }).Should().BeEmpty();
    }

    [Fact]
    public void Loss_sales_become_below_cost_events()
    {
        var loss = new LossSaleRecord { At = DateTimeOffset.Now, SaleId = "sale1", Cashier = "Бек", Lines = { new LossSaleLine { Title = "Сахар", Loss = 12.5 } } };
        var events = FraudAnalyzer.FromLoss(new[] { loss }, new FraudRules { BelowCost = true });
        events.Should().ContainSingle().Which.Should().Match<FraudEvent>(e => e.Kind == FraudKinds.BelowCost && e.SaleId == "sale1" && e.Amount == 12.5);
        FraudAnalyzer.FromLoss(new[] { loss }, new FraudRules { BelowCost = false }).Should().BeEmpty();
    }

    // ───────── балл кассира ─────────

    [Fact]
    public void Score_sums_weights_per_cashier_and_ignores_pin_ok()
    {
        var now = DateTimeOffset.Now;
        FraudEvent E(string kind, string cashier, double amount = 0, int daysAgo = 0) => new() { Kind = kind, Cashier = cashier, Amount = amount, At = now.AddDays(-daysAgo) };
        var events = new[]
        {
            E(FraudKinds.Removal, "Айдай", 600), E(FraudKinds.ReturnNoReceipt, "Айдай", 300), E(FraudKinds.PinOk, "Айдай", 5000),
            E(FraudKinds.Discount, "Бек", 50), E(FraudKinds.Shortage, "Бек", 700, daysAgo: 30),
        };
        var score = FraudAnalyzer.Score(events, now.AddDays(-7), now.AddMinutes(1));
        score.Should().HaveCount(2);
        score[0].Cashier.Should().Be("Айдай");
        score[0].Points.Should().Be(FraudKinds.Weight(FraudKinds.Removal) + FraudKinds.Weight(FraudKinds.ReturnNoReceipt));
        score[0].Events.Should().Be(2, "подтверждение PIN — не подозрительное действие");
        score[0].Amount.Should().Be(900);
        score[1].Points.Should().Be(FraudKinds.Weight(FraudKinds.Discount), "недостача месячной давности в неделю не входит");
    }

    [Fact]
    public void Week_starts_on_monday()
    {
        var wednesday = new DateTimeOffset(2026, 11, 4, 15, 30, 0, TimeSpan.FromHours(6));
        FraudAnalyzer.WeekStart(wednesday).Date.Should().Be(new DateTime(2026, 11, 2));
        var sunday = new DateTimeOffset(2026, 11, 8, 23, 0, 0, TimeSpan.FromHours(6));
        FraudAnalyzer.WeekStart(sunday).Date.Should().Be(new DateTime(2026, 11, 2));
    }

    [Fact]
    public void Merge_removes_duplicates_by_id_and_sorts_newest_first()
    {
        var a = new FraudEvent { Id = "x", At = DateTimeOffset.Now.AddHours(-2) };
        var b = new FraudEvent { Id = "y", At = DateTimeOffset.Now.AddHours(-1) };
        var dup = new FraudEvent { Id = "x", At = DateTimeOffset.Now };
        var merged = FraudAnalyzer.Merge(new[] { a }, new[] { b, dup });
        merged.Select(e => e.Id).Should().Equal("y", "x");
    }

    // ───────── журнал и настройки ─────────

    [Fact]
    public void Event_log_appends_reads_and_survives_a_broken_last_line()
    {
        FraudEventStore.Append(new FraudEvent { Id = "1", At = DateTimeOffset.Now.AddMinutes(-2), Kind = FraudKinds.Return, Amount = 3000, Cashier = "Айдай" });
        File.AppendAllText(Path.Combine(_dir, "events.jsonl"), "{\"Id\":\"обрыв");
        FraudEventStore.Append(new FraudEvent { Id = "2", At = DateTimeOffset.Now, Kind = FraudKinds.Discount, Value = 30 });
        var all = FraudEventStore.ReadAll();
        all.Select(e => e.Id).Should().Equal("2", "1");
    }

    [Fact]
    public void Settings_are_saved_shared_and_normalized()
    {
        AntiFraudSettingsStore.Update(d =>
        {
            d.Fraud.Enabled = true;
            d.Fraud.RemovalAmount = -5;
            d.Fraud.QuietFromHour = 99;
            d.Fraud.DiscountPercent = 500;
            d.Guard.Enabled = true;
            d.Guard.DiscountPercent = 12;
        });
        AntiFraudSettingsStore.Reset();
        var d2 = AntiFraudSettingsStore.Current;
        d2.Fraud.Enabled.Should().BeTrue();
        d2.Fraud.RemovalAmount.Should().Be(0, "отрицательный порог обрезается");
        d2.Fraud.QuietFromHour.Should().Be(23);
        d2.Fraud.DiscountPercent.Should().Be(100);
        d2.Guard.DiscountPercent.Should().Be(12);
    }

    [Fact]
    public void Broken_settings_file_gives_defaults()
    {
        File.WriteAllText(Path.Combine(_dir, "antifraud-settings.json"), "{ это не json");
        AntiFraudSettingsStore.Reset();
        AntiFraudSettingsStore.Current.Fraud.Enabled.Should().BeFalse();
    }

    // ───────── PIN старшего ─────────

    [Fact]
    public void Guard_asks_pin_only_when_enabled_and_over_threshold()
    {
        var off = new ActionGuardRules { Enabled = false };
        ActionGuardPolicy.NeedsPin(GuardAction.Discount, 90, off).Should().BeFalse("выключено — как раньше");

        var g = new ActionGuardRules { Enabled = true, Discount = true, DiscountPercent = 10, Return = true, ReturnAmount = 1000, RemovalAfterRing = true, RemovalAmount = 500, ShiftShortage = true, ShortageAmount = 300 };
        ActionGuardPolicy.NeedsPin(GuardAction.Discount, 10, g).Should().BeFalse("ровно порог — ещё можно");
        ActionGuardPolicy.NeedsPin(GuardAction.Discount, 10.5, g).Should().BeTrue();
        ActionGuardPolicy.NeedsPin(GuardAction.Return, 999, g).Should().BeFalse();
        ActionGuardPolicy.NeedsPin(GuardAction.Return, 1000, g).Should().BeTrue();
        ActionGuardPolicy.NeedsPin(GuardAction.RemovalAfterRing, 499, g).Should().BeFalse();
        ActionGuardPolicy.NeedsPin(GuardAction.RemovalAfterRing, 500, g).Should().BeTrue();
        ActionGuardPolicy.NeedsPin(GuardAction.ShiftShortage, 0, g).Should().BeFalse();
        ActionGuardPolicy.NeedsPin(GuardAction.ShiftShortage, 300, g).Should().BeTrue();
        ActionGuardPolicy.NeedsPin(GuardAction.ManualPrice, 0, g).Should().BeFalse("пункт не отмечен");
        g.ManualPrice = true;
        ActionGuardPolicy.NeedsPin(GuardAction.ManualPrice, 0, g).Should().BeTrue();
    }

    [Fact]
    public void Guard_return_with_zero_threshold_asks_for_any_return()
    {
        var g = new ActionGuardRules { Enabled = true, Return = true, ReturnAmount = 0 };
        ActionGuardPolicy.NeedsPin(GuardAction.Return, 1, g).Should().BeTrue();
        ActionGuardPolicy.NeedsPin(GuardAction.Return, 0, g).Should().BeTrue();
    }

    // ───────── тексты и сообщения ─────────

    [Fact]
    public void Every_kind_has_a_title_and_a_description()
    {
        foreach (var kind in FraudKinds.All)
        {
            FraudText.Title(kind).Should().NotBeNullOrWhiteSpace();
            FraudText.Describe(new FraudEvent { Kind = kind, Cashier = "Айдай", Amount = 1234, Value = 25, Subject = kind.StartsWith("pin", StringComparison.Ordinal) ? nameof(GuardAction.Return) : "Чай" })
                .Should().Contain("Айдай");
        }
    }

    [Fact]
    public void Telegram_message_lists_events_and_escapes_html()
    {
        var one = new[] { new FraudEvent { Kind = FraudKinds.Removal, Cashier = "<Айдай>", Amount = 700, Subject = "Чай & кофе" } };
        var text = FraudMonitor.BuildMessage(one);
        text.Should().Contain("&lt;Айдай&gt;").And.Contain("Чай &amp; кофе").And.NotContain("<Айдай>");

        var many = Enumerable.Range(0, 12).Select(i => new FraudEvent { Kind = FraudKinds.Discount, Cashier = "К" + i, Value = 30 }).ToList();
        var big = FraudMonitor.BuildMessage(many);
        big.Should().Contain("…", "после десяти строк — многоточие");
        big.Split('\n').Count(l => l.StartsWith("• ", StringComparison.Ordinal)).Should().Be(10);
    }

    [Fact]
    public void Quiet_hours_cross_midnight()
    {
        var r = new FraudRules { QuietFromHour = 22, QuietToHour = 8 };
        FraudMonitor.InQuietHours(r, DateTime.Today.AddHours(23)).Should().BeTrue();
        FraudMonitor.InQuietHours(r, DateTime.Today.AddHours(3)).Should().BeTrue();
        FraudMonitor.InQuietHours(r, DateTime.Today.AddHours(12)).Should().BeFalse();
        FraudMonitor.InQuietHours(new FraudRules { QuietFromHour = 5, QuietToHour = 5 }, DateTime.Today.AddHours(5)).Should().BeFalse("равные часы — тихих часов нет");
    }

    // ───────── чеки в Telegram ─────────

    [Theory]
    [InlineData("0555 123 456", "555123456")]
    [InlineData("+996 (555) 123-456", "555123456")]
    [InlineData("996555123456", "555123456")]
    [InlineData("12345", "12345")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Phone_is_compared_by_last_nine_digits(string? phone, string expected) =>
        TelegramReceiptLinks.LastDigits(phone).Should().Be(expected);

    [Fact]
    public void Link_unlink_and_lookup_by_different_phone_formats()
    {
        TelegramReceiptLinks.Link("+996 555 123 456", "chat-1", "Айдай");
        TelegramReceiptLinks.ChatsForPhone("0555123456").Should().Equal("chat-1");
        TelegramReceiptLinks.ChatsForPhone("0555 999 999").Should().BeEmpty();
        TelegramReceiptLinks.ChatsForPhone("123").Should().BeEmpty("короткий номер не ищем");

        // тот же чат, другой номер — старая привязка заменяется
        TelegramReceiptLinks.Link("0700 111 222", "chat-1", "Айдай");
        TelegramReceiptLinks.ChatsForPhone("0555123456").Should().BeEmpty();
        TelegramReceiptLinks.ChatsForPhone("700111222").Should().Equal("chat-1");

        // один номер — два чата (два устройства)
        TelegramReceiptLinks.Link("0700 111 222", "chat-2", null);
        TelegramReceiptLinks.ChatsForPhone("700111222").Should().BeEquivalentTo(new[] { "chat-1", "chat-2" });

        TelegramReceiptLinks.Unlink("chat-1").Should().BeTrue();
        TelegramReceiptLinks.Unlink("chat-1").Should().BeFalse("отписка повторно — ничего не было");
        TelegramReceiptLinks.ChatsForPhone("700111222").Should().Equal("chat-2");
    }

    [Fact]
    public void Links_are_saved_to_the_shared_file_and_reread()
    {
        TelegramReceiptLinks.Link("0555123456", "chat-9", "Бек");
        TelegramReceiptLinks.ResetForTests();
        TelegramReceiptLinks.ChatsForPhone("555123456").Should().Equal("chat-9");
        File.Exists(Path.Combine(_dir, "telegram-receipt-links.json")).Should().BeTrue();
    }

    [Fact]
    public void Consent_given_to_another_bot_is_not_used()
    {
        TelegramReceiptLinks.Link("0555123456", "chat-1", null);
        UserPreferences.Instance.TelegramBotUsername = "OtherBot";
        TelegramReceiptLinks.ChatsForPhone("0555123456").Should().BeEmpty("согласие дано другому боту");
        UserPreferences.Instance.TelegramBotUsername = "TestShopBot";
        TelegramReceiptLinks.ChatsForPhone("0555123456").Should().Equal("chat-1");
    }

    [Fact]
    public void Link_with_a_bad_phone_or_empty_chat_is_ignored()
    {
        TelegramReceiptLinks.Link("12345", "chat-1", null);
        TelegramReceiptLinks.Link("0555123456", "", null);
        TelegramReceiptLinks.Count.Should().Be(0);
    }

    [Fact]
    public void Shop_link_and_start_parameter()
    {
        TelegramReceiptLinks.ShopLink().Should().Be("https://t.me/TestShopBot?start=receipts");
        TelegramReceiptLinks.IsReceiptsStart("receipts").Should().BeTrue();
        TelegramReceiptLinks.IsReceiptsStart("RECEIPTS").Should().BeTrue("на бумаге ссылка печатается заглавными");
        TelegramReceiptLinks.IsReceiptsStart("s_abc_def").Should().BeFalse();
        TelegramReceiptLinks.IsReceiptsStart(null).Should().BeFalse();
        UserPreferences.Instance.TelegramBotUsername = "@WithAt";
        TelegramReceiptLinks.ShopLink().Should().Be("https://t.me/WithAt?start=receipts");
        UserPreferences.Instance.TelegramBotUsername = "  ";
        TelegramReceiptLinks.ShopLink().Should().BeNull();
    }

    [Fact]
    public void Paper_lines_only_when_enabled_and_asked()
    {
        TelegramReceiptLinks.PaperLines().Should().BeEmpty();
        AntiFraudSettingsStore.Update(d => { d.Receipts.Enabled = true; });
        TelegramReceiptLinks.PaperLines().Should().BeEmpty("ссылка на бумаге — отдельная галочка");
        AntiFraudSettingsStore.Update(d => { d.Receipts.LinkOnPaper = true; });
        var lines = TelegramReceiptLinks.PaperLines();
        lines.Should().HaveCount(2);
        lines[1].Should().Be("t.me/TestShopBot?start=receipts");
    }

    [Fact]
    public void Receipt_message_has_bonus_debt_stop_hint_and_is_escaped()
    {
        var text = TelegramReceiptDelivery.BuildMessage("Чек № 1\nЧай <2> & сахар", "Остаток долга: 500 сом", 120.5, "Магазин & Ко");
        text.Should().StartWith("<b>Магазин &amp; Ко</b>");
        text.Should().Contain("<pre>").And.Contain("Чай &lt;2&gt; &amp; сахар");
        text.Should().Contain("Остаток долга: 500 сом");
        (text.Contains("120.5") || text.Contains("120,5")).Should().BeTrue("баланс бонусов в сообщении");
        text.Should().Contain("/stop");

        var plain = TelegramReceiptDelivery.BuildMessage("Чек", null, 0, "");
        plain.Should().Contain("NurCRM").And.NotContain("🎁").And.NotContain("⏰");
    }

    [Fact]
    public async Task Sending_does_nothing_while_the_feature_is_off()
    {
        TelegramReceiptLinks.Link("0555123456", "chat-1", null);
        var result = await TelegramReceiptDelivery.SendCardAsync("0555123456", null, new SaleReceiptCardData());
        result.Should().Be(TelegramReceiptSendResult.Off, "по умолчанию выключено — в сеть ничего не уходит");
    }
}
