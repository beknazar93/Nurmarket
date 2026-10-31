using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-21, «доведи Нур Советника до 10» (И3 «Утренний отчёт»). Сам в 9:00 (время и вкл/выкл — в настройках):
/// выручка вчера к позавчера и к тому же дню прошлой недели, неделя к прошлой, прибыль, долги и просрочка, что заканчивается,
/// сроки годности и странности (удаления из чека, возвраты, скидки, разница в кассе при закрытии смены) — карточкой в окне
/// Нур Советника (событие <see cref="ReportReady"/>, <see cref="Latest"/>) и в Telegram владельцу.
///
/// Цифры считает ПРОГРАММА из тех же источников, что разделы: <see cref="OwnerPeriodStats"/> (отчёт сервера, как «Сводка»),
/// <see cref="OwnerShopFacts"/> (долги как «Долги клиентов», склад как «ABC»), <see cref="ProductExpiryIndex"/> (сроки с сервера),
/// список продаж сервера (возвраты, скидки), журнал «Удалено из чека» этого ПК, архив смен (Z-отчёты). ИИ пишет только вывод
/// в 2–3 строки и получает обезличенные цифры — без имён клиентов и сотрудников. Каждый источник — со своим таймаутом:
/// не ответил — пункт пропускается (с записью в журнал), остальное показывается.
///
/// Telegram: тот же бот, что сводка смены (<see cref="TelegramBotService.SendAsync"/> — токен и получатель из настроек программы;
/// sendMessage работает и когда бот переведён на сервер NurCRM, вебхук мешает только опросу). У сервера бота нет команды
/// «переслать текст владельцу» (есть только пробное сообщение) — если токена бота на этом ПК нет, отчёт только в окне.
/// Тихие часы 21:00–09:00 — в Telegram не шлём (до 9:00 — откладываем до 9:00, после 21:00 — пропускаем день).
/// Один отчёт в день; программа была выключена в 9:00 — отчёт при первом запуске после 9:00 за вчера.
/// Настройки и журнал «отправлено» — свой файл advisor-morning.json рядом с ai-chats.json советника (не UserPreferences, не база).</summary>
public static class OwnerMorningReport
{
    // ── Модель отчёта (данные готовы для карточки и для Telegram; подписи — на языке интерфейса в момент сборки) ──

    /// <summary>Изменение к прошлому периоду: процент (null — в прошлом периоде продаж не было) и «к чему» («к позавчера»).</summary>
    public sealed record Delta(double? Percent, string Against);

    /// <summary>Плитка цифры. Section — ключ раздела программы владельца для «Открыть …» ("overview", "debts"…).</summary>
    public sealed record Tile(string Key, string Title, string Value, IReadOnlyList<Delta> Deltas, string? Note, bool Danger, string Section);

    /// <summary>Строка списка (заканчивается, сроки, просрочка, странности). Danger — красным (просрочка, недостача).</summary>
    public sealed record Item(string Text, bool Danger, string Section);

    /// <summary>Готовый утренний отчёт. Day — за какой день (вчера), BuiltAt — когда собран. *Total — сколько всего в списке
    /// (показано до 5). OdditiesChecked — странности проверены хотя бы по одному источнику (пусто — «не замечено»).
    /// Problems — что не удалось получить (понятным текстом). Html — для Telegram (≤ 3500 символов), PlainText — для копирования.</summary>
    public sealed record Report(
        DateTime Day,
        DateTime BuiltAt,
        IReadOnlyList<Tile> Tiles,
        IReadOnlyList<Item> RunningOut,
        int RunningOutTotal,
        IReadOnlyList<Item> Expiry,
        int ExpiryTotal,
        IReadOnlyList<Item> Overdue,
        IReadOnlyList<Item> Oddities,
        bool OdditiesChecked,
        string? AiNote,
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> Problems,
        string Html,
        string PlainText)
    {
        /// <summary>Хоть одна цифра или список есть (иначе — нет связи, отчёт не показываем, а повторяем позже).</summary>
        public bool HasData => Tiles.Count > 0 || RunningOut.Count > 0 || Expiry.Count > 0 || Overdue.Count > 0 || Oddities.Count > 0;
    }

    /// <summary>Настройки отчёта: включён, время ЧЧ:ММ, слать в Telegram.</summary>
    public sealed class MorningSettings
    {
        public bool Enabled { get; set; } = true;
        public string Time { get; set; } = "09:00";
        // 2026-10-23: по умолчанию выключено (аудит Б-5): без токена бота ежедневная запись «Не отправлен» — шум
        public bool Telegram { get; set; } = false;
    }

    /// <summary>Запись журнала: когда, за какой день, куда (card / telegram), получилось ли, что случилось.</summary>
    public sealed record JournalEntry(DateTime At, DateTime ForDay, string Channel, bool Ok, string Message);

    public const string ChannelCard = "card";
    public const string ChannelTelegram = "telegram";

    /// <summary>Лимит одного сообщения отчёта в Telegram (у Telegram 4096, берём с запасом).</summary>
    public const int TelegramLimit = 3500;

    /// <summary>Отчёт готов (с UI-потока): плановый в 9:00, при первом запуске после 9:00 или по «Показать сейчас».</summary>
    public static event Action<Report>? ReportReady;

    /// <summary>Последний отчёт, собранный сегодня (в том числе до перезапуска программы); нет — null.</summary>
    public static Report? Latest
    {
        get
        {
            lock (StateLock)
                return State.LastReport is { } r && r.BuiltAt.Date == DateTime.Today ? r : null;
        }
    }

    /// <summary>Копия текущих настроек.</summary>
    public static MorningSettings Settings
    {
        get
        {
            lock (StateLock)
                return new MorningSettings { Enabled = State.Settings.Enabled, Time = State.Settings.Time, Telegram = State.Settings.Telegram };
        }
    }

    /// <summary>Журнал отправки, новые сверху.</summary>
    public static IReadOnlyList<JournalEntry> Journal
    {
        get
        {
            lock (StateLock)
                return State.Journal.OrderByDescending(j => j.At).ToList();
        }
    }

    /// <summary>Сохранить настройки. Время — «ЧЧ:ММ» (неверное — false, ничего не меняется).</summary>
    public static bool SaveSettings(bool enabled, string time, bool telegram)
    {
        if (!TryParseTime(time, out var t))
            return false;
        lock (StateLock)
        {
            State.Settings.Enabled = enabled;
            State.Settings.Time = $"{t.Hours:00}:{t.Minutes:00}";
            State.Settings.Telegram = telegram;
            SaveUnlocked();
        }
        PosLogger.Log($"Утренний отчёт: настройки — {(enabled ? "вкл" : "выкл")}, {t.Hours:00}:{t.Minutes:00}, Telegram {(telegram ? "да" : "нет")}.", "INFO");
        // Время сдвинули на уже прошедшее — проверить сразу, а не через минуту.
        _nextTryAt = DateTime.MinValue;
        if (_started)
            Dispatcher.UIThread.Post(() => _ = TickAsync());
        return true;
    }

    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var s = (text ?? "").Trim().Replace('.', ':').Replace(',', ':');
        if (!TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
            return false;
        time = t;
        return true;
    }

    /// <summary>Тихие часы 21:00–09:00: в Telegram не шлём.</summary>
    public static bool IsQuietHours(DateTime now) => now.Hour >= 21 || now.Hour < 9;

    // ── Планировщик ──

    private static bool _started;
    private static DispatcherTimer? _timer;
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private static DateTime _nextTryAt = DateTime.MinValue;
    private static DateTime _quietNotedDay = DateTime.MinValue;
    private static DateTime _noDataNotedDay = DateTime.MinValue;
    private static int _telegramAttempts;
    private static DateTime _telegramAttemptsDay = DateTime.MinValue;

    /// <summary>Запустить планировщик (повторный вызов ничего не делает). Вызывать с UI-потока при старте программы владельца.
    /// Первая проверка — через 90 с после запуска (чтобы не мешать загрузке «Сводки»), дальше — раз в минуту.</summary>
    public static void Start()
    {
        if (_started)
            return;
        _started = true;
        try
        {
            lock (StateLock)
                _ = State; // прочитать файл заранее
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _timer.Tick += (_, _) => _ = TickAsync();
            DispatcherTimer.RunOnce(() =>
            {
                _ = TickAsync();
                _timer.Start();
            }, TimeSpan.FromSeconds(90));
            PosLogger.Log("Утренний отчёт: планировщик запущен.", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Утренний отчёт: планировщик не запущен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private static async Task TickAsync()
    {
        try
        {
            var settings = Settings;
            // 2026-10-30: тумблер Нур Советника «Утренний отчёт» (Настройки → Операции → Нур Советник) — выключен: не собираем, ИИ и Telegram не трогаем.
            if (!settings.Enabled || !AdvisorCapabilities.IsOn("pro.morning") || !TryParseTime(settings.Time, out var at))
                return;
            var now = DateTime.Now;
            var today = now.Date;
            if (now < today + at || now < _nextTryAt)
                return;
            bool cardDone, telegramDone;
            lock (StateLock)
            {
                cardDone = State.CardDay == today && State.LastReport is { } last && last.BuiltAt.Date == today;
                telegramDone = State.TelegramDay == today || !State.Settings.Telegram;
            }
            if (cardDone && telegramDone)
                return;
            if (!await RunGate.WaitAsync(0).ConfigureAwait(true))
                return;
            try
            {
                var report = Latest;
                if (!cardDone || report is null)
                {
                    report = await BuildAsync(today.AddDays(-1), CancellationToken.None).ConfigureAwait(true);
                    if (!report.HasData)
                    {
                        _nextTryAt = DateTime.Now.AddMinutes(10);
                        // Повтор каждые 10 минут, а в журнал — один раз за день.
                        if (_noDataNotedDay == today)
                            return;
                        _noDataNotedDay = today;
                        AddJournal(today.AddDays(-1), ChannelCard, false, Tr.T(
                            "Отчёт не собран: сервер NurCRM не ответил — повтор через 10 минут.",
                            "Отчёт чогулган жок: NurCRM сервери жооп берген жок — 10 мүнөттөн кийин кайталанат.",
                            "The report wasn't built: the NurCRM server didn't respond — retrying in 10 minutes.",
                            "Rapor hazırlanamadı: NurCRM sunucusu yanıt vermedi — 10 dakika sonra tekrar denenecek.",
                            "Hisobot tuzilmadi: NurCRM serveri javob bermadi — 10 daqiqadan keyin qayta urinish."));
                        return;
                    }
                    Publish(report, manual: false);
                }
                await MaybeSendTelegramAsync(report).ConfigureAwait(true);
            }
            finally
            {
                RunGate.Release();
            }
        }
        catch (Exception ex)
        {
            _nextTryAt = DateTime.Now.AddMinutes(10);
            PosLogger.Log($"Утренний отчёт: ошибка планировщика ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    /// <summary>Кнопка «Показать отчёт сейчас»: свежий отчёт за вчера → <see cref="ReportReady"/>. В Telegram — только если время
    /// отчёта уже прошло, отчёт включён и сегодня ещё не отправлен (повторно не шлёт). Ничего не бросает; null — не собрался вовсе.</summary>
    public static async Task<Report?> RunNowAsync()
    {
        if (!AdvisorCapabilities.IsOn("pro.morning"))
            return null;
        try
        {
            await RunGate.WaitAsync().ConfigureAwait(true);
            try
            {
                var report = await BuildAsync(DateTime.Today.AddDays(-1), CancellationToken.None, forceStats: true).ConfigureAwait(true);
                if (!report.HasData)
                {
                    AddJournal(report.Day, ChannelCard, false, Tr.T(
                        "«Показать сейчас»: сервер NurCRM не ответил — проверьте интернет.",
                        "«Азыр көрсөтүү»: NurCRM сервери жооп берген жок — интернетти текшериңиз.",
                        "“Show now”: the NurCRM server didn't respond — check the internet.",
                        "«Şimdi göster»: NurCRM sunucusu yanıt vermedi — interneti kontrol edin.",
                        "«Hozir ko'rsatish»: NurCRM serveri javob bermadi — internetni tekshiring."));
                    return report;
                }
                Publish(report, manual: true);
                var settings = Settings;
                if (settings.Enabled && TryParseTime(settings.Time, out var at) && DateTime.Now >= DateTime.Today + at)
                    await MaybeSendTelegramAsync(report).ConfigureAwait(true);
                return report;
            }
            finally
            {
                RunGate.Release();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Утренний отчёт: «Показать сейчас» не выполнено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            return null;
        }
    }

    private static void Publish(Report report, bool manual)
    {
        var settings = Settings;
        var pastTime = settings.Enabled && TryParseTime(settings.Time, out var at) && DateTime.Now >= DateTime.Today + at;
        lock (StateLock)
        {
            State.LastReport = report;
            // Ручной показ до времени отчёта не отменяет планового: в 9:00 отчёт всё равно придёт.
            if (!manual || pastTime)
                State.CardDay = DateTime.Today;
            SaveUnlocked();
        }
        AddJournal(report.Day, ChannelCard, true, (manual
                ? Tr.T("Показан по кнопке", "Баскыч менен көрсөтүлдү", "Shown on request", "İstek üzerine gösterildi", "Tugma orqali ko'rsatildi")
                : Tr.T("Показан в окне Нур Советника", "Нур Кеңешчи терезесинде көрсөтүлдү", "Shown in the Nur Advisor window", "Nur Danışman penceresinde gösterildi", "Nur Maslahatchi oynasida ko'rsatildi"))
            + (report.Problems.Count > 0 ? " · " + string.Join("; ", report.Problems) : ""));
        try
        {
            ReportReady?.Invoke(report);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Утренний отчёт: подписчик карточки упал ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private static async Task MaybeSendTelegramAsync(Report report)
    {
        var today = DateTime.Today;
        lock (StateLock)
        {
            if (State.TelegramDay == today || !State.Settings.Telegram || !State.Settings.Enabled)
                return;
        }
        var now = DateTime.Now;
        if (IsQuietHours(now))
        {
            if (now.Hour >= 21)
            {
                MarkTelegramDone(today);
                AddJournal(report.Day, ChannelTelegram, false, Tr.T(
                    "Не отправлен: тихие часы 21:00–09:00 — отчёт только в окне.",
                    "Жөнөтүлгөн жок: тынч саат 21:00–09:00 — отчёт терезеде гана.",
                    "Not sent: quiet hours 21:00–09:00 — the report is in the window only.",
                    "Gönderilmedi: sessiz saatler 21:00–09:00 — rapor yalnızca pencerede.",
                    "Yuborilmadi: tinch soatlar 21:00–09:00 — hisobot faqat oynada."));
            }
            else if (_quietNotedDay != today)
            {
                _quietNotedDay = today;
                AddJournal(report.Day, ChannelTelegram, false, Tr.T(
                    "Отложен до 09:00 (тихие часы).", "09:00гө чейин калтырылды (тынч саат).", "Postponed until 09:00 (quiet hours).",
                    "09:00'a ertelendi (sessiz saatler).", "09:00 gacha qoldirildi (tinch soatlar)."));
            }
            return;
        }
        // 2026-10-23: нет токена бота — тихо пропускаем, без ежедневной записи «Не отправлен» (аудит Б-5);
        // подсказка про бота — в окне настроек отчёта
        if (!TelegramBotService.IsConfigured)
            return;
        if (!TelegramBotService.IsConfigured)
        {
            MarkTelegramDone(today);
            var serverMode = NurMarketKassa.Services.Api.ServerTelegramBotApi.LastKnownServerMode == true;
            AddJournal(report.Day, ChannelTelegram, false, serverMode
                ? Tr.T("Не отправлен: бот работает на сервере NurCRM, а на этом компьютере нет токена бота — переслать отчёт через сервер пока нельзя (у сервера нет такой команды).",
                    "Жөнөтүлгөн жок: бот NurCRM серверинде иштейт, бул компьютерде боттун токени жок — отчётту сервер аркылуу жөнөтүүгө азырынча болбойт.",
                    "Not sent: the bot runs on the NurCRM server and this computer has no bot token — the server can't forward the report yet.",
                    "Gönderilmedi: bot NurCRM sunucusunda çalışıyor, bu bilgisayarda bot jetonu yok — rapor sunucu üzerinden henüz iletilemiyor.",
                    "Yuborilmadi: bot NurCRM serverida ishlaydi, bu kompyuterda bot tokeni yo'q — hisobotni server orqali hozircha yuborib bo'lmaydi.")
                : Tr.T("Не отправлен: Telegram-бот не подключён (Настройки → Telegram-бот).",
                    "Жөнөтүлгөн жок: Telegram-бот туташкан эмес (Жөндөөлөр → Telegram-бот).",
                    "Not sent: the Telegram bot isn't connected (Settings → Telegram bot).",
                    "Gönderilmedi: Telegram botu bağlı değil (Ayarlar → Telegram botu).",
                    "Yuborilmadi: Telegram-bot ulanmagan (Sozlamalar → Telegram-bot)."));
            return;
        }
        if (_telegramAttemptsDay != today)
        {
            _telegramAttemptsDay = today;
            _telegramAttempts = 0;
        }
        _telegramAttempts++;
        string? error;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            error = await TelegramBotService.SendAsync(report.Html, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }
        if (error is null)
        {
            MarkTelegramDone(today);
            AddJournal(report.Day, ChannelTelegram, true, Tr.T("Отправлен владельцу в Telegram", "Ээсине Telegram'га жөнөтүлдү", "Sent to the owner in Telegram",
                "Sahibine Telegram'dan gönderildi", "Egasiga Telegram'da yuborildi"));
            return;
        }
        if (_telegramAttempts >= 3)
            MarkTelegramDone(today);
        else
            _nextTryAt = DateTime.Now.AddMinutes(10);
        AddJournal(report.Day, ChannelTelegram, false, error + (_telegramAttempts >= 3
            ? Tr.T(" — больше сегодня не пробуем.", " — бүгүн дагы аракет кылбайбыз.", " — no more attempts today.", " — bugün başka deneme yok.", " — bugun boshqa urinmaymiz.")
            : Tr.T(" — повтор через 10 минут.", " — 10 мүнөттөн кийин кайталанат.", " — retrying in 10 minutes.", " — 10 dakika sonra tekrar.", " — 10 daqiqadan keyin qayta.")));
    }

    private static void MarkTelegramDone(DateTime day)
    {
        lock (StateLock)
        {
            State.TelegramDay = day;
            SaveUnlocked();
        }
    }

    // ── Сборка отчёта ──

    private sealed record SalesFacts(int Checks, int Returned, double ReturnedSum, int Partial, double Discounts, int DiscountChecks, bool Truncated);

    private sealed record RemovalFacts(int Count, double Sum, int NoReason, int Clears);

    private sealed record CashDiff(string Cashier, DateTime ClosedAt, double Amount, bool Signed);

    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }

    /// <summary>Собрать отчёт за день (обычно вчера). Ничего не отправляет и не пишет в журнал. Не бросает: не ответивший источник
    /// попадает в Problems. Сравнения периодов есть только для вчерашнего дня (их считает OwnerPeriodStats от сегодняшней даты).
    /// Вызывать с UI-потока (точный расчёт долгов DebtsView запускается с него).</summary>
    public static Task<Report> BuildAsync(DateTime day, CancellationToken ct) => BuildAsync(day, ct, forceStats: false);

    private static async Task<Report> BuildAsync(DateTime day, CancellationToken ct, bool forceStats)
    {
        day = day.Date;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var problems = new List<string>();
        var isYesterday = day == DateTime.Today.AddDays(-1);
        try
        {
            var force = forceStats || OwnerPeriodStats.Cached is not { } cached || cached.BuiltAtLocal.Date != DateTime.Today;
            var statsTask = isYesterday
                ? Guard(T("Выручка и прибыль", "Түшүү жана пайда", "Revenue and profit", "Ciro ve kâr", "Tushum va foyda"), TimeSpan.FromSeconds(20),
                    c => OwnerPeriodStats.GetAsync(c, force), problems, ct)
                : Task.FromResult<OwnerPeriodStats.Snapshot?>(null);
            var debtsTask = Guard(T("Долги клиентов", "Кардарлардын карыздары", "Client debts", "Müşteri borçları", "Mijozlar qarzlari"), TimeSpan.FromSeconds(40),
                c => OwnerShopFacts.GetDebtsAsync(c, TimeSpan.FromSeconds(25)), problems, ct);
            var stockTask = Guard(T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"), TimeSpan.FromSeconds(45),
                c => OwnerShopFacts.StockRowsAsync(c), problems, ct);
            var expiryTask = Guard(T("Сроки годности", "Жарамдуулук мөөнөттөрү", "Expiry dates", "Son kullanma tarihleri", "Yaroqlilik muddatlari"), TimeSpan.FromSeconds(25),
                async c => new Box<IReadOnlyDictionary<string, ProductExpiryIndex.Mark>?>(await ProductExpiryIndex.RefreshAsync(false, c).ConfigureAwait(false) is var m && ProductExpiryIndex.IsLoaded ? m : null),
                problems, ct);
            var salesTask = Guard(T("Чеки (возвраты и скидки)", "Чектер (кайтаруулар жана арзандатуулар)", "Receipts (returns and discounts)", "Fişler (iadeler ve indirimler)", "Cheklar (qaytarishlar va chegirmalar)"),
                TimeSpan.FromSeconds(45), c => LoadSalesAsync(day, c), problems, ct);
            var removalsTask = Guard(T("Удалено из чека", "Чектен өчүрүлгөндөр", "Removed from receipt", "Fişten silinenler", "Chekdan o'chirilganlar"), TimeSpan.FromSeconds(10),
                c => Task.Run(() => LoadRemovals(day), c), problems, ct);
            var shiftsTask = Guard(T("Смены (разница в кассе)", "Сменалар (кассадагы айырма)", "Shifts (cash difference)", "Vardiyalar (kasa farkı)", "Smenalar (kassadagi farq)"),
                TimeSpan.FromSeconds(30), async c => new Box<IReadOnlyList<NurMarketKassa.Models.ShiftHistoryEntry>>(await ShiftHistoryService.LoadAsync(c).ConfigureAwait(false)), problems, ct);

            await Task.WhenAll(statsTask, debtsTask, stockTask, expiryTask, salesTask, removalsTask, shiftsTask).ConfigureAwait(true);
            var stats = statsTask.Result;
            var debts = debtsTask.Result;
            var stock = stockTask.Result;
            var expiry = expiryTask.Result?.Value;
            var sales = salesTask.Result;
            var removals = removalsTask.Result;
            var shifts = shiftsTask.Result?.Value;
            // GetAsync и RefreshAsync сами гасят ошибки сервера (null / прежние данные) — отметить, что цифр нет.
            if (isYesterday && stats is null && !problems.Any(p => p.StartsWith(T("Выручка и прибыль", "Түшүү жана пайда", "Revenue and profit", "Ciro ve kâr", "Tushum va foyda"), StringComparison.Ordinal)))
                problems.Add(T("Выручка и прибыль: сервер NurCRM не ответил", "Түшүү жана пайда: NurCRM сервери жооп берген жок", "Revenue and profit: the NurCRM server didn't respond",
                    "Ciro ve kâr: NurCRM sunucusu yanıt vermedi", "Tushum va foyda: NurCRM serveri javob bermadi"));
            if (expiry is null && !problems.Any(p => p.StartsWith(T("Сроки годности", "Жарамдуулук мөөнөттөрү", "Expiry dates", "Son kullanma tarihleri", "Yaroqlilik muddatlari"), StringComparison.Ordinal)))
                problems.Add(T("Сроки годности: сервер не ответил", "Жарамдуулук мөөнөттөрү: сервер жооп берген жок", "Expiry dates: the server didn't respond",
                    "Son kullanma tarihleri: sunucu yanıt vermedi", "Yaroqlilik muddatlari: server javob bermadi"));

            var sources = new List<string>();
            var tiles = new List<Tile>();
            var aiFacts = new StringBuilder();

            // ── Цифры: выручка, прибыль, неделя ──
            if (!isYesterday)
                problems.Add(T("Сравнения периодов есть только за вчера", "Мезгилдерди салыштыруу кечээ үчүн гана бар", "Period comparisons are only available for yesterday",
                    "Dönem karşılaştırmaları yalnızca dün için var", "Davrlarni solishtirish faqat kecha uchun bor"));
            if (stats is not null)
            {
                sources.Add(T("Сводка сервера NurCRM", "NurCRM серверинин жыйынтыгы", "NurCRM server summary", "NurCRM sunucu özeti", "NurCRM server xulosasi"));
                var y = stats.Get("yesterday");
                var vsDay = stats.Compare("yesterday_vs_day_before");
                var vsWeekDay = stats.Compare("yesterday_vs_last_week");
                var week = stats.Compare("week_vs_prev");
                if (y is not null)
                {
                    var deltas = new List<Delta>();
                    if (vsDay is not null)
                        deltas.Add(new Delta(vsDay.RevenueDeltaPercent, T("к позавчера", "мурдагы күнгө", "vs the day before", "önceki güne göre", "o'tgan kunga")));
                    if (vsWeekDay is not null)
                        deltas.Add(new Delta(vsWeekDay.RevenueDeltaPercent, T($"к {DayShort(vsWeekDay.Previous.From)} {vsWeekDay.Previous.From:dd.MM}", $"{DayShort(vsWeekDay.Previous.From)} {vsWeekDay.Previous.From:dd.MM} менен",
                            $"vs {DayShort(vsWeekDay.Previous.From)} {vsWeekDay.Previous.From:dd.MM}", $"{DayShort(vsWeekDay.Previous.From)} {vsWeekDay.Previous.From:dd.MM} gününe göre", $"{DayShort(vsWeekDay.Previous.From)} {vsWeekDay.Previous.From:dd.MM} ga")));
                    tiles.Add(new Tile("revenue", T("Выручка вчера", "Кечээки түшүү", "Revenue yesterday", "Dünkü ciro", "Kechagi tushum"), Money(y.Revenue), deltas,
                        T($"чеков {y.Checks:0} · средний чек {Money(y.AvgCheck)}", $"чек {y.Checks:0} · орточо чек {Money(y.AvgCheck)}", $"{y.Checks:0} receipts · average {Money(y.AvgCheck)}",
                            $"{y.Checks:0} fiş · ortalama {Money(y.AvgCheck)}", $"{y.Checks:0} chek · o'rtacha {Money(y.AvgCheck)}"), false, "overview"));
                    var profitDeltas = new List<Delta>();
                    if (vsDay is not null)
                        profitDeltas.Add(new Delta(vsDay.ProfitDeltaPercent, T("к позавчера", "мурдагы күнгө", "vs the day before", "önceki güne göre", "o'tgan kunga")));
                    tiles.Add(new Tile("profit", T("Валовая прибыль вчера", "Кечээки жалпы пайда", "Gross profit yesterday", "Dünkü brüt kâr", "Kechagi yalpi foyda"), Money(y.Profit), profitDeltas,
                        y.Margin != 0 ? T($"маржа {y.Margin:0.#} %", $"маржа {y.Margin:0.#} %", $"margin {y.Margin:0.#}%", $"marj %{y.Margin:0.#}", $"marja {y.Margin:0.#} %") : null,
                        y.Profit < 0, "overview"));
                    aiFacts.AppendLine($"Вчера выручка {Money(y.Revenue)}, чеков {y.Checks:0}, средний чек {Money(y.AvgCheck)}, валовая прибыль {Money(y.Profit)}"
                                       + (y.Margin != 0 ? $" (маржа {y.Margin:0.#} %)" : "") + ".");
                    if (vsDay is not null)
                        aiFacts.AppendLine($"К позавчера: выручка {OwnerPeriodStats.Pct(vsDay.RevenueDeltaPercent)}, прибыль {OwnerPeriodStats.Pct(vsDay.ProfitDeltaPercent)}.");
                    if (vsWeekDay is not null)
                        aiFacts.AppendLine($"К тому же дню прошлой недели: выручка {OwnerPeriodStats.Pct(vsWeekDay.RevenueDeltaPercent)}.");
                }
                if (week is not null)
                {
                    tiles.Add(new Tile("week", T($"Неделя {week.Current.From:dd.MM}–{week.Current.To:dd.MM}", $"Апта {week.Current.From:dd.MM}–{week.Current.To:dd.MM}", $"Week {week.Current.From:dd.MM}–{week.Current.To:dd.MM}",
                            $"Hafta {week.Current.From:dd.MM}–{week.Current.To:dd.MM}", $"Hafta {week.Current.From:dd.MM}–{week.Current.To:dd.MM}"),
                        Money(week.Current.Revenue),
                        new[] { new Delta(week.RevenueDeltaPercent, T("к прошлым 7 дням", "мурдагы 7 күнгө", "vs the previous 7 days", "önceki 7 güne göre", "oldingi 7 kunga")) },
                        T($"прибыль {Money(week.Current.Profit)}", $"пайда {Money(week.Current.Profit)}", $"profit {Money(week.Current.Profit)}", $"kâr {Money(week.Current.Profit)}", $"foyda {Money(week.Current.Profit)}"),
                        false, "overview"));
                    aiFacts.AppendLine($"7 полных дней: выручка {Money(week.Current.Revenue)} против {Money(week.Previous.Revenue)} — {OwnerPeriodStats.Pct(week.RevenueDeltaPercent)}; прибыль {Money(week.Current.Profit)}.");
                }
                if (stats.Missing.Count > 0)
                    problems.Add(T("сервер не ответил для части периодов", "сервер айрым мезгилдер үчүн жооп берген жок", "the server didn't answer for some periods",
                        "sunucu bazı dönemler için yanıt vermedi", "server ayrim davrlar uchun javob bermadi"));
            }

            // ── Долги ──
            var overdue = new List<Item>();
            if (debts is not null)
            {
                sources.Add(T("Долги клиентов", "Кардарлардын карыздары", "Client debts", "Müşteri borçları", "Mijozlar qarzlari"));
                var approx = debts.Exact ? "" : "≈ ";
                tiles.Add(new Tile("debts", T("Долги клиентов", "Кардарлардын карыздары", "Client debts", "Müşteri borçları", "Mijozlar qarzlari"), approx + Money(debts.Total),
                    Array.Empty<Delta>(),
                    debts.Overdue30 > 0.005
                        ? T($"просрочка > 30 дн.: {Money(debts.Overdue30)} ({debts.OverdueClients} кл.)", $"30 күндөн ашык: {Money(debts.Overdue30)} ({debts.OverdueClients} кард.)",
                            $"overdue > 30 d: {Money(debts.Overdue30)} ({debts.OverdueClients} clients)", $"30 günü geçen: {Money(debts.Overdue30)} ({debts.OverdueClients} müşteri)",
                            $"30 kundan oshgan: {Money(debts.Overdue30)} ({debts.OverdueClients} mijoz)")
                        : T($"должников {debts.Debtors.Count}, просрочки > 30 дн. нет", $"карыздарлар {debts.Debtors.Count}, 30 күндөн ашыгы жок", $"{debts.Debtors.Count} debtors, none overdue > 30 d",
                            $"{debts.Debtors.Count} borçlu, 30 günü geçen yok", $"{debts.Debtors.Count} qarzdor, 30 kundan oshgani yo'q"),
                    debts.Overdue30 > 0.005, "debts"));
                foreach (var d in debts.Debtors.Where(d => d.Overdue30 > 0.005).OrderByDescending(d => d.Overdue30).Take(3))
                    overdue.Add(new Item(T($"{d.Name} — {Money(d.Overdue30)} просрочено (долг с {d.Oldest:dd.MM})", $"{d.Name} — {Money(d.Overdue30)} мөөнөтү өткөн ({d.Oldest:dd.MM} баштап)",
                        $"{d.Name} — {Money(d.Overdue30)} overdue (since {d.Oldest:dd.MM})", $"{d.Name} — {Money(d.Overdue30)} gecikmiş ({d.Oldest:dd.MM} tarihinden beri)",
                        $"{d.Name} — {Money(d.Overdue30)} muddati o'tgan ({d.Oldest:dd.MM} dan)"), true, "debts"));
                // Для ИИ — без имён: только суммы и число клиентов.
                aiFacts.AppendLine($"Долги клиентов: всего {approx}{Money(debts.Total)} у {debts.Debtors.Count} клиентов; старше 30 дней {Money(debts.Overdue30)} у {debts.OverdueClients} клиентов.");
            }

            // ── Заканчивается ──
            var runningOut = new List<Item>();
            var runningOutTotal = 0;
            if (stock is not null)
            {
                sources.Add(T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"));
                var soldOut = OwnerShopFacts.SoldOut(stock, 100);
                var low = OwnerShopFacts.RunningLow(stock, 100);
                runningOutTotal = soldOut.Count + low.Count;
                foreach (var r in soldOut.Take(3))
                    runningOut.Add(new Item(T($"{r.Name} — закончился (за 30 дн. продано {r.Sold30:0.##})", $"{r.Name} — түгөндү (30 күндө {r.Sold30:0.##} сатылды)",
                        $"{r.Name} — sold out ({r.Sold30:0.##} sold in 30 d)", $"{r.Name} — tükendi (30 günde {r.Sold30:0.##} satıldı)", $"{r.Name} — tugadi (30 kunda {r.Sold30:0.##} sotildi)"), true, "restock"));
                foreach (var r in low.Take(5 - runningOut.Count))
                    runningOut.Add(new Item(T($"{r.Name} — осталось {r.Stock:0.###}, хватит на ~{Math.Max(1, Math.Round(r.DaysLeft)):0} дн.", $"{r.Name} — {r.Stock:0.###} калды, ~{Math.Max(1, Math.Round(r.DaysLeft)):0} күнгө жетет",
                        $"{r.Name} — {r.Stock:0.###} left, ~{Math.Max(1, Math.Round(r.DaysLeft)):0} d", $"{r.Name} — {r.Stock:0.###} kaldı, ~{Math.Max(1, Math.Round(r.DaysLeft)):0} gün yeter",
                        $"{r.Name} — {r.Stock:0.###} qoldi, ~{Math.Max(1, Math.Round(r.DaysLeft)):0} kunga yetadi"), false, "restock"));
                aiFacts.AppendLine($"Склад: закончились ходовые товары — {soldOut.Count}, запаса меньше чем на неделю — {low.Count}"
                                   + (runningOut.Count > 0 ? " (например: " + string.Join(", ", soldOut.Concat(low).Take(4).Select(r => r.Name)) + ")" : "") + ".");
            }

            // ── Сроки годности ──
            var expiryItems = new List<Item>();
            var expiryTotal = 0;
            if (expiry is not null)
            {
                sources.Add(T("Сроки годности (сервер)", "Жарамдуулук мөөнөттөрү (сервер)", "Expiry dates (server)", "Son kullanma tarihleri (sunucu)", "Yaroqlilik muddatlari (server)"));
                var marks = expiry.Values.OrderBy(m => m.Expired ? 0 : 1).ThenBy(m => m.DaysLeft).ToList();
                expiryTotal = marks.Count;
                foreach (var m in marks.Take(5))
                    expiryItems.Add(new Item($"{m.Name} — {ProductExpiryIndex.BadgeText(m)}" + (m.Quantity > 0
                        ? T($" (ост. {m.Quantity:0.###})", $" (калды {m.Quantity:0.###})", $" ({m.Quantity:0.###} left)", $" ({m.Quantity:0.###} kaldı)", $" ({m.Quantity:0.###} qoldi)") : ""),
                        m.Expired, "warehouse"));
                if (marks.Count > 0)
                    aiFacts.AppendLine($"Сроки годности: просрочено {marks.Count(m => m.Expired)} товаров, скоро истекает {marks.Count(m => !m.Expired)}.");
            }

            // ── Странности ──
            var oddities = new List<Item>();
            var odditiesChecked = false;
            if (removals is not null)
            {
                odditiesChecked = true;
                if (removals.Count > 0)
                {
                    sources.Add(T("Журнал «Удалено из чека» этого ПК", "Ушул ПКнын «Чектен өчүрүлгөндөр» журналы", "This PC's “Removed from receipt” log",
                        "Bu bilgisayarın «Fişten silinenler» kaydı", "Ushbu kompyuterning «Chekdan o'chirilganlar» jurnali"));
                    oddities.Add(new Item(T($"Удалено из чека до оплаты: {removals.Count} поз. на {Money(removals.Sum)}", $"Төлөмгө чейин чектен өчүрүлдү: {removals.Count} поз., {Money(removals.Sum)}",
                            $"Removed from receipts before payment: {removals.Count} items, {Money(removals.Sum)}", $"Ödemeden önce fişten silinen: {removals.Count} kalem, {Money(removals.Sum)}",
                            $"To'lovdan oldin chekdan o'chirildi: {removals.Count} ta, {Money(removals.Sum)}")
                        + (removals.NoReason > 0 ? T($", без причины {removals.NoReason}", $", себепсиз {removals.NoReason}", $", {removals.NoReason} without a reason", $", {removals.NoReason} nedensiz", $", {removals.NoReason} ta sababsiz") : "")
                        + (removals.Clears > 0 ? T($", очисток чека {removals.Clears}", $", чек тазалоо {removals.Clears}", $", {removals.Clears} receipt clears", $", {removals.Clears} fiş temizleme", $", {removals.Clears} ta chek tozalash") : ""),
                        removals.Sum >= 1000, "sales"));
                    aiFacts.AppendLine($"Удалено из чека до оплаты: {removals.Count} позиций на {Money(removals.Sum)}, без причины {removals.NoReason}, очисток чека {removals.Clears}.");
                }
            }
            if (sales is not null)
            {
                odditiesChecked = true;
                sources.Add(T("Чеки сервера NurCRM", "NurCRM серверинин чектери", "NurCRM server receipts", "NurCRM sunucu fişleri", "NurCRM server cheklari"));
                var approx = sales.Truncated ? "≈ " : "";
                if (sales.Returned > 0 || sales.Partial > 0)
                {
                    oddities.Add(new Item(T($"Возвраты и отмены: {approx}{sales.Returned} чек. на {Money(sales.ReturnedSum)}", $"Кайтаруулар жана жокко чыгаруулар: {approx}{sales.Returned} чек, {Money(sales.ReturnedSum)}",
                            $"Returns and cancellations: {approx}{sales.Returned} receipts, {Money(sales.ReturnedSum)}", $"İadeler ve iptaller: {approx}{sales.Returned} fiş, {Money(sales.ReturnedSum)}",
                            $"Qaytarish va bekor qilishlar: {approx}{sales.Returned} chek, {Money(sales.ReturnedSum)}")
                        + (sales.Partial > 0 ? T($", частичных возвратов {sales.Partial}", $", жарым-жартылай кайтаруу {sales.Partial}", $", {sales.Partial} partial returns", $", {sales.Partial} kısmi iade", $", {sales.Partial} ta qisman qaytarish") : ""),
                        false, "sales"));
                    aiFacts.AppendLine($"Возвраты и отмены чеков: {sales.Returned} на {Money(sales.ReturnedSum)}, частичных возвратов {sales.Partial} (всего чеков {sales.Checks}).");
                }
                if (sales.Discounts > 0.5)
                {
                    oddities.Add(new Item(T($"Скидки: {approx}{Money(sales.Discounts)} в {sales.DiscountChecks} чек.", $"Арзандатуулар: {approx}{Money(sales.Discounts)}, {sales.DiscountChecks} чекте",
                        $"Discounts: {approx}{Money(sales.Discounts)} in {sales.DiscountChecks} receipts", $"İndirimler: {approx}{Money(sales.Discounts)}, {sales.DiscountChecks} fişte",
                        $"Chegirmalar: {approx}{Money(sales.Discounts)}, {sales.DiscountChecks} ta chekda"), false, "sales"));
                    aiFacts.AppendLine($"Скидки: {Money(sales.Discounts)} в {sales.DiscountChecks} чеках из {sales.Checks}.");
                }
            }
            if (shifts is not null)
            {
                var closed = shifts.Where(s => s.ClosedAt is { } c && c.Date == day).ToList();
                if (closed.Count > 0)
                {
                    odditiesChecked = true;
                    sources.Add(T("Архив смен (Z-отчёты)", "Сменалар архиви (Z-отчёттор)", "Shift archive (Z reports)", "Vardiya arşivi (Z raporları)", "Smenalar arxivi (Z-hisobotlar)"));
                    var diffs = new List<CashDiff>();
                    foreach (var s in closed)
                    {
                        if (s.ClosingCash is { } fact && s.ExpectedCash is { } expected)
                        {
                            var diff = (double)(fact - expected);
                            if (Math.Abs(diff) >= 1)
                                diffs.Add(new CashDiff(s.Cashier, s.ClosedAt!.Value, diff, true));
                        }
                        else if (s.CashDiff is { } server && Math.Abs(server) >= 1)
                            diffs.Add(new CashDiff(s.Cashier, s.ClosedAt!.Value, (double)server, false));
                    }
                    foreach (var d in diffs.OrderByDescending(d => Math.Abs(d.Amount)).Take(3))
                    {
                        var text = !d.Signed
                            ? T($"Расхождение в кассе {Money(Math.Abs(d.Amount))}", $"Кассада айырма {Money(Math.Abs(d.Amount))}", $"Cash difference {Money(Math.Abs(d.Amount))}",
                                $"Kasa farkı {Money(Math.Abs(d.Amount))}", $"Kassada farq {Money(Math.Abs(d.Amount))}")
                            : d.Amount < 0
                                ? T($"Недостача в кассе {Money(-d.Amount)}", $"Кассада жетишпестик {Money(-d.Amount)}", $"Cash shortage {Money(-d.Amount)}", $"Kasa açığı {Money(-d.Amount)}", $"Kassada kamomad {Money(-d.Amount)}")
                                : T($"Излишек в кассе {Money(d.Amount)}", $"Кассада ашыкча {Money(d.Amount)}", $"Cash surplus {Money(d.Amount)}", $"Kasa fazlası {Money(d.Amount)}", $"Kassada ortiqcha {Money(d.Amount)}");
                        oddities.Add(new Item(text + T($" — смена {d.Cashier}, закрыта {d.ClosedAt:HH:mm}", $" — смена {d.Cashier}, {d.ClosedAt:HH:mm} жабылды", $" — shift of {d.Cashier}, closed {d.ClosedAt:HH:mm}",
                            $" — {d.Cashier} vardiyası, {d.ClosedAt:HH:mm} kapandı", $" — {d.Cashier} smenasi, {d.ClosedAt:HH:mm} da yopildi"), d.Signed && d.Amount < 0, "sales"));
                    }
                    if (diffs.Count > 0)
                        aiFacts.AppendLine($"Разница в кассе при закрытии смен: {diffs.Count} из {closed.Count} смен, недостача {Money(-diffs.Where(d => d.Signed && d.Amount < 0).Sum(d => d.Amount))}, "
                                           + $"излишек {Money(diffs.Where(d => d.Signed && d.Amount > 0).Sum(d => d.Amount))}.");
                }
            }

            var report = new Report(day, DateTime.Now, tiles, runningOut, runningOutTotal, expiryItems, expiryTotal, overdue, oddities, odditiesChecked,
                null, sources.Distinct().ToList(), problems.Distinct().ToList(), "", "");

            // ── Вывод ИИ (2–3 строки по обезличенным цифрам) ──
            string? note = null;
            if (report.HasData && TelegramAiChat.IsConfigured && aiFacts.Length > 0)
                note = await AskAiAsync(day, aiFacts.ToString(), ct).ConfigureAwait(true);

            report = report with { AiNote = note };
            report = report with { Html = Compose(report, html: true), PlainText = Compose(report, html: false) };
            PosLogger.Log($"Утренний отчёт за {day:dd.MM}: плиток {tiles.Count}, заканчивается {runningOutTotal}, сроков {expiryTotal}, странностей {oddities.Count}, "
                          + $"не получено {report.Problems.Count}, ИИ {(note is null ? "нет" : "да")}, {watch.ElapsedMilliseconds} мс.", "INFO");
            return report;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Утренний отчёт за {day:dd.MM} не собран ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            problems.Add(Describe(ex));
            var empty = new Report(day, DateTime.Now, Array.Empty<Tile>(), Array.Empty<Item>(), 0, Array.Empty<Item>(), 0, Array.Empty<Item>(), Array.Empty<Item>(), false,
                null, Array.Empty<string>(), problems.Distinct().ToList(), "", "");
            return empty with { Html = Compose(empty, true), PlainText = Compose(empty, false) };
        }
    }

    /// <summary>Источник с таймаутом: ошибка или «не ответил» — null, понятная причина в problems, подробности в журнал.</summary>
    private static async Task<TResult?> Guard<TResult>(string what, TimeSpan timeout, Func<CancellationToken, Task<TResult>> load, List<string> problems, CancellationToken ct)
        where TResult : class
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // Запуск — на потоке вызывающего (UI): точный расчёт долгов DebtsView стартует с него.
            return await load(cts.Token).WaitAsync(timeout + TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"Утренний отчёт: «{what}» пропущен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            lock (problems)
                problems.Add($"{what}: {Describe(ex)}");
            return null;
        }
    }

    /// <summary>Чеки за день с сервера (все кассы компании): возвраты/отмены (как «История чеков» — ReceiptHistoryService.IsReturnedOrCanceled),
    /// частичные возвраты, скидки (discount_total, как AnalyticsReportData — по оплаченным и частично возвращённым).</summary>
    private static async Task<SalesFacts> LoadSalesAsync(DateTime day, CancellationToken ct)
    {
        const int pageSize = 80;
        const int maxPages = 25;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int checks = 0, returned = 0, partial = 0, discountChecks = 0;
        double returnedSum = 0, discounts = 0;
        var truncated = false;
        for (var page = 1; ; page++)
        {
            if (page > maxPages)
            {
                truncated = true;
                PosLogger.Log($"Утренний отчёт: чеков за {day:dd.MM} больше {maxPages * pageSize} — возвраты и скидки посчитаны не по всем.", "WARNING");
                break;
            }
            var items = await App.SalesApi.PosSalesListAsync(page, pageSize, null, ct, dateFrom: day, dateToExclusive: day.AddDays(1)).ConfigureAwait(false);
            if (items.Count == 0)
                break;
            var added = 0;
            foreach (var row in items)
            {
                var id = Str(row, "id") ?? "";
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                added++;
                var status = (Str(row, "status") ?? "").ToLowerInvariant();
                if (status == "new")
                    continue;
                checks++;
                if (ReceiptHistoryService.IsReturnedOrCanceled(new ReceiptHistoryEntry { Id = id, Status = status }))
                {
                    returned++;
                    returnedSum += RowTotal(row);
                    continue;
                }
                if (status == "partially_returned")
                    partial++;
                if (status is "paid" or "partially_returned" or "debt")
                {
                    var discount = Num(row, "discount_total");
                    if (discount > 0.005)
                    {
                        discounts += discount;
                        discountChecks++;
                    }
                }
            }
            if (added == 0 || items.Count < pageSize)
                break;
        }
        return new SalesFacts(checks, returned, returnedSum, partial, discounts, discountChecks, truncated);
    }

    /// <summary>Журнал «Удалено из чека» этого ПК (CartRemovalLogStore — сервера для него в NurCRM нет): только своя компания и только
    /// этот день. Файла нет (программа владельца на ПК без кассы) — null: пункт просто не показывается.</summary>
    private static RemovalFacts? LoadRemovals(DateTime day)
    {
        if (!File.Exists(CartRemovalLogStore.FilePath))
            return null;
        var companyId = CompanyInfoService.LastCompany?.Id;
        var rows = CartRemovalLogStore.ReadAll()
            .Where(r => string.IsNullOrWhiteSpace(r.CompanyId)
                        || (!string.IsNullOrWhiteSpace(companyId) && string.Equals(r.CompanyId, companyId, StringComparison.OrdinalIgnoreCase)))
            .Where(r => r.At.LocalDateTime.Date == day)
            .ToList();
        return new RemovalFacts(rows.Count, rows.Sum(r => r.LineTotal), rows.Count(r => string.IsNullOrWhiteSpace(r.Reason) && string.IsNullOrWhiteSpace(r.Note)),
            rows.Where(r => r.Kind is CartRemovalLogStore.KindClear or CartRemovalLogStore.KindReceipt).Select(r => r.BatchId ?? r.At.ToString("O")).Distinct().Count());
    }

    private static async Task<string?> AskAiAsync(DateTime day, string facts, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            var language = T("русском", "кыргызском", "English", "Turkish", "Uzbek");
            // 2026-10-21: тон — спокойный и деловой, один совет (живьём ИИ писал «продажи катастрофически упали» про обычный тихий
            // день) (пакет 7, хвост Советника И3).
            var system = "Ты — Нур Советник, помощник владельца магазина. Тебе дают ГОТОВЫЕ цифры утреннего отчёта, их посчитала программа. "
                         + "Напиши вывод в 2–3 коротких строки: главное за вчера и ОДИН конкретный совет на сегодня. Цифры не пересчитывай и новых не придумывай. "
                         + "Тон — спокойный и деловой, как у опытного бухгалтера: без паники, драмы и оценочных слов (не «катастрофа», «обвал», «резко упали», "
                         + "«критично», «тревожно»), без восклицательных знаков и капса. Падение называй ровно: «выручка ниже позавчерашней на 35 %»; "
                         + "один тихий день — не повод для тревоги, если неделя в норме, так и скажи. "
                         + $"Без приветствия, без заголовков, таблиц и Markdown. Отвечай на языке: {language}.";
            var question = $"Утренний отчёт за {day:dd.MM.yyyy} ({DayShortRu(day)}):\n" + facts;
            var (answer, error) = await TelegramAiChat.AskOnceAsync(system, question, cts.Token).WaitAsync(TimeSpan.FromSeconds(27), ct).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(answer))
            {
                if (error is not null)
                    PosLogger.Log($"Утренний отчёт: вывод ИИ не получен ({error}).", "WARNING");
                return null;
            }
            var plain = TelegramAiChat.ToPlainText(WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(answer, "<[^>]+>", "")));
            var lines = plain.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(3).ToList();
            var text = string.Join("\n", lines);
            return text.Length > 450 ? text[..450].TrimEnd() + "…" : text;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"Утренний отчёт: вывод ИИ не получен ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    // ── Текст для Telegram (HTML) и для копирования ──

    /// <summary>Отчёт текстом. Длиннее лимита Telegram — списки короче (5 → 3 → 1 → 0 строк), в крайнем случае обрезка по строке.</summary>
    private static string Compose(Report r, bool html)
    {
        foreach (var max in new[] { 5, 3, 1, 0 })
        {
            var text = ComposeCore(r, html, max);
            if (!html || text.Length <= TelegramLimit)
                return text;
        }
        var cut = ComposeCore(r, html, 0);
        var at = cut.LastIndexOf('\n', Math.Min(cut.Length - 1, TelegramLimit - 2));
        return (at > 0 ? cut[..at] : cut[..(TelegramLimit - 2)]) + "\n…";
    }

    private static string ComposeCore(Report r, bool html, int maxItems)
    {
        string E(string s) => html ? WebUtility.HtmlEncode(s) : s;
        string B(string s) => html ? $"<b>{WebUtility.HtmlEncode(s)}</b>" : s;
        var sb = new StringBuilder();
        sb.AppendLine("☀️ " + B(Title(r)));
        sb.AppendLine(E(Subtitle(r)));
        if (r.Tiles.Count > 0)
        {
            sb.AppendLine();
            foreach (var t in r.Tiles)
            {
                var line = $"{E(t.Title)}: {B(t.Value)}";
                if (t.Deltas.Count > 0)
                    line += " — " + E(string.Join(" · ", t.Deltas.Select(DeltaText)));
                sb.AppendLine("• " + line);
                if (!string.IsNullOrWhiteSpace(t.Note))
                    sb.AppendLine("   " + E(t.Note!));
            }
        }
        void AddList(string title, int total, IReadOnlyList<Item> items)
        {
            if (items.Count == 0 || maxItems <= 0)
                return;
            sb.AppendLine();
            sb.AppendLine(B(title + (total > items.Count ? $" ({total})" : "")));
            foreach (var i in items.Take(maxItems))
                sb.AppendLine("• " + E(i.Text));
        }
        AddList(RunningOutTitle, r.RunningOutTotal, r.RunningOut);
        AddList(ExpiryTitle, r.ExpiryTotal, r.Expiry);
        AddList(OverdueTitle, r.Overdue.Count, r.Overdue);
        if (r.Oddities.Count > 0)
            AddList(OdditiesTitle, r.Oddities.Count, r.Oddities);
        else if (r.OdditiesChecked)
        {
            sb.AppendLine();
            sb.AppendLine(E(NoOdditiesText));
        }
        if (!string.IsNullOrWhiteSpace(r.AiNote))
        {
            sb.AppendLine();
            sb.AppendLine(html ? $"<i>✦ {WebUtility.HtmlEncode(r.AiNote!)}</i>" : "✦ " + r.AiNote);
        }
        if (r.Problems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(E(ProblemsPrefix + string.Join("; ", r.Problems)));
        }
        if (r.Sources.Count > 0)
            sb.AppendLine(html ? $"<i>{E(SourcesPrefix + string.Join(", ", r.Sources))}</i>" : SourcesPrefix + string.Join(", ", r.Sources));
        return sb.ToString().TrimEnd();
    }

    // ── Общие подписи (карточка и Telegram) ──

    public static string Title(Report r) => T($"Утренний отчёт · {r.BuiltAt:dd.MM}", $"Эртең мененки отчёт · {r.BuiltAt:dd.MM}", $"Morning report · {r.BuiltAt:dd.MM}",
        $"Sabah raporu · {r.BuiltAt:dd.MM}", $"Ertalabki hisobot · {r.BuiltAt:dd.MM}");

    public static string Subtitle(Report r) => T($"За {DayShort(r.Day)} {r.Day:dd.MM} · собран в {r.BuiltAt:HH:mm}", $"{DayShort(r.Day)} {r.Day:dd.MM} үчүн · {r.BuiltAt:HH:mm} чогулду",
        $"For {DayShort(r.Day)} {r.Day:dd.MM} · built at {r.BuiltAt:HH:mm}", $"{DayShort(r.Day)} {r.Day:dd.MM} için · {r.BuiltAt:HH:mm} hazırlandı",
        $"{DayShort(r.Day)} {r.Day:dd.MM} uchun · {r.BuiltAt:HH:mm} da tuzildi");

    public static string RunningOutTitle => T("Заканчивается", "Түгөнүп баратат", "Running out", "Tükeniyor", "Tugayapti");
    public static string ExpiryTitle => T("Сроки годности", "Жарамдуулук мөөнөттөрү", "Expiry dates", "Son kullanma tarihleri", "Yaroqlilik muddatlari");
    public static string OverdueTitle => T("Долги с просрочкой > 30 дней", "30 күндөн ашкан карыздар", "Debts overdue > 30 days", "30 günü geçen borçlar", "30 kundan oshgan qarzlar");
    public static string OdditiesTitle => T("Странности за день", "Күндүн өзгөчөлүктөрү", "Unusual things", "Olağandışı durumlar", "Kunning g'alati holatlari");
    public static string NoOdditiesText => T("Странностей не замечено (удаления, возвраты, скидки, касса).", "Өзгөчө эч нерсе байкалган жок (өчүрүүлөр, кайтаруулар, арзандатуулар, касса).",
        "Nothing unusual (removals, returns, discounts, cash).", "Olağandışı bir şey yok (silmeler, iadeler, indirimler, kasa).", "G'alati holat sezilmadi (o'chirishlar, qaytarishlar, chegirmalar, kassa).");
    public static string ProblemsPrefix => T("Не получено: ", "Алынган жок: ", "Not received: ", "Alınamadı: ", "Olinmadi: ");
    public static string SourcesPrefix => T("Откуда цифры: ", "Сандар кайдан: ", "Where the numbers come from: ", "Rakamların kaynağı: ", "Raqamlar qayerdan: ");

    /// <summary>«▲ +5 % к позавчера» / «без изменений к позавчера» / «к позавчера: сравнить не с чем».</summary>
    public static string DeltaText(Delta d) => d.Percent is not { } p
        ? d.Against + ": " + T("сравнить не с чем", "салыштырууга эч нерсе жок", "nothing to compare", "karşılaştıracak veri yok", "solishtirish uchun yo'q")
        : Math.Abs(p) < 0.5
            ? T("без изменений", "өзгөрүүсүз", "no change", "değişiklik yok", "o'zgarishsiz") + " " + d.Against
            : DeltaHead(p) + " " + d.Against;

    /// <summary>«▲ +5 %» / «▼ 12 %» (без «к чему»).</summary>
    public static string DeltaHead(double p)
    {
        var abs = Math.Abs(p);
        var n = abs >= 100 ? abs.ToString("0", Ru) : abs.ToString("0.#", Ru);
        return (p > 0 ? "▲ +" : "▼ ") + T($"{n} %", $"{n} %", $"{n}%", $"%{n}", $"{n} %");
    }

    // ── Мелочи ──

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string Money(double v) => Math.Round(v).ToString("N0", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    private static readonly string[] DaysRu = { "вс", "пн", "вт", "ср", "чт", "пт", "сб" };
    private static readonly string[] DaysKy = { "жк", "дш", "шш", "шр", "бш", "жм", "иш" };
    private static readonly string[] DaysEn = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] DaysTr = { "Paz", "Pzt", "Sal", "Çar", "Per", "Cum", "Cmt" };
    private static readonly string[] DaysUz = { "Ya", "Du", "Se", "Chor", "Pay", "Ju", "Sha" };

    public static string DayShort(DateTime d)
    {
        var i = (int)d.DayOfWeek;
        return T(DaysRu[i], DaysKy[i], DaysEn[i], DaysTr[i], DaysUz[i]);
    }

    private static string DayShortRu(DateTime d) => DaysRu[(int)d.DayOfWeek];

    /// <summary>Ошибка источника понятными словами (без технических подробностей — они в журнале).</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException or OperationCanceledException => T("не ответил вовремя", "убагында жооп берген жок", "didn't respond in time", "zamanında yanıt vermedi", "o'z vaqtida javob bermadi"),
        HttpRequestException => T("нет связи с сервером — проверьте интернет", "сервер менен байланыш жок — интернетти текшериңиз", "no connection to the server — check the internet",
            "sunucuya bağlantı yok — interneti kontrol edin", "server bilan aloqa yo'q — internetni tekshiring"),
        // 2026-10-31 (живой обход, В-8): владельцу показывалось сырое «ошибка (ApiException)» — теперь человеческая фраза, тип ошибки остаётся в журнале.
        _ => T("сервер NurCRM не ответил — нажмите «Повторить»", "NurCRM сервери жооп берген жок — «Кайталоо» баскычын басыңыз", "the NurCRM server didn't respond — press “Retry”",
            "NurCRM sunucusu yanıt vermedi — «Tekrar dene»ye basın", "NurCRM serveri javob bermadi — «Qayta urinish»ni bosing"),
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && JsonNumericReader.TryToDouble(v, out var d) ? d : 0;

    /// <summary>Сумма чека — те же поля, что «История чеков» (ReceiptHistoryService.RowTotal).</summary>
    private static double RowTotal(JsonElement row)
    {
        foreach (var key in new[] { "total", "grand_total", "total_amount", "amount", "total_sum", "final_total", "order_total" })
            if (row.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d))
                return d;
        return 0;
    }

    // ── Файл настроек и журнала: %APPDATA%\<NurMarketOwner>\advisor-morning.json ──

    private sealed class StateFile
    {
        public MorningSettings Settings { get; set; } = new();

        /// <summary>День (утро), когда плановый отчёт уже показан.</summary>
        public DateTime? CardDay { get; set; }

        /// <summary>День (утро), когда с Telegram закончили: отправлен, пропущен (тихие часы, нет бота) или 3 неудачи.</summary>
        public DateTime? TelegramDay { get; set; }

        public Report? LastReport { get; set; }

        public List<JournalEntry> Journal { get; set; } = new();
    }

    private static readonly object StateLock = new();
    private static StateFile? _state;

    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "advisor-morning.json");

    /// <summary>Состояние (читается один раз). Только под StateLock.</summary>
    private static StateFile State
    {
        get
        {
            if (_state is not null)
                return _state;
            try
            {
                if (File.Exists(StatePath))
                    _state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Утренний отчёт: файл настроек не прочитан ({ex.GetType().Name}) — настройки по умолчанию.", "WARNING");
            }
            _state ??= new StateFile();
            _state.Settings ??= new MorningSettings();
            _state.Journal ??= new List<JournalEntry>();
            return _state;
        }
    }

    private static void SaveUnlocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Утренний отчёт: файл настроек не записан ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private static void AddJournal(DateTime forDay, string channel, bool ok, string message)
    {
        lock (StateLock)
        {
            State.Journal.Add(new JournalEntry(DateTime.Now, forDay, channel, ok, message));
            if (State.Journal.Count > 60)
                State.Journal.RemoveRange(0, State.Journal.Count - 60);
            SaveUnlocked();
        }
        PosLogger.Log($"Утренний отчёт за {forDay:dd.MM} → {channel}: {(ok ? "да" : "нет")} — {message}", ok ? "INFO" : "WARNING");
    }
}
