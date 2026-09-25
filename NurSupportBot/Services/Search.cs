using System.Text;
using NurSupportBot.Data;

namespace NurSupportBot.Services;

/// <summary>Поиск инструкции по вопросу обычными словами («как сделать возврат?»). Без ИИ:
/// сравниваются основы слов вопроса с названием, ключевыми словами и текстом инструкции.
/// Основа — слово без окончания, не длиннее 6 букв: «возврат», «возврата», «возвратом» сходятся,
/// а «интернет» и «интерфейс» — нет. Синонимы («вернуть») добавляются в ключевые слова инструкции.</summary>
public static class Search
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "как", "где", "что", "чтобы", "это", "мне", "меня", "мой", "моя", "мои", "можно", "нужно", "надо",
        "сделать", "делать", "почему", "когда", "если", "или", "для", "при", "под", "над", "без",
        "есть", "нет", "уже", "еще", "ещё", "вот", "там", "тут", "так", "там", "его", "она", "они", "оно",
        "the", "в", "во", "на", "и", "а", "но", "не", "ни", "по", "с", "со", "у", "к", "ко", "о", "об", "от", "до",
        "из", "за", "же", "ли", "бы", "то", "я", "ты", "вы", "мы", "он", "их", "все", "всё", "весь",
        "пожалуйста", "подскажите", "скажите", "помогите", "хочу", "могу", "может",
    };

    public sealed record Hit(Node Node, double Score);

    public static List<Hit> Find(IEnumerable<Node> articles, string query, int take = 3)
    {
        var queryStems = Stems(query).Distinct().ToList();
        if (queryStems.Count == 0)
            return new();

        var hits = new List<Hit>();
        foreach (var node in articles)
        {
            var title = Stems(node.Title).ToList();
            var keys = Stems(node.Keywords ?? "").ToList();
            var text = Stems(node.Text ?? "").ToList();
            double score = 0;
            var matched = 0;
            foreach (var stem in queryStems)
            {
                var s = title.Any(t => Same(t, stem)) || keys.Any(k => Same(k, stem)) ? 3.0
                    : text.Any(t => Same(t, stem)) ? 0.7 : 0;
                if (s > 0)
                    matched++;
                score += s;
            }
            if (matched == 0)
                continue;
            // Доля совпавших слов вопроса: «возврат товара» точнее попадает в инструкцию, где есть оба слова.
            score *= 0.5 + 0.5 * matched / queryStems.Count;
            // Все слова вопроса есть в самом названии («не печатается чек» → «Не печатается чек — что делать?»)
            // — это точнее, чем совпадение по ключевым словам соседней инструкции.
            if (queryStems.Count >= 2 && queryStems.All(q => title.Any(t => Same(t, q))))
                score += 1;
            hits.Add(new Hit(node, score));
        }

        return hits
            .Where(h => h.Score >= 1.5)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Node.Title.Length)
            .Take(take)
            .ToList();
    }

    /// <summary>Основы совпадают, если равны или одна продолжает другую («верну» — «вернул»).</summary>
    private static bool Same(string a, string b) =>
        a == b || (Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));

    // Окончания, которые отрезаются от слова (от длинных к коротким): «возврата», «возвратом» → «возврат».
    private static readonly string[] Endings =
    {
        "иями", "ями", "ами", "ого", "его", "ому", "ему", "ыми", "ими", "ться", "ешь", "ишь",
        "ой", "ей", "ий", "ый", "ая", "яя", "ое", "ее", "ую", "юю", "ам", "ям", "ах", "ях", "ом", "ем", "ов", "ев",
        "ть", "ет", "ют", "ут", "ит", "ат", "ят", "ся",
        "ы", "и", "а", "я", "о", "е", "у", "ю", "ь",
    };

    private static string Stem(string word)
    {
        foreach (var ending in Endings)
        {
            if (word.Length - ending.Length >= 3 && word.EndsWith(ending, StringComparison.Ordinal))
            {
                word = word[..^ending.Length];
                break;
            }
        }
        return word.Length > 6 ? word[..6] : word;
    }

    public static IEnumerable<string> Stems(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant().Replace('ё', 'е'))
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        foreach (var word in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length < 2 || StopWords.Contains(word))
                continue;
            yield return Stem(word);
        }
    }
}
