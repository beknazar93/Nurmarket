using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.Hardware;

/// <summary>
/// 2026-10-05, владелец: «ИИ добавь и к голосовому управлению, которое есть у нас». Офлайн-распознавание (Vosk)
/// и разбор по словам (DefaultVoiceCommandParser) остаются главными — они быстрые и работают без интернета.
/// ИИ подключается только когда обычный разбор не справился: фраза не понята (Unknown) или товар не найден
/// (Vosk часто пишет марки по-своему: «кока кола» → «Coca-Cola», «спрайт ноль пять» → «Sprite 0,5»). Тогда ИИ
/// получает фразу и до 40 похожих товаров (только название, цена и остаток — без данных покупателей) и
/// возвращает команду. Вопрос вроде «касса сколько стоит пепси» — ответ текстом кассиру.
/// Нужен тот же ключ ИИ, что и для бота (UserPreferences.TelegramAiKey); без ключа или без связи — как раньше.
/// </summary>
public static class VoiceAi
{
    public static bool IsAvailable => TelegramAiChat.IsConfigured;

    /// <summary>Стоит ли спрашивать ИИ: обычный разбор ничего не дал.</summary>
    public static bool ShouldAsk(VoiceCommandResult result) =>
        result.Intent == VoiceIntent.Unknown
        || (result.Intent is VoiceIntent.AddProduct or VoiceIntent.FindProduct
            && result.Product is null
            && result.Candidates.Count == 0
            // «касса пачка» без товара — ответ на открытый диалог выбора упаковки, ИИ тут не нужен.
            && !(string.IsNullOrWhiteSpace(result.ProductQuery) && result.UnitKind != VoiceUnitKind.None));

    private const string System =
        "Ты помощник голосового управления кассы магазина в Кыргызстане. Кассир сказал фразу (распознана автоматически, "
        + "возможны ошибки распознавания, слова могут быть по-русски или по-кыргызски). Определи команду и ответь ОДНОЙ строкой JSON "
        + "без пояснений и без ```: {\"action\":\"...\",\"item\":N,\"qty\":Q,\"text\":\"...\"}. "
        + "action: add — добавить товар в чек; find — найти/показать товар; remove_last — убрать последнюю позицию; "
        + "clear — очистить чек; pay — оплата; answer — кассир спросил про товар (цена, остаток) — ответ в text коротко по-русски; "
        + "unknown — непонятно. item — номер товара из списка (1..N), если речь о товаре; если подходящего товара в списке нет — 0. "
        + "Учитывай похожее звучание и транслитерацию (кока кола = Coca-Cola, фанта = Fanta). qty — количество (по умолчанию 1; "
        + "«полкило» = 0.5, «два кг» = 2). Не выдумывай товары и цены — только из списка.";

    /// <summary>Разбор фразы ИИ. null — ИИ недоступен или ничего полезного не ответил (тогда остаётся обычный результат).</summary>
    public static async Task<VoiceCommandResult?> InterpretAsync(
        string commandText, string rawText, IReadOnlyList<CatalogProductTileVm> products, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(commandText))
            return null;

        var (productQuery, _, _) = VoiceCommandParser.ExtractQuantity(commandText);
        var shortlist = Shortlist(string.IsNullOrWhiteSpace(productQuery) ? commandText : productQuery, products, 40);
        var list = new StringBuilder();
        for (var i = 0; i < shortlist.Count; i++)
        {
            var p = shortlist[i];
            list.Append(i + 1).Append(". ").Append(p.Title);
            if (!string.IsNullOrWhiteSpace(p.PriceLine))
                list.Append(" — ").Append(p.PriceLine);
            if (!p.IsService)
                list.Append(", остаток ").Append(p.StockWithUnitText);
            if (p.MustWeigh)
                list.Append(", весовой");
            list.Append('\n');
        }
        var question = $"Фраза кассира: «{commandText}»\nТовары магазина (похожие по звучанию):\n"
            + (list.Length > 0 ? list.ToString() : "(нет похожих)\n");

        var started = DateTime.UtcNow;
        var (answer, error) = await TelegramAiChat.AskOnceAsync(System, question, ct).ConfigureAwait(false);
        var ms = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        if (answer is null)
        {
            PosLogger.Log($"Голосовое управление: ИИ не ответил за {ms} мс ({error}).", "VOICE");
            return null;
        }

        var parsed = Parse(answer);
        if (parsed is null)
        {
            PosLogger.Log($"Голосовое управление: ИИ ответил непонятно за {ms} мс.", "VOICE");
            return null;
        }
        var (action, item, qty, text) = parsed.Value;
        var product = item >= 1 && item <= shortlist.Count ? shortlist[item - 1] : null;
        PosLogger.Log($"Голосовое управление: ИИ за {ms} мс → {action}"
            + (product != null ? $", товар={product.Title} x{qty.ToString(CultureInfo.InvariantCulture)}" : "") + ".", "VOICE");

        return action switch
        {
            "add" when product != null => new VoiceCommandResult
            {
                Intent = VoiceIntent.AddProduct, Product = product, Candidates = new[] { product }, Quantity = qty > 0 ? qty : 1,
                ProductQuery = product.Title, RawText = rawText,
            },
            "find" when product != null => new VoiceCommandResult
            {
                Intent = VoiceIntent.FindProduct, Candidates = new[] { product }, ProductQuery = product.Title, RawText = rawText,
            },
            "remove_last" => new VoiceCommandResult { Intent = VoiceIntent.RemoveLastItem, RawText = rawText },
            "clear" => new VoiceCommandResult { Intent = VoiceIntent.ClearCart, RawText = rawText },
            "pay" => new VoiceCommandResult { Intent = VoiceIntent.Pay, RawText = rawText },
            "answer" when !string.IsNullOrWhiteSpace(text) => new VoiceCommandResult
            {
                Intent = VoiceIntent.AiAnswer, ProductQuery = text.Trim(), RawText = rawText,
                Candidates = product != null ? new[] { product } : Array.Empty<CatalogProductTileVm>(),
            },
            _ => null,
        };
    }

    private static (string Action, int Item, double Qty, string? Text)? Parse(string answer)
    {
        var m = Regex.Match(answer, @"\{.*\}", RegexOptions.Singleline);
        if (!m.Success)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()!.Trim().ToLowerInvariant() : "unknown";
            var item = root.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var iv) ? iv : 0;
            var qty = 1.0;
            if (root.TryGetProperty("qty", out var q))
            {
                if (q.ValueKind == JsonValueKind.Number)
                    qty = q.GetDouble();
                else if (q.ValueKind == JsonValueKind.String && double.TryParse(q.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var qs))
                    qty = qs;
            }
            if (qty is <= 0 or > 10000 || double.IsNaN(qty))
                qty = 1;
            var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return (action, item, qty, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>До max товаров, похожих на фразу по звучанию: сравнение буквенных троек после перевода кириллицы
    /// в латиницу (Vosk пишет «кока кола», в каталоге «Coca-Cola»). Каталог маленький — уходит целиком.</summary>
    internal static List<CatalogProductTileVm> Shortlist(string phrase, IReadOnlyList<CatalogProductTileVm> products, int max)
    {
        if (products.Count <= max)
            return products.ToList();
        var want = Trigrams(phrase);
        if (want.Count == 0)
            return new List<CatalogProductTileVm>();
        return products
            .Select(p => (Product: p, Score: Score(want, Trigrams(p.Title))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => x.Product)
            .ToList();
    }

    private static double Score(HashSet<string> want, HashSet<string> have)
    {
        if (have.Count == 0)
            return 0;
        var common = want.Count(have.Contains);
        return common == 0 ? 0 : common / (double)want.Count + common / (double)have.Count * 0.3;
    }

    private static HashSet<string> Trigrams(string text)
    {
        var latin = Latin(text.ToLowerInvariant());
        var set = new HashSet<string>();
        foreach (var word in Regex.Split(latin, "[^a-z0-9]+").Where(w => w.Length > 0))
        {
            var w = " " + word + " ";
            for (var i = 0; i + 3 <= w.Length; i++)
                set.Add(w.Substring(i, 3));
        }
        return set;
    }

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "j", ['з'] = "z",
        ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya", ['ө'] = "o", ['ү'] = "u", ['ң'] = "n",
    };

    private static string Latin(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (Translit.TryGetValue(ch, out var l))
                sb.Append(l);
            // Латинские c/q/x/w звучат как k/k/ks/v — сближаем с тем, что пишет русская модель распознавания.
            else if (ch == 'c')
                sb.Append('k');
            else if (ch == 'q')
                sb.Append('k');
            else if (ch == 'x')
                sb.Append("ks");
            else if (ch == 'w')
                sb.Append('v');
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }
}
