using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-22 (аналитика по категориям): расчёт вкладки «По категориям» раздела «Аналитика» и её выгрузки Word/Excel.
///
/// Откуда цифры (проверено GET на тестовом аккаунте NBS 22.10):
///  • товары периода — analytics/market/?tab=products&amp;limit=500 (название, категория, проданное количество, выручка, закупочная цена
///    карточки, остаток). Фильтра по категории у этого отчёта нет (ТЗ ч.30, п. 30.30), поэтому категория выбирается здесь, по
///    названию категории строки; выручка и «чеки» категории — из таблицы categories того же ответа;
///  • себестоимость = проданное количество × ЗАКУПОЧНАЯ ЦЕНА КАРТОЧКИ СЕЙЧАС (сервер не отдаёт цену закупки на момент продажи по
///    строкам — п. 30.31); итоги компании по сервису analytics/pnl считаются точнее, и в окне это подписано;
///  • склад категории — analytics/market/?tab=stock&amp;category=&lt;id&gt; (остаток по закупке, число товаров, остаток для списка
///    «не продавались»);
///  • расходы магазина — analytics/pnl (opex.total). По категориям сервер расходы не делит, поэтому «доля расходов» — только
///    пропорционально выручке и с явной подписью.
/// Считается только по серверу: без связи вкладка показывает «нет связи» (запасного расчёта по истории кассы нет — она знает
/// лишь свои продажи и разошлась бы с «Сводкой»). Деньги кассы не затрагиваются: только чтение.</summary>
public sealed class CategoryAnalyticsData
{
    /// <summary>Код псевдокатегории «Без категории» (у товаров без категории в ответе сервера category_id = null).</summary>
    public const string NoneId = "none";

    public sealed record Node(string Id, string Name, string? ParentId, int Depth);

    public sealed record ProductRow(string? ProductId, string Name, string Category, double Qty, double Revenue, double PurchasePrice, double Stock)
    {
        public double Cost => PurchasePrice > 0 ? Qty * PurchasePrice : 0;
    }

    public sealed record Totals(double Revenue, double Cost, double Qty, int Receipts, double CostCoverage)
    {
        public double Gross => Revenue - Cost;
        public double MarginPct => Revenue > 0 ? Gross / Revenue * 100 : 0;
        public double MarkupPct => Cost > 0 ? Gross / Cost * 100 : 0;
        public double AvgReceipt => Receipts > 0 ? Revenue / Receipts : 0;
        public static Totals Empty { get; } = new(0, 0, 0, 0, 1);
    }

    public sealed record StockRow(string Name, double Qty);

    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public DateTime PrevFrom { get; init; }
    public DateTime PrevTo { get; init; }

    /// <summary>Все категории каталога деревом (родитель → дети), для списка выбора.</summary>
    public IReadOnlyList<Node> Nodes { get; init; } = [];

    /// <summary>Выбранные категории вместе с подкатегориями; пусто — «Все категории».</summary>
    public IReadOnlyCollection<string> SelectedIds { get; init; } = [];

    public string SelectionTitle { get; init; } = "";

    public Totals Current { get; init; } = Totals.Empty;
    public Totals Previous { get; init; } = Totals.Empty;
    public Totals Company { get; init; } = Totals.Empty;
    public Totals CompanyPrevious { get; init; } = Totals.Empty;

    /// <summary>Выручка компании за период — по таблице categories (то же число, что «Сводка» по товарам).</summary>
    public double CompanyRevenue => Company.Revenue;

    public double SharePct => CompanyRevenue > 0 ? Current.Revenue / CompanyRevenue * 100 : 0;

    /// <summary>Расходы магазина за период (analytics/pnl → opex.total); null — сервер не ответил.</summary>
    public double? Opex { get; init; }

    /// <summary>Прибыль компании по pnl (маржа %, точнее нашей оценки по цене карточки); null — не ответил.</summary>
    public double? CompanyPnlMarginPct { get; init; }

    /// <summary>Доля общих расходов, приходящаяся на категорию ПРОПОРЦИОНАЛЬНО ВЫРУЧКЕ (подписывается в окне).</summary>
    public double? AllocatedOpex => Opex is { } o ? o * (SharePct / 100) : null;

    public double? NetAfterOpex => AllocatedOpex is { } a ? Current.Gross - a : null;

    // ---- склад
    public bool StockKnown { get; init; }
    public double StockCost { get; init; }
    public int StockProducts { get; init; }
    public double StockQty { get; init; }

    /// <summary>Хватит дней = остаток по закупке / (себестоимость проданного за день); null — продаж нет.</summary>
    public double? DaysOfStock => Current.Cost > 0 && StockKnown ? StockCost / (Current.Cost / Math.Max(1, (To.Date - From.Date).TotalDays + 1)) : null;

    /// <summary>Оборачиваемость = себестоимость проданного / остаток по закупке (раз за период).</summary>
    public double? Turnover => StockKnown && StockCost > 0 && Current.Cost > 0 ? Current.Cost / StockCost : null;

    public IReadOnlyList<ProductRow> Top { get; init; } = [];
    public IReadOnlyList<StockRow> NotSold { get; init; } = [];
    public int NotSoldTotal { get; init; }

    /// <summary>Сервер отдал не все товары (упёрлись в limit) — себестоимость/топ могут быть неполными.</summary>
    public bool Truncated { get; init; }

    // ------------------------------------------------------------------ загрузка

    private static async Task<JsonElement?> GetAsync(string path, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var client = NurCrmReportsApi.ClientResolver?.Invoke();
        if (client is not { AccessToken.Length: > 0 })
            return null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await client.RequestAsync(HttpMethod.Get, path, null, query, ct).ConfigureAwait(false);
            }
            catch (ApiException e) when (e.StatusCode == 429 && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                PosLogger.Log($"Аналитика по категориям: {path}: {e.Message}", "WARNING");
                return null;
            }
        }
    }

    private static string D(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Text(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static IEnumerable<JsonElement> Rows(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray() : [];

    /// <summary>Справочник категорий (GET categories/, постранично) деревом: порядок «родитель, затем его дети», Depth — вложенность.</summary>
    public static async Task<IReadOnlyList<Node>?> LoadNodesAsync(CancellationToken ct)
    {
        var raw = new List<(string Id, string Name, string? Parent)>();
        for (var page = 1; page <= 20; page++)
        {
            var json = await GetAsync("api/main/categories/", new Dictionary<string, string> { ["page"] = page.ToString(CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
            if (json is not { } j)
                return raw.Count > 0 ? Build(raw) : null;
            var list = j.ValueKind == JsonValueKind.Array ? j : j.TryGetProperty("results", out var r) ? r : default;
            if (list.ValueKind != JsonValueKind.Array)
                break;
            foreach (var row in list.EnumerateArray())
            {
                var id = Text(row, "id");
                var name = Text(row, "name");
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(name))
                    raw.Add((id!, name!.Trim(), Text(row, "parent")));
            }
            if (j.ValueKind != JsonValueKind.Object || !j.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                break;
        }
        return Build(raw);

        static List<Node> Build(List<(string Id, string Name, string? Parent)> all)
        {
            var ids = all.Select(a => a.Id).ToHashSet();
            var result = new List<Node>();
            void Walk(string? parent, int depth, HashSet<string> seen)
            {
                foreach (var c in all.Where(a => parent == null ? (a.Parent == null || !ids.Contains(a.Parent)) : a.Parent == parent)
                             .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    if (!seen.Add(c.Id))
                        continue;
                    result.Add(new Node(c.Id, c.Name, c.Parent, depth));
                    Walk(c.Id, depth + 1, seen);
                }
            }
            Walk(null, 0, []);
            return result;
        }
    }

    /// <summary>Выбранные категории + все их подкатегории.</summary>
    public static HashSet<string> Expand(IReadOnlyList<Node> nodes, IEnumerable<string> picked)
    {
        var result = new HashSet<string>(picked);
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var n in nodes)
            {
                if (n.ParentId != null && result.Contains(n.ParentId) && result.Add(n.Id))
                    grew = true;
            }
        }
        return result;
    }

    private sealed record ProductsReport(List<ProductRow> Rows, List<(string? Id, string Name, double Revenue, double Qty, int Receipts)> Categories, bool Truncated);

    private const int ProductLimit = 500;

    private static async Task<ProductsReport?> LoadProductsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var json = await GetAsync("api/main/analytics/market/", new Dictionary<string, string>
        {
            ["tab"] = "products", ["period_start"] = D(from), ["period_end"] = D(to), ["limit"] = ProductLimit.ToString(CultureInfo.InvariantCulture),
        }, ct).ConfigureAwait(false);
        if (json is not { } j || !j.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Object)
            return null;
        var rows = Rows(tables, "top_by_revenue")
            .Where(r => !string.IsNullOrWhiteSpace(Text(r, "name")))
            .Select(r => new ProductRow(Text(r, "product_id"), Text(r, "name")!, (Text(r, "category") ?? "").Trim(),
                Num(r, "qty_sold"), Num(r, "revenue"), Num(r, "purchase_price"), Num(r, "current_stock")))
            .ToList();
        var cats = Rows(tables, "categories")
            .Select(r => (Id: Text(r, "category_id"), Name: (Text(r, "category") ?? "").Trim(), Revenue: Num(r, "revenue"), Qty: Num(r, "qty_sold"), Receipts: (int)Math.Round(Num(r, "transactions"))))
            .ToList();
        return new ProductsReport(rows, cats, rows.Count >= ProductLimit);
    }

    private static Totals Sum(ProductsReport r, Func<string?, string, bool> pickCat, Func<string, bool> pickProd)
    {
        var cats = r.Categories.Where(c => pickCat(c.Id, c.Name)).ToList();
        var rows = r.Rows.Where(p => pickProd(p.Category)).ToList();
        // Выручка/штуки/чеки категории — из таблицы categories (полная); себестоимость — по товарам, у которых известна закупка.
        var revenue = cats.Sum(c => c.Revenue);
        var qty = cats.Sum(c => c.Qty);
        var cost = rows.Sum(p => p.Cost);
        var covered = rows.Where(p => p.PurchasePrice > 0).Sum(p => p.Revenue);
        var rowsRevenue = rows.Sum(p => p.Revenue);
        // Товары без закупочной цены: себестоимость неизвестна — не считаем её нулём, а показываем покрытие.
        var coverage = rowsRevenue > 0 ? covered / rowsRevenue : 1;
        if (revenue > 0 && rowsRevenue < revenue * 0.97)
            coverage *= rowsRevenue / revenue;
        return new Totals(revenue, cost, qty, cats.Sum(c => c.Receipts), coverage);
    }

    /// <summary>Собирает отчёт. <paramref name="picked"/> пусто — все категории. null — нет связи с сервером.</summary>
    public static async Task<CategoryAnalyticsData?> BuildAsync(DateTime from, DateTime to, IReadOnlyList<Node> nodes, IReadOnlyCollection<string> picked, CancellationToken ct)
    {
        from = from.Date;
        to = to.Date;
        var days = Math.Max(1, (int)(to - from).TotalDays + 1);
        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(days - 1));

        var current = await LoadProductsAsync(from, to, ct).ConfigureAwait(false);
        if (current is null)
            return null;
        var previous = await LoadProductsAsync(prevFrom, prevTo, ct).ConfigureAwait(false);

        var ids = Expand(nodes, picked);
        var names = nodes.Where(n => ids.Contains(n.Id)).Select(n => n.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var none = ids.Contains(NoneId);
        // Выбор: по id в таблице categories; у товаров — по названию категории (в строке товара id нет). «Без категории» — id null.
        bool PickCat(string? id, string name) => ids.Count == 0 || (id != null ? ids.Contains(id) : none);
        bool PickProd(string name) => ids.Count == 0 || (string.IsNullOrEmpty(name) ? none : names.Contains(name));
        bool All(string? id, string name) => true;
        bool AllProd(string name) => true;
        var cur = Sum(current, PickCat, PickProd);
        var company = Sum(current, All, AllProd);
        var prev = previous is null ? Totals.Empty : Sum(previous, PickCat, PickProd);
        var companyPrev = previous is null ? Totals.Empty : Sum(previous, All, AllProd);
        // Расходы магазина (pnl).
        double? opex = null, pnlMargin = null;
        var pnl = await GetAsync("api/main/analytics/pnl/", new Dictionary<string, string> { ["period_start"] = D(from), ["period_end"] = D(to) }, ct).ConfigureAwait(false);
        if (pnl is { } p && p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("opex", out var o))
                opex = Num(o, "total");
            pnlMargin = Num(p, "margin_percent");
        }

        // Склад выбранных категорий.
        var stockCost = 0.0;
        var stockQty = 0.0;
        var stockKnown = false;
        var stockIds = new Dictionary<string, (string Name, double Qty)>();
        var queries = ids.Count == 0 ? new List<string?> { null } : ids.Where(i => i != NoneId).Cast<string?>().ToList();
        foreach (var id in queries)
        {
            var q = new Dictionary<string, string> { ["tab"] = "stock", ["period_start"] = D(from), ["period_end"] = D(to) };
            if (id != null)
                q["category"] = id;
            var stock = await GetAsync("api/main/analytics/market/", q, ct).ConfigureAwait(false);
            if (stock is not { } s || s.ValueKind != JsonValueKind.Object)
                continue;
            stockKnown = true;
            if (s.TryGetProperty("cards", out var cards))
            {
                stockCost += Num(cards, "inventory_value");
                stockQty += Num(cards, "total_stock_quantity");
            }
            if (s.TryGetProperty("tables", out var st))
            {
                foreach (var r in Rows(st, "products_stock"))
                {
                    var pid = Text(r, "id");
                    if (pid != null && Text(r, "kind") is null or "product")
                        stockIds[pid] = (Text(r, "name") ?? "", Num(r, "quantity"));
                }
            }
        }

        var soldIds = current.Rows.Where(r => PickProd(r.Category) && r.ProductId != null && r.Qty > 0).Select(r => r.ProductId!).ToHashSet();
        var notSold = stockIds.Where(kv => !soldIds.Contains(kv.Key) && kv.Value.Qty > 0)
            .OrderByDescending(kv => kv.Value.Qty).ToList();

        string title = ids.Count == 0 ? "" : string.Join(", ", picked.Select(i => i == NoneId ? "—" : nodes.FirstOrDefault(n => n.Id == i)?.Name ?? i));

        return new CategoryAnalyticsData
        {
            From = from, To = to, PrevFrom = prevFrom, PrevTo = prevTo,
            Nodes = nodes, SelectedIds = ids, SelectionTitle = title,
            Current = cur, Previous = prev, Company = company, CompanyPrevious = companyPrev,
            Opex = opex, CompanyPnlMarginPct = pnlMargin,
            StockKnown = stockKnown, StockCost = stockCost, StockQty = stockQty, StockProducts = stockIds.Count,
            Top = current.Rows.Where(r => PickProd(r.Category) && r.Revenue > 0).OrderByDescending(r => r.Revenue).Take(10).ToList(),
            NotSold = notSold.Take(10).Select(kv => new StockRow(kv.Value.Name, kv.Value.Qty)).ToList(),
            NotSoldTotal = notSold.Count,
            Truncated = current.Truncated,
        };
    }
}
