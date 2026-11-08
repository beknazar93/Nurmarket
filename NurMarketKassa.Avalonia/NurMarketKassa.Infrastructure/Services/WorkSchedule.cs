using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>2026-11-08: правило рабочего времени кассира — «с, до» (местные часы ПК) и дни недели (маска: бит i = System.DayOfWeek i, воскресенье = 0).
/// Конец раньше начала — ночная смена (22:00–06:00): она целиком относится к дню начала.</summary>
public sealed record WorkRule(TimeSpan From, TimeSpan To, int DaysMask)
{
    public const int AllDays = 0x7F;

    public bool Overnight => From > To;
}

public enum WorkTimeVerdict
{
    /// <summary>Ограничения нет (по умолчанию) — работа как раньше.</summary>
    Unlimited,
    Inside,
    Outside,
}

/// <param name="EndsAt">Inside: когда рабочее время закончится (местное время).</param>
/// <param name="NextStart">Outside: ближайшее начало рабочего времени (null — не нашлось в ближайшие 8 суток).</param>
public readonly record struct WorkTimeStatus(WorkTimeVerdict Verdict, WorkRule? Rule, DateTime? EndsAt, DateTime? NextStart);

/// <summary>
/// 2026-11-08, решение владельца («рабочее время сотрудника»): сотрудник работает только в указанное время; по умолчанию ограничения нет —
/// смены открывают и закрывают вручную. Ограничение включает владелец для каждого кассира (Настройки → Сотрудники → карточка → «Рабочее время»),
/// время — местные часы ПК. Хранится в карточке сотрудника (EmployeeAccessCode.Work*) и вместе со списком сотрудников едет в облако компании
/// (SettingsCloudSync, SecureCodesKey + версия списка) — действует на всех кассах магазина. Здесь — чистые правила (без UI и без часов:
/// «сейчас» передаётся параметром, поэтому смена системных часов ничего не ломает — каждая проверка заново смотрит на текущее время).
/// Ничего не знает о деньгах, чеках и сменах.
/// </summary>
public static class WorkSchedule
{
    /// <summary>Выход при бездействии по умолчанию, минут (решение владельца).</summary>
    public const int DefaultIdleMinutes = 60;

    /// <summary>Потолок настройки «Выход при бездействии» (сутки).</summary>
    public const int MaxIdleMinutes = 24 * 60;

    /// <summary>Предупреждение «до конца рабочего времени» за столько минут.</summary>
    public const int EndWarningMinutes = 10;

    /// <summary>«ЧЧ:ММ» (также «Ч:ММ», «ЧЧ.ММ»); «24:00» = 00:00 (конец суток). Пусто/мусор — false.</summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var t = text?.Trim().Replace('.', ':');
        if (string.IsNullOrEmpty(t))
            return false;
        var parts = t.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || parts[1].Length != 2 || m > 59 || h > 24 || (h == 24 && m != 0))
            return false;
        time = h == 24 ? TimeSpan.Zero : new TimeSpan(h, m, 0);
        return true;
    }

    public static string FormatTime(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{time.Hours:00}:{time.Minutes:00}");

    /// <summary>Правило из полей карточки; null — ограничения нет (выключено, пустые или неверные поля, «с» равно «до»).</summary>
    public static WorkRule? RuleOf(bool? enabled, string? from, string? to, IReadOnlyCollection<int>? days)
    {
        if (enabled != true || !TryParseTime(from, out var f) || !TryParseTime(to, out var t) || f == t)
            return null;
        var mask = 0;
        if (days is not null)
            foreach (var d in days)
                if (d is >= 0 and <= 6)
                    mask |= 1 << d;
        return new WorkRule(f, t, mask == 0 ? WorkRule.AllDays : mask);
    }

    public static WorkRule? RuleOf(EmployeeAccessCode? card) =>
        card is null ? null : RuleOf(card.WorkLimitEnabled, card.WorkFrom, card.WorkTo, card.WorkDays);

    /// <summary>Правило вошедшего пользователя. Только для кассы: программа владельца, владелец и администратор не ограничиваются никогда;
    /// нет карточки сотрудника (список ещё не получен) — ограничения нет, как и по умолчанию.</summary>
    public static WorkRule? CurrentRule()
    {
        try
        {
            if (AppMode.IsOwner || EmployeeAccessGate.CurrentUserIsOwnerOrAdmin())
                return null;
            return RuleOf(EmployeeAccessGate.CurrentEmployeeCard());
        }
        catch
        {
            return null; // проверка рабочего времени не должна ронять вход
        }
    }

    private static bool DayAllowed(WorkRule rule, DateTime day) => (rule.DaysMask & (1 << (int)day.DayOfWeek)) != 0;

    /// <summary>Где мы сейчас относительно правила. Ночная смена: интервал начинается в день с галочкой и заканчивается следующим утром.</summary>
    public static WorkTimeStatus Evaluate(WorkRule? rule, DateTime now)
    {
        if (rule is null)
            return new WorkTimeStatus(WorkTimeVerdict.Unlimited, null, null, null);
        // Сегодняшний и вчерашний день: вчерашний интервал может перейти за полночь.
        for (var back = 0; back <= 1; back++)
        {
            var day = now.Date.AddDays(-back);
            if (!DayAllowed(rule, day))
                continue;
            var start = day + rule.From;
            var end = rule.Overnight ? day.AddDays(1) + rule.To : day + rule.To;
            if (now >= start && now < end)
                return new WorkTimeStatus(WorkTimeVerdict.Inside, rule, end, null);
        }
        DateTime? next = null;
        for (var fwd = 0; fwd <= 8 && next is null; fwd++)
        {
            var day = now.Date.AddDays(fwd);
            if (DayAllowed(rule, day) && day + rule.From > now)
                next = day + rule.From;
        }
        return new WorkTimeStatus(WorkTimeVerdict.Outside, rule, null, next);
    }

    /// <summary>«09:00–18:00» или «Пн–Пт 09:00–18:00» (если дни не все).</summary>
    public static string Describe(WorkRule rule)
    {
        var range = FormatTime(rule.From) + "–" + FormatTime(rule.To);
        return rule.DaysMask == WorkRule.AllDays ? range : DaysText(rule.DaysMask) + " " + range;
    }

    /// <summary>Дни подряд сворачиваются: «Пн–Пт», «Пн, Ср, Сб». Порядок — с понедельника.</summary>
    public static string DaysText(int mask)
    {
        var order = new[] { 1, 2, 3, 4, 5, 6, 0 };
        var names = DayNames();
        var sb = new StringBuilder();
        var i = 0;
        while (i < order.Length)
        {
            if ((mask & (1 << order[i])) == 0)
            {
                i++;
                continue;
            }
            var j = i;
            while (j + 1 < order.Length && (mask & (1 << order[j + 1])) != 0)
                j++;
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append(names[order[i]]);
            if (j - i >= 2)
                sb.Append('–').Append(names[order[j]]);
            else if (j - i == 1)
                sb.Append(", ").Append(names[order[j]]);
            i = j + 1;
        }
        return sb.ToString();
    }

    /// <summary>Короткие названия дней по System.DayOfWeek (0 = воскресенье).</summary>
    public static string[] DayNames() =>
    [
        Tr.T("Вс", "Жк", "Sun", "Paz", "Yak"),
        Tr.T("Пн", "Дш", "Mon", "Pzt", "Du"),
        Tr.T("Вт", "Шш", "Tue", "Sal", "Se"),
        Tr.T("Ср", "Шр", "Wed", "Çar", "Chor"),
        Tr.T("Чт", "Бш", "Thu", "Per", "Pay"),
        Tr.T("Пт", "Жм", "Fri", "Cum", "Ju"),
        Tr.T("Сб", "Иш", "Sat", "Cmt", "Sha"),
    ];

    /// <summary>Выход при бездействии, минут: null (не меняли) — 60; 0 — выключен; отрицательное — как 0; больше суток — сутки.</summary>
    public static int IdleLogoutMinutes()
    {
        var v = UserPreferences.Instance.IdleLogoutMinutes ?? DefaultIdleMinutes;
        return v <= 0 ? 0 : Math.Min(v, MaxIdleMinutes);
    }

    // ---- Тексты (5 языков) ----

    /// <summary>«Ваше рабочее время: 09:00–18:00».</summary>
    public static string YourHoursText(WorkRule rule) =>
        Tr.T($"Ваше рабочее время: {Describe(rule)}", $"Сиздин иш убактыңыз: {Describe(rule)}",
            $"Your working hours: {Describe(rule)}", $"Çalışma saatleriniz: {Describe(rule)}", $"Sizning ish vaqtingiz: {Describe(rule)}");

    /// <summary>«Рабочее время: 09:00–18:00» (в карточках и списках; ночная смена помечается).</summary>
    public static string HoursLine(WorkRule rule) =>
        Tr.T("Рабочее время: ", "Иш убактысы: ", "Working hours: ", "Çalışma saatleri: ", "Ish vaqti: ") + Describe(rule)
        + (rule.Overnight ? " · " + Tr.T("ночная смена", "түнкү смена", "night shift", "gece vardiyası", "tungi smena") : "");

    /// <summary>Отказ во входе / открытии смены вне рабочего времени.</summary>
    public static string OutsideRefusalText(WorkRule rule, DateTime? nextStart)
    {
        var next = nextStart is { } n ? NextStartLine(n) : ("", "", "", "", "");
        return YourHoursText(rule) + "\n"
               + Tr.T("Сейчас вход и открытие смены закрыты. Если нужно поработать в другое время — обратитесь к владельцу." + next.Item1,
                   "Азыр кирүү жана сменаны ачуу жабык. Башка убакта иштөө керек болсо — ээсине кайрылыңыз." + next.Item2,
                   "Sign-in and opening a shift are closed right now. If you need to work at another time, ask the owner." + next.Item3,
                   "Şu anda giriş ve vardiya açma kapalı. Başka bir saatte çalışmanız gerekirse sahibine başvurun." + next.Item4,
                   "Hozir kirish va smenani ochish yopiq. Boshqa vaqtda ishlash kerak bo'lsa — egasiga murojaat qiling." + next.Item5);
    }

    private static (string, string, string, string, string) NextStartLine(DateTime n)
    {
        var d = n.ToString("dd.MM", CultureInfo.InvariantCulture);
        var t = FormatTime(n.TimeOfDay);
        return ($"\nСледующий вход возможен {d} в {t}.", $"\nКийинки жолу {d} саат {t} кирүүгө болот.",
            $"\nNext sign-in is possible on {d} at {t}.", $"\nBir sonraki giriş: {d} saat {t}.", $"\nKeyingi kirish {d} soat {t} da mumkin.");
    }

    public static string EndWarningText(int minutesLeft, WorkRule rule) =>
        Tr.T($"Рабочее время закончится через {minutesLeft} мин ({Describe(rule)}). Завершите продажу и закройте смену.",
            $"Иш убактысы {minutesLeft} мүнөттөн кийин бүтөт ({Describe(rule)}). Сатууну аягына чыгарып, сменаны жабыңыз.",
            $"Working time ends in {minutesLeft} min ({Describe(rule)}). Finish the sale and close the shift.",
            $"Çalışma süresi {minutesLeft} dk sonra bitiyor ({Describe(rule)}). Satışı tamamlayıp vardiyayı kapatın.",
            $"Ish vaqti {minutesLeft} daqiqadan keyin tugaydi ({Describe(rule)}). Sotuvni tugatib, smenani yoping.");

    public static string IdleWarningText() =>
        Tr.T("Вы давно не работаете: через минуту — выход из аккаунта. Чек и смена сохранятся. Нажмите любую клавишу, чтобы остаться.",
            "Көптөн бери иштеген жоксуз: бир мүнөттөн кийин аккаунттан чыгасыз. Чек жана смена сакталат. Калуу үчүн каалаган баскычты басыңыз.",
            "You have been inactive: you will be signed out in a minute. The receipt and the shift stay saved. Press any key to stay.",
            "Uzun süredir işlem yapmadınız: bir dakika sonra hesaptan çıkılacak. Fiş ve vardiya saklanır. Kalmak için herhangi bir tuşa basın.",
            "Uzoq vaqtdan beri ishlamayapsiz: bir daqiqadan keyin akkauntdan chiqiladi. Chek va smena saqlanadi. Qolish uchun istalgan tugmani bosing.");
}

/// <summary>Что должна сделать касса по очередной проверке (см. <see cref="WorkTimeMonitor"/>).</summary>
public enum WorkTimeAction
{
    None,
    /// <summary>За 10 минут до конца — только предупреждение (плашка), ничего не прерывает.</summary>
    EndWarning,
    /// <summary>Рабочее время закончилось, есть чек или открытая смена — мягкий вопрос (отложить чек / закрыть смену).</summary>
    EndReached,
    /// <summary>Рабочее время закончилось, чека нет, а смена закрыта или кассир не ответил 15 минут — выход из аккаунта (смена остаётся как была).</summary>
    EndHardLogout,
    /// <summary>Бездействие почти дошло до предела — предупреждение за минуту.</summary>
    IdleWarning,
    /// <summary>Бездействие дошло до предела — выход (чек откладывается, смена не закрывается).</summary>
    IdleLogout,
}

/// <summary>
/// 2026-11-08: чистое решение «что делать сейчас» для кассира с ограничением рабочего времени — без UI и без часов: время, простой и правило
/// подаются параметрами, а состояние (что уже предупредили) хранится здесь. Касса раз в несколько секунд зовёт <see cref="Tick"/> и выполняет
/// ответ. Принципы: (1) оплату, печать и открытый диалог НИКОГДА не прерываем — <c>busy</c> = ничего не делаем и проверяем ещё раз на следующем такте;
/// (2) чек с товарами никогда не пропадает — при конце времени кассиру предлагают отложить его, жёсткий выход только без чека; (3) открытая смена не
/// закрывается сама; (4) у кассира без ограничения ничего не происходит (правило null — сброс состояния); (5) простой берётся из монотонного счётчика
/// системы, а рабочее время — из текущих часов на каждом такте, поэтому перевод часов вперёд/назад ничего не ломает (самое худшее — лишнее
/// предупреждение или вопрос, не потеря данных).
/// </summary>
public sealed class WorkTimeMonitor
{
    /// <summary>Чек пуст, смена открыта, рабочее время закончилось — столько ждём ответа кассира, потом выход.</summary>
    public static readonly TimeSpan HardLogoutGrace = TimeSpan.FromMinutes(15);

    /// <summary>Как часто повторять мягкий вопрос «рабочее время закончилось», пока кассир отвечает «Позже».</summary>
    public static readonly TimeSpan PromptRepeat = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan> _idle;
    private readonly Func<WorkRule?> _rule;
    private readonly Func<int> _idleMinutes;

    private DateTime? _warnedEndsAt;
    private DateTime? _lastEndsAt;
    private DateTime? _outsideSince;
    private DateTime? _lastPromptAt;
    private bool _idleWarned;

    public WorkTimeMonitor(Func<DateTime> now, Func<TimeSpan> idle, Func<WorkRule?> rule, Func<int> idleMinutes)
    {
        _now = now;
        _idle = idle;
        _rule = rule;
        _idleMinutes = idleMinutes;
    }

    /// <summary>Рабочее время, о котором сейчас идёт речь (для текста предупреждения).</summary>
    public WorkRule? LastRule { get; private set; }

    /// <summary>Сколько минут осталось до конца (для текста EndWarning).</summary>
    public int LastMinutesLeft { get; private set; }

    public void Reset()
    {
        _warnedEndsAt = null;
        _lastEndsAt = null;
        _outsideSince = null;
        _lastPromptAt = null;
        _idleWarned = false;
        LastRule = null;
    }

    /// <param name="busy">Идёт оплата/печать или открыт диалог/раздел поверх кассы — прерывать нельзя.</param>
    /// <param name="hasCart">В текущем чеке есть позиции.</param>
    /// <param name="shiftOpen">Смена открыта.</param>
    public WorkTimeAction Tick(bool busy, bool hasCart, bool shiftOpen)
    {
        var rule = _rule();
        if (rule is null)
        {
            Reset();
            return WorkTimeAction.None;
        }
        LastRule = rule;
        var now = _now();
        var status = WorkSchedule.Evaluate(rule, now);

        if (status.Verdict == WorkTimeVerdict.Inside && status.EndsAt is { } endsAt)
        {
            _lastEndsAt = endsAt;
            _outsideSince = null;
            _lastPromptAt = null;
            var left = endsAt - now;
            // Часы перевели назад — до конца снова много: предупреждение разрешено получить заново.
            if (left > TimeSpan.FromMinutes(WorkSchedule.EndWarningMinutes + 5))
                _warnedEndsAt = null;
            else if (left <= TimeSpan.FromMinutes(WorkSchedule.EndWarningMinutes) && _warnedEndsAt != endsAt)
            {
                _warnedEndsAt = endsAt;
                LastMinutesLeft = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
                return WorkTimeAction.EndWarning;
            }
        }
        else if (status.Verdict == WorkTimeVerdict.Outside)
        {
            _warnedEndsAt = null;
            if (_outsideSince is null || now < _outsideSince)
                _outsideSince = _lastEndsAt is { } e && e <= now && now - e < TimeSpan.FromDays(1) ? e : now;
            if (!busy)
            {
                var overdue = now - _outsideSince.Value;
                // Жёсткий выход — только без чека (оплата уже исключена через busy): либо смены нет, либо кассир 15 минут не отвечает.
                if (!hasCart && (!shiftOpen || overdue >= HardLogoutGrace))
                    return WorkTimeAction.EndHardLogout;
                if (_lastPromptAt is null || now < _lastPromptAt || now - _lastPromptAt.Value >= PromptRepeat)
                {
                    _lastPromptAt = now;
                    return WorkTimeAction.EndReached;
                }
            }
        }

        return IdleStep(busy);
    }

    private WorkTimeAction IdleStep(bool busy)
    {
        var limit = _idleMinutes();
        if (limit <= 0)
        {
            _idleWarned = false;
            return WorkTimeAction.None;
        }
        var idle = _idle();
        var limitSec = limit * 60.0;
        var warnAtSec = Math.Max(limitSec - 60, limitSec / 2);
        if (idle.TotalSeconds < warnAtSec)
        {
            _idleWarned = false;
            return WorkTimeAction.None;
        }
        if (idle.TotalSeconds >= limitSec)
            return busy ? WorkTimeAction.None : WorkTimeAction.IdleLogout;
        if (!_idleWarned)
        {
            _idleWarned = true;
            return WorkTimeAction.IdleWarning;
        }
        return WorkTimeAction.None;
    }
}
