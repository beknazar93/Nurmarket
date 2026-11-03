using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: сверка строк накладной с товарами склада. Порядок: штрихкод (основной и дополнительные, 12 ↔ 13 цифр) → артикул/код товара →
// название как есть → название нормализованное (регистр, кавычки, порядок слов, «500мл» = «500 мл») → нечёткое (общие слова, числа должны
// совпасть). Нечёткое и артикул с непохожим названием требуют подтверждения кассира. Словари строятся один раз на каталог.
// 2026-11-03: умнее нечёткое (InvoiceNameParser): сокращения («Мол.» = «Молоко»), единицы («1л» = «1 литр» = «1000 мл»), латиница ↔ кириллица («Coca-Cola» = «Кока-кола»),
// вложенность («A15 128» ⊂ «Samsung Galaxy A15 128GB»); до 5 кандидатов вместо одного; память поставщика («так назвали у этого поставщика → этот товар»)
// применяется перед нечётким. Числа/объём/модель разные — по-прежнему другой товар.

public enum InvoiceMatchKind
{
    None,
    Barcode,
    Article,
    Name,
    NormalizedName,
    Fuzzy,

    /// <summary>2026-11-03: кассир уже выбирал для этого названия у этого поставщика (InvoiceMatchMemory) — подтверждение не нужно, но можно изменить.</summary>
    Memory,

    /// <summary>2026-11-03: предложил ИИ (кнопка «Подобрать ИИ») — всегда с подтверждением.</summary>
    Ai,
}

/// <summary>Кандидат-товар склада для строки накладной и его похожесть 0..1.</summary>
public sealed record InvoiceCandidate(CatalogProductTileVm Tile, double Score);

public sealed class InvoiceMatch
{
    public InvoiceMatchKind Kind { get; init; }
    public CatalogProductTileVm? Tile { get; init; }

    /// <summary>0..1; для точных совпадений 1.</summary>
    public double Score { get; init; }

    /// <summary>Кассир должен подтвердить (нечёткое совпадение, неоднозначность, артикул при непохожем названии).</summary>
    public bool NeedsConfirm { get; init; }

    /// <summary>2026-11-03: до 5 похожих товаров склада по убыванию похожести (лучший — первым; для «Найден» он же = Tile). Пусто — нет похожих.</summary>
    public IReadOnlyList<InvoiceCandidate> Alternatives { get; init; } = [];

    /// <summary>2026-11-03: пояснение ИИ в одну фразу (Kind = Ai).</summary>
    public string Reason { get; init; } = "";

    public static InvoiceMatch Nothing { get; } = new();
}

public sealed class InvoiceCatalogIndex
{
    /// <summary>Порог «похожий товар» — тот же, что в мобилке (invoice_match.dart: fuzzyOffer = 0,75).</summary>
    public const double FuzzyOffer = 0.75;

    /// <summary>2026-11-03: для коротких названий (вместе слов и чисел ≤ 3 у любой из сторон) порог ниже; подтверждение всё равно обязательно.</summary>
    public const double ShortOffer = 0.70;

    /// <summary>2026-11-03: ниже этой похожести товар не попадает и в список «выбрать другой» (для строк «Новый» список показывается только по кнопке).</summary>
    public const double AlternativeMin = 0.55;

    public const int MaxAlternatives = 5;

    private readonly Dictionary<string, CatalogProductTileVm> _barcodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogProductTileVm> _articles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogProductTileVm> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogProductTileVm> _rawNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogProductTileVm> _sortedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byToken = new(StringComparer.Ordinal);
    private readonly List<(CatalogProductTileVm Tile, ParsedName Parsed)> _tiles = [];
    private readonly Dictionary<string, CatalogProductTileVm> _byId = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _tiles.Count;

    public InvoiceCatalogIndex(IReadOnlyList<CatalogProductTileVm> catalog)
    {
        foreach (var tile in catalog)
        {
            AddBarcode(tile.Barcode, tile);
            if (tile.AlternateBarcodeVariants is { } variants)
            {
                foreach (var v in variants)
                    AddBarcode(v.Barcode, tile);
            }
            if (!string.IsNullOrWhiteSpace(tile.AlternateBarcodesRaw))
            {
                foreach (var part in tile.AlternateBarcodesRaw.Split(['\n', '\r', ';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AddBarcode(part, tile);
            }
            AddArticle(tile.Article, tile);
            // 2026-10-27 (ревью Ск-8): «код товара» кассы (в т. ч. код весов PLU) — отдельный словарь: совпадение «артикула» накладной с чужим PLU всегда просит подтверждения.
            var codeKey = ArticleKey(tile.ProductCode);
            if (codeKey.Length >= 3)
                Prefer(_codes, codeKey, tile);

            if (!string.IsNullOrWhiteSpace(tile.Id))
                _byId.TryAdd(tile.Id, tile);
            var title = tile.Title ?? "";
            Prefer(_rawNames, RawKey(title), tile);
            var tokens = Tokens(title);
            if (tokens.Length == 0)
                continue;
            Prefer(_sortedNames, SortedKey(tokens), tile);
            var index = _tiles.Count;
            var parsed = InvoiceNameParser.Parse(title);
            _tiles.Add((tile, parsed));
            // 2026-11-03: ключ слова — звуковой (латиница и кириллица дают один ключ), плюс коды моделей («a15») — для коротких названий без длинных слов.
            foreach (var key in IndexKeys(parsed, title))
                Add(key, index);
        }
    }

    /// <summary>Ключи индекса кандидатов: звуковой ключ слова (≥ 3 букв), «~» + первые 4 для длинных слов, «m:» + код модели.</summary>
    private static IEnumerable<string> IndexKeys(ParsedName parsed, string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in parsed.Words)
        {
            if (w.Key.Length < 3)
                continue;
            if (seen.Add(w.Key))
                yield return w.Key;
            if (w.Key.Length >= 5 && seen.Add("~" + w.Key[..4]))
                yield return "~" + w.Key[..4];
        }
        foreach (var code in ModelCodes(name))
        {
            if (seen.Add("m:" + code))
                yield return "m:" + code;
        }
    }

    private static readonly Regex ModelRx = new(@"(?<![\p{L}\d])(\p{L}{1,3})\d{1,4}(?![\p{L}\d])", RegexOptions.Compiled);

    /// <summary>Коды моделей из названия: «A15», «S24», «x200» (буквы приведены к звуковому ключу — латинская «a» и кириллическая «а» одинаковы).</summary>
    private static IEnumerable<string> ModelCodes(string name)
    {
        foreach (Match m in ModelRx.Matches(name.ToLowerInvariant().Replace('ё', 'е')))
        {
            var letters = m.Groups[1].Value;
            yield return InvoiceNameData.Phonetic(letters) + m.Value[letters.Length..];
        }
    }

    private void Add(string key, int index)
    {
        if (!_byToken.TryGetValue(key, out var list))
            _byToken[key] = list = [];
        list.Add(index);
    }

    private static void Prefer(Dictionary<string, CatalogProductTileVm> map, string key, CatalogProductTileVm tile)
    {
        if (key.Length == 0)
            return;
        // Дубль в каталоге — берём товар с остатком (как ProductCodeLookup).
        if (!map.TryGetValue(key, out var existing) || (existing.Quantity <= 0 && tile.Quantity > 0))
            map[key] = tile;
    }

    private void AddBarcode(string? code, CatalogProductTileVm tile)
    {
        var digits = InvoiceText.CleanBarcode(code);
        var key = digits.Length > 0 ? BulkImportPlan.Canon(digits) : (code ?? "").Trim();
        if (key.Length > 0)
            Prefer(_barcodes, key, tile);
    }

    private void AddArticle(string? article, CatalogProductTileVm tile)
    {
        var key = ArticleKey(article);
        if (key.Length >= 3)
            Prefer(_articles, key, tile);
    }

    private static string ArticleKey(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public CatalogProductTileVm? FindByBarcode(string code)
    {
        var digits = InvoiceText.CleanBarcode(code);
        var key = digits.Length > 0 ? BulkImportPlan.Canon(digits) : code.Trim();
        return key.Length > 0 && _barcodes.TryGetValue(key, out var tile) ? tile : null;
    }

    // ------------------------------------------------------------------ нормализация названия

    private static readonly Regex Boundary = new(@"(?<=\d)(?=\p{L})|(?<=\p{L})(?=\d)", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"\p{L}+|\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    public static string RawKey(string? name) => Regex.Replace((name ?? "").Trim().ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ");

    public static string[] Tokens(string? name)
    {
        var s = (name ?? "").ToLowerInvariant().Replace('ё', 'е');
        foreach (var q in new[] { "\"", "«", "»", "“", "”", "„", "'", "`" })
            s = s.Replace(q, " ");
        s = Boundary.Replace(s, " ");
        return TokenRx.Matches(s).Select(m => m.Value.Replace(',', '.')).ToArray();
    }

    private static string SortedKey(string[] tokens) => string.Join(" ", tokens.OrderBy(t => t, StringComparer.Ordinal));

    // ------------------------------------------------------------------ сопоставление

    public InvoiceMatch Match(InvoiceItem item, InvoiceMatchMemory.Snapshot? memory = null)
    {
        if (item.Barcode.Length > 0 && FindByBarcode(item.Barcode) is { } byBarcode)
            return new InvoiceMatch { Kind = InvoiceMatchKind.Barcode, Tile = byBarcode, Score = 1 };

        var tokens = Tokens(item.Name);
        if (ArticleKey(item.Article) is { Length: >= 3 } ak && _articles.TryGetValue(ak, out var byArticle))
        {
            // Артикул поставщика мог случайно совпасть с чужим артикулом — если название совсем не похоже, просим подтверждения.
            var similarity = tokens.Length == 0 ? 1 : NameScore(item.Name, byArticle.Title);
            return new InvoiceMatch { Kind = InvoiceMatchKind.Article, Tile = byArticle, Score = similarity, NeedsConfirm = similarity < 0.3 };
        }
        // 2026-10-27 (ревью Ск-8): совпало только с «кодом товара» (PLU весов и т. п.), не с артикулом — всегда «Проверьте», даже без названия в файле.
        // Точное совпадение названия сильнее (идёт ниже), нечёткое — слабее.
        InvoiceMatch? byCodeMatch = null;
        if (ArticleKey(item.Article) is { Length: >= 3 } ck && _codes.TryGetValue(ck, out var byCode))
            byCodeMatch = new InvoiceMatch { Kind = InvoiceMatchKind.Article, Tile = byCode, Score = tokens.Length == 0 ? 0 : NameScore(item.Name, byCode.Title), NeedsConfirm = true };

        if (tokens.Length == 0)
            return byCodeMatch ?? InvoiceMatch.Nothing;

        if (_rawNames.TryGetValue(RawKey(item.Name), out var byRaw))
            return new InvoiceMatch { Kind = InvoiceMatchKind.Name, Tile = byRaw, Score = 1 };
        if (_sortedNames.TryGetValue(SortedKey(tokens), out var byNormalized))
            return new InvoiceMatch { Kind = InvoiceMatchKind.NormalizedName, Tile = byNormalized, Score = 0.97 };

        var parsed = InvoiceNameParser.Parse(item.Name);
        var alternatives = Rank(parsed, item.Name, AlternativeMin, out var offered);

        // 2026-11-03: память поставщика — перед нечётким. Товар исчез из каталога — запись молча игнорируется.
        if (memory?.Find(item.Name) is { Length: > 0 } rememberedId && _byId.TryGetValue(rememberedId, out var remembered))
        {
            var withFirst = alternatives.Where(c => !string.Equals(c.Tile.Id, remembered.Id, StringComparison.OrdinalIgnoreCase)).Take(MaxAlternatives - 1).ToList();
            withFirst.Insert(0, new InvoiceCandidate(remembered, 1));
            return new InvoiceMatch { Kind = InvoiceMatchKind.Memory, Tile = remembered, Score = 1, Alternatives = withFirst };
        }
        if (byCodeMatch is not null)
            return byCodeMatch;

        if (offered is not null)
        {
            var ordered = alternatives.Where(c => !ReferenceEquals(c.Tile, offered.Tile)).Take(MaxAlternatives - 1).ToList();
            ordered.Insert(0, offered);
            return new InvoiceMatch { Kind = InvoiceMatchKind.Fuzzy, Tile = offered.Tile, Score = offered.Score, NeedsConfirm = true, Alternatives = ordered };
        }
        // Достаточно похожего нет: строка «Новый», но список слабых кандидатов остаётся (кнопка «Похожие…» у строки).
        return alternatives.Count > 0 ? new InvoiceMatch { Alternatives = alternatives } : InvoiceMatch.Nothing;
    }

    /// <summary>Порог «предложить» для пары: короткие названия (слов и чисел ≤ 3 у любой стороны) — <see cref="ShortOffer"/>, иначе <see cref="FuzzyOffer"/>.</summary>
    private static double OfferThreshold(ParsedName a, ParsedName b) => Math.Min(a.Size, b.Size) <= 3 ? ShortOffer : FuzzyOffer;

    /// <summary>Похожесть названия накладной и названия товара 0..1 (разные числа — 0).</summary>
    public static double NameScore(string? invoiceName, string? productName) =>
        InvoiceNameParser.Compare(InvoiceNameParser.Parse(invoiceName), InvoiceNameParser.Parse(productName)).Score;

    private HashSet<int> Gather(ParsedName parsed, string name)
    {
        var candidates = new HashSet<int>();
        void Take(string key)
        {
            if (_byToken.TryGetValue(key, out var list) && list.Count <= 4000)
                candidates.UnionWith(list);
        }
        foreach (var w in parsed.Words.Where(w => w.Key.Length >= 3).Select(w => w.Key).Distinct())
        {
            Take(w);
            if (w.Length >= 4)
                Take("~" + w[..4]);
            if (candidates.Count > 8000)
                break;
        }
        foreach (var code in ModelCodes(name).Distinct())
            Take("m:" + code);
        return candidates;
    }

    /// <summary>До пяти самых похожих товаров (похожесть ≥ <paramref name="minScore"/>), лучший первым; при равенстве — тот, что есть в наличии.
    /// <paramref name="offered"/> — лучший из тех, что проходят порог «предложить» (null — таких нет).</summary>
    private List<InvoiceCandidate> Rank(ParsedName parsed, string name, double minScore, out InvoiceCandidate? offered)
    {
        offered = null;
        var found = new List<(InvoiceCandidate Candidate, bool Offer)>();
        foreach (var index in Gather(parsed, name))
        {
            var (tile, tileParsed) = _tiles[index];
            var score = InvoiceNameParser.Compare(parsed, tileParsed).Score;
            if (score >= minScore)
                found.Add((new InvoiceCandidate(tile, score), score >= OfferThreshold(parsed, tileParsed)));
        }
        var sorted = found
            .OrderByDescending(f => Math.Round(f.Candidate.Score, 6))
            .ThenByDescending(f => f.Candidate.Tile.Quantity > 0)
            .Take(MaxAlternatives * 4)
            .ToList();
        offered = sorted.FirstOrDefault(f => f.Offer).Candidate;
        return sorted.Select(f => f.Candidate).Take(MaxAlternatives).ToList();
    }

    /// <summary>Похожие товары для названия без накладной (редактор закупки по фото): только те, что проходят порог «предложить», до <see cref="MaxAlternatives"/>.</summary>
    public IReadOnlyList<InvoiceCandidate> Suggest(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Tokens(name).Length == 0)
            return [];
        var parsed = InvoiceNameParser.Parse(name);
        var all = Rank(parsed, name, AlternativeMin, out _);
        return all.Where(c => c.Score >= OfferThreshold(parsed, InvoiceNameParser.Parse(c.Tile.Title))).ToList();
    }

    /// <summary>Грубый отбор кандидатов для ИИ (кнопка «Подобрать ИИ»): общие слова без учёта чисел, до <paramref name="max"/> штук, самые похожие первыми.</summary>
    public IReadOnlyList<CatalogProductTileVm> RoughCandidates(string? name, int max)
    {
        if (string.IsNullOrWhiteSpace(name))
            return [];
        var parsed = InvoiceNameParser.Parse(name);
        if (parsed.Words.Length == 0)
            return [];
        var scored = new List<(CatalogProductTileVm Tile, double Score)>();
        foreach (var index in Gather(parsed, name))
        {
            var (tile, tp) = _tiles[index];
            var matched = 0;
            var used = new bool[tp.Words.Length];
            foreach (var w in parsed.Words)
            {
                for (var j = 0; j < tp.Words.Length; j++)
                {
                    if (used[j] || !InvoiceNameParser.WordsClose(w, tp.Words[j]))
                        continue;
                    used[j] = true;
                    matched++;
                    break;
                }
            }
            if (matched > 0)
                scored.Add((tile, 2.0 * matched / (parsed.Words.Length + tp.Words.Length)));
        }
        return scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Tile.Quantity > 0).Take(max).Select(x => x.Tile).ToList();
    }

    public CatalogProductTileVm? FindById(string? id) => !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out var tile) ? tile : null;

    /// <summary>Похожесть двух названий 0..1 по уже разбитым на слова строкам (совместимость): разные числа — 0.</summary>
    public static double Similarity(string[] a, string[] b) =>
        a.Length == 0 || b.Length == 0 ? 0 : NameScore(string.Join(" ", a), string.Join(" ", b));
}
