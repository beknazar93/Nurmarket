using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>URL превью товара (логика product_media.product_image_url).</summary>
public static class ProductImageUrl
{
    public static string? TryGet(JsonElement p, string apiBaseUrl)
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
            if (p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
            {
                AddCandidate(candidates, ToAbsolute(v.GetString(), baseTrim));
            }
        }

        foreach (var k in new[] { "image", "photo", "thumbnail", "picture", "cover", "img" })
        {
            if (!p.TryGetProperty(k, out var v))
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
            if (!p.TryGetProperty(nest, out var nested) || nested.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var kk in new[] { "url", "src", "image", "file", "path", "thumbnail", "full", "download_url" })
            {
                if (nested.TryGetProperty(kk, out var u) && u.ValueKind == JsonValueKind.String)
                    AddCandidate(candidates, ToAbsolute(u.GetString(), baseTrim));
            }
        }

        foreach (var key in new[] { "images", "photos", "gallery", "media", "attachments" })
        {
            if (!p.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
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
            score -= 50;
        else
            score += 40;

        return score;
    }
}
