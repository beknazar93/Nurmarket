using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

internal static class CatalogVersionParser
{
    public static CatalogVersionInfo? TryParseMeta(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var payload = root;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            payload = data;

        long? version = TryLong(payload, "catalogVersion", "catalog_version", "version");
        var lastModified = TryDate(payload, "lastModified", "last_modified", "updated_at", "modified_at");

        if (version.HasValue)
        {
            return new CatalogVersionInfo
            {
                CatalogVersion = version,
                LastModified = lastModified,
                Token = version.Value.ToString(CultureInfo.InvariantCulture),
                Source = "meta",
            };
        }

        if (lastModified.HasValue)
        {
            return new CatalogVersionInfo
            {
                LastModified = lastModified,
                Token = lastModified.Value.ToString("O", CultureInfo.InvariantCulture),
                Source = "meta",
            };
        }

        return null;
    }

    public static CatalogVersionInfo? TryParseListProbe(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var payload = root;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            payload = data;

        var count = TryLong(payload, "count", "total", "total_count");
        var items = UnwrapItems(payload);
        DateTimeOffset? latest = null;
        string? latestId = null;

        foreach (var item in items)
        {
            var updated = TryDate(item, "updated_at", "modified_at", "last_modified", "changed_at");
            if (!updated.HasValue)
                continue;

            if (!latest.HasValue || updated > latest)
            {
                latest = updated;
                latestId = TryProductId(item);
            }
        }

        if (!count.HasValue && !latest.HasValue)
            return null;

        var token = $"{count ?? 0}|{latest?.ToString("O", CultureInfo.InvariantCulture) ?? ""}|{latestId ?? ""}";
        return new CatalogVersionInfo
        {
            CatalogVersion = count,
            LastModified = latest,
            Token = token,
            Source = "list-probe",
        };
    }

    private static IEnumerable<JsonElement> UnwrapItems(JsonElement root)
    {
        foreach (var key in new[] { "results", "items", "products", "data" })
        {
            if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var el in arr.EnumerateArray())
                yield return el;
            yield break;
        }
    }

    private static long? TryLong(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
                return n;
            if (v.ValueKind == JsonValueKind.String
                && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static DateTimeOffset? TryDate(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var text = v.GetString();
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                return dto;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        return null;
    }

    private static string? TryProductId(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("id", out var idProp))
            return null;

        return idProp.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(idProp.GetString()) ? null : idProp.GetString(),
            JsonValueKind.Number => idProp.GetRawText(),
            _ => null
        };
    }
}
