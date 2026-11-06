﻿using System.IO;
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
    /// <summary>2026-11-03: Caption — подпись найденной картинки/страницы (по ней сверяется название); Doubtful — уверенности мало («проверьте», в
    /// «Поставить все» не входит); Description/DescriptionSource — краткое описание товара из интернета (ставится вместе с фото, если на сервере пусто).</summary>
    public sealed record Candidate(CatalogProductTileVm Product, string ImageUrl, string Source, string? Caption = null, bool Doubtful = false,
        string? Description = null, string? DescriptionSource = null);

    private static readonly HttpClient Http = CreateHttp();

    // 2026-11-03: единый лимит запросов в минуту (бережно к открытым базам, Яндексу и ключу Gemini; без «шторма» при пачке из 50 товаров).
    private static readonly RequestPacer OffPacer = new(60, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(300));
    private static readonly RequestPacer YandexPacer = new(30, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1500));
    private static readonly RequestPacer WebPacer = new(10, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(6));

    /// <summary>2026-11-03, владелец (снимок: для «моюн эти» подобрался кадр из YouTube): видео и соцсети дают кадры и аватарки, а не фото товара —
    /// такие адреса (страница или сама картинка) не принимаются. Список — только здесь.</summary>
    private static readonly string[] BlockedHosts =
    {
        "youtube.com", "youtu.be", "ytimg.com", "yt3.ggpht.com", "googlevideo.com", "tiktok.com", "instagram.com", "cdninstagram.com",
        "facebook.com", "fb.com", "fbcdn.net", "vk.com", "vk.ru", "userapi.com", "ok.ru", "mycdn.me", "twitter.com", "x.com", "twimg.com",
        "t.me", "rutube.ru", "dzen.ru", "vimeo.com", "dailymotion.com", "likee.video",
    };

    public static bool IsBlockedSource(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return false;
        var host = u.Host.ToLowerInvariant();
        return BlockedHosts.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    private static readonly HashSet<string> NameStopWords = new(StringComparer.OrdinalIgnoreCase) { "для", "это", "эти", "этот", "товар", "шт", "the", "and" };

    /// <summary>Значимые слова названия (основа — первые 5 букв; объём вроде «450гр» — число): по ним сверяется подпись найденной картинки.</summary>
    private static List<string> SignificantWords(string title) =>
        Regex.Split((title ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Select(w => Regex.Match(w, @"^(\d+)([.,]\d+)?(г|гр|кг|мл|л|шт|g|kg|ml|l)$") is { Success: true } m ? m.Groups[1].Value : w)
            .Where(w => (w.Length >= 3 || (w.Length == 2 && w.All(char.IsDigit))) && !NameStopWords.Contains(w))
            .Select(w => w.Length > 5 ? w[..5] : w).Distinct().ToList();

    /// <summary>Какая доля значимых слов названия есть в подписи (0..1). Нет слов или нет подписи — 0.</summary>
    public static double NameMatch(string title, string? caption)
    {
        var words = SignificantWords(title);
        if (words.Count == 0 || string.IsNullOrWhiteSpace(caption))
            return 0;
        var low = caption.ToLowerInvariant();
        return words.Count(low.Contains) / (double)words.Count;
    }

    /// <summary>Фото похоже на другой товар (или подписи нет) — не включается в «Поставить все».</summary>
    // 2026-11-05 (приёмка 1.17.75, С-2): порог был 0,5 — «Губка кухонная Дует 5шт» получила снимок «ДОМИ» (совпали «губка», «кухон», не совпал бренд «Дует»)
    // как «уверенный». Теперь уверенное — когда совпало не меньше 0,8 значимых слов (бренд и объём не отбрасываются); остальное — «проверьте».
    private static bool IsDoubtfulMatch(string title, string? caption) => NameMatch(title, caption) < 0.8;

    private enum PhotoQuality { Bad, Doubtful, Good }

    /// <summary>Размер картинки по заголовку файла (PNG, JPEG, GIF, WebP) без декодирования; null — формат не распознан.</summary>
    internal static (int Width, int Height)? ReadImageSize(byte[] b)
    {
        try
        {
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
                return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
            if (b.Length > 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
                return (b[6] | (b[7] << 8), b[8] | (b[9] << 8));
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF)
                    {
                        i++;
                        continue;
                    }
                    var marker = b[i + 1];
                    if (marker == 0xFF)
                    {
                        i++;
                        continue;
                    }
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                        return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                    if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                    {
                        i += 2;
                        continue;
                    }
                    i += 2 + ((b[i + 2] << 8) | b[i + 3]);
                }
                return null;
            }
            if (b.Length > 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            {
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
                    return (1 + (b[24] | (b[25] << 8) | (b[26] << 16)), 1 + (b[27] | (b[28] << 8) | (b[29] << 16)));
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
                    return ((b[26] | (b[27] << 8)) & 0x3FFF, (b[28] | (b[29] << 8)) & 0x3FFF);
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
                {
                    var bits = b[21] | (b[22] << 8) | (b[23] << 16) | ((long)b[24] << 24);
                    return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
        }
        return null;
    }

    /// <summary>2026-11-03: картинка пригодна? Не с видео/соцсети, настоящая (скачалась, 3 КБ–8 МБ), не мельче 150 пикселей; мельче 300 или с
    /// вытянутыми сторонами (баннер, листовка) — «сомнительная».</summary>
    private static async Task<PhotoQuality> CheckImageAsync(string url, CancellationToken ct)
    {
        if (IsBlockedSource(url))
            return PhotoQuality.Bad;
        var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
        if (bytes is not { Length: > 3000 and < 8_000_000 })
            return PhotoQuality.Bad;
        if (ReadImageSize(bytes) is { } size && size.Width > 0 && size.Height > 0)
        {
            if (Math.Min(size.Width, size.Height) < 150)
                return PhotoQuality.Bad;
            var ratio = (double)size.Width / size.Height;
            if (Math.Min(size.Width, size.Height) < 300 || ratio < 0.6 || ratio > 1.7)
                return PhotoQuality.Doubtful;
        }
        return PhotoQuality.Good;
    }

    // 2026-11-05 (аудит №3, Р3-01/Р3-05): раньше — до 61 картинки по 8 МБ (~0,5 ГБ) и сброс целиком. Теперь LRU по объёму: не больше 64 МБ,
    // одна картинка не больше 4 МБ; вытесненные байты освобождаются сборщиком (Bitmap здесь не держим — окно советника декодирует сам и сжимает до 224 px).
    private const long RecentImagesMaxBytes = 64L * 1024 * 1024;
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private static readonly object RecentGate = new();
    private static readonly LinkedList<string> RecentOrder = new();
    private static readonly Dictionary<string, (byte[] Bytes, LinkedListNode<string> Node)> RecentImages = new();
    private static long _recentBytes;

    /// <summary>Для диагностики памяти: сколько картинок и байт держит кэш поиска фото.</summary>
    public static (int Count, long Bytes) RecentImagesStats()
    {
        lock (RecentGate)
            return (RecentImages.Count, _recentBytes);
    }

    /// <summary>Освободить кэш картинок (закрытие «Нур Советника»).</summary>
    public static void ClearRecentImages()
    {
        lock (RecentGate)
        {
            RecentImages.Clear();
            RecentOrder.Clear();
            _recentBytes = 0;
        }
    }

    private static bool TryGetRecent(string url, out byte[]? bytes)
    {
        lock (RecentGate)
        {
            if (RecentImages.TryGetValue(url, out var entry))
            {
                RecentOrder.Remove(entry.Node);
                RecentOrder.AddFirst(entry.Node);
                bytes = entry.Bytes;
                return true;
            }
        }
        bytes = null;
        return false;
    }

    private static void AddRecent(string url, byte[] bytes)
    {
        lock (RecentGate)
        {
            if (RecentImages.TryGetValue(url, out var old))
            {
                _recentBytes -= old.Bytes.Length;
                RecentOrder.Remove(old.Node);
                RecentImages.Remove(url);
            }
            RecentImages[url] = (bytes, RecentOrder.AddFirst(url));
            _recentBytes += bytes.Length;
            while (_recentBytes > RecentImagesMaxBytes && RecentOrder.Last is { } last)
            {
                if (RecentImages.Remove(last.Value, out var dropped))
                    _recentBytes -= dropped.Bytes.Length;
                RecentOrder.RemoveLast();
            }
        }
    }

    /// <summary>2026-11-05: скачать картинку с пределом MaxImageBytes (заголовок Content-Length и обрыв по ходу чтения), а не целиком.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        if (response.Content.Headers.ContentLength is { } declared && declared > MaxImageBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static readonly (string Name, string Host)[] Sources =
    {
        ("Open Food Facts", "world.openfoodfacts.org"),
        ("Open Beauty Facts", "world.openbeautyfacts.org"),
        ("Open Products Facts", "world.openproductsfacts.org"),
    };

    private static HttpClient CreateHttp()
    {
        // 2026-11-06 (аудит скорости, 25.2): общая фабрика внешних клиентов — автораспаковка и смена DNS через 15 мин вместо голого HttpClient.
        var http = NurMarketKassa.Services.ExternalHttp.Create(TimeSpan.FromSeconds(12));
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
            // 2026-11-03: сначала товары с остатком (по убыванию), потом по названию — самое нужное получает фото первым.
            .OrderByDescending(t => t.Quantity)
            .ThenBy(t => t.Title)
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
                // 2026-11-03: тело поиска одного товара вынесено в SearchOneAsync (его же ищут пачками); интернет-поиск — для первых webLimit.
                hits[index] = await SearchOneAsync(product, () => Interlocked.Decrement(ref webLeft) >= 0, withDescription: false, ct).ConfigureAwait(false);
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

    /// <summary>2026-11-03, владелец: «ищет фото только для 10 товаров из 15 тысяч» — поиск одного товара для пачек (<see cref="PhotoBatchRunner{T,TResult}"/>):
    /// открытые базы по штрихкоду → по названию → Яндекс.Картинки → (если allowWeb) поиск Google через Gemini; каждый шаг — не чаще лимита в минуту
    /// (<see cref="RequestPacer"/>). Найденное фото с видео/соцсети не принимается, неточное по названию или мелкое/вытянутое помечается
    /// <see cref="Candidate.Doubtful"/>. Если найдено фото и в карточке нет описания — ещё и краткое описание из интернета. Кончился лимит ключа
    /// Gemini — <see cref="PhotoQuotaException"/> (с уже найденным фото внутри).</summary>
    public static async Task<Candidate?> SearchOneAsync(CatalogProductTileVm product, Func<bool> allowWeb, bool withDescription, CancellationToken ct)
    {
        Candidate? hit = null;
        var code = SearchableBarcode(product);
        if (code is not null)
        {
            foreach (var (name, host) in Sources)
            {
                await OffPacer.WaitAsync(ct).ConfigureAwait(false);
                var url = await TryFindAsync(host, code, ct).ConfigureAwait(false);
                if (url is null)
                    continue;
                var quality = await CheckImageAsync(url, ct).ConfigureAwait(false);
                if (quality == PhotoQuality.Bad)
                    continue;
                // По штрихкоду это точно тот товар; сомнительным фото делают только размер и форма.
                hit = new Candidate(product, url, name, null, quality == PhotoQuality.Doubtful);
                break;
            }
        }
        // 2026-10-05, владелец: «найти фото по названию или штрихкоду в интернете, если в базе нет».
        if (hit is null)
        {
            await OffPacer.WaitAsync(ct).ConfigureAwait(false);
            hit = await TryFindByNameAsync(product, ct).ConfigureAwait(false);
        }
        // 2026-10-06: первое скачиваемое фото из Яндекс.Картинок (магазины — первыми). 2026-11-03: первое «уверенное»; если таких нет — первое сомнительное.
        if (hit is null && product.Title.Trim().Length >= 3)
        {
            await YandexPacer.WaitAsync(ct).ConfigureAwait(false);
            Candidate? weak = null;
            foreach (var y in (await YandexImagesAsync(product.Title, ct).ConfigureAwait(false)).Take(4))
            {
                var quality = await CheckImageAsync(y.ImageUrl, ct).ConfigureAwait(false);
                if (quality == PhotoQuality.Bad)
                    continue;
                var doubtful = quality == PhotoQuality.Doubtful || IsDoubtfulMatch(product.Title, y.Caption);
                var candidate = new Candidate(product, y.ImageUrl, "интернет: " + y.Host, y.Caption, doubtful);
                if (!doubtful)
                {
                    hit = candidate;
                    break;
                }
                weak ??= candidate;
            }
            hit ??= weak;
        }
        if (hit is null && allowWeb())
        {
            // 2026-11-05 (приёмка 1.17.75, С-1): поминутный 429 — подождать ~30 с и повторить (до 2 раз), а не останавливать пачку.
            for (var retry = 0; ; retry++)
            {
                await WebPacer.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    hit = await TryFindOnWebCoreAsync(product, ct, throwOnQuota: true).ConfigureAwait(false);
                    break;
                }
                catch (PhotoQuotaException q) when (retry < MinuteLimitRetries && IsMinuteLimit(q.Message))
                {
                    await WaitMinuteLimitAsync(product.Title, ct).ConfigureAwait(false);
                }
            }
        }
        // 2026-11-05: описание — необязательное дополнение к уже найденному фото; отказ по лимиту не должен ни терять фото, ни останавливать пачку.
        if (hit is not null && withDescription && string.IsNullOrWhiteSpace(product.Description) && DescriptionSearchAvailable)
        {
            await WebPacer.WaitAsync(ct).ConfigureAwait(false);
            var (text, source) = await FindDescriptionAsync(product, ct, throwOnQuota: false).ConfigureAwait(false);
            if (text is not null)
                hit = hit with { Description = text, DescriptionSource = source };
        }
        return hit;
    }

    private const int MinuteLimitRetries = 2;

    /// <summary>2026-11-05: суточная квота («исчерпан суточный лимит») — ждать бесполезно; всё остальное (поминутный 429) — повторять.</summary>
    private static bool IsMinuteLimit(string? error) => error is not null && !error.Contains("суточн", StringComparison.OrdinalIgnoreCase);

    /// <summary>2026-11-05: пауза ~30 с на поминутный лимит Gemini и снятие 30-минутной блокировки поиска перед повтором.</summary>
    private static async Task WaitMinuteLimitAsync(string title, CancellationToken ct)
    {
        PosLogger.Log($"Поиск фото: лимит запросов в минуту (товар «{title}») — жду 30 с и повторяю.", "CATALOG");
        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        TelegramAiChat.ResetWebSearchBlockIfMinuteLimit();
    }

    /// <summary>Можно ли искать описание: ключ ИИ есть, поиск Google сейчас не заблокирован, владелец не выключил отправку названий в интернет (bc.web).</summary>
    public static bool DescriptionSearchAvailable =>
        TelegramAiChat.IsConfigured && !TelegramAiChat.WebSearchUnavailable && AdvisorCapabilities.IsOn("bc.web");

    private static bool IsQuotaError(string? error) =>
        error is not null && (error.Contains("лимит", StringComparison.OrdinalIgnoreCase) || error.Contains("недоступен для этого ключа", StringComparison.OrdinalIgnoreCase));

    /// <summary>2026-11-03, владелец: «найди для них фото в интернете и описание… и дополни эти товары». Краткое описание (1–3 предложения, без рекламы)
    /// по фактам со страниц; источник — первый сайт из поиска. Нет уверенных сведений — (null, null).</summary>
    public static async Task<(string? Text, string? Source)> FindDescriptionAsync(CatalogProductTileVm product, CancellationToken ct, bool throwOnQuota = false)
    {
        var code = SearchableBarcode(product);
        var question = $"Найди в интернете сведения о товаре «{product.Title}»" + (code is null ? "" : $" (штрихкод {code})")
                       + ". Напиши одно краткое описание для карточки магазина: 1–3 предложения по-русски — что это за товар, из чего или для чего, объём или вес, если они известны. "
                       + "Без рекламных слов (лучший, уникальный, выгодно), без цены, без ссылок, без названия магазина. "
                       + "Если уверенных сведений именно об этом товаре нет — ответь одним словом: Не нашлось.";
        try
        {
            var (answer, sources, error) = await NurMarketKassa.Services.TelegramAiChat.AskWebAsync(
                "Ты помощник магазина: пишешь краткие описания товаров только по фактам со страниц интернета. Не выдумывай.", question, ct).ConfigureAwait(false);
            if (answer is null)
            {
                PosLogger.Log($"Описание товара «{product.Title}»: не получено ({error}).", "CATALOG");
                if (throwOnQuota && IsQuotaError(error))
                    throw new PhotoQuotaException(error!);
                return (null, null);
            }
            var text = CleanDescription(answer);
            return text is null ? (null, null) : (text, sources.Count > 0 && sources[0].Title.Trim().Length > 0 ? sources[0].Title.Trim() : "интернет");
        }
        catch (Exception ex) when (ex is not PhotoQuotaException && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            PosLogger.Log($"Описание товара «{product.Title}»: поиск прерван ({ex.Message}).", "CATALOG");
            return (null, null);
        }
    }

    private static string? CleanDescription(string answer)
    {
        var t = Regex.Replace(answer, @"https?://\S+", "");
        t = Regex.Replace(t, @"\[\d+(?:\s*,\s*\d+)*\]", "");
        t = Regex.Replace(t, @"[*_#`>]+", "");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length < 20 || (t.Contains("не нашлось", StringComparison.OrdinalIgnoreCase) && t.Length < 80))
            return null;
        if (t.Length > 400)
        {
            var cut = t.LastIndexOfAny(new[] { '.', '!', '?' }, 399);
            t = cut > 120 ? t[..(cut + 1)] : t[..400].TrimEnd() + "…";
        }
        return t;
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
                    return new Candidate(product, image, "Open Food Facts (по названию)", name, IsDoubtfulMatch(title, name)); // 2026-11-03: совпало мало слов — «проверьте»
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
    public static Task<Candidate?> TryFindOnWebAsync(CatalogProductTileVm product, CancellationToken ct) => TryFindOnWebCoreAsync(product, ct, throwOnQuota: false);

    /// <summary>2026-11-03: throwOnQuota — лимит ключа Gemini поднимает <see cref="PhotoQuotaException"/> (пачки останавливаются), а не «не найдено».</summary>
    private static async Task<Candidate?> TryFindOnWebCoreAsync(CatalogProductTileVm product, CancellationToken ct, bool throwOnQuota)
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
                if (throwOnQuota && IsQuotaError(error))
                    throw new PhotoQuotaException(error!);
                return null;
            }
            var urls = Regex.Matches(answer ?? "", @"https?://[^\s<>()""'\]]+").Select(m => m.Value.TrimEnd('.', ',', ';'))
                .Concat(sources.Select(x => x.Uri)).Distinct().Take(8).ToList();
            // 2026-11-03: первая «уверенная» картинка (подпись страницы совпадает с названием); нет такой — первая сомнительная.
            Candidate? weak = null;
            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                var image = await ResolveImageAsync(url, ct).ConfigureAwait(false);
                if (image is null)
                    continue;
                var quality = await CheckImageAsync(image.Value.ImageUrl, ct).ConfigureAwait(false);
                if (quality == PhotoQuality.Bad)
                    continue;
                var host = Uri.TryCreate(image.Value.PageUrl, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "интернет";
                var caption = image.Value.Title + " " + Uri.UnescapeDataString(image.Value.ImageUrl);
                var doubtful = quality == PhotoQuality.Doubtful || IsDoubtfulMatch(product.Title, caption);
                var candidate = new Candidate(product, image.Value.ImageUrl, "интернет: " + host, caption, doubtful);
                if (!doubtful)
                    return candidate;
                weak ??= candidate;
            }
            return weak;
        }
        catch (Exception ex) when (ex is not PhotoQuotaException && (ex is not OperationCanceledException || !ct.IsCancellationRequested))
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
        // 2026-10-06, владелец: «очень долго ищет фото». Раньше шаги шли по очереди (три базы по штрихкоду — до 12 с каждая,
        // затем Яндекс, проверка картинок, поиск по названию, DuckDuckGo). Теперь Яндекс и базы — одновременно, на проверку
        // картинок не больше 6 с (что успело — то и показываем), медленные источники — только если вариантов мало.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var code = SearchableBarcode(product);
        var barcodeTasks = code is null
            ? new List<(string Name, Task<string?> Task)>()
            : Sources.Select(s => (Name: s.Name, Task: TryFindAsync(s.Host, code, ct))).ToList();
        // 2026-10-06, владелец (снимок: «Aos Extra pover 450гр бальзам» — вариантов 0, а в интернете фото много): DuckDuckGo
        // стал отвечать проверкой «вы не робот» (202, anomaly) — страниц 0. Основной источник теперь Яндекс.Картинки: прямые
        // ссылки на фото магазинов (Ozon, Маркет…), каждая проверяется скачиванием.
        var yandex = await YandexImagesAsync(product.Title, ct).ConfigureAwait(false);
        if (yandex.Count < 4 && code is not null)
            yandex = yandex.Concat(await YandexImagesAsync(code, ct).ConfigureAwait(false)).DistinctBy(x => x.ImageUrl).ToList();
        var checks = yandex.Take(max + 4).Select(y => (Image: y, Ok: IsRealImageAsync(y.ImageUrl, ct))).ToList();
        await Task.WhenAny(Task.WhenAll(checks.Select(c => c.Ok)), Task.Delay(6000, ct)).ConfigureAwait(false);
        if (barcodeTasks.Count > 0)
            await Task.WhenAny(Task.WhenAll(barcodeTasks.Select(b => b.Task)), Task.Delay(1500, ct)).ConfigureAwait(false);
        // Фото из базы по штрихкоду — точно этот товар, поэтому первыми.
        foreach (var (name, task) in barcodeTasks)
            if (task.IsCompletedSuccessfully && task.Result is { } url)
                Add(new Candidate(product, url, name));
        foreach (var (image, ok) in checks)
            if (ok.IsCompletedSuccessfully && ok.Result)
                Add(new Candidate(product, image.ImageUrl, "интернет: " + image.Host));
        var pages = (IReadOnlyList<string>)Array.Empty<string>();
        if (list.Count < 3)
        {
            Add(await TryFindByNameAsync(product, ct).ConfigureAwait(false));
            pages = await NurMarketKassa.Services.ProductInfoResearch.SearchPageUrlsAsync($"{product.Title} {code}".Trim(), ct).ConfigureAwait(false);
            var resolved = await Task.WhenAll(pages.Take(8).Select(async page => await ResolveImageAsync(page, ct).ConfigureAwait(false))).ConfigureAwait(false);
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
        PosLogger.Log($"Фото товара «{product.Title}»: вариантов на выбор {list.Count} за {watch.ElapsedMilliseconds} мс (Яндекс.Картинки {yandex.Count}, страниц в поиске {pages.Count}).", "CATALOG");
        return list;
    }

    /// <summary>2026-10-06: Яндекс.Картинки — прямые ссылки на фото (origUrl) и сайт, где фото нашлось. Фото из отзывов
    /// (otzovik, irecommend — снято в руке, с фоном) — в конец списка, магазины — первыми. Капча или ошибка — пусто.</summary>
    private static async Task<List<(string ImageUrl, string Host, string Caption)>> YandexImagesAsync(string query, CancellationToken ct)
    {
        var result = new List<(string ImageUrl, string Host, string Caption)>();
        var scores = new Dictionary<string, int>();
        // 2026-10-07, владелец: «неточность в поиске в интернете — не находит или неправильно показывает фото». Слова названия
        // (без веса и объёма вроде «50г», «450гр»); фото, у которого в подписи и адресе страницы нет ни одного из них, — отбрасываем,
        // остальные — по числу совпавших слов. Слово сравнивается по основе (первые 5 букв), чтобы «батончик» = «батончики».
        var keyWords = Regex.Split(query.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3 && !Regex.IsMatch(w, @"^\d+([.,]\d+)?(г|гр|кг|мл|л|шт|g|kg|ml|l)?$"))
            .Select(w => w.Length > 5 ? w[..5] : w).Distinct().ToList();
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
                // Подпись фото: «snippet.title» после origUrl и «alt» перед ним.
                var title = Regex.Match(tail, @"""snippet"":\{""title"":""([^""]*)""").Groups[1].Value;
                var head = html.Substring(Math.Max(0, m.Index - 700), Math.Min(700, m.Index));
                var alt = Regex.Matches(head, @"""alt"":""([^""]*)""").Select(x => x.Groups[1].Value).LastOrDefault() ?? "";
                var about = (title + " " + alt + " " + Uri.UnescapeDataString(hostUrl) + " " + image).ToLowerInvariant();
                // 2026-11-03: видео и соцсети (кадры, аватарки) — не фото товара.
                if (IsBlockedSource(image) || IsBlockedSource(hostUrl))
                    continue;
                if (result.All(r => r.ImageUrl != image))
                {
                    result.Add((image, host, about));
                    // Магазин («купить», Ozon, Маркет) — выше; фотобанк (рисунки, силуэты, «работ на тему») — ниже.
                    var shop = new[] { "купить", "магазин", "ozon", "market", "wildberries", "lavka", "korzinka", "globus", "цена" }.Any(about.Contains) ? 1 : 0;
                    var stock = new[] { "freepik", "shutterstock", "depositphotos", "istock", "vecteezy", "pngtree", "dreamstime", "работ на тему", "вектор", "силуэт", "clipart" }.Any(about.Contains) ? 2 : 0;
                    var hits = keyWords.Count(about.Contains);
                    scores[image] = hits == 0 ? 0 : Math.Max(1, hits * 2 + shop - stock);
                }
            }
            if (result.Count == 0 && html.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                PosLogger.Log("Фото товара: Яндекс.Картинки просят капчу.", "CATALOG");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Фото товара: Яндекс.Картинки не ответили ({ex.Message}).", "CATALOG");
        }
        static bool Review(string host) => host.Contains("otzovik") || host.Contains("irecommend") || host.Contains("pinterest");
        if (keyWords.Count > 0 && scores.Values.Any(v => v > 0))
            result = result.Where(r => scores[r.ImageUrl] > 0).ToList();
        return result.OrderByDescending(r => scores.TryGetValue(r.ImageUrl, out var sc) ? sc : 0)
            .ThenBy(r => Review(r.Host) || Review(r.ImageUrl) ? 1 : 0).ToList();
    }

    /// <summary>Ссылка → картинка: если это изображение — оно; если страница — её og:image / twitter:image.</summary>
    private static async Task<(string ImageUrl, string PageUrl, string Title)?> ResolveImageAsync(string url, CancellationToken ct)
    {
        if (IsBlockedSource(url))
            return null;
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
            if (IsBlockedSource(finalUrl)) // 2026-11-03: ссылка поиска могла привести на YouTube/соцсеть
                return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return await IsRealImageAsync(finalUrl, ct).ConfigureAwait(false) ? (finalUrl, finalUrl, "") : null;
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase))
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new char[400_000];
            using var reader = new StreamReader(stream);
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            var html = new string(buffer, 0, read);
            // 2026-11-03: подпись страницы (og:title или <title>) — по ней сверяется название товара.
            var pageTitle = System.Net.WebUtility.HtmlDecode(
                Regex.Match(html, @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)", RegexOptions.IgnoreCase).Groups[1].Value is { Length: > 0 } og
                    ? og
                    : Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value).Trim();
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
                    return (abs.ToString(), finalUrl, pageTitle);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // страница не открылась
        }
        return null;
    }

    private static async Task<bool> IsRealImageAsync(string url, CancellationToken ct) =>
        await CheckImageAsync(url, ct).ConfigureAwait(false) != PhotoQuality.Bad; // 2026-11-03: + не с видео/соцсети, не мельче 150 px

    /// <summary>Скачать картинку для предпросмотра (байты).</summary>
    public static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        // 2026-10-06 (снимок владельца: часть вариантов — пустые рамки): картинку скачивали дважды — при проверке и для
        // показа; второй раз сайты (otzovik, ozon) отвечали медленно. Последние скачанные — из памяти.
        if (TryGetRecent(url, out var cached))
            return cached;
        try
        {
            var bytes = await ReadLimitedAsync(url, ct).ConfigureAwait(false);
            if (bytes is { Length: > 0 })
                AddRecent(url, bytes);
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
        // 2026-11-03: поставленное запоминается — поиск пачками такой товар больше не берёт (до обновления каталога у плитки ещё нет ImageUrl).
        PhotoSearchMemory.Instance.Mark(product.Id, PhotoSearchMemory.Set);
        PhotoSearchMemory.Instance.Save();
        return true;
    }

    /// <summary>2026-11-03: поставить найденное описание — только если на сервере у товара описание пустое (существующее НЕ затираем; не смогли
    /// прочитать карточку — не пишем). false — описания нет, оно уже было или сервер не принял.</summary>
    public static async Task<bool> ApplyDescriptionAsync(Candidate candidate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(candidate.Description) || string.IsNullOrEmpty(candidate.Product.Id))
            return false;
        var api = App.GetRequiredService<ICatalogApiService>();
        var detail = await api.ProductsDetailAsync(candidate.Product.Id, ct).ConfigureAwait(false);
        if (detail is not { ValueKind: JsonValueKind.Object } card)
            return false;
        if (card.TryGetProperty("description", out var current) && current.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(current.GetString()))
            return false;
        await api.PatchProductFieldsAsync(candidate.Product.Id, new Dictionary<string, object?> { ["description"] = candidate.Description!.Trim() }, ct).ConfigureAwait(false);
        candidate.Product.Description = candidate.Description!.Trim();
        PosLogger.Log($"Описание товара «{candidate.Product.Title}» поставлено (источник: {candidate.DescriptionSource}).", "CATALOG");
        return true;
    }
}
