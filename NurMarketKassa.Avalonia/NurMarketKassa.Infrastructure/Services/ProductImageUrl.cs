using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>Одно фото товара для просмотрщика: полноразмерная ссылка и (если сервер дал) ссылка на миниатюру.</summary>
public sealed record ProductPhotoRef(string Url, string? ThumbUrl);

/// <summary>URL превью товара (логика product_media.product_image_url).</summary>
public static class ProductImageUrl
{
    public static string? TryGet(JsonElement p, string apiBaseUrl) => TryGet(p, apiBaseUrl, null);

    /// <param name="props">2026-10-04, отчёт о производительности (п. 16): поля товара, заранее собранные
    /// одним проходом (ProductCatalogMapper.TryTile, JsonProps): ~26 имён верхнего уровня, почти все
    /// отсутствуют, — без перебора всех полей на каждое имя. Правила выбора картинки те же.</param>
    internal static string? TryGet(JsonElement p, string apiBaseUrl, IReadOnlyDictionary<string, JsonElement>? props)
    {
        if (p.ValueKind != JsonValueKind.Object)
            return null;
        var baseTrim = apiBaseUrl.Trim().TrimEnd('/');
        var candidates = new List<string>();

        foreach (var k in new[]
                 {
                     "primary_image_url", "image_url", "thumbnail_url", "photo_url", "picture_url",
                     "main_image_url", "cover_url", "preview_url", "image_path", "media_url",
                 })
        {
            if (JsonProps.TryGet(p, props, k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                AddCandidate(candidates, ToAbsolute(v.GetString(), baseTrim));
            }
        }

        foreach (var k in new[] { "image", "photo", "thumbnail", "picture", "cover", "img" })
        {
            if (!JsonProps.TryGet(p, props, k, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                AddCandidate(candidates, ToAbsolute(v.GetString(), baseTrim));
            }

            if (v.ValueKind == JsonValueKind.Object)
            {
                foreach (var kk in new[]
                         {
                             "url", "src", "image", "file", "path", "thumbnail", "full", "download_url", "href",
                         })
                {
                    if (v.TryGetProperty(kk, out var u) && u.ValueKind == JsonValueKind.String)
                        AddCandidate(candidates, ToAbsolute(u.GetString(), baseTrim));
                }
            }
        }

        foreach (var nest in new[] { "primary_image", "main_image", "cover_image", "image_data", "photo_data" })
        {
            if (!JsonProps.TryGet(p, props, nest, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var kk in new[] { "url", "src", "image", "file", "path", "thumbnail", "full", "download_url" })
            {
                if (nested.TryGetProperty(kk, out var u) && u.ValueKind == JsonValueKind.String)
                    AddCandidate(candidates, ToAbsolute(u.GetString(), baseTrim));
            }
        }

        foreach (var key in new[] { "images", "photos", "gallery", "media", "attachments" })
        {
            if (!JsonProps.TryGet(p, props, key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            var n = 0;
            foreach (var it in arr.EnumerateArray())
            {
                if (++n > 8)
                    break;
                if (it.ValueKind == JsonValueKind.String)
                    AddCandidate(candidates, ToAbsolute(it.GetString(), baseTrim));

                if (it.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kk in new[] { "url", "src", "image", "file", "path", "thumbnail", "full" })
                    {
                        if (it.TryGetProperty(kk, out var u) && u.ValueKind == JsonValueKind.String)
                            AddCandidate(candidates, ToAbsolute(u.GetString(), baseTrim));
                    }
                }
            }
        }

        return ChooseBestCandidate(candidates);
    }

    /// <summary>2026-11-20 (просмотр фото товара на весь экран): все фото карточки товара для просмотрщика — по порядку сервера, без повторов.
    /// Сервер отдаёт <c>images[].image_url</c> и <c>images[].image_thumb_url</c> (миниатюра для мгновенного показа); понимаем и старые формы
    /// (строка-ссылка, url/src/file…). Если массива нет, берём главное фото карточки (image_url / primary_image_url…). Не больше 12 фото.</summary>
    public static IReadOnlyList<ProductPhotoRef> ExtractGallery(JsonElement p, string apiBaseUrl)
    {
        var result = new List<ProductPhotoRef>();
        if (p.ValueKind != JsonValueKind.Object)
            return result;
        var baseTrim = (apiBaseUrl ?? "").Trim().TrimEnd('/');

        void Add(string? url, string? thumb)
        {
            var full = ToAbsolute(url, baseTrim);
            var small = ToAbsolute(thumb, baseTrim);
            if (full.Length == 0 && small.Length == 0)
                return;
            if (full.Length == 0)
                full = small; // есть только миниатюра — показываем её как основное
            if (result.Count >= 12 || result.Any(r => string.Equals(r.Url, full, StringComparison.OrdinalIgnoreCase)))
                return;
            result.Add(new ProductPhotoRef(full, small.Length == 0 || string.Equals(small, full, StringComparison.OrdinalIgnoreCase) ? null : small));
        }

        static string? Str(JsonElement o, params string[] names)
        {
            foreach (var n in names)
                if (o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString();
            return null;
        }

        foreach (var key in new[] { "images", "photos", "gallery", "media" })
        {
            if (!p.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind == JsonValueKind.String)
                    Add(it.GetString(), null);
                else if (it.ValueKind == JsonValueKind.Object)
                    Add(Str(it, "image_url", "url", "src", "image", "file", "path", "full", "download_url"),
                        Str(it, "image_thumb_url", "thumb_url", "thumbnail_url", "thumbnail", "thumb"));
            }
        }

        if (result.Count == 0)
        {
            var main = TryGet(p, apiBaseUrl ?? "");
            if (main is not null)
                Add(main, Str(p, "image_thumb_url", "thumbnail_url"));
        }
        return result;
    }

    private static string ToAbsolute(string? u, string baseNoSlash)
    {
        u = (u ?? "").Trim();
        if (u.Length == 0)
            return "";
        if (u.StartsWith("//", StringComparison.Ordinal))
            return "https:" + u;
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return u;
        if (baseNoSlash.Length > 0 && Uri.TryCreate(baseNoSlash + "/", UriKind.Absolute, out var baseUri))
        {
            if (Uri.TryCreate(baseUri, u.TrimStart('/'), out var absolute))
                return absolute.ToString();
        }

        return u;
    }

    private static void AddCandidate(List<string> items, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (items.Contains(value, StringComparer.OrdinalIgnoreCase))
            return;
        items.Add(value);
    }

    private static string? ChooseBestCandidate(List<string> candidates)
    {
        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderByDescending(ScoreCandidate)
            .FirstOrDefault();
    }

    private static int ScoreCandidate(string url)
    {
        var raw = url.ToLowerInvariant();
        var score = 0;

        if (raw.Contains("thumbnail", StringComparison.Ordinal) || raw.Contains("thumb", StringComparison.Ordinal))
            score += 15;
        if (raw.Contains(".jpg", StringComparison.Ordinal) || raw.Contains(".jpeg", StringComparison.Ordinal))
            score += 100;
        else if (raw.Contains(".png", StringComparison.Ordinal))
            score += 90;
        else if (raw.Contains(".gif", StringComparison.Ordinal))
            score += 70;
        else if (raw.Contains(".webp", StringComparison.Ordinal))
            score += 95; // 2026-11-06 (аудит скорости, шаг 21): штраф −50 убран — WebP с сайта легче JPEG, а в кэш всё равно пишется 320 px WebP
        else
            score += 40;

        return score;
    }
}
