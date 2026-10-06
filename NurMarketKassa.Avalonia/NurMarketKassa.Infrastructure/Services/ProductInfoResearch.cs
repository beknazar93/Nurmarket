using System.Collections.Concurrent;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, владелец: «поиск информации в интернете по товарам, если нужно, сделай возможным ИИ-ассистенту».
/// Когда просят дополнить, описать или заполнить карточку товара, программа сама ищет сведения о товаре до вопроса ИИ:
/// 1) открытые базы по штрихкоду (Open Food Facts, Open Beauty Facts, Open Products Facts — бесплатно, без ключа): бренд,
///    страна, объём/вес, состав, категория;
/// 2) интернет — поиск Google в Gemini или Groq (TelegramAiChat.AskWebAsync): факты со страниц о товаре.
/// Найденное уходит ИИ блоком «НАЙДЕНО В ИНТЕРНЕТЕ» — описание, страну и бренд он берёт оттуда, а не придумывает;
/// ссылки-источники показываются под ответом советника. Результат по товару помнится час.</summary>
public static class ProductInfoResearch
{
    public sealed record Finding(CatalogProductTileVm Product, string Text, IReadOnlyList<TelegramAiChat.WebSource> Sources);

    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, (DateTime At, Finding Finding)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(1);

    private static readonly (string Name, string Host)[] OpenBases =
    {
        ("Open Food Facts", "world.openfoodfacts.org"),
        ("Open Beauty Facts", "world.openbeautyfacts.org"),
        ("Open Products Facts", "world.openproductsfacts.org"),
    };

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // Open Food Facts просит указывать программу в User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NurMarketKassa/1.17 (+https://nurcrm.kg)");
        return http;
    }

    /// <summary>Просьба о сведениях о товаре: дополнить, описать, заполнить карточку, найти состав/страну/производителя.</summary>
    public static bool LooksLikeInfoRequest(string question)
    {
        var t = (question ?? "").ToLowerInvariant();
        string[] words =
        {
            "дополн", "заполн", "опиш", "описан", "информац", "сведени", "карточк", "состав", "производ", "страна", "страну", "бренд",
            "характерист", "в интернете", "в инете", "найди про", "что за товар", "толукта", "маалымат", "сүрөттө", "кура",
        };
        return words.Any(t.Contains);
    }

    /// <summary>Сведения о товарах (не больше трёх, по два одновременно).</summary>
    public static async Task<List<Finding>> ResearchAsync(IReadOnlyList<CatalogProductTileVm> products, CancellationToken ct)
    {
        var list = products.Take(3).ToList();
        var results = new Finding?[list.Count];
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(list.Select(async (p, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[i] = await ResearchOneAsync(p, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Сведения о товаре «{p.Title}»: не найдены ({ex.Message}).", "WARNING");
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return results.Where(r => r != null).Select(r => r!).ToList();
    }

    private static async Task<Finding> ResearchOneAsync(CatalogProductTileVm p, CancellationToken ct)
    {
        if (Cache.TryGetValue(p.Id, out var cached) && DateTime.UtcNow - cached.At < CacheAge)
            return cached.Finding;

        var parts = new List<string>();
        var sources = new List<TelegramAiChat.WebSource>();
        var barcode = (p.Barcode ?? "").Trim();
        if (barcode.Length is >= 8 and <= 14 && barcode.All(char.IsDigit))
        {
            foreach (var (name, host) in OpenBases)
            {
                var facts = await OpenBaseAsync(host, barcode, ct).ConfigureAwait(false);
                if (facts is null)
                    continue;
                parts.Add($"{name} (штрихкод {barcode}): {facts}");
                sources.Add(new TelegramAiChat.WebSource($"{name}: {p.Title}", $"https://{host}/product/{barcode}"));
                break;
            }
        }

        // Интернет: поиск DuckDuckGo (бесплатно, без ключа, ~1,5 с) — заголовки и выдержки первых страниц; ИИ берёт из них факты.
        // 2026-10-06: Groq с поиском отвечал по 60 с и упирался в лимит — весь ответ советника шёл 3 минуты.
        var (snippets, found) = await DuckDuckGoAsync($"{p.Title} {barcode}".Trim(), ct).ConfigureAwait(false);
        if (found.Count < 2 && barcode.Length > 0)
            (snippets, found) = await DuckDuckGoAsync(p.Title, ct).ConfigureAwait(false);
        if (snippets.Length > 0)
        {
            parts.Add("Поиск в интернете (заголовки и выдержки страниц):\n" + snippets);
            sources.AddRange(found.Take(4));
        }
        else if (!TelegramAiChat.WebSearchUnavailable && TelegramAiChat.IsConfigured)
        {
            // Запасной путь — поиск Google в Gemini (если ключ его позволяет), не дольше 25 с.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(25));
            var system = "Ты собираешь сведения о товаре для карточки магазина. Ответь ТОЛЬКО фактами с найденных страниц: производитель и бренд, "
                         + "страна, состав или материал, объём/вес, назначение. 4–8 коротких строк, по-русски. Если товар не нашёлся — «Не нашлось».";
            try
            {
                var (answer, webSources, _) = await TelegramAiChat.AskWebAsync(system, $"Товар: «{p.Title}» {barcode}".Trim(), limit.Token).ConfigureAwait(false);
                if (answer is { Length: > 0 } && !answer.Trim().StartsWith("Не нашлось", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add("Интернет: " + answer.Trim());
                    sources.AddRange(webSources);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
        }

        var finding = new Finding(p, parts.Count > 0 ? string.Join("\n", parts) : "в открытых базах и в интернете сведений не нашлось", sources);
        Cache[p.Id] = (DateTime.UtcNow, finding);
        PosLogger.Log($"Сведения о товаре «{p.Title}»: найдено источников {sources.Count}.", "INFO");
        return finding;
    }

    /// <summary>Поиск DuckDuckGo (html-версия): «1. заголовок — выдержка (сайт)» для первых 6 результатов и ссылки.</summary>
    /// <summary>2026-10-06: адреса страниц из поиска DuckDuckGo (без ключа) — например, чтобы взять с них фото товара.</summary>
    public static async Task<IReadOnlyList<string>> SearchPageUrlsAsync(string query, CancellationToken ct) =>
        (await DuckDuckGoAsync(query, ct).ConfigureAwait(false)).Sources.Select(s => s.Uri).ToList();

    private static async Task<(string Text, List<TelegramAiChat.WebSource> Sources)> DuckDuckGoAsync(string query, CancellationToken ct)
    {
        var sources = new List<TelegramAiChat.WebSource>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query));
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ("", sources);
            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var links = System.Text.RegularExpressions.Regex.Matches(html, "class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", System.Text.RegularExpressions.RegexOptions.Singleline);
            var snippets = System.Text.RegularExpressions.Regex.Matches(html, "class=\"result__snippet\"[^>]*>(.*?)</a>", System.Text.RegularExpressions.RegexOptions.Singleline);
            static string Clean(string x) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(x, "<[^>]+>", "")).Trim();
            var lines = new List<string>();
            for (var i = 0; i < Math.Min(6, Math.Min(links.Count, snippets.Count)); i++)
            {
                var url = System.Net.WebUtility.HtmlDecode(links[i].Groups[1].Value);
                // Ссылки DuckDuckGo — через переход «/l/?uddg=<настоящий адрес>».
                var m = System.Text.RegularExpressions.Regex.Match(url, "[?&]uddg=([^&]+)");
                if (m.Success)
                    url = Uri.UnescapeDataString(m.Groups[1].Value);
                if (url.StartsWith("//"))
                    url = "https:" + url;
                if (url.Contains("duckduckgo.com/y.js") || url.Contains("ad_domain"))
                    continue; // реклама
                var title = Clean(links[i].Groups[2].Value);
                var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
                lines.Add($"{lines.Count + 1}. {title} — {Clean(snippets[i].Groups[1].Value)} ({host})");
                sources.Add(new TelegramAiChat.WebSource(title.Length > 0 ? title : host, url));
            }
            return (string.Join("\n", lines), sources);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Сведения о товаре: поиск DuckDuckGo не удался ({ex.Message}).", "INFO");
            return ("", sources);
        }
    }

    /// <summary>Карточка из открытой базы по штрихкоду — строкой фактов; null — нет такого товара.</summary>
    private static async Task<string?> OpenBaseAsync(string host, string barcode, CancellationToken ct)
    {
        try
        {
            var url = $"https://{host}/api/v2/product/{barcode}.json?fields=product_name,product_name_ru,brands,quantity,countries,"
                      + "categories,ingredients_text_ru,ingredients_text,manufacturing_places,origins,labels";
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.Object)
                return null;
            string? S(string name) => product.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
            var facts = new List<string>();
            void Add(string label, string? value)
            {
                if (value is { Length: > 0 })
                    facts.Add($"{label}: {(value.Length > 400 ? value[..400] + "…" : value)}");
            }
            Add("название", S("product_name_ru") ?? S("product_name"));
            Add("бренд", S("brands"));
            Add("объём/вес", S("quantity"));
            Add("страны продажи", S("countries"));
            Add("место производства", S("manufacturing_places") ?? S("origins"));
            Add("категории", S("categories"));
            Add("состав", S("ingredients_text_ru") ?? S("ingredients_text"));
            Add("отметки", S("labels"));
            return facts.Count > 0 ? string.Join("; ", facts) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Блок для ИИ.</summary>
    public static string ContextText(IReadOnlyList<Finding> findings) =>
        findings.Count == 0
            ? ""
            : "НАЙДЕНО В ИНТЕРНЕТЕ О ТОВАРАХ (описание, страну, бренд, состав бери ТОЛЬКО отсюда и из карточки; чего здесь нет — не выдумывай, "
              + "так и скажи владельцу):\n" + string.Join("\n", findings.Select(f => $"• «{f.Product.Title}»:\n{f.Text}"));
}
