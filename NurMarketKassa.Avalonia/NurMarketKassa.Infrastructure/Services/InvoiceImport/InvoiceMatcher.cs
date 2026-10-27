using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27: сверка строк накладной с товарами склада. Порядок: штрихкод (основной и дополнительные, 12 ↔ 13 цифр) → артикул/код товара →
// название как есть → название нормализованное (регистр, кавычки, порядок слов, «500мл» = «500 мл») → нечёткое (общие слова, числа должны
// совпасть). Нечёткое и артикул с непохожим названием требуют подтверждения кассира. Словари строятся один раз на каталог.

public enum InvoiceMatchKind
{
    None,
    Barcode,
    Article,
    Name,
    NormalizedName,
    Fuzzy,
}

public sealed class InvoiceMatch
{
    public InvoiceMatchKind Kind { get; init; }
    public CatalogProductTileVm? Tile { get; init; }

    /// <summary>0..1; для точных совпадений 1.</summary>
    public double Score { get; init; }

    /// <summary>Кассир должен подтвердить (нечёткое совпадение, неоднозначность, артикул при непохожем названии).</summary>
    public bool NeedsConfirm { get; init; }

    public static InvoiceMatch Nothing { get; } = new();
}

public sealed class InvoiceCatalogIndex
{
    /// <summary>Порог «похожий товар» — тот же, что в мобилке (invoice_match.dart: fuzzyOffer = 0,75).</summary>
    public const double FuzzyOffer = 0.75;

    private readonly Dictionary<string, CatalogProductTileVm> _barcodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogProductTileVm> _articles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogProductTileVm> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CatalogProductTileVm> _rawNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CatalogProductTileVm> _sortedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byToken = new(StringComparer.Ordinal);
    private readonly List<(CatalogProductTileVm Tile, string[] Tokens)> _tiles = [];

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

            var title = tile.Title ?? "";
            Prefer(_rawNames, RawKey(title), tile);
            var tokens = Tokens(title);
            if (tokens.Length == 0)
                continue;
            Prefer(_sortedNames, SortedKey(tokens), tile);
            var index = _tiles.Count;
            _tiles.Add((tile, tokens));
            foreach (var token in tokens.Where(t => t.Length >= 3 && !t.Any(char.IsDigit)).Distinct())
            {
                Add(token, index);
                if (token.Length >= 5)
                    Add("~" + token[..4], index);
            }
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

    public InvoiceMatch Match(InvoiceItem item)
    {
        if (item.Barcode.Length > 0 && FindByBarcode(item.Barcode) is { } byBarcode)
            return new InvoiceMatch { Kind = InvoiceMatchKind.Barcode, Tile = byBarcode, Score = 1 };

        var tokens = Tokens(item.Name);
        if (ArticleKey(item.Article) is { Length: >= 3 } ak && _articles.TryGetValue(ak, out var byArticle))
        {
            // Артикул поставщика мог случайно совпасть с чужим артикулом — если название совсем не похоже, просим подтверждения.
            var similarity = tokens.Length == 0 ? 1 : Similarity(tokens, Tokens(byArticle.Title));
            return new InvoiceMatch { Kind = InvoiceMatchKind.Article, Tile = byArticle, Score = similarity, NeedsConfirm = similarity < 0.3 };
        }
        // 2026-10-27 (ревью Ск-8): совпало только с «кодом товара» (PLU весов и т. п.), не с артикулом — всегда «Проверьте», даже без названия в файле.
        // Точное совпадение названия сильнее (идёт ниже), нечёткое — слабее.
        InvoiceMatch? byCodeMatch = null;
        if (ArticleKey(item.Article) is { Length: >= 3 } ck && _codes.TryGetValue(ck, out var byCode))
            byCodeMatch = new InvoiceMatch { Kind = InvoiceMatchKind.Article, Tile = byCode, Score = tokens.Length == 0 ? 0 : Similarity(tokens, Tokens(byCode.Title)), NeedsConfirm = true };

        if (tokens.Length == 0)
            return byCodeMatch ?? InvoiceMatch.Nothing;

        if (_rawNames.TryGetValue(RawKey(item.Name), out var byRaw))
            return new InvoiceMatch { Kind = InvoiceMatchKind.Name, Tile = byRaw, Score = 1 };
        if (_sortedNames.TryGetValue(SortedKey(tokens), out var byNormalized))
            return new InvoiceMatch { Kind = InvoiceMatchKind.NormalizedName, Tile = byNormalized, Score = 0.97 };
        if (byCodeMatch is not null)
            return byCodeMatch;

        // Нечёткое: кандидаты по общим словам, оценка по доле совпавших слов; числа (размер, объём, модель) обязаны совпасть.
        var candidates = new HashSet<int>();
        foreach (var token in tokens.Where(t => t.Length >= 3 && !t.Any(char.IsDigit)).Distinct())
        {
            if (_byToken.TryGetValue(token, out var exact) && exact.Count <= 4000)
                candidates.UnionWith(exact);
            if (token.Length >= 4 && _byToken.TryGetValue("~" + token[..4], out var prefixed) && prefixed.Count <= 4000)
                candidates.UnionWith(prefixed);
            if (candidates.Count > 8000)
                break;
        }

        CatalogProductTileVm? best = null;
        var bestScore = 0.0;
        var second = 0.0;
        foreach (var index in candidates)
        {
            var (tile, tileTokens) = _tiles[index];
            var score = Similarity(tokens, tileTokens);
            if (score > bestScore || (Math.Abs(score - bestScore) < 1e-9 && best is not null && best.Quantity <= 0 && tile.Quantity > 0))
            {
                second = Math.Max(second, bestScore);
                bestScore = score;
                best = tile;
            }
            else if (score > second)
                second = score;
        }

        if (best is not null && bestScore >= FuzzyOffer)
            return new InvoiceMatch { Kind = InvoiceMatchKind.Fuzzy, Tile = best, Score = bestScore, NeedsConfirm = true };
        return InvoiceMatch.Nothing;
    }

    /// <summary>Похожесть двух названий 0..1: 2 × совпавшие слова / (слов в первом + слов во втором). Разные числа — 0.</summary>
    public static double Similarity(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var numsA = a.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var numsB = b.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
        var penalty = 0.0;
        if (numsA.Count > 0 && numsB.Count > 0)
        {
            if (!numsA.SetEquals(numsB))
            {
                // «15» против «15, 1504» — лишнее число только у одной стороны: допустимо, но похожесть ниже. «15» против «17» — другой товар.
                if (!(numsA.IsSubsetOf(numsB) || numsB.IsSubsetOf(numsA)))
                    return 0;
                penalty = 0.12;
            }
        }
        else if (numsA.Count > 0 || numsB.Count > 0)
            penalty = 0.12;

        var wordsA = a.Where(t => !t.Any(char.IsDigit)).ToList();
        var wordsB = b.Where(t => !t.Any(char.IsDigit)).ToList();
        if (wordsA.Count == 0 || wordsB.Count == 0)
            return numsA.Count > 0 && numsA.SetEquals(numsB) ? 0.5 : 0;

        var used = new bool[wordsB.Count];
        var matched = 0;
        foreach (var w in wordsA)
        {
            for (var j = 0; j < wordsB.Count; j++)
            {
                if (used[j] || !WordsClose(w, wordsB[j]))
                    continue;
                used[j] = true;
                matched++;
                break;
            }
        }

        var total = wordsA.Count + wordsB.Count + numsA.Count + numsB.Count;
        var numMatched = numsA.Intersect(numsB).Count();
        var score = 2.0 * (matched + numMatched) / total;
        return Math.Max(0, score - penalty);
    }

    private static bool WordsClose(string x, string y)
    {
        if (x == y)
            return true;
        var min = Math.Min(x.Length, y.Length);
        if (min >= 4 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)))
            return true;
        return min >= 5 && Math.Abs(x.Length - y.Length) <= 1 && Levenshtein(x, y) <= 1;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
