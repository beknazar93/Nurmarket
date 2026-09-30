using System.Globalization;
using System.Linq;
using System.Text;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-30, владелец: «добавь небольшое бесплатное ИИ для общения — помощник боту».
///
/// Помощник понимает обычные фразы (без «/») по-русски и по-кыргызски: «сколько заработали
/// сегодня», «кто должен», «что заканчивается», «цена кола», «бүгүн канча түшүм» — и отвечает
/// теми же отчётами, что и команды. Работает целиком внутри программы: без интернета, без ключей
/// и без оплаты, данные магазина никуда не отправляются. Это не «большая» нейросеть — он
/// узнаёт смысл по ключевым словам и ищет товары в каталоге кассы; на непонятный вопрос честно
/// отвечает, что понял не всё, и подсказывает, как спросить.
/// </summary>
public static class TelegramAssistant
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Разбирает фразу. Command — имя существующей команды бота («segodnya», «dolgi»…),
    /// Reply — готовый ответ (поиск товара, приветствие, выручка за месяц, подсказка). Оба null —
    /// фраза не для бота (например, сообщение покупателя не про долг).
    /// Chat = true — это разговор (приветствие, спасибо, «почему…», непонятный вопрос): если
    /// подключён ИИ (TelegramAiChat), ответит он, иначе уходит готовый Reply.</summary>
    public static (string? Command, string? Reply, bool Chat) Understand(string text, bool isOwner)
    {
        var words = Normalize(text);
        if (words.Length == 0)
            return (null, null, false);

        // Покупатель (не владелец) может спросить только про свой долг.
        if (!isOwner)
            return Has(words, DebtWords) ? ("dolg", null, false) : (null, null, false);

        // «Почему упала выручка?», «как поднять продажи» — вопрос-рассуждение, а не отчёт.
        if (words.Length >= 3 && Has(words, ReasoningWords))
            return (null, FallbackText, true);

        var (command, reply) = UnderstandIntent(words);
        if (command != null || reply != null)
            return (command, reply, IsSmallTalk(words));
        return (null, FallbackText, true);
    }

    private static bool IsSmallTalk(string[] words) =>
        !Has(words, HelpWords) && (Has(words, ThanksWords) || Has(words, GreetingWords))
        && !Has(words, RevenueWords) && !Has(words, DebtWords);

    private static (string? Command, string? Reply) UnderstandIntent(string[] words)
    {
        // Вопрос о конкретном товаре: «цена кола», «сколько осталось сахара», «кола бар бы».
        if (Has(words, ProductQueryWords) && FindProducts(words) is { Count: > 0 } found)
            return (null, BuildProductReply(found));

        if (Has(words, DebtWords))
            return ("dolgi", null);
        if (Has(words, LowStockWords))
            return ("ostatki", null);
        if (Has(words, RestockWords))
            return ("zakaz", null);
        if (Has(words, AbcWords))
            return ("abc", null);
        if (Has(words, SeasonWords))
            return ("sezon", null);
        if (Has(words, TopWords))
            return ("top", null);
        if (Has(words, AdviceWords))
            return ("soveti", null);
        if (Has(words, RevenueWords) || Has(words, TodayWords) || Has(words, WeekWords) || Has(words, MonthWords))
        {
            if (Has(words, MonthWords))
                return (null, TelegramReportBuilder.BuildRevenue(30, "За 30 дней"));
            return Has(words, WeekWords) ? ("nedelya", null) : ("segodnya", null);
        }

        if (Has(words, HelpWords))
            return ("help", null);
        if (Has(words, ThanksWords))
            return (null, "Пожалуйста! Обращайтесь 🙂");
        if (Has(words, GreetingWords))
            return (null, "Здравствуйте! Я помощник магазина. Спросите обычными словами, например:\n"
                          + "• сколько заработали сегодня\n• кто должен\n• что заканчивается\n• цена кола");

        // Одно-два слова без вопроса — возможно, это просто название товара.
        if (words.Length <= 3 && FindProducts(words) is { Count: > 0 } byName)
            return (null, BuildProductReply(byName));

        return (null, null);
    }

    private const string FallbackText = "Я понял не всё 🙂 Спросите, например:\n"
        + "• сколько заработали сегодня / за неделю / за месяц\n"
        + "• кто должен\n• что заканчивается\n• что заказать\n• что лучше продаётся\n"
        + "• цена кола, остаток сахар\n\nИли /help — список команд.";

    /// <summary>2026-09-30: каталог для ИИ-консультанта покупателей — только то, что и так видно на
    /// полке и витрине: название, цена, «есть / нет в наличии» (точный остаток не раскрываем).
    /// Сначала товары из вопроса, затем общий список того, что есть в наличии.</summary>
    public static string CustomerCatalogContext(string question)
    {
        List<CatalogProductTileVm> all;
        try
        {
            all = CatalogCacheService.Products.Where(p => !string.IsNullOrWhiteSpace(p.Title)).ToList();
        }
        catch
        {
            return "(каталог недоступен)";
        }

        string Line(CatalogProductTileVm p) =>
            $"- {p.Title}: {LocalCartService.ParsePrice(p.PriceLine).ToString("N2", Ru)} сом, {(p.Quantity > 0 ? "есть в наличии" : "нет в наличии")}";

        var sb = new StringBuilder();
        var found = FindProducts(Normalize(question));
        if (found.Count > 0)
        {
            sb.AppendLine("Найдено по вопросу:");
            foreach (var p in found)
                sb.AppendLine(Line(p));
            sb.AppendLine();
        }

        sb.AppendLine("Есть в наличии:");
        foreach (var p in all.Where(p => p.Quantity > 0).OrderBy(p => p.Title).Take(120))
        {
            if (sb.Length > 5500)
                break;
            sb.AppendLine(Line(p));
        }

        return sb.ToString();
    }

    /// <summary>2026-09-30 (живой случай: Google перегружен, покупатель получал только «извините»):
    /// ответ покупателю без ИИ — по каталогу. Нашёлся товар из вопроса — цена и «есть / нет»;
    /// спросили «какие товары есть» — список того, что в наличии; иначе — телефон магазина.</summary>
    public static string CustomerFallback(string question, string storeName, string? phone)
    {
        var words = Normalize(question);
        string Line(CatalogProductTileVm p) =>
            $"• {Escape(p.Title)} — {LocalCartService.ParsePrice(p.PriceLine).ToString("N2", Ru)} сом, {(p.Quantity > 0 ? "есть в наличии" : "нет в наличии")}";

        var found = FindProducts(words);
        if (found.Count > 0)
            return string.Join("\n", found.Select(Line));

        if (Has(words, CatalogListWords))
        {
            List<CatalogProductTileVm> inStock;
            try
            {
                inStock = CatalogCacheService.Products.Where(p => p.Quantity > 0 && !string.IsNullOrWhiteSpace(p.Title))
                    .OrderBy(p => p.Title).Take(25).ToList();
            }
            catch
            {
                inStock = [];
            }

            if (inStock.Count > 0)
                return $"<b>Сейчас в наличии в «{Escape(storeName)}»</b> (часть списка):\n" + string.Join("\n", inStock.Select(Line))
                       + "\n\nСпросите о конкретном товаре — например «сколько стоит сахар».";
        }

        return "Такого товара я не нашёл 🙂 Напишите название точнее"
               + (string.IsNullOrWhiteSpace(phone) ? "." : $" или позвоните в магазин: {Escape(phone)}.");
    }

    private static readonly string[] CatalogListWords = { "какие", "каки", "ассортимент", "что есть", "что у вас", "список", "товары", "кандай", "эмнелер" };

    /// <summary>Товары из вопроса — строками для сводки, которую получает ИИ (цена и остаток), чтобы
    /// он отвечал про конкретный товар по настоящим данным, а не выдумывал. null — не нашлось.</summary>
    public static string? ProductContext(string text)
    {
        var found = FindProducts(Normalize(text));
        if (found.Count == 0)
            return null;
        var sb = new StringBuilder();
        foreach (var p in found)
        {
            var stock = p.Quantity.ToString("0.###", Ru) + (string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit);
            sb.AppendLine($"- {p.Title}: цена {LocalCartService.ParsePrice(p.PriceLine).ToString("N2", Ru)} сом, остаток {stock}");
        }
        return sb.ToString();
    }

    // Основы слов: слово фразы подходит, если начинается с одной из них.
    private static readonly string[] DebtWords = { "долг", "долж", "задолж", "насыя", "насия", "карыз", "карз" };
    private static readonly string[] LowStockWords = { "остатк", "заканчива", "закончи", "кончил", "кончает", "нехватк", "тугон", "түгөн", "калган" };
    private static readonly string[] RestockWords = { "заказ", "закуп", "докуп", "пополн", "привезти", "буйрутм", "заказат" };
    private static readonly string[] AbcWords = { "abc", "абс", "авс" };
    private static readonly string[] SeasonWords = { "сезон", "мезгил" };
    private static readonly string[] TopWords = { "топ", "лучш", "популяр", "ходов", "хит", "мыкты", "көп сат", "продаваем" };
    private static readonly string[] AdviceWords = { "совет", "рекоменд", "подскаж", "кеңеш", "кенеш", "сунуш" };
    private static readonly string[] RevenueWords = { "выручк", "заработ", "продаж", "продал", "доход", "оборот", "касс", "түшүм", "тушум", "сатуу", "сатты", "киреше", "акча", "денег", "деньг" };
    private static readonly string[] TodayWords = { "сегодн", "бүгүн", "бугун", "дела", "итог", "отчет", "сводк", "кандай иш" };
    private static readonly string[] WeekWords = { "недел", "жума", "7" };
    private static readonly string[] MonthWords = { "месяц", "30" };
    private static readonly string[] ReasoningWords = { "почему", "зачем", "как лучше", "как увелич", "как подня", "что делать", "посовету", "эмне үчүн", "эмнеге", "кантип" };
    private static readonly string[] HelpWords = { "помощ", "помоги", "умеешь", "команд", "справк", "жардам", "help" };
    private static readonly string[] ThanksWords = { "спасиб", "рахмат", "ракмат", "благодар", "чоң рахмат" };
    private static readonly string[] GreetingWords = { "привет", "здравств", "салам", "добрый", "доброе", "hello", "hi", "кандай" };
    private static readonly string[] ProductQueryWords = { "цен", "стоит", "почем", "баа", "остат", "осталос", "наличи", "есть", "бар", "канча", "сколько" };

    /// <summary>Слова, которые не относятся к названию товара, — их не ищем в каталоге.</summary>
    private static readonly string[] NotProductWords =
    {
        "цен", "стоит", "почем", "баа", "остат", "осталос", "наличи", "есть", "ли", "бар", "бы", "канча",
        "сколько", "какая", "какой", "какие", "у", "нас", "на", "в", "по", "а", "и", "за", "шт", "штук",
        "сом", "мне", "скажи", "покажи", "еще", "ещё", "складе", "склад", "товар", "товара", "бар бы", "или", "же", "вас", "у вас", "это",
    };

    private static string[] Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant().Replace('ё', 'е'))
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool Has(string[] words, string[] stems)
    {
        foreach (var stem in stems)
        {
            if (stem.Contains(' '))
            {
                if (string.Join(' ', words).Contains(stem, StringComparison.Ordinal))
                    return true;
                continue;
            }

            foreach (var word in words)
            {
                if (word.StartsWith(stem, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Товары, в названии которых есть все значимые слова фразы (по первым 4 буквам —
    /// так «сахара», «сахар», «сахару» находят один и тот же «Сахар»).</summary>
    private static List<CatalogProductTileVm> FindProducts(string[] words)
    {
        var keys = words
            .Where(w => w.Length >= 2 && !NotProductWords.Any(n => IsStopWord(w, n)))
            .Select(w => w.Length > 4 ? w[..4] : w)
            .Distinct()
            .ToList();
        if (keys.Count == 0)
            return [];

        List<CatalogProductTileVm> products;
        try
        {
            products = CatalogCacheService.Products.ToList();
        }
        catch
        {
            return [];
        }

        return products
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Where(p =>
            {
                var title = p.Title.ToLowerInvariant().Replace('ё', 'е');
                return keys.All(k => title.Contains(k, StringComparison.Ordinal));
            })
            .OrderBy(p => p.Title.Length)
            .Take(8)
            .ToList();
    }

    /// <summary>Короткие служебные слова («в», «у», «ли») — только целиком, иначе «вода» потерялась бы
    /// из-за «в»; длинные — по началу слова с окончанием («цены», «остатки»).</summary>
    private static bool IsStopWord(string word, string stop) =>
        word == stop || (stop.Length >= 3 && word.StartsWith(stop, StringComparison.Ordinal) && word.Length <= stop.Length + 3);

    private static string BuildProductReply(List<CatalogProductTileVm> products)
    {
        var sb = new StringBuilder();
        sb.AppendLine(products.Count == 1 ? "<b>Нашёл товар</b>" : $"<b>Нашёл товаров: {products.Count}</b>");
        sb.AppendLine();
        foreach (var p in products)
        {
            var price = LocalCartService.ParsePrice(p.PriceLine);
            var stock = p.Quantity.ToString("0.###", Ru) + (string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit);
            sb.AppendLine($"• <b>{Escape(p.Title)}</b>");
            sb.AppendLine($"  цена {price.ToString("N2", Ru)} сом · остаток {stock}");
        }

        return sb.ToString();
    }

    private static string Escape(string? text) => (text ?? "")
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
