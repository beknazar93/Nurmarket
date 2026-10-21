using System.Collections.Concurrent;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-21: язык ответов бота и меню кнопками (бот, идеи 3 и 10 отчёта 20.10). Раньше все ответы были только по-русски, команды —
/// латиницей без меню («/segodnya» надо помнить). Теперь:
/// • покупателю — на языке его Telegram (language_code: ru, ky, en, tr, uz; прочие — язык программы магазина);
/// • владельцу — на языке программы (Настройки → Экран), как остальные надписи (Tr.T);
/// • меню кнопками (reply keyboard) владельцу и покупателю + список команд в Telegram (setMyCommands) на 5 языках.
/// Язык текущего ответа хранится в AsyncLocal: каждое сообщение обрабатывается своей задачей (очередь по чатам).
/// </summary>
public static class TelegramBotText
{
    private static readonly AsyncLocal<AppLanguage?> Current = new();
    private static readonly ConcurrentDictionary<string, AppLanguage> ChatLanguages = new(StringComparer.Ordinal);
    private static readonly object FileGate = new();
    private static bool _loaded;

    /// <summary>Язык текущего ответа: задан обработчиком сообщения или язык программы.</summary>
    public static AppLanguage Language => Current.Value ?? UserPreferences.Instance.Language;

    public static void Use(AppLanguage language) => Current.Value = language;

    /// <summary>Как Tr.T, но на языке собеседника бота.</summary>
    public static string T(string ru, string ky, string en, string tr, string uz) => Language switch
    {
        AppLanguage.Kyrgyz => ky,
        AppLanguage.English => en,
        AppLanguage.Turkish => tr,
        AppLanguage.Uzbek => uz,
        _ => ru,
    };

    /// <summary>language_code Telegram → язык ответа; неизвестный — null (тогда язык программы магазина).</summary>
    public static AppLanguage? FromCode(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        if (c.Length < 2)
            return null;
        return c[..2] switch
        {
            "ru" or "be" or "uk" or "kk" => AppLanguage.Russian,
            "ky" => AppLanguage.Kyrgyz,
            "en" => AppLanguage.English,
            "tr" or "az" => AppLanguage.Turkish,
            "uz" => AppLanguage.Uzbek,
            _ => null,
        };
    }

    /// <summary>Запомнить язык покупателя (для напоминаний о долге, которые бот шлёт сам).</summary>
    public static void Remember(string chatId, AppLanguage language)
    {
        EnsureLoaded();
        if (ChatLanguages.TryGetValue(chatId, out var old) && old == language)
            return;
        ChatLanguages[chatId] = language;
        Save();
    }

    /// <summary>Язык чата для сообщений, которые бот шлёт сам; не знаем — язык программы.</summary>
    public static AppLanguage ForChat(string chatId)
    {
        EnsureLoaded();
        return ChatLanguages.TryGetValue(chatId, out var l) ? l : UserPreferences.Instance.Language;
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "telegram-chat-lang.json");

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (FileGate)
        {
            if (_loaded)
                return;
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) is { } map)
                    foreach (var (chat, lang) in map)
                        ChatLanguages[chat] = (AppLanguage)lang;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: языки чатов не прочитаны ({ex.GetType().Name}).", "TELEGRAM");
            }
            _loaded = true;
        }
    }

    private static void Save()
    {
        try
        {
            lock (FileGate)
            {
                var map = ChatLanguages.Take(20000).ToDictionary(kv => kv.Key, kv => (int)kv.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map));
                File.Move(tmp, FilePath, true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: язык чата не сохранён ({ex.GetType().Name}).", "TELEGRAM");
        }
    }

    // ───────────────────────── меню кнопками ─────────────────────────

    internal sealed record MenuButton(string Command, string Ru, string Ky, string En, string Tr, string Uz)
    {
        public string Text => T(Ru, Ky, En, Tr, Uz);

        public bool Matches(string text) =>
            new[] { Ru, Ky, En, Tr, Uz }.Any(l => string.Equals(Clean(l), text, StringComparison.OrdinalIgnoreCase));
    }

    internal static readonly MenuButton[] OwnerButtons =
    {
        new("segodnya", "📊 Сегодня", "📊 Бүгүн", "📊 Today", "📊 Bugün", "📊 Bugun"),
        new("nedelya", "📅 Неделя", "📅 Жума", "📅 Week", "📅 Hafta", "📅 Hafta"),
        new("dolgi", "💸 Долги", "💸 Карыздар", "💸 Debts", "💸 Borçlar", "💸 Qarzlar"),
        new("ostatki", "📦 Кончается", "📦 Түгөнүп жатат", "📦 Running low", "📦 Azalanlar", "📦 Tugayapti"),
        new("zakaz", "🛒 Что заказать", "🛒 Эмнени заказ кылуу", "🛒 What to reorder", "🛒 Ne sipariş edilmeli", "🛒 Nimani buyurtma qilish"),
        new("top", "🏆 Топ товаров", "🏆 Эң көп сатылган", "🏆 Top products", "🏆 En çok satanlar", "🏆 Eng ko'p sotilgan"),
        new("mesyac", "🗓 Месяц", "🗓 Ай", "🗓 Month", "🗓 Ay", "🗓 Oy"),
        new("help", "❓ Помощь", "❓ Жардам", "❓ Help", "❓ Yardım", "❓ Yordam"),
    };

    internal static readonly MenuButton[] CustomerButtons =
    {
        new("catalog", "🛍 Каталог", "🛍 Каталог", "🛍 Catalog", "🛍 Katalog", "🛍 Katalog"),
        new("dolg", "💳 Мой долг", "💳 Менин карызым", "💳 My debt", "💳 Borcum", "💳 Mening qarzim"),
        new("call", "📞 Позвонить", "📞 Чалуу", "📞 Call the shop", "📞 Mağazayı ara", "📞 Qo'ng'iroq qilish"),
        new("address", "📍 Адрес", "📍 Дарек", "📍 Address", "📍 Adres", "📍 Manzil"),
    };

    private static string Clean(string s) => s.Trim();

    /// <summary>Нажатая кнопка меню (текст кнопки на любом из 5 языков) → команда; иначе null.</summary>
    public static string? CommandForButton(string? text, bool owner)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 40)
            return null;
        return (owner ? OwnerButtons.Concat(CustomerButtons) : CustomerButtons).FirstOrDefault(b => b.Matches(t))?.Command;
    }

    private static object Keyboard(IEnumerable<MenuButton> buttons) => new Dictionary<string, object?>
    {
        ["keyboard"] = buttons.Chunk(2).Select(row => row.Select(b => new Dictionary<string, string> { ["text"] = b.Text }).ToArray()).ToArray(),
        ["resize_keyboard"] = true,
        ["is_persistent"] = true,
    };

    /// <summary>Меню владельца: отчёты одним нажатием.</summary>
    public static object OwnerKeyboard() => Keyboard(OwnerButtons);

    /// <summary>Меню покупателя: каталог, мой долг, позвонить, адрес.</summary>
    public static object CustomerKeyboard() => Keyboard(CustomerButtons);

    /// <summary>Кнопка «Подтвердить номер» (Telegram сам пришлёт номер этого человека — подделать нельзя).</summary>
    public static object ContactKeyboard() => new Dictionary<string, object?>
    {
        ["keyboard"] = new[]
        {
            new object[]
            {
                new Dictionary<string, object> { ["text"] = T("📱 Подтвердить номер", "📱 Номерди ырастоо", "📱 Confirm my number", "📱 Numaramı doğrula", "📱 Raqamni tasdiqlash"), ["request_contact"] = true },
            },
        },
        ["resize_keyboard"] = true,
        ["one_time_keyboard"] = true,
    };

    /// <summary>Английские и короткие синонимы команд → команда бота (латиница: Telegram не даёт команды кириллицей).</summary>
    public static string Alias(string command) => command switch
    {
        "today" or "bugun" => "segodnya",
        "week" or "hafta" => "nedelya",
        "month" or "mesyats" or "ay" or "oy" => "mesyac",
        "debts" => "dolgi",
        "debt" or "mydebt" => "dolg",
        "stock" or "low" => "ostatki",
        "restock" => "zakaz",
        "advice" => "soveti",
        "season" => "sezon",
        "katalog" or "products" => "catalog",
        "phone" => "call",
        "adres" => "address",
        "меню" => "menu",
        _ => command,
    };

    // ───────────────────────── список команд в Telegram ─────────────────────────

    private static readonly (string Command, string Ru, string Ky, string En, string Tr, string Uz)[] OwnerCommands =
    {
        ("segodnya", "Выручка за сегодня", "Бүгүнкү түшүм", "Today's revenue", "Bugünkü ciro", "Bugungi tushum"),
        ("nedelya", "Выручка за 7 дней", "7 күндүк түшүм", "Revenue for 7 days", "7 günlük ciro", "7 kunlik tushum"),
        ("mesyac", "Выручка с начала месяца", "Айдын башынан бери түшүм", "Revenue month to date", "Ay başından beri ciro", "Oy boshidan beri tushum"),
        ("dolgi", "Должники и напоминания", "Карызкорлор жана эскертмелер", "Debtors and reminders", "Borçlular ve hatırlatmalar", "Qarzdorlar va eslatmalar"),
        ("ostatki", "Что заканчивается", "Эмне түгөнүп жатат", "Running low", "Azalan ürünler", "Nima tugayapti"),
        ("zakaz", "Что пора заказать", "Эмнени заказ кылуу керек", "What to reorder", "Ne sipariş edilmeli", "Nimani buyurtma qilish kerak"),
        ("top", "Топ товаров", "Эң көп сатылгандар", "Top products", "En çok satanlar", "Eng ko'p sotilganlar"),
        ("abc", "ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC tahlil"),
        ("sezon", "Сезонность", "Мезгилдүүлүк", "Seasonality", "Mevsimsellik", "Mavsumiylik"),
        ("soveti", "Советы по магазину", "Дүкөн боюнча кеңештер", "Shop advice", "Mağaza önerileri", "Do'kon bo'yicha maslahatlar"),
        ("menu", "Меню кнопками", "Баскычтар менюсу", "Button menu", "Düğme menüsü", "Tugmalar menyusi"),
        ("help", "Что умеет бот", "Бот эмне кыла алат", "What the bot can do", "Bot neler yapabilir", "Bot nimalar qila oladi"),
    };

    private static readonly (string Command, string Ru, string Ky, string En, string Tr, string Uz)[] CustomerCommands =
    {
        ("start", "Начать", "Баштоо", "Start", "Başla", "Boshlash"),
        ("catalog", "Каталог товаров", "Товарлар каталогу", "Product catalog", "Ürün kataloğu", "Mahsulotlar katalogi"),
        ("dolg", "Мой долг", "Менин карызым", "My debt", "Borcum", "Mening qarzim"),
        ("menu", "Меню кнопками", "Баскычтар менюсу", "Button menu", "Düğme menüsü", "Tugmalar menyusi"),
        ("help", "Что умеет бот", "Бот эмне кыла алат", "What the bot can do", "Bot neler yapabilir", "Bot nimalar qila oladi"),
    };

    /// <summary>Списки команд для setMyCommands: (код языка или null — по умолчанию, команды).</summary>
    internal static IEnumerable<(string? LanguageCode, object Commands)> CommandSets(bool owner)
    {
        var source = owner ? OwnerCommands : CustomerCommands;
        foreach (var (code, pick) in new (string?, Func<(string, string, string, string, string, string), string>)[]
                 {
                     (null, c => c.Item2), ("ky", c => c.Item3), ("en", c => c.Item4), ("tr", c => c.Item5), ("uz", c => c.Item6),
                 })
            yield return (code, source.Select(c => new Dictionary<string, string> { ["command"] = c.Command, ["description"] = pick(c) }).ToArray());
    }
}
