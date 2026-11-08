using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-08 (ответ Умара 08.11, п.39 Б-95): отзыв покупателя на товар витрины. Телефон автора сервер не отдаёт.
/// Разбор терпимый: товар может быть uuid-строкой или объектом {id, name|title}, неизвестные поля игнорируются.</summary>
public sealed class ShowcaseReview
{
    public string Id { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string ProductName { get; init; } = "";
    /// <summary>Оценка 1–5 (0 — сервер не прислал).</summary>
    public int Rating { get; init; }
    public string Text { get; init; } = "";
    public string AuthorName { get; init; } = "";
    /// <summary>pending | approved | rejected.</summary>
    public string Status { get; init; } = ShowcaseExtrasApiService.ReviewPending;
    public string Reply { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Страница отзывов для сотрудников + число непроверенных (pending_count).</summary>
public sealed record ShowcaseReviewsPage(IReadOnlyList<ShowcaseReview> Items, int? PendingCount);

/// <summary>2026-11-08 (п.37 Б-93): коллекция витрины («Весна», «Подарки»). Порядок ProductIds важен — так товары идут на сайте.</summary>
public sealed class ShowcaseCollection
{
    public string Id { get; init; } = "";
    public string Slug { get; init; } = "";
    /// <summary>Название по языкам (ru, ky, en, tr, uz).</summary>
    public IReadOnlyDictionary<string, string> Titles { get; init; } = new Dictionary<string, string>();
    public string Description { get; init; } = "";
    public string Season { get; init; } = "";
    public string? CoverId { get; init; }
    public IReadOnlyList<string> ProductIds { get; init; } = Array.Empty<string>();
    /// <summary>Названия товаров, если сервер прислал products объектами (для показа без каталога).</summary>
    public IReadOnlyDictionary<string, string> ProductNames { get; init; } = new Dictionary<string, string>();
    public bool IsActive { get; init; } = true;
    public int Position { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>Название для показа: язык программы, затем ru, затем любое.</summary>
    public string TitleFor(string lang)
    {
        if (Titles.TryGetValue(lang, out var t) && t.Length > 0)
            return t;
        if (Titles.TryGetValue("ru", out var ru) && ru.Length > 0)
            return ru;
        return Titles.Values.FirstOrDefault(v => v.Length > 0) ?? "";
    }
}

/// <summary>Что записать в коллекцию (POST/PATCH). null у поля PATCH — не трогать (обложку и описание не шлём, пока их не меняли).</summary>
public sealed class ShowcaseCollectionDraft
{
    public IReadOnlyDictionary<string, string> Titles { get; init; } = new Dictionary<string, string>();
    public string? Description { get; init; }
    public string? Season { get; init; }
    public string? CoverId { get; init; }
    public bool ClearCover { get; init; }
    public IReadOnlyList<string> ProductIds { get; init; } = Array.Empty<string>();
    public bool IsActive { get; init; } = true;
    public int? Position { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
    public bool ClearDates { get; init; }
}

/// <summary>2026-11-08 (ответ Умара 08.11, п.37 и п.39): отзывы с модерацией и коллекции витрины для сотрудников
/// (владелец или админ с услугой «Онлайн витрина»). Транспорт — NurMarketApiClient; ошибки — ApiException. Списки разбираются терпимо:
/// массив или {results, count, next}, pending_count — только у отзывов. Страницы до конца, не больше <see cref="MaxPages"/>.</summary>
public sealed class ShowcaseExtrasApiService
{
    public const string ReviewPending = "pending";
    public const string ReviewApproved = "approved";
    public const string ReviewRejected = "rejected";

    public static readonly string[] Languages = { "ru", "ky", "en", "tr", "uz" };

    private const string ReviewsPath = "api/main/showcase/reviews/";
    private const string CollectionsPath = "api/main/showcase/collections/";
    private const int MaxPages = 20;

    private readonly NurMarketApiClient _api;

    public ShowcaseExtrasApiService(NurMarketApiClient api) => _api = api;

    // ── Отзывы ──────────────────────────────────────────────────────────────────────

    /// <summary>GET main/showcase/reviews/?status=…&amp;product=…; status/product пустые — без фильтра.</summary>
    public async Task<ShowcaseReviewsPage> ListReviewsAsync(string? status, string? productId, CancellationToken ct = default)
    {
        var result = new List<ShowcaseReview>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? pending = null;
        for (var page = 1; page <= MaxPages; page++)
        {
            var query = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(status))
                query["status"] = status!;
            if (!string.IsNullOrWhiteSpace(productId))
                query["product"] = productId!;
            if (page > 1)
                query["page"] = page.ToString(CultureInfo.InvariantCulture);
            var data = await _api.RequestAsync(HttpMethod.Get, ReviewsPath, null, query.Count > 0 ? query : null, ct).ConfigureAwait(false);
            pending ??= IntOf(data, "pending_count");
            var added = 0;
            foreach (var row in Rows(data))
            {
                var review = ParseReview(row);
                if (review.Id.Length == 0 || !seen.Add(review.Id))
                    continue;
                result.Add(review);
                added++;
            }
            if (!HasNext(data) || added == 0)
                break;
        }

        return new ShowcaseReviewsPage(result.OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue).ToList(), pending);
    }

    /// <summary>PATCH main/showcase/reviews/{uuid}/ {status, reply}. Ответ — обновлённый отзыв (если сервер его вернул).</summary>
    public async Task<ShowcaseReview?> ModerateReviewAsync(string id, string status, string? reply, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string> { ["status"] = status, ["reply"] = reply ?? "" };
        var data = await _api.RequestAsync(HttpMethod.Patch, $"{ReviewsPath}{Uri.EscapeDataString(id.Trim())}/", body, null, ct).ConfigureAwait(false);
        PosLogger.Log($"Отзыв {id}: статус → {status}", "SHOWCASE");
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _) ? ParseReview(data) : null;
    }

    /// <summary>DELETE main/showcase/reviews/{uuid}/.</summary>
    public async Task DeleteReviewAsync(string id, CancellationToken ct = default)
    {
        await _api.RequestAsync(HttpMethod.Delete, $"{ReviewsPath}{Uri.EscapeDataString(id.Trim())}/", null, null, ct).ConfigureAwait(false);
        PosLogger.Log($"Отзыв {id}: удалён", "SHOWCASE");
    }

    internal static ShowcaseReview ParseReview(JsonElement row)
    {
        string productId = "", productName = "";
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("product", out var p))
        {
            if (p.ValueKind == JsonValueKind.Object)
            {
                productId = Str(p, "id") ?? "";
                productName = Str(p, "name") ?? Str(p, "title") ?? "";
            }
            else
                productId = Str(row, "product") ?? "";
        }
        productName = Str(row, "product_name") ?? Str(row, "product_title") ?? productName;
        productId = Str(row, "product_id") ?? productId;
        return new ShowcaseReview
        {
            Id = Str(row, "id") ?? "",
            ProductId = productId,
            ProductName = productName,
            Rating = Math.Clamp((int)Math.Round(NumOf(row, "rating") ?? 0), 0, 5),
            Text = Str(row, "text") ?? Str(row, "comment") ?? "",
            AuthorName = Str(row, "author_name") ?? Str(row, "author") ?? "",
            Status = (Str(row, "status") ?? ReviewPending).ToLowerInvariant(),
            Reply = Str(row, "reply") ?? "",
            CreatedAt = DateOf(row, "created_at"),
        };
    }

    // ── Коллекции ───────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ShowcaseCollection>> ListCollectionsAsync(CancellationToken ct = default)
    {
        var result = new List<ShowcaseCollection>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxPages; page++)
        {
            var query = page > 1 ? new Dictionary<string, string> { ["page"] = page.ToString(CultureInfo.InvariantCulture) } : null;
            var data = await _api.RequestAsync(HttpMethod.Get, CollectionsPath, null, query, ct).ConfigureAwait(false);
            var added = 0;
            foreach (var row in Rows(data))
            {
                var c = ParseCollection(row);
                if (c.Id.Length == 0 || !seen.Add(c.Id))
                    continue;
                result.Add(c);
                added++;
            }
            if (!HasNext(data) || added == 0)
                break;
        }

        return result.OrderBy(c => c.Position).ToList();
    }

    public async Task<ShowcaseCollection?> GetCollectionAsync(string id, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, $"{CollectionsPath}{Uri.EscapeDataString(id.Trim())}/", null, null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object ? ParseCollection(data) : null;
    }

    public async Task<ShowcaseCollection?> CreateCollectionAsync(ShowcaseCollectionDraft draft, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Post, CollectionsPath, BuildBody(draft), null, ct).ConfigureAwait(false);
        PosLogger.Log("Коллекция витрины создана", "SHOWCASE");
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _) ? ParseCollection(data) : null;
    }

    public async Task<ShowcaseCollection?> UpdateCollectionAsync(string id, ShowcaseCollectionDraft draft, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Patch, $"{CollectionsPath}{Uri.EscapeDataString(id.Trim())}/", BuildBody(draft), null, ct).ConfigureAwait(false);
        PosLogger.Log($"Коллекция {id}: сохранена", "SHOWCASE");
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _) ? ParseCollection(data) : null;
    }

    /// <summary>Только включить/выключить: PATCH {is_active}.</summary>
    public async Task SetCollectionActiveAsync(string id, bool active, CancellationToken ct = default)
    {
        await _api.RequestAsync(HttpMethod.Patch, $"{CollectionsPath}{Uri.EscapeDataString(id.Trim())}/",
            new Dictionary<string, bool> { ["is_active"] = active }, null, ct).ConfigureAwait(false);
        PosLogger.Log($"Коллекция {id}: {(active ? "включена" : "выключена")}", "SHOWCASE");
    }

    public async Task DeleteCollectionAsync(string id, CancellationToken ct = default)
    {
        await _api.RequestAsync(HttpMethod.Delete, $"{CollectionsPath}{Uri.EscapeDataString(id.Trim())}/", null, null, ct).ConfigureAwait(false);
        PosLogger.Log($"Коллекция {id}: удалена", "SHOWCASE");
    }

    /// <summary>Сдвиг товара в коллекции на одну позицию вверх (-1) или вниз (+1); за край списка — без изменений.</summary>
    public static void MoveId(List<string> ids, int index, int delta)
    {
        var target = index + delta;
        if (index < 0 || index >= ids.Count || target < 0 || target >= ids.Count)
            return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
    }

    /// <summary>Тело POST/PATCH: {title{…}, description, season, cover, products:[id…], is_active, position, starts_at, ends_at}.
    /// Порядок products — как в списке. Обложка и описание уходят, только если заданы (PATCH не затирает прежние).</summary>
    internal static JsonObject BuildBody(ShowcaseCollectionDraft d)
    {
        var title = new JsonObject();
        foreach (var lang in Languages)
            if (d.Titles.TryGetValue(lang, out var t) && !string.IsNullOrWhiteSpace(t))
                title[lang] = t.Trim();
        var products = new JsonArray();
        foreach (var id in d.ProductIds)
            products.Add(id);
        var body = new JsonObject { ["title"] = title, ["products"] = products, ["is_active"] = d.IsActive };
        if (d.Description != null)
            body["description"] = d.Description.Trim();
        if (d.Season != null)
            body["season"] = d.Season.Trim();
        if (d.ClearCover)
            body["cover"] = null;
        else if (!string.IsNullOrWhiteSpace(d.CoverId))
            body["cover"] = d.CoverId;
        if (d.Position is { } pos)
            body["position"] = pos;
        if (d.ClearDates)
        {
            body["starts_at"] = null;
            body["ends_at"] = null;
        }
        else
        {
            if (d.StartsAt is { } s)
                body["starts_at"] = s.ToString("o", CultureInfo.InvariantCulture);
            if (d.EndsAt is { } e)
                body["ends_at"] = e.ToString("o", CultureInfo.InvariantCulture);
        }
        return body;
    }

    internal static ShowcaseCollection ParseCollection(JsonElement row)
    {
        var titles = new Dictionary<string, string>();
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("title", out var t))
        {
            if (t.ValueKind == JsonValueKind.Object)
            {
                foreach (var lang in Languages)
                    if (Str(t, lang) is { } v)
                        titles[lang] = v;
            }
            else if (t.ValueKind == JsonValueKind.String && Str(row, "title") is { } plain)
                titles["ru"] = plain;
        }

        var ids = new List<string>();
        var names = new Dictionary<string, string>();
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("products", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in list.EnumerateArray())
            {
                string? id;
                if (it.ValueKind == JsonValueKind.Object)
                {
                    id = Str(it, "id") ?? Str(it, "product");
                    if (id != null && (Str(it, "name") ?? Str(it, "title")) is { } n)
                        names[id] = n;
                }
                else
                    id = it.ValueKind is JsonValueKind.String or JsonValueKind.Number ? it.ToString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id!);
            }
        }

        string? cover = null;
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("cover", out var c))
            cover = c.ValueKind == JsonValueKind.Object ? Str(c, "id") : Str(row, "cover");

        string description = "";
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("description", out var d))
            description = d.ValueKind == JsonValueKind.Object
                ? (Str(d, "ru") ?? Languages.Select(l => Str(d, l)).FirstOrDefault(x => x != null) ?? "")
                : Str(row, "description") ?? "";

        return new ShowcaseCollection
        {
            Id = Str(row, "id") ?? "",
            Slug = Str(row, "slug") ?? "",
            Titles = titles,
            Description = description,
            Season = Str(row, "season") ?? "",
            CoverId = cover,
            ProductIds = ids,
            ProductNames = names,
            IsActive = !(row.ValueKind == JsonValueKind.Object && row.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.False),
            Position = (int)(NumOf(row, "position") ?? 0),
            StartsAt = DateOf(row, "starts_at"),
            EndsAt = DateOf(row, "ends_at"),
        };
    }

    // ── Помощники разбора ───────────────────────────────────────────────────────────

    private static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        var rows = data.ValueKind == JsonValueKind.Array
            ? data
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
                ? r
                : default;
        return rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
    }

    private static bool HasNext(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;

    private static int? IntOf(JsonElement e, string key) => NumOf(e, key) is { } d ? (int)d : null;

    private static string? Str(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double? NumOf(JsonElement e, string key) =>
        Str(e, key) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTimeOffset? DateOf(JsonElement e, string key) =>
        Str(e, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
