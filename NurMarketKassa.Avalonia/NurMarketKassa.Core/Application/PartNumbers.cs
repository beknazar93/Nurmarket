using System.Text;

namespace NurMarketKassa.Core.Application;

/// <summary>
/// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ2): нормализация каталожных номеров. Клиент называет «0 986 452-041», «04465-0К090» (К — кириллическая),
/// «W 712/75»; сервер пробелы и дефисы НЕ нормализует (ответ Умара 19.11), поэтому касса хранит и ищет номер в одном виде:
/// заглавные латинские буквы и цифры, без пробелов, дефисов, точек, слэшей и прочих знаков.
/// Кириллица, похожая на латиницу по форме (А В Е К М Н О Р С Т Х У), и по расположению клавиши, когда формы нет (Й→Q, Ц→W), приводится к латинице.
/// Чистая функция без зависимостей: Portable и Android берут её вместе с приложением.
/// </summary>
public static class PartNumbers
{
    /// <summary>Самый длинный номер, который кассa хранит (значение атрибута на сервере — до 64 знаков).</summary>
    public const int MaxLength = 64;

    /// <summary>Рейтинг совпадения: 0 — номер совпал целиком, 1 — запрос в начале номера, 2 — запрос внутри номера, 3 — не совпало.</summary>
    public const int NoMatch = 3;

    private static char MapLetter(char c) => c switch
    {
        'А' or 'а' => 'A',
        'В' or 'в' => 'B',
        'Е' or 'е' => 'E',
        'К' or 'к' => 'K',
        'М' or 'м' => 'M',
        'Н' or 'н' => 'H',
        'О' or 'о' => 'O',
        'Р' or 'р' => 'P',
        'С' or 'с' => 'C',
        'Т' or 'т' => 'T',
        'Х' or 'х' => 'X',
        'У' or 'у' => 'Y',
        'Й' or 'й' => 'Q',
        'Ц' or 'ц' => 'W',
        _ => char.ToUpperInvariant(c),
    };

    /// <summary>Номер в одном виде: «0 986 452-041» → «0986452041», «04465-0К090» (К — кириллица) → «044650K090». Пусто, если букв и цифр нет. Не длиннее <see cref="MaxLength"/>.</summary>
    public static string Norm(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        foreach (var raw in value)
        {
            if (!char.IsLetterOrDigit(raw))
                continue;
            var c = MapLetter(raw);
            // Остальная кириллица (Ж, Щ…) и другие буквы остаются как есть: из них номер не складывается, но запрос не должен «склеиваться» с чужими.
            sb.Append(c);
            if (sb.Length >= MaxLength)
                break;
        }
        return sb.ToString();
    }

    /// <summary>«Нестрогий» вид для второй попытки: буква O → цифра 0 (на бумаге и в голосе их путают).</summary>
    public static string Loose(string? normalized) => string.IsNullOrEmpty(normalized) ? "" : normalized.Replace('O', '0');

    /// <summary>Запрос в поле поиска «похож на номер»: после очистки не короче 3 знаков, есть цифра, а в исходной строке нет слов-названий (больше одного «длинного» слова).
    /// «0 986 452 041», «W 712/75», «04465-0K090» — да; «фильтр масляный», «колодки камри» — нет.</summary>
    public static bool LooksLikePart(string? query)
    {
        var n = Norm(query);
        if (n.Length < 3 || !n.Any(char.IsDigit))
            return false;
        var words = (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var longAlpha = 0;
        foreach (var w in words)
            if (w.Count(char.IsLetter) >= 4 && !w.Any(char.IsDigit))
                longAlpha++;
        return longAlpha == 0;
    }

    /// <summary>Рейтинг одного номера (уже нормализованного) относительно запроса: 0 — равен, 1 — начинается, 2 — содержит, 3 — не совпал.
    /// Сначала сравнение как есть, потом нестрогое (O ↔ 0) с тем же рейтингом, но не лучше 1 — точное совпадение важнее.</summary>
    public static int RankOne(string numberNorm, string queryNorm, string queryLoose)
    {
        if (numberNorm.Length == 0 || queryNorm.Length == 0)
            return NoMatch;
        if (numberNorm.Equals(queryNorm, StringComparison.Ordinal))
            return 0;
        if (numberNorm.StartsWith(queryNorm, StringComparison.Ordinal))
            return 1;
        if (numberNorm.Contains(queryNorm, StringComparison.Ordinal))
            return 2;
        var loose = Loose(numberNorm);
        if (loose.Equals(queryLoose, StringComparison.Ordinal))
            return 1;
        if (loose.StartsWith(queryLoose, StringComparison.Ordinal))
            return 2;
        return loose.Contains(queryLoose, StringComparison.Ordinal) ? 2 : NoMatch;
    }

    /// <summary>Лучший рейтинг по набору номеров товара (артикул, каталожный номер, OEM, кроссы).</summary>
    public static int Rank(string queryNorm, IEnumerable<string?> numbersNorm)
    {
        if (queryNorm.Length == 0)
            return NoMatch;
        var loose = Loose(queryNorm);
        var best = NoMatch;
        foreach (var n in numbersNorm)
        {
            if (string.IsNullOrEmpty(n))
                continue;
            var r = RankOne(n, queryNorm, loose);
            if (r < best)
            {
                best = r;
                if (best == 0)
                    break;
            }
        }
        return best;
    }

    /// <summary>Разобрать поле «OEM / кросс-номера»: по одному в строке или через запятую, точку с запятой, «|». Пустые и повторы убираются.
    /// В строке вида «Bosch 0 451 103 316» первое слово из 3+ букв — название бренда, номер — остальное; «W 712/94» и «OC 983» (слово короче 3 букв) остаются номером.
    /// Результат — нормализованные номера (<see cref="Norm"/>).</summary>
    public static List<string> ParseList(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in text.Split(new[] { '\n', '\r', ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var n = Norm(StripBrand(part));
            if (n.Length > 0 && seen.Add(n))
                result.Add(n);
        }
        return result;
    }

    /// <summary>Убрать название бренда в начале строки: «Mann W 811/80» → «W 811/80». Только если дальше есть что оставить и в нём есть цифра.</summary>
    public static string StripBrand(string line)
    {
        var s = line.Trim();
        var space = s.IndexOf(' ');
        if (space < 3)
            return s;
        var first = s[..space];
        if (!first.All(char.IsLetter) || first.Length < 3)
            return s;
        var rest = s[(space + 1)..];
        return rest.Any(char.IsDigit) ? rest : s;
    }
}
