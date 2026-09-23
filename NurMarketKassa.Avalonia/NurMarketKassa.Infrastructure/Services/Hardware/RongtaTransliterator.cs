using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Кириллица → латиница для названий товаров, отправляемых на весы Rongta напрямую
/// (свой TCP-сервер, RongtaTcpProtocol) — прошивка весов не печатает кириллицу (та же
/// проблема, что уже решена на сайте флагом translit=1 для экспорта .txp, см.
/// scales-and-plu.md, раздел 10 "Типичные проблемы": "Кириллица «?????» на Rongta").
/// Стандартная ГОСТ-подобная посимвольная таблица — не идеальна для казахских/кыргызских
/// спецсимволов вне русского алфавита, но лучше вопросиков на этикетке.</summary>
public static class RongtaTransliterator
{
    private static readonly Dictionary<char, string> Map = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d",
        ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n",
        ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        // Кыргызский/казахский алфавит поверх русского
        ['ң'] = "ng", ['ү'] = "u", ['ұ'] = "u", ['қ'] = "q", ['ғ'] = "g",
        ['ө'] = "o", ['һ'] = "h", ['і'] = "i",
    };

    public static string Transliterate(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var lower = char.ToLowerInvariant(ch);
            if (Map.TryGetValue(lower, out var latin))
            {
                sb.Append(char.IsUpper(ch) && latin.Length > 0
                    ? char.ToUpperInvariant(latin[0]) + latin[1..]
                    : latin);
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}
