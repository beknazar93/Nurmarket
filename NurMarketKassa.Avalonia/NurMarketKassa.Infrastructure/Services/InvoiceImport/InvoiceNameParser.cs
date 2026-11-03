using System.Globalization;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-11-03: разбор названия товара для сопоставления: слова (со звуковым ключом и признаком «сокращение с точкой») и числовые факты
// (объём в мл, масса в граммах, обычные числа). «1л» = «1 литр» = «1000 мл», «0,5кг» = «500 г», «Мол.» = «Молоко», «Coca-Cola» = «Кока-кола».
// Сравнение двух разобранных названий — InvoiceNameParser.Compare: число/объём/модель разные → несовпадение (0), иначе доля общих слов и фактов.

public sealed class ParsedName
{
    public sealed record Word(string Text, string Key, bool Abbrev);

    /// <summary>Числовой факт: Kind 'v' — объём (значение в мл), 'm' — масса (в граммах), 'n' — обычное число.</summary>
    public readonly record struct Fact(char Kind, double Value);

    public Word[] Words { get; init; } = [];
    public Fact[] Facts { get; init; } = [];

    /// <summary>Слова и числа, как раньше в индексе: слова без цифр, ≥ 3 букв — ключ кандидатов (звуковой ключ, одинаковый для латиницы и кириллицы).</summary>
    public int Size => Words.Length + Facts.Length;
}

public static class InvoiceNameParser
{
    private static readonly Regex QuantityRx = new(InvoiceNameData.QuantityPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Boundary = new(@"(?<=\d)(?=\p{L})|(?<=\p{L})(?=\d)", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"\p{L}+\.?|\d+(?:[.,]\d+)?", RegexOptions.Compiled);
    private static readonly Regex SlashAbbr = new(@"(?<![\p{L}\d])(б/а|ж/б|ст/б|пл/б|т/п|ср-во|ср-ва)(?![\p{L}\d])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] Quotes = ["\"", "«", "»", "“", "”", "„", "'", "`"];

    public static ParsedName Parse(string? name)
    {
        var s = (name ?? "").ToLowerInvariant().Replace('ё', 'е');
        foreach (var q in Quotes)
            s = s.Replace(q, " ");
        // «б/а», «ж/б», «ср-во» — сокращения со знаком внутри: в основу слова до разбивки на слова.
        s = SlashAbbr.Replace(s, m => " " + (InvoiceNameData.Abbreviations.TryGetValue(m.Value, out var stem) ? stem : m.Value) + " ");

        // Число с единицей — в факт (объём/масса) или в обычное число («10 шт», «128GB»).
        var facts = new List<ParsedName.Fact>();
        s = QuantityRx.Replace(s, m =>
        {
            if (!TryNumber(m.Groups[1].Value, out var value))
                return m.Value;
            if (InvoiceNameData.UnitOf(m.Groups[2].Value) is { } unit)
                facts.Add(new ParsedName.Fact(unit.Kind, Math.Round(value * unit.Factor, 6)));
            else
                facts.Add(new ParsedName.Fact('n', value));
            return " ";
        });

        s = Boundary.Replace(s, " ");
        var words = new List<ParsedName.Word>();
        foreach (Match m in TokenRx.Matches(s))
        {
            var t = m.Value;
            if (char.IsDigit(t[0]))
            {
                if (TryNumber(t, out var value))
                    facts.Add(new ParsedName.Fact('n', value));
                continue;
            }
            var abbrev = t.EndsWith('.');
            t = t.TrimEnd('.');
            if (t.Length == 0 || InvoiceNameData.NoiseWords.Contains(t))
                continue;
            if (InvoiceNameData.Abbreviations.TryGetValue(t, out var stem))
            {
                t = stem;
                abbrev = false;
            }
            words.Add(new ParsedName.Word(t, InvoiceNameData.Phonetic(t), abbrev));
        }
        return new ParsedName { Words = words.ToArray(), Facts = facts.ToArray() };
    }

    private static bool TryNumber(string raw, out double value) =>
        double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Ключ для памяти поставщика: слова (основы) и факты по порядку не зависят от порядка слов, регистра, «1л»/«1 литр». Пусто — в названии нечего запоминать.</summary>
    public static string MemoryKey(string? name)
    {
        var p = Parse(name);
        var parts = p.Words.Select(w => w.Text).Concat(p.Facts.Select(f => f.Kind + f.Value.ToString("0.######", CultureInfo.InvariantCulture))).OrderBy(x => x, StringComparer.Ordinal);
        return string.Join(" ", parts);
    }

    // ------------------------------------------------------------------ сравнение

    public readonly record struct Comparison(double Score, bool Conflict, int MatchedWords, int MatchedFacts, bool ShortCovered);

    private static bool Same(double x, double y) => Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));

    /// <summary>Совпадение двух числовых фактов: тот же вид и значение; обычное число совпадает с объёмом/массой, если это то же значение в литрах/мл
    /// или в кг/г («Кока-кола 0,5» = «Coca-Cola 0.5л», «Шоколад 100» = «Шоколад 100г»). Объём против массы — нет.</summary>
    private static bool FactsMatch(ParsedName.Fact a, ParsedName.Fact b)
    {
        if (a.Kind == b.Kind)
            return Same(a.Value, b.Value);
        if (a.Kind == 'n')
            return PlainMatchesTyped(a.Value, b);
        if (b.Kind == 'n')
            return PlainMatchesTyped(b.Value, a);
        return false;
    }

    private static bool PlainMatchesTyped(double plain, ParsedName.Fact typed) =>
        Same(plain, typed.Value) || Same(plain, typed.Value / 1000);

    public static bool WordsClose(ParsedName.Word a, ParsedName.Word b)
    {
        var x = a.Key;
        var y = b.Key;
        if (x == y)
            return true;
        var min = Math.Min(x.Length, y.Length);
        if (min >= 4 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)))
            return true;
        // «Мол.», «Шок.»: слово с точкой короче 4 букв — сокращение, достаточно, что чужое слово с него начинается.
        if (a.Abbrev && x.Length >= 2 && y.StartsWith(x, StringComparison.Ordinal))
            return true;
        if (b.Abbrev && y.Length >= 2 && x.StartsWith(y, StringComparison.Ordinal))
            return true;
        return min >= 5 && Math.Abs(x.Length - y.Length) <= 1 && Levenshtein(x, y) <= 1;
    }

    /// <summary>Похожесть двух названий 0..1. Числа/объём/модель разные у обеих сторон — несовпадение (0). Иначе: 2 × общее / (всего слов и фактов), минус 0,12,
    /// если у одной стороны есть лишнее число; «вложенность»: все слова и числа короткого названия есть в длинном (≥ 2 штук) — не ниже 0,72 + 0,2 × доля длины.</summary>
    public static Comparison Compare(ParsedName a, ParsedName b)
    {
        if (a.Size == 0 || b.Size == 0)
            return new Comparison(0, false, 0, 0, false);

        var used = new bool[b.Words.Length];
        var mw = 0;
        foreach (var w in a.Words)
        {
            for (var j = 0; j < b.Words.Length; j++)
            {
                if (used[j] || !WordsClose(w, b.Words[j]))
                    continue;
                used[j] = true;
                mw++;
                break;
            }
        }

        var usedF = new bool[b.Facts.Length];
        var mf = 0;
        foreach (var f in a.Facts)
        {
            for (var j = 0; j < b.Facts.Length; j++)
            {
                if (usedF[j] || !FactsMatch(f, b.Facts[j]))
                    continue;
                usedF[j] = true;
                mf++;
                break;
            }
        }
        var leftA = a.Facts.Length - mf;
        var leftB = b.Facts.Length - mf;
        // «15» против «16», «1 л» против «2 л»: у обеих сторон остались несовпавшие числа — другой товар.
        if (leftA > 0 && leftB > 0)
            return new Comparison(0, true, mw, mf, false);

        if (a.Words.Length == 0 || b.Words.Length == 0)
            return new Comparison(mf > 0 && leftA == 0 && leftB == 0 ? 0.5 : 0, false, mw, mf, false);

        var penalty = leftA + leftB > 0 ? 0.12 : 0.0;
        var total = a.Words.Length + b.Words.Length + a.Facts.Length + b.Facts.Length;
        var score = Math.Max(0, 2.0 * (mw + mf) / total - penalty);

        // Вложенность: короткое название целиком внутри длинного («A15 128» ⊂ «Samsung Galaxy A15 128GB»).
        var shortIsA = a.Size <= b.Size;
        var shortP = shortIsA ? a : b;
        var longP = shortIsA ? b : a;
        var covered = mw == shortP.Words.Length && (shortIsA ? leftA : leftB) == 0 && mw >= 1;
        if (covered && shortP.Size >= 2 && (mf > 0 || shortP.Words.Length >= 2))
            score = Math.Max(score, Math.Min(0.95, 0.72 + 0.2 * shortP.Size / (double)longP.Size));
        return new Comparison(score, false, mw, mf, covered);
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
