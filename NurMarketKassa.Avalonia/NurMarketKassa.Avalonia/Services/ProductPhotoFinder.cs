using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-05, владелец: «добавь возможность ИИ, чтобы он сам поискал фото в интернете и поставил на товары
/// в складе, если есть; если нет — попросить сфоткать владельца», «добавь техническую возможность к ИИ для загрузки
/// фото на склад». Фото ищется по штрихкоду в открытых базах товаров (Open Food Facts — продукты, Open Beauty Facts —
/// косметика, Open Products Facts — прочие товары; бесплатные, без ключа, фото под открытой лицензией). Найденное
/// владелец видит и подтверждает — только после этого фото уходит в карточку товара на сервер NurCRM
/// (ICatalogApiService.UploadProductImageAsync, как «Загрузить фото» на складе).</summary>
public static class ProductPhotoFinder
{
    public sealed record Candidate(CatalogProductTileVm Product, string ImageUrl, string Source);

    private static readonly HttpClient Http = CreateHttp();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> RecentImages = new();

    private static readonly (string Name, string Host)[] Sources =
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
        // 2026-10-06, владелец (снимок: пустые варианты фото с lavka.yandex.ru): без Accept сайты отдают AVIF, который окно
        // не показывает и который не годится для карточки. Просим форматы, которые программа понимает.
        http.DefaultRequestHeaders.Accept.ParseAdd("image/webp,image/jpeg,image/png,image/*;q=0.8,text/html;q=0.7,*/*;q=0.5");
        return http;
    }

    /// <summary>Товары склада без фото (не услуги).</summary>
    public static IReadOnlyList<CatalogProductTileVm> WithoutPhoto()
    {
        IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
        if (tiles.Count == 0)
            tiles = CatalogCacheService.Products.ToList();
        return tiles.Where(t => !t.IsService
                                && string.IsNullOrWhiteSpace(t.ImageUrl)
                                && string.IsNullOrWhiteSpace(t.ProductImagePath))
            .OrderBy(t => t.Title)
            .ToList();
    }

    /// <summary>Штрихкод, по которому есть смысл искать: 8–14 цифр (EAN-8, UPC, EAN-13, GTIN-14).</summary>
    public static string? SearchableBarcode(CatalogProductTileVm product)
    {
        var code = new string((product.Barcode ?? "").Where(char.IsDigit).ToArray());
        return code.Length is >= 8 and <= 14 ? code : null;
    }

    /// <summary>2026-10-05, владелец: «чтобы ИИ смог по команде найти фото по названию или штрихкоду товара». Товары,
    /// названные в просьбе: штрихкод (8–14 цифр) или слова названия («найди фото для кока колы»). Пусто — просьба про все
    /// товары без фото.</summary>
    public static IReadOnlyList<CatalogProductTileVm> MatchRequest(string request)
    {
        IReadOnlyList<CatalogProductTileVm> tiles = LocalProductRepository.Instance.LoadAllTiles();
        if (tiles.Count == 0)
            tiles = CatalogCacheService.Products.ToList();
        tiles = tiles.Where(t => !t.IsService).ToList();
        var codes = Regex.Matches(request, @"\d{8,14}").Select(m => m.Value).ToHashSet();
        if (codes.Count > 0)
            return tiles.Where(t => SearchableBarcode(t) is { } c && codes.Contains(c)).ToList();
        var lower = request.ToLowerInvariant();
        if (new[] { "без фото", "всех", "все товары", "всем", "сүрөтсүз", "without", "all products", "fotoğrafsız", "rasmsiz" }.Any(lower.Contains))
            return Array.Empty<CatalogProductTileVm>();
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "найди", "найти", "поищи", "ищи", "поставь", "поставить", "загрузи", "загрузить", "добавь", "фото", "фотку", "фотографию",
            "фотография", "картинку", "для", "товара", "товару", "товар", "товаров", "интернете", "интернет", "в", "на", "по", "его", "её",
            "сүрөт", "сүрөтүн", "тап", "кой", "photo", "picture", "image", "find", "for", "the", "set", "upload", "fotoğraf", "bul", "rasm", "top",
        };
        var words = Regex.Split(lower, @"[^\p{L}\p{N}]+").Where(w => w.Length >= 3 && !stop.Contains(w)).Distinct().ToList();
        if (words.Count == 0)
            return Array.Empty<CatalogProductTileVm>();
        var scored = tiles.Select(t => (Tile: t, Score: words.Count(w => t.Title.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0).ToList();
        if (scored.Count == 0)
            return Array.Empty<CatalogProductTileVm>();
        var best = scored.Max(x => x.Score);
        return scored.Where(x => x.Score == best).Select(x => x.Tile).Take(10).ToList();
    }

    /// <summary>Поиск фото для товаров — по два товара одновременно, с паузой (бережно к бесплатным базам: у Open Food
    /// Facts лимит ~100 запросов в минуту). progress(сделано, всего). Порядок результатов — как во входном списке.</summary>
    public static async Task<(List<Candidate> Found, List<CatalogProductTileVm> NotFound)> SearchAsync(
        IReadOnlyList<CatalogProductTileVm> products, Action<int, int>? progress, CancellationToken ct, int webLimit = 0)
    {
        var webLeft = webLimit;
        PosLogger.Log($"Фото товаров: поиск по {products.Count} штрихкодам.", "CATALOG");
        var hits = new Candidate?[products.Count];
        var done = 0;
        using var gate = new SemaphoreSlim(2);
        var tasks = products.Select(async (product, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var code = SearchableBarcode(product);
                if (code is not null)
                {
                    foreach (var (name, host) in Sources)
                    {
                        var url = await TryFindAsync(host, code, ct).ConfigureAwait(false);
                        if (url is not null)
                        {
                            hits[index] = new Candidate(product, url, name);
                            break;
                        }
                    }
                    await Task.Delay(150, ct).ConfigureAwait(false);
                }
                // 2026-10-05, владелец: «найти фото по названию или штрихкоду в интернете, если в базе нет».
                hits[index] ??= await TryFindByNameAsync(product, ct).ConfigureAwait(false);
                // 2026-10-06: первое скачиваемое фото из Яндекс.Картинок (магазины — первыми), с паузой — бережно к Яндексу.
                if (hits[index] is null && product.Title.Trim().Length >= 3)
                {
                    foreach (var y in (await YandexImagesAsync(product.Title, ct).ConfigureAwait(false)).Take(4))
                        if (await IsRealImageAsync(y.ImageUrl, ct).ConfigureAwait(false))
                        {
                            hits[index] = new Candidate(product, y.ImageUrl, "интернет: " + y.Host);
                            break;
                        }
                    await Task.Delay(400, ct).ConfigureAwait(false);
                }
                if (hits[index] is null && Interlocked.Decrement(ref webLeft) >= 0)
                    hits[index] = await TryFindOnWebAsync(product, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
            progress?.Invoke(Interlocked.Increment(ref done), products.Count);
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        var found = hits.Where(h => h is not null).Select(h => h!).ToList();
        var notFound = products.Where((_, i) => hits[i] is null).ToList();
        PosLogger.Log($"Фото товаров: найдено {found.Count} из {products.Count} (нет в базах — {notFound.Count}).", "CATALOG");
        return (found, notFound);
    }

    private static async Task<string?> TryFindAsync(string host, string code, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"https://{host}/api/v2/product/{code}.json?fields=image_front_url,image_url", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Number || status.GetInt32() != 1)
                return null;
            if (!root.TryGetProperty("product", out var product))
                return null;
            foreach (var key in new[] { "image_front_url", "image_url" })
            {
                if (product.TryGetProperty(key, out var v) && v.GetString() is { Length: > 10 } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return url;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Нет в базе (404), нет сети, база не ответила за 12 с (таймаут HttpClient — тоже «отмена»,
            // но не наша), неожиданный ответ — просто «не найдено».
        }
        return null;
    }

    /// <summary>Open Food Facts по названию товара (продукты питания). Берём первый найденный товар, у которого с нашим
    /// совпадает хотя бы одно слово названия от 3 букв, — иначе «Банан» нашёл бы «банановые чипсы» другого бренда.</summary>
    private static async Task<Candidate?> TryFindByNameAsync(CatalogProductTileVm product, CancellationToken ct)
    {
        var title = product.Title.Trim();
        if (title.Length < 3)
            return null;
        try
        {
            var url = "https://world.openfoodfacts.org/cgi/search.pl?search_simple=1&action=process&json=1&page_size=5"
                      + "&fields=product_name,image_front_url&search_terms=" + Uri.EscapeDataString(title);
            var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("products", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;
            var words = Regex.Split(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 3).ToList();
            foreach (var p in list.EnumerateArray())
            {
                var name = p.TryGetProperty("product_name", out var n) ? (n.GetString() ?? "").ToLowerInvariant() : "";
                if (p.TryGetProperty("image_front_url", out var img) && img.GetString() is { Length: > 10 } image
                    && image.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && words.Any(name.Contains))
                    return new Candidate(product, image, "Open Food Facts (по названию)");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // не нашлось
        }
        return null;
    }

    /// <summary>Интернет: поиск Google через Gemini находит страницы товара, на странице берём его фото (og:image) или
    /// прямую ссылку на картинку; картинка проверяется скачиванием (это действительно изображение, не меньше 3 КБ).</summary>
    public static async Task<Candidate?> TryFindOnWebAsync(CatalogProductTileVm product, CancellationToken ct)
    {
        var code = SearchableBarcode(product);
        var question = $"Найди в интернете фотографию товара «{product.Title}»" + (code is null ? "" : $" (штрихкод {code})")
                       + ". Нужна страница интернет-магазина или производителя с фото именно этого товара. "
                       + "Ответь только ссылками, по одной в строке: сначала прямые ссылки на изображения (jpg, png, webp), затем страницы товара. Без пояснений.";
        try
        {
            var (answer, sources, error) = await NurMarketKassa.Services.TelegramAiChat.AskWebAsync(
                "Ты помощник магазина: находишь в интернете фото товаров для карточек склада. Не выдумывай ссылки.", question, ct).ConfigureAwait(false);
            if (answer is null && sources.Count == 0)
            {
                PosLogger.Log($"Фото товара «{product.Title}»: поиск в интернете не удался ({error}).", "CATALOG");
                return null;
            }
            var urls = Regex.Matches(answer ?? "", @"https?://[^\s<>()""'\]]+").Select(m => m.Value.TrimEnd('.', ',', ';'))
                .Concat(sources.Select(x => x.Uri)).Distinct().Take(8).ToList();
            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                var image = await ResolveImageAsync(url, ct).ConfigureAwait(false);
                if (image is not null)
                {
                    var host = Uri.TryCreate(image.Value.PageUrl, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "интернет";
                    return new Candidate(product, image.Value.ImageUrl, "интернет: " + host);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Фото товара «{product.Title}»: поиск в интернете прерван ({ex.Message}).", "CATALOG");
        }
        return null;
    }

    /// <summary>2026-10-06, владелец: «при поиске в интернете отображай найденные фото товаров на выбор в чате». Несколько
    /// вариантов фото одного товара: открытые базы по штрихкоду, поиск по названию и фото со страниц магазинов из поиска
    /// DuckDuckGo (без ключа; картинка проверяется скачиванием). Ставит владелец — выбранное.</summary>
    public static async Task<List<Candidate>> FindChoicesAsync(CatalogProductTileVm product, int max, CancellationToken ct)
    {
        var list = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Candidate? c)
        {
            if (c is not null && list.Count < max && seen.Add(c.ImageUrl))
                list.Add(c);
        }
        var code = SearchableBarcode(product);
        if (code is not null)
            foreach (var (name, host) in Sources)
                if (await TryFindAsync(host, code, ct).ConfigureAwait(false) is { } url)
                    Add(new Candidate(product, url, name));
        // 2026-10-06, владелец (снимок: «Aos Extra pover 450гр бальзам» — вариантов 0, а в интернете фото много): DuckDuckGo
        // стал отвечать проверкой «вы не робот» (202, anomaly) — страниц 0. Основной источник теперь Яндекс.Картинки: прямые
        // ссылки на фото магазинов (Ozon, Маркет…), каждая проверяется скачиванием.
        var yandex = await YandexImagesAsync(product.Title, ct).ConfigureAwait(false);
        if (yandex.Count < 4 && code is not null)
            yandex = yandex.Concat(await YandexImagesAsync(code, ct).ConfigureAwait(false)).DistinctBy(x => x.ImageUrl).ToList();
        var checkedImages = await Task.WhenAll(yandex.Take(max + 6).Select(async y =>
            await IsRealImageAsync(y.ImageUrl, ct).ConfigureAwait(false) ? y : ((string ImageUrl, string Host)?)null)).ConfigureAwait(false);
        foreach (var y in checkedImages)
            if (y is { } ok)
                Add(new Candidate(product, ok.ImageUrl, "интернет: " + ok.Host));
        Add(await TryFindByNameAsync(product, ct).ConfigureAwait(false));
        var pages = (IReadOnlyList<string>)Array.Empty<string>();
        if (list.Count < max)
        {
            pages = await NurMarketKassa.Services.ProductInfoResearch.SearchPageUrlsAsync($"{product.Title} {code}".Trim(), ct).ConfigureAwait(false);
            if (pages.Count < 3 && code is not null)
                pages = pages.Concat(await NurMarketKassa.Services.ProductInfoResearch.SearchPageUrlsAsync(product.Title, ct).ConfigureAwait(false)).Distinct().ToList();
            var resolved = await Task.WhenAll(pages.Take(10).Select(async page => await ResolveImageAsync(page, ct).ConfigureAwait(false))).ConfigureAwait(false);
            foreach (var image in resolved)
            {
                if (image is not { } im)
                    continue;
                var host = Uri.TryCreate(im.PageUrl, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "интернет";
                Add(new Candidate(product, im.ImageUrl, "интернет: " + host));
            }
        }
        // Мало вариантов — поиск Google через Gemini (ключ ИИ уже есть; страница картинок Google без браузера фото не отдаёт).
        if (list.Count < 2)
            Add(await TryFindOnWebAsync(product, ct).ConfigureAwait(false));
        PosLogger.Log($"Фото товара «{product.Title}»: вариантов на выбор {list.Count} (Яндекс.Картинки {yandex.Count}, страниц в поиске {pages.Count}).", "CATALOG");
        return list;
    }

    /// <summary>2026-10-06: Яндекс.Картинки — прямые ссылки на фото (origUrl) и сайт, где фото нашлось. Фото из отзывов
    /// (otzovik, irecommend — снято в руке, с фоном) — в конец списка, магазины — первыми. Капча или ошибка — пусто.</summary>
    private static async Task<List<(string ImageUrl, string Host)>> YandexImagesAsync(string query, CancellationToken ct)
    {
        var result = new List<(string ImageUrl, string Host)>();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://yandex.ru/images/search?text=" + Uri.EscapeDataString(query));
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            request.Headers.AcceptLanguage.ParseAdd("ru,en;q=0.8");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                PosLogger.Log($"Фото товара: Яндекс.Картинки ответили {(int)response.StatusCode}.", "CATALOG");
                return result;
            }
            var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (Match m in Regex.Matches(html, @"""origUrl"":""(https://[^""]+)"""))
            {
                var image = Regex.Unescape(m.Groups[1].Value);
                // Сайт, где нашлось фото, — «snippet.url» сразу после origUrl.
                var tail = html.Substring(m.Index, Math.Min(1500, html.Length - m.Index));
                var page = Regex.Match(tail, @"""snippet"":\{.*?""url"":""(https?://[^""]+)""", RegexOptions.Singleline);
                var hostUrl = page.Success ? page.Groups[1].Value : image;
                var host = Uri.TryCreate(hostUrl, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "интернет";
                if (result.All(r => r.ImageUrl != image))
                    result.Add((image, host));
            }
            if (result.Count == 0 && html.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                PosLogger.Log("Фото товара: Яндекс.Картинки просят капчу.", "CATALOG");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Фото товара: Яндекс.Картинки не ответили ({ex.Message}).", "CATALOG");
        }
        static bool Review(string host) => host.Contains("otzovik") || host.Contains("irecommend") || host.Contains("pinterest");
        return result.OrderBy(r => Review(r.Host) || Review(r.ImageUrl) ? 1 : 0).ToList();
    }

    /// <summary>Ссылка → картинка: если это изображение — оно; если страница — её og:image / twitter:image.</summary>
    private static async Task<(string ImageUrl, string PageUrl)?> ResolveImageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return await IsRealImageAsync(finalUrl, ct).ConfigureAwait(false) ? (finalUrl, finalUrl) : null;
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new char[400_000];
            using var reader = new StreamReader(stream);
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            var html = new string(buffer, 0, read);
            foreach (var pattern in new[]
                     {
                         @"<meta[^>]+property=[""']og:image(?::secure_url)?[""'][^>]+content=[""']([^""']+)",
                         @"<meta[^>]+content=[""']([^""']+)[""'][^>]+property=[""']og:image",
                         @"<meta[^>]+name=[""']twitter:image[""'][^>]+content=[""']([^""']+)",
                     })
            {
                var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (!m.Success)
                    continue;
                var raw = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (!Uri.TryCreate(new Uri(finalUrl), raw, out var abs) || abs.Scheme != Uri.UriSchemeHttps)
                    continue;
                if (await IsRealImageAsync(abs.ToString(), ct).ConfigureAwait(false))
                    return (abs.ToString(), finalUrl);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // страница не открылась
        }
        return null;
    }

    private static async Task<bool> IsRealImageAsync(string url, CancellationToken ct)
    {
        var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
        return bytes is { Length: > 3000 and < 8_000_000 };
    }

    /// <summary>Скачать картинку для предпросмотра (байты).</summary>
    public static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        // 2026-10-06 (снимок владельца: часть вариантов — пустые рамки): картинку скачивали дважды — при проверке и для
        // показа; второй раз сайты (otzovik, ozon) отвечали медленно. Последние скачанные — из памяти.
        if (RecentImages.TryGetValue(url, out var cached))
            return cached;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length is > 0 and < 8_000_000)
            {
                if (RecentImages.Count > 60)
                    RecentImages.Clear();
                RecentImages[url] = bytes;
            }
            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Поставить найденное фото товару: скачать во временный файл и загрузить на сервер как главное фото.</summary>
    public static async Task<bool> ApplyAsync(Candidate candidate, CancellationToken ct)
    {
        var bytes = await DownloadAsync(candidate.ImageUrl, ct).ConfigureAwait(false);
        if (bytes is not { Length: > 0 })
            return false;
        var ext = candidate.ImageUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-photos");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, candidate.Product.Id + ext);
        // 2026-10-06: фото Яндекс.Маркета приходят в WebP (ссылка без расширения) — на сервер отдаём PNG, а не WebP под именем .jpg.
        var isWebp = bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
                     && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
        if (isWebp)
        {
            try
            {
                file = Path.Combine(dir, candidate.Product.Id + ".png");
                using var input = new MemoryStream(bytes);
                using var bitmap = new Avalonia.Media.Imaging.Bitmap(input);
                bitmap.Save(file);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Фото товара: WebP не перевёлся в PNG ({ex.Message}).", "CATALOG");
                return false;
            }
        }
        else
            await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        return await UploadAsync(candidate.Product, file, $"найдено в {candidate.Source}", ct).ConfigureAwait(false);
    }

    /// <summary>Загрузить фото товара из файла (найденное или снятое владельцем).</summary>
    public static async Task<bool> UploadAsync(CatalogProductTileVm product, string file, string how, CancellationToken ct)
    {
        var api = App.GetRequiredService<ICatalogApiService>();
        var url = await api.UploadProductImageAsync(product.Id, file, isPrimary: true, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url))
            return false;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => product.ProductImagePath = file);
        PosLogger.Log($"Фото товара «{product.Title}» загружено ({how}).", "CATALOG");
        return true;
    }
}
