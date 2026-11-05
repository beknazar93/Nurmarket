using System.Text;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-11-03: данные для умного сопоставления названий накладной с товарами склада (InvoiceNameParser / InvoiceMatcher): сокращения («мол.» → «молок…»),
// единицы объёма и веса, звуковой ключ латиница ↔ кириллица. Только данные и две маленькие функции — список расширяется правкой таблиц ниже.
// Правила нарочно простые и переносятся в мобилку (invoice_match.dart) один к одному.

public static class InvoiceNameData
{
    /// <summary>Сокращение (нижний регистр, ё → е) → основа слова. Основа короче полного слова, чтобы работало правило «одно слово — начало другого»
    /// (4+ букв): «газ» → «газирован» совпадёт и с «газированный», и с «газированная». Применяется к обеим сторонам, поэтому лишнего не ломает.</summary>
    public static readonly IReadOnlyDictionary<string, string> Abbreviations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["мол"] = "молок", ["молочн"] = "молочн",
        ["конд"] = "кондитерск", ["кондит"] = "кондитерск",
        ["шок"] = "шоколад", ["шоколадн"] = "шоколад",
        ["печ"] = "печень", ["печен"] = "печень",
        ["нап"] = "напит", ["напиток"] = "напит", ["напитки"] = "напит",
        ["газ"] = "газирован", ["газир"] = "газирован",
        ["безалк"] = "безалкогольн", ["бзалк"] = "безалкогольн",
        ["мин"] = "минеральн", ["минер"] = "минеральн",
        ["йог"] = "йогурт",
        ["сгущ"] = "сгущен",
        ["колб"] = "колбас",
        ["консерв"] = "консерв", ["конс"] = "консерв",
        ["макар"] = "макарон",
        ["помид"] = "помидор",
        ["шамп"] = "шампун",
        ["дезод"] = "дезодорант",
        ["туал"] = "туалетн",
        ["стир"] = "стираль",
        ["ср-во"] = "средств", ["срво"] = "средств", ["ср-ва"] = "средств", ["срва"] = "средств",
        ["стекл"] = "стекл",
        ["пласт"] = "пластик",
        ["ж/б"] = "банк", ["жб"] = "банк", ["банка"] = "банк", ["банки"] = "банк",
        ["ст/б"] = "стекл", ["стб"] = "стекл",
        ["пл/б"] = "пластик", ["плб"] = "пластик",
        ["т/п"] = "тетрапак",
        ["б/а"] = "безалкогольн",
    };

    /// <summary>Слова-единицы без числа и счётные слова: из названия выкидываются («кг», «шт», «уп» не отличают один товар от другого).</summary>
    public static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "шт", "штук", "штуки", "штука", "pcs", "pc", "piece", "pieces", "dona", "дана",
        "уп", "упак", "упаковка", "упаковки", "pack", "pkg",
        "кг", "kg", "гр", "грамм", "г", "g", "gr", "л", "литр", "l", "lt", "мл", "ml",
        "gb", "гб", "tb", "тб", "mb", "мб",
    };

    /// <summary>Число + единица: «1 л», «1литр», «0,5л», «500 мл», «200г», «0,2 кг», «100 гр.»; «шт», «gb» дают просто число (единица отбрасывается).
    /// Группы: 1 — число, 2 — единица.</summary>
    public const string QuantityPattern =
        @"(?<![\p{L}\d.,])(\d+(?:[.,]\d+)?)[\s\-]*(килограмм\w*|кг|kg|грамм\w*|гр|г|gram\w*|gr|g|миллилитр\w*|мл|ml|литр\w*|ltr|lt|л|l|штук\w*|шт|pcs|pc|gb|гб|tb|тб|mb|мб)(?![\p{L}])";

    /// <summary>Вид и множитель единицы: объём в мл, масса в граммах; null — единица «ничего не значит» (шт, GB): число остаётся обычным числом.</summary>
    public static (char Kind, double Factor)? UnitOf(string unit)
    {
        var u = unit.ToLowerInvariant().Replace('ё', 'е');
        if (u.StartsWith("килограмм", StringComparison.Ordinal) || u is "кг" or "kg")
            return ('m', 1000);
        if (u.StartsWith("грамм", StringComparison.Ordinal) || u.StartsWith("gram", StringComparison.Ordinal) || u is "гр" or "г" or "gr" or "g")
            return ('m', 1);
        if (u.StartsWith("миллилитр", StringComparison.Ordinal) || u is "мл" or "ml")
            return ('v', 1);
        if (u.StartsWith("литр", StringComparison.Ordinal) || u is "л" or "l" or "lt" or "ltr")
            return ('v', 1000);
        return null;
    }

    // ------------------------------------------------------------------ звуковой ключ

    private static readonly Dictionary<char, string> CyrToLat = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "i",
        ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f",
        ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "", ['ы'] = "i", ['ь'] = "", ['э'] = "e", ['ю'] = "u", ['я'] = "a",
        ['ү'] = "u", ['ө'] = "o", ['ң'] = "n", ['ұ'] = "u", ['қ'] = "k", ['ғ'] = "g", ['һ'] = "h", ['ә'] = "a", ['і'] = "i",
    };

    /// <summary>Звуковой ключ слова: кириллица → латиница, затем одинаковая правка латиницы («ck» → k, «c» перед e/i/y → s, иначе k, «y» → i, «x» → ks, «w» → v),
    /// повторы букв схлопываются. «Кока»/«Coca» → koka, «Фанта»/«Fanta» → fanta, «Сникерс»/«Snickers» → snikers. Слово без букв остаётся как есть.</summary>
    public static string Phonetic(string word)
    {
        var sb = new StringBuilder(word.Length + 4);
        foreach (var ch in word)
        {
            var c = char.ToLowerInvariant(ch);
            if (CyrToLat.TryGetValue(c, out var lat))
                sb.Append(lat);
            else if (c > 127 && char.IsLetter(c) && AnyAscii.Transliteration.Transliterate(c.ToString()) is { Length: > 0 } ascii)
                // 2026-11-05: буква не из нашей таблицы (ş ğ ı ç ö ü ў ҳ ñ é и т. п.) — запасной транслит AnyAscii (ISC), «ş» → «s», «ў» → «u».
                sb.Append(ascii.ToLowerInvariant());
            else
                sb.Append(c);
        }
        var s = sb.ToString()
            .Replace("tsch", "ch").Replace("sch", "sh").Replace("tch", "ch").Replace("ck", "k").Replace("ph", "f").Replace("kh", "h")
            .Replace("q", "k").Replace("x", "ks").Replace("w", "v").Replace("y", "i").Replace("j", "zh");
        // «c»: перед e/i — «s», перед h — часть «ch» (оставляем), иначе — «k».
        var t = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == 'c')
            {
                var next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (next == 'h')
                    t.Append('c');
                else
                    t.Append(next is 'e' or 'i' ? 's' : 'k');
            }
            else
                t.Append(c);
        }
        // схлопнуть повторы («pp» → p, «ll» → l)
        var r = new StringBuilder(t.Length);
        foreach (var c in t.ToString())
        {
            if (r.Length == 0 || r[^1] != c)
                r.Append(c);
        }
        return r.ToString();
    }
}
