using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

public enum CatalogFeedStatus
{
    /// <summary>Пришли изменения (возможно, пустой список) и новый курсор.</summary>
    Ok,

    /// <summary>304 по ETag — ничего нового.</summary>
    NotModified,

    /// <summary>Адреса нет на сервере (404/405/501), он отказал в запросе или ответ не похож на ленту — касса работает по-старому.</summary>
    Unsupported,
}

/// <summary>2026-10-27 (Б-46 сервера, CATALOG_FEED): одна страница ленты изменений каталога.</summary>
public sealed class CatalogFeedPage
{
    public CatalogFeedStatus Status { get; init; }
    public List<JsonElement> Items { get; init; } = [];
    public List<string> DeletedIds { get; init; } = [];
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
    public string? ETag { get; init; }
    /// <summary>Для журнала: чем закончился разбор (имена полей ответа / причина «не поддерживается»).</summary>
    public string Note { get; init; } = "";
}

/// <summary>Отдельный интерфейс, как <see cref="IPosQuickCheckoutApi"/>: заглушки общего ICatalogApiService его не знают — касса тогда
/// просто живёт по старой логике (полная загрузка по таймеру).</summary>
public interface ICatalogFeedApi
{
    /// <summary>Область ленты (филиал): курсор из одной области в другой не переносится.</summary>
    string ScopeKey { get; }

    /// <summary>GET api/main/products/changes/?since=&lt;курсор&gt;&amp;limit=…; ETag прошлого ответа по этому же курсору — If-None-Match.</summary>
    Task<CatalogFeedPage> ProductsChangesAsync(string? since, int limit, string? etag, CancellationToken ct = default);
}

public sealed partial class CatalogApiService : ICatalogFeedApi
{
    public string ScopeKey => _client.ActiveBranchId ?? "";

    public async Task<CatalogFeedPage> ProductsChangesAsync(string? since, int limit, string? etag, CancellationToken ct = default)
    {
        var qs = new Dictionary<string, string> { ["limit"] = Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(since))
            qs["since"] = since;

        JsonElement body;
        int status;
        string? newEtag;
        try
        {
            (body, status, newEtag) = await _client
                .GetConditionalAsync("api/main/products/changes/", qs, etag, ct, TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode is 404 or 405 or 501)
        {
            return new CatalogFeedPage { Status = CatalogFeedStatus.Unsupported, Note = $"HTTP {ex.StatusCode}" };
        }

        if (status == 304)
            return new CatalogFeedPage { Status = CatalogFeedStatus.NotModified, ETag = newEtag, NextCursor = since };

        return ParseFeed(body, newEtag);
    }

    // 2026-10-27 (ревью, К-11): сервер ленту ещё не выложил, формат неизвестен — имена полей терпимые: списки изменений results/items/products/…/
    // data (в т. ч. вложенный data.items), курсор cursor/next_cursor/nextCursor/next (в т. ч. адрес с ?cursor=…), удалённые — идентификаторами или объектами.
    private static readonly string[] ItemKeys = ["results", "items", "products", "changes", "updated", "upserts", "changed", "modified", "data"];
    private static readonly string[] DeletedKeys = ["deleted", "deleted_ids", "removed", "removed_ids", "deleted_products", "deleted_items", "deletions"];
    private static readonly string[] CursorKeys = ["cursor", "next_cursor", "nextCursor", "last_cursor", "new_cursor", "next_since", "next"];
    private static readonly string[] HasMoreKeys = ["has_more", "hasMore", "has_next", "hasNext", "more"];
    private static readonly string[] WrapperKeys = ["data", "result", "payload", "meta", "pagination"];

    /// <summary>Объекты, в которых ищем поля ответа: сам ответ и его обёртки (data/result/payload/meta/pagination).</summary>
    private static List<JsonElement> Scopes(JsonElement root)
    {
        var list = new List<JsonElement> { root };
        foreach (var key in WrapperKeys)
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Object)
                list.Add(el);
        return list;
    }

    /// <summary>Курсор из значения: строка или число; адрес следующей страницы (https://…?cursor=abc / ?since=abc) — берём параметр; адрес без
    /// параметра курсора — не курсор (подставлять адрес в since нельзя).</summary>
    private static string? ReadCursor(JsonElement el, out bool wasUrl)
    {
        wasUrl = false;
        var text = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ValueKind == JsonValueKind.Number ? el.GetRawText() : null;
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith('/') || text.StartsWith('?'))
        {
            wasUrl = true;
            var q = text.IndexOf('?');
            if (q < 0)
                return null;
            foreach (var pair in text[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;
                var name = pair[..eq];
                if (name is "cursor" or "since" or "next_cursor" && eq + 1 < pair.Length)
                    return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }

            return null;
        }

        return text;
    }

    /// <summary>Разбор ответа ленты. Формат у сервера мог отличаться в деталях, поэтому имена полей терпимые; ответ без понятного курсора
    /// и без списка изменений — «не поддерживается» (касса не гадает и не двигает курсор).</summary>
    internal static CatalogFeedPage ParseFeed(JsonElement root, string? etag)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new CatalogFeedPage { Status = CatalogFeedStatus.Unsupported, Note = "ответ не объект" };

        var scopes = Scopes(root);
        JsonElement? itemsEl = null;
        foreach (var scope in scopes)
        {
            foreach (var key in ItemKeys)
                if (scope.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
                {
                    itemsEl = el;
                    break;
                }

            if (itemsEl is not null)
                break;
        }

        string? cursor = null;
        var cursorWasNextUrl = false;
        foreach (var scope in scopes)
        {
            foreach (var key in CursorKeys)
                if (scope.TryGetProperty(key, out var el) && el.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    cursor = ReadCursor(el, out var wasUrl);
                    if (!string.IsNullOrWhiteSpace(cursor))
                    {
                        cursorWasNextUrl = wasUrl && key == "next";
                        break;
                    }

                    cursor = null;
                }

            if (cursor is not null)
                break;
        }

        if (itemsEl is null && cursor is null)
        {
            var names = string.Join(",", root.EnumerateObject().Select(p => p.Name).Take(12));
            return new CatalogFeedPage { Status = CatalogFeedStatus.Unsupported, Note = $"неизвестный формат: поля {names}" };
        }

        var items = new List<JsonElement>();
        var deleted = new List<string>();
        if (itemsEl is { } arr)
            foreach (var row in arr.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    // 2026-10-27 (ревью, К-11): лента из одних идентификаторов — карточек нет, применять нечего; курсор двигать нельзя (товары
                    // остались бы старыми). «Не поддерживается» — прежняя логика (полная загрузка по таймеру).
                    return new CatalogFeedPage { Status = CatalogFeedStatus.Unsupported, Note = "строки ленты не объекты (только идентификаторы)" };
                }

                // Удалённый товар мог прийти строкой ленты с отметкой.
                if ((row.TryGetProperty("deleted", out var d1) && d1.ValueKind == JsonValueKind.True)
                    || (row.TryGetProperty("is_deleted", out var d2) && d2.ValueKind == JsonValueKind.True)
                    || (row.TryGetProperty("deleted_at", out var d3) && d3.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d3.GetString()))
                    || (row.TryGetProperty("status", out var d4) && d4.ValueKind == JsonValueKind.String
                        && string.Equals(d4.GetString(), "deleted", StringComparison.OrdinalIgnoreCase)))
                {
                    if (NurMarketKassa.Services.ProductCatalogMapper.TryId(row) is { Length: > 0 } delId)
                        deleted.Add(delId);
                    continue;
                }

                items.Add(row.Clone());
            }

        var deletedFound = false;
        foreach (var scope in scopes)
        {
            foreach (var key in DeletedKeys)
                if (scope.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in el.EnumerateArray())
                    {
                        var id = d.ValueKind switch
                        {
                            JsonValueKind.String => d.GetString(),
                            JsonValueKind.Number => d.GetRawText(),
                            // 2026-10-27 (К-11): объект удалённого — {"id": …} или {"product_id": …}
                            JsonValueKind.Object => NurMarketKassa.Services.ProductCatalogMapper.TryId(d)
                                ?? (d.TryGetProperty("product_id", out var pid) && (pid.ValueKind == JsonValueKind.String || pid.ValueKind == JsonValueKind.Number)
                                    ? (pid.ValueKind == JsonValueKind.String ? pid.GetString() : pid.GetRawText())
                                    : null),
                            _ => null,
                        };
                        if (!string.IsNullOrWhiteSpace(id))
                            deleted.Add(id.Trim());
                    }

                    deletedFound = true;
                    break;
                }

            if (deletedFound)
                break;
        }

        var hasMore = false;
        var hasMoreSeen = false;
        foreach (var scope in scopes)
        {
            foreach (var key in HasMoreKeys)
                if (scope.TryGetProperty(key, out var hm) && hm.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    hasMore = hm.ValueKind == JsonValueKind.True;
                    hasMoreSeen = true;
                    break;
                }

            if (hasMoreSeen)
                break;
        }

        // Постраничный ответ в стиле DRF: нет has_more, но есть «next» с адресом следующей страницы — страницы ещё есть.
        if (!hasMoreSeen && cursorWasNextUrl)
            hasMore = true;
        return new CatalogFeedPage
        {
            Status = CatalogFeedStatus.Ok,
            Items = items,
            DeletedIds = deleted,
            NextCursor = cursor,
            HasMore = hasMore,
            ETag = etag,
            Note = $"товаров {items.Count}, удалено {deleted.Count}, has_more={hasMore}",
        };
    }
}
