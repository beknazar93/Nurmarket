using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-10-02, владелец: «в ветаптеке ты не добавил альтернативу, если товар закончился, как в
/// аптеках». Подбор замены товару, которого нет в наличии, как в аптеке — по порядку:
/// 1) то же действующее вещество (из описания товара: «Действующее вещество — фипронил 10%», «д.в.:»,
///    «Состав:»); 2) общие значимые слова в названии («капли … блох … кошек»); 3) та же категория
/// (как было раньше, 2026-09-03). Только товары в наличии, кроме самого товара.
/// Считается по кэшу каталога на кассе — без запросов к серверу.</summary>
public static class ProductAlternatives
{
    private static readonly Regex ActiveSubstance = new(
        @"(?:действующ\w*\s+веществ\w*|д\.\s?в\.|состав|active\s+ingredient|таасир\s+этүүчү\s+зат)\s*[:\-—–]?\s*([^\d,.;:()\n]{3,40})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "тест", "для", "от", "и", "с", "со", "в", "на", "по", "из", "до", "шт", "мл", "мг", "гр", "кг", "уп", "упаковка",
        "таблеток", "таблетки", "штук", "взрослых", "the", "for", "and",
    };

    /// <summary>Действующее вещество из описания (в нижнем регистре) или null.</summary>
    public static string? ActiveSubstanceOf(CatalogProductTileVm p)
    {
        if (string.IsNullOrWhiteSpace(p.Description))
            return null;
        var m = ActiveSubstance.Match(p.Description);
        if (!m.Success)
            return null;
        var s = m.Groups[1].Value.Trim().Trim('—', '-', '–', ' ').ToLowerInvariant();
        return s.Length >= 3 ? s : null;
    }

    /// <summary>Замены товару: лучшие сверху. reason — почему предложен (для подписи).</summary>
    public static List<(CatalogProductTileVm Product, string Reason)> Find(CatalogProductTileVm current, IEnumerable<CatalogProductTileVm> catalog, int limit = 10)
    {
        var substance = ActiveSubstanceOf(current);
        var words = Words(current.Title);
        var scored = new List<(CatalogProductTileVm P, double Score, string Reason)>();
        foreach (var p in catalog)
        {
            if (p.Quantity <= 1e-6 || string.Equals(p.Id, current.Id, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(p.Title))
                continue;
            double score = 0;
            string reason = "";
            if (substance != null && ActiveSubstanceOf(p) is { } other
                && (other.Contains(substance, StringComparison.OrdinalIgnoreCase) || substance.Contains(other, StringComparison.OrdinalIgnoreCase)))
            {
                score += 100;
                reason = Tr.T($"то же действующее вещество: {substance}", $"ошол эле таасир этүүчү зат: {substance}", $"same active ingredient: {substance}",
                    $"aynı etken madde: {substance}", $"bir xil ta'sir etuvchi modda: {substance}");
            }
            var common = Words(p.Title).Count(words.Contains);
            if (common > 0)
            {
                score += 10 * common;
                if (reason.Length == 0)
                    reason = Tr.T("похожее название", "окшош аталыш", "similar name", "benzer ad", "o'xshash nom");
            }
            var sameCategory = !string.IsNullOrWhiteSpace(current.Category) && string.Equals(p.Category, current.Category, StringComparison.OrdinalIgnoreCase);
            if (sameCategory)
            {
                score += 5;
                if (reason.Length == 0)
                    reason = Tr.T("та же категория", "ошол эле категория", "same category", "aynı kategori", "o'sha kategoriya");
            }
            if (!string.IsNullOrWhiteSpace(current.Brand) && string.Equals(p.Brand, current.Brand, StringComparison.OrdinalIgnoreCase))
                score += 2;
            // Без общего вещества, слова или категории — это не замена.
            if (score >= 5)
                scored.Add((p, score, reason));
        }

        // Нашлись настоящие аналоги (вещество или название) — только они: каплям от блох не предлагаем корм.
        if (scored.Any(x => x.Score >= 10))
            scored = scored.Where(x => x.Score >= 10).ToList();
        return scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.P.Quantity).Take(limit)
            .Select(x => (x.P, x.Reason)).ToList();
    }

    private static HashSet<string> Words(string? title) =>
        Regex.Split((title ?? "").ToLowerInvariant(), @"[^\p{L}]+")
            .Where(w => w.Length >= 4 && !StopWords.Contains(w))
            .Select(w => w.Length > 6 ? w[..6] : w)   // основа слова: «кошек»/«кошки» → одна
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
