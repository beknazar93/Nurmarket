using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Promotions;

/// <summary>2026-11-27 (ответ Умара «Конструктор акций Ф1 + pos/quote/», раздел 4; наше ТЗ «конструктор акций», раздел 7): локальный расчёт акций v2 должен
/// совпадать с сервером до копейки. Числа раздела 7 (1, 2, 3, 4, 6, 8, 10 + ручная скидка, фикс-цена, исключения, подсказка), сверка с 40 живыми ответами
/// pos/quote/ NBS (promo-v2-quote-fixture.json, записаны 27.11), свойства на случайных чеках, чек кассы (метки в строках, тело pos/checkout/ с expected_total),
/// 409 total_mismatch. Запускать только через tools/qa/test-safe.ps1.</summary>
[Collection("UserPreferences")]
public sealed class PromoV2EngineTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 27, 12, 0, 0, TimeSpan.FromHours(6)); // пятница

    public PromoV2EngineTests()
    {
        PromoV2Cart.EnabledOverride = true;
        PromoV2Cart.Clock = () => Noon;
        PromoProductInfo.ResetForTests();
    }

    public void Dispose()
    {
        PromoV2Cart.EnabledOverride = null;
        PromoV2Cart.Clock = () => DateTimeOffset.UtcNow;
        PromoV2Store.SetForTests(null);
        PromoProductInfo.ResetForTests();
    }

    private static PromoV2Engine.Rule R(string json) =>
        PromoV2Engine.Rule.Parse(JsonDocument.Parse(json).RootElement) ?? throw new InvalidOperationException("акция не разобрана: " + json);

    private const string Water21 = """{"id":"w","title":"2+1","kind":"buy_n_get_m","params":{"buy":2,"get":1,"get_discount_percent":100,"cheapest":true},"scope":{"products":["water","w120","w100","w80"]},"priority":10}""";

    private static PromoV2Engine.LineInput L(int index, string product, decimal price, decimal qty, decimal manual = 0, decimal purchase = 0, string? category = null, string? brand = null, decimal? legacy = null) =>
        new() { Index = index, ProductId = product, Price = price, Qty = qty, Manual = manual, PurchasePrice = purchase, CategoryName = category, BrandName = brand, Legacy = legacy, Group = index };

    private static decimal Total(IReadOnlyList<PromoV2Engine.LineInput> lines, params PromoV2Engine.Rule[] rules) =>
        Total(lines, new PromoV2Engine.Context { At = Noon }, rules);

    private static decimal Total(IReadOnlyList<PromoV2Engine.LineInput> lines, PromoV2Engine.Context ctx, params PromoV2Engine.Rule[] rules)
    {
        var r = PromoV2Engine.Calculate(lines, rules, ctx);
        return lines.Sum(l => l.Qty * l.Price) - r.Lines.Sum(x => x.HasV2 ? x.PromoDiscount + x.ManualAllowed : (lines[x.Index].Legacy ?? lines[x.Index].Manual));
    }

    // ───────────────────────── раздел 7 ТЗ ─────────────────────────

    [Theory]
    [InlineData(2, 200)]
    [InlineData(3, 200)]
    [InlineData(6, 400)]
    [InlineData(7, 500)]
    public void T1_TwoPlusOne_Water100(int qty, int expected) =>
        Total(new[] { L(0, "water", 100, qty) }, R(Water21)).Should().Be(expected);

    [Fact]
    public void T2_TwoPlusOne_Category_CheapestFree()
    {
        var rule = R("""{"id":"c","title":"2+1","kind":"buy_n_get_m","params":{"buy":2,"get":1,"get_discount_percent":100},"scope":{"categories":["parent"]},"priority":10}""");
        var ctx = new PromoV2Engine.Context
        {
            At = Noon,
            Categories = new Dictionary<string, (string, string?)> { ["parent"] = ("Напитки", null), ["child"] = ("Вода", "parent") },
        };
        var lines = new[] { L(0, "w120", 120, 1, category: "Вода"), L(1, "w100", 100, 1, category: "Вода"), L(2, "w80", 80, 1, category: "Вода") };
        var r = PromoV2Engine.Calculate(lines, new[] { rule }, ctx);
        r.Lines[2].PromoDiscount.Should().Be(80, "бесплатна самая дешёвая; подкатегория «Вода» входит в «Напитки»");
        Total(lines, ctx, rule).Should().Be(220);
    }

    [Fact]
    public void T3_Priority_TwoPlusOne_Beats_Percent()
    {
        var dairy = R("""{"id":"m","title":"-15% молочка","kind":"percent","params":{"percent":15},"scope":{"products":["kefir"]},"priority":5}""");
        var kefir = R("""{"id":"k","title":"2+1 кефир","kind":"buy_n_get_m","params":{"buy":2,"get":1,"get_discount_percent":100},"scope":{"products":["kefir"]},"priority":10}""");
        Total(new[] { L(0, "kefir", 60, 3) }, dairy, kefir).Should().Be(120);
    }

    [Fact]
    public void T3b_Priority_ZeroDiscountWinner_IsSkipped()
    {
        // NBS 27.11: акция с бо́льшим приоритетом, не давшая строке скидки (2 шт при 2+1), не мешает −15 %.
        var pct = R("""{"id":"m","title":"-15%","kind":"percent","params":{"percent":15},"scope":{"products":["water"]},"priority":5}""");
        Total(new[] { L(0, "water", 120, 2) }, R(Water21), pct).Should().Be(204);
    }

    [Theory]
    [InlineData(13, 59, true)]
    [InlineData(14, 0, false)]
    [InlineData(9, 59, false)]
    [InlineData(10, 0, true)]
    public void T4_HappyHours_EndExcluded(int h, int m, bool on)
    {
        var rule = R("""{"id":"h","title":"часы","kind":"percent","params":{"percent":5},"scope":{"all":true},"time_from":"10:00","time_to":"14:00"}""");
        PromoV2Engine.IsActiveAt(rule, new DateTimeOffset(2026, 11, 27, h, m, 0, TimeSpan.FromHours(6))).Should().Be(on);
    }

    [Theory]
    [InlineData(27, 21, 59, false)]
    [InlineData(27, 22, 0, true)]
    [InlineData(28, 1, 59, false)] // 28.11 — суббота, а акция только по пятницам (день недели — по календарной дате момента, как у сервера)
    [InlineData(27, 1, 0, true)]
    public void T4b_OverMidnight_And_Weekday(int day, int h, int m, bool on)
    {
        var rule = R("""{"id":"g","title":"ночь","kind":"amount","params":{"amount":"7.00","per":"unit"},"scope":{"all":true},"time_from":"22:00","time_to":"02:00","weekdays":[5]}""");
        PromoV2Engine.IsActiveAt(rule, new DateTimeOffset(2026, 11, day, h, m, 0, TimeSpan.FromHours(6))).Should().Be(on);
    }

    [Fact]
    public void T4c_Offline_Receipt_CountedAt_SoldAt()
    {
        var rule = """{"id":"h","title":"часы","status":"active","kind":"percent","params":{"percent":10},"scope":{"all":true},"time_from":"10:00","time_to":"14:00"}""";
        UseStore(rule);
        PromoV2Cart.Clock = () => new DateTimeOffset(2026, 11, 27, 15, 0, 0, TimeSpan.FromHours(6)); // досылка в 15:00
        var cart = NewCart(("p1", 100m, 1, 0m));
        var root = Root(cart);
        PromoV2Cart.Apply(root, new DateTimeOffset(2026, 11, 27, 13, 50, 0, TimeSpan.FromHours(6))).Should().BeTrue("чек пробит в 13:50");
        LineStamp(root, 0)!["discount"]!.GetValue<double>().Should().Be(10);
        PromoV2Cart.Apply(root).Should().BeTrue("в 15:00 акции уже нет");
        LineStamp(root, 0).Should().BeNull();
    }

    [Fact]
    public void T6_LimitPerReceipt_Blocks()
    {
        var rule = R("""{"id":"w","title":"2+1","kind":"buy_n_get_m","params":{"buy":2,"get":1,"get_discount_percent":100},"scope":{"products":["water"]},"limit_per_receipt":1}""");
        Total(new[] { L(0, "water", 100, 6) }, rule).Should().Be(500);
    }

    [Fact]
    public void T8_BelowCost_Capped_Unless_Allowed()
    {
        var capped = PromoV2Engine.Calculate(new[] { L(0, "p", 100, 1, purchase: 90) },
            new[] { R("""{"id":"a","title":"-20%","kind":"percent","params":{"percent":20},"scope":{"all":true}}""") }, new PromoV2Engine.Context { At = Noon });
        capped.Lines[0].PromoDiscount.Should().Be(10, "цена 90 = закупка");
        capped.Warnings.Should().ContainSingle(w => w.Code == "promotion_capped_by_cost" && w.Cut == 10 && w.PurchasePrice == 90);
        var allowed = PromoV2Engine.Calculate(new[] { L(0, "p", 100, 1, purchase: 90) },
            new[] { R("""{"id":"a","title":"-20%","kind":"percent","params":{"percent":20},"scope":{"all":true},"allow_below_cost":true}""") }, new PromoV2Engine.Context { At = Noon });
        allowed.Lines[0].PromoDiscount.Should().Be(20);
        allowed.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void T10_LegacyRules_StayAsBefore_PriorityZero()
    {
        var line = JsonDocument.Parse("""{"promotion_rules":[{"min_amount":"300","discount_percent":"10","promo_quantity":10}]}""").RootElement;
        var legacy = (decimal)PromotionRules.LineDiscount(line, 11, 30)!.Value;
        legacy.Should().Be(30m, "30 × 11 при пределе 10 → 300 × 10 %");
        var r = PromoV2Engine.Calculate(new[] { L(0, "p", 30, 11, legacy: legacy) }, Array.Empty<PromoV2Engine.Rule>(), new PromoV2Engine.Context { At = Noon });
        r.Lines[0].HasV2.Should().BeFalse("старая ступень — прежний путь расчёта");
        // v2 с приоритетом 1 главнее старой ступени (priority 0)
        var v2 = R("""{"id":"x","title":"-5%","kind":"percent","params":{"percent":5},"scope":{"all":true},"priority":1}""");
        PromoV2Engine.Calculate(new[] { L(0, "p", 30, 11, legacy: legacy) }, new[] { v2 }, new PromoV2Engine.Context { At = Noon }).Lines[0].PromoDiscount.Should().Be(16.5m);
    }

    [Fact]
    public void Manual_Discount_OnlyWhenStackable()
    {
        var plain = R("""{"id":"a","title":"p","kind":"percent","params":{"percent":10},"scope":{"all":true}}""");
        var stack = R("""{"id":"a","title":"p","kind":"percent","params":{"percent":10},"scope":{"all":true},"stackable_with_manual":true}""");
        var r1 = PromoV2Engine.Calculate(new[] { L(0, "p", 120, 1, manual: 5) }, new[] { plain }, new PromoV2Engine.Context { At = Noon }).Lines[0];
        (r1.PromoDiscount, r1.ManualAllowed, r1.ManualBlocked).Should().Be((12m, 0m, true));
        var r2 = PromoV2Engine.Calculate(new[] { L(0, "p", 120, 1, manual: 5) }, new[] { stack }, new PromoV2Engine.Context { At = Noon }).Lines[0];
        (r2.PromoDiscount, r2.ManualAllowed).Should().Be((12m, 5m));
        // NBS: 100 (закупка 90), −10 % + ручная 15 → урезается ручная целиком (cut 15), итог строки 90.
        var r3 = PromoV2Engine.Calculate(new[] { L(0, "p", 100, 1, manual: 15, purchase: 90) }, new[] { stack }, new PromoV2Engine.Context { At = Noon });
        (r3.Lines[0].PromoDiscount, r3.Lines[0].ManualAllowed).Should().Be((10m, 0m));
        r3.Warnings.Single().Cut.Should().Be(15);
    }

    [Fact]
    public void Stackable_Promos_Sum_From_Gross()
    {
        var d = R("""{"id":"d","title":"D 10%","kind":"percent","params":{"percent":10},"scope":{"all":true},"priority":20,"stackable_with_promos":true}""");
        var e = R("""{"id":"e","title":"E 20%","kind":"percent","params":{"percent":20},"scope":{"all":true},"priority":20,"stackable_with_promos":true}""");
        var r = PromoV2Engine.Calculate(new[] { L(0, "p", 120, 3) }, new[] { d, e }, new PromoV2Engine.Context { At = Noon }).Lines[0];
        r.PromoDiscount.Should().Be(108, "72 + 36, как в quote NBS");
        r.Applied.Select(a => a.Title).Should().Equal("E 20%", "D 10%");
    }

    [Fact]
    public void FixedPrice_And_Amount_And_Limits()
    {
        var fixedP = R("""{"id":"f","title":"79","kind":"fixed_price","params":{"price":"79.00"},"scope":{"all":true}}""");
        var lines = new[] { L(0, "a", 120, 2), L(1, "b", 100, 1), L(2, "c", 80, 1), L(3, "d", 33.33m, 1) };
        Total(lines, fixedP).Should().Be(349.33m, "как quote NBS: 82 + 21 + 1, дешёвый товар без скидки");
        var limited = R("""{"id":"f","title":"79","kind":"fixed_price","params":{"price":"79.00"},"scope":{"all":true},"limit_per_receipt":2}""");
        Total(new[] { L(0, "c", 80, 2), L(1, "a", 120, 2) }, limited).Should().Be(318, "лимит 2 шт достаётся самым дорогим");
        var perLine = R("""{"id":"l","title":"−20","kind":"amount","params":{"amount":"20.00","per":"line"},"scope":{"all":true}}""");
        Total(new[] { L(0, "x", 33.33m, 3), L(1, "c", 80, 2), L(2, "x", 33.33m, 1) }, perLine).Should().Be(233.32m);
        var perUnit = R("""{"id":"u","title":"−40","kind":"amount","params":{"amount":"40.00","per":"unit"},"scope":{"all":true}}""");
        Total(new[] { L(0, "x", 33.33m, 3) }, perUnit).Should().Be(0, "скидка не больше суммы строки");
    }

    [Fact]
    public void Exclusions_And_Brands()
    {
        var rule = R("""{"id":"b","title":"бренд","kind":"percent","params":{"percent":10},"scope":{"brands":["br"],"exclude_products":["skip"]}}""");
        var ctx = new PromoV2Engine.Context { At = Noon, Brands = new Dictionary<string, string> { ["br"] = "Aos" } };
        var r = PromoV2Engine.Calculate(new[] { L(0, "keep", 100, 1, brand: "Aos"), L(1, "skip", 100, 1, brand: "Aos"), L(2, "other", 100, 1, brand: "Bos") }, new[] { rule }, ctx);
        r.Lines.Select(l => l.PromoDiscount).Should().Equal(10m, 0m, 0m);
    }

    [Fact]
    public void Hint_AddOneMore_ForFree()
    {
        var r = PromoV2Engine.Calculate(new[] { L(0, "water", 100, 2) }, new[] { R(Water21) }, new PromoV2Engine.Context { At = Noon });
        r.Hints.Should().ContainSingle(h => h.NeedQty == 1);
        PromoV2Cart.HintText(1, 100).Should().Be("Добавьте ещё 1 — будет бесплатно");
        PromoV2Engine.Calculate(new[] { L(0, "water", 100, 1) }, new[] { R(Water21) }, new PromoV2Engine.Context { At = Noon }).Hints.Should().BeEmpty("в неполном блоке меньше N");
    }

    [Fact]
    public void Rounding_HalfUp_PerLine()
    {
        var half = R("""{"id":"h","title":"50%","kind":"buy_n_get_m","params":{"buy":2,"get":1,"get_discount_percent":50},"scope":{"all":true},"allow_below_cost":true}""");
        PromoV2Engine.Calculate(new[] { L(0, "x", 33.33m, 3) }, new[] { half }, new PromoV2Engine.Context { At = Noon }).Lines[0].PromoDiscount.Should().Be(16.67m);
        PromoV2Engine.Calculate(new[] { L(0, "x", 33.33m, 6) }, new[] { half }, new PromoV2Engine.Context { At = Noon }).Lines[0].PromoDiscount.Should().Be(33.33m);
        // равные цены: бесплатная штука — у более поздней строки (NBS: 1 + 2 шт → скидка у второй строки)
        var r = PromoV2Engine.Calculate(new[] { L(0, "x", 33.33m, 1), L(1, "x", 33.33m, 2) }, new[] { half }, new PromoV2Engine.Context { At = Noon });
        r.Lines.Select(l => l.PromoDiscount).Should().Equal(0m, 16.67m);
    }

    // ───────────────────────── сверка с живыми ответами pos/quote/ (NBS 27.11) ─────────────────────────

    private static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    [Fact]
    public void Matches_Server_Quote_On_Recorded_Carts()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Here(), "promo-v2-quote-fixture.json")));
        var fx = doc.RootElement;
        var rules = fx.GetProperty("active").GetProperty("promotions").EnumerateArray().Select(PromoV2Engine.Rule.Parse).Where(r => r is not null).Cast<PromoV2Engine.Rule>().ToList();
        rules.Should().HaveCountGreaterThan(5);
        var products = fx.GetProperty("products").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!, p => p);
        var cats = fx.GetProperty("categories").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!,
            c => (c.GetProperty("name").GetString()!, c.GetProperty("parent").ValueKind == JsonValueKind.String ? c.GetProperty("parent").GetString() : null));
        var brands = fx.GetProperty("brands").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("name").GetString()!);
        var failures = new List<string>();
        var cases = 0;
        foreach (var c in fx.GetProperty("cases").EnumerateArray())
        {
            cases++;
            var req = c.GetProperty("request");
            var resp = c.GetProperty("response");
            var lines = new List<PromoV2Engine.LineInput>();
            var groups = new Dictionary<string, int>();
            var i = 0;
            var reqIndex = -1;
            var firstReq = new Dictionary<int, int>(); // index строки запроса → позиция (сервер отвечает index первой строки склеенной позиции)
            foreach (var it in req.GetProperty("items").EnumerateArray())
            {
                reqIndex++;
                var pid = it.GetProperty("product").GetString()!;
                var p = products[pid];
                var price = decimal.Parse(it.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
                var qty = decimal.Parse(it.GetProperty("qty").GetString()!, CultureInfo.InvariantCulture);
                var manual = it.TryGetProperty("discount", out var d) ? decimal.Parse(d.GetString()!, CultureInfo.InvariantCulture) : 0m;
                // Как сервер (и PromoV2Cart): одинаковые строки (товар + цена) — одна позиция, количество и ручная скидка складываются.
                var key = pid + "|" + price;
                if (groups.TryGetValue(key, out var g))
                {
                    var prev = lines[g];
                    lines[g] = new PromoV2Engine.LineInput
                    {
                        Index = g, Group = g, ProductId = pid, Price = price, Qty = prev.Qty + qty, Manual = prev.Manual + manual,
                        PurchasePrice = prev.PurchasePrice, CategoryName = prev.CategoryName, BrandName = prev.BrandName,
                    };
                    continue;
                }
                groups[key] = i;
                firstReq[reqIndex] = i;
                lines.Add(new PromoV2Engine.LineInput
                {
                    Index = i, Group = i, ProductId = pid, Price = price, Qty = qty, Manual = manual,
                    PurchasePrice = decimal.Parse(p.GetProperty("purchase_price").GetString()!, CultureInfo.InvariantCulture),
                    CategoryName = p.GetProperty("category").ValueKind == JsonValueKind.String ? p.GetProperty("category").GetString() : null,
                    BrandName = p.GetProperty("brand").ValueKind == JsonValueKind.String ? p.GetProperty("brand").GetString() : null,
                });
                i++;
            }
            var at = DateTimeOffset.Parse(resp.GetProperty("at").GetString()!, CultureInfo.InvariantCulture);
            var result = PromoV2Engine.Calculate(lines, rules, new PromoV2Engine.Context { At = at, Categories = cats, Brands = brands });
            foreach (var sl in resp.GetProperty("lines").EnumerateArray())
            {
                var idx = sl.GetProperty("index").GetInt32();
                var server = decimal.Parse(sl.GetProperty("discount").GetString()!, CultureInfo.InvariantCulture);
                if (!firstReq.TryGetValue(idx, out idx))
                {
                    failures.Add($"чек {cases}: строка сервера {sl.GetProperty("index").GetInt32()} не первая в позиции");
                    continue;
                }
                var lr = result.Lines[idx];
                var local = lr.HasV2 ? lr.PromoDiscount + lr.ManualAllowed : lines[idx].Manual;
                if (local != server)
                    failures.Add($"чек {cases} строка {idx}: касса {local}, сервер {server} ({sl.GetRawText()})");
            }
            var hintsServer = resp.GetProperty("hints").GetArrayLength();
            if (hintsServer != result.Hints.Count)
                failures.Add($"чек {cases}: подсказок касса {result.Hints.Count}, сервер {hintsServer}");
        }
        cases.Should().BeGreaterThanOrEqualTo(20);
        failures.Should().BeEmpty();
    }

    // ───────────────────────── свойства ─────────────────────────

    private static readonly string[] Kinds = { "buy_n_get_m", "percent", "amount", "amount_line", "fixed_price" };

    private static readonly Gen<PromoV2Engine.Rule> RuleGen =
        Gen.Select(Gen.Select(Gen.Int[0, 4], Gen.Int[1, 4], Gen.Int[1, 3], Gen.Int[1, 100], Gen.Int[0, 30]), Gen.Select(Gen.Int[0, 3], Gen.Bool, Gen.Bool, Gen.Bool, Gen.Int[0, 2]))
            .Select(t =>
            {
                var ((k, buy, get, pct, prio), (lim, stackP, stackM, below, scope)) = t;
                var kind = Kinds[k];
                var prm = kind switch
                {
                    "buy_n_get_m" => $$"""{"buy":{{buy}},"get":{{get}},"get_discount_percent":{{pct}}}""",
                    "percent" => $$"""{"percent":{{pct}}}""",
                    "amount" => $$"""{"amount":"{{pct}}.50","per":"unit"}""",
                    "amount_line" => $$"""{"amount":"{{pct}}.00","per":"line"}""",
                    _ => $$"""{"price":"{{pct}}.99"}""",
                };
                var sc = scope switch { 0 => """{"all":true}""", 1 => """{"products":["p0","p2"]}""", _ => """{"all":true,"exclude_products":["p1"]}""" };
                return R($$"""{"id":"{{Guid.NewGuid():N}}","title":"r","kind":"{{(kind == "amount_line" ? "amount" : kind)}}","params":{{prm}},"scope":{{sc}},"priority":{{prio}},"limit_per_receipt":{{(lim == 0 ? "null" : lim.ToString())}},"stackable_with_promos":{{(stackP ? "true" : "false")}},"stackable_with_manual":{{(stackM ? "true" : "false")}},"allow_below_cost":{{(below ? "true" : "false")}}}""");
            });

    private static readonly Gen<PromoV2Engine.LineInput> LineGen =
        Gen.Select(Gen.Int[0, 3], Gen.Int[1, 50000], Gen.Int[1, 9], Gen.Int[0, 3000], Gen.Int[0, 60000])
            .Select(t => new PromoV2Engine.LineInput
            {
                ProductId = "p" + t.Item1, Price = t.Item2 / 100m, Qty = t.Item3, Manual = t.Item4 / 100m, PurchasePrice = t.Item5 % 3 == 0 ? 0 : t.Item5 / 100m,
            });

    [Fact]
    public void Properties_RandomCarts()
    {
        Gen.Select(LineGen.List[1, 6], RuleGen.List[0, 4]).Sample(t =>
        {
            var (raw, rules) = t;
            var lines = raw.Select((l, i) => new PromoV2Engine.LineInput
            {
                Index = i, Group = i, ProductId = l.ProductId, Price = l.Price, Qty = l.Qty, Manual = Math.Min(l.Manual, l.Price * l.Qty), PurchasePrice = l.PurchasePrice,
            }).ToList();
            var ctx = new PromoV2Engine.Context { At = Noon };
            var a = PromoV2Engine.Calculate(lines, rules, ctx);
            var b = PromoV2Engine.Calculate(lines, rules, ctx);
            foreach (var lr in a.Lines)
            {
                var l = lines[lr.Index];
                var gross = l.Qty * l.Price;
                var disc = lr.PromoDiscount + lr.ManualAllowed;
                disc.Should().BeGreaterThanOrEqualTo(0);
                disc.Should().BeLessThanOrEqualTo(Math.Round(gross, 2, MidpointRounding.AwayFromZero), "И-2");
                (gross - disc).Should().BeGreaterThanOrEqualTo(-0.005m);
                if (lr.HasV2 && l.PurchasePrice > 0 && lr.PromoDiscount > 0 && !rules.Where(r => lr.Applied.Any(x => x.Id == r.Id)).All(r => r.AllowBelowCost))
                    (gross - disc).Should().BeGreaterThanOrEqualTo(Math.Min(gross, l.Qty * l.PurchasePrice) - 0.01m, "И-1");
                lr.PromoDiscount.Should().Be(lr.Applied.Where(x => x.Kind == "promotion").Sum(x => x.Amount), "сумма разбора = скидка строки");
                (lr.PromoDiscount * 100 % 1).Should().Be(0, "до копейки");
            }
            a.Lines.Select(x => (x.PromoDiscount, x.ManualAllowed)).Should().Equal(b.Lines.Select(x => (x.PromoDiscount, x.ManualAllowed)), "повторяемость");
        }, iter: 3000);
    }

    // ───────────────────────── чек кассы: метки, итог, тело pos/checkout/ ─────────────────────────

    private static void UseStore(params string[] promotions)
    {
        var state = new PromoV2Store.State();
        var raw = (JsonArray)JsonNode.Parse("[" + string.Join(",", promotions) + "]")!;
        state.RawPromotions = raw;
        state.Rules = promotions.Select(p => PromoV2Engine.Rule.Parse(JsonDocument.Parse(p).RootElement)!).ToList();
        PromoV2Store.SetForTests(state);
    }

    private static CartService NewCart(params (string Id, decimal Price, int Qty, decimal Purchase)[] lines)
    {
        var cart = new CartService();
        LocalCartService.StartNewLocalCart(cart, "shift-promo");
        foreach (var (id, price, qty, purchase) in lines)
        {
            var pid = id.Length == 36 ? id : Guid.NewGuid().ToString();
            PromoProductInfo.Set(id, new PromoProductInfo.Info(null, null, purchase));
            PromoProductInfo.Set(pid, new PromoProductInfo.Info(null, null, purchase));
            var tile = new CatalogProductTileVm(pid, "Товар " + id, price.ToString("0.00", CultureInfo.InvariantCulture) + " сом", false) { Quantity = 1000 };
            LocalCartService.AddProduct(cart, tile, qty.ToString(CultureInfo.InvariantCulture));
        }
        return cart;
    }

    private static JsonObject Root(CartService cart) => (JsonObject)JsonNode.Parse(cart.GetRawText())!;

    private static JsonObject? LineStamp(JsonObject root, int i) => ((JsonArray)root["items"]!)[i]![PromoV2Cart.LineField] as JsonObject;

    [Fact]
    public void Cart_TwoPlusOne_Total_Body_And_ExpectedTotal()
    {
        var pid = Guid.NewGuid().ToString();
        UseStore($$$"""{"id":"w","title":"2+1","status":"active","kind":"buy_n_get_m","params":{"buy":2,"get":1,"get_discount_percent":100},"scope":{"products":["{{{pid}}}"]},"priority":10}""");
        var cart = NewCart((pid, 123m, 3, 0m), (Guid.NewGuid().ToString(), 50m, 1, 0m));
        var totals = CartTotalsCalculator.Calculate(cart.Root);
        totals.TotalDue.Should().Be(296, "369 − 123 + 50");
        var line0 = CartDisplayHelper.EnumerateItems(cart.Root).First();
        PromoV2Cart.AppliedLines(line0).Should().ContainSingle().Which.Should().Be("Акция «2+1»: −123,00");

        ServerCapabilities.SetFeaturesForTests(ServerCapabilities.PromotionsV2, ServerCapabilities.PosQuote);
        try
        {
            var body = QuickCheckoutBody.TryBuild(cart.GetRawText(), "cash", "300", null, null, Guid.NewGuid().ToString(), null, false, null, false, out var why);
            body.Should().NotBeNull(why);
            body!["items"]![0]!["discount"].Should().BeNull("скидку акции считает сервер — в теле только ручная");
            QuickCheckoutBody.ComputeTotal(body).Should().Be(296);
            body[QuickCheckoutBody.ExpectedTotalField]!.GetValue<string>().Should().Be("296.00");
            QuickCheckoutBody.Fingerprint(body).Should().NotContain("expected_total", "ключ повтора не зависит от признака pos_quote");
        }
        finally
        {
            ServerCapabilities.ResetMemoryForTests();
        }
    }

    [Fact]
    public void Cart_Feature_Off_Strips_Stamps()
    {
        var pid = Guid.NewGuid().ToString();
        UseStore($$$"""{"id":"w","title":"−10%","status":"active","kind":"percent","params":{"percent":10},"scope":{"products":["{{{pid}}}"]}}""");
        var cart = NewCart((pid, 100m, 1, 0m));
        CartTotalsCalculator.Calculate(cart.Root).TotalDue.Should().Be(90);
        PromoV2Cart.EnabledOverride = false;
        var root = Root(cart);
        PromoV2Cart.Apply(root).Should().BeTrue();
        LineStamp(root, 0).Should().BeNull("без признака promotions_v2 касса считает как раньше");
    }

    [Fact]
    public void Server_409_TotalMismatch_Recognized_And_Applied()
    {
        var payload = JsonDocument.Parse("""
            {"detail":"Итог чека на сервере отличается от итога кассы.","code":"total_mismatch","server_total":"252.00","expected_total":"1.00",
             "lines":[{"index":0,"product":"p","variant":null,"name":null,"qty":"3.000","price":"120.00","discount":"108.00","amount":"252.00"}]}
            """).RootElement.Clone();
        PosQuoteService.IsTotalMismatch(new ApiException("x", 409, payload)).Should().BeTrue();
        PosQuoteService.IsTotalMismatch(new ApiException("x", 409, JsonDocument.Parse("""{"code":"in_progress"}""").RootElement.Clone())).Should().BeFalse();
        PosQuoteService.MismatchText(payload).Should().Contain("было 1,00").And.Contain("стало 252,00").And.Contain("НЕ проведена");

        PromoV2Store.SetForTests(new PromoV2Store.State());
        var cart = NewCart((Guid.NewGuid().ToString(), 120m, 3, 0m));
        var root = Root(cart);
        PromoV2Cart.ApplyServerLines(root, payload.GetProperty("lines")).Should().BeTrue();
        LineStamp(root, 0)!["discount"]!.GetValue<double>().Should().Be(108);
        using var doc = JsonDocument.Parse(root.ToJsonString());
        CartDisplayHelper.EffectiveLineDiscount(CartDisplayHelper.EnumerateItems(doc.RootElement).First()).Should().Be(108);
        PromoV2Cart.Apply(root).Should().BeFalse("до изменения состава чека остаётся итог сервера");
    }

    [Fact]
    public void Quote_Response_Parsed_And_Applied_291()
    {
        // Пример из ответа Умара: 3 × 123 (2+1) + 50 (−10 %) = 291,00.
        var resp = JsonDocument.Parse("""
            {"subtotal":"419.00","lines":[{"index":0,"product":"a","qty":"3.000","price":"123.00","subtotal":"369.00","discount":"123.00","line_total":"246.00",
               "applied":[{"kind":"promotion","id":"p1","version":1,"title":"2+1","qty":"1","amount":"123.00"}]},
              {"index":1,"product":"b","qty":"1.000","price":"50.00","subtotal":"50.00","discount":"5.00","line_total":"45.00",
               "applied":[{"kind":"promotion","id":"p2","version":1,"title":"-10%","qty":"1.000","amount":"5.00"}]}],
             "line_discount_total":"128.00","order_discounts":[],"bonus_redeem":"0.00","total":"291.00","discount_total":"128.00",
             "warnings":[],"hints":[{"promotion":"p1","need_qty":1,"text":"Добавьте ещё 1 — будет бесплатно"}],"price_mismatch":[],"rules_version":"v"}
            """).RootElement;
        var q = PosQuoteService.Parse(resp)!;
        q.Total.Should().Be(291m);

        // Локально те же акции дают тот же итог.
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        UseStore($$$"""{"id":"p1","title":"2+1","status":"active","kind":"buy_n_get_m","params":{"buy":2,"get":1,"get_discount_percent":100},"scope":{"products":["{{{a}}}"]},"priority":10}""",
                 $$$"""{"id":"p2","title":"-10%","status":"active","kind":"percent","params":{"percent":10},"scope":{"products":["{{{b}}}"]}}""");
        var cart = NewCart((a, 123m, 3, 0m), (b, 50m, 1, 0m));
        ((decimal)CartTotalsCalculator.Calculate(cart.Root).TotalDue).Should().Be(q.Total);

        var root = Root(cart);
        PromoV2Cart.ApplyServerLines(root, q.Lines, q.Hints, q.Warnings);
        using var doc = JsonDocument.Parse(root.ToJsonString());
        PromoV2Cart.Hints(doc.RootElement).Should().ContainSingle().Which.Should().Be("Добавьте ещё 1 — будет бесплатно");
    }
}
