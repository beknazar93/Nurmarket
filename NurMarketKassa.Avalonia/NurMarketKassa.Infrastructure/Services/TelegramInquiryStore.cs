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
/// бота слушает то одна, то другая программа, а счёт должен быть один. Хранятся последние
/// <see cref="MaxEntries"/> сообщений — этого хватает на сводку за неделю.
/// </summary>
public static class TelegramInquiryStore
{
    private const int MaxEntries = 2000;
    private static readonly object Gate = new();
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public sealed record Entry(DateTime At, string ChatId, string? Name, string Text, string? OrderNumber = null);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "telegram-inquiries.json");

    public static void Record(string chatId, string? name, string text, string? orderNumber = null)
    {
        try
        {
            lock (Gate)
            {
                var list = Load();
                list.Add(new Entry(DateTime.Now, chatId, name, text.Length > 300 ? text[..300] : text, orderNumber));
                if (list.Count > MaxEntries)
                    list.RemoveRange(0, list.Count - MaxEntries);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Обращения к боту: не записано ({ex.Message}).", "TELEGRAM");
        }
    }

    /// <summary>Заказ оформлен — номер ставится последнему сообщению этого покупателя.</summary>
    public static void MarkLastOrder(string chatId, string orderNumber)
    {
        try
        {
            lock (Gate)
            {
                var list = Load();
                var index = list.FindLastIndex(e => e.ChatId == chatId);
                if (index < 0)
                    list.Add(new Entry(DateTime.Now, chatId, null, "заказ", orderNumber));
                else
                    list[index] = list[index] with { OrderNumber = orderNumber };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
            }
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
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new List<Entry>()
                    : new List<Entry>();
            }
            catch
            {
                return new List<Entry>();
            }
        }
    }

    /// <summary>Сводка для владельца: сегодня, 7 дней, заказы и последние вопросы.</summary>
    public static string BuildReport()
    {
        var all = Load();
        var today = all.Where(e => e.At.Date == DateTime.Today).ToList();
        var week = all.Where(e => e.At.Date > DateTime.Today.AddDays(-7)).ToList();

        string Line(List<Entry> list) =>
            $"{list.Count} сообщ. от {list.Select(e => e.ChatId).Distinct().Count()} чел."
            + (list.Count(e => e.OrderNumber != null) is var orders and > 0 ? $", заказов: {orders}" : "");

        var sb = new StringBuilder();
        sb.AppendLine("<b>Обращения покупателей к боту</b>");
        sb.AppendLine();
        sb.AppendLine($"Сегодня: {Line(today)}");
        sb.AppendLine($"За 7 дней: {Line(week)}");

        var recent = all.OrderByDescending(e => e.At).Take(10).ToList();
        if (recent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<b>Последние вопросы</b>");
            foreach (var e in recent)
            {
                var who = string.IsNullOrWhiteSpace(e.Name) ? "Покупатель" : e.Name;
                var order = e.OrderNumber != null ? $" — <b>заказ {Escape(e.OrderNumber)}</b>" : "";
                sb.AppendLine($"• {e.At.ToString("dd.MM HH:mm", Ru)} {Escape(who)}: {Escape(e.Text)}{order}");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Покупатели пока не писали. Дайте им ссылку на бота — например, на чеке или в WhatsApp.");
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
               + $"за 7 дней {week.Count} сообщ. от {week.Select(e => e.ChatId).Distinct().Count()} чел., "
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
                File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(all));
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
