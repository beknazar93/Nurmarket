using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 («Рассылки по сегментам клиентов», ТОП-15 п. 2): правила рассылки без окна — сегменты, список получателей и защита от повторов
/// (не беспокоить, раз в N дней, один номер — один раз, дневной лимит), подстановки в текст без утечки чужих данных, дни рождения, отказ клиента, журнал и шаблоны.
/// Файл настроек в этих тестах на диск не пишется (<see cref="ClientMailing.ResetForTests"/>).</summary>
[Collection("UiScenarios")]
public sealed class ClientMailingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0);

    public ClientMailingTests()
    {
        TestHost.Ensure();
        ClientMailing.ResetForTests();
    }

    public void Dispose() => ClientMailing.ResetForTests();

    private static ClientMailing.Contact C(string id, string name = "Клиент", string phone = "0700 111 222", double debt = 0, int? lastDaysAgo = null, double spent = 0, DateTime? birthday = null) =>
        new(id, name, phone, ClientMailing.WhatsAppNumber(phone), debt, lastDaysAgo is { } d ? Now.Date.AddDays(-d) : null, spent, spent > 0 ? 1 : 0, birthday); // 2026-11-26: то же правило, что ClientMailingData

    private static ClientMailing.Settings S() => new();

    // ── Сегменты ────────────────────────────────────────────────────────────────

    [Fact]
    public void Segments_pick_the_right_clients()
    {
        var s = S(); // 30 дней, VIP от 20 000, 7 дней до дня рождения
        ClientMailing.InSegment(ClientMailing.Segment.Debtors, C("a", debt: 100), s, Now).Should().BeTrue();
        ClientMailing.InSegment(ClientMailing.Segment.Debtors, C("a", debt: 0), s, Now).Should().BeFalse();

        ClientMailing.InSegment(ClientMailing.Segment.Inactive, C("a", lastDaysAgo: 30), s, Now).Should().BeTrue("ровно N дней — уже давно");
        ClientMailing.InSegment(ClientMailing.Segment.Inactive, C("a", lastDaysAgo: 29), s, Now).Should().BeFalse();
        ClientMailing.InSegment(ClientMailing.Segment.Inactive, C("a", lastDaysAgo: null), s, Now).Should().BeFalse("ни одной покупки за год — не «давно не покупал», а неизвестно");

        ClientMailing.InSegment(ClientMailing.Segment.Vip, C("a", spent: 20000), s, Now).Should().BeTrue();
        ClientMailing.InSegment(ClientMailing.Segment.Vip, C("a", spent: 19999), s, Now).Should().BeFalse();
        var zero = S();
        zero.VipMinSpent = 0;
        ClientMailing.InSegment(ClientMailing.Segment.Vip, C("a", spent: 0), zero, Now).Should().BeFalse("VIP без покупок не бывает даже при пороге 0");

        ClientMailing.InSegment(ClientMailing.Segment.Birthday, C("a", birthday: new DateTime(2000, 11, 29)), s, Now).Should().BeTrue();
        ClientMailing.InSegment(ClientMailing.Segment.Birthday, C("a", birthday: new DateTime(2000, 12, 20)), s, Now).Should().BeFalse();
        ClientMailing.InSegment(ClientMailing.Segment.Birthday, C("a", birthday: null), s, Now).Should().BeFalse();

        ClientMailing.InSegment(ClientMailing.Segment.AllWithPhone, C("a"), s, Now).Should().BeTrue();
    }

    [Fact]
    public void Birthday_distance_handles_today_year_end_and_leap_day()
    {
        ClientMailing.DaysToBirthday(new DateTime(2000, 11, 26), Now).Should().Be(0);
        ClientMailing.DaysToBirthday(new DateTime(2000, 11, 25), Now).Should().Be(364, "вчера — до следующего почти год");
        ClientMailing.DaysToBirthday(new DateTime(2000, 1, 2), new DateTime(2026, 12, 30)).Should().Be(3, "через Новый год");
        ClientMailing.DaysToBirthday(new DateTime(2000, 2, 29), new DateTime(2027, 2, 27)).Should().Be(1, "29 февраля в невисокосный год — 28 февраля");
        ClientMailing.DaysToBirthday(null, Now).Should().BeNull();
    }

    [Theory]
    [InlineData("15.03", 3, 15)]
    [InlineData("1.3", 3, 1)]
    [InlineData("15.03.1990", 3, 15)]
    [InlineData("15/03", 3, 15)]
    [InlineData("1990-03-15", 3, 15)]
    [InlineData("29.02", 2, 29)]
    public void Birthday_text_is_parsed_to_day_and_month(string text, int month, int day)
    {
        ClientMailing.TryParseBirthday(text, out var value).Should().BeTrue();
        value.Should().Be(new DateTime(2000, month, day));
    }

    [Theory]
    [InlineData("31.02")]
    [InlineData("32.01")]
    [InlineData("15.13")]
    [InlineData("вчера")]
    [InlineData("15")]
    public void Birthday_garbage_is_rejected(string text) => ClientMailing.TryParseBirthday(text, out _).Should().BeFalse();

    [Fact]
    public void Empty_birthday_text_clears_the_date_without_error()
    {
        ClientMailing.TryParseBirthday("  ", out var value).Should().BeTrue();
        value.Should().BeNull();
    }

    // ── Список получателей и защита ─────────────────────────────────────────────

    private static ClientMailing.PlanResult Plan(ClientMailing.Segment seg, IEnumerable<ClientMailing.Contact> contacts, ISet<string>? dnd = null, Dictionary<string, DateTime>? mailed = null,
        ClientMailing.Settings? s = null, int openedToday = 0) =>
        ClientMailing.Plan(seg, contacts, s ?? S(), dnd ?? new HashSet<string>(), mailed ?? new Dictionary<string, DateTime>(), Now, openedToday);

    [Fact]
    public void Plan_counts_people_without_phone_but_does_not_list_them()
    {
        var plan = Plan(ClientMailing.Segment.Debtors, [C("a", debt: 100), C("b", phone: "", debt: 200), C("c", phone: "abc", debt: 300)]);
        plan.InSegment.Should().Be(3);
        plan.NoPhone.Should().Be(2);
        plan.Items.Select(i => i.Contact.ClientId).Should().Equal("a");
    }

    [Fact]
    public void Do_not_disturb_and_recent_messages_are_skipped_with_a_reason()
    {
        var mailed = new Dictionary<string, DateTime> { ["b"] = Now.AddDays(-3), ["c"] = Now.AddDays(-14) };
        var plan = Plan(ClientMailing.Segment.AllWithPhone,
            [C("a", "А", "0700 000 001"), C("b", "Б", "0700 000 002"), C("c", "В", "0700 000 003"), C("d", "Г", "0700 000 004")],
            dnd: new HashSet<string> { "a" }, mailed: mailed);
        var by = plan.Items.ToDictionary(i => i.Contact.ClientId);
        by["a"].Skip.Should().Be(ClientMailing.SkipReason.DoNotDisturb);
        by["b"].Skip.Should().Be(ClientMailing.SkipReason.RecentlyWritten, "писали 3 дня назад, окно 14 дней");
        by["c"].Skip.Should().Be(ClientMailing.SkipReason.None, "ровно 14 дней назад — снова можно");
        by["d"].Skip.Should().Be(ClientMailing.SkipReason.None);
        plan.Items.Take(2).Should().OnlyContain(i => i.IsDue, "доступные — сверху");
    }

    [Fact]
    public void One_phone_is_one_chat_even_for_two_clients()
    {
        var plan = Plan(ClientMailing.Segment.Debtors, [C("a", "Айбек", "0700 111 222", debt: 500), C("b", "Айбек (брат)", "+996 700 111 222", debt: 100)]);
        plan.Items.Count(i => i.IsDue).Should().Be(1);
        plan.Items.Single(i => !i.IsDue).Skip.Should().Be(ClientMailing.SkipReason.DuplicatePhone);
        plan.Items.First().Contact.ClientId.Should().Be("a", "у кого больше долг — тот остаётся в списке");
    }

    [Fact]
    public void Daily_limit_counts_chats_already_opened_today()
    {
        var s = S();
        s.DailyLimit = 5;
        var contacts = Enumerable.Range(1, 10).Select(i => C("c" + i, "К" + i, $"0700 000 {i:000}")).ToList();
        Plan(ClientMailing.Segment.AllWithPhone, contacts, s: s).Items.Count(i => i.IsDue).Should().Be(5);
        Plan(ClientMailing.Segment.AllWithPhone, contacts, s: s, openedToday: 3).Items.Count(i => i.IsDue).Should().Be(2, "три чата уже открыты — осталось два");
        Plan(ClientMailing.Segment.AllWithPhone, contacts, s: s, openedToday: 5).Items.Should().OnlyContain(i => i.Skip == ClientMailing.SkipReason.DailyLimit);
    }

    [Fact]
    public void Segments_are_ordered_by_what_matters()
    {
        Plan(ClientMailing.Segment.Debtors, [C("a", "А", "0700 000 001", debt: 100), C("b", "Б", "0700 000 002", debt: 900)]).Items.Select(i => i.Contact.ClientId).Should().Equal("b", "a");
        Plan(ClientMailing.Segment.Vip, [C("a", "А", "0700 000 001", spent: 30000), C("b", "Б", "0700 000 002", spent: 90000)]).Items.Select(i => i.Contact.ClientId).Should().Equal("b", "a");
        Plan(ClientMailing.Segment.Birthday, [C("a", "А", "0700 000 001", birthday: new DateTime(2000, 12, 1)), C("b", "Б", "0700 000 002", birthday: new DateTime(2000, 11, 27))])
            .Items.Select(i => i.Contact.ClientId).Should().Equal("b", "a");
    }

    // ── Согласие ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ClientMailing.Segment.Vip, true, false, true, true)]       // маркетинг выключен с датой — отказ
    [InlineData(ClientMailing.Segment.Vip, true, true, true, false)]
    [InlineData(ClientMailing.Segment.Vip, false, false, false, false)]    // «не спрашивали» — не отказ
    [InlineData(ClientMailing.Segment.Inactive, false, false, true, true)]
    [InlineData(ClientMailing.Segment.Debtors, true, false, true, false)]  // долг — сервисное сообщение: хватает сервисного согласия
    [InlineData(ClientMailing.Segment.Debtors, false, false, true, true)]  // оба выключены с датой — отказ (как у кнопки «WA»)
    [InlineData(ClientMailing.Segment.Debtors, false, false, false, false)]
    public void Refusal_depends_on_the_kind_of_message(ClientMailing.Segment segment, bool service, bool marketing, bool hasDate, bool expected) =>
        ClientMailing.IsRefused(segment, service, marketing, hasDate).Should().Be(expected);

    // ── Текст ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_puts_only_this_clients_data_into_the_text()
    {
        var a = C("a", "Айбек", debt: 12500, lastDaysAgo: 40);
        var b = C("b", "Бермет", debt: 300, lastDaysAgo: 50);
        const string t = "Здравствуйте, {имя}! Магазин «{магазин}»: долг {долг} сом, не были {дней} дн.";
        var ra = ClientMailing.Render(t, a, "Нур", Now);
        var rb = ClientMailing.Render(t, b, "Нур", Now);
        ra.Should().Be($"Здравствуйте, Айбек! Магазин «Нур»: долг {DebtWhatsAppLink.FormatAmount(12500)} сом, не были 40 дн.");
        ra.Should().NotContain("Бермет").And.NotContain("300");
        rb.Should().Contain("Бермет").And.NotContain("Айбек").And.NotContain("12");
    }

    [Fact]
    public void Render_understands_latin_aliases_and_any_case()
    {
        var c = C("a", "Айбек", debt: 100, lastDaysAgo: 10);
        ClientMailing.Render("{NAME} {Shop} {debt} {Days} {amount}", c, "Нур", Now).Should().Be("Айбек Нур 100 10 100");
        ClientMailing.Render("{неизвестно} {имя}", c, "Нур", Now).Should().Be("{неизвестно} Айбек", "незнакомые слова остаются как набрал владелец");
    }

    [Theory]
    [InlineData("Здравствуйте, {имя}! Заходите.", "Здравствуйте! Заходите.")]
    [InlineData("{имя}, поздравляем!", "поздравляем!")]
    [InlineData("Магазин «{магазин}» ждёт.", "Магазин ждёт.")]
    public void Render_without_a_name_or_shop_leaves_no_gaps(string template, string expected)
    {
        var c = C("a", "", lastDaysAgo: 5);
        ClientMailing.Render(template, c, "", Now).Should().Be(expected);
    }

    [Fact]
    public void Default_templates_exist_for_every_segment_and_language_with_known_words_only()
    {
        var known = new HashSet<string> { "{имя}", "{долг}", "{магазин}", "{дней}" };
        foreach (var seg in ClientMailing.Segments)
            foreach (var lang in ClientMailing.Languages)
            {
                var t = ClientMailing.DefaultTemplate(seg, lang);
                t.Should().NotBeNullOrWhiteSpace($"{seg} {lang}");
                t.Length.Should().BeInRange(30, ClientMailing.MaxTextLength / 2, $"{seg} {lang}: короткий текст — ссылка WhatsApp не вырастет");
                foreach (Match m in Regex.Matches(t, @"\{[^}]*\}"))
                    known.Should().Contain(m.Value, $"{seg} {lang}: неизвестная подстановка");
                t.Should().Contain("{имя}", $"{seg} {lang}: обращение по имени");
                if (seg == ClientMailing.Segment.Debtors)
                    t.Should().Contain("{долг}");
            }
        ClientMailing.DefaultTemplate(ClientMailing.Segment.Debtors, "ky").Should().NotBe(ClientMailing.DefaultTemplate(ClientMailing.Segment.Debtors, "ru"));
        ClientMailing.DefaultTemplate(ClientMailing.Segment.Debtors, "uz").Should().NotBe(ClientMailing.DefaultTemplate(ClientMailing.Segment.Debtors, "ru"));
    }

    [Fact]
    public void Worst_case_text_still_fits_into_a_whatsapp_link()
    {
        // Самый длинный стандартный текст (кириллица кодируется по 6 знаков на букву) + очень длинные имя и название магазина.
        var c = C("a", ClientMailingFixture.LongName, debt: 99_999_999, lastDaysAgo: 300);
        foreach (var seg in ClientMailing.Segments)
            foreach (var lang in ClientMailing.Languages)
            {
                var text = ClientMailing.Render(ClientMailing.DefaultTemplate(seg, lang), c, new string('М', 40), Now);
                var url = DebtWhatsAppLink.BuildUrl(c.Phone, text);
                url.Should().NotBeNull();
                ClientMailing.UrlTooLong(url).Should().BeFalse($"{seg} {lang}: ссылка {url!.Length} знаков");
            }
        ClientMailing.UrlTooLong(new string('x', 2001)).Should().BeTrue();
    }

    [Fact]
    public void Link_opens_one_chat_for_one_number_and_carries_the_text()
    {
        var c = C("a", "Айбек", "0700 111 222", debt: 100);
        var url = DebtWhatsAppLink.BuildUrl(c.Phone, ClientMailing.Render("Привет, {имя}!", c, "Нур", Now));
        url.Should().StartWith("https://wa.me/996700111222?text=");
        Uri.UnescapeDataString(url![(url.IndexOf("text=", StringComparison.Ordinal) + 5)..]).Should().Be("Привет, Айбек!");
    }

    // ── Хранилище: не беспокоить, журнал, шаблоны, настройки ────────────────────

    [Fact]
    public void Do_not_disturb_is_remembered_and_can_be_undone()
    {
        ClientMailing.SetDoNotDisturb("a", "Айбек", true);
        ClientMailing.DoNotDisturb().Should().Contain("a");
        ClientMailing.DoNotDisturbNames()["a"].Should().Be("Айбек");
        ClientMailing.SetDoNotDisturb("a", "Айбек", false);
        ClientMailing.DoNotDisturb().Should().BeEmpty();
    }

    [Fact]
    public void Journal_marks_clients_counts_the_day_and_masks_the_phone()
    {
        var c = C("a", "Айбек", "0700 111 222");
        ClientMailing.LogOpened(c, ClientMailing.Segment.Vip, "ky", Now);
        ClientMailing.LastMailed()["a"].Should().Be(Now);
        ClientMailing.OpenedToday(Now).Should().Be(1);
        ClientMailing.OpenedToday(Now.AddDays(1)).Should().Be(0);
        var entry = ClientMailing.Journal().Single();
        entry.Phone.Should().NotContain("111").And.NotContain("222", "в журнале только начало номера");
        entry.Segment.Should().Be("vip");
        entry.Language.Should().Be("ky");

        // Снять отметку: клиент снова доступен.
        ClientMailing.RemoveLog(entry);
        ClientMailing.LastMailed().Should().BeEmpty();
        Plan(ClientMailing.Segment.AllWithPhone, [c], mailed: new Dictionary<string, DateTime>(ClientMailing.LastMailed())).Items.Single().IsDue.Should().BeTrue();
    }

    [Fact]
    public void Edited_text_is_kept_per_segment_and_language_and_default_is_restored()
    {
        ClientMailing.GetTemplate(ClientMailing.Segment.Vip, "ru").Should().Be(ClientMailing.DefaultTemplate(ClientMailing.Segment.Vip, "ru"));
        ClientMailing.SaveTemplate(ClientMailing.Segment.Vip, "ru", "Особая скидка для вас, {имя}!");
        ClientMailing.GetTemplate(ClientMailing.Segment.Vip, "ru").Should().Be("Особая скидка для вас, {имя}!");
        ClientMailing.GetTemplate(ClientMailing.Segment.Vip, "ky").Should().Be(ClientMailing.DefaultTemplate(ClientMailing.Segment.Vip, "ky"), "другой язык не затронут");
        ClientMailing.GetTemplate(ClientMailing.Segment.Inactive, "ru").Should().Be(ClientMailing.DefaultTemplate(ClientMailing.Segment.Inactive, "ru"), "другой сегмент не затронут");
        ClientMailing.SaveTemplate(ClientMailing.Segment.Vip, "ru", "");
        ClientMailing.GetTemplate(ClientMailing.Segment.Vip, "ru").Should().Be(ClientMailing.DefaultTemplate(ClientMailing.Segment.Vip, "ru"));
    }

    [Fact]
    public void Too_long_text_is_cut_to_the_limit()
    {
        ClientMailing.SaveTemplate(ClientMailing.Segment.AllWithPhone, "ru", new string('а', 900));
        ClientMailing.GetTemplate(ClientMailing.Segment.AllWithPhone, "ru").Length.Should().Be(ClientMailing.MaxTextLength);
    }

    [Fact]
    public void Settings_are_kept_inside_safe_limits()
    {
        var s = ClientMailing.GetSettings();
        s.InactiveDays = 1;
        s.MinGapDays = 0;
        s.DailyLimit = 100000;
        s.BirthdayDaysAhead = 99;
        s.VipMinSpent = -5;
        s.Language = "de";
        ClientMailing.SaveSettings(s);
        var saved = ClientMailing.GetSettings();
        saved.InactiveDays.Should().Be(7);
        saved.MinGapDays.Should().Be(1, "не реже чем раз в день — защита от повторов не выключается совсем");
        saved.DailyLimit.Should().Be(200);
        saved.BirthdayDaysAhead.Should().Be(30);
        saved.VipMinSpent.Should().Be(0);
        saved.Language.Should().BeEmpty();
    }

    [Fact]
    public void Local_birthdays_are_stored_as_day_and_month()
    {
        ClientMailing.SetBirthday("a", new DateTime(2000, 3, 15));
        ClientMailing.LocalBirthdays()["a"].Should().Be(new DateTime(2000, 3, 15));
        ClientMailing.SetBirthday("a", null);
        ClientMailing.LocalBirthdays().Should().BeEmpty();
    }

    [AvaloniaFact]
    public void Every_segment_has_a_title_and_a_hint_in_every_language()
    {
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UiShot.Setup(UiTheme.Light, lang);
            foreach (var seg in ClientMailing.Segments)
            {
                ClientMailing.SegmentTitle(seg).Should().NotBeNullOrWhiteSpace($"{seg} {lang}");
                ClientMailing.SegmentHint(seg, S()).Should().NotBeNullOrWhiteSpace($"{seg} {lang}");
            }
            foreach (var reason in new[] { ClientMailing.SkipReason.DoNotDisturb, ClientMailing.SkipReason.RecentlyWritten, ClientMailing.SkipReason.DuplicatePhone, ClientMailing.SkipReason.DailyLimit })
                ClientMailing.SkipText(reason, 14, 30).Should().NotBeNullOrWhiteSpace($"{reason} {lang}");
        }
    }

    // ── 2026-11-26: стресс-тест рассылки ────────────────────────────────────────

    [Theory]
    [InlineData("+996 555 12-34-56", "996555123456")]
    [InlineData("0555 123 456", "996555123456")]
    [InlineData("996555123456", "996555123456")]
    [InlineData("555123456", "996555123456")]
    [InlineData("(0555) 12 34 56", "996555123456")]
    [InlineData("+7 701 123 45 67", "77011234567")]
    [InlineData("0312 62-00-00", null)]        // городской Бишкек — WhatsApp нет
    [InlineData("+996 3222 5 12 34", null)]    // городской Ош
    [InlineData("8 701 123 45 67", null)]      // 11 цифр без «+» — непонятно чей номер, ссылку не строим
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void Phone_for_WhatsApp_uses_the_same_rule_as_the_link(string phone, string? expected)
    {
        ClientMailing.WhatsAppNumber(phone).Should().Be(expected);
        if (expected is not null)
            DebtWhatsAppLink.BuildUrl(phone, "т").Should().StartWith("https://wa.me/" + expected + "?", "номер из списка = номер ссылки: очередь не встаёт на «не удалось открыть»");
    }

    [Fact]
    public void Do_not_disturb_and_recent_message_follow_the_phone_not_only_the_card()
    {
        // Клиент «a» просил не писать; у него вторая карточка «a2» с тем же номером (дубль). «b» писали вчера, «b2» — тот же номер, другая карточка.
        var contacts = new[]
        {
            C("a", "Айбек", "0700 111 222", debt: 100), C("a2", "Айбек (дубль)", "+996 700 111 222", debt: 300),
            C("b", "Бакыт", "0700 333 444", debt: 50), C("b2", "Бакыт эже", "996700333444", debt: 70),
            C("c", "Чолпон", "0700 555 666", debt: 10),
        };
        var plan = Plan(ClientMailing.Segment.Debtors, contacts, dnd: new HashSet<string> { "a" }, mailed: new Dictionary<string, DateTime> { ["b"] = Now.AddDays(-1) });
        var by = plan.Items.ToDictionary(i => i.Contact.ClientId);
        by["a2"].Skip.Should().Be(ClientMailing.SkipReason.DoNotDisturb, "номер клиента, просившего не писать, не получает сообщений и через вторую карточку");
        by["b2"].Skip.Should().Be(ClientMailing.SkipReason.RecentlyWritten, "этому номеру писали вчера");
        by["b2"].LastMailedAt.Should().Be(Now.AddDays(-1));
        plan.Items.Where(i => i.IsDue).Select(i => i.Contact.ClientId).Should().Equal("c");
    }

    [Fact]
    public void Five_thousand_clients_plan_fast_and_respect_the_daily_limit()
    {
        var s = S();
        s.DailyLimit = 30;
        var contacts = Enumerable.Range(0, 5000).Select(i => C("id" + i, "Клиент " + i, $"0700 {i / 1000:000} {i % 1000:000}", debt: i % 3 == 0 ? 100 + i : 0, lastDaysAgo: i % 400, spent: i * 10.0)).ToList();
        var mailed = Enumerable.Range(0, 2000).ToDictionary(i => "id" + (i * 2), i => Now.AddDays(-(i % 30)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var seg in ClientMailing.Segments)
        {
            var plan = Plan(seg, contacts, s: s, mailed: mailed, openedToday: 4);
            plan.Items.Count(i => i.IsDue).Should().BeLessThanOrEqualTo(26, $"{seg}: 4 чата уже открыты, лимит 30");
            plan.Items.Select(i => i.Contact.WhatsApp).Where((p, k) => plan.Items[k].IsDue).Should().OnlyHaveUniqueItems();
        }
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "5 сегментов × 5 000 клиентов — без заметной паузы в окне");
    }

    [Fact]
    public void Journal_does_not_forget_recent_chats_under_a_high_daily_limit()
    {
        // 200 чатов в день × 25 дней = 5 000 записей. Раньше журнал резался до 3 000 — первые дни забывались, и «не чаще раза в 30 дней» нарушалось.
        var first = C("first", "Первый", "0700 000 001");
        ClientMailing.LogOpened(first, ClientMailing.Segment.Vip, "ru", Now.AddDays(-25));
        for (var day = 25; day >= 1; day--)
            for (var k = 0; k < 200; k++)
                ClientMailing.LogOpened(C("x" + day + "-" + k, "К", "0700 000 002"), ClientMailing.Segment.Vip, "ru", Now.AddDays(-day).AddMinutes(k));
        ClientMailing.LastMailed().Should().ContainKey("first", "чат 25 дней назад помнится при окне 30 дней");
        var s = S();
        s.MinGapDays = 30;
        Plan(ClientMailing.Segment.AllWithPhone, [first], s: s, mailed: new Dictionary<string, DateTime>(ClientMailing.LastMailed())).Items.Single().Skip
            .Should().Be(ClientMailing.SkipReason.RecentlyWritten);

        // старше 90 дней — уходит (файл не растёт бесконечно)
        ClientMailing.ResetForTests();
        ClientMailing.LogOpened(C("old", "Старый", "0700 000 003"), ClientMailing.Segment.Vip, "ru", Now.AddDays(-120));
        for (var k = 0; k < 3001; k++)
            ClientMailing.LogOpened(C("n" + k, "К", "0700 000 004"), ClientMailing.Segment.Vip, "ru", Now.AddMinutes(k));
        ClientMailing.LastMailed().Should().NotContainKey("old");
    }

    [Fact]
    public void Broken_or_empty_settings_file_is_kept_as_a_copy_and_limits_survive_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "mailing-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "client-mailing.json");
        try
        {
            // 1) испорченный файл: настройки по умолчанию, но копия остаётся рядом (в ней — список «не беспокоить»)
            File.WriteAllText(file, "{\"DoNotDisturb\":{\"a\":\"Айбек\"},\"Log\":[{\"At\":");
            ClientMailing.ResetForTests(persist: true, filePath: file);
            ClientMailing.DoNotDisturb().Should().BeEmpty();
            Directory.GetFiles(dir, "client-mailing.json.broken-*").Should().ContainSingle("испорченный файл не пропадает при следующем сохранении");

            // 2) пустой файл — тоже без падения
            File.WriteAllText(file, "");
            ClientMailing.ResetForTests(persist: true, filePath: file);
            ClientMailing.GetSettings().DailyLimit.Should().Be(30);

            // 3) «перезапуск»: журнал и «не беспокоить» читаются из файла — дневной лимит и защита от повторов действуют
            ClientMailing.SetDoNotDisturb("dnd", "Не писать", true);
            ClientMailing.LogOpened(C("a", "Айбек", "0700 111 222"), ClientMailing.Segment.Debtors, "ru", DateTime.Now);
            ClientMailing.ResetForTests(persist: true, filePath: file);
            ClientMailing.OpenedToday().Should().Be(1);
            ClientMailing.DoNotDisturb().Should().Contain("dnd");
            ClientMailing.LastMailed().Should().ContainKey("a");
            File.ReadAllText(file).Should().NotContain("111 222").And.NotContain("111222", "полный номер в файл не пишется");
        }
        finally
        {
            ClientMailing.ResetForTests();
            try { Directory.Delete(dir, true); } catch { /* временная папка */ }
        }
    }

    [Fact]
    public void Placeholders_with_empty_long_and_odd_values_give_a_valid_short_link()
    {
        var longName = new string('Ж', 200);
        var emoji = C("e", "Айгүл 🌷", "0700 111 222", debt: 1234567.5);
        var text = ClientMailing.Render("Здравствуйте, {имя}!\nДолг: {долг} сом 🙏\nМагазин «{магазин}»", emoji, "Нур & Ко", Now);
        text.Should().Contain("Айгүл 🌷").And.Contain("1 234 567,50").And.Contain("\n");
        var url = DebtWhatsAppLink.BuildUrl(emoji.Phone, text)!;
        url.Should().NotContain(" ").And.NotContain("\n").And.NotContain("&Ко", "амперсанд и перевод строки закодированы");
        Uri.UnescapeDataString(url[(url.IndexOf("text=", StringComparison.Ordinal) + 5)..]).Should().Be(text);

        ClientMailing.Render("{имя}, {долг} сом", C("z", "", "0700 111 222", debt: 0), null, Now).Should().Be("0 сом", "пустое имя не оставляет «, »");
        var big = DebtWhatsAppLink.BuildUrl("0700 111 222", ClientMailing.Render(new string('я', ClientMailing.MaxTextLength - 6) + "{имя}", C("l", longName, "0700 111 222"), "Нур", Now))!;
        ClientMailing.UrlTooLong(big).Should().BeTrue("длинное имя раздуло ссылку — окно не открывает её, а просит сократить текст");
    }
}
