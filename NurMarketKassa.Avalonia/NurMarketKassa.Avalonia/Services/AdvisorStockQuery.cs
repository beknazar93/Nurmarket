using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-30, владелец: «на команду „найди товары с 0 остатком“ Нур Советник отвечает „не могу“». Списки и поиск по складу
/// выполняет ПРОГРАММА локально по каталогу (без нейросети, без отправки каталога в ИИ, на 15 000 товаров — миллисекунды):
/// нулевой и отрицательный остаток, заканчивается, без штрихкода / цены / закупки / категории, цена ниже закупки, дубли штрихкодов и
/// названий, «дорогие товары», топ по остатку / стоимости остатка, товары категории / бренда, цена и остаток «дешевле / дороже / от … до …».
/// Распознавание — по шаблонам (ё=е, кыргызские и английские слова). Тумблер реестра: «read.stocklists». Закупка и стоимость остатка —
/// только если разрешено <c>showMoney</c> (WarehouseWindow.RevisionShowPurchase); иначе запрос про закупку отклоняется.
/// 2026-10-30 (приёмка 1.17.66): фраза разбирается по ТИПАМ — остаток / цена / закупка / дата / категория: даты («с 01.10», «за октябрь»)
/// и числа с «дороже / до / от» вырезаются до поиска слов и больше не путаются с «с 0» и «дорогие»; «нулевой» берёт тот тип, рядом с
/// которым стоит («нулевой ценой» → цена, «0 остатком» → остаток); неясное («с нулём», «до 100») и разные типы в одной фразе —
/// не угадываются, а уточняются кнопками (<see cref="Kind.Clarify"/>). Опечатки в один знак и неверная раскладка тоже понимаются.</summary>
public static class AdvisorStockQuery
{
    public enum Kind { Zero, Negative, Low, NoBarcode, NoPrice, NoPurchase, NoCategory, BelowCost, DupBarcode, DupName, TopPrice, TopQty, TopValue, Category, Brand, PriceRange, QtyRange, Clarify }

    /// <summary>Разобранный запрос. <paramref name="Options"/> — для <see cref="Kind.Clarify"/>: варианты, из которых выбирает владелец.
    /// <paramref name="DateIgnored"/> — во фразе была дата, а списки по дате не строятся (дата не учтена).</summary>
    public sealed record Parsed(Kind Kind, string? Arg = null, double? A = null, double? B = null, Parsed[]? Options = null, bool DateIgnored = false);

    /// <param name="Title">Заголовок («Товары с нулевым остатком»).</param>
    /// <param name="Total">Сколько товаров подошло.</param>
    /// <param name="Text">Сообщение для чата: заголовок + таблица Markdown (первые <see cref="Shown"/> строк).</param>
    /// <param name="FullTable">Все строки (до 5000) — для «Скачать в Excel».</param>
    /// <param name="NavId">Раздел/фильтр склада для «Показать все на складе» (warehouse.out, warehouse.low или warehouse).</param>
    /// <param name="NeedsMoney">Запрос требует права на закупку, а его нет — Text содержит отказ.</param>
    /// <param name="Options">Не null — это уточняющий вопрос (Text), варианты — кнопками.</param>
    public sealed record Result(Kind Kind, string Title, int Total, string Text, string FullTable, string NavId, bool CanOrder, bool NeedsMoney, Parsed[]? Options = null);

    public const int Shown = 15;
    private const int ExcelMax = 5000;

    private static string T(string ru, string ky, string en, string tr, string uz) => NurMarketKassa.Services.Tr.T(ru, ky, en, tr, uz);

    private static string Fold(string? s) => NurMarketKassa.Services.AdvisorNavRouter.Fold(s);

    private static string[] F(params string[] words) => words.Select(Fold).ToArray();

    private static bool HasAny(string q, string[] foldedWords) => foldedWords.Any(q.Contains);

    // ── Словари (свёрнуты Fold: ё=е, ү=у, ө=о, ң=н, ş=s…) ─────────────────────────────────────────────────────────────
    private static readonly string[] NavWords = F("откро", "открой", "открыть", "перейд", "зайди", "ачып", "ачкыла", "open ");
    private static readonly string[] ActionWords = F("поставь", "измени", "спиши", "оприход", "добавь", "создай", "удали", "акци", "заказать", "заказ ", "продаж", "выручк", "прибыл");
    // 2026-10-30 (приёмка 1.17.67, п. 6а): вопросы про продажи/чеки/клиентов/долги с «больше 100», «нулевой» — не списки склада.
    // Слова склада (остаток, склад, наличие…) возвращают разбор: «что продали и какой остаток» — как раньше. «Товар» склад-словом не считается.
    private static readonly string[] SalesRoots = F("продал", "продан", "продав", "чек", "выручк", "касс", "смен", "покупател", "клиент", "долг", "расход",
        "сатыл", "сатты", "сатуу", "түшүм", "кардар", "карыз", "sotil", "sotd", "sotuv", "mijoz", "qarz", "satil", "satis", "fis", "musteri", "borc", "sold", "sale", "receipt", "customer", "debt", "revenue");
    private static readonly string[] WarehouseCtxRoots = F("склад", "остат", "налич", "калдык", "калдыг", "кампа", "qoldiq", "qoldig", "ombor", "stock", "stok", "depo", "mevcut", "warehouse", "inventor");
    private static readonly string[] AskWords = F("найди", "найти", "покажи", "выведи", "вывести", "список", "какие", "какой", "дай ", "сколько", "перечисли", "есть ли", "что ", "тап", "көрсөт", "тизм",
        "list", "show", "find", "which", "how many", "what", "tovar", "mahsulot", "топ ", "top ", "самые", "самых");
    private static readonly string[] AskRoots = F("найд", "покаж", "выведи", "перечисл", "списк", "какие", "сколько", "көрсөт", "show", "find", "which");
    private static readonly string[] GoodsRoots = F("товар", "позици", "продукт", "склад", "цен", "закуп", "штрих", "категор", "остат", "наличи", "тауар", "каттам", "калдык", "калдыг", "баа",
        "product", "item", "stock", "urun", "mahsulot", "ombor", "barcode", "barkod", "price", "brand", "бренд", "qoldiq", "qoldig");

    private static readonly string[] StockRoots = F("остат", "налич", "калдык", "калдыг", "qoldiq", "qoldig", "stock", "stok", "mevcut", "количеств", "штук", "dona", "даана");
    private static readonly string[] StockExact = F("шт", "pcs", "pc");
    private static readonly string[] PriceRoots = F("цен", "баа", "стоимост", "price", "narx", "fiyat");
    private static readonly string[] PriceExact = F("сом", "som");
    private static readonly string[] CostRoots = F("закуп", "себестоим", "purchase", "xarid", "alis", "cost");
    private static readonly string[] BarcodeRoots = F("штрих", "баркод", "barcode", "barkod", "shtrix");
    private static readonly string[] CategoryRoots = F("категор", "kategori", "uncategor");
    private static readonly string[] ZeroRoots = F("нул", "нол", "zero", "sifir", "nol");
    // «закончились», «нет в наличии» — нулевой остаток сам по себе, без слова «ноль»
    private static readonly string[] GoneRoots = F("закончил", "закончен", "кончил", "тугон", "tugagan", "tugadi", "распродан");
    private static readonly string[] GonePhrases = F("нет в наличии", "калдыгы жок", "out of stock", "sold out", "пустые", "пустой остаток", "zero stock", "остатка нет", "остатков нет");
    private static readonly string[] LowRoots = F("заканчива", "кончает", "кончают", "азайып", "tugayotgan");
    private static readonly string[] LowPhrases = F("мало остал", "малый остаток", "малые остатки", "низкий остаток", "низкие остатки", "running low", "low stock");
    private static readonly string[] NegativeRoots = F("отрицательн", "терс", "negative", "manfiy", "olumsuz", "минусов");
    private static readonly string[] NegativePhrases = F("в минусе", "минус на склад", "терс калдык");
    private static readonly string[] BelowCostPhrases = F("ниже закупк", "ниже себестоим", "в минус продаем", "в минус продаём", "убыточн", "дешевле закупк", "below cost", "цена ниже");
    private static readonly string[] DupRoots = F("дубл", "одинаков", "duplicate", "кайталан", "takror");
    private static readonly string[] ValueByWord = F("заморожен", "дорогие остатки", "дорогих остатк", "по закупк", "денег", "money", "frozen", "акча");
    private static readonly string[] TopQtyWords = F("больше всего", "самых много", "самое большое", "наибольш", "топ ", "top ", "кампада эң көп", "most in stock");
    private static readonly string[] ExpensiveRoots = F("дорог", "expensive", "кымбат", "qimmat", "pahali");
    private static readonly string[] NoBarcodePhrases = F("штрихкода нет", "штрихкоду жок", "kodsiz", "kodsuz", "кодсуз", "кодсыз", "no barcode", "without barcode");
    private static readonly string[] NegTokens = F("без", "нет", "нету", "нема", "жок", "no", "without", "missing", "yok", "yoq", "отсутств");
    private static readonly string[] NotWords = F("указан", "задан", "заполнен", "установлен");
    private static readonly string[] LackSuffixes = F("суз", "сыз", "сиз", "siz", "suz", "syz");

    // ── Даты (в списках склада не поддержаны — вырезаются, чтобы «с 01.10» не стало «с 0») ─────────────────────────────
    private const string MonthNames = @"январ\w*|феврал\w*|март\w*|апрел\w*|ма[яй]\b|июн\w*|июл\w*|август\w*|сентябр\w*|октябр\w*|ноябр\w*|декабр\w*|january|february|march|april|june|july|august|september|october|november|december";
    private static readonly Regex DateRx = new(
        @"(?<![\d.,/])(?:0[1-9]|[12]\d|3[01])[./](?:0[1-9]|1[0-2])(?:[./](?:\d{4}|\d{2}))?(?![\d]|[.,]\d)"      // 01.10  15.10.2026
        + @"|(?<![\d.,/])\d{1,2}[./](?:0?[1-9]|1[0-2])[./](?:\d{4}|\d{2})(?!\d)"                                  // 1.10.26
        + @"|\b\d{1,2}\s*(?:числа|число|chisla)\b"                                                                  // до 15 числа
        + @"|\b(?:\d{1,2}\s*)?(?:" + MonthNames + @")"                                                              // 1 октября, за октябрь
        + @"|\b(?:вчера|позавчера|сегодн\w*|за\s+неделю|за\s+месяц|за\s+день|на\s+этой\s+неделе|на\s+прошлой\s+неделе|yesterday|today|last\s+week|this\s+week|last\s+month|this\s+month|кечээ|бугун|kecha|bugun)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Числовые границы: «дороже 500», «до 100 шт», «от 10 до 50» ───────────────────────────────────────────────────
    private const string Num = @"(\d{1,7}(?: \d{3})*(?:[.,]\d+)?)";
    private const string L = @"(?<![\p{L}])";
    private static readonly Regex[] BetweenRx =
    {
        new(L + @"(?:от|from|between|между|с)\s*" + Num + @"\s*(?:до|to|and|и|по|-)\s*" + Num + @"(?!\d)", RegexOptions.Compiled),
        new(Num + @"\s*(?:дан|ден|дон|тан|тен|тон|dan|den)\s*" + Num + @"\s*(?:га|ге|го|ка|ке|ко|ga|ge)?\s*(?:чейин|gacha|kadar|arasi)", RegexOptions.Compiled),
        new(Num + @"\s*(?:ile|ve)\s*" + Num + @"\s*arasi", RegexOptions.Compiled),
        new(@"(?<![\d.,-])" + Num + @"\s*-\s*" + Num + @"(?![\d-])", RegexOptions.Compiled),
    };
    private static readonly Regex NotMoreRx = new(L + @"(?:не\s+(?:более|больше|дороже|выше|свыше)|at\s+most|max(?:imum)?|<=)\s*" + Num, RegexOptions.Compiled);
    private static readonly Regex NotLessRx = new(L + @"(?:не\s+(?:менее|меньше|дешевле|ниже)|at\s+least|min(?:imum)?|>=)\s*" + Num, RegexOptions.Compiled);
    private static readonly Regex MoreRx = new(
        L + @"(?:дороже|больше|свыше|более|выше|от|above|over|more\s+than|greater\s+than|>)\s*" + Num
        + @"|" + Num + @"\s*(?:дан|ден|дон|тан|тен|тон)\s*(?:жогору|ашык|кымбат|артык)"
        + @"|" + Num + @"\s*(?:dan|den)\s*(?:qimmat|kop|yuqori|ortiq|fazla|pahali|buyuk|cok)", RegexOptions.Compiled);
    private static readonly Regex LessRx = new(
        L + @"(?:дешевле|меньше|менее|ниже|до|under|below|less\s+than|up\s+to|<)\s*" + Num
        + @"|" + Num + @"\s*(?:дан|ден|дон|тан|тен|тон)\s*(?:томон|аз|арзан|кем)"
        + @"|" + Num + @"\s*(?:dan|den)\s*(?:arzon|kam|past|az|ucuz|kucuk)", RegexOptions.Compiled);
    // слова, которые сами говорят «это про цену»
    private static readonly Regex PriceCmpRx = new(@"дороже|дешевле|не\s+дороже|не\s+дешевле|кымбат|арзан|qimmat|arzon|pahali|ucuz", RegexOptions.Compiled);

    private static readonly Regex CategoryRx = new(@"(?:в категории|из категории|категории|категория|категориясы|category|kategoriya|kategori)\s+([^,.?!]{2,40})", RegexOptions.Compiled);
    private static readonly Regex BrandRx = new(@"(?:бренда?|марки|марка|производителя|брендинин|brand|marka|brend)\s+([^,.?!]{2,40})", RegexOptions.Compiled);
    private static readonly Regex ArgCutRx = new(@"\s+(?:с|со|и|где|у которых|которые|без|на складе|остат\w*|нулев\w*|дороже|дешевле|with|and|where)\b.*$", RegexOptions.Compiled);

    private static List<string> Tok(string q) =>
        Regex.Matches(q, @"\d+(?:[.,]\d+)?|\p{L}+").Select(m => m.Value).ToList();

    // ── Нечёткое сравнение слова с корнем: корень в начале слова или отличается одним знаком (опечатка) ───────────────────
    private static bool Within1(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }
            if (++edits > 1)
                return false;
            if (a.Length == b.Length && i + 1 < a.Length && a[i] == b[j + 1] && a[i + 1] == b[j])
            {
                i += 2;
                j += 2; // две соседние буквы местами
            }
            else if (a.Length > b.Length)
                i++;
            else if (a.Length < b.Length)
                j++;
            else
            {
                i++;
                j++;
            }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    private static bool Stem(string tok, string root)
    {
        if (tok.StartsWith(root, StringComparison.Ordinal))
            return true;
        if (root.Length < 5 || tok.Length < root.Length - 1)
            return false;
        for (var len = root.Length - 1; len <= root.Length + 1 && len <= tok.Length; len++)
            if (Within1(tok.AsSpan(0, len), root))
                return true;
        return false;
    }

    private static bool StemAny(string tok, string[] roots) => roots.Any(r => Stem(tok, r));

    private static bool IsStockTok(string t) => StemAny(t, StockRoots) || StockExact.Contains(t);
    private static bool IsPriceTok(string t) => StemAny(t, PriceRoots) || PriceExact.Contains(t);
    private static bool IsCostTok(string t) => StemAny(t, CostRoots);

    private static List<int> Idx(List<string> toks, Func<string, bool> pred) =>
        Enumerable.Range(0, toks.Count).Where(i => pred(toks[i])).ToList();

    private static bool HitAny(List<string> toks, string[] roots) => toks.Any(t => StemAny(t, roots));

    private static List<int> NegIdx(List<string> toks)
    {
        var res = new List<int>();
        for (var i = 0; i < toks.Count; i++)
        {
            if (NegTokens.Any(n => toks[i] == n || n.Length >= 6 && toks[i].StartsWith(n, StringComparison.Ordinal)))
                res.Add(i);
            else if (toks[i] == "не" && i + 1 < toks.Count && NotWords.Any(w => toks[i + 1].StartsWith(w, StringComparison.Ordinal)))
                res.Add(i);
        }
        return res;
    }

    /// <summary>«Нет X» / «без X» / «Xсыз»: слово-отрицание не дальше двух слов от существительного либо суффикс «-сыз / -siz».</summary>
    private static bool Lacks(List<string> toks, params string[][] rootSets)
    {
        var roots = rootSets.SelectMany(x => x).ToArray();
        var neg = NegIdx(toks);
        for (var j = 0; j < toks.Count; j++)
        {
            if (!StemAny(toks[j], roots))
                continue;
            if (LackSuffixes.Any(s => toks[j].EndsWith(s, StringComparison.Ordinal)) && toks[j].Length >= 6)
                return true;
            if (neg.Any(i => Math.Abs(i - j) <= 2))
                return true;
        }
        return false;
    }

    private const string RuRow = "йцукенгшщзхъфывапролджэячсмитьбю";
    private const string EnRow = "qwertyuiop[]asdfghjkl;'zxcvbnm,.";

    private static string Remap(string s, string from, string to)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var i = from.IndexOf(c);
            sb.Append(i >= 0 ? to[i] : c);
        }
        return sb.ToString();
    }

    /// <summary>Фраза набрана не в той раскладке («yfqlb njdfhs» вместо «найди товары»): английские буквы → русские и обратно.</summary>
    private static IEnumerable<string> LayoutVariants(string folded)
    {
        var cyr = folded.Count(c => c is >= 'Ѐ' and <= 'ӿ');
        var lat = folded.Count(c => c is >= 'a' and <= 'z');
        if (cyr == 0 && lat >= 4)
            yield return Remap(folded, EnRow, RuRow);
        else if (lat == 0 && cyr >= 4)
            yield return Remap(folded, RuRow, EnRow);
    }

    /// <summary>Распознать запрос. null — это не запрос к складу (отвечает обычный путь).</summary>
    public static Parsed? Parse(string? question)
    {
        if (ParseOne(question) is { } p)
            return p;
        foreach (var alt in LayoutVariants(Fold(question)))
            if (ParseOne(alt) is { } p2)
                return p2;
        return null;
    }

    private enum Unit { Unknown, Price, Qty }

    private static Unit UnitOf(string q, Match m)
    {
        if (PriceCmpRx.IsMatch(m.Value))
            return Unit.Price;
        var from = Math.Max(0, m.Index - 28);
        var to = Math.Min(q.Length, m.Index + m.Length + 28);
        var win = Tok(q.Substring(from, to - from));
        bool p = win.Any(IsPriceTok), s = win.Any(IsStockTok);
        if (p == s)
        {
            var all = Tok(q);
            p = all.Any(IsPriceTok);
            s = all.Any(IsStockTok);
        }
        return p == s ? Unit.Unknown : p ? Unit.Price : Unit.Qty;
    }

    private static double? Num2(string s) =>
        double.TryParse(s.Replace(" ", "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Blank(string q, Match m) => q.Remove(m.Index, m.Length).Insert(m.Index, new string(' ', m.Length));

    /// <summary>Находит границы («дороже 500», «до 100», «от 10 до 50»), вырезает их из строки и кладёт кандидатов (цена / остаток / оба — если неясно).</summary>
    private static string TakeRanges(string q, List<Parsed> cands, out bool found)
    {
        double? a = null, b = null;
        var units = new List<Unit>();
        void Take(Regex rx, int role) // 0 — между, 1 — «не меньше» (A), 2 — «не больше» (B)
        {
            while (true)
            {
                var m = rx.Match(q);
                if (!m.Success)
                    return;
                var nums = m.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => Num2(g.Value)).ToList();
                q = Blank(q, m);
                if (nums.Count == 0 || nums[0] is null)
                    continue;
                units.Add(UnitOf(q, m));
                if (role == 0 && nums.Count >= 2 && nums[1] is not null)
                {
                    var lo = Math.Min(nums[0]!.Value, nums[1]!.Value);
                    var hi = Math.Max(nums[0]!.Value, nums[1]!.Value);
                    a ??= lo;
                    b ??= hi;
                }
                else if (role == 1)
                    a ??= nums[0];
                else if (role == 2)
                    b ??= nums[0];
            }
        }
        foreach (var rx in BetweenRx)
            Take(rx, 0);
        Take(NotMoreRx, 2);
        Take(NotLessRx, 1);
        Take(MoreRx, 1);
        Take(LessRx, 2);
        found = units.Count > 0;
        if (!found)
            return q;
        var known = units.Where(u => u != Unit.Unknown).Distinct().ToList();
        if (known.Count == 1)
            cands.Add(new Parsed(known[0] == Unit.Price ? Kind.PriceRange : Kind.QtyRange, null, a, b));
        else
        {
            cands.Add(new Parsed(Kind.PriceRange, null, a, b));
            cands.Add(new Parsed(Kind.QtyRange, null, a, b));
        }
        return q;
    }

    private static Parsed? ParseOne(string? text)
    {
        var q = Fold(text);
        if (q.Length < 4 || q.Length > 200)
            return null;
        // «Открой …» / «перейди …» — это переход, им занимается AdvisorNavRouter.
        if (HasAny(q, NavWords))
            return null;
        // Просьбы изменить (поставь цену, спиши) и вопросы про заказ/акции/продажи — не списки склада.
        if (HasAny(q, ActionWords))
            return null;
        var dateIgnored = false;
        q = DateRx.Replace(q, _ =>
        {
            dateIgnored = true;
            return " ";
        });
        var all = Tok(q);
        if (HitAny(all, SalesRoots) && !HitAny(all, WarehouseCtxRoots))
            return null;
        var ask = HasAny(q, AskWords) || all.Any(t => t.Length >= 5 && StemAny(t, AskRoots));
        var goods = HitAny(all, GoodsRoots);
        if (!ask && !goods)
            return null;

        var cands = new List<Parsed>();
        q = TakeRanges(q, cands, out var comparator);
        var toks = Tok(q);
        var stockIdx = Idx(toks, IsStockTok);
        var priceIdx = Idx(toks, IsPriceTok);
        var costIdx = Idx(toks, IsCostTok);
        // 2026-10-30: «больше 100» / «нулевой» без единого слова про товар, остаток, цену, закупку или склад — не склад (приёмка 1.17.67, п. 6а).
        if (!goods && stockIdx.Count == 0 && priceIdx.Count == 0 && costIdx.Count == 0 && !PriceCmpRx.IsMatch(q) && !HitAny(toks, ExpensiveRoots)
            && !HasAny(q, GonePhrases) && !HitAny(toks, GoneRoots) && !HitAny(toks, NegativeRoots) && !HitAny(toks, LowRoots) && !HasAny(q, LowPhrases)
            && (comparator || Idx(toks, t => t == "0" || StemAny(t, ZeroRoots)).Count > 0))
            return null;

        // Нулевое: «нулевой / 0 / ноль / zero» берёт тот тип, рядом с которым стоит.
        var zeroIdx = Idx(toks, t => t == "0" || StemAny(t, ZeroRoots));
        var gone = HasAny(q, GonePhrases) || HitAny(toks, GoneRoots) || Lacks(toks, StockRoots, F("склад"));
        foreach (var z in zeroIdx)
        {
            int Dist(List<int> l) => l.Count == 0 ? int.MaxValue : l.Min(i => Math.Abs(i - z));
            var d = new[] { (Kind.Zero, Dist(stockIdx)), (Kind.NoPrice, Dist(priceIdx)), (Kind.NoPurchase, Dist(costIdx)) };
            var best = d.Min(x => x.Item2);
            if (best <= 3)
                foreach (var x in d.Where(x => x.Item2 == best))
                    cands.Add(new Parsed(x.Item1));
            else if (!gone)
            {
                cands.Add(new Parsed(Kind.Zero));
                cands.Add(new Parsed(Kind.NoPrice));
            }
        }
        if (gone)
            cands.Add(new Parsed(Kind.Zero));

        if (HitAny(toks, NegativeRoots) || HasAny(q, NegativePhrases))
            cands.Add(new Parsed(Kind.Negative));
        if (HitAny(toks, LowRoots) || HasAny(q, LowPhrases) || toks.Any(t => t is "мало" or "низк" or "низкий" or "низкие") && stockIdx.Count > 0)
            cands.Add(new Parsed(Kind.Low));
        if (Lacks(toks, BarcodeRoots) || HasAny(q, NoBarcodePhrases))
            cands.Add(new Parsed(Kind.NoBarcode));
        if (Lacks(toks, PriceRoots))
            cands.Add(new Parsed(Kind.NoPrice));
        if (Lacks(toks, CostRoots))
            cands.Add(new Parsed(Kind.NoPurchase));
        var noCategory = Lacks(toks, CategoryRoots);
        if (noCategory)
            cands.Add(new Parsed(Kind.NoCategory));
        if (HasAny(q, BelowCostPhrases))
            cands.Add(new Parsed(Kind.BelowCost));
        if (HitAny(toks, DupRoots))
            cands.Add(new Parsed(HitAny(toks, BarcodeRoots) ? Kind.DupBarcode : Kind.DupName));
        var topValue = HasAny(q, ValueByWord) || q.Contains(Fold("стоимост")) && zeroIdx.Count == 0 && !comparator && !Lacks(toks, CostRoots);
        if (topValue)
            cands.Add(new Parsed(Kind.TopValue));
        if (!topValue && HasAny(q, TopQtyWords) && (stockIdx.Count > 0 || q.Contains(Fold("склад"))))
            cands.Add(new Parsed(Kind.TopQty));
        if (!topValue && HitAny(toks, ExpensiveRoots))
            cands.Add(new Parsed(Kind.TopPrice));
        if (!noCategory && CategoryRx.Match(q) is { Success: true } cat && CutArg(cat.Groups[1].Value) is { } catArg)
            cands.Add(new Parsed(Kind.Category, catArg));
        if (BrandRx.Match(q) is { Success: true } brand && CutArg(brand.Groups[1].Value) is { } brandArg)
            cands.Add(new Parsed(Kind.Brand, brandArg));

        // «дороже 500» уже цена — «самые дорогие» к ней не добавляется
        if (cands.Any(c => c.Kind is Kind.PriceRange))
            cands.RemoveAll(c => c.Kind == Kind.TopPrice);
        var distinct = cands.GroupBy(c => c.Kind).Select(g => g.First()).OrderBy(c => c.Kind).ToList();
        if (distinct.Count == 0)
            return null;
        if (distinct.Count == 1)
            return distinct[0] with { DateIgnored = dateIgnored };
        return new Parsed(Kind.Clarify, null, null, null, distinct.Take(4).ToArray(), dateIgnored);
    }

    private static string? CutArg(string raw)
    {
        var arg = ArgCutRx.Replace(raw.Trim(), "").Trim();
        return arg.Length >= 2 ? arg : null;
    }

    private static string Range(double? a, double? b)
    {
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        if (a is { } x && b is { } y)
            return T($"от {N(x)} до {N(y)}", $"{N(x)}дан {N(y)}га чейин", $"from {N(x)} to {N(y)}", $"{N(x)}–{N(y)} arası", $"{N(x)} dan {N(y)} gacha");
        if (a is { } lo)
            return T($"от {N(lo)}", $"{N(lo)}дан жогору", $"from {N(lo)}", $"{N(lo)} ve üzeri", $"{N(lo)} dan yuqori");
        if (b is { } hi)
            return T($"до {N(hi)}", $"{N(hi)}га чейин", $"up to {N(hi)}", $"{N(hi)} ve altı", $"{N(hi)} gacha");
        return "";
    }

    /// <summary>Короткое название варианта для вопроса «Вы имеете в виду…» и для кнопки.</summary>
    public static string Label(Parsed p) => p.Kind switch
    {
        Kind.Zero => T("нулевой остаток", "нөл калдык", "zero stock", "sıfır stok", "nol qoldiq"),
        Kind.Negative => T("отрицательный остаток", "терс калдык", "negative stock", "negatif stok", "manfiy qoldiq"),
        Kind.Low => T("заканчивается", "түгөнүп жатат", "running low", "azalıyor", "tugayapti"),
        Kind.NoBarcode => T("без штрихкода", "штрихкодсуз", "no barcode", "barkodsuz", "shtrix-kodsiz"),
        Kind.NoPrice => T("нулевая цена", "нөл баа", "zero price", "sıfır fiyat", "nol narx"),
        Kind.NoPurchase => T("нулевая закупка", "нөл сатып алуу баасы", "zero purchase price", "sıfır alış fiyatı", "nol xarid narxi"),
        Kind.NoCategory => T("без категории", "категориясыз", "no category", "kategorisiz", "kategoriyasiz"),
        Kind.BelowCost => T("цена ниже закупки", "баасы сатып алуудан төмөн", "price below purchase", "fiyat alışın altında", "narx xariddan past"),
        Kind.DupBarcode => T("дубли штрихкодов", "кайталанган штрихкоддор", "duplicate barcodes", "yinelenen barkodlar", "takroriy shtrix-kodlar"),
        Kind.DupName => T("дубли названий", "кайталанган аталыштар", "duplicate names", "yinelenen adlar", "takroriy nomlar"),
        Kind.TopPrice => T("самые дорогие", "эң кымбат", "most expensive", "en pahalı", "eng qimmat"),
        Kind.TopQty => T("больше всего на складе", "кампада эң көп", "most in stock", "depoda en çok", "omborda eng ko'p"),
        Kind.TopValue => T("больше всего денег на складе", "кампада эң көп акча", "most money in stock", "depoda en çok para", "omborda eng ko'p pul"),
        Kind.Category => T($"категория «{p.Arg}»", $"«{p.Arg}» категориясы", $"category “{p.Arg}”", $"«{p.Arg}» kategorisi", $"«{p.Arg}» kategoriyasi"),
        Kind.Brand => T($"бренд «{p.Arg}»", $"«{p.Arg}» бренди", $"brand “{p.Arg}”", $"«{p.Arg}» markası", $"«{p.Arg}» brendi"),
        Kind.PriceRange => T("цена ", "баасы ", "price ", "fiyat ", "narx ") + Range(p.A, p.B),
        Kind.QtyRange => T("остаток ", "калдыгы ", "stock ", "stok ", "qoldiq ") + Range(p.A, p.B),
        _ => "",
    };

    /// <summary>«Вы имеете в виду: нулевой остаток или нулевая цена?» — конфликтная или неясная формулировка.</summary>
    public static string ClarifyText(IReadOnlyList<Parsed> options)
    {
        var labels = options.Select(Label).ToList();
        var last = labels[^1];
        var head = string.Join(", ", labels.Take(labels.Count - 1));
        return T($"Вы имеете в виду: {head} или {last}?", $"Сиз эмнени айтып жатасыз: {head} же {last}?", $"Do you mean: {head} or {last}?",
            $"Şunu mu kastediyorsunuz: {head} mı yoksa {last} mı?", $"Siz nimani nazarda tutyapsiz: {head} yoki {last}?");
    }

    private sealed record Row(CatalogProductTileVm Tile, double Qty, double Price, double Cost);

    /// <summary>Выполнить запрос по каталогу. <paramref name="showMoney"/> — можно ли показывать закупку и стоимость остатка.</summary>
    public static Result? Run(string? question, IReadOnlyList<CatalogProductTileVm> tiles, bool showMoney) =>
        Parse(question) is { } p ? Run(p, tiles, showMoney) : null;

    /// <summary>Выполнить уже разобранный запрос (в том числе вариант, выбранный кнопкой уточнения).</summary>
    public static Result? Run(Parsed p, IReadOnlyList<CatalogProductTileVm> tiles, bool showMoney)
    {
        // 2026-10-30 (приёмка 1.17.66): тумблер «Списки склада» проверяется и здесь, в точке исполнения (не только в окне).
        if (!NurMarketKassa.Services.AdvisorCapabilities.IsOn("read.stocklists"))
            return null;
        if (p.Kind == Kind.Clarify && p.Options is { Length: > 1 } options)
            return new Result(Kind.Clarify, "", 0, ClarifyText(options), "", "warehouse", false, false, options);
        var rows = new List<Row>(tiles.Count);
        foreach (var t in tiles)
            if (!t.IsService)
                rows.Add(new Row(t, t.Quantity, t.PriceValue, t.PurchasePrice));
        var needsMoney = p.Kind is Kind.NoPurchase or Kind.BelowCost or Kind.TopValue;
        if (needsMoney && !showMoney)
            return new Result(p.Kind, "", 0, T("Закупочные цены и стоимость склада видят только владелец и сотрудники с правом на закупку.", "Сатып алуу баасын жана кампанын наркын ээси жана тиешелүү укугу бар кызматкерлер гана көрөт.",
                "Purchase prices and stock value are visible only to the owner and staff with the purchase right.", "Alış fiyatlarını ve stok değerini yalnızca sahip ve alış yetkisi olan çalışanlar görür.", "Xarid narxi va ombor qiymatini faqat egasi va tegishli ruxsati bor xodimlar ko'radi."), "", "warehouse", false, true);

        IEnumerable<Row> found;
        string title;
        string nav = "warehouse";
        var canOrder = false;
        switch (p.Kind)
        {
            case Kind.Zero:
                found = rows.Where(r => Math.Abs(r.Qty) < 1e-9).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T("Товары с нулевым остатком", "Калдыгы нөл товарлар", "Products with zero stock", "Stoku sıfır olan ürünler", "Qoldig'i nol mahsulotlar");
                nav = "warehouse.out"; canOrder = true; break;
            case Kind.Negative:
                found = rows.Where(r => r.Qty < -1e-9).OrderBy(r => r.Qty);
                title = T("Товары с отрицательным остатком", "Калдыгы терс товарлар", "Products with negative stock", "Stoku negatif ürünler", "Qoldig'i manfiy mahsulotlar"); break;
            case Kind.Low:
                found = rows.Where(r => r.Qty > 1e-9 && r.Qty < CatalogProductTileVm.LowStockQuantityThreshold).OrderBy(r => r.Qty);
                title = T($"Заканчиваются (остаток меньше {CatalogProductTileVm.LowStockQuantityThreshold:0})", $"Түгөнүп жатат (калдыгы {CatalogProductTileVm.LowStockQuantityThreshold:0}дан аз)", $"Running low (stock under {CatalogProductTileVm.LowStockQuantityThreshold:0})",
                    $"Azalıyor (stok {CatalogProductTileVm.LowStockQuantityThreshold:0}'dan az)", $"Tugayapti (qoldiq {CatalogProductTileVm.LowStockQuantityThreshold:0} dan kam)");
                nav = "warehouse.low"; canOrder = true; break;
            case Kind.NoBarcode:
                found = rows.Where(r => string.IsNullOrWhiteSpace(r.Tile.Barcode)).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T("Товары без штрихкода", "Штрихкодсуз товарлар", "Products without a barcode", "Barkodsuz ürünler", "Shtrix-kodsiz mahsulotlar"); break;
            case Kind.NoPrice:
                found = rows.Where(r => r.Price <= 0).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T("Товары без цены продажи", "Сатуу баасы жок товарлар", "Products without a sale price", "Satış fiyatı olmayan ürünler", "Sotuv narxi yo'q mahsulotlar"); break;
            case Kind.NoPurchase:
                found = rows.Where(r => r.Cost <= 0).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T("Товары без закупочной цены", "Сатып алуу баасы жок товарлар", "Products without a purchase price", "Alış fiyatı olmayan ürünler", "Xarid narxi yo'q mahsulotlar"); break;
            case Kind.NoCategory:
                found = rows.Where(r => string.IsNullOrWhiteSpace(r.Tile.Category)).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T("Товары без категории", "Категориясыз товарлар", "Products without a category", "Kategorisiz ürünler", "Kategoriyasiz mahsulotlar"); break;
            case Kind.BelowCost:
                found = rows.Where(r => r.Cost > 0 && r.Price > 0 && r.Price < r.Cost).OrderBy(r => r.Price - r.Cost);
                title = T("Цена продажи ниже закупки", "Сатуу баасы сатып алуудан төмөн", "Sale price below purchase price", "Satış fiyatı alışın altında", "Sotuv narxi xariddan past"); break;
            case Kind.DupBarcode:
                found = rows.Where(r => !string.IsNullOrWhiteSpace(r.Tile.Barcode)).GroupBy(r => r.Tile.Barcode!.Trim()).Where(g => g.Count() > 1).SelectMany(g => g).OrderBy(r => r.Tile.Barcode);
                title = T("Дубли штрихкодов", "Кайталанган штрихкоддор", "Duplicate barcodes", "Yinelenen barkodlar", "Takroriy shtrix-kodlar"); break;
            case Kind.DupName:
                found = rows.GroupBy(r => Fold(r.Tile.Title).Trim()).Where(g => g.Key.Length > 0 && g.Count() > 1).SelectMany(g => g).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T("Дубли названий", "Кайталанган аталыштар", "Duplicate names", "Yinelenen adlar", "Takroriy nomlar"); break;
            case Kind.TopPrice:
                found = rows.OrderByDescending(r => r.Price).Take(Shown);
                title = T($"Самые дорогие товары (по цене продажи, топ {Shown})", $"Эң кымбат товарлар (сатуу баасы боюнча, топ {Shown})", $"Most expensive products (by sale price, top {Shown})", $"En pahalı ürünler (satış fiyatına göre, ilk {Shown})", $"Eng qimmat mahsulotlar (sotuv narxi bo'yicha, top {Shown})"); break;
            case Kind.TopQty:
                found = rows.OrderByDescending(r => r.Qty).Take(Shown);
                title = T($"Больше всего на складе (топ {Shown})", $"Кампада эң көп (топ {Shown})", $"Most in stock (top {Shown})", $"Depoda en çok (ilk {Shown})", $"Omborda eng ko'p (top {Shown})"); break;
            case Kind.TopValue:
                found = rows.Where(r => r.Qty > 0 && r.Cost > 0).OrderByDescending(r => r.Qty * r.Cost).Take(Shown);
                title = T($"Больше всего денег на складе (остаток × закупка, топ {Shown})", $"Кампада эң көп акча (калдык × сатып алуу, топ {Shown})", $"Most money in stock (stock × purchase, top {Shown})", $"Depoda en çok para (stok × alış, ilk {Shown})", $"Omborda eng ko'p pul (qoldiq × xarid, top {Shown})"); break;
            case Kind.Category:
                found = rows.Where(r => r.Tile.Category is { } c && Fold(c).Contains(p.Arg!)).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T($"Категория «{p.Arg}»", $"«{p.Arg}» категориясы", $"Category “{p.Arg}”", $"«{p.Arg}» kategorisi", $"«{p.Arg}» kategoriyasi"); break;
            case Kind.Brand:
                found = rows.Where(r => r.Tile.Brand is { } b && Fold(b).Contains(p.Arg!)).OrderBy(r => r.Tile.Title, StringComparer.CurrentCultureIgnoreCase);
                title = T($"Бренд «{p.Arg}»", $"«{p.Arg}» бренди", $"Brand “{p.Arg}”", $"«{p.Arg}» markası", $"«{p.Arg}» brendi"); break;
            case Kind.QtyRange:
                found = rows.Where(r => (p.A is not { } a || r.Qty >= a) && (p.B is not { } b || r.Qty <= b)).OrderBy(r => r.Qty);
                title = T("Остаток ", "Калдыгы ", "Stock ", "Stok ", "Qoldiq ") + Range(p.A, p.B); break;
            default:
                found = rows.Where(r => (p.A is not { } a || r.Price >= a) && (p.B is not { } b || r.Price <= b)).OrderBy(r => r.Price);
                title = T($"Цена {(p.A is null ? "" : "от " + p.A)}{(p.B is null ? "" : " до " + p.B)} сом", $"Баасы {(p.A is null ? "" : p.A + "дан")}{(p.B is null ? "" : " " + p.B + "га чейин")} сом", $"Price {(p.A is null ? "" : "from " + p.A)}{(p.B is null ? "" : " to " + p.B)} som",
                    $"Fiyat {(p.A is null ? "" : p.A + "'dan")}{(p.B is null ? "" : " " + p.B + "'a kadar")} som", $"Narx {(p.A is null ? "" : p.A + " dan")}{(p.B is null ? "" : " " + p.B + " gacha")} so'm"); break;
        }
        var all = found.ToList();
        var total = all.Count;
        var list = all.Take(ExcelMax).ToList();
        var full = Table(list, showMoney, list.Count);
        var shown = Table(list.Take(Shown).ToList(), showMoney, list.Count);
        var head = list.Count == 0
            ? T($"{title}: ничего не найдено.", $"{title}: эч нерсе табылган жок.", $"{title}: nothing found.", $"{title}: bulunamadı.", $"{title}: hech narsa topilmadi.")
            : T($"{title}: {total} шт." + (total > Shown ? $" Показаны первые {Shown}." : ""), $"{title}: {total} даана." + (total > Shown ? $" Алгачкы {Shown} көрсөтүлдү." : ""), $"{title}: {total}." + (total > Shown ? $" First {Shown} shown." : ""),
                $"{title}: {total}." + (total > Shown ? $" İlk {Shown} gösterildi." : ""), $"{title}: {total} ta." + (total > Shown ? $" Birinchi {Shown} ta ko'rsatildi." : ""));
        // 2026-10-30: дата во фразе («с 01.10», «за октябрь») списками не поддерживается — честно говорим, что она не учтена.
        if (p.DateIgnored)
            head += "\n" + T("Дату из запроса учесть нельзя — список без даты.", "Суроодогу датаны эске алууга болбойт — тизме датасыз.", "The date in the request can't be applied — the list has no date filter.",
                "İstekteki tarih uygulanamaz — liste tarihsizdir.", "So'rovdagi sanani hisobga olib bo'lmaydi — ro'yxat sanasiz.");
        return new Result(p.Kind, title, total, list.Count == 0 ? head : head + "\n\n" + shown, full, nav, canOrder && list.Count > 0, false);
    }

    private static string Table(List<Row> rows, bool showMoney, int total)
    {
        var sb = new StringBuilder();
        sb.Append("| № | ").Append(T("Товар", "Товар", "Product", "Ürün", "Mahsulot")).Append(" | ").Append(T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")).Append(" | ")
          .Append(T("Цена, сом", "Баасы, сом", "Price, som", "Fiyat, som", "Narx, so'm"));
        if (showMoney)
            sb.Append(" | ").Append(T("Закупка, сом", "Сатып алуу, сом", "Purchase, som", "Alış, som", "Xarid, so'm"));
        sb.Append(" | ").Append(T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod")).AppendLine(" |");
        sb.Append("|---:|---|---:|---:|").Append(showMoney ? "---:|" : "").AppendLine("---|");
        var i = 0;
        string Cell(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Replace("|", "/").Trim();
        foreach (var r in rows)
        {
            sb.Append("| ").Append(++i).Append(" | ").Append(Cell(r.Tile.Title)).Append(" | ").Append(r.Qty.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ').Append(Cell(r.Tile.Unit)).Append(" | ")
              .Append(r.Price.ToString("0.##", CultureInfo.InvariantCulture));
            if (showMoney)
                sb.Append(" | ").Append(r.Cost > 0 ? r.Cost.ToString("0.##", CultureInfo.InvariantCulture) : "—");
            sb.Append(" | ").Append(Cell(r.Tile.Barcode)).AppendLine(" |");
        }
        return sb.ToString().TrimEnd();
    }
}
