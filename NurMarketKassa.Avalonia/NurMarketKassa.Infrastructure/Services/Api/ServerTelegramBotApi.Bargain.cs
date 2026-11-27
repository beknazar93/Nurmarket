using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 2: торг», docs/ТЗ/Умар — API торга бота-продавца Ф2 (26.11).md): правила торга по товарам (telegram-bot/bargain/products/),
/// симулятор «Проверить торг» (bargain/test/), журнал оферт (bargain/offers/). Цену решает сервер; «пол» цены и закупка видны только владельцу. Признак сервера — bot_bargain.</summary>
public interface IBotBargainApi
{
    Task<BotBargainProductsPage> GetBargainProductsAsync(string? search, string? categoryId, bool onlyRules, int page, CancellationToken ct = default);

    /// <summary>PUT правил товара. Ошибка 400 (min_price выше цены, скидка вне 0…50) — ApiException, поля разбирает <see cref="BotBargainText.Describe"/>.</summary>
    Task<BotBargainProduct?> SaveBargainProductAsync(string productId, BotBargainRule rule, CancellationToken ct = default);

    /// <summary>DELETE — сбросить правила товара («как в общих настройках»).</summary>
    Task ResetBargainProductAsync(string productId, CancellationToken ct = default);

    /// <summary>Симулятор торга: тест-чат в отчёт не попадает. reset — начать торг заново.</summary>
    Task<BotBargainTest> TestBargainAsync(string productId, decimal? offer, bool reset, CancellationToken ct = default);

    Task<BotBargainOffersPage> GetBargainOffersAsync(DateTime from, DateTime to, string? decision, int page, CancellationToken ct = default);

    /// <summary>Категории магазина (id и имя) — для «Категории, где торг разрешён».</summary>
    Task<IReadOnlyList<BotCategory>> GetCategoriesAsync(CancellationToken ct = default);
}

public sealed record BotCategory(string Id, string Name);

/// <summary>Правила торга товара: Allowed null — «как в общих настройках»; MinPrice / MaxDiscountPercent null — не заданы.</summary>
public sealed record BotBargainRule(bool? Allowed, decimal? MinPrice, decimal? MaxDiscountPercent);

/// <summary>Строка списка правил торга. Floor — итоговый пол цены (max из минимальной цены, закупки с маржой и лимита скидки): только владельцу.</summary>
public sealed record BotBargainProduct(string ProductId, string Name, decimal Price, decimal? PurchasePrice, string? CategoryId, bool? Allowed, decimal? MinPrice,
    decimal? MaxDiscountPercent, bool EffectiveAllowed, decimal? Floor)
{
    public BotBargainRule Rule => new(Allowed, MinPrice, MaxDiscountPercent);

    public bool HasRule => Allowed is not null || MinPrice is not null || MaxDiscountPercent is not null;
}

public sealed record BotBargainProductsPage(int? Count, bool HasNext, IReadOnlyList<BotBargainProduct> Rows);

/// <summary>Ответ симулятора: Decision — fixed (цена фиксированная), counter (встречная цена), accept (договорились), decline (лучшая цена); Text — ответ бота без разметки Telegram.</summary>
public sealed record BotBargainTest(string Decision, decimal? Price, decimal? ListPrice, string Text, bool Button, decimal? Floor);

/// <summary>Решение движка в журнале оферт. Order — номер заказа, если по оферте оформили заказ.</summary>
public sealed record BotBargainOffer(DateTimeOffset? At, string ChatId, bool IsTest, string ProductName, decimal? ListPrice, decimal? ClientOffer, string Decision, decimal? Price, int Step,
    decimal? Floor, decimal? LimitPercent, string? Order);

public sealed record BotBargainOffersPage(int? Count, bool HasNext, IReadOnlyList<BotBargainOffer> Rows);

/// <summary>seller_config.bargain: торг вкл/выкл (нужно ещё seller_enabled), лимит скидки 0…50 %, маржа 0…500 %, ступеней 1…5, категории (id), «звать продавца при лучшей цене».</summary>
public sealed record BotBargainConfig(bool Enabled, decimal MaxDiscountPercent, decimal MinMarginPercent, bool OnlyMarked, int Steps, IReadOnlyList<string> AllowedCategories, bool HandoffOnDecline)
{
    public const decimal MaxDiscountLimit = 50m;
    public const decimal MaxMarginLimit = 500m;
    public const int MinSteps = 1;
    public const int MaxSteps = 5;

    /// <summary>Только изменённые поля блока «Торг» — для частичного PATCH seller_config.bargain.</summary>
    public Dictionary<string, object?> DiffBody(BotBargainConfig before)
    {
        var d = new Dictionary<string, object?>();
        if (Enabled != before.Enabled) d["enabled"] = Enabled;
        if (MaxDiscountPercent != before.MaxDiscountPercent) d["max_discount_percent"] = MaxDiscountPercent;
        if (MinMarginPercent != before.MinMarginPercent) d["min_margin_percent"] = MinMarginPercent;
        if (OnlyMarked != before.OnlyMarked) d["only_marked"] = OnlyMarked;
        if (Steps != before.Steps) d["steps"] = Steps;
        if (!AllowedCategories.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(before.AllowedCategories.OrderBy(x => x, StringComparer.Ordinal))) d["allowed_categories"] = AllowedCategories.ToList();
        if (HandoffOnDecline != before.HandoffOnDecline) d["handoff_on_decline"] = HandoffOnDecline;
        return d;
    }
}

/// <summary>Блок «Торг» отчёта бота (sales-stats → bargain). ConcessionsTotal — сколько уступили от цены в списке. top_products — см. <see cref="BotBargainTop"/>.</summary>
public sealed record BotBargainStats(int Dialogs, int Proposals, int AcceptedByCustomer, int Orders, decimal ConcessionsTotal, double AvgDiscountPercent, double DeclineSharePercent,
    IReadOnlyList<BotBargainTop> TopProducts);

/// <summary>2026-11-27 (ответ Умара 27.11, п. 4): top_products — до 10 элементов {product_id, name, proposals (сколько раз предлагали торг), orders (сколько заказов с этим торгом)}, по убыванию proposals.
/// Число не пришло — null (не 0).</summary>
public sealed record BotBargainTop(string Name, int? Proposals, int? Orders = null, string? ProductId = null);

/// <summary>Реплика бота при торге (test-ai → bargain: {tag, text}); Offer — предложение покупателя, которое ИИ передал движку.</summary>
public sealed record BotBargainNote(string Text, decimal? Offer);

public static class BotBargainText
{
    /// <summary>2026-11-26 (стресс-тест): проверка правил товара до PUT — понятная причина сразу, без похода на сервер: мин. цена 0,01…цена товара и не больше 2 знаков после запятой
    /// (копейки не теряются и не округляются молча), скидка 0…50 %. null — всё в порядке.</summary>
    public static string? CheckRule(decimal? minPrice, decimal? maxDiscountPercent, decimal listPrice)
    {
        if (minPrice is { } m)
        {
            if (m <= 0)
                return Tr.T("Минимальная цена должна быть больше нуля.", "Минималдуу баа нөлдөн чоң болушу керек.", "The minimum price must be above zero.", "En düşük fiyat sıfırdan büyük olmalı.", "Minimal narx noldan katta bo'lishi kerak.");
            if (decimal.Round(m, 2) != m)
                return Tr.T("Минимальная цена — не больше 2 знаков после запятой (тыйыны).", "Минималдуу баа — үтүрдөн кийин 2 белгиден ашпасын (тыйын).", "The minimum price: at most 2 decimal places.", "En düşük fiyat: virgülden sonra en fazla 2 hane.", "Minimal narx — verguldan keyin 2 belgidan oshmasin.");
            if (listPrice > 0 && m > listPrice)
                return Tr.T("Минимальная цена не может быть выше цены товара.", "Минималдуу баа товардын баасынан жогору болбойт.", "The minimum price cannot exceed the product price.", "En düşük fiyat ürün fiyatından yüksek olamaz.", "Minimal narx mahsulot narxidan yuqori bo'lolmaydi.");
        }
        if (maxDiscountPercent is { } d && (d < 0 || d > BotBargainConfig.MaxDiscountLimit))
            return Tr.T("Макс. скидка — от 0 до 50 %.", "Макс. арзандатуу — 0дөн 50 %га чейин.", "Max. discount is 0 to 50 %.", "Maks. indirim %0 ile %50 arasında.", "Maks. chegirma — 0 dan 50 % gacha.");
        return null;
    }

    /// <summary>Ошибка 400 по-русски: «Минимальная цена: Не выше цены товара.» вместо «min_price: …». Сообщения полей сервер присылает по-русски. Не поле формы — как <see cref="ServerTelegramBotApi.DescribeFields"/>.</summary>
    public static string Describe(Exception ex, Func<string, string> label)
    {
        if (ex is ApiException { Payload: { ValueKind: JsonValueKind.Object } payload } && payload.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            var parts = new List<string>();
            foreach (var p in fields.EnumerateObject())
            {
                var msg = p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0 ? p.Value[0].ToString() : p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                if (!string.IsNullOrWhiteSpace(msg))
                    parts.Add(label(p.Name) + ": " + msg);
            }
            if (parts.Count > 0)
                return string.Join("; ", parts);
        }
        return ServerTelegramBotApi.DescribeFields(ex);
    }
}

public sealed partial class ServerTelegramBotApi : IBotBargainApi
{
    public async Task<BotBargainProductsPage> GetBargainProductsAsync(string? search, string? categoryId, bool onlyRules, int page, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string> { ["page"] = page.ToString(CultureInfo.InvariantCulture), ["page_size"] = "30" };
        if (!string.IsNullOrWhiteSpace(search))
            query["search"] = search!.Trim();
        if (!string.IsNullOrWhiteSpace(categoryId))
            query["category"] = categoryId!;
        if (onlyRules)
            query["only_rules"] = "1";
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "bargain/products/", null, query, ct).ConfigureAwait(false);
        return new BotBargainProductsPage(PageCount(data), HasNextPage(data), Rows(data).Select(ParseBargainProduct).ToList());
    }

    public async Task<BotBargainProduct?> SaveBargainProductAsync(string productId, BotBargainRule rule, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["allowed"] = rule.Allowed,
            ["min_price"] = rule.MinPrice?.ToString(CultureInfo.InvariantCulture),
            ["max_discount_percent"] = rule.MaxDiscountPercent?.ToString(CultureInfo.InvariantCulture),
        };
        var data = await _api.RequestAsync(HttpMethod.Put, Root + "bargain/products/" + Uri.EscapeDataString(productId) + "/", body, null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object ? ParseBargainProduct(data) : null;
    }

    public async Task ResetBargainProductAsync(string productId, CancellationToken ct = default) =>
        await _api.RequestAsync(HttpMethod.Delete, Root + "bargain/products/" + Uri.EscapeDataString(productId) + "/", null, null, ct).ConfigureAwait(false);

    public async Task<BotBargainTest> TestBargainAsync(string productId, decimal? offer, bool reset, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["product_id"] = productId };
        if (offer is { } o)
            body["offer"] = o.ToString(CultureInfo.InvariantCulture);
        if (reset)
            body["reset"] = true;
        var d = await _api.RequestAsync(HttpMethod.Post, Root + "bargain/test/", body, null, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        return new BotBargainTest(Str(d, "decision") ?? "", DecN(d, "price"), DecN(d, "list_price"), BotSellerText.Plain(Str(d, "text")), Bool(d, "button"), DecN(d, "floor"));
    }

    public async Task<BotBargainOffersPage> GetBargainOffersAsync(DateTime from, DateTime to, string? decision, int page, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["page_size"] = "30",
        };
        if (!string.IsNullOrWhiteSpace(decision))
            query["decision"] = decision!;
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "bargain/offers/", null, query, ct).ConfigureAwait(false);
        var rows = Rows(data).Select(r =>
        {
            string? order = null;
            if (r.TryGetProperty("order", out var o))
                order = o.ValueKind == JsonValueKind.Object ? Str(o, "number") ?? Str(o, "id") : Str(r, "order");
            return new BotBargainOffer(Date(r, "at"), Str(r, "chat_id") ?? "", Bool(r, "is_test"), Str(r, "product_name") ?? "", DecN(r, "list_price"), DecN(r, "client_offer"), Str(r, "decision") ?? "",
                DecN(r, "price"), Int(r, "step"), DecN(r, "floor"), DecN(r, "limit_percent"), order);
        }).ToList();
        return new BotBargainOffersPage(PageCount(data), HasNextPage(data), rows);
    }

    public async Task<IReadOnlyList<BotCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var list = new List<BotCategory>();
        for (var page = 1; page <= 20; page++)
        {
            var data = await _api.RequestAsync(HttpMethod.Get, "api/main/categories/", null,
                new Dictionary<string, string> { ["page"] = page.ToString(CultureInfo.InvariantCulture), ["page_size"] = "300" }, ct).ConfigureAwait(false);
            list.AddRange(Rows(data).Select(r => new BotCategory(Str(r, "id") ?? "", Str(r, "name") ?? "")).Where(c => c.Id.Length > 0 && c.Name.Length > 0));
            if (!HasNextPage(data))
                break;
        }
        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static BotBargainConfig? ParseBargainConfig(JsonElement seller)
    {
        if (seller.ValueKind != JsonValueKind.Object || !seller.TryGetProperty("bargain", out var b) || b.ValueKind != JsonValueKind.Object)
            return null;
        var cats = new List<string>();
        if (b.TryGetProperty("allowed_categories", out var ac) && ac.ValueKind == JsonValueKind.Array)
            cats.AddRange(ac.EnumerateArray().Select(x => x.ToString()).Where(x => x.Length > 0));
        return new BotBargainConfig(Bool(b, "enabled"), DecN(b, "max_discount_percent") ?? 5m, DecN(b, "min_margin_percent") ?? 10m,
            !b.TryGetProperty("only_marked", out var om) || om.ValueKind != JsonValueKind.False,
            Int(b, "steps") is >= 1 and var steps ? steps : 3, cats, Bool(b, "handoff_on_decline"));
    }

    internal static BotBargainStats? ParseBargainStats(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object || !stats.TryGetProperty("bargain", out var b) || b.ValueKind != JsonValueKind.Object)
            return null;
        var top = new List<BotBargainTop>();
        if (b.TryGetProperty("top_products", out var tp) && tp.ValueKind == JsonValueKind.Array)
            foreach (var t in tp.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String)
                {
                    top.Add(new BotBargainTop(t.GetString() ?? "", null));
                    continue;
                }
                if (t.ValueKind != JsonValueKind.Object)
                    continue;
                var name = Str(t, "name") ?? Str(t, "product_name") ?? Str(t, "title") ?? "";
                // 2026-11-27: боевая форма — proposals и orders; прежние «count»/«offers» (форма не была расписана) — как «предложили торг», если proposals нет.
                var proposals = IntN(t, "proposals") ?? IntN(t, "count") ?? IntN(t, "offers");
                top.Add(new BotBargainTop(name, proposals, IntN(t, "orders"), Str(t, "product_id")));
            }
        return new BotBargainStats(Int(b, "dialogs"), Int(b, "proposals"), Int(b, "accepted_by_customer"), Int(b, "orders"), Dec(b, "concessions_total"), Num(b, "avg_discount_percent"),
            Num(b, "decline_share_percent"), top);
    }

    private static BotBargainProduct ParseBargainProduct(JsonElement r)
    {
        bool? allowed = r.TryGetProperty("allowed", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.ValueKind == JsonValueKind.True : null;
        return new BotBargainProduct(Str(r, "product_id") ?? "", Str(r, "name") ?? "", Dec(r, "price"), DecN(r, "purchase_price"), Str(r, "category_id"), allowed, DecN(r, "min_price"),
            DecN(r, "max_discount_percent"), Bool(r, "effective_allowed"), DecN(r, "floor"));
    }

    /// <summary>Целое от сервера (число или строка «5»); нет поля — null.</summary>
    private static int? IntN(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out var n) ? n : null,
                JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
                _ => null,
            }
            : null;

    private static int? PageCount(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : null;

    private static bool HasNextPage(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String;

    /// <summary>Число от сервера (строка «130.00» или число); нет поля или null — null (не 0: «закупка не задана» ≠ «0 сом»).</summary>
    private static decimal? DecN(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetDecimal(out var n) ? n : null,
                JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) => s,
                _ => null,
            }
            : null;
}
