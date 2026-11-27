using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

// 2026-11-27 (ответ Умара «Конструктор акций Ф1 + pos/quote/», prm2711): API акций v2 для раздела «Акции» программы владельца.
// Модель и ошибки проверены вживую на NBS 27.11: сервер возвращает даты в UTC, деньги — строками ("20.00"), проценты и N/M — числами.

/// <summary>Акция v2 (promotions/). Изменяемая — её же правит мастер «Новая акция / Изменить акцию».</summary>
public sealed class Promotion
{
    public const string KindBuyNGetM = "buy_n_get_m";
    public const string KindPercent = "percent";
    public const string KindAmount = "amount";
    public const string KindFixedPrice = "fixed_price";

    public const string StatusDraft = "draft";
    public const string StatusActive = "active";
    public const string StatusPaused = "paused";
    public const string StatusFinished = "finished";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = StatusDraft;
    public string Kind { get; set; } = KindBuyNGetM;

    // params по виду
    public int Buy { get; set; } = 2;
    public int Get { get; set; } = 1;
    public decimal GetDiscountPercent { get; set; } = 100m;
    public bool Cheapest { get; set; } = true;
    public decimal Percent { get; set; } = 10m;
    public decimal? MinAmount { get; set; }
    public decimal? MaxQty { get; set; }
    public decimal Amount { get; set; } = 20m;
    /// <summary>unit — за штуку, line — на строку.</summary>
    public string Per { get; set; } = "unit";
    public decimal Price { get; set; }

    // scope
    public bool ScopeAll { get; set; }
    public List<string> Products { get; set; } = new();
    public List<string> Categories { get; set; } = new();
    public List<string> Brands { get; set; } = new();
    public List<string> ExcludeProducts { get; set; } = new();

    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    /// <summary>1 = Пн … 7 = Вс; пусто — все дни.</summary>
    public List<int> Weekdays { get; set; } = new();
    public string? TimeFrom { get; set; }
    public string? TimeTo { get; set; }

    /// <summary>all | clients.</summary>
    public string Audience { get; set; } = "all";
    public List<string> Levels { get; set; } = new();
    public int Priority { get; set; } = 10;
    public bool StackableWithManual { get; set; }
    public bool StackableWithPromos { get; set; }
    public bool AllowBelowCost { get; set; }
    public List<string> Branches { get; set; } = new();
    public int? LimitPerReceipt { get; set; }
    public int? LimitTotal { get; set; }
    public int? LimitPerClient { get; set; }
    public int UsedTotal { get; set; }
    public int Version { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    /// <summary>Ответ на повтор того же Idempotency-Key: сервер вернул уже созданную акцию.</summary>
    public bool Replayed { get; set; }

    public bool IsDraft => Status == StatusDraft;
    public bool IsActive => Status == StatusActive;
    public bool IsPaused => Status == StatusPaused;
    public bool IsFinished => Status == StatusFinished;

    /// <summary>Запланированная: запущена, но период ещё не начался.</summary>
    public bool IsScheduled(DateTimeOffset now) => IsActive && StartsAt is { } s && s > now;

    /// <summary>Вид у запущенной (или когда-либо запущенной) акции менять нельзя — 409 promotion_kind_locked.</summary>
    public bool KindLocked => !string.IsNullOrEmpty(Id) && !IsDraft;

    public Promotion Clone()
    {
        var c = (Promotion)MemberwiseClone();
        c.Products = new List<string>(Products);
        c.Categories = new List<string>(Categories);
        c.Brands = new List<string>(Brands);
        c.ExcludeProducts = new List<string>(ExcludeProducts);
        c.Weekdays = new List<int>(Weekdays);
        c.Levels = new List<string>(Levels);
        c.Branches = new List<string>(Branches);
        return c;
    }

    /// <summary>params по виду — как в разделе 3 ответа сервера.</summary>
    public Dictionary<string, object?> ParamsBody()
    {
        var p = new Dictionary<string, object?>();
        switch (Kind)
        {
            case KindBuyNGetM:
                p["buy"] = Buy;
                p["get"] = Get;
                p["get_discount_percent"] = GetDiscountPercent;
                p["cheapest"] = Cheapest;
                break;
            case KindPercent:
                p["percent"] = Percent;
                if (MinAmount is { } min && min > 0)
                    p["min_amount"] = PromotionsApi.Money(min);
                if (MaxQty is { } max && max > 0)
                    p["max_qty"] = max;
                break;
            case KindAmount:
                p["amount"] = PromotionsApi.Money(Amount);
                p["per"] = Per == "line" ? "line" : "unit";
                break;
            case KindFixedPrice:
                p["price"] = PromotionsApi.Money(Price);
                break;
        }
        return p;
    }

    public Dictionary<string, object?> ScopeBody()
    {
        if (ScopeAll)
        {
            var all = new Dictionary<string, object?> { ["all"] = true };
            if (ExcludeProducts.Count > 0)
                all["exclude_products"] = ExcludeProducts.ToList();
            return all;
        }
        var s = new Dictionary<string, object?>();
        if (Products.Count > 0) s["products"] = Products.ToList();
        if (Categories.Count > 0) s["categories"] = Categories.ToList();
        if (Brands.Count > 0) s["brands"] = Brands.ToList();
        if (ExcludeProducts.Count > 0) s["exclude_products"] = ExcludeProducts.ToList();
        return s;
    }

    /// <summary>Тело POST/PATCH (и promotion для preview). <paramref name="includeKind"/>=false — у запущенной акции вид не отправляется.</summary>
    public Dictionary<string, object?> ToBody(bool includeKind = true, string? status = null)
    {
        var b = new Dictionary<string, object?>
        {
            ["title"] = Title.Trim(),
            ["description"] = Description.Trim(),
            ["params"] = ParamsBody(),
            ["scope"] = ScopeBody(),
            ["starts_at"] = StartsAt is { } s ? PromotionsApi.Iso(s) : null,
            ["ends_at"] = EndsAt is { } e ? PromotionsApi.Iso(e) : null,
            ["weekdays"] = Weekdays.Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d).ToList(),
            ["time_from"] = string.IsNullOrWhiteSpace(TimeFrom) ? null : TimeFrom.Trim(),
            ["time_to"] = string.IsNullOrWhiteSpace(TimeTo) ? null : TimeTo.Trim(),
            ["audience"] = Audience == "clients" ? "clients" : "all",
            ["priority"] = Priority,
            ["stackable_with_manual"] = StackableWithManual,
            ["stackable_with_promos"] = StackableWithPromos,
            ["allow_below_cost"] = AllowBelowCost,
            ["branches"] = Branches.ToList(),
            ["limit_per_receipt"] = LimitPerReceipt is > 0 ? LimitPerReceipt : null,
        };
        if (includeKind)
            b["kind"] = Kind;
        if (status != null)
            b["status"] = status;
        return b;
    }
}

public sealed record PromotionList(string Version, int Count, IReadOnlyList<Promotion> Items);

public sealed record PromotionApplied(string Kind, string Id, int Version, string Title, decimal Qty, decimal Amount);

public sealed record PromotionPreviewLine(int Index, string Product, decimal Qty, decimal Price, decimal Discount, decimal LineTotal, IReadOnlyList<PromotionApplied> Applied);

public sealed record PromotionWarning(string Code, int? Index, decimal? Cut, decimal? PurchasePrice, string Detail);

public sealed record PromotionHint(string Promotion, decimal? NeedQty, string Text);

public sealed record PromotionPreview(decimal Subtotal, decimal Total, decimal Discount, IReadOnlyList<PromotionPreviewLine> Lines,
    IReadOnlyList<PromotionWarning> Warnings, IReadOnlyList<PromotionHint> Hints);

/// <summary>Строка примера корзины для preview: товар, количество, цена (необязательно — иначе цена сервера).</summary>
public sealed record PromotionPreviewItem(string Product, decimal Qty, decimal? Price = null, decimal? Discount = null);

/// <summary>Строка отчёта promotions/report/ по одной акции.</summary>
public sealed record PromotionReportRow(string PromotionId, string Title, int Receipts, decimal Units, decimal Discount, decimal Revenue, decimal? Margin);

/// <summary>Категория/бренд/филиал по UUID (категория — с родителем: категория включает подкатегории).</summary>
public sealed record PromotionRef(string Id, string Name, string? ParentId = null);

/// <summary>Найденный товар для выбора (scope, пример корзины).</summary>
public sealed record PromotionProduct(string Id, string Name, decimal? Price);

/// <summary>Ошибка акций — понятный текст (5 языков), поле и шаг мастера (1 Название, 2 Вид, 3 На что, 4 Когда, 5 Условия, 0 — не к шагу).</summary>
public sealed record PromotionError(string Code, string Text, string? Field, int Step, int? StatusCode);

public interface IPromotionsApi
{
    Task<PromotionList> ListAsync(string statuses = "active,paused,draft,finished", CancellationToken ct = default);
    Task<Promotion> GetAsync(string id, CancellationToken ct = default);
    Task<Promotion> CreateAsync(Promotion promotion, string idempotencyKey, string? status = null, CancellationToken ct = default);
    Task<Promotion> UpdateAsync(Promotion promotion, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    /// <summary>action: start | pause | resume | finish.</summary>
    Task<Promotion?> ChangeStatusAsync(string id, string action, CancellationToken ct = default);
    Task<PromotionPreview> PreviewAsync(Promotion promotion, IReadOnlyList<PromotionPreviewItem> items, CancellationToken ct = default);
    Task<IReadOnlyList<PromotionReportRow>> ReportAsync(DateTime from, DateTime to, string? promotionId = null, CancellationToken ct = default);
    Task<IReadOnlyList<PromotionRef>> CategoriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PromotionRef>> BrandsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PromotionRef>> BranchesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PromotionProduct>> SearchProductsAsync(string query, CancellationToken ct = default);
    Task<PromotionProduct?> ProductAsync(string id, CancellationToken ct = default);
}

/// <summary>2026-11-27: API конструктора акций (Ф1). Ключ Idempotency-Key для создания задаёт вызывающий (один на открытие мастера, повтор того же сохранения — тем же ключом).</summary>
public sealed class PromotionsApi : IPromotionsApi
{
    private const string Root = "api/main/promotions/";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    /// <summary>Часовой пояс компании для акций — Asia/Bishkek (UTC+6, без перехода на летнее время).</summary>
    public static readonly TimeSpan CompanyOffset = TimeSpan.FromHours(6);

    private readonly NurMarketApiClient _api;
    private readonly ICatalogApiService? _catalog;

    public PromotionsApi(NurMarketApiClient api, ICatalogApiService? catalog = null)
    {
        _api = api;
        _catalog = catalog;
    }

    public async Task<PromotionList> ListAsync(string statuses = "active,paused,draft,finished", CancellationToken ct = default)
    {
        var q = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(statuses))
            q["status"] = statuses;
        var data = await _api.RequestAsync(HttpMethod.Get, Root, null, q, ct, Timeout).ConfigureAwait(false);
        return ParseList(data);
    }

    public async Task<Promotion> GetAsync(string id, CancellationToken ct = default) =>
        Parse(await _api.RequestAsync(HttpMethod.Get, Root + Esc(id) + "/", null, null, ct, Timeout).ConfigureAwait(false));

    public async Task<Promotion> CreateAsync(Promotion promotion, string idempotencyKey, string? status = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Нужен ключ повтора (Idempotency-Key).", nameof(idempotencyKey));
        var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey.Trim() };
        var data = await _api.RequestAsync(HttpMethod.Post, Root, promotion.ToBody(includeKind: true, status: status), null, ct, Timeout, headers).ConfigureAwait(false);
        return Parse(data);
    }

    public async Task<Promotion> UpdateAsync(Promotion promotion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(promotion.Id))
            throw new ArgumentException("У акции нет id.", nameof(promotion));
        var data = await _api.RequestAsync(HttpMethod.Patch, Root + Esc(promotion.Id) + "/", promotion.ToBody(includeKind: !promotion.KindLocked), null, ct, Timeout).ConfigureAwait(false);
        return Parse(data);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        _api.RequestAsync(HttpMethod.Delete, Root + Esc(id) + "/", null, null, ct, Timeout);

    public async Task<Promotion?> ChangeStatusAsync(string id, string action, CancellationToken ct = default)
    {
        if (action is not ("start" or "pause" or "resume" or "finish"))
            throw new ArgumentException("Неизвестное действие: " + action, nameof(action));
        var data = await _api.RequestAsync(HttpMethod.Post, Root + Esc(id) + "/" + action + "/", new Dictionary<string, object?>(), null, ct, Timeout).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _) ? Parse(data) : null;
    }

    public async Task<PromotionPreview> PreviewAsync(Promotion promotion, IReadOnlyList<PromotionPreviewItem> items, CancellationToken ct = default)
    {
        var body = promotion.ToBody(includeKind: true);
        // promotions/preview/ без названия отвечает 400 promotion_invalid{title} — для примера подставляем рабочее название.
        if (string.IsNullOrWhiteSpace(promotion.Title))
            body["title"] = Tr.T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Oldindan ko'rish");
        var list = items.Select(i =>
        {
            var row = new Dictionary<string, object?> { ["product"] = i.Product, ["qty"] = i.Qty };
            if (i.Price is { } p) row["price"] = Money(p);
            if (i.Discount is { } d && d > 0) row["discount"] = Money(d);
            return row;
        }).ToList();
        var data = await _api.RequestAsync(HttpMethod.Post, Root + "preview/", new Dictionary<string, object?> { ["promotion"] = body, ["items"] = list }, null, ct, Timeout).ConfigureAwait(false);
        return ParsePreview(data);
    }

    public async Task<IReadOnlyList<PromotionReportRow>> ReportAsync(DateTime from, DateTime to, string? promotionId = null, CancellationToken ct = default)
    {
        var q = new Dictionary<string, string>
        {
            ["date_from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["date_to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(promotionId))
            q["promotion"] = promotionId;
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "report/", null, q, ct, Timeout).ConfigureAwait(false);
        return ParseReport(data);
    }

    public Task<IReadOnlyList<PromotionRef>> CategoriesAsync(CancellationToken ct = default) => RefsAsync("api/main/categories/", ct);

    public Task<IReadOnlyList<PromotionRef>> BrandsAsync(CancellationToken ct = default) => RefsAsync("api/main/brands/", ct);

    public Task<IReadOnlyList<PromotionRef>> BranchesAsync(CancellationToken ct = default) => RefsAsync("api/users/branches/", ct);

    public async Task<IReadOnlyList<PromotionProduct>> SearchProductsAsync(string query, CancellationToken ct = default)
    {
        if (_catalog == null || string.IsNullOrWhiteSpace(query))
            return Array.Empty<PromotionProduct>();
        // Тот же поиск товаров, что у каталога программы владельца (products/list/?search=).
        var found = await _catalog.ProductsSearchAsync(query, 30, ct).ConfigureAwait(false);
        return found.Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => new PromotionProduct(p.Id!, (p.Name ?? p.Title ?? "").Trim(), p.Price))
            .ToList();
    }

    public async Task<PromotionProduct?> ProductAsync(string id, CancellationToken ct = default)
    {
        if (_catalog == null || string.IsNullOrWhiteSpace(id))
            return null;
        var el = await _catalog.ProductsDetailAsync(id, ct).ConfigureAwait(false);
        if (el is not { ValueKind: JsonValueKind.Object } x)
            return null;
        var name = Str(x, "name");
        if (name.Length == 0)
            name = Str(x, "title");
        return new PromotionProduct(id, name, Dec(x, "price"));
    }

    private async Task<IReadOnlyList<PromotionRef>> RefsAsync(string path, CancellationToken ct)
    {
        var result = new List<PromotionRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 30; page++)
        {
            var q = new Dictionary<string, string> { ["page"] = page.ToString(CultureInfo.InvariantCulture), ["page_size"] = "300" };
            var data = await _api.RequestAsync(HttpMethod.Get, path, null, q, ct, Timeout).ConfigureAwait(false);
            var rows = Rows(data).ToList();
            foreach (var r in rows)
            {
                var id = Str(r, "id");
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                var parent = r.TryGetProperty("parent", out var p) ? p.ValueKind switch
                {
                    JsonValueKind.String => p.GetString(),
                    JsonValueKind.Object => Str(p, "id"),
                    _ => null,
                } : null;
                result.Add(new PromotionRef(id, Str(r, "name"), string.IsNullOrWhiteSpace(parent) ? null : parent));
            }
            var hasNext = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;
            if (rows.Count == 0 || !hasNext)
                break;
        }
        return result;
    }

    // ── Разбор (терпимый: числа и строки, отсутствующие поля) ───────────────────────────

    public static PromotionList ParseList(JsonElement data)
    {
        var items = Rows(data).Select(Parse).ToList();
        var count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : items.Count;
        return new PromotionList(data.ValueKind == JsonValueKind.Object ? Str(data, "version") : "", count, items);
    }

    public static Promotion Parse(JsonElement x)
    {
        var p = new Promotion();
        if (x.ValueKind != JsonValueKind.Object)
            return p;
        p.Id = Str(x, "id");
        p.Title = Str(x, "title");
        p.Description = Str(x, "description");
        p.Status = Str(x, "status") is { Length: > 0 } st ? st : Promotion.StatusDraft;
        p.Kind = Str(x, "kind") is { Length: > 0 } k ? k : Promotion.KindPercent;
        if (x.TryGetProperty("params", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            p.Buy = (int)(Dec(pr, "buy") ?? 2);
            p.Get = (int)(Dec(pr, "get") ?? 1);
            p.GetDiscountPercent = Dec(pr, "get_discount_percent") ?? 100m;
            p.Cheapest = !pr.TryGetProperty("cheapest", out var ch) || ch.ValueKind != JsonValueKind.False;
            p.Percent = Dec(pr, "percent") ?? 0m;
            p.MinAmount = Dec(pr, "min_amount");
            p.MaxQty = Dec(pr, "max_qty");
            p.Amount = Dec(pr, "amount") ?? 0m;
            p.Per = Str(pr, "per") == "line" ? "line" : "unit";
            p.Price = Dec(pr, "price") ?? 0m;
        }
        if (x.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.Object)
        {
            p.ScopeAll = sc.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True;
            p.Products = Ids(sc, "products");
            p.Categories = Ids(sc, "categories");
            p.Brands = Ids(sc, "brands");
            p.ExcludeProducts = Ids(sc, "exclude_products");
        }
        p.StartsAt = Date(x, "starts_at");
        p.EndsAt = Date(x, "ends_at");
        p.Weekdays = x.TryGetProperty("weekdays", out var wd) && wd.ValueKind == JsonValueKind.Array
            ? wd.EnumerateArray().Select(d => d.TryGetInt32(out var i) ? i : int.TryParse(d.ToString(), out var j) ? j : 0).Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d).ToList()
            : new List<int>();
        p.TimeFrom = NullIfEmpty(Str(x, "time_from"));
        p.TimeTo = NullIfEmpty(Str(x, "time_to"));
        p.Audience = Str(x, "audience") == "clients" ? "clients" : "all";
        p.Levels = Ids(x, "levels");
        p.Priority = (int)(Dec(x, "priority") ?? 0);
        p.StackableWithManual = Bool(x, "stackable_with_manual");
        p.StackableWithPromos = Bool(x, "stackable_with_promos");
        p.AllowBelowCost = Bool(x, "allow_below_cost");
        p.Branches = Ids(x, "branches");
        p.LimitPerReceipt = Int(x, "limit_per_receipt");
        p.LimitTotal = Int(x, "limit_total");
        p.LimitPerClient = Int(x, "limit_per_client");
        p.UsedTotal = Int(x, "used_total") ?? 0;
        p.Version = Int(x, "version") ?? 0;
        p.CreatedAt = Date(x, "created_at");
        p.UpdatedAt = Date(x, "updated_at");
        p.Replayed = Bool(x, "replayed");
        return p;
    }

    public static PromotionPreview ParsePreview(JsonElement x)
    {
        var lines = new List<PromotionPreviewLine>();
        if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
            foreach (var l in ls.EnumerateArray())
            {
                var applied = new List<PromotionApplied>();
                if (l.TryGetProperty("applied", out var ap) && ap.ValueKind == JsonValueKind.Array)
                    foreach (var a in ap.EnumerateArray())
                        applied.Add(new PromotionApplied(Str(a, "kind"), Str(a, "id"), Int(a, "version") ?? 0, Str(a, "title"), Dec(a, "qty") ?? 0, Dec(a, "amount") ?? 0));
                lines.Add(new PromotionPreviewLine(Int(l, "index") ?? lines.Count, Str(l, "product"), Dec(l, "qty") ?? 0, Dec(l, "price") ?? 0, Dec(l, "discount") ?? 0,
                    Dec(l, "line_total") ?? 0, applied));
            }
        var warnings = new List<PromotionWarning>();
        if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("warnings", out var ws) && ws.ValueKind == JsonValueKind.Array)
            foreach (var w in ws.EnumerateArray())
                warnings.Add(w.ValueKind == JsonValueKind.String
                    ? new PromotionWarning(w.GetString() ?? "", null, null, null, "")
                    : new PromotionWarning(Str(w, "code"), Int(w, "index"), Dec(w, "cut"), Dec(w, "purchase_price"), Str(w, "detail") is { Length: > 0 } d ? d : Str(w, "text")));
        var hints = new List<PromotionHint>();
        if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("hints", out var hs) && hs.ValueKind == JsonValueKind.Array)
            foreach (var h in hs.EnumerateArray())
                hints.Add(h.ValueKind == JsonValueKind.String ? new PromotionHint("", null, h.GetString() ?? "") : new PromotionHint(Str(h, "promotion"), Dec(h, "need_qty"), Str(h, "text")));
        var subtotal = Dec(x, "subtotal") ?? lines.Sum(l => l.Price * l.Qty);
        var total = Dec(x, "total") ?? lines.Sum(l => l.LineTotal);
        var discount = Dec(x, "discount") ?? Dec(x, "discount_total") ?? lines.Sum(l => l.Discount);
        return new PromotionPreview(subtotal, total, discount, lines, warnings, hints);
    }

    /// <summary>Отчёт: {results:[…]} / [..] / {promotions:[…]}; акция — promotion (строкой или объектом {id,title}), promotion_id или id.</summary>
    public static IReadOnlyList<PromotionReportRow> ParseReport(JsonElement x)
    {
        IEnumerable<JsonElement> rows = x.ValueKind switch
        {
            JsonValueKind.Array => x.EnumerateArray(),
            JsonValueKind.Object when x.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array => r.EnumerateArray(),
            JsonValueKind.Object when x.TryGetProperty("promotions", out var r2) && r2.ValueKind == JsonValueKind.Array => r2.EnumerateArray(),
            _ => Array.Empty<JsonElement>(),
        };
        var list = new List<PromotionReportRow>();
        foreach (var r in rows)
        {
            if (r.ValueKind != JsonValueKind.Object)
                continue;
            string id = "", title = Str(r, "title");
            if (r.TryGetProperty("promotion", out var pr))
            {
                if (pr.ValueKind == JsonValueKind.String)
                    id = pr.GetString() ?? "";
                else if (pr.ValueKind == JsonValueKind.Object)
                {
                    id = Str(pr, "id");
                    if (title.Length == 0)
                        title = Str(pr, "title");
                }
            }
            if (id.Length == 0)
                id = Str(r, "promotion_id");
            if (id.Length == 0)
                id = Str(r, "id");
            if (title.Length == 0)
                title = Str(r, "promotion_title");
            list.Add(new PromotionReportRow(id, title, (int)(Dec(r, "receipts") ?? Dec(r, "sales") ?? 0), Dec(r, "units") ?? Dec(r, "qty") ?? 0,
                Dec(r, "discount") ?? Dec(r, "discount_total") ?? 0, Dec(r, "revenue") ?? 0, Dec(r, "margin")));
        }
        return list;
    }

    // ── Помощники ──────────────────────────────────────────────────────────────────

    internal static string Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Дата для сервера — во времени компании (+06:00).</summary>
    public static string Iso(DateTimeOffset v) => v.ToOffset(CompanyOffset).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+06:00";

    /// <summary>Перевод даты сервера (UTC) во время компании.</summary>
    public static DateTimeOffset Local(DateTimeOffset v) => v.ToOffset(CompanyOffset);

    private static string Esc(string id) => Uri.EscapeDataString((id ?? "").Trim());

    private static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
            return r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        return Array.Empty<JsonElement>();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Str(JsonElement x, string name) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static bool Bool(JsonElement x, string name) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    private static decimal? Dec(JsonElement x, string name)
    {
        if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse((v.GetString() ?? "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }

    private static int? Int(JsonElement x, string name) => Dec(x, name) is { } d ? (int)d : null;

    private static DateTimeOffset? Date(JsonElement x, string name) =>
        Str(x, name) is { Length: > 0 } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static List<string> Ids(JsonElement x, string name)
    {
        var list = new List<string>();
        if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var e in a.EnumerateArray())
            {
                var id = e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString() ?? "",
                    JsonValueKind.Number => e.GetRawText(),
                    JsonValueKind.Object => Str(e, "id"),
                    _ => "",
                };
                if (id.Length > 0 && !list.Contains(id))
                    list.Add(id);
            }
        return list;
    }
}

/// <summary>2026-11-27: видимость раздела «Акции» и права. Раздел — только при признаке сервера promotions_v2 и роли владелец/админ (права сервера: создавать и менять — владелец и админ);
/// «Разрешить продажу ниже закупки» — только владельцу (иначе 403 promotion_below_cost_forbidden).</summary>
public static class PromotionsAccess
{
    public static bool FeatureOn() => ServerCapabilities.Has(ServerCapabilities.PromotionsV2);

    public static bool ShouldOffer(bool featureOn, bool isOwnerOrAdmin) => featureOn && isOwnerOrAdmin;

    public static bool IsOwner(JsonElement payload)
    {
        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        return user.ValueKind == JsonValueKind.Object && user.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String
               && string.Equals(role.GetString(), "owner", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>2026-11-27: ошибки сервера акций → понятный текст с подсказкой, что исправить, и шаг мастера для promotion_invalid{field}.</summary>
public static class PromotionErrors
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Шаг мастера по полю сервера: 1 Название, 2 Вид, 3 На что, 4 Когда, 5 Условия; 0 — неизвестно.</summary>
    public static int StepOf(string? field)
    {
        var f = (field ?? "").Trim().ToLowerInvariant();
        var dot = f.IndexOfAny(new[] { '.', '[' });
        if (dot > 0)
            f = f[..dot];
        return f switch
        {
            "title" or "description" => 1,
            "kind" or "params" or "buy" or "get" or "get_discount_percent" or "percent" or "amount" or "per" or "price" or "min_amount" or "max_qty" => 2,
            "scope" or "products" or "categories" or "brands" or "exclude_products" or "all" => 3,
            "starts_at" or "ends_at" or "weekdays" or "time_from" or "time_to" => 4,
            "audience" or "levels" or "priority" or "stackable_with_manual" or "stackable_with_promos" or "allow_below_cost" or "branches" or "limit_per_receipt" or "limit_total" or "limit_per_client" => 5,
            _ => 0,
        };
    }

    public static string StepName(int step) => step switch
    {
        1 => T("Название", "Аталышы", "Name", "Ad", "Nomi"),
        2 => T("Вид акции", "Акциянын түрү", "Promotion type", "Kampanya türü", "Aksiya turi"),
        3 => T("На что действует", "Эмнеге таасир этет", "Applies to", "Neye uygulanır", "Nimaga amal qiladi"),
        4 => T("Когда", "Качан", "When", "Ne zaman", "Qachon"),
        5 => T("Условия", "Шарттар", "Conditions", "Koşullar", "Shartlar"),
        6 => T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Oldindan ko'rish"),
        _ => "",
    };

    public static PromotionError Describe(Exception ex)
    {
        if (ex is OperationCanceledException or TaskCanceledException or HttpRequestException)
            return new PromotionError("network", T("Нет связи с сервером NurCRM. Проверьте интернет и повторите — данные мастера не потеряны.",
                "NurCRM сервери менен байланыш жок. Интернетти текшерип, кайталаңыз — мастердеги маалымат жоголгон жок.",
                "No connection to the NurCRM server. Check the internet and try again — the wizard data is kept.",
                "NurCRM sunucusuyla bağlantı yok. İnterneti kontrol edip tekrar deneyin — sihirbaz verileri korunuyor.",
                "NurCRM serveri bilan aloqa yo'q. Internetni tekshirib, qayta urining — ustadagi ma'lumotlar saqlangan."), null, 0, null);
        if (ex is not ApiException api)
            return new PromotionError("unknown", T("Не получилось выполнить действие. Подробности — в журнале ошибок.", "Аракет аткарылган жок. Чоо-жайы — каталар журналында.",
                "The action failed. See the error log for details.", "İşlem yapılamadı. Ayrıntılar hata günlüğünde.", "Amal bajarilmadi. Tafsilotlar xatolar jurnalida."), null, 0, null);

        var payload = api.Payload;
        string code = "", detail = "", field = "", required = "";
        if (payload is { ValueKind: JsonValueKind.Object } p)
        {
            code = S(p, "code");
            detail = S(p, "detail");
            field = S(p, "field");
            required = S(p, "required");
            if (field.Length == 0 && p.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Object)
                field = fs.EnumerateObject().Select(o => o.Name).FirstOrDefault() ?? "";
            if (detail.Length == 0 && p.TryGetProperty("fields", out var fs2) && fs2.ValueKind == JsonValueKind.Object)
                detail = fs2.EnumerateObject().Select(o => o.Value.ValueKind == JsonValueKind.Array ? string.Join(" ", o.Value.EnumerateArray().Select(v => v.ToString())) : o.Value.ToString()).FirstOrDefault() ?? "";
        }
        var status = api.StatusCode;
        var server = detail.Length > 0 ? " " + T("Сервер: ", "Сервер: ", "Server: ", "Sunucu: ", "Server: ") + "«" + detail.TrimEnd('.') + "»." : "";

        switch (code)
        {
            case "promotion_invalid":
            {
                var step = StepOf(field);
                var where = step > 0 ? " " + T("Исправьте на шаге «", "«", "Fix it at step “", "«", "«") + StepName(step) + T("».", "» кадамында оңдоңуз.", "”.", "» adımında düzeltin.", "» qadamida tuzating.") : "";
                var what = StepOf(field) switch
                {
                    1 => T("Укажите название акции — его увидят кассир и покупатель в чеке.", "Акциянын аталышын жазыңыз — аны кассир жана чектеги сатып алуучу көрөт.",
                        "Enter the promotion name — the cashier and the customer see it on the receipt.", "Kampanya adını girin — kasiyer ve fişte müşteri görür.",
                        "Aksiya nomini kiriting — uni kassir va chekdagi xaridor ko'radi."),
                    2 => T("Проверьте условия вида акции (числа, процент, сумму, цену).", "Акциянын түрүнүн шарттарын текшериңиз (сандар, пайыз, сумма, баа).",
                        "Check the promotion type settings (numbers, percent, amount, price).", "Kampanya türü koşullarını kontrol edin (sayılar, yüzde, tutar, fiyat).",
                        "Aksiya turi shartlarini tekshiring (sonlar, foiz, summa, narx)."),
                    3 => T("Выберите, на что действует акция: «Все товары» или хотя бы один товар, категорию или бренд.", "Акция эмнеге таасир этерин тандаңыз: «Бардык товарлар» же жок дегенде бир товар, категория же бренд.",
                        "Choose what the promotion applies to: “All products” or at least one product, category or brand.", "Kampanyanın neye uygulanacağını seçin: «Tüm ürünler» ya da en az bir ürün, kategori veya marka.",
                        "Aksiya nimaga amal qilishini tanlang: «Barcha mahsulotlar» yoki kamida bitta mahsulot, toifa yoki brend."),
                    4 => T("Проверьте даты и часы: дата окончания не раньше начала, часы в виде ЧЧ:ММ.", "Даталарды жана сааттарды текшериңиз: аягы башталышынан эрте эмес, сааттар СС:ММ түрүндө.",
                        "Check dates and hours: the end is not before the start, hours as HH:MM.", "Tarih ve saatleri kontrol edin: bitiş başlangıçtan önce değil, saatler SS:DD biçiminde.",
                        "Sana va soatlarni tekshiring: tugash boshlanishdan oldin emas, soatlar SS:DD ko'rinishida."),
                    5 => T("Проверьте условия: приоритет, лимит, филиалы.", "Шарттарды текшериңиз: артыкчылык, чек, филиалдар.",
                        "Check the conditions: priority, limit, branches.", "Koşulları kontrol edin: öncelik, limit, şubeler.",
                        "Shartlarni tekshiring: ustuvorlik, limit, filiallar."),
                    _ => T("Сервер не принял акцию.", "Сервер акцияны кабыл алган жок.", "The server rejected the promotion.", "Sunucu kampanyayı kabul etmedi.", "Server aksiyani qabul qilmadi."),
                };
                return new PromotionError(code, what + where + server, field.Length > 0 ? field : null, step, status);
            }
            case "promotion_kind_locked":
                return new PromotionError(code, T("У запущенной акции нельзя сменить вид. Оставьте прежний вид или завершите акцию и создайте новую с нужным видом.",
                    "Иштеп жаткан акциянын түрүн өзгөртүүгө болбойт. Мурунку түрүн калтырыңыз же акцияны аяктап, керектүү түрү менен жаңысын түзүңүз.",
                    "The type of a started promotion can't be changed. Keep the type, or finish the promotion and create a new one.",
                    "Başlatılmış kampanyanın türü değiştirilemez. Türü koruyun ya da kampanyayı bitirip yenisini oluşturun.",
                    "Boshlangan aksiyaning turini o'zgartirib bo'lmaydi. Turini qoldiring yoki aksiyani yakunlab, kerakli turdagi yangisini yarating."), "kind", 2, status);
            case "promotion_finished":
                return new PromotionError(code, T("Акция уже завершена — её нельзя менять или запускать снова. Создайте новую акцию.",
                    "Акция аяктаган — аны өзгөртүүгө же кайра иштетүүгө болбойт. Жаңы акция түзүңүз.",
                    "The promotion is already finished — it can't be changed or started again. Create a new one.",
                    "Kampanya zaten bitti — değiştirilemez veya yeniden başlatılamaz. Yeni bir kampanya oluşturun.",
                    "Aksiya allaqachon yakunlangan — uni o'zgartirib yoki qayta boshlab bo'lmaydi. Yangi aksiya yarating."), null, 0, status);
            case "promotion_bad_status":
                return new PromotionError(code, T("Это действие недоступно в текущем статусе акции. Нажмите «Обновить» — возможно, её уже изменили с другого устройства.",
                    "Бул аракет акциянын азыркы абалында жеткиликсиз. «Жаңылоо» басыңыз — балким, аны башка түзмөктөн өзгөртүшкөндүр.",
                    "This action isn't available in the promotion's current status. Press “Refresh” — it may have been changed on another device.",
                    "Bu işlem kampanyanın mevcut durumunda yapılamaz. «Yenile»ye basın — başka bir cihazdan değiştirilmiş olabilir.",
                    "Bu amal aksiyaning hozirgi holatida mavjud emas. «Yangilash»ni bosing — ehtimol, u boshqa qurilmadan o'zgartirilgan."), null, 0, status);
            case "promotion_not_draft":
                return new PromotionError(code, T("Удалить можно только черновик без продаж. Запущенную акцию остановите кнопкой «Завершить» — она останется в отчёте.",
                    "Сатуусу жок каралама гана өчүрүлөт. Иштеп жаткан акцияны «Аяктоо» баскычы менен токтотуңуз — ал отчётто калат.",
                    "Only a draft without sales can be deleted. Stop a started promotion with “Finish” — it stays in the report.",
                    "Yalnızca satışı olmayan taslak silinebilir. Başlatılmış kampanyayı «Bitir» ile durdurun — raporda kalır.",
                    "Faqat sotuvsiz qoralamani o'chirish mumkin. Boshlangan aksiyani «Yakunlash» tugmasi bilan to'xtating — u hisobotda qoladi."), null, 0, status);
            case "promotion_below_cost_forbidden":
                return new PromotionError(code, T("«Разрешить продажу ниже закупки» может включить только владелец. Снимите эту галочку на шаге «Условия» или попросите владельца.",
                    "«Сатып алуу баасынан төмөн сатууга уруксат» дегенди ээси гана күйгүзө алат. Бул белгини «Шарттар» кадамында алып салыңыз же ээсинен сураңыз.",
                    "Only the owner can turn on “Allow selling below cost”. Untick it at step “Conditions” or ask the owner.",
                    "«Alış fiyatının altında satışa izin ver»i yalnızca sahip açabilir. «Koşullar» adımında işareti kaldırın ya da sahibinden isteyin.",
                    "«Xarid narxidan past sotishga ruxsat»ni faqat egasi yoqa oladi. Bu belgini «Shartlar» qadamida olib tashlang yoki egasidan so'rang."), "allow_below_cost", 5, status);
        }
        if (status == 403 || code == "permission_denied")
            return new PromotionError(code.Length > 0 ? code : "permission_denied", T("Нет права управлять акциями. Создавать и менять акции могут владелец и администратор"
                    + (required.Length > 0 ? " (право «" + required + "»)" : "") + ". Войдите под их логином или попросите выдать право.",
                "Акцияларды башкарууга укук жок. Акцияларды ээси жана администратор түзүп, өзгөртө алат"
                    + (required.Length > 0 ? " («" + required + "» укугу)" : "") + ". Алардын логини менен кириңиз же укук берүүнү сураңыз.",
                "No permission to manage promotions. The owner and administrators can create and change promotions"
                    + (required.Length > 0 ? " (permission “" + required + "”)" : "") + ". Sign in with their login or ask for the permission.",
                "Kampanyaları yönetme yetkiniz yok. Kampanyaları sahip ve yönetici oluşturup değiştirebilir"
                    + (required.Length > 0 ? " («" + required + "» yetkisi)" : "") + ". Onların girişiyle oturum açın ya da yetki isteyin.",
                "Aksiyalarni boshqarishga huquq yo'q. Aksiyalarni egasi va administrator yaratib, o'zgartira oladi"
                    + (required.Length > 0 ? " («" + required + "» huquqi)" : "") + ". Ularning logini bilan kiring yoki huquq berishni so'rang."), null, 0, status);
        if (status == 404)
            return new PromotionError("not_found", T("Акция не найдена — возможно, её уже удалили. Нажмите «Обновить».", "Акция табылган жок — балким, аны өчүрүп салышкандыр. «Жаңылоо» басыңыз.",
                "Promotion not found — it may have been deleted. Press “Refresh”.", "Kampanya bulunamadı — silinmiş olabilir. «Yenile»ye basın.",
                "Aksiya topilmadi — ehtimol, u o'chirilgan. «Yangilash»ni bosing."), null, 0, status);
        if (status == 401)
            return new PromotionError("auth", T("Вход истёк. Войдите заново и повторите.", "Кирүү мөөнөтү бүттү. Кайра кирип, кайталаңыз.", "The sign-in expired. Sign in again and retry.",
                "Oturum süresi doldu. Yeniden giriş yapıp tekrar deneyin.", "Kirish muddati tugadi. Qayta kirib, takrorlang."), null, 0, status);
        if (status is >= 500 or 429 or null)
            return new PromotionError(code.Length > 0 ? code : "server", T("Сервер NurCRM сейчас не отвечает. Повторите через минуту — данные мастера не потеряны.",
                "NurCRM сервери азыр жооп бербей жатат. Бир мүнөттөн кийин кайталаңыз — мастердеги маалымат жоголгон жок.",
                "The NurCRM server isn't responding now. Retry in a minute — the wizard data is kept.",
                "NurCRM sunucusu şu an yanıt vermiyor. Bir dakika sonra tekrar deneyin — sihirbaz verileri korunuyor.",
                "NurCRM serveri hozir javob bermayapti. Bir daqiqadan so'ng takrorlang — ustadagi ma'lumotlar saqlangan."), null, 0, status);
        var step2 = StepOf(field);
        return new PromotionError(code.Length > 0 ? code : "rejected", T("Сервер не принял запрос.", "Сервер сурамды кабыл алган жок.", "The server rejected the request.", "Sunucu isteği kabul etmedi.", "Server so'rovni qabul qilmadi.")
            + (server.Length > 0 ? server : " " + api.Message), field.Length > 0 ? field : null, step2, status);
    }

    private static string S(JsonElement x, string name) =>
        x.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Number ? v.GetRawText() : "" : "";
}
