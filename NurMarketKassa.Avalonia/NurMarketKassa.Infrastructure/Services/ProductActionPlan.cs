using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, владелец: «к ИИ и боту дай полный доступ к товарам — количество добавить, уменьшить, следить за сроками
/// годности, количеством и, если надо, как с фото полностью наполнить информацией товары, описание».
///
/// ИИ (советник программы владельца и бот) ничего не меняет сам: он пишет строки «ТОВАР: {…}» (как «БОТ: {…}» для настроек бота),
/// программа показывает владельцу, что будет сделано, и выполняет только по «Выполнить» (в боте — кнопка или ответ «да»).
/// Выполнение — ProductActions: остаток — документом ревизии с перечитанным остатком сервера, поля — PATCH товара, фото — поиск
/// в интернете (ProductPhotoFinder, задаётся приложением).</summary>
public static class ProductActionPlan
{
    /// <param name="Purchase">Закупочная цена (приход по накладной, новый товар).</param>
    /// <param name="Price">Цена продажи (с наценкой).</param>
    public sealed record Step(string Op, CatalogProductTileVm Product, double? Qty, string? Text, string? Reason,
        double? Purchase = null, double? Price = null, string? Barcode = null, string? Unit = null, string? Category = null,
        bool RaisedToMin = false, double? ExpectedStock = null);

    /// <summary>2026-10-06, владелец: «при загрузке товаров и создании товаров из накладной надо сразу ставить маржу минимум 20%».
    /// Минимальная наценка прихода и нового товара по накладной (UserPreferences.AiMinMarkupPercent, по умолчанию 20).</summary>
    public static double MinMarkupPercent => UserPreferences.Instance.AiMinMarkupPercent > 0 ? UserPreferences.Instance.AiMinMarkupPercent : 20;

    /// <summary>Цена с минимальной наценкой — округление ВВЕРХ (до сома, дороже 1000 — до 10 сом), чтобы наценка не стала ниже минимума.</summary>
    public static double MinMarkupPrice(double purchase)
    {
        var v = purchase * (1 + MinMarkupPercent / 100);
        return v >= 1000 ? Math.Ceiling(v / 10 - 1e-9) * 10 : Math.Ceiling(v - 1e-9);
    }

    /// <summary>Цена прихода/нового товара не ниже закупки + минимальная наценка. price = null — цену не меняем (если текущая
    /// <paramref name="current"/> уже не ниже минимума). Raised = цену подняли до минимума.</summary>
    private static (double? Price, bool Raised) EnsureMinMarkup(double? purchase, double? price, double current)
    {
        if (purchase is not { } p || p <= 0)
            return (price, false);
        var min = MinMarkupPrice(p);
        var effective = price ?? current;
        // «Поднято» — только если была цена ниже минимума (названная или текущая); новый товар без цены — просто минимум.
        return effective + 1e-6 >= min ? (price, false) : (min, price.HasValue || current > 0);
    }

    /// <summary>Найти и поставить фото товару (приложение: ProductPhotoFinder). Ответ — что получилось.</summary>
    public static Func<CatalogProductTileVm, CancellationToken, Task<ProductActions.Result>>? PhotoFinder { get; set; }

    /// <summary>2026-10-06, владелец: «открывать товар на складе голосом». Открыть товар на складе программы владельца
    /// (задаёт окно программы владельца). Нет — операция «open» недоступна (бот).</summary>
    public static Action<CatalogProductTileVm>? OpenProduct { get; set; }

    /// <summary>2026-10-06, владелец: «дай нашему ИИ звонку полный доступ к программе, чтобы он мог открывать разные вкладки и
    /// разделы». Открыть раздел меню программы владельца по словам («открой зарплату», «перейди в финансы») — сразу, без
    /// нейросети. Ответ — название открытого раздела; null — раздел не узнан (или это не программа владельца).</summary>
    public static Func<string, bool, string?>? NavigateByWords { get; set; }

    /// <summary>2026-10-06, владелец: «сделай так, чтобы наш ИИ напрямую открывал программу и показывал наглядно изменения».
    /// Открыть раздел программы владельца по ключу (warehouse, sales, finance, salary…).</summary>
    public static Action<string>? OpenSection { get; set; }

    /// <summary>Показать на складе изменённые товары (id) с пометкой — после «Выполнить».</summary>
    public static Action<IReadOnlyList<string>, string>? ShowChangedProducts { get; set; }

    /// <summary>Правила для фото накладной (дописываются к инструкции, когда владелец приложил фото).</summary>
    public static string PhotoPromptText =>
        "ФОТО К ВОПРОСУ: если на фото накладная, распознай ВСЕ строки до конца документа — не ограничивай число товаров. "
        + "Видимая часть ответа должна содержать только Markdown-таблицу, без вступления, с заголовками строго в этом порядке: "
        + "| № | Наименование товара | Штрихкод / Артикул | Ед. изм. | Кол-во | Цена закупа | Розничная цена | Сумма (закуп) | "
        + "|---|---|---|---|---:|---:|---:|---:|. Не добавляй заголовок или пояснение перед таблицей. "
        + "Каждую позицию накладной вынеси в отдельную строку и не пропускай её из-за неразборчивого поля. "
        + "Если реквизит отсутствует или не читается — поставь в его ячейке ровно -. Не угадывай значения. "
        + "Сумма строки — количество × закупочная цена за единицу; если в документе указана только сумма строки, "
        + "вычисли цену за единицу, когда это возможно, иначе ставь - в невычислимых полях. "
        + "Сразу под таблицей выведи блок итогов строго из трёх строк: «Всего наименований: N», «Общее количество единиц: Q», "
        + "«Общая сумма закупа: X сом». N — число товарных строк, Q — сумма количества по всем строкам (сохраняй обозначения единиц, если единицы смешаны), "
        + "X — сумма столбца «Сумма (закуп)». Если итог не удаётся вычислить — поставь -. "
        + "Не добавляй до таблицы, между таблицей и итогами или после итогов пояснения, советы, вопросы и иной видимый текст. "
        + "Для программы после итогов добавь по одной служебной строке ТОВАР на каждую строку, у которой читаются название, количество и закупочная цена: "
        + "для точного совпадения с товаром каталога — {\"op\": \"receive\", \"product\": \"точное название из каталога\", \"qty\": N, \"purchase\": закупка, \"price\": розничная цена}; "
        + "для нового товара — {\"op\": \"create\", \"name\": \"название с накладной\", \"qty\": N, \"purchase\": закупка, \"price\": розничная цена, \"barcode\": \"если виден\", \"unit\": \"если видна\"}. "
        + "Служебные строки не являются частью видимого ответа: программа удалит их перед показом. Не создавай действие, если обязательное значение не распознано; "
        + "неразборчивую позицию всё равно оставь в таблице с дефисами. Розничная цена = закупка × (1 + наценка / 100): "
        + $"если владелец указал наценку, используй её, но не ниже {MinMarkupPercent:0} %; иначе применяй {MinMarkupPercent:0} % без вопроса владельцу. "
        + "Округляй цену продажи вверх до целого сома, а цены от 1000 сом — вверх до 10 сом. "
        + "Для фото не накладной соблюдай обычный режим: если это товар — опиши его и предложи заполнить карточку.";

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Regex Line = new(@"(?im)^[ \t*`•\-]*ТОВАР\s*:\s*(\{[^\n]*\})[ \t*`]*$");
    // 2026-10-09: старый ответ Gemini печатал команды прихода как `receive: {…} — товар` вместо служебной строки ТОВАР.
    private static readonly Regex ReceiveDisplayLine = new(@"(?im)^\s*receive\s*:\s*(\{[^\n]*?\})\s*[—–-]\s*(.+?)\s*$");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static bool IsExplicitWebSearchRequest(string question)
    {
        var t = (question ?? "").ToLowerInvariant();
        var web = new[] { "в интернете", "в интернет", "из интернета", "из сети", "в сети", "на сайте", "с сайта", "на веб-сайте", "онлайн", "online", "on the web", "on the internet", "from the internet", "from the web", "from website", "website", "internette", "internetda", "internetdan", "интернеттен", "интернетте", "тармактан" };
        var action = new[] { "найд", "поищ", "ищи", "проверь", "посмотр", "загруз", "скача", "search", "find", "look up", "download", "ara", "bul", "qidir", "top", "изде", "тап" };
        var negated = new[] { "не ищи", "не надо искать", "не нужно искать", "не ищите", "не загружай", "не скачивай", "don't search", "do not search", "don't look up" };
        return !negated.Any(t.Contains) && web.Any(t.Contains) && action.Any(t.Contains);
    }

    public static bool IsExplicitPhotoWebSearchRequest(string question)
    {
        var t = (question ?? "").ToLowerInvariant();
        var photo = new[] { "фото", "фотк", "картинк", "изображен", "сүрөт", "photo", "picture", "image", "fotoğraf", "resim", "rasm", "surat" };
        return IsExplicitWebSearchRequest(t) && photo.Any(t.Contains);
    }

    /// <summary>Правила для ИИ (дописываются к инструкции советника и бота).</summary>
    public const string PromptText =
        "ДЕЙСТВИЯ С ТОВАРАМИ: ты можешь предлагать изменения товаров — владелец подтвердит кнопкой, до неё ничего не меняется "
        + "(не пиши, что уже сделал; пиши «предлагаю» / «подтвердите»). Каждое действие — ОТДЕЛЬНОЙ строкой в конце ответа, ровно так: "
        + "ТОВАР: {\"op\": \"...\", \"product\": \"точное название или id из сводки\", ...} — одной строкой JSON. Операции: "
        + "stock_in (приход, \"qty\": число), stock_out (списание, \"qty\", \"reason\": \"Просрочка\"/\"Брак\"/\"Порча\"/\"Кража\"/\"Другое\"), "
        + "stock_set (точный остаток по пересчёту, \"qty\"), set_expiry (срок годности, \"date\": \"ГГГГ-ММ-ДД\"), "
        + "set_shelf_life (срок хранения, \"days\": число), set_min (минимальный остаток, \"qty\"), set_price (цена продажи, \"price\"), "
        + "set_purchase (закупочная цена, \"price\"), set_description (описание для витрины и сайта, \"text\": 2–5 предложений: что это, "
        + "состав/материал, объём/вес, чем полезно — без выдуманных цифр и сертификатов), set_country (страна, \"text\"), "
        + "set_name (новое название, \"text\"), set_brand (бренд, \"text\"), set_category (категория, \"text\" — лучше из уже существующих), "
        + "set_barcode (штрихкод, \"text\": только цифры), set_article (артикул, \"text\"), "
        + "receive (приход по накладной: \"qty\", \"purchase\" — закупка, \"price\" — новая цена продажи), "
        + "create (новый товар: \"name\", \"qty\", \"purchase\", \"price\", \"barcode\"?, \"unit\"?, \"category\"?), "
        + "open_section (открыть раздел программы, \"section\": warehouse — склад, sales — продажи, finance — финансы, analytics — аналитика, "
        + "salary — зарплата, clients — клиенты, debts — долги, restock — пополнение и сроки; выполняется сразу; используй, когда просят открыть или показать раздел), "
        + "photo (найти и поставить фото только если пользователь прямо попросил поискать его в интернете / сети), open (открыть товар на складе программы — выполняется сразу, без подтверждения; "
        + "только когда просят открыть или показать товар). Количество для «добавь/прибавь/пришло» — stock_in, для «спиши/убери/уменьши» — stock_out, "
        + "для «на складе ровно N» — stock_set. Просроченный товар (раздел «Сроки годности») — предлагай stock_out с reason «Просрочка». "
        + "«Заполни карточку / дополни информацию / опиши товар» — несколько строк сразу: set_description, set_country, set_brand и set_category "
        + "(если их нет в сводке и ты уверен). Не предлагай фото через интернет без прямой команды искать фото в сети. "
        + "Не меняй название и штрихкод, если об этом не просили. "
        + "Не предлагай значения, которые уже стоят в карточке; если в карточке бренд или категория явно неверные (по найденному в интернете), "
        + "предложи верные и скажи почему. "
        + "Если в сводке есть блок «НАЙДЕНО В ИНТЕРНЕТЕ О ТОВАРАХ» — описание, страну, бренд и состав бери оттуда и коротко скажи, откуда сведения; "
        + "если там «сведений не нашлось» — не придумывай факты, предложи владельцу дописать сам или ограничься тем, что видно из названия. "
        + "Обрабатывай все явно перечисленные действия и все строки накладной; не отбрасывай позиции из-за их количества. "
        + "Название товара пиши точно как в сводке; если товар не найден или неясен — спроси, а не угадывай.";

    /// <summary>Похоже на просьбу изменить товар (бот: такие фразы идут к ИИ, а не в готовые отчёты).</summary>
    public static bool LooksLikeAction(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        string[] verbs =
        {
            "добав", "прибав", "оприход", "приход", "пришло", "спиш", "списат", "списан", "убер", "уменьш", "увелич", "поставь", "установи", "измени",
            "смени", "заполни", "опиши", "описан", "срок год", "годен до", "минимальн", "пересчит", "ревизи", "фото для", "найди фото",
            "дополн", "карточк", "бренд", "категори", "штрихкод", "переимен", "артикул", "страну", "страна производ",
            "открой", "открыть", "покажи товар", "ачып", "ачкыла",
            "кош", "чыгар", "өзгөрт",
        };
        return verbs.Any(t.Contains);
    }

    private static bool IsExactProductMatch(string? raw, CatalogProductTileVm candidate)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        var q = raw.Trim();
        return string.Equals(candidate.Id, q, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(candidate.Barcode) && string.Equals(candidate.Barcode.Trim(), q, StringComparison.OrdinalIgnoreCase))
            || string.Equals(candidate.Title.Trim(), q, StringComparison.CurrentCultureIgnoreCase);
    }

    private static CatalogProductTileVm? ResolveProductForAction(string? rawName, string op)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return null;
        var exact = ProductActions.Find(rawName);
        if (exact is not null && IsExactProductMatch(rawName, exact))
            return exact;
        if (op is "photo" or "open")
            return exact;
        return null;
    }

    /// <summary>Товары, о которых спрашивают (по словам вопроса в названии), — до <paramref name="max"/>, лучшие совпадения первыми.</summary>
    public static List<CatalogProductTileVm> Mentioned(string question, int max = 5)
    {
        // Название в кавычках — точно этот товар (06.10: «…450гр бальзам» цеплял и «Энергетик … 0,450гр»).
        var quoted = Regex.Matches(question ?? "", "[«\"„“]([^«»\"„“”]{3,120})[»\"”]").Select(m => ProductActions.Find(m.Groups[1].Value.Trim()))
            .Where(p => p != null).Select(p => p!).Distinct().Take(max).ToList();
        if (quoted.Count > 0)
            return quoted;
        var words = Regex.Split((question ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 3).Distinct().ToList();
        if (words.Count == 0)
            return new List<CatalogProductTileVm>();
        List<CatalogProductTileVm> all;
        try
        {
            all = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return new List<CatalogProductTileVm>();
        }
        var scored = all
            .Select(p => (p, Score: words.Count(w => p.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) || (p.Barcode ?? "") == w)))
            .Where(x => x.Score > 0)
            .ToList();
        if (scored.Count == 0)
            return new List<CatalogProductTileVm>();
        // Только лучшие совпадения: товар с одним случайным общим словом рядом с точным попаданием не нужен.
        var best = scored.Max(x => x.Score);
        return scored
            .Where(x => x.Score >= Math.Max(1, best - 1) && (best < 3 || x.Score >= best * 0.6))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.p.Title.Length)
            .Take(max)
            .Select(x => x.p)
            .ToList();
    }

    /// <summary>Полные карточки упомянутых товаров для ИИ (описание, страна, срок, есть ли фото) — чтобы дополнять, а не угадывать.</summary>
    public static async Task<string> MentionedCardsContextAsync(string question, CancellationToken ct)
    {
        var products = Mentioned(question);
        if (products.Count == 0)
            return "";
        var lines = new List<string>();
        foreach (var p in products)
            lines.Add(await ProductActions.DescribeForAiAsync(p, ct).ConfigureAwait(false));
        return "КАРТОЧКИ ТОВАРОВ ИЗ ВОПРОСА (для действий «ТОВАР:»):\n" + string.Join("\n", lines);
    }

    /// <summary>Строки «ТОВАР: {…}» из ответа ИИ → шаги (товар найден в каталоге, операция известна). Строки из ответа убираются.</summary>
    /// <summary>Строки последнего разбора, которые не удалось превратить в действие (товар не найден), — показать владельцу.</summary>
    public static (string Answer, List<Step> Steps, IReadOnlyList<string> Skipped) Extract(string answer, string question = "")
    {
        var steps = new List<Step>();
        var skipped = new List<string>();
        var receiveLines = ReceiveDisplayLine.Matches(answer).Cast<Match>().ToList();
        var extractionAnswer = answer;
        string? receiveTable = null;
        if (receiveLines.Count > 0)
        {
            var tableRows = new List<string>();
            var quantities = new List<double>();
            var amounts = new List<double>();
            for (var i = 0; i < receiveLines.Count; i++)
            {
                var match = receiveLines[i];
                try
                {
                    using var doc = JsonDocument.Parse(match.Groups[1].Value);
                    var root = doc.RootElement;
                    string? Value(string name) => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                        ? value.ToString().Trim() : null;
                    double? Number(string name) => double.TryParse(Value(name)?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                        && double.IsFinite(value) ? value : null;
                    var name = match.Groups[2].Value.Trim();
                    var qty = Number("qty");
                    var purchase = Number("purchase");
                    var price = Number("price");
                    var barcode = Value("barcode") ?? Value("article") ?? Value("sku");
                    var unit = Value("unit");
                    var category = Value("category");
                    var amount = qty.HasValue && purchase.HasValue ? qty.Value * purchase.Value : (double?)null;
                    static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
                    string F(double? value) => value?.ToString("N2", Ru) ?? "-";
                    tableRows.Add($"| {i + 1} | {Cell(name)} | {Cell(barcode ?? "-")} | {Cell(unit ?? "-")} | {Cell(qty?.ToString("0.###", Ru) ?? "-")} | {F(purchase)} | {F(price)} | {F(amount)} |");
                    if (qty.HasValue) quantities.Add(qty.Value);
                    if (amount.HasValue) amounts.Add(amount.Value);

                    var directive = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["op"] = "receive", ["product"] = name, ["qty"] = qty, ["purchase"] = purchase, ["price"] = price,
                        ["barcode"] = barcode, ["unit"] = unit, ["category"] = category,
                    });
                    extractionAnswer = extractionAnswer.Replace(receiveLines[i].Value, "ТОВАР: " + directive, StringComparison.Ordinal);
                }
                catch (JsonException)
                {
                    // Некорректную служебную строку убираем из видимого ответа, но не создаём по ней приход.
                    extractionAnswer = extractionAnswer.Replace(match.Value, "", StringComparison.Ordinal);
                }
            }
            if (tableRows.Count > 0)
            {
                receiveTable = "| № | Наименование товара | Штрихкод / Артикул | Ед. изм. | Кол-во | Цена закупа | Розничная цена | Сумма (закуп) |\n"
                    + "|---:|---|---|---|---:|---:|---:|---:|\n" + string.Join("\n", tableRows)
                    + $"\n\nВсего наименований: {tableRows.Count}\nОбщее количество единиц: {(quantities.Count == tableRows.Count ? quantities.Sum().ToString("0.###", Ru) : "-")}\nОбщая сумма закупа: {(amounts.Count == tableRows.Count ? amounts.Sum().ToString("N2", Ru) : "-")} сом";
            }
        }
        foreach (Match m in Line.Matches(extractionAnswer))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var root = doc.RootElement;
                string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString().Trim() : null;
                double? N(params string[] names)
                {
                    foreach (var n in names)
                        if (S(n) is { } raw && double.TryParse(raw.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                            return d;
                    return null;
                }
                var op = (S("op") ?? "").ToLowerInvariant();
                if (op == "open_section")
                {
                    if (S("section") is { Length: > 0 } section)
                        steps.Add(new Step(op, new CatalogProductTileVm("", section, "", false), null, section, null));
                    continue;
                }
                // Наценка вместо цены: price = purchase × (1 + margin / 100).
                var purchase = N("purchase", "purchase_price", "cost");
                var price = N("price", "sale_price");
                if (price is null && purchase is { } pp && N("margin", "markup") is { } margin)
                    price = RoundPrice(pp * (1 + margin / 100));
                if (op == "create")
                {
                    var newName = (S("name") ?? S("product") ?? "").Trim();
                    // Такой товар уже есть — это приход, а не новый товар.
                    if (ProductActions.Find(newName) is { } existing)
                        op = "receive";
                    else if (newName.Length >= 2 && N("qty", "quantity") is { } nq && nq >= 0 && purchase is { } np && np >= 0)
                    {
                        var (newPrice, raised) = EnsureMinMarkup(np, price, 0);
                        steps.Add(new Step("create", new CatalogProductTileVm("", newName, $"{newPrice ?? 0:0.00}", false), nq, null, null, np, newPrice,
                            S("barcode") is { Length: >= 4 } bc && bc.All(char.IsDigit) ? bc : null, S("unit"), S("category"), raised));
                        continue;
                    }
                    else
                        continue;
                }
                var rawProductName = S("product") ?? S("id") ?? S("name");
                var product = ResolveProductForAction(rawProductName, op);
                if (product is null)
                {
                    // 2026-10-06, владелец (снимок: накладная на 20 позиций, «баг — не появляется кнопка!!»): ИИ писал «receive» для
                    // товаров, которых нет в каталоге, — все строки молча пропускались. Приход неизвестного товара с закупкой — это новый товар.
                    var unknownName = (rawProductName ?? "").Trim();
                    if (op is "receive" or "stock_in" && unknownName.Length >= 2 && N("qty", "quantity") is { } uq && uq >= 0 && purchase is { } up && up >= 0)
                    {
                        var (newPrice, raised) = EnsureMinMarkup(up, price, 0);
                        steps.Add(new Step("create", new CatalogProductTileVm("", unknownName, $"{newPrice ?? 0:0.00}", false), uq, null, null, up, newPrice,
                            S("barcode") is { Length: >= 4 } ubc && ubc.All(char.IsDigit) ? ubc : null, S("unit"), S("category"), raised));
                        continue;
                    }
                    if (op is not "photo" and not "open" && !string.IsNullOrWhiteSpace(rawProductName))
                    {
                        PosLogger.Log($"ИИ: действие с товаром пропущено — товар «{rawProductName}» не подтверждён по точному совпадению.", "INFO");
                        skipped.Add(rawProductName);
                    }
                    continue;
                }
                Step? step = op switch
                {
                    "stock_in" or "stock_out" or "stock_set" or "set_min" when N("qty", "quantity") is { } q && q >= 0 => new Step(op, product, q, null, S("reason")),
                    "set_price" or "set_purchase" when N("price", "value") is { } p && p >= 0 => new Step(op, product, p, null, null),
                    "set_shelf_life" when N("days", "value") is { } days && days >= 0 => new Step(op, product, days, null, null),
                    "set_expiry" when DateTime.TryParseExact(S("date") ?? S("value") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        => new Step(op, product, null, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null),
                    "set_description" or "set_country" or "set_name" or "set_brand" or "set_category" or "set_article"
                        when (S("text") ?? S("value")) is { Length: > 0 } text => new Step(op, product, null, text.Length > 3000 ? text[..3000] : text, null),
                    "set_barcode" when (S("text") ?? S("value")) is { Length: >= 4 } code && code.All(char.IsDigit) => new Step(op, product, null, code, null),
                    "photo" when IsExplicitPhotoWebSearchRequest(question) => new Step(op, product, null, null, null),
                    "photo" => null,
                    "open" => new Step(op, product, null, null, null),
                    "receive" when N("qty", "quantity") is { } rq && rq > 0 => EnsureMinMarkup(purchase, price, product.PriceValue) is var (rp, rr)
                        ? new Step(op, product, rq, null, null, purchase, rp, S("barcode") ?? S("article") ?? S("sku"), S("unit"), RaisedToMin: rr)
                        : null,
                    _ => null,
                };
                // То же значение, что уже в карточке (06.10: «категория Парфюм», «бренд Legend» — без изменений), не предлагаем.
                if (step is { Op: "set_brand" } && string.Equals(step.Text?.Trim(), product.Brand?.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    step = null;
                if (step is { Op: "set_category" } && string.Equals(step.Text?.Trim(), product.Category?.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    step = null;
                if (step is { Op: "set_name" } && string.Equals(step.Text?.Trim(), product.Title.Trim(), StringComparison.CurrentCultureIgnoreCase))
                    step = null;
                if (step is { Op: "stock_in" or "stock_out" or "stock_set" or "receive" })
                    step = step with { ExpectedStock = product.Quantity };
                if (step != null)
                    steps.Add(step);
            }
            catch (JsonException)
            {
            }
        }
        var cleaned = Line.Replace(extractionAnswer, "").Trim();
        if (receiveTable is not null && !cleaned.Contains("|---", StringComparison.Ordinal))
            cleaned = receiveTable;
        return (Regex.Replace(cleaned, @"\n{3,}", "\n\n"), steps, skipped);
    }

    private static string Unit(CatalogProductTileVm p) => p.MustWeigh ? T("кг", "кг", "kg", "kg", "kg") : T("шт", "даана", "pcs", "adet", "dona");

    private static string Q(double v) => v.ToString("0.###", Ru);
    private static double ExpectedStock(Step s) => s.ExpectedStock ?? s.Product.Quantity;

    /// <summary>Что будет сделано — для подтверждения.</summary>
    public static string Describe(Step s)
    {
        var name = "«" + s.Product.Title + "»";
        var som = T("сом", "сом", "som", "som", "so'm");
        return s.Op switch
        {
            "stock_in" => T($"{name} — приход {Q(s.Qty!.Value)} {Unit(s.Product)} (сейчас {Q(ExpectedStock(s))})", $"{name} — кириш {Q(s.Qty!.Value)} {Unit(s.Product)} (азыр {Q(ExpectedStock(s))})",
                $"{name} — receive {Q(s.Qty!.Value)} {Unit(s.Product)} (now {Q(ExpectedStock(s))})", $"{name} — giriş {Q(s.Qty!.Value)} {Unit(s.Product)} (şu an {Q(ExpectedStock(s))})",
                $"{name} — kirim {Q(s.Qty!.Value)} {Unit(s.Product)} (hozir {Q(ExpectedStock(s))})"),
            "stock_out" => T($"{name} — списать {Q(s.Qty!.Value)} {Unit(s.Product)}{(s.Reason is { Length: > 0 } r ? $" ({r})" : "")} (сейчас {Q(ExpectedStock(s))})",
                $"{name} — эсептен чыгаруу {Q(s.Qty!.Value)} {Unit(s.Product)}{(s.Reason is { Length: > 0 } r2 ? $" ({r2})" : "")} (азыр {Q(ExpectedStock(s))})",
                $"{name} — write off {Q(s.Qty!.Value)} {Unit(s.Product)}{(s.Reason is { Length: > 0 } r3 ? $" ({r3})" : "")} (now {Q(ExpectedStock(s))})",
                $"{name} — düş {Q(s.Qty!.Value)} {Unit(s.Product)}{(s.Reason is { Length: > 0 } r4 ? $" ({r4})" : "")} (şu an {Q(ExpectedStock(s))})",
                $"{name} — hisobdan chiqarish {Q(s.Qty!.Value)} {Unit(s.Product)}{(s.Reason is { Length: > 0 } r5 ? $" ({r5})" : "")} (hozir {Q(ExpectedStock(s))})"),
            "stock_set" => T($"{name} — остаток ровно {Q(s.Qty!.Value)} {Unit(s.Product)} (сейчас {Q(ExpectedStock(s))})", $"{name} — калдык так {Q(s.Qty!.Value)} {Unit(s.Product)} (азыр {Q(ExpectedStock(s))})",
                $"{name} — stock exactly {Q(s.Qty!.Value)} {Unit(s.Product)} (now {Q(ExpectedStock(s))})", $"{name} — stok tam {Q(s.Qty!.Value)} {Unit(s.Product)} (şu an {Q(ExpectedStock(s))})",
                $"{name} — qoldiq aniq {Q(s.Qty!.Value)} {Unit(s.Product)} (hozir {Q(ExpectedStock(s))})"),
            "set_min" => T($"{name} — минимальный остаток {Q(s.Qty!.Value)}", $"{name} — минималдуу калдык {Q(s.Qty!.Value)}", $"{name} — minimum stock {Q(s.Qty!.Value)}",
                $"{name} — asgari stok {Q(s.Qty!.Value)}", $"{name} — minimal qoldiq {Q(s.Qty!.Value)}"),
            "set_price" => T($"{name} — цена продажи {Q(s.Qty!.Value)} {som} (сейчас {s.Product.PriceLine})", $"{name} — сатуу баасы {Q(s.Qty!.Value)} {som} (азыр {s.Product.PriceLine})",
                $"{name} — sale price {Q(s.Qty!.Value)} {som} (now {s.Product.PriceLine})", $"{name} — satış fiyatı {Q(s.Qty!.Value)} {som} (şu an {s.Product.PriceLine})",
                $"{name} — sotuv narxi {Q(s.Qty!.Value)} {som} (hozir {s.Product.PriceLine})"),
            "set_purchase" => T($"{name} — закупочная цена {Q(s.Qty!.Value)} {som}", $"{name} — сатып алуу баасы {Q(s.Qty!.Value)} {som}", $"{name} — purchase price {Q(s.Qty!.Value)} {som}",
                $"{name} — alış fiyatı {Q(s.Qty!.Value)} {som}", $"{name} — xarid narxi {Q(s.Qty!.Value)} {som}"),
            "set_shelf_life" => T($"{name} — срок хранения {Q(s.Qty!.Value)} дн.", $"{name} — сактоо мөөнөтү {Q(s.Qty!.Value)} күн", $"{name} — shelf life {Q(s.Qty!.Value)} days",
                $"{name} — raf ömrü {Q(s.Qty!.Value)} gün", $"{name} — saqlash muddati {Q(s.Qty!.Value)} kun"),
            "set_expiry" => T($"{name} — срок годности до {Pretty(s.Text)}", $"{name} — жарактуулук мөөнөтү {Pretty(s.Text)} чейин", $"{name} — expires {Pretty(s.Text)}",
                $"{name} — son kullanma {Pretty(s.Text)}", $"{name} — yaroqlilik muddati {Pretty(s.Text)} gacha"),
            "set_description" => T($"{name} — описание: {Short(s.Text)}", $"{name} — сүрөттөмө: {Short(s.Text)}", $"{name} — description: {Short(s.Text)}",
                $"{name} — açıklama: {Short(s.Text)}", $"{name} — tavsif: {Short(s.Text)}"),
            "set_country" => T($"{name} — страна: {s.Text}", $"{name} — өлкө: {s.Text}", $"{name} — country: {s.Text}", $"{name} — ülke: {s.Text}", $"{name} — mamlakat: {s.Text}"),
            "set_name" => T($"{name} — новое название: «{s.Text}»", $"{name} — жаңы аталышы: «{s.Text}»", $"{name} — new name: “{s.Text}”", $"{name} — yeni ad: «{s.Text}»", $"{name} — yangi nomi: «{s.Text}»"),
            "set_brand" => T($"{name} — бренд: {s.Text}", $"{name} — бренд: {s.Text}", $"{name} — brand: {s.Text}", $"{name} — marka: {s.Text}", $"{name} — brend: {s.Text}"),
            "set_category" => T($"{name} — категория: {s.Text}", $"{name} — категория: {s.Text}", $"{name} — category: {s.Text}", $"{name} — kategori: {s.Text}", $"{name} — kategoriya: {s.Text}"),
            "set_barcode" => T($"{name} — штрихкод: {s.Text} (был {s.Product.Barcode})", $"{name} — штрихкод: {s.Text} ({s.Product.Barcode} болчу)", $"{name} — barcode: {s.Text} (was {s.Product.Barcode})",
                $"{name} — barkod: {s.Text} (önceki {s.Product.Barcode})", $"{name} — shtrix-kod: {s.Text} (avval {s.Product.Barcode})"),
            "set_article" => T($"{name} — артикул: {s.Text}", $"{name} — артикул: {s.Text}", $"{name} — article: {s.Text}", $"{name} — ürün kodu: {s.Text}", $"{name} — artikul: {s.Text}"),
            "receive" => T($"{name} — приход {Q(s.Qty!.Value)} {Unit(s.Product)}{PriceText(s)}", $"{name} — кириш {Q(s.Qty!.Value)} {Unit(s.Product)}{PriceText(s)}",
                $"{name} — receive {Q(s.Qty!.Value)} {Unit(s.Product)}{PriceText(s)}", $"{name} — giriş {Q(s.Qty!.Value)} {Unit(s.Product)}{PriceText(s)}",
                $"{name} — kirim {Q(s.Qty!.Value)} {Unit(s.Product)}{PriceText(s)}"),
            "create" => T($"Новый товар {name} — {Q(s.Qty!.Value)} {s.Unit ?? "шт"}{PriceText(s)}{(s.Barcode is { } b ? $", штрихкод {b}" : "")}",
                $"Жаңы товар {name} — {Q(s.Qty!.Value)} {s.Unit ?? "даана"}{PriceText(s)}{(s.Barcode is { } b2 ? $", штрихкод {b2}" : "")}",
                $"New product {name} — {Q(s.Qty!.Value)} {s.Unit ?? "pcs"}{PriceText(s)}{(s.Barcode is { } b3 ? $", barcode {b3}" : "")}",
                $"Yeni ürün {name} — {Q(s.Qty!.Value)} {s.Unit ?? "adet"}{PriceText(s)}{(s.Barcode is { } b4 ? $", barkod {b4}" : "")}",
                $"Yangi mahsulot {name} — {Q(s.Qty!.Value)} {s.Unit ?? "dona"}{PriceText(s)}{(s.Barcode is { } b5 ? $", shtrix-kod {b5}" : "")}"),
            "open_section" => T($"Открыть раздел «{SectionTitle(s.Text)}»", $"«{SectionTitle(s.Text)}» бөлүмүн ачуу", $"Open the “{SectionTitle(s.Text)}” section",
                $"«{SectionTitle(s.Text)}» bölümünü aç", $"«{SectionTitle(s.Text)}» bo'limini ochish"),
            "open" => T($"{name} — открыть на складе", $"{name} — кампада ачуу", $"{name} — open in the warehouse", $"{name} — depoda aç", $"{name} — omborda ochish"),
            "photo" => T($"{name} — найти фото в интернете и поставить", $"{name} — интернеттен сүрөт таап коюу", $"{name} — find a photo online and set it",
                $"{name} — internetten fotoğraf bulup koy", $"{name} — internetdan surat topib qo'yish"),
            _ => name,
        };
    }

    /// <summary>«, закупка 50 сом, цена 63 сом (наценка 26 %)».</summary>
    private static string PriceText(Step s)
    {
        var som = T("сом", "сом", "som", "som", "so'm");
        var parts = new List<string>();
        if (s.Purchase is { } p)
            parts.Add(T($"закупка {Q(p)} {som}", $"сатып алуу {Q(p)} {som}", $"cost {Q(p)} {som}", $"alış {Q(p)} {som}", $"xarid {Q(p)} {som}"));
        if (s.Price is { } pr)
            parts.Add(T($"цена {Q(pr)} {som}", $"баасы {Q(pr)} {som}", $"price {Q(pr)} {som}", $"fiyat {Q(pr)} {som}", $"narx {Q(pr)} {som}")
                      // 2026-10-06: у прихода — и прежняя цена, чтобы снижение или рост были видны до «Выполнить».
                      + (s.Op == "receive" && s.Product.PriceValue > 0 && Math.Abs(s.Product.PriceValue - pr) > 0.001
                          ? $" ({T("было", "болчу", "was", "önceki", "avval")} {Q(s.Product.PriceValue)})" : "")
                      + (s.Purchase is > 0 ? $" ({T("наценка", "үстөк", "markup", "kâr payı", "ustama")} {(pr / s.Purchase!.Value - 1) * 100:0}%)" : "")
                      + (s.RaisedToMin ? " — " + T($"поднято до минимальной наценки {MinMarkupPercent:0}%", $"минималдуу {MinMarkupPercent:0}% үстөккө чейин көтөрүлдү",
                          $"raised to the minimum {MinMarkupPercent:0}% markup", $"asgari %{MinMarkupPercent:0} kâr payına yükseltildi", $"minimal {MinMarkupPercent:0}% ustamagacha ko'tarildi") : ""));
        return parts.Count > 0 ? ", " + string.Join(", ", parts) : "";
    }

    /// <summary>Цена продажи: до целого сома, дороже 1000 — до 10 сом.</summary>
    public static double RoundPrice(double v) => v >= 1000 ? Math.Round(v / 10, MidpointRounding.AwayFromZero) * 10 : Math.Round(v, MidpointRounding.AwayFromZero);

    private static string SectionTitle(string? key) => (key ?? "").ToLowerInvariant() switch
    {
        "warehouse" => T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"),
        "sales" => T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"),
        "finance" => T("Финансы", "Каржы", "Finance", "Finans", "Moliya"),
        "analytics" => T("Аналитика", "Талдоо", "Analytics", "Analiz", "Analitika"),
        "salary" => T("Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi"),
        "clients" => T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar"),
        "debts" => T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari"),
        "restock" => T("Пополнение и сроки", "Толуктоо жана мөөнөттөр", "Restock & expiry", "Stok yenileme ve SKT", "To'ldirish va muddatlar"),
        _ => key ?? "",
    };

    private static string Pretty(string? isoDate) =>
        DateTime.TryParseExact(isoDate ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : isoDate ?? "";

    private static string Short(string? text) => text is null ? "" : text.Length > 160 ? text[..160] + "…" : text;

    /// <summary>Выполнить шаг (после подтверждения владельца).</summary>
    public static async Task<ProductActions.Result> ExecuteAsync(Step s, string? actor, CancellationToken ct = default)
    {
        var id = s.Product.Id;
        var who = actor ?? "ИИ";
        return s.Op switch
        {
            "stock_in" => await ProductActions.ChangeStockAsync(id, s.Qty, null, $"Приход ({who})", who, ct, s.ExpectedStock ?? s.Product.Quantity).ConfigureAwait(false),
            "stock_out" => await ProductActions.ChangeStockAsync(id, -s.Qty, null, (s.Reason is { Length: > 0 } r ? r : "Списание") + $" ({who})", who, ct, s.ExpectedStock ?? s.Product.Quantity).ConfigureAwait(false),
            "stock_set" => await ProductActions.ChangeStockAsync(id, null, s.Qty, $"Ревизия ({who})", who, ct, s.ExpectedStock ?? s.Product.Quantity).ConfigureAwait(false),
            "set_min" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["minimum_quantity"] = s.Qty }, T("минимальный остаток", "минималдуу калдык", "minimum stock", "asgari stok", "minimal qoldiq"), who, ct).ConfigureAwait(false),
            "set_price" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["price"] = s.Qty!.Value.ToString("0.00", CultureInfo.InvariantCulture) }, T("цена продажи", "сатуу баасы", "sale price", "satış fiyatı", "sotuv narxi"), who, ct).ConfigureAwait(false),
            "set_purchase" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["purchase_price"] = s.Qty!.Value.ToString("0.00", CultureInfo.InvariantCulture) }, T("закупочная цена", "сатып алуу баасы", "purchase price", "alış fiyatı", "xarid narxi"), who, ct).ConfigureAwait(false),
            "set_shelf_life" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["shelf_life_days"] = (int)Math.Round(s.Qty!.Value) }, T("срок хранения", "сактоо мөөнөтү", "shelf life", "raf ömrü", "saqlash muddati"), who, ct).ConfigureAwait(false),
            "set_expiry" => await ExpiryAsync(id, s.Text!, who, ct).ConfigureAwait(false),
            "set_description" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["description"] = s.Text }, T("описание", "сүрөттөмө", "description", "açıklama", "tavsif"), who, ct).ConfigureAwait(false),
            "set_country" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["country"] = s.Text }, T("страна", "өлкө", "country", "ülke", "mamlakat"), who, ct).ConfigureAwait(false),
            "set_name" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["name"] = s.Text }, T("название", "аталышы", "name", "ad", "nomi"), who, ct).ConfigureAwait(false),
            // Бренд и категория — по названию (сервер сопоставляет сам, проверено 06.10: brand_name/category_name).
            "set_brand" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["brand_name"] = s.Text }, T("бренд", "бренд", "brand", "marka", "brend"), who, ct).ConfigureAwait(false),
            "set_category" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["category_name"] = s.Text }, T("категория", "категория", "category", "kategori", "kategoriya"), who, ct).ConfigureAwait(false),
            "set_barcode" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["barcode"] = s.Text }, T("штрихкод", "штрихкод", "barcode", "barkod", "shtrix-kod"), who, ct).ConfigureAwait(false),
            "set_article" => await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["article"] = s.Text }, T("артикул", "артикул", "article", "ürün kodu", "artikul"), who, ct).ConfigureAwait(false),
            "receive" => await ReceiveAsync(s, who, ct).ConfigureAwait(false),
            "create" => await CreateAsync(s, who, ct).ConfigureAwait(false),
            "open_section" => OpenSection is { } openSection
                ? OpenedSection(openSection, s.Text ?? "")
                : new ProductActions.Result(false, T("Разделы открываются в программе владельца.", "Бөлүмдөр ээсинин программасында ачылат.", "Sections open in the owner app.",
                    "Bölümler sahip programında açılır.", "Bo'limlar egasi dasturida ochiladi.")),
            "open" => OpenProduct is { } open
                ? Opened(open, s.Product)
                : new ProductActions.Result(false, T("Открыть товар можно в программе владельца.", "Товарды ээсинин программасында ачса болот.", "Products open in the owner app.",
                    "Ürün sahip programında açılır.", "Mahsulot egasi dasturida ochiladi.")),
            "photo" => PhotoFinder is { } finder
                ? await finder(s.Product, ct).ConfigureAwait(false)
                : new ProductActions.Result(false, T("Поиск фото доступен в программе владельца.", "Сүрөт издөө ээсинин программасында жеткиликтүү.", "Photo search is available in the owner app.",
                    "Fotoğraf arama sahip programında kullanılabilir.", "Surat qidirish egasi dasturida mavjud.")),
            _ => new ProductActions.Result(false, "?"),
        };
    }

    private static ProductActions.Result OpenedSection(Action<string> open, string key)
    {
        open(key);
        return new ProductActions.Result(true, T($"Открыт раздел «{SectionTitle(key)}».", $"«{SectionTitle(key)}» бөлүмү ачылды.", $"Opened “{SectionTitle(key)}”.",
            $"«{SectionTitle(key)}» açıldı.", $"«{SectionTitle(key)}» ochildi."));
    }

    /// <summary>Приход по накладной: остаток + закупка и цена продажи (если указаны).</summary>
    private static async Task<ProductActions.Result> ReceiveAsync(Step s, string who, CancellationToken ct)
    {
        var stock = await ProductActions.ChangeStockAsync(s.Product.Id, s.Qty, null, $"Приход по накладной ({who})", who, ct,
            s.ExpectedStock ?? s.Product.Quantity).ConfigureAwait(false);
        if (!stock.Ok)
            return stock;
        var fields = new Dictionary<string, object?>();
        if (s.Purchase is { } p)
            fields["purchase_price"] = p.ToString("0.00", CultureInfo.InvariantCulture);
        if (s.Price is { } pr)
            fields["price"] = pr.ToString("0.00", CultureInfo.InvariantCulture);
        if (fields.Count == 0)
            return stock;
        ProductActions.Result prices;
        try
        {
            prices = await ProductActions.UpdateFieldsAsync(s.Product.Id, fields, T("закупка и цена", "сатып алуу жана баа", "cost and price", "alış ve fiyat", "xarid va narx"), who, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ProductActions.Result(true, stock.Message + PriceText(s)
                + T(" — приход выполнен, изменение цен прервано. Не повторяйте приход; проверьте цены отдельно.",
                    " — кириш аткарылды, бааларды өзгөртүү токтотулду. Киришти кайталап жасабаңыз; бааларды өзүнчө текшериңиз.",
                    " — stock receipt completed, price update was interrupted. Do not repeat the receipt; check prices separately.",
                    " — stok girişi tamamlandı, fiyat güncellemesi kesildi. Girişi tekrarlamayın; fiyatları ayrıca kontrol edin.",
                    " — kirim bajarildi, narx yangilanishi to'xtadi. Kirimni takrorlamang; narxlarni alohida tekshiring."), Partial: true);
        }
        if (prices.Ok)
            return new ProductActions.Result(true, stock.Message + PriceText(s));
        return new ProductActions.Result(true, stock.Message
            + T(" — приход выполнен, но цены не обновлены. Не повторяйте приход; обновите цены отдельно: ",
                " — кириш аткарылды, бирок баалар жаңырган жок. Киришти кайталап жасабаңыз; бааларды өзүнчө жаңыртыңыз: ",
                " — receipt completed, but prices were not updated. Do not repeat the receipt; update prices separately: ",
                " — giriş tamamlandı ancak fiyatlar güncellenmedi. Girişi tekrarlamayın; fiyatları ayrıca güncelleyin: ",
                " — kirim bajarildi, ammo narxlar yangilanmadi. Kirimni takrorlamang; narxlarni alohida yangilang: ") + prices.Message, Partial: true);
    }

    /// <summary>Новый товар по накладной — карточка сразу с остатком, закупкой и ценой (CreateProductAsync, как «Создать товар»).</summary>
    private static async Task<ProductActions.Result> CreateAsync(Step s, string who, CancellationToken ct)
    {
        try
        {
            var request = new Api.ProductEditRequest
            {
                Name = s.Product.Title,
                Barcode = s.Barcode,
                CategoryName = s.Category,
                Unit = string.IsNullOrWhiteSpace(s.Unit) ? "шт" : s.Unit!,
                IsWeight = s.Unit is "кг" or "kg",
                Quantity = s.Qty ?? 0,
                PurchasePrice = s.Purchase,
                Price = s.Price,
                MarkupPercent = s.Purchase is > 0 && s.Price is { } pr ? Math.Round((pr / s.Purchase.Value - 1) * 100, 2) : null,
            };
            await PosApp.CatalogApi.CreateProductAsync(request, ct).ConfigureAwait(false);
            ProductActions.RequestCatalogRefresh?.Invoke();
            PosLogger.Log($"Действие с товаром ({who}): создан «{s.Product.Title}», {Q(s.Qty ?? 0)} шт, закупка {s.Purchase}, цена {s.Price}.", "STOCK");
            return new ProductActions.Result(true, T($"Создан товар «{s.Product.Title}»: {Q(s.Qty ?? 0)} {s.Unit ?? "шт"}{PriceText(s)}.",
                $"«{s.Product.Title}» товары түзүлдү: {Q(s.Qty ?? 0)}{PriceText(s)}.", $"Created “{s.Product.Title}”: {Q(s.Qty ?? 0)}{PriceText(s)}.",
                $"«{s.Product.Title}» oluşturuldu: {Q(s.Qty ?? 0)}{PriceText(s)}.", $"«{s.Product.Title}» yaratildi: {Q(s.Qty ?? 0)}{PriceText(s)}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Действие с товаром: «{s.Product.Title}» не создан ({ex.Message}).", "WARNING");
            return new ProductActions.Result(false, T($"«{s.Product.Title}» не создан: ", $"«{s.Product.Title}» түзүлгөн жок: ", $"“{s.Product.Title}” not created: ",
                $"«{s.Product.Title}» oluşturulmadı: ", $"«{s.Product.Title}» yaratilmadi: ") + ex.Message);
        }
    }

    private static ProductActions.Result Opened(Action<CatalogProductTileVm> open, CatalogProductTileVm product)
    {
        open(product);
        return new ProductActions.Result(true, T($"«{product.Title}» открыт на складе.", $"«{product.Title}» кампада ачылды.", $"“{product.Title}” is open in the warehouse.",
            $"«{product.Title}» depoda açıldı.", $"«{product.Title}» omborda ochildi."));
    }

    /// <summary>Голосом в звонке: «да, выполни» / «нет, не надо» (фраза целиком, с запятыми и точками).</summary>
    public static bool IsVoiceYes(string text)
    {
        var t = NormalizeVoiceConfirmation(text);
        if (IsVoiceNo(text))
            return false;
        return t is "да" or "давай" or "выполни" or "выполняй" or "подтверждаю" or "подтверждаю действие"
            or "да выполни" or "давай выполни" or "да выполняй" or "да подтверждаю"
            or "ооба" or "макул" or "аткар" or "ооба аткар" or "макул аткар"
            or "ок" or "окей" or "yes" or "yes do it" or "confirm" or "confirm order";
    }

    public static bool IsVoiceNo(string text)
    {
        var t = NormalizeVoiceConfirmation(text);
        return t is "нет" or "нет не надо" or "не надо" or "не надо делать" or "не надо выполнять"
            or "не выполняй" or "не выполняй это" or "не подтверждаю" or "не подтверждать" or "не подтверждаю действие"
            or "отмена" or "отмени" or "отказываюсь" or "жок" or "жок аткарба" or "жок аткарба муну"
            or "no" or "no thanks" or "no do not" or "do not" or "dont do it" or "cancel" or "cancel it";
    }

    private static string NormalizeVoiceConfirmation(string? text) =>
        Regex.Replace((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static async Task<ProductActions.Result> ExpiryAsync(string id, string isoDate, string who, CancellationToken ct)
    {
        var result = await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["expiration_date"] = isoDate },
            T("срок годности", "жарактуулук мөөнөтү", "expiry date", "son kullanma tarihi", "yaroqlilik muddati"), who, ct).ConfigureAwait(false);
        if (result.Ok)
            _ = ProductExpiryIndex.RefreshAsync(force: true, ct);
        return result;
    }

    /// <summary>Всё по очереди; ответ — по строке на шаг (✓ / ✗).</summary>
    public static async Task<string> ExecuteAllAsync(IReadOnlyList<Step> steps, string? actor, CancellationToken ct = default)
    {
        var duplicateReceipts = steps
            .Where(s => s.Op is "stock_in" or "receive" && !string.IsNullOrWhiteSpace(s.Product.Id))
            .GroupBy(s => s.Product.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicateReceipts.Count > 0)
        {
            var duplicateNames = duplicateReceipts.Select(g => g.First().Product.Title).ToList();
            return T(
                "Приход не выполнен: в пакете несколько строк для одного товара (" + string.Join(", ", duplicateNames) + "). Сверьте позиции и объедините их перед повтором.",
                "Киреше аткарылган жок: топтомдо бир товар үчүн бир нече сап бар (" + string.Join(", ", duplicateNames) + "). Позицияларды текшерип, кайталоодон мурун бириктириңиз.",
                "Receiving was not started: the batch has multiple lines for the same product (" + string.Join(", ", duplicateNames) + "). Review and merge those lines before retrying.",
                "Alış işlemi başlatılmadı: toplu işlemde aynı ürün için birden fazla satır var (" + string.Join(", ", duplicateNames) + "). Tekrar denemeden önce satırları inceleyip birleştirin.",
                "Kirim boshlanmadi: to'plamda bir mahsulot uchun bir nechta qator bor (" + string.Join(", ", duplicateNames) + "). Takrorlashdan oldin qatorlarni tekshirib birlashtiring.");
        }

        var lines = new List<string>();
        var changed = new List<string>();
        var completed = 0;
        var failed = 0;
        var partial = 0;
        var stopped = false;
        foreach (var step in steps)
        {
            ProductActions.Result r;
            try
            {
                r = await ExecuteAsync(step, actor, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lines.Add(T("⚠ Пакет остановлен. Результат текущего шага проверьте в NurCRM перед повтором.",
                    "⚠ Топтом токтотулду. Кайталоодон мурун учурдагы кадамдын жыйынтыгын NurCRM'ден текшериңиз.",
                    "⚠ Batch stopped. Check the current step in NurCRM before retrying.",
                    "⚠ Toplu işlem durduruldu. Tekrar denemeden önce mevcut adımı NurCRM'de kontrol edin.",
                    "⚠ To'plam to'xtatildi. Takrorlashdan oldin joriy qadamni NurCRM'da tekshiring."));
                stopped = true;
                break;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ: пакет действий остановлен на «{step.Product.Title}» ({ex.Message}).", "WARNING");
                lines.Add(T("⚠ Пакет остановлен из-за ошибки. Проверьте результат текущего шага в NurCRM перед повтором.",
                    "⚠ Топтом катага байланыштуу токтотулду. Кайталоодон мурун учурдагы кадамды NurCRM'ден текшериңиз.",
                    "⚠ Batch stopped after an error. Check the current step in NurCRM before retrying.",
                    "⚠ Toplu işlem hata nedeniyle durduruldu. Tekrar denemeden önce mevcut adımı NurCRM'de kontrol edin.",
                    "⚠ To'plam xato sabab to'xtatildi. Takrorlashdan oldin joriy qadamni NurCRM'da tekshiring."));
                stopped = true;
                break;
            }
            lines.Add((r.Partial ? "⚠ " : r.Ok ? "✓ " : "✗ ") + r.Message);
            if (r.Partial) partial++;
            else if (r.Ok) completed++;
            else failed++;
            if (r.Ok && step.Op is not ("open" or "open_section" or "create") && step.Product.Id.Length > 0)
                changed.Add(step.Product.Id);
            if (r.Ok && step.Op == "create")
                changed.Add("name:" + step.Product.Title);
        }
        // 2026-10-06, владелец: «чтобы ИИ показывал наглядно изменения» — склад с изменёнными товарами.
        if (changed.Count > 0)
            ShowChangedProducts?.Invoke(changed.Distinct().ToList(), string.Join("\n", lines));
        var summary = T(
            $"Итог: выполнено {completed}, частично {partial}, с ошибкой {failed}" + (stopped ? "; пакет остановлен." : "."),
            $"Жыйынтык: аткарылды {completed}, жарым-жартылай {partial}, ката {failed}" + (stopped ? "; топтом токтотулду." : "."),
            $"Result: {completed} completed, {partial} partial, {failed} failed" + (stopped ? "; batch stopped." : "."),
            $"Sonuç: {completed} tamamlandı, {partial} kısmi, {failed} başarısız" + (stopped ? "; işlem durduruldu." : "."),
            $"Natija: {completed} bajarildi, {partial} qisman, {failed} xato" + (stopped ? "; to'plam to'xtatildi." : "."));
        return summary + (lines.Count > 0 ? "\n" + string.Join("\n", lines) : "");
    }

    // ── бот: ожидающие подтверждения (кнопки «Выполнить / Отмена» или ответ «да / нет») ──

    private sealed record Pending(string ChatId, List<Step> Steps, DateTime CreatedUtc);

    private static readonly ConcurrentDictionary<string, Pending> PendingByToken = new();
    private static readonly ConcurrentDictionary<string, string> TokenByChat = new();
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Запомнить действия для чата владельца; ответ — короткий ключ для кнопок.</summary>
    public static string Remember(string chatId, List<Step> steps)
    {
        foreach (var old in PendingByToken.Where(p => DateTime.UtcNow - p.Value.CreatedUtc > PendingLifetime).Select(p => p.Key).ToList())
            PendingByToken.TryRemove(old, out _);
        var token = Guid.NewGuid().ToString("N")[..10];
        PendingByToken[token] = new Pending(chatId, steps, DateTime.UtcNow);
        TokenByChat[chatId] = token;
        return token;
    }

    /// <summary>Забрать действия по ключу (кнопка) — только для того же чата и не старше 15 минут.</summary>
    public static List<Step>? Take(string token, string chatId)
    {
        if (!PendingByToken.TryRemove(token, out var p) || p.ChatId != chatId || DateTime.UtcNow - p.CreatedUtc > PendingLifetime)
            return null;
        TokenByChat.TryRemove(chatId, out _);
        return p.Steps;
    }

    /// <summary>Последние ожидающие действия чата (ответ «да» / «нет» текстом или голосом).</summary>
    public static string? PendingToken(string chatId) =>
        TokenByChat.TryGetValue(chatId, out var token) && PendingByToken.TryGetValue(token, out var p) && DateTime.UtcNow - p.CreatedUtc <= PendingLifetime ? token : null;

    public static bool IsYes(string text)
    {
        var t = text.Trim().ToLowerInvariant().TrimEnd('.', '!');
        return t is "да" or "ага" or "выполни" or "выполнить" or "подтверждаю" or "давай" or "ок" or "ok" or "ооба" or "макул" or "yes" or "evet" or "ha";
    }

    public static bool IsNo(string text)
    {
        var t = text.Trim().ToLowerInvariant().TrimEnd('.', '!');
        return t is "нет" or "отмена" or "не надо" or "отменить" or "жок" or "no" or "hayır" or "yo'q";
    }
}
