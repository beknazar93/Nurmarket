using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-27 (ответ Умара «Конструктор акций Ф1 + pos/quote/», раздел 4; проверено запросами preview/quote на NBS 27.11):
/// локальный расчёт акций v2 — те же правила, что у сервера, чтобы итог кассы совпадал до копейки и без сети.
/// Правила (все проверены вживую, см. docs/Отчеты/Акции v2 в кассе 27.11.2026.md):
/// • каждая акция считается по ВСЕМ подходящим строкам чека независимо; затем у строки из акций с ненулевой скидкой берётся наибольший priority
///   (равный — бо́льшая скидка); остальные добавляются, только если и выбранная, и добавляемая stackable_with_promos; скидки складываются (каждая — от суммы строки);
/// • N+M: штуки всех подходящих строк — от дорогих к дешёвым (равные — по порядку строк), блоками по N+M, в блоке скидка M последним (самым дешёвым);
///   limit_per_receipt — блоков; подсказка, если в неполном блоке ≥ N штук: «добавьте ещё N+M−остаток»;
/// • percent (min_amount — от суммы строки, max_qty — штук), amount (per unit — не больше цены; per line — раз на строку), fixed_price (цена выше — разница);
///   limit_per_receipt — штук (per line — строк), достаются самым дорогим;
/// • скидка акции на строке — до копейки HALF_UP; ручная скидка — только если все акции строки stackable_with_manual;
/// • И-2: скидка ≤ суммы строки; И-1: строка не дешевле закупки × количество, если не у всех акций allow_below_cost — урезается сначала ручная, потом акции с конца
///   (warnings promotion_capped_by_cost {index, cut, purchase_price});
/// • старые promotion_rules товара — кандидат с priority 0 (сумму считает PromotionRules.LineDiscount, как раньше).
/// </summary>
public static class PromoV2Engine
{
    public sealed class Rule
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public int Version { get; init; }
        public string Kind { get; init; } = "";
        public int Buy { get; init; }
        public int Get { get; init; }
        public decimal GetPercent { get; init; } = 100m;
        public decimal Percent { get; init; }
        public decimal? MinAmount { get; init; }
        public decimal? MaxQty { get; init; }
        public decimal Amount { get; init; }
        public bool PerLine { get; init; }
        public decimal FixedPrice { get; init; }
        public bool ScopeAll { get; init; }
        public HashSet<string> Products { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Categories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Brands { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Exclude { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset? StartsAt { get; init; }
        public DateTimeOffset? EndsAt { get; init; }
        public HashSet<int> Weekdays { get; init; } = new();
        public int? TimeFrom { get; init; }
        public int? TimeTo { get; init; }
        public bool ClientsOnly { get; init; }
        public int Priority { get; init; }
        public bool StackManual { get; init; }
        public bool StackPromos { get; init; }
        public bool AllowBelowCost { get; init; }
        public HashSet<string> Branches { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int? LimitPerReceipt { get; init; }
        public bool Legacy { get; init; }

        /// <summary>Разбор акции из pos/promotions/active/ (неизвестный kind / пустые параметры — null: такую акцию касса не считает).</summary>
        public static Rule? Parse(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object)
                return null;
            var status = Str(p, "status");
            if (status is not null && !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                return null;
            var kind = Str(p, "kind") ?? "";
            var prm = p.TryGetProperty("params", out var pe) && pe.ValueKind == JsonValueKind.Object ? pe : default;
            var scope = p.TryGetProperty("scope", out var se) && se.ValueKind == JsonValueKind.Object ? se : default;
            var r = new Rule
            {
                Id = Str(p, "id") ?? "",
                Title = Str(p, "title") ?? "",
                Version = (int)(Num(p, "version") ?? 0),
                Kind = kind,
                Buy = (int)(Num(prm, "buy") ?? 0),
                Get = (int)(Num(prm, "get") ?? 0),
                GetPercent = Num(prm, "get_discount_percent") ?? 100m,
                Percent = Num(prm, "percent") ?? 0m,
                MinAmount = Num(prm, "min_amount"),
                MaxQty = Num(prm, "max_qty"),
                Amount = Num(prm, "amount") ?? 0m,
                PerLine = string.Equals(Str(prm, "per"), "line", StringComparison.OrdinalIgnoreCase),
                FixedPrice = Num(prm, "price") ?? 0m,
                ScopeAll = scope.ValueKind == JsonValueKind.Object && scope.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True,
                Products = Set(scope, "products"),
                Categories = Set(scope, "categories"),
                Brands = Set(scope, "brands"),
                Exclude = Set(scope, "exclude_products"),
                StartsAt = Date(p, "starts_at"),
                EndsAt = Date(p, "ends_at"),
                Weekdays = p.TryGetProperty("weekdays", out var wd) && wd.ValueKind == JsonValueKind.Array
                    ? wd.EnumerateArray().Select(x => x.TryGetInt32(out var d) ? d : 0).Where(d => d is >= 1 and <= 7).ToHashSet()
                    : new HashSet<int>(),
                TimeFrom = Minutes(Str(p, "time_from")),
                TimeTo = Minutes(Str(p, "time_to")),
                ClientsOnly = string.Equals(Str(p, "audience"), "clients", StringComparison.OrdinalIgnoreCase),
                Priority = (int)(Num(p, "priority") ?? 0),
                StackManual = Bool(p, "stackable_with_manual"),
                StackPromos = Bool(p, "stackable_with_promos"),
                AllowBelowCost = Bool(p, "allow_below_cost"),
                Branches = Set(p, "branches"),
                LimitPerReceipt = Num(p, "limit_per_receipt") is { } lim && lim > 0 ? (int)lim : null,
            };
            var ok = kind switch
            {
                "buy_n_get_m" => r.Buy > 0 && r.Get > 0 && r.GetPercent > 0,
                "percent" => r.Percent > 0,
                "amount" => r.Amount > 0,
                "fixed_price" => r.FixedPrice >= 0 && Num(prm, "price") is not null,
                _ => false, // combo / qty_tiers — Ф2: касса их не считает (сервер поправит итог через quote)
            };
            return ok ? r : null;
        }
    }

    public sealed class LineInput
    {
        public int Index { get; init; }
        public string? ProductId { get; init; }
        public decimal Price { get; init; }
        public decimal Qty { get; init; }
        public string? CategoryName { get; init; }
        public string? BrandName { get; init; }
        public decimal PurchasePrice { get; init; }
        public decimal Manual { get; init; }
        /// <summary>Скидка по старым promotion_rules товара (PromotionRules.LineDiscount); null — правил нет.</summary>
        public decimal? Legacy { get; init; }
        /// <summary>Порядок среди равных цен (у сервера одинаковые строки — одна позиция): индекс первой строки того же товара и цены.</summary>
        public int Group { get; init; }
    }

    public sealed class Context
    {
        public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
        public bool HasClient { get; init; }
        public string? BranchId { get; init; }
        /// <summary>id категории → (название, id родителя).</summary>
        public IReadOnlyDictionary<string, (string Name, string? Parent)> Categories { get; init; } = new Dictionary<string, (string, string?)>();
        /// <summary>id бренда → название.</summary>
        public IReadOnlyDictionary<string, string> Brands { get; init; } = new Dictionary<string, string>();
        public TimeZoneInfo? Zone { get; init; }
    }

    public sealed record Applied(string Kind, string? Id, int Version, string Title, decimal Qty, decimal Amount);
    public sealed record Hint(string PromotionId, int NeedQty, decimal Percent, string Title);
    public sealed record Warning(string Code, int Index, decimal Cut, decimal? PurchasePrice);

    public sealed class LineResult
    {
        public int Index { get; init; }
        /// <summary>Скидка по акциям (после урезаний И-1/И-2).</summary>
        public decimal PromoDiscount { get; set; }
        /// <summary>Ручная скидка, которую примет сервер (0 — акция её не разрешает или урезана).</summary>
        public decimal ManualAllowed { get; set; }
        public List<Applied> Applied { get; } = new();
        /// <summary>У строки выбрана акция v2 (иначе — прежний расчёт: старая акция товара или ручная скидка).</summary>
        public bool HasV2 { get; set; }
        public bool ManualBlocked { get; set; }
        /// <summary>Все акции строки разрешают ручную скидку кассира (stackable_with_manual).</summary>
        public bool ManualOk { get; set; } = true;
    }

    public sealed class Result
    {
        public List<LineResult> Lines { get; } = new();
        public List<Hint> Hints { get; } = new();
        public List<Warning> Warnings { get; } = new();
    }

    private static readonly TimeZoneInfo Bishkek = FindZone();

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Bishkek"); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Bishkek+6", TimeSpan.FromHours(6), "Asia/Bishkek", "Asia/Bishkek");
        }
    }

    /// <summary>Действует ли акция в момент <paramref name="at"/> (время компании Asia/Bishkek): период [starts_at; ends_at), день недели (1 = Пн) и часы [с; до), через полночь — 22:00–02:00.</summary>
    public static bool IsActiveAt(Rule r, DateTimeOffset at, TimeZoneInfo? zone = null)
    {
        if (r.StartsAt is { } s && at < s)
            return false;
        if (r.EndsAt is { } e && at >= e)
            return false;
        var local = TimeZoneInfo.ConvertTime(at, zone ?? Bishkek);
        var dow = local.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)local.DayOfWeek;
        if (r.Weekdays.Count > 0 && !r.Weekdays.Contains(dow))
            return false;
        var t = local.Hour * 60 + local.Minute;
        return (r.TimeFrom, r.TimeTo) switch
        {
            ({ } f, { } to) when f < to => t >= f && t < to,
            ({ } f, { } to) when f > to => t >= f || t < to,
            ({ } f, null) => t >= f,
            (null, { } to) => t < to,
            _ => true,
        };
    }

    public static bool InScope(Rule r, LineInput line, Context ctx)
    {
        if (string.IsNullOrWhiteSpace(line.ProductId))
            return false;
        var pid = line.ProductId.Trim();
        if (r.Exclude.Contains(pid))
            return false;
        if (r.ScopeAll || r.Products.Contains(pid))
            return true;
        if (r.Brands.Count > 0 && !string.IsNullOrWhiteSpace(line.BrandName)
            && r.Brands.Any(b => ctx.Brands.TryGetValue(b, out var name) && string.Equals(name.Trim(), line.BrandName.Trim(), StringComparison.Ordinal)))
            return true;
        if (r.Categories.Count > 0 && !string.IsNullOrWhiteSpace(line.CategoryName))
        {
            var cname = line.CategoryName.Trim();
            foreach (var (id, info) in ctx.Categories)
            {
                if (!string.Equals(info.Name.Trim(), cname, StringComparison.Ordinal))
                    continue;
                // Категория включает подкатегории: идём вверх по родителям.
                var cur = id;
                for (var depth = 0; depth < 32 && cur is not null; depth++)
                {
                    if (r.Categories.Contains(cur))
                        return true;
                    cur = ctx.Categories.TryGetValue(cur, out var c) ? c.Parent : null;
                }
            }
        }

        return false;
    }

    public static Result Calculate(IReadOnlyList<LineInput> lines, IReadOnlyList<Rule> rules, Context ctx)
    {
        var result = new Result();
        var raw = new Dictionary<int, List<(Rule Rule, decimal Amount, decimal Qty)>>();
        foreach (var line in lines)
            raw[line.Index] = new List<(Rule, decimal, decimal)>();

        foreach (var rule in rules)
        {
            if (!IsActiveAt(rule, ctx.At, ctx.Zone))
                continue;
            if (rule.ClientsOnly && !ctx.HasClient)
                continue;
            if (rule.Branches.Count > 0 && (string.IsNullOrWhiteSpace(ctx.BranchId) || !rule.Branches.Contains(ctx.BranchId.Trim())))
                continue;
            var eligible = lines.Where(l => l.Price > 0 && l.Qty > 0 && InScope(rule, l, ctx)).ToList();
            if (eligible.Count == 0)
                continue;
            foreach (var (index, amount, qty) in RuleDiscounts(rule, eligible, result.Hints))
                if (amount > 0)
                    raw[index].Add((rule, amount, qty));
        }

        foreach (var line in lines)
        {
            var lr = new LineResult { Index = line.Index };
            result.Lines.Add(lr);
            var candidates = raw[line.Index];
            if (line.Legacy is { } legacy && legacy > 0)
                candidates.Add((new Rule { Legacy = true, Kind = "legacy_tier", Title = "", Priority = 0 }, legacy, line.Qty));
            if (candidates.Count == 0)
                continue;
            var ordered = candidates
                .Select((c, i) => (c.Rule, c.Amount, c.Qty, i))
                .OrderByDescending(c => c.Rule.Priority).ThenByDescending(c => c.Amount).ThenBy(c => c.i)
                .ToList();
            var chosen = new List<(Rule Rule, decimal Amount, decimal Qty)> { (ordered[0].Rule, ordered[0].Amount, ordered[0].Qty) };
            if (ordered[0].Rule.StackPromos)
                chosen.AddRange(ordered.Skip(1).Where(c => c.Rule.StackPromos).Select(c => (c.Rule, c.Amount, c.Qty)));
            if (chosen[0].Rule.Legacy)
                continue; // старая акция товара — прежний путь (PromotionRules.LineDiscount, ручная скидка не принимается)

            lr.HasV2 = true;
            var manualOk = chosen.All(c => c.Rule.StackManual);
            lr.ManualOk = manualOk;
            lr.ManualBlocked = !manualOk && line.Manual > 0;
            var manual = manualOk ? Round(line.Manual) : 0m;
            var amounts = chosen.Select(c => c.Amount).ToList();
            var gross = line.Qty * line.Price;
            var total = amounts.Sum() + manual;

            // И-2: скидка не больше суммы строки.
            var clampTo = Math.Max(0m, Round(gross));
            if (total > clampTo)
            {
                Cut(ref manual, amounts, total - clampTo);
                result.Warnings.Add(new Warning("discount_clamped", line.Index, total - clampTo, null));
                total = amounts.Sum() + manual;
            }

            // И-1: не ниже закупки (если не у всех акций строки allow_below_cost).
            if (line.PurchasePrice > 0 && !chosen.All(c => c.Rule.AllowBelowCost))
            {
                var max = Math.Max(0m, Round(gross - line.Qty * line.PurchasePrice));
                if (total > max)
                {
                    var cut = total - max;
                    Cut(ref manual, amounts, cut);
                    result.Warnings.Add(new Warning("promotion_capped_by_cost", line.Index, cut, line.PurchasePrice));
                }
            }

            lr.ManualAllowed = manual;
            for (var i = 0; i < chosen.Count; i++)
            {
                if (amounts[i] <= 0)
                    continue;
                lr.PromoDiscount += amounts[i];
                var r = chosen[i].Rule;
                lr.Applied.Add(new Applied("promotion", r.Id, r.Version, r.Title, chosen[i].Qty, amounts[i]));
            }
            if (manual > 0)
                lr.Applied.Add(new Applied("manual", null, 0, "", line.Qty, manual));
        }

        return result;
    }

    /// <summary>Урезание: сначала ручная скидка, потом акции с конца списка.</summary>
    private static void Cut(ref decimal manual, List<decimal> amounts, decimal cut)
    {
        var m = Math.Min(manual, cut);
        manual -= m;
        cut -= m;
        for (var i = amounts.Count - 1; i >= 0 && cut > 0; i--)
        {
            var a = Math.Min(amounts[i], cut);
            amounts[i] -= a;
            cut -= a;
        }
    }

    /// <summary>Скидка одной акции по каждой подходящей строке: (индекс строки, сумма до копейки, сколько штук со скидкой).</summary>
    private static IEnumerable<(int Index, decimal Amount, decimal Qty)> RuleDiscounts(Rule r, List<LineInput> eligible, List<Hint> hints)
    {
        // Порядок «от дорогих к дешёвым», равные — по порядку строк (у сервера — по порядку позиций).
        var byPrice = eligible.OrderByDescending(l => l.Price).ThenBy(l => l.Group).ThenBy(l => l.Index).ToList();
        switch (r.Kind)
        {
            case "buy_n_get_m":
            {
                var units = new List<LineInput>();
                foreach (var l in byPrice)
                    for (var i = 0; i < (int)Math.Floor(l.Qty); i++)
                        units.Add(l);
                var size = r.Buy + r.Get;
                var blocks = units.Count / size;
                if (r.LimitPerReceipt is { } lim)
                    blocks = Math.Min(blocks, lim);
                var acc = new Dictionary<int, (decimal Sum, int Qty)>();
                for (var b = 0; b < blocks; b++)
                    for (var k = b * size + r.Buy; k < (b + 1) * size; k++)
                    {
                        var u = units[k];
                        acc.TryGetValue(u.Index, out var cur);
                        acc[u.Index] = (cur.Sum + u.Price * Math.Min(100m, r.GetPercent) / 100m, cur.Qty + 1);
                    }

                var rest = units.Count - (units.Count / size) * size;
                if (r.LimitPerReceipt is not { } limit || units.Count / size < limit)
                    if (rest >= r.Buy && rest > 0)
                        hints.Add(new Hint(r.Id, size - rest, Math.Min(100m, r.GetPercent), r.Title));
                foreach (var l in eligible)
                    if (acc.TryGetValue(l.Index, out var a))
                        yield return (l.Index, Math.Min(Round(a.Sum), Round(l.Qty * l.Price)), a.Qty);
                yield break;
            }
            case "percent":
            case "amount":
            case "fixed_price":
            {
                decimal? left = r.LimitPerReceipt;
                foreach (var l in byPrice)
                {
                    if (left is <= 0)
                        break;
                    var gross = l.Qty * l.Price;
                    if (r.Kind == "amount" && r.PerLine)
                    {
                        left -= 1;
                        yield return (l.Index, Math.Min(Round(r.Amount), Round(gross)), l.Qty);
                        continue;
                    }
                    if (r.Kind == "percent" && r.MinAmount is { } min && Round(gross) < min)
                        continue;
                    var dq = l.Qty;
                    if (r.Kind == "percent" && r.MaxQty is { } mq && mq > 0)
                        dq = Math.Min(dq, mq);
                    if (left is { } lft)
                        dq = Math.Min(dq, lft);
                    var amount = r.Kind switch
                    {
                        "percent" => Round(dq * l.Price * Math.Min(100m, r.Percent) / 100m),
                        "amount" => Round(dq * Math.Min(r.Amount, l.Price)),
                        _ => l.Price > r.FixedPrice ? Round(dq * (l.Price - r.FixedPrice)) : 0m,
                    };
                    if (amount <= 0)
                        continue;
                    if (left is not null)
                        left -= dq;
                    yield return (l.Index, Math.Min(amount, Round(gross)), dq);
                }
                yield break;
            }
        }
    }

    public static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static decimal? Num(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }

    private static bool Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static HashSet<string> Set(JsonElement o, string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
            {
                var s = x.ValueKind switch { JsonValueKind.String => x.GetString(), JsonValueKind.Number => x.GetRawText(), _ => null };
                if (!string.IsNullOrWhiteSpace(s))
                    set.Add(s.Trim());
            }
        return set;
    }

    private static DateTimeOffset? Date(JsonElement o, string name) =>
        Str(o, name) is { Length: > 0 } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static int? Minutes(string? hhmm)
    {
        if (string.IsNullOrWhiteSpace(hhmm))
            return null;
        var parts = hhmm.Trim().Split(':');
        return parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && h is >= 0 and <= 24 && m is >= 0 and < 60
            ? h * 60 + m
            : null;
    }
}
