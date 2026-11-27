using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-26 («Рассылки по сегментам клиентов», ТОП-15 п. 2 отчёта о конкурентах): правила и хранилище рассылки. Ничего не отправляет:
/// сегмент (должники, «не покупали N дней», VIP по сумме покупок, день рождения, все с телефоном) → список получателей → владелец открывает
/// чаты WhatsApp ПО ОДНОМУ (wa.me с готовым текстом) и сам нажимает «Отправить». Защита: «не беспокоить» (отписка), один клиент — не чаще раза
/// в N дней, один номер — один раз, не больше N чатов в день, тихие часы (общие с напоминаниями должникам). Данные клиентов, долги и покупки приходят
/// из существующих запросов (<see cref="ClientMailingData"/>), здесь — чистая логика и свой файл client-mailing.json (рядом с debt-reminders.json).
/// В журнал и файл телефоны попадают только первыми 4 цифрами.</summary>
public static class ClientMailing
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string DefaultFilePath = Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName, "client-mailing.json");
    private static string? _filePathForTests;
    private static string FilePath => _filePathForTests ?? DefaultFilePath;

    /// <summary>2026-11-26 (стресс-тест): журнал хранит все записи за столько дней (верхний предел «не чаще раза в N дней»), чтобы защита от повторов не забывала.</summary>
    private const int LogKeepDays = 90;
    private const int LogMaxEntries = 20000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public enum Segment { Debtors, Inactive, Vip, Birthday, AllWithPhone }

    public static readonly Segment[] Segments = { Segment.Debtors, Segment.Inactive, Segment.Vip, Segment.Birthday, Segment.AllWithPhone };
    public static readonly string[] Languages = { "ru", "ky", "uz" };

    /// <summary>Максимальная длина текста: ссылка wa.me с текстом не должна вырасти до предела адреса (~2000 знаков после кодирования; кириллица — по 6 знаков на букву).</summary>
    public const int MaxTextLength = 320;

    /// <summary>Предел длины готовой ссылки: длиннее система (Windows, Android) может её не открыть.</summary>
    public const int MaxUrlLength = 2000;

    /// <summary>Ссылка слишком длинная для открытия (длинное имя или текст) — чат не открываем, владелец сократит текст.</summary>
    public static bool UrlTooLong(string? url) => (url?.Length ?? 0) > MaxUrlLength;

    /// <summary>Клиент для рассылки. Debt — долг (0 — нет), LastPurchase — последняя покупка за период загрузки, Spent — сумма покупок за период, Birthday — только день и месяц (год условный).</summary>
    public sealed record Contact(string ClientId, string Name, string Phone, string? WhatsApp, double Debt, DateTime? LastPurchase, double Spent, int Receipts, DateTime? Birthday);

    /// <summary>Почему клиент недоступен для рассылки сейчас. NoPhone в список не попадает (только счётчик).</summary>
    public enum SkipReason { None, NoPhone, DoNotDisturb, RecentlyWritten, DuplicatePhone, DailyLimit }

    public sealed record Recipient(Contact Contact, SkipReason Skip, DateTime? LastMailedAt, int DaysSincePurchase)
    {
        public bool IsDue => Skip == SkipReason.None;
    }

    public sealed record PlanResult(IReadOnlyList<Recipient> Items, int NoPhone, int InSegment);

    /// <summary>Настройки рассылки (файл client-mailing.json).</summary>
    public sealed class Settings
    {
        public int InactiveDays { get; set; } = 30;
        public double VipMinSpent { get; set; } = 20000;
        public int BirthdayDaysAhead { get; set; } = 7;
        public int MinGapDays { get; set; } = 14;
        public int DailyLimit { get; set; } = 30;
        public string Language { get; set; } = "";

        public Settings Copy() => (Settings)MemberwiseClone();

        internal void Normalize()
        {
            InactiveDays = Math.Clamp(InactiveDays, 7, 300);
            VipMinSpent = Math.Clamp(VipMinSpent, 0, 100_000_000);
            BirthdayDaysAhead = Math.Clamp(BirthdayDaysAhead, 0, 30);
            MinGapDays = Math.Clamp(MinGapDays, 1, 90);
            DailyLimit = Math.Clamp(DailyLimit, 5, 200);
            Language = Languages.Contains(Language ?? "") ? Language! : "";
        }
    }

    /// <summary>Запись журнала: чат открыт (отправляет владелец сам). Phone — первые 4 цифры.</summary>
    public sealed class LogEntry
    {
        public DateTime At { get; set; }
        public string ClientId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Segment { get; set; } = "";
        public string Language { get; set; } = "ru";
    }

    private sealed class Store
    {
        public Settings Settings { get; set; } = new();
        public Dictionary<string, string> Templates { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> DoNotDisturb { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Birthdays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<LogEntry> Log { get; set; } = new();
    }

    private static readonly object Sync = new();
    private static Store? _store;

    /// <summary>Как у DebtReminderPlanner: тесты и стенд снимков (<see cref="ResetForTests"/>) отключают запись на диск.</summary>
    private static bool _persistenceDisabled;

    public static event Action? Changed;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // ── Сегменты и список ───────────────────────────────────────────────────────

    /// <summary>Клиент входит в сегмент (без учёта защиты от повторов — её накладывает <see cref="Plan"/>).</summary>
    public static bool InSegment(Segment segment, Contact c, Settings s, DateTime today) => segment switch
    {
        Segment.Debtors => c.Debt > 0.005,
        Segment.Inactive => c.LastPurchase is { } last && (today.Date - last.Date).TotalDays >= s.InactiveDays,
        Segment.Vip => c.Spent >= s.VipMinSpent && c.Spent > 0.005,
        Segment.Birthday => DaysToBirthday(c.Birthday, today) is { } d && d <= s.BirthdayDaysAhead,
        _ => true,
    };

    /// <summary>Сколько дней до ближайшего дня рождения (0 — сегодня); null — день рождения неизвестен. 29 февраля в невисокосный год — 28 февраля.</summary>
    public static int? DaysToBirthday(DateTime? birthday, DateTime today)
    {
        if (birthday is not { } b)
            return null;
        var t = today.Date;
        var next = BirthdayIn(t.Year, b);
        if (next < t)
            next = BirthdayIn(t.Year + 1, b);
        return (int)(next - t).TotalDays;
    }

    private static DateTime BirthdayIn(int year, DateTime b) => new(year, b.Month, Math.Min(b.Day, DateTime.DaysInMonth(year, b.Month)));

    /// <summary>«15.03», «15.03.1990», «1990-03-15» → день и месяц (год условный 2000). Пусто — null (без ошибки); мусор — false.</summary>
    public static bool TryParseBirthday(string? text, out DateTime? value)
    {
        value = null;
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return true;
        int day, month;
        var m = Regex.Match(t, @"^(\d{1,2})[./-](\d{1,2})(?:[./-]\d{2,4})?$");
        var iso = Regex.Match(t, @"^\d{4}-(\d{1,2})-(\d{1,2})");
        if (iso.Success)
        {
            month = int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture);
            day = int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (m.Success)
        {
            day = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else
            return false;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2000, month))
            return false;
        value = new DateTime(2000, month, day);
        return true;
    }

    /// <summary>Список получателей сегмента: в сегменте, с номером WhatsApp, не «не беспокоить», не писали раньше чем N дней назад, один номер — один раз,
    /// не больше дневного лимита (сколько чатов уже открыто сегодня — <paramref name="openedToday"/>). Недоступные остаются в списке с причиной (кроме «нет телефона»).</summary>
    public static PlanResult Plan(Segment segment, IEnumerable<Contact> contacts, Settings s, ISet<string> doNotDisturb, IReadOnlyDictionary<string, DateTime> lastMailed,
        DateTime now, int openedToday = 0)
    {
        var all = contacts as IReadOnlyCollection<Contact> ?? contacts.ToList();
        // 2026-11-26 (стресс-тест): один номер у нескольких карточек клиента (дубль карточки, семья) — «не беспокоить» и «недавно писали» действуют на НОМЕР:
        // раньше клиент, попросивший не писать, получал сообщение через вторую карточку с тем же телефоном.
        var blockedPhones = new Dictionary<string, (SkipReason Reason, DateTime? At)>(StringComparer.Ordinal);
        foreach (var c in all)
        {
            if (c.WhatsApp is not { } phone)
                continue;
            if (doNotDisturb.Contains(c.ClientId))
                blockedPhones[phone] = (SkipReason.DoNotDisturb, null);
            else if (lastMailed.TryGetValue(c.ClientId, out var at) && (now - at).TotalDays < s.MinGapDays
                     && (!blockedPhones.TryGetValue(phone, out var known) || known.Reason == SkipReason.RecentlyWritten && known.At < at))
                blockedPhones[phone] = (SkipReason.RecentlyWritten, at);
        }
        var members = all.Where(c => InSegment(segment, c, s, now)).ToList();
        var noPhone = members.Count(c => c.WhatsApp is null);
        var withPhone = members.Where(c => c.WhatsApp is not null);
        withPhone = segment switch
        {
            Segment.Debtors => withPhone.OrderByDescending(c => c.Debt),
            Segment.Inactive => withPhone.OrderByDescending(c => c.Spent),
            Segment.Vip => withPhone.OrderByDescending(c => c.Spent),
            Segment.Birthday => withPhone.OrderBy(c => DaysToBirthday(c.Birthday, now) ?? 999),
            _ => withPhone.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var seenPhones = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<Recipient>();
        var due = 0;
        foreach (var c in withPhone)
        {
            DateTime? lastAt = lastMailed.TryGetValue(c.ClientId, out var last) ? last : null;
            var byPhone = blockedPhones.TryGetValue(c.WhatsApp!, out var blocked) ? blocked.Reason : SkipReason.None;
            if (byPhone == SkipReason.RecentlyWritten && (lastAt is null || blocked.At > lastAt))
                lastAt = blocked.At;
            var skip = doNotDisturb.Contains(c.ClientId) ? SkipReason.DoNotDisturb
                : lastAt is { } at && (now - at).TotalDays < s.MinGapDays ? SkipReason.RecentlyWritten
                : byPhone != SkipReason.None ? byPhone
                : !seenPhones.Add(c.WhatsApp!) ? SkipReason.DuplicatePhone
                : openedToday + due >= s.DailyLimit ? SkipReason.DailyLimit
                : SkipReason.None;
            if (skip == SkipReason.None)
                due++;
            var days = c.LastPurchase is { } lp ? Math.Max(0, (int)(now.Date - lp.Date).TotalDays) : 0;
            items.Add(new Recipient(c, skip, lastAt, days));
        }
        return new PlanResult(items.OrderBy(i => i.IsDue ? 0 : 1).ToList(), noPhone, members.Count);
    }

    /// <summary>2026-11-26 (стресс-тест): номер для WhatsApp — тем же правилом, что ссылка wa.me (<see cref="DebtWhatsAppLink.NormalizePhone"/>: +996 / 0555 / 996555 / 555,
    /// пробелы, дефисы, скобки; чужая страна — только с «+»). Раньше список брал другое правило (любые 11+ цифр) — в очередь попадали номера, для которых
    /// ссылка не строилась, и очередь останавливалась «Не удалось открыть WhatsApp». Городские номера Кыргызстана (код 3…: Бишкек 312, Ош 3222) — без WhatsApp.</summary>
    public static string? WhatsAppNumber(string? phone)
    {
        var n = DebtWhatsAppLink.NormalizePhone(phone);
        return n is { Length: 12 } && n.StartsWith("9963", StringComparison.Ordinal) ? null : n;
    }

    // ── Согласие ────────────────────────────────────────────────────────────────

    /// <summary>Явный отказ по ответу сервера (согласия клиента на сообщения; дата есть — значит, клиент или кассир их менял). Рассылка («маркетинг») — отказ, если
    /// маркетинговое согласие выключено при заполненной дате; напоминание о долге — сервисное: отказ, если выключены оба (как у кнопки «WA» в долгах).</summary>
    public static bool IsRefused(Segment segment, bool service, bool marketing, bool hasUpdatedAt) => segment == Segment.Debtors
        ? DebtWhatsAppLink.IsExplicitRefusal(service, marketing, hasUpdatedAt)
        : hasUpdatedAt && !marketing;

    // ── Текст ───────────────────────────────────────────────────────────────────

    /// <summary>Подстановки: {имя}/{name}, {долг}/{debt}/{amount}, {магазин}/{shop}, {дней}/{days} (дней с последней покупки). Данные только этого клиента.
    /// Если имени нет — убирается «, !» и двойные пробелы.</summary>
    public static string Render(string template, Contact c, string? shop, DateTime? now = null)
    {
        var name = (c.Name ?? "").Trim();
        var days = c.LastPurchase is { } lp ? Math.Max(0, (int)((now ?? DateTime.Now).Date - lp.Date).TotalDays) : 0;
        var text = Regex.Replace(template ?? "", @"\{(имя|name)\}", _ => name, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\{(долг|debt|amount)\}", _ => DebtWhatsAppLink.FormatAmount(c.Debt), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\{(магазин|shop)\}", _ => (shop ?? "").Trim(), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\{(дней|days)\}", _ => days.ToString(CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
        return Tidy(text);
    }

    /// <summary>Пустые подстановки не оставляют «Здравствуйте, !», «магазин «»», двойные пробелы.</summary>
    internal static string Tidy(string text)
    {
        text = Regex.Replace(text, @"^[\s,]+", ""); // «{имя}, поздравляем» без имени не начинается с запятой
        text = Regex.Replace(text, @"\s+,", ",");
        text = Regex.Replace(text, @",\s*([!.?])", "$1");
        text = text.Replace("«»", "").Replace("«  »", "");
        text = Regex.Replace(text, @"[ \t]{2,}", " ");
        text = Regex.Replace(text, @"\s+([!.?,])", "$1");
        return text.Trim();
    }

    public static string DefaultTemplate(Segment segment, string lang) => (segment, NormalizeLang(lang)) switch
    {
        (Segment.Debtors, "ky") => "Саламатсызбы, {имя}! Бул «{магазин}» дүкөнү. {долг} сом карызыңызды эскертебиз. Мүмкүнчүлүк болгондо төлөп бериңиз. Рахмат!",
        (Segment.Debtors, "uz") => "Assalomu alaykum, {имя}! Bu «{магазин}» do'koni. {долг} so'm qarzingizni eslatamiz. Imkon bo'lganda to'lang. Rahmat!",
        (Segment.Debtors, _) => "Здравствуйте, {имя}! Это магазин «{магазин}». Напоминаем о долге {долг} сом. Пожалуйста, погасите его, когда сможете. Спасибо!",
        (Segment.Inactive, "ky") => "Саламатсызбы, {имя}! «{магазин}» дүкөнүнө келбей {дней} күн болду. Сизди дагы көргүбүз келет!",
        (Segment.Inactive, "uz") => "Assalomu alaykum, {имя}! «{магазин}» do'koniga kelmaganingizga {дней} kun bo'ldi. Sizni yana ko'rishdan xursand bo'lamiz!",
        (Segment.Inactive, _) => "Здравствуйте, {имя}! Давно не видели вас в магазине «{магазин}» — уже {дней} дн. Будем рады видеть вас снова!",
        (Segment.Vip, "ky") => "Саламатсызбы, {имя}! Дайыма биз менен болгонуңуз үчүн рахмат. «{магазин}» дүкөнү сиз үчүн атайын сунуш даярдады. Кирип туруңуз!",
        (Segment.Vip, "uz") => "Assalomu alaykum, {имя}! Doimo biz bilan bo'lganingiz uchun rahmat. «{магазин}» do'koni siz uchun maxsus taklif tayyorladi. Kirib turing!",
        (Segment.Vip, _) => "Здравствуйте, {имя}! Спасибо, что вы с нами — вы наш постоянный клиент. Магазин «{магазин}» приготовил для вас особое предложение. Заходите!",
        (Segment.Birthday, "ky") => "{имя}, туулган күнүңүз менен куттуктайбыз! «{магазин}» дүкөнү сизге бакыт жана ийгилик каалайт. Конокко келиңиз!",
        (Segment.Birthday, "uz") => "{имя}, tug'ilgan kuningiz bilan tabriklaymiz! «{магазин}» do'koni sizga baxt va omad tilaydi. Mehmonga keling!",
        (Segment.Birthday, _) => "{имя}, поздравляем вас с днём рождения! Магазин «{магазин}» желает вам радости и добра. Ждём вас в гости!",
        (_, "ky") => "Саламатсызбы, {имя}! «{магазин}» дүкөнү жаңылыктар тууралуу кабарлайт. Кирип туруңуз!",
        (_, "uz") => "Assalomu alaykum, {имя}! «{магазин}» do'koni yangiliklar haqida xabar beradi. Kirib turing!",
        _ => "Здравствуйте, {имя}! Магазин «{магазин}» напоминает о себе: у нас много нового. Заходите!",
    };

    public static string SegmentKey(Segment segment) => segment.ToString().ToLowerInvariant();

    public static string SegmentTitle(Segment segment) => segment switch
    {
        Segment.Debtors => T("Должники", "Карызы бар кардарлар", "Debtors", "Borçlular", "Qarzdorlar"),
        Segment.Inactive => T("Давно не покупали", "Көптөн бери сатып албагандар", "Haven't bought for a while", "Uzun süredir almayanlar", "Anchadan beri xarid qilmaganlar"),
        Segment.Vip => T("VIP-клиенты", "VIP-кардарлар", "VIP customers", "VIP müşteriler", "VIP mijozlar"),
        Segment.Birthday => T("День рождения", "Туулган күн", "Birthday", "Doğum günü", "Tug'ilgan kun"),
        _ => T("Все с телефоном", "Телефону барлардын баары", "Everyone with a phone", "Telefonu olan herkes", "Telefoni borlarning hammasi"),
    };

    /// <summary>Что делает сегмент (одна строка под выбором).</summary>
    public static string SegmentHint(Segment segment, Settings s) => segment switch
    {
        Segment.Debtors => T("Клиенты с непогашенным долгом.", "Карызы төлөнө элек кардарлар.", "Customers with an unpaid debt.", "Ödenmemiş borcu olan müşteriler.", "To'lanmagan qarzi bor mijozlar."),
        Segment.Inactive => T($"Покупали, но последняя покупка была {s.InactiveDays} и более дней назад (по чекам за год).",
            $"Сатып алган, бирок акыркы сатып алуу {s.InactiveDays} жана андан көп күн мурун болгон (бир жылдын чектери боюнча).",
            $"Bought before, but the last purchase was {s.InactiveDays}+ days ago (by receipts of the last year).",
            $"Daha önce almış, son alışveriş {s.InactiveDays}+ gün önce (son bir yılın fişlerine göre).",
            $"Oldin xarid qilgan, oxirgi xarid {s.InactiveDays} va undan ortiq kun oldin (oxirgi yil cheklari bo'yicha)."),
        Segment.Vip => T($"Сумма покупок за год не меньше {DebtWhatsAppLink.FormatAmount(s.VipMinSpent)} сом.", $"Бир жылдагы сатып алуулардын суммасы {DebtWhatsAppLink.FormatAmount(s.VipMinSpent)} сомдон кем эмес.",
            $"Purchases over the last year total at least {DebtWhatsAppLink.FormatAmount(s.VipMinSpent)} som.", $"Son bir yılın alışverişi en az {DebtWhatsAppLink.FormatAmount(s.VipMinSpent)} som.",
            $"Oxirgi yildagi xaridlar kamida {DebtWhatsAppLink.FormatAmount(s.VipMinSpent)} so'm."),
        Segment.Birthday => T($"День рождения в ближайшие {s.BirthdayDaysAhead} дн. Даты в карточке клиента на сервере нет — укажите её здесь справа, и она запомнится.",
            $"Туулган күнү алдыдагы {s.BirthdayDaysAhead} күндө. Серверде кардардын картасында дата жок — аны оң жактан жазыңыз, эстеп калат.",
            $"Birthday within {s.BirthdayDaysAhead} days. The customer card on the server has no date — enter it on the right and it will be remembered.",
            $"Doğum günü önümüzdeki {s.BirthdayDaysAhead} gün içinde. Sunucudaki müşteri kartında tarih yok — sağdan girin, hatırlanır.",
            $"Tug'ilgan kun keyingi {s.BirthdayDaysAhead} kun ichida. Serverda mijoz kartasida sana yo'q — o'ng tomonda kiriting, eslab qolinadi."),
        _ => T("Все клиенты, у кого есть телефон.", "Телефону бар бардык кардарлар.", "All customers who have a phone.", "Telefonu olan tüm müşteriler.", "Telefoni bor barcha mijozlar."),
    };

    public static string SkipText(SkipReason reason, int minGapDays, int dailyLimit) => reason switch
    {
        SkipReason.DoNotDisturb => T("не беспокоить", "тынчсыздандырбоо", "do not disturb", "rahatsız etme", "bezovta qilmaslik"),
        SkipReason.RecentlyWritten => T($"недавно писали — не чаще раза в {minGapDays} дн.", $"жакында жазылган — {minGapDays} күндө бир жолудан көп эмес", $"messaged recently — once per {minGapDays} days at most",
            $"yakın zamanda yazıldı — en fazla {minGapDays} günde bir", $"yaqinda yozilgan — {minGapDays} kunda bir martadan ko'p emas"),
        SkipReason.DuplicatePhone => T("тот же номер уже в списке", "бул номер тизмеде бар", "the same number is already in the list", "aynı numara listede var", "bu raqam ro'yxatda bor"),
        SkipReason.DailyLimit => T($"дневной лимит {dailyLimit} чатов", $"күнүнө {dailyLimit} чат чеги", $"daily limit of {dailyLimit} chats", $"günlük {dailyLimit} sohbet sınırı", $"kunlik {dailyLimit} ta chat chegarasi"),
        _ => "",
    };

    public static string NormalizeLang(string? lang) => (lang ?? "").Trim().ToLowerInvariant() switch
    {
        "ky" or "kg" => "ky",
        "uz" => "uz",
        _ => "ru",
    };

    public static string LanguageTitle(string lang) => NormalizeLang(lang) switch
    {
        "ky" => "Кыргызча",
        "uz" => "O'zbekcha",
        _ => "Русский",
    };

    /// <summary>Язык сообщения по умолчанию: выбранный владельцем, иначе язык интерфейса (ky / uz), иначе русский.</summary>
    public static string DefaultLanguage()
    {
        var chosen = GetSettings().Language;
        if (chosen.Length > 0)
            return chosen;
        var app = DebtWhatsAppLink.AppLanguageCode();
        return app is "ky" or "uz" ? app : "ru";
    }

    // ── Настройки, шаблоны, «не беспокоить», дни рождения ───────────────────────

    public static Settings GetSettings()
    {
        lock (Sync)
            return Data().Settings.Copy();
    }

    public static void SaveSettings(Settings settings)
    {
        var copy = settings.Copy();
        copy.Normalize();
        lock (Sync)
        {
            var store = Data();
            store.Settings = copy;
            Save(store);
        }
        RaiseChanged();
    }

    /// <summary>Текст сегмента на языке: сохранённый владельцем или стандартный.</summary>
    public static string GetTemplate(Segment segment, string lang)
    {
        lock (Sync)
            return Data().Templates.TryGetValue(TemplateKey(segment, lang), out var text) && !string.IsNullOrWhiteSpace(text) ? text : DefaultTemplate(segment, lang);
    }

    public static void SaveTemplate(Segment segment, string lang, string? text)
    {
        var clean = (text ?? "").Trim();
        if (clean.Length > MaxTextLength)
            clean = clean[..MaxTextLength];
        lock (Sync)
        {
            var store = Data();
            if (clean.Length == 0 || clean == DefaultTemplate(segment, lang))
                store.Templates.Remove(TemplateKey(segment, lang));
            else
                store.Templates[TemplateKey(segment, lang)] = clean;
            Save(store);
        }
    }

    private static string TemplateKey(Segment segment, string lang) => SegmentKey(segment) + "|" + NormalizeLang(lang);

    public static ISet<string> DoNotDisturb()
    {
        lock (Sync)
            return new HashSet<string>(Data().DoNotDisturb.Keys, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, string> DoNotDisturbNames()
    {
        lock (Sync)
            return new Dictionary<string, string>(Data().DoNotDisturb, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>«Не беспокоить»: клиент больше не попадает в рассылки (отписка по просьбе клиента).</summary>
    public static void SetDoNotDisturb(string clientId, string? name, bool on)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return;
        lock (Sync)
        {
            var store = Data();
            if (on)
                store.DoNotDisturb[clientId] = string.IsNullOrWhiteSpace(name) ? clientId : name!.Trim();
            else if (!store.DoNotDisturb.Remove(clientId))
                return;
            Save(store);
        }
        PosLogger.Log("Рассылка: клиент " + (on ? "помечен «не беспокоить»." : "возвращён в рассылки."), "INFO");
        RaiseChanged();
    }

    /// <summary>Дни рождения, которые владелец указал сам (id → «дд.мм»), как день и месяц.</summary>
    public static IReadOnlyDictionary<string, DateTime> LocalBirthdays()
    {
        lock (Sync)
        {
            var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, text) in Data().Birthdays)
                if (TryParseBirthday(text, out var value) && value is { } v)
                    result[id] = v;
            return result;
        }
    }

    public static void SetBirthday(string clientId, DateTime? birthday)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return;
        lock (Sync)
        {
            var store = Data();
            if (birthday is { } b)
                store.Birthdays[clientId] = b.ToString("dd.MM", CultureInfo.InvariantCulture);
            else
                store.Birthdays.Remove(clientId);
            Save(store);
        }
        RaiseChanged();
    }

    // ── Журнал ──────────────────────────────────────────────────────────────────

    /// <summary>Последний раз, когда клиенту открывали чат (любой сегмент).</summary>
    public static IReadOnlyDictionary<string, DateTime> LastMailed()
    {
        lock (Sync)
            return Data().Log.GroupBy(l => l.ClientId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(l => l.At), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Сколько чатов открыто сегодня (для дневного лимита).</summary>
    public static int OpenedToday(DateTime? now = null)
    {
        var day = (now ?? DateTime.Now).Date;
        lock (Sync)
            return Data().Log.Count(l => l.At.Date == day);
    }

    public static void LogOpened(Contact c, Segment segment, string lang, DateTime? now = null)
    {
        var entry = new LogEntry
        {
            At = now ?? DateTime.Now, ClientId = c.ClientId, Name = c.Name, Phone = DebtReminderPlanner.MaskPhone(c.Phone),
            Segment = SegmentKey(segment), Language = NormalizeLang(lang),
        };
        lock (Sync)
        {
            var store = Data();
            store.Log.Add(entry);
            // 2026-11-26 (стресс-тест): раньше журнал резался до 3000 записей — при лимите 200 чатов в день это ~15 дней, и «не чаще раза в 30–90 дней»
            // забывало, кому писали. Теперь всё за 90 дней хранится, сверх — не больше 20 000 записей.
            if (store.Log.Count > 3000)
            {
                var cutoff = entry.At.AddDays(-LogKeepDays);
                store.Log.RemoveAll(l => l.At < cutoff);
                if (store.Log.Count > LogMaxEntries)
                    store.Log.RemoveRange(0, store.Log.Count - LogMaxEntries);
            }
            Save(store);
        }
        PosLogger.Log($"Рассылка: открыт чат ({SegmentKey(segment)}, {entry.Phone}).", "INFO");
        RaiseChanged();
    }

    public static IReadOnlyList<LogEntry> Journal(int take = 300)
    {
        lock (Sync)
            return Data().Log.OrderByDescending(l => l.At).Take(take).ToList();
    }

    /// <summary>Снять отметку (владелец открыл чат, но не отправил): клиент снова доступен для рассылки.</summary>
    public static void RemoveLog(LogEntry entry)
    {
        lock (Sync)
        {
            var store = Data();
            if (store.Log.RemoveAll(l => l.At == entry.At && string.Equals(l.ClientId, entry.ClientId, StringComparison.OrdinalIgnoreCase)) == 0)
                return;
            Save(store);
        }
        RaiseChanged();
    }

    public static string SegmentTitleByKey(string key)
    {
        foreach (var segment in Segments)
            if (SegmentKey(segment) == key)
                return SegmentTitle(segment);
        return key;
    }

    // ── Файл ────────────────────────────────────────────────────────────────────

    private static Store Data()
    {
        if (_store is not null)
            return _store;
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath), JsonOptions) is { } loaded)
            {
                loaded.Settings ??= new Settings();
                loaded.Settings.Normalize();
                loaded.Templates = new Dictionary<string, string>(loaded.Templates ?? new(), StringComparer.Ordinal);
                loaded.DoNotDisturb = new Dictionary<string, string>(loaded.DoNotDisturb ?? new(), StringComparer.OrdinalIgnoreCase);
                loaded.Birthdays = new Dictionary<string, string>(loaded.Birthdays ?? new(), StringComparer.OrdinalIgnoreCase);
                loaded.Log ??= new List<LogEntry>();
                return _store = loaded;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Рассылка: файл настроек не прочитан ({ex.GetType().Name}) — настройки по умолчанию.", "WARNING");
            // 2026-11-26 (стресс-тест): испорченный файл раньше молча перезаписывался при первом сохранении — пропадал список «не беспокоить»
            // (клиенты, просившие не писать). Теперь копия остаётся рядом (client-mailing.json.broken-…), её можно восстановить.
            BackupBroken();
        }
        return _store = new Store();
    }

    private static void BackupBroken()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Рассылка: копия испорченного файла не сохранена ({ex.GetType().Name}).", "WARNING");
        }
    }

    private static void Save(Store store)
    {
        if (_persistenceDisabled)
            return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(store, JsonOptions));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Рассылка: файл настроек не сохранён ({ex.GetType().Name}).", "ERROR");
        }
    }

    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Рассылка: подписчик Changed упал ({ex.GetType().Name}).", "WARNING");
        }
    }

    /// <summary>2026-11-27: смена компании — client-mailing.json унесён в карантин (клиенты и «не беспокоить» прежней компании, CompanyStoreRegistry);
    /// хранилище в памяти сбрасываем, чтобы оно не записалось в файл новой компании. Следующее обращение прочитает файл заново.</summary>
    public static void ResetMemory()
    {
        lock (Sync)
            _store = null;
    }

    /// <summary>Для тестов: пустое хранилище; без <paramref name="persist"/> файл на диск не пишется. <paramref name="filePath"/> — читать и писать этот файл
    /// (хранилище загрузится из него при первом обращении, как после перезапуска программы).</summary>
    public static void ResetForTests(bool persist = false, string? filePath = null)
    {
        lock (Sync)
        {
            _filePathForTests = filePath;
            _store = filePath is null ? new Store() : null;
            _persistenceDisabled = !persist;
        }
    }
}
