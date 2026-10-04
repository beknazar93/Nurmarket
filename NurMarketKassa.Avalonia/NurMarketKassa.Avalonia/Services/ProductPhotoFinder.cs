using System.IO;
using System.Net.Http;
using System.Text.Json;
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

    /// <summary>Поиск фото для товаров — по два товара одновременно, с паузой (бережно к бесплатным базам: у Open Food
    /// Facts лимит ~100 запросов в минуту). progress(сделано, всего). Порядок результатов — как во входном списке.</summary>
    public static async Task<(List<Candidate> Found, List<CatalogProductTileVm> NotFound)> SearchAsync(
        IReadOnlyList<CatalogProductTileVm> products, Action<int, int>? progress, CancellationToken ct)
    {
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

    /// <summary>Скачать картинку для предпросмотра (байты).</summary>
    public static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            return await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
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
