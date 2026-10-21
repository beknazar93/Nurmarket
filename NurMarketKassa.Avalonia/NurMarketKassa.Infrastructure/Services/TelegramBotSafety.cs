using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-21 (бот, ВАЖНО 2 отчёта 20.10): каталог для бота — копия, а не живой список. CatalogCacheService.Products меняется в потоке
/// экрана (Clear + Add при каждой синхронизации), а бот читал его из своего потока без блокировки: стресс-тест S14 — 14 ×
/// NullReferenceException, «сбой обработки» и пауза 30 с для всех. Копию снимаем сразу после обновления каталога (событие CacheUpdated
/// приходит в потоке экрана, когда список уже собран); до первого события — осторожное копирование с проверкой.
/// </summary>
internal static class TelegramCatalog
{
    private static CatalogProductTileVm[] _snapshot = Array.Empty<CatalogProductTileVm>();
    private static bool _fromEvent;

    static TelegramCatalog()
    {
        CatalogCacheService.CacheUpdated += OnCacheUpdated;
    }

    private static void OnCacheUpdated()
    {
        try
        {
            _snapshot = CatalogCacheService.Products.Where(p => p != null).ToArray();
            _fromEvent = true;
        }
        catch (Exception)
        {
            // Список меняют прямо сейчас — следующее событие снимет копию.
        }
    }

    public static IReadOnlyList<CatalogProductTileVm> Products
    {
        get
        {
            var snap = _snapshot;
            var live = CatalogCacheService.Products;
            if (_fromEvent || (snap.Length > 0 && snap.Length == live.Count))
                return snap;
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    var n = live.Count;
                    var copy = live.ToArray();
                    // Целая копия: длина не поменялась во время копирования и пустых мест нет. Пустой список вместо непустого не берём —
                    // это середина пересборки (Clear уже был, Add ещё нет).
                    if (copy.Length == n && live.Count == n && Array.TrueForAll(copy, p => p != null) && (copy.Length > 0 || snap.Length == 0))
                    {
                        _snapshot = copy;
                        return copy;
                    }
                }
                catch (Exception)
                {
                    // ArgumentException / InvalidOperationException при одновременной правке — пробуем ещё раз.
                }
                Thread.Yield();
            }
            return snap;
        }
    }
}

/// <summary>
/// 2026-10-21 (бот, ВАЖНО 5): антиспам заказов из бота. Стресс-тест S13: один чат за минуту оформил 20 настоящих заказов витрины и
/// прислал владельцу 20 уведомлений (ограничивал только лимит 20 вопросов ИИ в час). Теперь с одного чата — не чаще 1 заказа в 10 минут
/// и не больше 3 в сутки; со всех чатов вместе — не больше 200 в час (защита от наплыва с разных аккаунтов).
/// </summary>
internal static class TelegramOrderGate
{
    private static readonly TimeSpan PerChatGap = TimeSpan.FromMinutes(10);
    private const int PerChatPerDay = 3;
    private const int PerBotPerHour = 200;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<DateTime>> ByChat = new(StringComparer.Ordinal);
    private static readonly Queue<DateTime> All = new();

    /// <summary>null — заказ можно создать (и он засчитан); иначе — текст отказа покупателю.</summary>
    public static string? Check(string chatId)
    {
        var now = DateTime.UtcNow;
        var phone = UserPreferences.Instance.OwnerPhone;
        var call = string.IsNullOrWhiteSpace(phone) ? "" : TelegramBotText.T($" Телефон магазина: {phone}.", $" Дүкөндүн телефону: {phone}.",
            $" Shop phone: {phone}.", $" Mağaza telefonu: {phone}.", $" Do'kon telefoni: {phone}.");
        lock (Gate)
        {
            while (All.Count > 0 && now - All.Peek() > TimeSpan.FromHours(1))
                All.Dequeue();
            if (!ByChat.TryGetValue(chatId, out var list))
                ByChat[chatId] = list = new List<DateTime>();
            list.RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (list.Count > 0 && now - list[^1] < PerChatGap)
            {
                var wait = (int)Math.Ceiling((PerChatGap - (now - list[^1])).TotalMinutes);
                PosLogger.Log("Заказ из бота отклонён антиспамом: повтор с того же чата раньше 10 минут.", "TELEGRAM");
                return TelegramBotText.T($"Ваш заказ уже передан магазину — продавец свяжется с вами. Новый заказ можно оформить через {wait} мин.",
                    $"Заказыңыз дүкөнгө берилди — сатуучу сиз менен байланышат. Жаңы заказды {wait} мүнөттөн кийин бере аласыз.",
                    $"Your order has already been sent to the shop — the seller will contact you. You can place a new order in {wait} min.",
                    $"Siparişiniz mağazaya iletildi — satıcı sizinle iletişime geçecek. Yeni siparişi {wait} dk sonra verebilirsiniz.",
                    $"Buyurtmangiz do'konga yuborildi — sotuvchi siz bilan bog'lanadi. Yangi buyurtmani {wait} daqiqadan keyin berishingiz mumkin.") + call;
            }
            if (list.Count >= PerChatPerDay || All.Count >= PerBotPerHour)
            {
                PosLogger.Log($"Заказ из бота отклонён антиспамом ({(list.Count >= PerChatPerDay ? "3 заказа за сутки с чата" : "200 заказов за час на бота")}).", "TELEGRAM");
                return TelegramBotText.T("Сегодня через бота уже оформлено несколько заказов. Чтобы заказать ещё, позвоните в магазин.",
                    "Бүгүн бот аркылуу бир нече заказ берилди. Дагы заказ берүү үчүн дүкөнгө чалыңыз.",
                    "Several orders have already been placed through the bot today. To order more, please call the shop.",
                    "Bugün bot üzerinden birkaç sipariş verildi. Daha fazla sipariş için mağazayı arayın.",
                    "Bugun bot orqali bir nechta buyurtma berildi. Yana buyurtma berish uchun do'konga qo'ng'iroq qiling.") + call;
            }
            list.Add(now);
            All.Enqueue(now);
            return null;
        }
    }
}

/// <summary>
/// 2026-10-21 (бот, ВАЖНО 8): журнал напоминаний о долге «кому когда» (%APPDATA%\NurMarketKassa\telegram-reminders.json).
/// Стресс-тест S11: вторая рассылка подряд ушла тем же 950 людям — защиты от повтора не было. Теперь одному чату — не чаще раза в 20 часов.
/// </summary>
internal static class TelegramReminderLog
{
    internal static readonly TimeSpan MinGap = TimeSpan.FromHours(20);
    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _map;

    internal sealed record Entry(string? ClientId, DateTime AtUtc, double Amount);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "telegram-reminders.json");

    private static Dictionary<string, Entry> Map()
    {
        if (_map != null)
            return _map;
        try
        {
            _map = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Журнал напоминаний бота не прочитан ({ex.GetType().Name}).", "TELEGRAM");
        }
        return _map ??= new Dictionary<string, Entry>(StringComparer.Ordinal);
    }

    public static bool SentRecently(string chatId)
    {
        lock (Gate)
            return Map().TryGetValue(chatId, out var e) && DateTime.UtcNow - e.AtUtc < MinGap;
    }

    public static void MarkSent(string chatId, string? clientId, double amount)
    {
        lock (Gate)
            Map()[chatId] = new Entry(clientId, DateTime.UtcNow, amount);
    }

    public static void Forget(string chatId)
    {
        lock (Gate)
            Map().Remove(chatId);
    }

    public static void Flush()
    {
        lock (Gate)
        {
            try
            {
                var map = Map();
                foreach (var old in map.Where(kv => DateTime.UtcNow - kv.Value.AtUtc > TimeSpan.FromDays(90)).Select(kv => kv.Key).ToList())
                    map.Remove(old);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Журнал напоминаний бота не сохранён ({ex.GetType().Name}).", "WARNING");
            }
        }
    }
}
