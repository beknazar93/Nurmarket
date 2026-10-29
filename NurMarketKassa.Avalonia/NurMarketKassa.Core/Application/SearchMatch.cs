using System.Text;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Core.Application;

/// <summary>
/// 2026-10-29 (жалоба владельца «Asu — не работает поиск!» и аудит всех полей поиска): ЕДИНЫЙ помощник поиска по тексту.
/// Раньше каждое окно сравнивало по-своему (Contains одной подстрокой, где-то OrdinalIgnoreCase, где-то Current…, ё≠е,
/// «асу негаз» не находило «негаз асу», «Asu» не находило «Асу», «фыг» — тоже). Теперь:
///  • Normalize: нижний регистр, ё→е, ү/ө/ң→у/о/н (кыргызские буквы без смены раскладки), пробелы схлопнуты, «дикие» пробелы и невидимые символы убраны;
///  • слова запроса — в любом порядке, каждое — подстрока («негаз асу» находит «Асу негазированная 1л»);
///  • если по запросу как набран ничего нет — вторая попытка: «не та раскладка» (ASU ↔ ФЫГ) и транслит (Asu ↔ Асу).
/// Название не SearchText — это имя свойства во многих моделях (конфликт имён), а в Avalonia есть своя TextSearch.
/// </summary>
public static class SearchMatch
{
    /// <summary>Разделитель полей в ключе поиска: слово запроса не может его содержать, значит, не «склеится» между полями.</summary>
    public const char FieldSeparator = '\u001f';

    private const string EnKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string RuKeys = "йцукенгшщзхъфывапролджэячсмитьбюё";

    // Ключ: символ раскладки -> парный символ другой раскладки (после Normalize ё уже е, поэтому «`» -> «е»).
    private static readonly Dictionary<char, char> EnToRu = BuildMap(EnKeys, RuKeys);
    private static readonly Dictionary<char, char> RuToEn = BuildMap(RuKeys, EnKeys);

    private static Dictionary<char, char> BuildMap(string from, string to)
    {
        var d = new Dictionary<char, char>(from.Length);
        for (var i = 0; i < from.Length && i < to.Length; i++)
            d[from[i]] = to[i];
        return d;
    }

    /// <summary>Нижний регистр, ё→е, кыргызские/казахские буквы → ближайшие русские, пробелы схлопнуты, края обрезаны.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var raw in text)
        {
            if (raw is '​' or '‌' or '‍' or '﻿' or '­')
                continue; // невидимые символы (вставка из мессенджера/Excel)
            if (char.IsWhiteSpace(raw) || raw == ' ')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            var c = Fold(char.ToLowerInvariant(raw));
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static char Fold(char c) => c switch
    {
        'ё' => 'е',
        'ң' => 'н',
        'ө' => 'о',
        'ү' => 'у',
        'ұ' => 'у',
        'қ' => 'к',
        'ғ' => 'г',
        'һ' => 'х',
        'ı' => 'i',
        '’' or 'ʼ' or '`' => '\'',
        _ => c,
    };

    /// <summary>Ключ строки для поиска: поля через разделитель, уже нормализованные. Считать один раз на строку и хранить.</summary>
    public static string Key(params string?[] fields)
    {
        if (fields.Length == 1)
            return Normalize(fields[0]);
        var sb = new StringBuilder();
        foreach (var f in fields)
        {
            if (string.IsNullOrEmpty(f))
                continue;
            if (sb.Length > 0)
                sb.Append(FieldSeparator);
            sb.Append(Normalize(f));
        }
        return sb.ToString();
    }

    public static SearchQuery Prepare(string? query) => new(query);

    /// <summary>Подходит ли строка запросу (без списка): слова в любом порядке, при неудаче — раскладка/транслит.</summary>
    // Запрос один на весь проход по списку — разбираем его один раз (иначе на каждую строку заново: длинная вставка в поиск подвешивала окно).
    [ThreadStatic] private static SearchQuery? _lastQuery;
    [ThreadStatic] private static string? _lastQueryText;

    public static bool Matches(string? query, params string?[] fields)
    {
        if (_lastQuery is null || !string.Equals(_lastQueryText, query, StringComparison.Ordinal))
        {
            _lastQuery = new SearchQuery(query);
            _lastQueryText = query;
        }
        var q = _lastQuery;
        if (q.IsEmpty)
            return true;
        var key = Key(fields);
        return q.Matches(key) || q.MatchesFuzzy(key);
    }

    /// <summary>Отфильтровать список: сначала запрос как набран; если пусто — с учётом раскладки и транслита (Asu→Асу, ФЫГ→ASU).
    /// Порядок элементов сохраняется. Пустой запрос возвращает список как есть.</summary>
    public static List<T> Filter<T>(IEnumerable<T> source, string? query, Func<T, string> key)
    {
        var q = new SearchQuery(query);
        if (q.IsEmpty)
            return source.ToList();
        var items = source as IList<T> ?? source.ToList();
        var strict = items.Where(x => q.Matches(key(x))).ToList();
        if (strict.Count > 0)
            return strict;
        return items.Where(x => q.MatchesFuzzy(key(x))).ToList();
    }

    /// <summary>Фильтр по готовым ключам (ключ считается один раз на строку заранее — для каталога в тысячи товаров).</summary>
    public static List<T> Filter<T>(IEnumerable<T> source, string? query, Func<T, string> key, out bool usedFuzzy)
    {
        usedFuzzy = false;
        var q = new SearchQuery(query);
        if (q.IsEmpty)
            return source.ToList();
        var items = source as IList<T> ?? source.ToList();
        var strict = items.Where(x => q.Matches(key(x))).ToList();
        if (strict.Count > 0)
            return strict;
        usedFuzzy = true;
        return items.Where(x => q.MatchesFuzzy(key(x))).ToList();
    }

    /// <summary>Поиск товаров по каталогу: название, штрихкод, код, артикул, категория, бренд (SearchKey плитки — считается один раз).
    /// <paramref name="extra"/> — дополнительные условия «или» (доп. штрихкоды, штрихкод размера). Сначала как набрано, потом раскладка/транслит.</summary>
    public static List<CatalogProductTileVm> Products(IEnumerable<CatalogProductTileVm> source, string? query, Func<CatalogProductTileVm, bool>? extra = null)
    {
        var q = new SearchQuery(query);
        var items = source as IList<CatalogProductTileVm> ?? source.ToList();
        if (q.IsEmpty)
            return items.ToList();
        var strict = items.Where(p => q.Matches(p.SearchKey) || (extra?.Invoke(p) ?? false)).ToList();
        return strict.Count > 0 ? strict : items.Where(p => q.MatchesFuzzy(p.SearchKey) || (extra?.Invoke(p) ?? false)).ToList();
    }

    // ---------------------------------------------------------------- раскладка и транслит

    internal static string SwapLayout(string token, bool fromLatin)
    {
        var map = fromLatin ? EnToRu : RuToEn;
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
            sb.Append(map.TryGetValue(c, out var m) ? m : c);
        return sb.ToString();
    }

    private static readonly (string Lat, string Cyr)[] LatToCyrDigraphs =
    {
        ("sch", "щ"), ("shch", "щ"), ("zh", "ж"), ("kh", "х"), ("ts", "ц"), ("ch", "ч"), ("sh", "ш"),
        ("yu", "ю"), ("ya", "я"), ("yo", "е"), ("ye", "е"), ("ph", "ф"),
    };

    private static readonly Dictionary<char, string> LatToCyr = new()
    {
        ['a'] = "а", ['b'] = "б", ['c'] = "к", ['d'] = "д", ['e'] = "е", ['f'] = "ф", ['g'] = "г", ['h'] = "х", ['i'] = "и",
        ['j'] = "ж", ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п", ['q'] = "к", ['r'] = "р",
        ['s'] = "с", ['t'] = "т", ['u'] = "у", ['v'] = "в", ['w'] = "в", ['x'] = "кс", ['y'] = "ы", ['z'] = "з",
    };

    private static readonly Dictionary<char, string> CyrToLat = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s",
        ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    internal static string TranslitLatToCyr(string token, bool cAsTs = false)
    {
        var sb = new StringBuilder(token.Length);
        for (var i = 0; i < token.Length;)
        {
            var matched = false;
            foreach (var (lat, cyr) in LatToCyrDigraphs)
            {
                if (string.CompareOrdinal(token, i, lat, 0, lat.Length) == 0)
                {
                    sb.Append(cyr);
                    i += lat.Length;
                    matched = true;
                    break;
                }
            }
            if (matched)
                continue;
            sb.Append(cAsTs && token[i] == 'c' ? "ц" : LatToCyr.TryGetValue(token[i], out var m) ? m : token[i].ToString());
            i++;
        }
        return sb.ToString();
    }

    internal static string TranslitCyrToLat(string token, bool vAsW = false)
    {
        var sb = new StringBuilder(token.Length);
        foreach (var c in token)
            sb.Append(vAsW && c == 'в' ? "w" : CyrToLat.TryGetValue(c, out var m) ? m : c.ToString());
        return sb.ToString();
    }

    internal static bool HasLatin(string s)
    {
        foreach (var c in s)
            if (c is >= 'a' and <= 'z')
                return true;
        return false;
    }

    internal static bool HasCyrillic(string s)
    {
        foreach (var c in s)
            if (c is >= 'а' and <= 'я')
                return true;
        return false;
    }
}

/// <summary>Подготовленный запрос: слова нормализованы, варианты раскладки/транслита посчитаны один раз на запрос, а не на строку.</summary>
public sealed class SearchQuery
{
    private readonly string[] _tokens;
    private readonly string[][] _variants;

    public SearchQuery(string? query)
    {
        Normalized = SearchMatch.Normalize(query);
        _tokens = Normalized.Length == 0
            ? Array.Empty<string>()
            : Normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim(',', ';', ':', '"', '«', '»', '(', ')')).Where(t => t.Length > 0).ToArray();
        _variants = new string[_tokens.Length][];
        for (var i = 0; i < _tokens.Length; i++)
        {
            var t = _tokens[i];
            var list = new List<string>(3);
            if (SearchMatch.HasLatin(t))
            {
                list.Add(SearchMatch.SwapLayout(t, fromLatin: true));   // asu -> фыг
                list.Add(SearchMatch.TranslitLatToCyr(t));              // asu -> асу
                if (t.Contains('c'))
                    list.Add(SearchMatch.TranslitLatToCyr(t, cAsTs: true)); // cezar -> цезар
            }
            if (SearchMatch.HasCyrillic(t))
            {
                list.Add(SearchMatch.SwapLayout(t, fromLatin: false));  // фыг -> asu
                list.Add(SearchMatch.TranslitCyrToLat(t));              // асу -> asu
                if (t.Contains('в'))
                    list.Add(SearchMatch.TranslitCyrToLat(t, vAsW: true));  // воллингер -> wollinger
            }
            _variants[i] = list.Where(v => v.Length > 0 && v != t).Distinct().ToArray();
        }
    }

    public string Normalized { get; }

    public bool IsEmpty => _tokens.Length == 0;

    public IReadOnlyList<string> Tokens => _tokens;

    /// <summary>Все слова запроса (в любом порядке) есть в ключе строки. Ключ уже нормализован (SearchMatch.Key/Normalize).</summary>
    public bool Matches(string normalizedKey)
    {
        foreach (var t in _tokens)
            if (normalizedKey.IndexOf(t, StringComparison.Ordinal) < 0)
                return false;
        return true;
    }

    /// <summary>2026-10-29 (приёмка 1.17.64, В-4): насколько название подходит запросу — 0: каждое слово запроса (или его вариант раскладки/транслита)
    /// начинает слово названия; 1: встречается внутри названия; 2: в названии нет (товар найден по штрихкоду, бренду, категории). Меньше — точнее.</summary>
    public int TitleRank(string? title)
    {
        if (_tokens.Length == 0)
            return 0;
        var t = SearchMatch.Normalize(title);
        var worst = 0;
        for (var i = 0; i < _tokens.Length; i++)
        {
            var best = 2;
            for (var k = -1; k < _variants[i].Length && best > 0; k++)
            {
                var cand = k < 0 ? _tokens[i] : _variants[i][k];
                for (var from = 0; from < t.Length;)
                {
                    var idx = t.IndexOf(cand, from, StringComparison.Ordinal);
                    if (idx < 0)
                        break;
                    if (idx == 0 || t[idx - 1] == ' ')
                    {
                        best = 0;
                        break;
                    }
                    best = Math.Min(best, 1);
                    from = idx + 1;
                }
            }
            worst = Math.Max(worst, best);
        }
        return worst;
    }

    /// <summary>То же, но каждое слово можно найти и в «другой раскладке» или транслите.</summary>
    public bool MatchesFuzzy(string normalizedKey)
    {
        for (var i = 0; i < _tokens.Length; i++)
        {
            if (normalizedKey.IndexOf(_tokens[i], StringComparison.Ordinal) >= 0)
                continue;
            var ok = false;
            foreach (var v in _variants[i])
            {
                if (normalizedKey.IndexOf(v, StringComparison.Ordinal) >= 0)
                {
                    ok = true;
                    break;
                }
            }
            if (!ok)
                return false;
        }
        return true;
    }
}
