using System.Net.Http;
using System.Text.RegularExpressions;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-07, владелец (ссылка barcode-list.ru/barcode/RU/barcode-4870002590986/Поиск.htm): «при добавлении новых товаров,
/// если наименование есть, поставь штрихкод отсюда, но дай проверить и сверить с товаром». barcode-list.ru — открытая база
/// штрихкодов, которые магазины вносят сами: поиск по названию (Поиск.htm?barcode=название) отдаёт таблицу «штрихкод — название —
/// единица — рейтинг» (рейтинг — сколько раз этот штрихкод под этим названием встречался). Здесь — до 3 вариантов штрихкода
/// для нашего названия: сначала по совпадению слов и чисел названия (вес «50г» тоже сверяется), затем по рейтингу. Ничего не
/// ставит сам: варианты показываются владельцу со своим названием из базы — сверить и нажать «Поставить».</summary>
public static class BarcodeListLookup
{
    public sealed record Suggestion(string Barcode, string Name, int Rating);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly Dictionary<string, List<Suggestion>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Слова (от 3 букв, по основе — первые 5 букв) и числа названия: «Батончик Mars 50г» → батон, mars, 50.</summary>
    private static (HashSet<string> Words, HashSet<string> Numbers) Tokens(string name)
    {
        var lower = name.ToLowerInvariant().Replace('ё', 'е');
        var words = Regex.Matches(lower, @"\p{L}{3,}").Select(m => m.Value.Length > 5 ? m.Value[..5] : m.Value).ToHashSet();
        var numbers = Regex.Matches(lower, @"\d+(?:[.,]\d+)?").Select(m => m.Value.Replace(',', '.')).ToHashSet();
        return (words, numbers);
    }

    public static async Task<List<Suggestion>> FindByNameAsync(string name, CancellationToken ct)
    {
        name = (name ?? "").Trim();
        if (name.Length < 4)
            return new();
        lock (Cache)
            if (Cache.TryGetValue(name, out var cached))
                return cached;
        var result = new List<Suggestion>();
        // Накладная на 10 новых товаров — не 10 запросов разом: бережно к бесплатной базе.
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var url = "https://barcode-list.ru/barcode/RU/%D0%9F%D0%BE%D0%B8%D1%81%D0%BA.htm?barcode=" + Uri.EscapeDataString(name);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("ru");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                PosLogger.Log($"Штрихкод по названию: barcode-list.ru ответил {(int)response.StatusCode}.", "CATALOG");
                return result;
            }
            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var rows = new List<Suggestion>();
            foreach (Match row in Regex.Matches(html, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline))
            {
                var cells = Regex.Matches(row.Groups[1].Value, @"<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
                    .Select(c => System.Net.WebUtility.HtmlDecode(Regex.Replace(c.Groups[1].Value, "<[^>]+>", "")).Trim()).ToList();
                // № | штрихкод | название | единица | рейтинг
                if (cells.Count < 5 || !Regex.IsMatch(cells[1], @"^\d{8,14}$") || cells[2].Length == 0
                    || cells[2].Contains("НЕ НАЙДЕН", StringComparison.OrdinalIgnoreCase))
                    continue;
                rows.Add(new Suggestion(cells[1], cells[2], int.TryParse(cells[4], out var r) ? r : 1));
            }
            var (ourWords, ourNumbers) = Tokens(name);
            result = rows.GroupBy(r => r.Barcode)
                .Select(g =>
                {
                    // Название из базы, лучше всего совпавшее с нашим, — его и показываем для сверки.
                    var best = g.Select(r =>
                    {
                        var (w, n) = Tokens(r.Name);
                        return (Row: r, Words: ourWords.Count(w.Contains), NumbersOk: ourNumbers.Count == 0 || ourNumbers.All(n.Contains));
                    }).OrderByDescending(x => x.Words).ThenByDescending(x => x.NumbersOk).ThenByDescending(x => x.Row.Rating).First();
                    return (Barcode: g.Key, best.Row.Name, best.Words, best.NumbersOk, Rating: g.Sum(r => r.Rating));
                })
                // Совпало хотя бы половина слов названия (и не меньше одного), вес/объём — не другой.
                .Where(x => x.Words >= Math.Max(1, (ourWords.Count + 1) / 2) && x.NumbersOk)
                .OrderByDescending(x => x.Words).ThenByDescending(x => x.Rating)
                .Take(3)
                .Select(x => new Suggestion(x.Barcode, x.Name, x.Rating))
                .ToList();
            PosLogger.Log($"Штрихкод по названию «{name}»: строк в базе {rows.Count}, вариантов {result.Count}.", "CATALOG");
            lock (Cache)
                Cache[name] = result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Штрихкод по названию «{name}»: barcode-list.ru не ответил ({ex.Message}).", "CATALOG");
        }
        finally
        {
            Gate.Release();
        }
        return result;
    }
}
