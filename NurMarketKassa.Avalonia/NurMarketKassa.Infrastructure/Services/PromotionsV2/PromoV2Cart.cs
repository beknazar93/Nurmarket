using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-27 (акции v2): связывает расчёт <see cref="PromoV2Engine"/> со снимком чека. Скидка акции кладётся В СТРОКУ чека (поле <c>promo_v2</c>:
/// discount — по акциям, manual — ручная, которую примет сервер, applied[] — разбор «Акция 2+1: −123,00»), поэтому экран, окно оплаты, тело запроса,
/// офлайн-очередь и печать берут скидку строки из одной функции CartDisplayHelper.EffectiveLineDiscount — как старые promotion_rules (продажа №1136).
/// Подсказки (hints) и предупреждения (warnings) — в корне чека (<c>promo_v2_hints</c>, <c>promo_v2_warnings</c>).
/// Без признака сервера promotions_v2 метки снимаются — касса считает как раньше.
/// Ответ сервера (pos/quote/ или 409 total_mismatch) можно наложить на чек (<see cref="ApplyServerLines"/>): тогда до следующего изменения состава чека
/// (отпечаток <c>promo_v2_fp</c>) показывается итог СЕРВЕРА, а не локальный.
/// </summary>
public static class PromoV2Cart
{
    public const string LineField = "promo_v2";
    public const string HintsField = "promo_v2_hints";
    public const string WarningsField = "promo_v2_warnings";
    public const string FingerprintField = "promo_v2_fp";
    public const string AtField = "promo_v2_at";

    /// <summary>Часы расчёта (для проверок — подменяются). Офлайн-чек считается на момент пробития (sold_at = «сейчас» при оплате).</summary>
    public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Только для проверок: включить расчёт без признака сервера.</summary>
    public static bool? EnabledOverride { get; set; }

    public static bool Enabled => EnabledOverride ?? ServerCapabilities.Has(ServerCapabilities.PromotionsV2);

    /// <summary>Пересчитать метки акций в строках чека. true — чек изменился (скидки строк пересчитать: RecalcLine).</summary>
    public static bool Apply(JsonObject root, DateTimeOffset? at = null)
    {
        if (root["items"] is not JsonArray items)
            return false;
        if (!Enabled)
            return Strip(root, items);

        PromoV2Store.RefreshInBackground();
        var state = PromoV2Store.Current;
        // Ответ сервера по этому же составу чека — главнее локального расчёта.
        if (root[FingerprintField]?.GetValue<string>() is { Length: > 0 } fp && fp == Fingerprint(root, items))
            return false;
        root.Remove(FingerprintField);
        // Акций нет — снять старые метки (если были) и не тратить время на разбор строк.
        if (state.Rules.Count == 0)
            return Strip(root, items);
        var before = Snapshot(root, items);


        // Сервер склеивает одинаковые строки (товар + цена + вариант) в одну позицию и считает акции по ней (проверено quote NBS 27.11: «−10 на строку»
        // у двух строк одного товара даёт −10 один раз) — касса считает так же: по группам, затем делит скидку группы между её строками по сумме.
        var nodes = new List<JsonObject>();
        var groupNodes = new List<List<int>>();
        var groupGross = new List<List<decimal>>();
        var inputs = new List<PromoV2Engine.LineInput>();
        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstLine = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var node in items)
            {
                if (node is not JsonObject line)
                    continue;
                line.Remove(LineField);
                nodes.Add(line);
                var doc = JsonDocument.Parse(line.ToJsonString());
                docs.Add(doc);
                var it = doc.RootElement;
                var custom = CartDisplayHelper.IsCustomLine(it);
                var pid = custom ? null : CartDisplayHelper.TryProductId(it);
                var price = (decimal)CartDisplayHelper.UnitPrice(it);
                var qty = (decimal)CartDisplayHelper.LineQuantity(it);
                var manual = (decimal)CartDisplayHelper.ManualLineDiscount(it);
                var key = pid is null ? "custom#" + nodes.Count : pid + "|" + price.ToString("0.###", CultureInfo.InvariantCulture) + "|" + CartDisplayHelper.ServerVariantId(it);
                if (byKey.TryGetValue(key, out var g))
                {
                    var prev = inputs[g];
                    inputs[g] = new PromoV2Engine.LineInput
                    {
                        Index = g, Group = g, ProductId = prev.ProductId, Price = prev.Price, Qty = prev.Qty + qty, CategoryName = prev.CategoryName,
                        BrandName = prev.BrandName, PurchasePrice = prev.PurchasePrice, Manual = prev.Manual + manual,
                    };
                    groupNodes[g].Add(nodes.Count - 1);
                    groupGross[g].Add(qty * price);
                    continue;
                }
                var info = PromoProductInfo.Get(pid);
                byKey[key] = inputs.Count;
                groupNodes.Add(new List<int> { nodes.Count - 1 });
                groupGross.Add(new List<decimal> { qty * price });
                firstLine.Add(it);
                inputs.Add(new PromoV2Engine.LineInput
                {
                    Index = inputs.Count, Group = inputs.Count, ProductId = pid, Price = price, Qty = qty,
                    CategoryName = info?.Category, BrandName = info?.Brand, PurchasePrice = info?.Purchase ?? 0m, Manual = manual,
                });
            }

            // Старые promotion_rules — по склеенной позиции (как у сервера).
            for (var g = 0; g < inputs.Count; g++)
            {
                var legacy = PromotionRules.LineDiscount(firstLine[g], (double)inputs[g].Qty, (double)inputs[g].Price);
                if (legacy is { } l)
                    inputs[g] = new PromoV2Engine.LineInput
                    {
                        Index = g, Group = g, ProductId = inputs[g].ProductId, Price = inputs[g].Price, Qty = inputs[g].Qty, CategoryName = inputs[g].CategoryName,
                        BrandName = inputs[g].BrandName, PurchasePrice = inputs[g].PurchasePrice, Manual = inputs[g].Manual, Legacy = (decimal)l,
                    };
            }
        }
        finally
        {
            foreach (var d in docs)
                d.Dispose();
        }

        root.Remove(HintsField);
        root.Remove(WarningsField);
        if (state.Rules.Count > 0 && inputs.Count > 0)
        {
            var when = at ?? Clock();
            var result = PromoV2Engine.Calculate(inputs, state.Rules, new PromoV2Engine.Context
            {
                At = when,
                HasClient = HasClient(root),
                BranchId = DeviceIdentity.BranchProvider(),
                Categories = state.Categories,
                Brands = state.Brands,
            });
            foreach (var lr in result.Lines)
            {
                if (!lr.HasV2)
                    continue;
                var members = groupNodes[lr.Index];
                if (members.Count == 1)
                {
                    nodes[members[0]][LineField] = Stamp(lr.PromoDiscount, lr.ManualAllowed, lr.ManualBlocked, lr.Applied, "local", lr.ManualOk);
                    continue;
                }
                // Склеенная позиция: скидка делится по сумме строк (до копейки, остаток — последней строке).
                var gross = groupGross[lr.Index];
                var sum = gross.Sum();
                decimal promoLeft = lr.PromoDiscount, manualLeft = lr.ManualAllowed;
                for (var i = 0; i < members.Count; i++)
                {
                    var last = i == members.Count - 1;
                    var share = sum > 0 ? gross[i] / sum : (last ? 1m : 0m);
                    var p = last ? promoLeft : PromoV2Engine.Round(lr.PromoDiscount * share);
                    var m = last ? manualLeft : PromoV2Engine.Round(lr.ManualAllowed * share);
                    promoLeft -= p;
                    manualLeft -= m;
                    nodes[members[i]][LineField] = Stamp(p, m, lr.ManualBlocked, lr.Applied.Select(a => a with { Amount = PromoV2Engine.Round(a.Amount * share) }), "local", lr.ManualOk);
                }
            }
            if (result.Hints.Count > 0)
                root[HintsField] = new JsonArray(result.Hints.Select(h => (JsonNode)new JsonObject
                {
                    ["promotion"] = h.PromotionId, ["need_qty"] = h.NeedQty, ["percent"] = (double)h.Percent, ["text"] = HintText(h.NeedQty, h.Percent),
                }).ToArray());
            if (result.Warnings.Count > 0)
                root[WarningsField] = new JsonArray(result.Warnings.Select(w => (JsonNode)new JsonObject
                {
                    ["code"] = w.Code, ["index"] = w.Index, ["cut"] = w.Cut.ToString("0.00", CultureInfo.InvariantCulture),
                    ["purchase_price"] = w.PurchasePrice?.ToString("0.00", CultureInfo.InvariantCulture),
                }).ToArray());
            root[AtField] = when.ToString("O");
        }

        return before != Snapshot(root, items);
    }

    /// <summary>Наложить строки ответа сервера (pos/quote/ lines[] или 409 total_mismatch lines[]) на чек: index ответа = порядок строк чека
    /// (запрос строится без склейки строк — <see cref="QuickCheckoutBody.TryBuildUnmerged"/>). true — что-то изменилось.</summary>
    public static bool ApplyServerLines(JsonObject root, JsonElement serverLines, JsonElement? hints = null, JsonElement? warnings = null)
    {
        if (root["items"] is not JsonArray items || serverLines.ValueKind != JsonValueKind.Array)
            return false;
        var nodes = items.OfType<JsonObject>().ToList();
        // Сервер склеивает одинаковые строки (товар + цена + вариант) в одну позицию.
        var groups = new List<List<int>>();
        var count = serverLines.GetArrayLength();
        if (count == nodes.Count)
            groups.AddRange(Enumerable.Range(0, nodes.Count).Select(i => new List<int> { i }));
        else
        {
            var byKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < nodes.Count; i++)
            {
                using var d = JsonDocument.Parse(nodes[i].ToJsonString());
                var it = d.RootElement;
                var pid = CartDisplayHelper.IsCustomLine(it) ? null : CartDisplayHelper.TryProductId(it);
                var key = pid is null ? "custom#" + i : pid + "|" + CartDisplayHelper.UnitPrice(it).ToString("0.###", CultureInfo.InvariantCulture) + "|" + CartDisplayHelper.ServerVariantId(it);
                if (!byKey.TryGetValue(key, out var g))
                {
                    byKey[key] = g = new List<int>();
                    groups.Add(g);
                }
                g.Add(i);
            }
        }
        // index ответа = номер ПЕРВОЙ строки склеенной позиции в запросе (NBS 27.11: строки 0,1,3 при склейке 0 и 2).
        var byFirst = groups.ToDictionary(g => g[0]);

        var before = Snapshot(root, items);
        foreach (var sl in serverLines.EnumerateArray())
        {
            if (!sl.TryGetProperty("index", out var ie) || !ie.TryGetInt32(out var index) || !byFirst.TryGetValue(index, out var group))
                continue;
            var applied = new List<PromoV2Engine.Applied>();
            decimal promo = 0, manual = 0;
            if (sl.TryGetProperty("applied", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                {
                    var kind = a.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    var amount = Dec(a, "amount");
                    if (kind == "manual")
                        manual += amount;
                    else
                        promo += amount;
                    applied.Add(new PromoV2Engine.Applied(kind,
                        a.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                        a.TryGetProperty("version", out var v) && v.TryGetInt32(out var ver) ? ver : 0,
                        a.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "",
                        Dec(a, "qty"), amount));
                }
            var serverDiscount = Dec(sl, "discount");
            // 409 total_mismatch присылает строки без applied[] — ручную часть берём из прежних меток строк (если были), остальное — акция.
            if (!sl.TryGetProperty("applied", out _))
                manual = Math.Min(serverDiscount, group.Sum(i => nodes[i][LineField] is JsonObject prev && prev["manual"] is { } pm ? (decimal)pm.GetValue<double>() : 0m));
            // Сервер мог дать скидку без разбора (старые ступени без applied) — всё, что сверх ручной, считаем акцией.
            if (promo + manual < serverDiscount)
                promo = serverDiscount - manual;

            // Склеенная позиция: скидка делится между строками по сумме строки (до копейки, остаток — последней).
            var gross = group.Select(i =>
            {
                using var d = JsonDocument.Parse(nodes[i].ToJsonString());
                return (decimal)CartDisplayHelper.LineQuantity(d.RootElement) * (decimal)CartDisplayHelper.UnitPrice(d.RootElement);
            }).ToList();
            var grossSum = gross.Sum();
            decimal promoLeft = promo, manualLeft = manual;
            for (var gi = 0; gi < group.Count; gi++)
            {
                var last = gi == group.Count - 1;
                var share = grossSum > 0 ? gross[gi] / grossSum : (last ? 1m : 0m);
                var linePromo = last ? promoLeft : PromoV2Engine.Round(promo * share);
                var lineManual = last ? manualLeft : PromoV2Engine.Round(manual * share);
                promoLeft -= linePromo;
                manualLeft -= lineManual;
                var lineApplied = group.Count == 1 ? applied : applied.Select(a => a with { Amount = PromoV2Engine.Round(a.Amount * share) }).ToList();
                var line = nodes[group[gi]];
                using var doc = JsonDocument.Parse(line.ToJsonString());
                var manualSent = (decimal)CartDisplayHelper.ManualLineDiscount(doc.RootElement);
                if (lineApplied.Count == 0 && linePromo + lineManual <= 0 && line[LineField] is null)
                    continue; // у строки нет акции ни у нас, ни у сервера — прежний путь
                var hasPromo = lineApplied.Any(a => a.Kind != "manual") || linePromo > 0;
                if (!hasPromo && lineManual == manualSent)
                {
                    line.Remove(LineField); // только ручная скидка, как отправили — метка не нужна
                    continue;
                }
                line[LineField] = Stamp(linePromo, lineManual, manualSent > lineManual, lineApplied, "server", !(manualSent > lineManual));
            }
        }

        root.Remove(HintsField);
        if (hints is { ValueKind: JsonValueKind.Array } h && h.GetArrayLength() > 0)
            root[HintsField] = JsonNode.Parse(h.GetRawText());
        root.Remove(WarningsField);
        if (warnings is { ValueKind: JsonValueKind.Array } w && w.GetArrayLength() > 0)
            root[WarningsField] = JsonNode.Parse(w.GetRawText());
        root[FingerprintField] = Fingerprint(root, items);
        return before != Snapshot(root, items);
    }

    /// <summary>Снять все метки (признак выключен). true — чек изменился.</summary>
    public static bool Strip(JsonObject root, JsonArray items)
    {
        var changed = false;
        foreach (var line in items.OfType<JsonObject>())
            changed |= line.Remove(LineField);
        changed |= root.Remove(HintsField);
        changed |= root.Remove(WarningsField);
        root.Remove(FingerprintField);
        root.Remove(AtField);
        return changed;
    }

    private static JsonObject Stamp(decimal promo, decimal manual, bool manualBlocked, IEnumerable<PromoV2Engine.Applied> applied, string src, bool manualOk = true) => new()
    {
        ["manual_ok"] = manualOk,
        ["discount"] = (double)promo,
        ["manual"] = (double)manual,
        ["manual_blocked"] = manualBlocked,
        ["src"] = src,
        ["applied"] = new JsonArray(applied.Select(a => (JsonNode)new JsonObject
        {
            ["kind"] = a.Kind, ["id"] = a.Id, ["version"] = a.Version, ["title"] = a.Title,
            ["qty"] = a.Qty.ToString("0.###", CultureInfo.InvariantCulture), ["amount"] = a.Amount.ToString("0.00", CultureInfo.InvariantCulture),
        }).ToArray()),
    };

    private static bool HasClient(JsonObject root) =>
        new[] { "client", "client_id" }.Any(k => root[k] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => !string.IsNullOrWhiteSpace(s),
            JsonObject o => o["id"] is not null,
            _ => false,
        });

    /// <summary>Отпечаток состава чека (товар, цена, количество, ручная скидка, скидка на чек) — пока он тот же, итог сервера остаётся в силе.</summary>
    public static string Fingerprint(JsonObject root, JsonArray items)
    {
        var sb = new StringBuilder();
        foreach (var line in items.OfType<JsonObject>())
        {
            using var doc = JsonDocument.Parse(line.ToJsonString());
            var it = doc.RootElement;
            sb.Append(CartDisplayHelper.TryProductId(it)).Append('|').Append(CartDisplayHelper.UnitPrice(it).ToString("0.###", CultureInfo.InvariantCulture))
              .Append('|').Append(CartDisplayHelper.LineQuantity(it).ToString("0.###", CultureInfo.InvariantCulture))
              .Append('|').Append(CartDisplayHelper.ManualLineDiscount(it).ToString("0.##", CultureInfo.InvariantCulture)).Append(';');
        }
        sb.Append(root["order_discount_percent"]?.ToJsonString()).Append(root["order_discount_total"]?.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string Snapshot(JsonObject root, JsonArray items) =>
        string.Join("#", items.OfType<JsonObject>().Select(l => l[LineField]?.ToJsonString() ?? "")) + root[HintsField]?.ToJsonString() + root[WarningsField]?.ToJsonString();

    // ───────── чтение меток (экран, печать, тело запроса) ─────────

    /// <summary>Метка акции строки: (скидка по акциям, ручная скидка, которую примет сервер); null — у строки акции v2 нет.</summary>
    public static (double Promo, double Manual)? Of(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty(LineField, out var s) || s.ValueKind != JsonValueKind.Object)
            return null;
        return ((double)Dec(s, "discount"), (double)Dec(s, "manual"));
    }

    /// <summary>Ручная скидка на строке недоступна: акция строки не сочетается с ручной (stackable_with_manual = false).</summary>
    public static bool ManualBlocked(JsonElement line) =>
        line.ValueKind == JsonValueKind.Object && line.TryGetProperty(LineField, out var s) && s.ValueKind == JsonValueKind.Object
        && ((s.TryGetProperty("manual_ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            || (s.TryGetProperty("manual_blocked", out var b) && b.ValueKind == JsonValueKind.True));

    /// <summary>Строки разбора для чека и корзины: «Акция «2+1»: −123,00». Ручная скидка — отдельной строкой «Скидка кассира».</summary>
    public static IReadOnlyList<string> AppliedLines(JsonElement line)
    {
        var list = new List<string>();
        if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty(LineField, out var s) || s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("applied", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var a in arr.EnumerateArray())
        {
            var amount = Dec(a, "amount");
            if (amount <= 0)
                continue;
            var kind = a.TryGetProperty("kind", out var k) ? k.GetString() : null;
            var title = a.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var label = kind == "manual"
                ? Tr.T("Скидка кассира", "Кассирдин арзандатуусу", "Cashier discount", "Kasiyer indirimi", "Kassir chegirmasi")
                : string.IsNullOrWhiteSpace(title)
                    ? Tr.T("Акция", "Акция", "Promotion", "Kampanya", "Aksiya")
                    : Tr.T($"Акция «{title}»", $"«{title}» акциясы", $"Promotion “{title}”", $"“{title}” kampanyası", $"«{title}» aksiyasi");
            list.Add($"{label}: −{MoneyText.Amount(amount)}");
        }
        return list;
    }

    /// <summary>Подсказки над корзиной («Добавьте ещё 1 — будет бесплатно»).</summary>
    public static IReadOnlyList<string> Hints(JsonElement cart)
    {
        var list = new List<string>();
        if (cart.ValueKind == JsonValueKind.Object && cart.TryGetProperty(HintsField, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var h in arr.EnumerateArray())
            {
                var need = h.TryGetProperty("need_qty", out var n) && n.TryGetInt32(out var q) ? q : 0;
                // Текст сервера — по-русски; на других языках интерфейса — свой перевод по числу.
                var text = Tr.T("ru", "ky", "en", "tr", "uz") == "ru" && h.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var pct = h.TryGetProperty("percent", out _) ? Dec(h, "percent") : 100m;
                text ??= need > 0 ? HintText(need, pct) : null;
                if (!string.IsNullOrWhiteSpace(text) && !list.Contains(text))
                    list.Add(text);
            }
        return list;
    }

    /// <summary>Предупреждения сервера/расчёта понятным текстом.</summary>
    public static IReadOnlyList<string> Warnings(JsonElement cart)
    {
        var list = new List<string>();
        if (cart.ValueKind == JsonValueKind.Object && cart.TryGetProperty(WarningsField, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var w in arr.EnumerateArray())
            {
                var code = w.TryGetProperty("code", out var c) ? c.GetString() : null;
                var text = WarningText(code, Dec(w, "cut"), w.TryGetProperty("purchase_price", out _) ? Dec(w, "purchase_price") : null);
                if (text is not null && !list.Contains(text))
                    list.Add(text);
            }
        return list;
    }

    public static string? WarningText(string? code, decimal cut, decimal? purchase) => code switch
    {
        "promotion_capped_by_cost" => Tr.T(
            $"Скидка по акции урезана на {MoneyText.Amount(cut)}: цена не может быть ниже закупки{(purchase is > 0 ? $" ({MoneyText.Amount(purchase.Value)})" : "")}.",
            $"Акциянын арзандатуусу {MoneyText.Amount(cut)} кыскартылды: баа сатып алуу баасынан төмөн болбойт{(purchase is > 0 ? $" ({MoneyText.Amount(purchase.Value)})" : "")}.",
            $"Promotion discount reduced by {MoneyText.Amount(cut)}: the price cannot go below cost{(purchase is > 0 ? $" ({MoneyText.Amount(purchase.Value)})" : "")}.",
            $"Kampanya indirimi {MoneyText.Amount(cut)} azaltıldı: fiyat alış fiyatının altına inemez{(purchase is > 0 ? $" ({MoneyText.Amount(purchase.Value)})" : "")}.",
            $"Aksiya chegirmasi {MoneyText.Amount(cut)} ga kamaytirildi: narx tannarxdan past bo'lolmaydi{(purchase is > 0 ? $" ({MoneyText.Amount(purchase.Value)})" : "")}."),
        "discount_clamped" => Tr.T(
            $"Скидка больше суммы строки — уменьшена на {MoneyText.Amount(cut)}.",
            $"Арзандатуу саптын суммасынан көп — {MoneyText.Amount(cut)} азайтылды.",
            $"Discount exceeded the line amount — reduced by {MoneyText.Amount(cut)}.",
            $"İndirim satır tutarını aştı — {MoneyText.Amount(cut)} azaltıldı.",
            $"Chegirma qator summasidan oshdi — {MoneyText.Amount(cut)} ga kamaytirildi."),
        _ => null,
    };

    public static string HintText(int need, decimal percent) => percent >= 100m
        ? Tr.T($"Добавьте ещё {need} — будет бесплатно", $"Дагы {need} кошуңуз — бекер болот", $"Add {need} more — it will be free", $"{need} tane daha ekleyin — bedava olacak", $"Yana {need} ta qo'shing — bepul bo'ladi")
        : Tr.T($"Добавьте ещё {need} — скидка {percent:0.##} %", $"Дагы {need} кошуңуз — {percent:0.##} % арзандатуу", $"Add {need} more — {percent:0.##}% off", $"{need} tane daha ekleyin — %{percent:0.##} indirim", $"Yana {need} ta qo'shing — {percent:0.##} % chegirma");

    private static decimal Dec(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v))
            return 0m;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return d;
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m;
    }
}
