using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Staff;

/// <summary>2026-11-08 (решение владельца «рабочее время сотрудника»): правила времени (границы, ночные смены, дни недели, пустые настройки = нет ограничения),
/// решения монитора (конец рабочего времени, бездействие, перевод часов, оплата/диалог не прерываются, чек не пропадает) и хранение в карточке сотрудника.
/// Ничего не читает с диска и не пишет: настройки кассы (UserPreferences.Instance) меняются в памяти и возвращаются.</summary>
public sealed class WorkScheduleTests
{
    // 2026-11-09 — понедельник
    private static readonly DateTime Mon = new(2026, 11, 9);

    private static WorkRule Rule(string from, string to, params int[] days) =>
        WorkSchedule.RuleOf(true, from, to, days.Length == 0 ? null : days)!;

    // ── разбор и пустые настройки ──────────────────────────────────────────────────

    [Theory]
    [InlineData("09:00", 9, 0)]
    [InlineData("9:30", 9, 30)]
    [InlineData("18.45", 18, 45)]
    [InlineData("00:00", 0, 0)]
    [InlineData("24:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    public void TryParseTime_AcceptsHhMm(string text, int h, int m)
    {
        WorkSchedule.TryParseTime(text, out var t).Should().BeTrue();
        t.Should().Be(new TimeSpan(h, m, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9")]
    [InlineData("25:00")]
    [InlineData("12:60")]
    [InlineData("12:5")]
    [InlineData("-1:00")]
    [InlineData("ab:cd")]
    [InlineData("24:30")]
    public void TryParseTime_RejectsGarbage(string? text) =>
        WorkSchedule.TryParseTime(text, out _).Should().BeFalse();

    [Fact]
    public void EmptyOrDisabledSettings_MeanNoLimit()
    {
        WorkSchedule.RuleOf((EmployeeAccessCode?)null).Should().BeNull();
        WorkSchedule.RuleOf(new EmployeeAccessCode { Name = "Айгуль" }).Should().BeNull("по умолчанию ограничения нет");
        WorkSchedule.RuleOf(false, "09:00", "18:00", null).Should().BeNull("выключено");
        WorkSchedule.RuleOf(null, "09:00", "18:00", null).Should().BeNull();
        WorkSchedule.RuleOf(true, "", "18:00", null).Should().BeNull("пустое «с»");
        WorkSchedule.RuleOf(true, "09:00", null, null).Should().BeNull("пустое «до»");
        WorkSchedule.RuleOf(true, "xx", "yy", null).Should().BeNull("мусор");
        WorkSchedule.RuleOf(true, "09:00", "09:00", null).Should().BeNull("«с» = «до» — смысла нет, ограничения нет");
        WorkSchedule.Evaluate(null, Mon.AddHours(3)).Verdict.Should().Be(WorkTimeVerdict.Unlimited);
        WorkSchedule.Evaluate(null, Mon.AddHours(3)).EndsAt.Should().BeNull();
    }

    [Fact]
    public void RuleOf_DaysMask_AllDaysWhenEmptyOrInvalid()
    {
        WorkSchedule.RuleOf(true, "09:00", "18:00", null)!.DaysMask.Should().Be(WorkRule.AllDays);
        WorkSchedule.RuleOf(true, "09:00", "18:00", new List<int>())!.DaysMask.Should().Be(WorkRule.AllDays);
        WorkSchedule.RuleOf(true, "09:00", "18:00", new List<int> { 9, -3 })!.DaysMask.Should().Be(WorkRule.AllDays, "мусорные дни игнорируются, остальных нет — каждый день");
        WorkSchedule.RuleOf(true, "09:00", "18:00", new List<int> { 1, 3, 3 })!.DaysMask.Should().Be((1 << 1) | (1 << 3));
    }

    // ── границы дневного интервала ─────────────────────────────────────────────────

    [Theory]
    [InlineData(8, 59, 59, WorkTimeVerdict.Outside)]
    [InlineData(9, 0, 0, WorkTimeVerdict.Inside)]
    [InlineData(12, 0, 0, WorkTimeVerdict.Inside)]
    [InlineData(17, 59, 59, WorkTimeVerdict.Inside)]
    [InlineData(18, 0, 0, WorkTimeVerdict.Outside)]
    [InlineData(23, 30, 0, WorkTimeVerdict.Outside)]
    [InlineData(0, 0, 0, WorkTimeVerdict.Outside)]
    public void DayInterval_Boundaries(int h, int m, int s, WorkTimeVerdict expected)
    {
        var st = WorkSchedule.Evaluate(Rule("09:00", "18:00"), Mon.Add(new TimeSpan(h, m, s)));
        st.Verdict.Should().Be(expected);
        if (expected == WorkTimeVerdict.Inside)
            st.EndsAt.Should().Be(Mon.AddHours(18));
    }

    [Fact]
    public void Outside_NextStart_IsTodayBeforeStart_AndTomorrowAfterEnd()
    {
        var rule = Rule("09:00", "18:00");
        WorkSchedule.Evaluate(rule, Mon.AddHours(7)).NextStart.Should().Be(Mon.AddHours(9));
        WorkSchedule.Evaluate(rule, Mon.AddHours(19)).NextStart.Should().Be(Mon.AddDays(1).AddHours(9));
    }

    // ── ночные смены ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(21, 59, WorkTimeVerdict.Outside)]
    [InlineData(22, 0, WorkTimeVerdict.Inside)]
    [InlineData(23, 59, WorkTimeVerdict.Inside)]
    public void Overnight_EveningPart(int h, int m, WorkTimeVerdict expected)
    {
        var st = WorkSchedule.Evaluate(Rule("22:00", "06:00"), Mon.Add(new TimeSpan(h, m, 0)));
        st.Verdict.Should().Be(expected);
        if (expected == WorkTimeVerdict.Inside)
            st.EndsAt.Should().Be(Mon.AddDays(1).AddHours(6), "ночная смена кончается следующим утром");
    }

    [Theory]
    [InlineData(0, 0, WorkTimeVerdict.Inside)]
    [InlineData(3, 30, WorkTimeVerdict.Inside)]
    [InlineData(5, 59, WorkTimeVerdict.Inside)]
    [InlineData(6, 0, WorkTimeVerdict.Outside)]
    [InlineData(12, 0, WorkTimeVerdict.Outside)]
    public void Overnight_MorningPart(int h, int m, WorkTimeVerdict expected)
    {
        var st = WorkSchedule.Evaluate(Rule("22:00", "06:00"), Mon.Add(new TimeSpan(h, m, 0)));
        st.Verdict.Should().Be(expected);
        if (expected == WorkTimeVerdict.Inside)
            st.EndsAt.Should().Be(Mon.AddHours(6));
    }

    [Fact]
    public void Overnight_EndAtMidnight()
    {
        var rule = Rule("18:00", "00:00");
        rule.Overnight.Should().BeTrue();
        WorkSchedule.Evaluate(rule, Mon.AddHours(23)).EndsAt.Should().Be(Mon.AddDays(1));
        WorkSchedule.Evaluate(rule, Mon.AddDays(1)).Verdict.Should().Be(WorkTimeVerdict.Outside);
    }

    // ── дни недели ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Days_MondayToFriday_WeekendIsOutside_NextStartIsMonday()
    {
        var rule = Rule("09:00", "18:00", 1, 2, 3, 4, 5);
        WorkSchedule.Evaluate(rule, Mon.AddHours(12)).Verdict.Should().Be(WorkTimeVerdict.Inside);
        var sat = Mon.AddDays(5).AddHours(12);
        sat.DayOfWeek.Should().Be(DayOfWeek.Saturday);
        var st = WorkSchedule.Evaluate(rule, sat);
        st.Verdict.Should().Be(WorkTimeVerdict.Outside);
        st.NextStart.Should().Be(Mon.AddDays(7).AddHours(9));
    }

    [Fact]
    public void Days_NightShiftBelongsToTheDayItStarts()
    {
        var rule = Rule("22:00", "06:00", (int)DayOfWeek.Friday);
        var fri = Mon.AddDays(4);
        WorkSchedule.Evaluate(rule, fri.AddHours(23)).Verdict.Should().Be(WorkTimeVerdict.Inside);
        WorkSchedule.Evaluate(rule, fri.AddDays(1).AddHours(3)).Verdict.Should().Be(WorkTimeVerdict.Inside, "суббота 03:00 — хвост пятничной смены");
        WorkSchedule.Evaluate(rule, fri.AddDays(1).AddHours(23)).Verdict.Should().Be(WorkTimeVerdict.Outside, "субботняя смена не разрешена");
        WorkSchedule.Evaluate(rule, fri.AddDays(2).AddHours(3)).Verdict.Should().Be(WorkTimeVerdict.Outside, "воскресенье 03:00 — хвоста нет");
        WorkSchedule.Evaluate(rule, fri.AddHours(3)).Verdict.Should().Be(WorkTimeVerdict.Outside, "пятница 03:00 — хвост четверга, а четверг не разрешён");
    }

    [Fact]
    public void Describe_And_DaysText()
    {
        var old = UserPreferences.Instance.Language;
        UserPreferences.Instance.Language = AppLanguage.Russian;
        try
        {
            WorkSchedule.Describe(Rule("09:00", "18:00")).Should().Be("09:00–18:00");
            WorkSchedule.Describe(Rule("09:00", "18:00", 1, 2, 3, 4, 5)).Should().Be("Пн–Пт 09:00–18:00");
            WorkSchedule.DaysText((1 << 1) | (1 << 3) | (1 << 6)).Should().Be("Пн, Ср, Сб");
            WorkSchedule.DaysText((1 << 6) | (1 << 0)).Should().Be("Сб, Вс");
            WorkSchedule.YourHoursText(Rule("09:00", "18:00")).Should().Be("Ваше рабочее время: 09:00–18:00");
            WorkSchedule.OutsideRefusalText(Rule("09:00", "18:00"), Mon.AddDays(1).AddHours(9)).Should().StartWith("Ваше рабочее время: 09:00–18:00").And.Contain("10.11");
        }
        finally { UserPreferences.Instance.Language = old; }
    }

    [Fact]
    public void AllTexts_HaveFiveLanguages_NoneEmpty()
    {
        var rule = Rule("09:00", "18:00");
        var old = UserPreferences.Instance.Language;
        try
        {
            var seen = new HashSet<string>();
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
            {
                UserPreferences.Instance.Language = lang;
                foreach (var text in new[]
                         {
                             WorkSchedule.YourHoursText(rule), WorkSchedule.OutsideRefusalText(rule, Mon.AddHours(9)), WorkSchedule.OutsideRefusalText(rule, null),
                             WorkSchedule.EndWarningText(10, rule), WorkSchedule.IdleWarningText(), WorkSchedule.HoursLine(rule),
                         })
                    text.Should().NotBeNullOrWhiteSpace($"{lang}");
                seen.Add(WorkSchedule.IdleWarningText());
            }
            seen.Should().HaveCount(5, "пять разных переводов");
        }
        finally { UserPreferences.Instance.Language = old; }
    }

    // ── выход при бездействии: настройка ───────────────────────────────────────────

    [Theory]
    [InlineData(null, 60)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(30, 30)]
    [InlineData(5000, 1440)]
    public void IdleLogoutMinutes_DefaultsToSixty_ZeroMeansOff(int? stored, int expected)
    {
        var prefs = UserPreferences.Instance;
        var old = prefs.IdleLogoutMinutes;
        prefs.IdleLogoutMinutes = stored;
        try { WorkSchedule.IdleLogoutMinutes().Should().Be(expected); }
        finally { prefs.IdleLogoutMinutes = old; }
    }

    // ── монитор: конец рабочего времени ────────────────────────────────────────────

    private sealed class Clock
    {
        public DateTime Now;
        public TimeSpan Idle;
        public WorkRule? Rule;
        public int IdleMinutes = 60;

        public WorkTimeMonitor Monitor() => new(() => Now, () => Idle, () => Rule, () => IdleMinutes);
    }

    [Fact]
    public void Monitor_NoRule_DoesNothing_EvenAfterHugeIdle()
    {
        var c = new Clock { Now = Mon.AddHours(3), Idle = TimeSpan.FromHours(10), Rule = null };
        var m = c.Monitor();
        m.Tick(false, true, true).Should().Be(WorkTimeAction.None, "кассир без ограничения — автовыхода нет");
        m.Tick(false, false, false).Should().Be(WorkTimeAction.None);
    }

    [Fact]
    public void Monitor_EndWarning_OnceTenMinutesBefore()
    {
        var c = new Clock { Rule = Rule("09:00", "18:00"), Now = Mon.AddHours(17).AddMinutes(30) };
        var m = c.Monitor();
        m.Tick(false, true, true).Should().Be(WorkTimeAction.None, "до конца 30 минут");
        c.Now = Mon.AddHours(17).AddMinutes(50);
        m.Tick(false, true, true).Should().Be(WorkTimeAction.EndWarning);
        m.LastMinutesLeft.Should().Be(10);
        c.Now = Mon.AddHours(17).AddMinutes(55);
        m.Tick(false, true, true).Should().Be(WorkTimeAction.None, "второй раз за ту же смену не предупреждаем");
    }

    [Fact]
    public void Monitor_ClockMovedBack_AllowsNewWarning_ClockForward_DoesNotBreak()
    {
        var c = new Clock { Rule = Rule("09:00", "18:00"), Now = Mon.AddHours(17).AddMinutes(51) };
        var m = c.Monitor();
        m.Tick(false, false, true).Should().Be(WorkTimeAction.EndWarning);
        c.Now = Mon.AddHours(12); // часы перевели на 6 часов назад
        m.Tick(false, false, true).Should().Be(WorkTimeAction.None);
        c.Now = Mon.AddHours(17).AddMinutes(52);
        m.Tick(false, false, true).Should().Be(WorkTimeAction.EndWarning, "после перевода назад предупреждение возможно снова");
        c.Now = Mon.AddDays(3).AddHours(5); // часы скакнули на трое суток вперёд: вне времени — мягко, без исключений
        var act = () => m.Tick(false, true, true);
        act.Should().NotThrow();
        m.Tick(false, true, true).Should().NotBe(WorkTimeAction.EndHardLogout, "чек на месте — жёсткого выхода нет");
    }

    [Fact]
    public void Monitor_AfterEnd_WithCart_AsksSoftly_NeverHardLogsOut_NeverLosesReceipt()
    {
        var c = new Clock { Rule = Rule("09:00", "18:00"), Now = Mon.AddHours(17).AddMinutes(59) };
        var m = c.Monitor();
        m.Tick(false, true, true);
        c.Now = Mon.AddHours(18).AddSeconds(5);
        m.Tick(false, true, true).Should().Be(WorkTimeAction.EndReached, "первый вопрос сразу");
        // дальше — часами напролёт с чеком в корзине: только вопрос раз в 5 минут, никогда EndHardLogout
        var asked = 0;
        for (var minutes = 1; minutes <= 600; minutes++)
        {
            c.Now = Mon.AddHours(18).AddMinutes(minutes);
            var action = m.Tick(false, true, true);
            action.Should().NotBe(WorkTimeAction.EndHardLogout, $"чек в корзине ({minutes} мин после конца)");
            if (action == WorkTimeAction.EndReached)
                asked++;
        }
        asked.Should().BeInRange(100, 125, "вопрос повторяется примерно раз в 5 минут");
    }

    [Fact]
    public void Monitor_AfterEnd_PaymentOrDialog_NeverInterrupts()
    {
        var c = new Clock { Rule = Rule("09:00", "18:00"), Now = Mon.AddHours(19), Idle = TimeSpan.FromHours(5) };
        var m = c.Monitor();
        for (var i = 0; i < 50; i++)
        {
            c.Now = c.Now.AddMinutes(2);
            m.Tick(busy: true, hasCart: false, shiftOpen: true).Should().Be(WorkTimeAction.None, "идёт оплата/печать/диалог — ничего не прерываем");
        }
        m.Tick(busy: false, hasCart: false, shiftOpen: false).Should().Be(WorkTimeAction.EndHardLogout, "освободилось — чека и смены нет — выход");
    }

    [Fact]
    public void Monitor_AfterEnd_EmptyCart_ClosedShift_LogsOutAtOnce()
    {
        var c = new Clock { Rule = Rule("09:00", "18:00"), Now = Mon.AddHours(18).AddMinutes(1) };
        c.Monitor().Tick(false, false, false).Should().Be(WorkTimeAction.EndHardLogout);
    }

    [Fact]
    public void Monitor_AfterEnd_EmptyCart_OpenShift_AsksFirst_ThenLogsOutAfterGrace_ShiftIsNeverClosedByMonitor()
    {
        var c = new Clock { Rule = Rule("09:00", "18:00"), Now = Mon.AddHours(17).AddMinutes(59) };
        var m = c.Monitor();
        m.Tick(false, false, true);
        c.Now = Mon.AddHours(18).AddMinutes(1);
        m.Tick(false, false, true).Should().Be(WorkTimeAction.EndReached, "сначала вопрос «закрыть смену?»");
        c.Now = Mon.AddHours(18).AddMinutes(8);
        m.Tick(false, false, true).Should().Be(WorkTimeAction.EndReached, "ответ «Позже» — повтор через 5 минут");
        c.Now = Mon.AddHours(18).AddMinutes(11);
        m.Tick(false, false, true).Should().Be(WorkTimeAction.None, "после последнего вопроса прошло меньше 5 минут");
        c.Now = Mon.AddHours(18).AddMinutes(16);
        m.Tick(false, false, true).Should().Be(WorkTimeAction.EndHardLogout, "15 минут без ответа и чека нет — выход (смена останется открытой — закрытия среди действий монитора нет)");
        Enum.GetNames<WorkTimeAction>().Should().NotContain(n => n.Contains("CloseShift"), "монитор смену сам не закрывает");
    }

    [Fact]
    public void Monitor_RuleChangesToUnlimited_ResetsState()
    {
        var c = new Clock { Rule = Rule("09:00", "18:00"), Now = Mon.AddHours(19) };
        var m = c.Monitor();
        m.Tick(false, true, true).Should().Be(WorkTimeAction.EndReached);
        c.Rule = null; // владелец снял ограничение
        m.Tick(false, true, true).Should().Be(WorkTimeAction.None);
        c.Rule = Rule("09:00", "18:00");
        m.Tick(false, true, true).Should().Be(WorkTimeAction.EndReached, "состояние сброшено — вопрос задаётся заново");
    }

    // ── монитор: бездействие ───────────────────────────────────────────────────────

    [Fact]
    public void Monitor_Idle_WarnsAtMinuteBefore_ThenLogsOut_OnlyAtLimit()
    {
        var c = new Clock { Rule = Rule("00:00", "23:59"), Now = Mon.AddHours(12), IdleMinutes = 60 };
        var m = c.Monitor();
        c.Idle = TimeSpan.FromMinutes(30);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.None);
        c.Idle = TimeSpan.FromMinutes(58).Add(TimeSpan.FromSeconds(59));
        m.Tick(false, false, false).Should().Be(WorkTimeAction.None, "до предупреждения ещё секунда");
        c.Idle = TimeSpan.FromMinutes(59);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.IdleWarning);
        c.Idle = TimeSpan.FromMinutes(59).Add(TimeSpan.FromSeconds(30));
        m.Tick(false, false, false).Should().Be(WorkTimeAction.None, "предупреждение один раз");
        c.Idle = TimeSpan.FromMinutes(60);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.IdleLogout);
    }

    [Fact]
    public void Monitor_Idle_InputResetsWarning()
    {
        var c = new Clock { Rule = Rule("00:00", "23:59"), Now = Mon.AddHours(12), IdleMinutes = 60 };
        var m = c.Monitor();
        c.Idle = TimeSpan.FromMinutes(59);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.IdleWarning);
        c.Idle = TimeSpan.FromSeconds(3); // кассир тронул мышь
        m.Tick(false, false, false).Should().Be(WorkTimeAction.None);
        c.Idle = TimeSpan.FromMinutes(59);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.IdleWarning, "после действия предупреждение снова возможно");
    }

    [Fact]
    public void Monitor_Idle_NotDuringPaymentPrintOrDialog_ButLogsOutAfterwards()
    {
        var c = new Clock { Rule = Rule("00:00", "23:59"), Now = Mon.AddHours(12), IdleMinutes = 60, Idle = TimeSpan.FromMinutes(90) };
        var m = c.Monitor();
        m.Tick(busy: true, hasCart: true, shiftOpen: true).Should().NotBe(WorkTimeAction.IdleLogout, "оплата/печать/диалог идут — не выходим");
        m.Tick(busy: false, hasCart: true, shiftOpen: true).Should().Be(WorkTimeAction.IdleLogout, "освободилось — выход (чек откладывает касса, смена остаётся)");
    }

    [Fact]
    public void Monitor_Idle_ZeroMeansNever_AndCustomValueIsUsed()
    {
        var c = new Clock { Rule = Rule("00:00", "23:59"), Now = Mon.AddHours(12), IdleMinutes = 0, Idle = TimeSpan.FromHours(8) };
        var m = c.Monitor();
        m.Tick(false, false, false).Should().Be(WorkTimeAction.None, "0 = не выходить");
        c.IdleMinutes = 10;
        c.Idle = TimeSpan.FromMinutes(10);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.IdleLogout, "владелец поставил 10 минут");
    }

    [Fact]
    public void Monitor_Idle_OneMinuteLimit_WarnsHalfwayThenLogsOut()
    {
        var c = new Clock { Rule = Rule("00:00", "23:59"), Now = Mon.AddHours(12), IdleMinutes = 1 };
        var m = c.Monitor();
        c.Idle = TimeSpan.FromSeconds(10);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.None);
        c.Idle = TimeSpan.FromSeconds(30);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.IdleWarning);
        c.Idle = TimeSpan.FromSeconds(60);
        m.Tick(false, false, false).Should().Be(WorkTimeAction.IdleLogout);
    }

    // ── хранение: карточка сотрудника и облако ─────────────────────────────────────

    [Fact]
    public void Card_WithoutWorkSettings_SerializesWithoutWorkFields()
    {
        var json = JsonSerializer.Serialize(EmployeeAccessGate.ToCloudSecure(new[] { new EmployeeAccessCode { Name = "Айгуль", ServerId = "u1" } }));
        json.Should().NotContain("Work", "по умолчанию в облако ничего нового не уходит — старые кассы и файлы не меняются");
    }

    [Fact]
    public void Card_WorkSettings_TravelToCloudCopy_AndBack()
    {
        var card = new EmployeeAccessCode { Name = "Айгуль", ServerId = "u1", WorkLimitEnabled = true, WorkFrom = "09:00", WorkTo = "18:00", WorkDays = new List<int> { 1, 2, 3 } };
        var copy = EmployeeAccessGate.ToCloudSecure(new[] { card }).Single();
        copy.WorkLimitEnabled.Should().BeTrue();
        copy.WorkFrom.Should().Be("09:00");
        copy.WorkTo.Should().Be("18:00");
        copy.WorkDays.Should().Equal(1, 2, 3);
        copy.WorkDays.Should().NotBeSameAs(card.WorkDays, "копия, а не общая ссылка");

        var back = JsonSerializer.Deserialize<List<EmployeeAccessCode>>(JsonSerializer.Serialize(new[] { copy }))!.Single();
        WorkSchedule.RuleOf(back).Should().Be(WorkSchedule.RuleOf(card));
        EmployeeAccessGate.ToCloudPublic(new[] { card }).Single().WorkLimitEnabled.Should().BeNull("старое поле облака — только имена");
    }

    [Fact]
    public void CloudApply_UpdatesWorkSettingsOfExistingCard()
    {
        var prefs = UserPreferences.Instance;
        var oldList = prefs.EmployeeAccessCodes;
        var oldVersion = prefs.EmployeeAccessCodesVersion;
        try
        {
            prefs.EmployeeAccessCodes = new List<EmployeeAccessCode> { new() { Name = "Айгуль", ServerId = "u1", Email = "a@x.kg" } };
            prefs.EmployeeAccessCodesVersion = 5;
            var remote = new List<EmployeeAccessCode>
            {
                new() { Name = "Айгуль", ServerId = "u1", Email = "a@x.kg", WorkLimitEnabled = true, WorkFrom = "22:00", WorkTo = "06:00" },
            };
            EmployeeAccessGate.ApplyCloudSecure(remote, 6, out _).Should().BeTrue();
            var card = prefs.EmployeeAccessCodes.Single();
            WorkSchedule.RuleOf(card)!.Overnight.Should().BeTrue();

            // облако без ограничения (владелец выключил на другой кассе) — снимается и здесь
            var cleared = new List<EmployeeAccessCode> { new() { Name = "Айгуль", ServerId = "u1", Email = "a@x.kg" } };
            EmployeeAccessGate.ApplyCloudSecure(cleared, 7, out _);
            WorkSchedule.RuleOf(prefs.EmployeeAccessCodes.Single()).Should().BeNull();
        }
        finally
        {
            prefs.EmployeeAccessCodes = oldList;
            prefs.EmployeeAccessCodesVersion = oldVersion;
        }
    }

    // ── правило вошедшего пользователя ─────────────────────────────────────────────

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void CurrentRule_OnlyForLimitedCashier_NotForOwnerAdminOrUnlimited()
    {
        var prefs = UserPreferences.Instance;
        var oldList = prefs.EmployeeAccessCodes;
        var oldCompany = prefs.EmployeeAccessCodesCompanyId;
        var oldProvider = EmployeeAccessGate.CurrentUserProvider;
        try
        {
            prefs.EmployeeAccessCodesCompanyId = null;
            prefs.EmployeeAccessCodes = new List<EmployeeAccessCode>
            {
                new() { Name = "Кассир", ServerId = "u1", WorkLimitEnabled = true, WorkFrom = "09:00", WorkTo = "18:00" },
                new() { Name = "Свободный", ServerId = "u2" },
                new() { Name = "Владелец", ServerId = "u3", WorkLimitEnabled = true, WorkFrom = "09:00", WorkTo = "18:00" },
            };

            EmployeeAccessGate.CurrentUserProvider = () => Payload("""{"id":"u1","role":"cashier"}""");
            WorkSchedule.CurrentRule().Should().NotBeNull("кассир с включённым ограничением");

            EmployeeAccessGate.CurrentUserProvider = () => Payload("""{"id":"u2","role":"cashier"}""");
            WorkSchedule.CurrentRule().Should().BeNull("ограничение у него не включено — по умолчанию ограничения нет");

            EmployeeAccessGate.CurrentUserProvider = () => Payload("""{"id":"u3","role":"owner"}""");
            WorkSchedule.CurrentRule().Should().BeNull("владелец не ограничивается, даже если карточка настроена");

            EmployeeAccessGate.CurrentUserProvider = () => Payload("""{"id":"u3","role":"admin"}""");
            WorkSchedule.CurrentRule().Should().BeNull("администратор тоже");

            EmployeeAccessGate.CurrentUserProvider = () => Payload("""{"id":"zzz","role":"cashier"}""");
            WorkSchedule.CurrentRule().Should().BeNull("карточки нет (список ещё не получен) — ограничения нет");

            EmployeeAccessGate.CurrentUserProvider = () => throw new InvalidOperationException("сбой профиля");
            WorkSchedule.CurrentRule().Should().BeNull("сбой не ломает вход");
        }
        finally
        {
            EmployeeAccessGate.CurrentUserProvider = oldProvider;
            prefs.EmployeeAccessCodes = oldList;
            prefs.EmployeeAccessCodesCompanyId = oldCompany;
        }
    }
}
