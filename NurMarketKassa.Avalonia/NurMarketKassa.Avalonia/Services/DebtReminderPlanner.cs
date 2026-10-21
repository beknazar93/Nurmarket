using System.Globalization;
using System.Text.Json;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-21, И9 «Напоминания должникам по расписанию». Планировщик ничего сам не отправляет: он только считает,
/// КОМУ пора напомнить (просрочка не меньше заданной, клиент не исключён, давно не напоминали, есть WhatsApp или Telegram),
/// и говорит «пора напомнить N должникам» (<see cref="DueText"/>) — с учётом расписания «раз в N дней» и тихих часов
/// 21:00–09:00. Отправляет владелец в окне DebtReminderWindow: WhatsApp — только открывает чат с готовым
/// текстом (жмёт «Отправить» сам владелец, в журнале — «открыт чат»), Telegram — после явного подтверждения и не чаще раза
/// в 3–7 дней одному клиенту. Долги — тот же точный расчёт, что «Сводка» и «Долги клиентов» (<see cref="OwnerShopFacts.GetDebtsAsync"/>).
/// Настройки, исключённые клиенты и журнал — свой файл debt-reminders.json в папке данных программы (рядом с ai-chats.json
/// советника); UserPreferences и база не меняются. Телефоны в журнал программы — только первые 4 цифры.</summary>
public static class DebtReminderPlanner
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "debt-reminders.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Языки шаблона сообщения клиенту.</summary>
    public static readonly string[] Languages = { "ru", "ky", "uz" };

    public const string DefaultTemplateRu =
        "Здравствуйте, {name}! Это магазин «{shop}». Напоминаем о долге {amount} сом (уже {days} дн.). Пожалуйста, верните долг, когда сможете. Спасибо!";
    public const string DefaultTemplateKy =
        "Саламатсызбы, {name}! Бул «{shop}» дүкөнү. {amount} сом карызыңызды эскертебиз ({days} күн болду). Мүмкүнчүлүк болгондо карызды кайтарып бериңиз. Рахмат!";
    public const string DefaultTemplateUz =
        "Assalomu alaykum, {name}! Bu «{shop}» do'koni. {amount} so'm qarzingizni eslatamiz ({days} kun bo'ldi). Imkon bo'lganda qarzni qaytaring. Rahmat!";

    /// <summary>Настройки расписания и текста. Хранятся в debt-reminders.json.</summary>
    public sealed class Settings
    {
        /// <summary>Напоминать по расписанию (карточка «Пора напомнить…» в Советнике).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Раз в N дней (1–30).</summary>
        public int EveryDays { get; set; } = 7;

        /// <summary>Минимальная просрочка: дней с самого старого непогашенного чека (0–365).</summary>
        public int MinOverdueDays { get; set; } = 7;

        /// <summary>Одному клиенту — не чаще раза в N дней (3–7).</summary>
        public int PerClientDays { get; set; } = 7;

        /// <summary>Тихие часы: с QuietFromHour до QuietToHour программа не предлагает напоминать.</summary>
        public int QuietFromHour { get; set; } = 21;
        public int QuietToHour { get; set; } = 9;

        /// <summary>Язык сообщения по умолчанию: ru / ky / uz.</summary>
        public string Language { get; set; } = "ru";

        public string TemplateRu { get; set; } = DefaultTemplateRu;
        public string TemplateKy { get; set; } = DefaultTemplateKy;
        public string TemplateUz { get; set; } = DefaultTemplateUz;

        /// <summary>Владелец хотя бы раз подтвердил список (до этого перед отправкой — обязательное подтверждение).</summary>
        public bool ListConfirmed { get; set; }

        /// <summary>Когда последний раз напоминали (открыт чат / отправлено) — от этого считается «раз в N дней».</summary>
        public DateTime? LastBatchAt { get; set; }

        /// <summary>«Напомнить позже» — до этого времени карточка «Пора напомнить» не показывается.</summary>
        public DateTime? PostponedUntil { get; set; }

        public Settings Copy() => (Settings)MemberwiseClone();

        internal void Normalize()
        {
            EveryDays = Math.Clamp(EveryDays, 1, 30);
            MinOverdueDays = Math.Clamp(MinOverdueDays, 0, 365);
            PerClientDays = Math.Clamp(PerClientDays, 3, 7);
            QuietFromHour = Math.Clamp(QuietFromHour, 0, 23);
            QuietToHour = Math.Clamp(QuietToHour, 0, 23);
            Language = NormalizeLang(Language);
            if (string.IsNullOrWhiteSpace(TemplateRu))
                TemplateRu = DefaultTemplateRu;
            if (string.IsNullOrWhiteSpace(TemplateKy))
                TemplateKy = DefaultTemplateKy;
            if (string.IsNullOrWhiteSpace(TemplateUz))
                TemplateUz = DefaultTemplateUz;
        }
    }

    /// <summary>Запись журнала: кому, когда, каким способом. Channel — "whatsapp" / "telegram"; Status — "opened" (открыт
    /// чат WhatsApp, отправляет владелец), "sent" (ушло в Telegram), "failed" (Telegram не принял). Phone — первые 4 цифры.</summary>
    public sealed class LogEntry
    {
        public DateTime At { get; set; }
        public string ClientId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Status { get; set; } = "";
        public double Amount { get; set; }
        public string Language { get; set; } = "ru";
        public string? Error { get; set; }
    }

    private sealed class Store
    {
        public Settings Settings { get; set; } = new();
        public List<LogEntry> Log { get; set; } = new();

        /// <summary>Исключённые из напоминаний: id клиента → имя (чтобы показать список, даже если долг погашен).</summary>
        public Dictionary<string, string> Excluded { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Клиенты, которых владелец уже подтверждал (галочкой) — в следующий раз отмечены сразу.</summary>
        public HashSet<string> Confirmed { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Язык сообщения для клиента, если выбран не язык по умолчанию.</summary>
        public Dictionary<string, string> ClientLanguage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Почему клиент не к напоминанию сейчас.</summary>
    public enum SkipReason
    {
        None,
        NotOverdue,
        Excluded,
        RecentlyReminded,
        NoContact,
    }

    /// <summary>Должник для напоминания. WhatsApp — международный номер (996…) или null; HasTelegram — подписан на бота магазина.
    /// OverdueDays — дней с самого старого непогашенного чека. Exact = false — суммы предварительные (точный расчёт не готов).</summary>
    public sealed record Candidate(string ClientId, string Name, string Phone, string? WhatsApp, bool HasTelegram, double Amount, int Checks,
        DateTime Oldest, int OverdueDays, DateTime? LastRemindedAt, string? LastChannel, SkipReason Skip, bool Confirmed, bool Exact)
    {
        public bool IsDue => Skip == SkipReason.None;

        /// <summary>Чат Telegram (только для отправки из окна напоминаний, наружу не показывается).</summary>
        internal string? TelegramChatId { get; init; }
    }

    private static readonly object Sync = new();
    private static Store? _store;
    private static IReadOnlyList<Candidate>? _last;
    private static DateTime _lastAtUtc = DateTime.MinValue;
    private static Dictionary<string, (string Phone, string? Chat)>? _contacts;
    private static DateTime _contactsAtUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim LoadGate = new(1, 1);

    /// <summary>Как у LabelTemplateStore: стенд снимков выставляет true отражением — файл на диск не пишется.</summary>
#pragma warning disable CS0649 // выставляется стендом отражением
    private static bool _persistenceDisabled;
#pragma warning restore CS0649

    /// <summary>Изменились настройки, журнал или исключённые (с любого потока) — карточка в Советнике может пересчитать текст.</summary>
    public static event Action? Changed;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // ── Настройки ────────────────────────────────────────────────────────────────

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
            // Служебные поля (подтверждение, последний раз, «позже») меняют только методы планировщика.
            copy.ListConfirmed = store.Settings.ListConfirmed;
            copy.LastBatchAt = store.Settings.LastBatchAt;
            copy.PostponedUntil = store.Settings.PostponedUntil;
            store.Settings = copy;
            Save(store);
        }
        _last = null;
        RaiseChanged();
    }

    /// <summary>Сейчас тихие часы (по умолчанию 21:00–09:00) — отправку не предлагать.</summary>
    public static bool InQuietHours(DateTime? now = null)
    {
        var s = GetSettings();
        var h = (now ?? DateTime.Now).Hour;
        return s.QuietFromHour > s.QuietToHour ? h >= s.QuietFromHour || h < s.QuietToHour
            : s.QuietFromHour < s.QuietToHour && h >= s.QuietFromHour && h < s.QuietToHour;
    }

    /// <summary>Текст «Тихие часы 21:00–09:00».</summary>
    public static string QuietHoursText()
    {
        var s = GetSettings();
        var span = $"{s.QuietFromHour:00}:00–{s.QuietToHour:00}:00";
        return T($"Тихие часы {span}: клиентам не пишем.", $"Тынч сааттар {span}: кардарларга жазбайбыз.", $"Quiet hours {span}: no messages to customers.",
            $"Sessiz saatler {span}: müşterilere yazılmaz.", $"Tinch soatlar {span}: mijozlarga yozilmaydi.");
    }

    // ── Кандидаты ───────────────────────────────────────────────────────────────

    /// <summary>Должники к напоминанию сейчас и пропущенные (с причиной): недавно напоминали, исключён, нет ни телефона, ни Telegram,
    /// ещё не просрочен. Сначала — к напоминанию (по сумме), потом пропущенные. Нет связи с сервером — исключение (окно пишет понятно).</summary>
    public static async Task<IReadOnlyList<Candidate>> CandidatesAsync(CancellationToken ct)
    {
        var facts = await OwnerShopFacts.GetDebtsAsync(ct).ConfigureAwait(true);
        var contacts = await ContactsAsync(facts.Debtors, ct).ConfigureAwait(true);
        Settings settings;
        Dictionary<string, string> excluded;
        HashSet<string> confirmed;
        Dictionary<string, LogEntry> lastByClient;
        lock (Sync)
        {
            var store = Data();
            settings = store.Settings.Copy();
            excluded = new Dictionary<string, string>(store.Excluded, StringComparer.OrdinalIgnoreCase);
            confirmed = new HashSet<string>(store.Confirmed, StringComparer.OrdinalIgnoreCase);
            lastByClient = store.Log.Where(l => l.Status is "opened" or "sent")
                .GroupBy(l => l.ClientId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.At).First(), StringComparer.OrdinalIgnoreCase);
        }

        var today = DateTime.Today;
        var now = DateTime.Now;
        var list = new List<Candidate>();
        foreach (var d in facts.Debtors)
        {
            // Продажа в долг без клиента — напомнить некому.
            if (string.IsNullOrWhiteSpace(d.ClientId) || d.Amount <= 0.005)
                continue;
            var phone = !string.IsNullOrWhiteSpace(d.Phone) ? d.Phone.Trim()
                : contacts.TryGetValue(d.ClientId, out var c) ? c.Phone : "";
            var chat = contacts.TryGetValue(d.ClientId, out var c2) && !string.IsNullOrWhiteSpace(c2.Chat) ? c2.Chat : SafeChat(d.ClientId);
            var wa = WhatsAppDigits(phone);
            var days = Math.Max(0, (int)(today - d.Oldest.Date).TotalDays);
            lastByClient.TryGetValue(d.ClientId, out var last);
            var skip = excluded.ContainsKey(d.ClientId) ? SkipReason.Excluded
                : days < settings.MinOverdueDays ? SkipReason.NotOverdue
                : last is not null && (now - last.At).TotalDays < settings.PerClientDays ? SkipReason.RecentlyReminded
                : wa is null && string.IsNullOrWhiteSpace(chat) ? SkipReason.NoContact
                : SkipReason.None;
            list.Add(new Candidate(d.ClientId, d.Name, phone, wa, !string.IsNullOrWhiteSpace(chat), d.Amount, d.Checks, d.Oldest, days,
                last?.At, last?.Channel, skip, confirmed.Contains(d.ClientId), facts.Exact) { TelegramChatId = chat });
        }

        var result = list.OrderBy(c => c.IsDue ? 0 : 1).ThenByDescending(c => c.Amount).ToList();
        _last = result;
        _lastAtUtc = DateTime.UtcNow;
        return result;
    }

    /// <summary>Последний посчитанный список (для карточки без запросов) или null, если не считали / старше 30 минут.</summary>
    public static IReadOnlyList<Candidate>? LastCandidates =>
        _last is { } l && DateTime.UtcNow - _lastAtUtc <= TimeSpan.FromMinutes(30) ? l : null;

    /// <summary>Телефоны и чаты Telegram должников: одним запросом сводки сервера (BE-03), недостающие телефоны — из списка
    /// клиентов (как «Долги клиентов»). Кэш 10 минут. Ошибки — в журнал, без телефонов список всё равно строится.</summary>
    private static async Task<Dictionary<string, (string Phone, string? Chat)>> ContactsAsync(IReadOnlyList<OwnerShopFacts.DebtorFact> debtors, CancellationToken ct)
    {
        if (_contacts is { } cached && DateTime.UtcNow - _contactsAtUtc <= TimeSpan.FromMinutes(10)
            && debtors.All(d => string.IsNullOrWhiteSpace(d.ClientId) || cached.ContainsKey(d.ClientId)))
            return cached;
        await LoadGate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var result = new Dictionary<string, (string Phone, string? Chat)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var info in await App.GetRequiredService<ClientDebtsApiService>().GetDebtorsAsync(0, ct).ConfigureAwait(true))
                    if (!string.IsNullOrWhiteSpace(info.ClientId))
                        result[info.ClientId] = ((info.Phone ?? "").Trim(), info.TelegramChatId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Напоминания должникам: сводка должников с сервера не получена ({ex.GetType().Name}).", "WARNING");
            }

            if (debtors.Any(d => !string.IsNullOrWhiteSpace(d.ClientId) && string.IsNullOrWhiteSpace(d.Phone)
                                 && (!result.TryGetValue(d.ClientId, out var r) || string.IsNullOrWhiteSpace(r.Phone))))
            {
                try
                {
                    foreach (var c in await App.GetRequiredService<IClientsApiService>().GetClientsAsync(null, ct).ConfigureAwait(true))
                    {
                        if (Str(c, "id") is not { Length: > 0 } id || Str(c, "phone") is not { Length: > 0 } phone)
                            continue;
                        result[id] = result.TryGetValue(id, out var e) ? (string.IsNullOrWhiteSpace(e.Phone) ? phone.Trim() : e.Phone, e.Chat) : (phone.Trim(), null);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Напоминания должникам: телефоны клиентов не получены ({ex.GetType().Name}).", "WARNING");
                }
            }

            // Должник, которого нет ни там, ни там, — пустой записью, чтобы кэш не перезапрашивал из-за него.
            foreach (var d in debtors)
                if (!string.IsNullOrWhiteSpace(d.ClientId) && !result.ContainsKey(d.ClientId))
                    result[d.ClientId] = ("", null);
            _contacts = result;
            _contactsAtUtc = DateTime.UtcNow;
            return result;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    private static string? SafeChat(string clientId)
    {
        try
        {
            return TelegramSubscriberStore.TryGetChatId(clientId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Напоминания должникам: подписка клиента на бота не прочитана ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString() : null;

    /// <summary>Международный номер для WhatsApp (как в Советнике): «0700 123 456» → 996700123456, «700123456» → 996700123456.</summary>
    public static string? WhatsAppDigits(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 10 && digits[0] == '0')
            digits = "996" + digits[1..];
        else if (digits.Length == 9)
            digits = "996" + digits;
        return digits.Length >= 11 ? digits : null;
    }

    /// <summary>Телефон для журнала: только первые 4 цифры.</summary>
    public static string MaskPhone(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? "" : (digits.Length > 4 ? digits[..4] : digits) + "…";
    }

    // ── Расписание ──────────────────────────────────────────────────────────────

    /// <summary>Пора ли напомнить (для карточки в Советнике): включено, не тихие часы, прошло «раз в N дней» с прошлого раза,
    /// не отложено и есть к кому. Кандидаты — из последнего расчёта (<see cref="LastCandidates"/>), иначе — оценка по точному
    /// кэшу долгов программы без телефонов. reason — понятная причина (5 языков) и в «да», и в «нет».</summary>
    public static bool IsDue(out string reason)
    {
        var s = GetSettings();
        var now = DateTime.Now;
        if (!s.Enabled)
        {
            reason = T("Напоминания по расписанию выключены.", "Мөөнөт боюнча эскертүүлөр өчүк.", "Scheduled reminders are off.",
                "Planlı hatırlatmalar kapalı.", "Jadval bo'yicha eslatmalar o'chirilgan.");
            return false;
        }
        if (InQuietHours(now))
        {
            reason = QuietHoursText();
            return false;
        }
        if (s.PostponedUntil is { } until && until > now)
        {
            reason = T($"Отложено до {until:dd.MM HH:mm}.", $"{until:dd.MM HH:mm} чейин кийинкиге калтырылды.", $"Postponed until {until:dd.MM HH:mm}.",
                $"{until:dd.MM HH:mm} tarihine ertelendi.", $"{until:dd.MM HH:mm} gacha qoldirildi.");
            return false;
        }
        if (s.LastBatchAt is { } lastBatch && (now - lastBatch).TotalDays < s.EveryDays)
        {
            var next = lastBatch.AddDays(s.EveryDays);
            reason = T($"Следующее напоминание — {next:dd.MM}.", $"Кийинки эскертүү — {next:dd.MM}.", $"Next reminder — {next:dd.MM}.",
                $"Sonraki hatırlatma — {next:dd.MM}.", $"Keyingi eslatma — {next:dd.MM}.");
            return false;
        }
        var count = DueCountEstimate(s);
        if (count is null)
        {
            reason = T("Список должников ещё не загружен.", "Карыздарлардын тизмеси али жүктөлө элек.", "The debtor list is not loaded yet.",
                "Borçlu listesi henüz yüklenmedi.", "Qarzdorlar ro'yxati hali yuklanmagan.");
            return false;
        }
        if (count == 0)
        {
            reason = T($"Нет должников с просрочкой больше {s.MinOverdueDays} дн., кому пора напомнить.",
                $"Мөөнөтү {s.MinOverdueDays} күндөн ашкан, эскертүү керек карыздар жок.",
                $"No debtors overdue more than {s.MinOverdueDays} days to remind.",
                $"Hatırlatılacak {s.MinOverdueDays} günden fazla gecikmiş borçlu yok.",
                $"{s.MinOverdueDays} kundan ortiq kechikkan, eslatish kerak qarzdor yo'q.");
            return false;
        }
        reason = DueSentence(count.Value, s.MinOverdueDays);
        return true;
    }

    /// <summary>Текст карточки «Пора напомнить 4 должникам с просрочкой &gt; 7 дней» или null, если не пора.</summary>
    public static string? DueText() => IsDue(out var reason) ? reason : null;

    /// <summary>То же, что <see cref="DueText"/>, но сначала пересчитывает список (запросы к серверу). Ошибка сети — null.</summary>
    public static async Task<string?> DueTextAsync(CancellationToken ct)
    {
        try
        {
            await CandidatesAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Напоминания должникам: список не посчитан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return null;
        }
        return DueText();
    }

    private static string DueSentence(int count, int minDays) =>
        T($"Пора напомнить {count} должник{RuEnding(count)} с просрочкой > {minDays} дн.",
            $"Мөөнөтү {minDays} күндөн ашкан {count} карыздарга эскертүү убагы келди.",
            $"Time to remind {count} debtor{(count == 1 ? "" : "s")} overdue > {minDays} days.",
            $"{minDays} günden fazla gecikmiş {count} borçluya hatırlatma zamanı.",
            $"{minDays} kundan ortiq kechikkan {count} qarzdorga eslatish vaqti keldi.");

    private static string RuEnding(int n)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        return mod10 == 1 && mod100 != 11 ? "у" : "ам";
    }

    /// <summary>Сколько должников к напоминанию: из последнего расчёта, иначе — по точному кэшу долгов (без проверки телефона).</summary>
    private static int? DueCountEstimate(Settings s)
    {
        if (LastCandidates is { } last)
        {
            // Напоминания после расчёта (открыт чат / отправлено) — уже не «пора».
            return last.Count(c => c.IsDue && !RecentlyReminded(c.ClientId, s));
        }
        if (DebtsView.KnownExact() is not { } exact)
            return null;
        var today = DateTime.Today;
        return exact.Count(d => d.ClientId.Length > 0 && d.Total > 0.005 && d.Sales.Count > 0
                                && (today - d.Oldest.Date).TotalDays >= s.MinOverdueDays
                                && !IsExcluded(d.ClientId) && !RecentlyReminded(d.ClientId, s));
    }

    /// <summary>«Напомнить позже»: карточка «Пора напомнить» не показывается заданное время (по умолчанию — до завтра 10:00).</summary>
    public static void Postpone(TimeSpan? delay = null)
    {
        lock (Sync)
        {
            var store = Data();
            store.Settings.PostponedUntil = delay is { } d ? DateTime.Now + d : DateTime.Today.AddDays(1).AddHours(10);
            Save(store);
        }
        RaiseChanged();
    }

    // ── Клиенты: язык, исключение, подтверждение ───────────────────────────────

    /// <summary>Когда этому клиенту последний раз напоминали (открыт чат / отправлено), или null.</summary>
    public static DateTime? LastRemindedAt(string clientId)
    {
        lock (Sync)
            return Data().Log.Where(l => l.Status is "opened" or "sent" && string.Equals(l.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                .Select(l => (DateTime?)l.At).Max();
    }

    /// <summary>Клиенту напоминали меньше PerClientDays дней назад — второй раз не напоминаем.</summary>
    public static bool RecentlyReminded(string clientId) => RecentlyReminded(clientId, GetSettings());

    private static bool RecentlyReminded(string clientId, Settings s) =>
        LastRemindedAt(clientId) is { } at && (DateTime.Now - at).TotalDays < s.PerClientDays;

    public static string LanguageFor(string clientId)
    {
        lock (Sync)
        {
            var store = Data();
            return store.ClientLanguage.TryGetValue(clientId, out var lang) ? NormalizeLang(lang) : store.Settings.Language;
        }
    }

    public static void SetLanguage(string clientId, string lang)
    {
        lang = NormalizeLang(lang);
        lock (Sync)
        {
            var store = Data();
            if (lang == store.Settings.Language)
                store.ClientLanguage.Remove(clientId);
            else
                store.ClientLanguage[clientId] = lang;
            Save(store);
        }
    }

    public static bool IsExcluded(string clientId)
    {
        lock (Sync)
            return Data().Excluded.ContainsKey(clientId);
    }

    /// <summary>Исключённые из напоминаний: id → имя.</summary>
    public static IReadOnlyDictionary<string, string> ExcludedClients()
    {
        lock (Sync)
            return new Dictionary<string, string>(Data().Excluded, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Исключить клиента из напоминаний (отписка): больше не попадает в «пора напомнить».</summary>
    public static void Exclude(string clientId, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return;
        lock (Sync)
        {
            var store = Data();
            store.Excluded[clientId] = string.IsNullOrWhiteSpace(name) ? clientId : name!.Trim();
            store.Confirmed.Remove(clientId);
            Save(store);
        }
        _last = null;
        PosLogger.Log("Напоминания должникам: клиент исключён из напоминаний.", "INFO");
        RaiseChanged();
    }

    /// <summary>Вернуть клиента в напоминания.</summary>
    public static void Include(string clientId)
    {
        lock (Sync)
        {
            var store = Data();
            if (!store.Excluded.Remove(clientId))
                return;
            Save(store);
        }
        _last = null;
        PosLogger.Log("Напоминания должникам: клиент возвращён в напоминания.", "INFO");
        RaiseChanged();
    }

    /// <summary>Владелец подтвердил список (галочки): эти клиенты в следующий раз отмечены сразу, ListConfirmed = true.</summary>
    public static void ConfirmList(IEnumerable<string> clientIds)
    {
        lock (Sync)
        {
            var store = Data();
            foreach (var id in clientIds)
                if (!string.IsNullOrWhiteSpace(id))
                    store.Confirmed.Add(id);
            store.Settings.ListConfirmed = true;
            Save(store);
        }
        RaiseChanged();
    }

    // ── Текст сообщения ─────────────────────────────────────────────────────────

    /// <summary>Шаблон (ru / ky / uz) с подставленными {name}, {amount}, {shop}, {days}.</summary>
    public static string Render(Candidate c, string lang)
    {
        var s = GetSettings();
        var template = NormalizeLang(lang) switch
        {
            "ky" => s.TemplateKy,
            "uz" => s.TemplateUz,
            _ => s.TemplateRu,
        };
        var amount = Math.Abs(c.Amount - Math.Round(c.Amount)) < 0.005 ? Math.Round(c.Amount).ToString("N0", Ru) : c.Amount.ToString("N2", Ru);
        var shop = (UserPreferences.Instance.StoreName ?? "").Trim();
        return template
            .Replace("{name}", (c.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{amount}", amount, StringComparison.OrdinalIgnoreCase)
            .Replace("{shop}", shop, StringComparison.OrdinalIgnoreCase)
            .Replace("{days}", c.OverdueDays.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    public static string NormalizeLang(string? lang) => (lang ?? "").Trim().ToLowerInvariant() switch
    {
        "ky" or "kg" or "kyrgyz" => "ky",
        "uz" or "uzbek" => "uz",
        _ => "ru",
    };

    /// <summary>Название языка шаблона для выбора в окне.</summary>
    public static string LanguageTitle(string lang) => NormalizeLang(lang) switch
    {
        "ky" => "Кыргызча",
        "uz" => "O'zbekcha",
        _ => "Русский",
    };

    // ── Журнал ──────────────────────────────────────────────────────────────────

    /// <summary>WhatsApp: открыт чат с готовым текстом (отправляет владелец сам) — «открыт чат», а не «отправлено».</summary>
    public static void LogOpened(Candidate c, string lang) => AddLog(c, "whatsapp", "opened", lang, null);

    /// <summary>Telegram: ok — сообщение принято Telegram; иначе — ошибка (причина без секретов).</summary>
    public static void LogSent(Candidate c, string lang, bool ok, string? error = null) =>
        AddLog(c, "telegram", ok ? "sent" : "failed", lang, ok ? null : error);

    private static void AddLog(Candidate c, string channel, string status, string lang, string? error)
    {
        var entry = new LogEntry
        {
            At = DateTime.Now, ClientId = c.ClientId, Name = c.Name, Phone = MaskPhone(c.Phone), Channel = channel, Status = status,
            Amount = c.Amount, Language = NormalizeLang(lang), Error = CleanError(error),
        };
        lock (Sync)
        {
            var store = Data();
            store.Log.Add(entry);
            if (store.Log.Count > 2000)
                store.Log.RemoveRange(0, store.Log.Count - 2000);
            if (status != "failed")
                store.Settings.LastBatchAt = entry.At;
            Save(store);
        }
        PosLogger.Log($"Напоминания должникам: {channel} — {status}" + (entry.Phone.Length > 0 ? $" ({entry.Phone})" : "") + ".", "INFO");
        RaiseChanged();
    }

    /// <summary>Причина ошибки для журнала: без токена бота (на случай, если адрес попал в текст исключения), не длиннее 200 знаков.</summary>
    private static string? CleanError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return null;
        var clean = System.Text.RegularExpressions.Regex.Replace(error, @"bot\d+:[A-Za-z0-9_\-]+", "bot***");
        return clean.Length > 200 ? clean[..200] : clean;
    }

    /// <summary>Журнал, новые сверху.</summary>
    public static IReadOnlyList<LogEntry> Journal(int take = 500)
    {
        lock (Sync)
            return Data().Log.OrderByDescending(l => l.At).Take(take).ToList();
    }

    // ── Файл ────────────────────────────────────────────────────────────────────

    private static Store Data()
    {
        if (_store is not null)
            return _store;
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    // Словари после чтения — без учёта регистра id.
                    loaded.Settings ??= new Settings();
                    loaded.Settings.Normalize();
                    loaded.Log ??= new List<LogEntry>();
                    loaded.Excluded = new Dictionary<string, string>(loaded.Excluded ?? new(), StringComparer.OrdinalIgnoreCase);
                    loaded.Confirmed = new HashSet<string>(loaded.Confirmed ?? new(), StringComparer.OrdinalIgnoreCase);
                    loaded.ClientLanguage = new Dictionary<string, string>(loaded.ClientLanguage ?? new(), StringComparer.OrdinalIgnoreCase);
                    return _store = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Напоминания должникам: файл настроек не прочитан ({ex.GetType().Name}) — настройки по умолчанию.", "WARNING");
        }
        return _store = new Store();
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
            // Сначала во временный файл — оборванная запись не испортит журнал.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(store, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Напоминания должникам: файл настроек не сохранён ({ex.GetType().Name}).", "ERROR");
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
            PosLogger.Log($"Напоминания должникам: подписчик Changed упал ({ex.GetType().Name}).", "WARNING");
        }
    }
}
