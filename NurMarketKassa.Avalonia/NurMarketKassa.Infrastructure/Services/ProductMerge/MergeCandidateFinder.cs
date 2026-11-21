using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.ProductMerge;

/// <summary>2026-11-21 («Найти дубли и варианты»): строит группы-кандидаты по уже загруженному каталогу. Чистая логика: ни сети, ни окон, ни записи.
/// Ход: (1) из названия убираем вкус/цвет/размер-букву (словарь ru/ky/uz/en) и «шумовые» слова («со вкусом»), числа с единицами (0,5 л, 500 мл, 1 кг, 3,2 %) оставляем
/// отдельным ключом — разный объём/вес/процент = разные товары; (2) группа = одинаковая основа + одинаковый ключ объёма, внутри — один бренд и одна категория
/// (пустой бренд/категория присоединяется к единственному непустому); (3) то, что словарь не поймал, ищется по общему началу названия (при той же цене и том же бренде
/// или от двух общих слов). Каждая группа получает оценку уверенности, причину и предупреждения; блокирующие причины (весовой, комплект, услуга, другая единица,
/// нет штрихкода) помечаются у товара. Ничего не объединяет: решение — за владельцем в окне «Объединение товаров».</summary>
public static class MergeCandidateFinder
{
    /// <summary>Допуск по цене продажи: 0,5 % (не меньше 1 копейки) — «цена одна».</summary>
    public static bool SamePrice(double a, double b) => Math.Abs(a - b) <= Math.Max(0.01, 0.005 * Math.Max(Math.Abs(a), Math.Abs(b)));

    // ── словарь вкусов и цветов (корни для приставочного поиска — от 4 букв; короткие слова — точным списком) ──
    private static readonly string[] FlavorStems =
    [
        "клубник", "клубнич", "земляник", "лимон", "малин", "вишн", "яблок", "яблоч", "персик", "апельсин", "мандарин", "банан", "виноград", "ананас", "манго", "груш",
        "арбуз", "черник", "смородин", "кокос", "шоколад", "ванил", "карамел", "лайм", "гранат", "абрикос", "облепих", "клюкв", "брусник", "ежевик", "маракуй", "фундук",
        "миндал", "кулпунай", "шабдаал", "жүзүм", "өрүк", "карагат", "дарбыз", "qulupnay", "shaftoli", "smorodina", "tarvuz", "strawberr", "raspberr", "blueberr", "pineapple",
        "chocolate", "vanilla", "watermelon", "coconut", "caramel", "cherry", "peach", "banana", "lemon", "apple", "orange", "mango", "grape", "lime", "mint",
    ];

    private static readonly HashSet<string> FlavorWords = new(StringComparer.Ordinal)
    {
        // короткие и неоднозначные — только точные формы
        "мята", "мятный", "мятная", "мятное", "мятные", "мятой", "мяты", "мед", "медовый", "медовая", "медовое", "дыня", "дынный", "дынная", "дыней", "дыни", "киви", "слива", "сливовый",
        "алма", "лимон", "малина", "банан", "апельсин", "коон", "olma", "limon", "malina", "banan", "apelsin", "uzum", "o'rik", "qovun", "vanil", "shokolad",
        "melon", "kiwi", "plum", "berry", "cola",
    };

    // цвета: кириллица с окончаниями прилагательных, остальное — точные слова
    private static readonly Regex ColorRu = new(
        "^(красн|син|голуб|зелен|желт|черн|бел|сер|розов|оранжев|фиолетов|коричнев|золот|серебрян|бежев|бордов|салатов|бирюзов|лилов|прозрачн|сиренев)(ый|ой|ий|ая|яя|ое|ее|ые|ие|ого|его|ых|их|ым|им|ом|ем|ую|юю)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ColorWords = new(StringComparer.Ordinal)
    {
        // ky
        "кызыл", "көк", "жашыл", "сары", "кара", "ак", "кызгылт", "күрөң", "боз", "сур", "кочкул",
        // uz
        "qizil", "ko'k", "yashil", "sariq", "qora", "oq", "pushti", "jigarrang", "kulrang", "zangori",
        // en
        "red", "blue", "green", "yellow", "black", "white", "pink", "grey", "gray", "purple", "brown", "gold", "silver", "beige",
        // буквенные размеры
        "xs", "s", "m", "l", "xl", "xxl", "xxxl",
    };

    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "со", "с", "вкусом", "вкус", "вкусы", "цвет", "цвета", "цветом", "аромат", "ароматом", "менен", "даамы", "rangi", "mazasi", "flavor", "flavour", "color", "colour", "taste",
    };

    private static readonly Regex QtyRegex = new(
        @"(?<![\p{L}\d])(\d+(?:[.,]\d+)?)\s*(%|мл|ml|литр\w*|л|l|кг|kg|гр|г|g|шт|pcs|см|cm|мм|mm|м|m)(?![\p{L}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е').Replace('ʻ', '\'').Replace('’', '\'').Replace('`', '\'').Replace('‘', '\'');

    private static bool IsFlavor(string t)
    {
        if (FlavorWords.Contains(t))
            return true;
        foreach (var stem in FlavorStems)
            if (t.Length >= stem.Length && t.StartsWith(stem, StringComparison.Ordinal) && t.Length - stem.Length <= 5)
                return true;
        return false;
    }

    private static bool IsVariantWord(string t) => IsFlavor(t) || ColorWords.Contains(t) || ColorRu.IsMatch(t);

    /// <summary>Ключ «число + единица» в одной системе мер: 0,5 л = 500 мл = «500ml»; кг → г; м/см → мм; процент и штуки как есть.</summary>
    internal static string QtyKey(string number, string unit)
    {
        if (!double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return number + unit;
        var (factor, canon) = unit switch
        {
            "л" or "l" => (1000.0, "ml"),
            "мл" or "ml" => (1.0, "ml"),
            "кг" or "kg" => (1000.0, "g"),
            "г" or "гр" or "g" => (1.0, "g"),
            "шт" or "pcs" => (1.0, "pcs"),
            "см" or "cm" => (10.0, "mm"),
            "мм" or "mm" => (1.0, "mm"),
            "м" or "m" => (1000.0, "mm"),
            "%" => (1.0, "pct"),
            _ => unit.StartsWith("литр", StringComparison.Ordinal) ? (1000.0, "ml") : (1.0, unit),
        };
        return Math.Round(v * factor, 3).ToString("0.###", CultureInfo.InvariantCulture) + canon;
    }

    /// <summary>Разбор названия: основа без вкуса/цвета, ключ объёма, название варианта.</summary>
    internal sealed record Parsed(MergeProduct Product, string BaseKey, string QtyKey, string BaseText, string VariantName, string[] BaseTokens, string[] BaseOrig);

    internal static Parsed Parse(MergeProduct p)
    {
        var title = (p.Title ?? "").Trim();
        var norm = Norm(title);
        if (norm.Length != title.Length)
            title = norm; // страховка: позиции слов в нормализованной и исходной строке должны совпадать
        // числа с единицами — в ключ объёма и «гасим» пробелами (позиции остаются теми же, чтобы резать исходное написание)
        var qtyKeys = new List<string>();
        var chars = norm.ToCharArray();
        foreach (Match m in QtyRegex.Matches(norm))
        {
            qtyKeys.Add(QtyKey(m.Groups[1].Value, m.Groups[2].Value));
            for (var i = m.Index; i < m.Index + m.Length; i++)
                chars[i] = ' ';
        }
        var blanked = new string(chars);

        var baseTokens = new List<string>();
        var baseOrig = new List<string>();
        var variant = new List<string>();
        var cut = new List<(int Start, int Length)>();
        foreach (Match m in TokenRegex.Matches(blanked))
        {
            var t = m.Value.Trim('\'');
            if (t.Length == 0)
                continue;
            if (NoiseWords.Contains(t))
                cut.Add((m.Index, m.Length));
            else if (IsVariantWord(t))
            {
                cut.Add((m.Index, m.Length));
                variant.Add(title.Substring(m.Index, Math.Min(m.Length, title.Length - m.Index)).Trim('\''));
            }
            else
            {
                baseTokens.Add(t);
                baseOrig.Add(title.Substring(m.Index, m.Length).Trim('\''));
            }
        }

        // основа в исходном написании: убираем слова-вкусы и шум, объём («0,5 л») остаётся
        var sb = new StringBuilder();
        var pos = 0;
        foreach (var (start, length) in cut)
        {
            if (start > title.Length)
                break;
            sb.Append(title, pos, Math.Max(0, start - pos));
            pos = Math.Min(title.Length, start + length);
        }
        if (pos < title.Length)
            sb.Append(title, pos, title.Length - pos);
        var baseText = Regex.Replace(sb.ToString(), @"\s+", " ").Trim(' ', ',', ';', '-', '/');

        var variantName = variant.Count == 0 ? "" : UpFirst(string.Join(" ", variant));
        qtyKeys.Sort(StringComparer.Ordinal);
        return new Parsed(p, string.Join(" ", baseTokens), string.Join("+", qtyKeys), baseText, variantName, baseTokens.ToArray(), baseOrig.ToArray());
    }

    private static string UpFirst(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Key(string? s) => Norm((s ?? "").Trim());

    /// <summary>Делит элементы по значению: непустые значения — свои части; пустые присоединяются к единственной непустой части, иначе образуют свою.</summary>
    private static List<List<Parsed>> Partition(List<Parsed> items, Func<Parsed, string> key)
    {
        var named = items.Where(i => key(i).Length > 0).GroupBy(key, StringComparer.Ordinal).Select(g => g.ToList()).ToList();
        var empty = items.Where(i => key(i).Length == 0).ToList();
        if (empty.Count > 0)
        {
            if (named.Count == 1)
                named[0].AddRange(empty);
            else
                named.Add(empty);
        }
        return named;
    }

    private static string Majority(IEnumerable<string?> values) =>
        values.Select(v => Key(v)).GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault() ?? "";

    private static string UnitKey(string? unit)
    {
        var u = Key(unit);
        return u is "" or "шт" or "шт." or "pcs" or "дона" or "dona" or "adet" ? "шт" : u.TrimEnd('.');
    }

    public static string GroupId(IEnumerable<string> ids)
    {
        var joined = string.Join("|", ids.OrderBy(i => i, StringComparer.Ordinal));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }

    /// <summary>Группы-кандидаты, отсортированные по уверенности (сначала сильные), потом по числу товаров. Ничего не меняет.</summary>
    public static List<MergeGroup> Find(IEnumerable<MergeProduct> products)
    {
        var parsed = new List<Parsed>();
        foreach (var p in products)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Title) || p.IsService || p.IsBundle)
                continue; // услуга и комплект товарами-вариантами не бывают; в «тихие» кандидаты их не берём
            if (!p.AllCodes().Any())
                continue; // без штрихкода нечего переносить вариантом
            var r = Parse(p);
            if (r.BaseTokens.Length == 0 || r.BaseKey.Replace(" ", "").Length < 2)
                continue; // «Клубника» целиком — вкус без основы
            parsed.Add(r);
        }

        var groups = new List<MergeGroup>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        // ── проход 1: одинаковая основа + одинаковый объём (разница — вкус/цвет или одинаковое название) ──
        foreach (var bucket in parsed.GroupBy(r => r.BaseKey + "\u0001" + r.QtyKey, StringComparer.Ordinal).Where(g => g.Count() >= 2))
            foreach (var byBrand in Partition(bucket.ToList(), r => Key(r.Product.Brand)))
                foreach (var part in Partition(byBrand, r => Key(r.Product.Category)).Where(x => x.Count >= 2))
                {
                    var anyVariant = part.Any(r => r.VariantName.Length > 0);
                    var g = Build(part, byDictionary: anyVariant, prefixDepth: 0);
                    groups.Add(g);
                    foreach (var r in part)
                        used.Add(r.Product.Id);
                }

        // ── проход 2: общее начало названия у остальных (слова вкуса словарь не знает) ──
        var rest = parsed.Where(r => !used.Contains(r.Product.Id)).ToList();
        foreach (var qtyBucket in rest.GroupBy(r => r.QtyKey, StringComparer.Ordinal))
        {
            var pool = qtyBucket.ToList();
            for (var depth = 5; depth >= 1; depth--)
            {
                var byPrefix = pool.Where(r => r.BaseTokens.Length >= depth).GroupBy(r => string.Join(" ", r.BaseTokens.Take(depth)), StringComparer.Ordinal).Where(g => g.Count() >= 2);
                foreach (var pg in byPrefix.ToList())
                {
                    var members = pg.Where(r => pool.Contains(r) && r.BaseTokens.Length - depth <= 2).ToList(); // «хвост» после общего начала — не длиннее двух слов
                    if (members.Count < 2)
                        continue;
                    foreach (var byBrand in Partition(members, r => Key(r.Product.Brand)))
                        foreach (var part in Partition(byBrand, r => Key(r.Product.Category)).Where(x => x.Count >= 2))
                        {
                            // слабая связь (одно общее слово) — только при одном и том же бренде и одной цене; от двух слов — при одной цене
                            var samePrice = part.All(r => SamePrice(r.Product.Price, part[0].Product.Price));
                            var brandKnown = part.All(r => Key(r.Product.Brand).Length > 0);
                            if (!samePrice || (depth == 1 && !brandKnown))
                                continue;
                            groups.Add(Build(part, byDictionary: false, prefixDepth: depth));
                            foreach (var r in part)
                                pool.Remove(r);
                        }
                }
            }
        }

        return groups.OrderByDescending(g => g.Score).ThenByDescending(g => g.Members.Count).ThenBy(g => g.BaseTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static MergeGroup Build(List<Parsed> part, bool byDictionary, int prefixDepth)
    {
        var majorityUnit = Majority(part.Select(r => UnitKey(r.Product.Unit)));
        var members = part.Select(r =>
        {
            var blockers = new List<MergeBlocker>();
            if (r.Product.IsWeight)
                blockers.Add(MergeBlocker.Weighed);
            if (!r.Product.AllCodes().Any())
                blockers.Add(MergeBlocker.NoBarcode);
            if (UnitKey(r.Product.Unit) != majorityUnit)
                blockers.Add(MergeBlocker.OtherUnit);
            var name = byDictionary || prefixDepth == 0
                ? r.VariantName
                : TailName(r, prefixDepth);
            return new MergeMember(r.Product, name, blockers);
        }).OrderByDescending(m => m.Product.Quantity).ThenBy(m => m.Product.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var warnings = new List<MergeWarning>();
        var prices = part.Select(r => r.Product.Price).ToList();
        var pricesSame = prices.All(x => SamePrice(x, prices[0]));
        if (!pricesSame)
            warnings.Add(MergeWarning.PricesDiffer);
        if (part.Any(r => r.Product.HasExpiry))
            warnings.Add(MergeWarning.ExpiryBatches);
        var withStock = part.Where(r => r.Product.Quantity > 0.0005 && r.Product.PurchasePrice > 0).Select(r => r.Product.PurchasePrice).ToList();
        if (withStock.Count > 1 && withStock.Any(x => !SamePrice(x, withStock[0])))
            warnings.Add(MergeWarning.PurchaseDiffers);
        var brands = part.Select(r => Key(r.Product.Brand)).ToList();
        var brandKnown = brands.All(b => b.Length > 0);
        if (!brandKnown)
            warnings.Add(MergeWarning.BrandMissing);
        var cats = part.Select(r => Key(r.Product.Category)).ToList();
        var catSame = cats.All(c => c.Length > 0 && c == cats[0]);
        if (!catSame)
            warnings.Add(MergeWarning.CategoryDiffers);

        var exactDuplicate = !byDictionary && prefixDepth == 0; // одинаковое название при разных штрихкодах
        var score = byDictionary ? 85 : exactDuplicate ? 80 : prefixDepth >= 2 ? 50 : 35; // общее начало без словаря — только «вероятно» или «сомнительно»
        score += brandKnown && brands.Distinct().Count() == 1 ? 5 : -10;
        score += catSame ? 5 : -10;
        score += pricesSame ? 5 : -25;
        if (part.Count > 5)
            score -= byDictionary || exactDuplicate ? 3 : 10;
        score = Math.Clamp(score, 5, 98);

        var main = part.OrderByDescending(r => r.VariantName.Length == 0).ThenBy(r => r.BaseText.Length).First();
        var brand = part.Select(r => r.Product.Brand).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b));
        var category = part.Select(r => r.Product.Category).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        return new MergeGroup(GroupId(part.Select(r => r.Product.Id)), BaseTitleOf(part, prefixDepth, main), members, score, brand, category, byDictionary, warnings);
    }

    private static string TailName(Parsed r, int depth) =>
        UpFirst(string.Join(" ", r.BaseOrig.Skip(depth)));

    private static string BaseTitleOf(List<Parsed> part, int prefixDepth, Parsed main)
    {
        if (prefixDepth == 0)
            return main.BaseText.Length > 0 ? main.BaseText : main.Product.Title.Trim();
        // общее начало, в написании самого короткого названия
        var shortest = part.OrderBy(r => r.Product.Title.Length).First();
        return string.Join(" ", shortest.BaseOrig.Take(prefixDepth));
    }
}
