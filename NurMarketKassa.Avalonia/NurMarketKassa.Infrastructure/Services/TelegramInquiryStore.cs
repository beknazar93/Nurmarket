using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-01, владелец: «фиксируй количество обращений клиентов к боту». Каждое сообщение
/// покупателя (не владельца) боту записывается: когда, от кого (имя в Telegram), что спросил и
/// оформлен ли заказ. Владелец спрашивает бота «сколько обращений было» — получает сводку.
///
/// Файл общий для кассы и программы владельца (%APPDATA%\NurMarketKassa\telegram-inquiries.json):
/// бота слушает то одна, то другая программа, а счёт должен быть один.
///
/// 2026-10-21 (бот, ВАЖНО 10 отчёта 20.10): хранилось 2000 записей — «за 7 дней: 2000» вместо ~9 500; файл читался и переписывался
/// целиком на КАЖДОЕ сообщение (8,2 мс), запись не атомарная, а при ошибке чтения список считался пустым и следующая запись стирала
/// историю. Теперь: записи за 35 дней (не больше 30 000), в памяти; на диск — не чаще раза в 300 мс через временный файл;
/// испорченный файл откладывается рядом (.broken), а не затирается; если записи всё же обрезаны — отчёт честно пишет «не меньше».
/// </summary>
public static class TelegramInquiryStore
{
    private const int MaxEntries = 30000;
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(35);
    private static readonly object Gate = new();
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static List<Entry>? _cache;
    private static DateTime _cacheFileTimeUtc;
    private static bool _dirty;
    private static bool _trimmed;
    private static DateTime _lastFlushUtc;

    public sealed record Entry(DateTime At, string ChatId, string? Name, string Text, string? OrderNumber = null);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "telegram-inquiries.json");

    /// <summary>Список в памяти (под Gate). Файл изменила другая программа, а у нас нет незаписанного — перечитываем.</summary>
    private static List<Entry> Cache()
    {
        var fileTime = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        if (_cache != null && (_dirty || fileTime == _cacheFileTimeUtc))
            return _cache;
        try
        {
            _cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new List<Entry>()
                : new List<Entry>();
        }
        catch (Exception ex)
        {
            // Испорченный файл не затираем молча — откладываем рядом, чтобы историю можно было восстановить.
            PosLogger.Log($"Обращения к боту: файл не прочитан ({ex.GetType().Name}) — отложен как .broken, начинаю новый.", "WARNING");
            try
            {
                File.Move(FilePath, FilePath + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", true);
            }
            catch (Exception)
            {
                // Не вышло — новый файл всё равно не затрёт старый до первой записи.
            }
            _cache = new List<Entry>();
        }
        _cacheFileTimeUtc = fileTime;
        return _cache;
    }

    public static void Record(string chatId, string? name, string text, string? orderNumber = null)
    {
        try
        {
            lock (Gate)
            {
                var list = Cache();
                list.Add(new Entry(DateTime.Now, chatId, name, text.Length > 300 ? text[..300] : text, orderNumber));
                Trim(list);
                _dirty = true;
            }
            if (DateTime.UtcNow - _lastFlushUtc > TimeSpan.FromSeconds(2))
                Flush();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обращения к боту: не записано ({ex.Message}).", "TELEGRAM");
        }
    }

    private static void Trim(List<Entry> list)
    {
        var old = DateTime.Now - KeepFor;
        var expired = list.FindIndex(e => e.At >= old);
        if (expired > 0)
            list.RemoveRange(0, expired);
        if (list.Count > MaxEntries)
        {
            list.RemoveRange(0, list.Count - MaxEntries);
            _trimmed = true;
        }
    }

    /// <summary>Записать на диск, если есть новое (вызывает опрос бота раз в 300 мс и при остановке).</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            if (!_dirty || _cache is null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_cache));
                File.Move(tmp, FilePath, true);
                _cacheFileTimeUtc = File.GetLastWriteTimeUtc(FilePath);
                _dirty = false;
                _lastFlushUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Обращения к боту: файл не записан ({ex.GetType().Name}).", "TELEGRAM");
            }
        }
    }

    /// <summary>Заказ оформлен — номер ставится последнему сообщению этого покупателя.</summary>
    public static void MarkLastOrder(string chatId, string orderNumber)
    {
        try
        {
            lock (Gate)
            {
                var list = Cache();
                var index = list.FindLastIndex(e => e.ChatId == chatId);
                if (index < 0)
                    list.Add(new Entry(DateTime.Now, chatId, null, "заказ", orderNumber));
                else
                    list[index] = list[index] with { OrderNumber = orderNumber };
                _dirty = true;
            }
            Flush();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обращения к боту: заказ не отмечен ({ex.Message}).", "TELEGRAM");
        }
    }

    public static List<Entry> Load()
    {
        lock (Gate)
        {
            try
            {
                return Cache().ToList();
            }
            catch
            {
                return new List<Entry>();
            }
        }
    }

    /// <summary>Записи обрезаны по числу (больше 30 000 за 35 дней) — счёт за неделю может быть меньше настоящего.</summary>
    private static bool Truncated(List<Entry> all) => _trimmed && all.Count > 0 && all[0].At.Date > DateTime.Today.AddDays(-7);

    /// <summary>Сводка для владельца: сегодня, 7 дней, заказы и последние вопросы.</summary>
    public static string BuildReport()
    {
        var all = Load();
        var today = all.Where(e => e.At.Date == DateTime.Today).ToList();
        var week = all.Where(e => e.At.Date > DateTime.Today.AddDays(-7)).ToList();
        var atLeast = Truncated(all) ? TelegramBotText.T("не меньше ", "кеминде ", "at least ", "en az ", "kamida ") : "";

        string Line(List<Entry> list, string prefix = "")
        {
            var orders = list.Count(e => e.OrderNumber != null);
            var people = list.Select(e => e.ChatId).Distinct().Count();
            return prefix + TelegramBotText.T($"{list.Count} сообщ. от {people} чел.", $"{people} кишиден {list.Count} билдирүү", $"{list.Count} messages from {people} people",
                       $"{people} kişiden {list.Count} mesaj", $"{people} kishidan {list.Count} xabar")
                   + (orders > 0 ? TelegramBotText.T($", заказов: {orders}", $", заказдар: {orders}", $", orders: {orders}", $", sipariş: {orders}", $", buyurtmalar: {orders}") : "");
        }

        var sb = new StringBuilder();
        sb.AppendLine(TelegramBotText.T("<b>Обращения покупателей к боту</b>", "<b>Сатып алуучулардын ботко кайрылуулары</b>", "<b>Customer messages to the bot</b>",
            "<b>Müşterilerin bota mesajları</b>", "<b>Xaridorlarning botga murojaatlari</b>"));
        sb.AppendLine();
        sb.AppendLine(TelegramBotText.T("Сегодня: ", "Бүгүн: ", "Today: ", "Bugün: ", "Bugun: ") + Line(today));
        sb.AppendLine(TelegramBotText.T("За 7 дней: ", "7 күндө: ", "Last 7 days: ", "Son 7 gün: ", "7 kunda: ") + Line(week, atLeast));

        var recent = all.OrderByDescending(e => e.At).Take(10).ToList();
        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(TelegramBotText.T("<b>Последние вопросы</b>", "<b>Акыркы суроолор</b>", "<b>Latest questions</b>", "<b>Son sorular</b>", "<b>So'nggi savollar</b>"));
            foreach (var e in recent)
            {
                var who = string.IsNullOrWhiteSpace(e.Name) ? TelegramBotText.T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor") : e.Name;
                var order = e.OrderNumber != null ? " — <b>" + TelegramBotText.T("заказ ", "заказ ", "order ", "sipariş ", "buyurtma ") + Escape(e.OrderNumber) + "</b>" : "";
                sb.AppendLine($"• {e.At.ToString("dd.MM HH:mm", Ru)} {Escape(who)}: {Escape(e.Text)}{order}");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(TelegramBotText.T("Покупатели пока не писали. Дайте им ссылку на бота — например, на чеке или в WhatsApp.",
                "Сатып алуучулар азырынча жазышкан жок. Аларга боттун шилтемесин бериңиз — мисалы, чекте же WhatsApp'та.",
                "Customers haven't written yet. Give them the bot link — for example on the receipt or in WhatsApp.",
                "Müşteriler henüz yazmadı. Onlara bot bağlantısını verin — örneğin fişte veya WhatsApp'ta.",
                "Xaridorlar hali yozishmagan. Ularga bot havolasini bering — masalan, chekda yoki WhatsApp'da."));
        }

        return sb.ToString();
    }

    /// <summary>Короткая строка для сводки ИИ владельца.</summary>
    public static string ShortSummary()
    {
        var all = Load();
        var today = all.Where(e => e.At.Date == DateTime.Today).ToList();
        var week = all.Where(e => e.At.Date > DateTime.Today.AddDays(-7)).ToList();
        return $"Обращения покупателей к боту: сегодня {today.Count} сообщ. от {today.Select(e => e.ChatId).Distinct().Count()} чел., "
               + $"за 7 дней {(Truncated(all) ? "не меньше " : "")}{week.Count} сообщ. от {week.Select(e => e.ChatId).Distinct().Count()} чел., "
               + $"заказов через бота за 7 дней: {week.Count(e => e.OrderNumber != null)}.";
    }

    // ── Данные покупателя для заказов ─────────────────────────────────────────────

    /// <summary>2026-10-01, владелец: «запомнить ФИО и номер телефона, не спрашивать при каждом заказе,
    /// просто попросить подтвердить». Имя и телефон из последнего оформленного заказа — по chat_id
    /// покупателя (%APPDATA%\NurMarketKassa\telegram-customers.json, общий для кассы и программы владельца).</summary>
    public sealed record CustomerProfile(string Name, string Phone, DateTime UpdatedAt);

    private static string ProfilesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "telegram-customers.json");

    public static CustomerProfile? GetProfile(string chatId)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(ProfilesPath))
                    return null;
                var all = JsonSerializer.Deserialize<Dictionary<string, CustomerProfile>>(File.ReadAllText(ProfilesPath));
                return all != null && all.TryGetValue(chatId, out var p) ? p : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static void SaveProfile(string chatId, string name, string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return;
        try
        {
            lock (Gate)
            {
                var all = File.Exists(ProfilesPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, CustomerProfile>>(File.ReadAllText(ProfilesPath)) ?? new()
                    : new Dictionary<string, CustomerProfile>();
                all[chatId] = new CustomerProfile(name.Trim(), phone.Trim(), DateTime.Now);
                Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
                // 2026-10-21: через временный файл — сбой при записи не портит профили.
                var tmp = ProfilesPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, ProfilesPath, true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Данные покупателя бота не сохранены ({ex.Message}).", "TELEGRAM");
        }
    }

    private static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
