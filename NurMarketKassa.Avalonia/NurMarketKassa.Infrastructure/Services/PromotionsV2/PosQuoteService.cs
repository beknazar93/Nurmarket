using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-27 (ответ Умара «pos/quote/», в бою 27.11): POST api/main/pos/quote/ — предпросмотр чека тем же телом, что pos/checkout/, без оплаты; ничего не пишет.
/// <c>total</c> — ровно то число, которое сервер сверит с expected_total. Касса спрашивает quote при изменении чека (с задержкой ~300 мс, прежний запрос
/// отменяется) и сверяет с локальным итогом: расхождение пишется в журнал, а на экран ложится итог СЕРВЕРА (PromoV2Cart.ApplyServerLines), не молча.
/// Позиции идут без склейки одинаковых строк, чтобы index ответа совпадал со строкой чека. Без признака pos_quote — ни одного запроса.
/// </summary>
public static class PosQuoteService
{
    public sealed class Quote
    {
        public decimal Total { get; init; }
        public JsonElement Lines { get; init; }
        public JsonElement Hints { get; init; }
        public JsonElement Warnings { get; init; }
        public string? RulesVersion { get; init; }
    }

    /// <summary>Только для проверок: свой «сервер» вместо HTTP.</summary>
    public static Func<JsonObject, CancellationToken, Task<JsonElement>>? SendOverride { get; set; }

    public static bool Enabled => ServerCapabilities.Has(ServerCapabilities.PosQuote);

    /// <summary>Тело quote из снимка чека: как pos/checkout/ (клиент, скидка на чек, филиал), позиции без склейки, без payment/expected_total. null — чек этим путём не считается.</summary>
    public static JsonObject? BuildBody(string cartJson, string? clientId, DateTimeOffset? at)
    {
        var body = QuickCheckoutBody.TryBuild(cartJson, "cash", null, null, clientId, null, null, false, null, false, out _);
        if (body is null)
            return null;
        using var doc = JsonDocument.Parse(cartJson);
        if (QuickCheckoutBody.TryBuildUnmerged(doc.RootElement, out _) is not { } items)
            return null;
        body["items"] = items;
        body.Remove("payment");
        body.Remove("print_receipt");
        body.Remove(QuickCheckoutBody.ExpectedTotalField);
        body.Remove("shift");
        if (at is { } when)
            body["at"] = when.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        return body;
    }

    public static async Task<Quote?> QuoteAsync(string cartJson, string? clientId = null, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        if (!Enabled && SendOverride is null)
            return null;
        var body = BuildBody(cartJson, clientId, at);
        if (body is null)
            return null;
        JsonElement d;
        if (SendOverride is { } send)
            d = await send(body, ct).ConfigureAwait(false);
        else
        {
            if (ServerLoyalty.ApiProvider?.Invoke() is not { } api || string.IsNullOrEmpty(api.AccessToken))
                return null;
            d = await api.RequestAsync(HttpMethod.Post, "api/main/pos/quote/", body, null, ct, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        return Parse(d);
    }

    public static Quote? Parse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !TryDec(d, "total", out var total))
            return null;
        return new Quote
        {
            Total = total,
            Lines = d.TryGetProperty("lines", out var l) ? l.Clone() : default,
            Hints = d.TryGetProperty("hints", out var h) ? h.Clone() : default,
            Warnings = d.TryGetProperty("warnings", out var w) ? w.Clone() : default,
            RulesVersion = d.TryGetProperty("rules_version", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString() : null,
        };
    }

    /// <summary>409 total_mismatch (pos/checkout/ с expected_total): итог сервера отличается — продажа НЕ создана.</summary>
    public static bool IsTotalMismatch(ApiException ex) =>
        ex.StatusCode == 409 && ex.Payload is { ValueKind: JsonValueKind.Object } p
        && p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == "total_mismatch";

    /// <summary>Текст для кассира: «Цены или акции изменились: было X, стало Y» + до 5 строк с новыми суммами.</summary>
    public static string MismatchText(JsonElement payload, string? cartJson = null)
    {
        TryDec(payload, "server_total", out var server);
        TryDec(payload, "expected_total", out var expected);
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(cartJson))
        {
            using var doc = JsonDocument.Parse(cartJson);
            names = CartDisplayHelper.EnumerateItems(doc.RootElement).Select(CartDisplayHelper.ItemName).ToList();
        }
        var rows = new List<string>();
        if (payload.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
            foreach (var l in lines.EnumerateArray().Take(5))
            {
                var idx = l.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : -1;
                var name = l.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : idx >= 0 && idx < names.Count ? names[idx] : null;
                TryDec(l, "amount", out var amount);
                if (amount == 0)
                    TryDec(l, "line_total", out amount);
                TryDec(l, "discount", out var disc);
                rows.Add($"• {name ?? "—"}: {MoneyText.Amount(amount)}{(disc > 0 ? " (−" + MoneyText.Amount(disc) + ")" : "")}");
            }
        var list = rows.Count > 0 ? "\n" + string.Join("\n", rows) : "";
        return Tr.T(
            $"Цены или акции изменились: было {MoneyText.Amount(expected)}, стало {MoneyText.Amount(server)}. Продажа НЕ проведена — чек пересчитан, проверьте итог и оплатите ещё раз.{list}",
            $"Баалар же акциялар өзгөрдү: {MoneyText.Amount(expected)} болчу, {MoneyText.Amount(server)} болду. Сатуу ӨТКӨРҮЛГӨН ЖОК — чек кайра эсептелди, жыйынтыкты текшерип, кайра төлөңүз.{list}",
            $"Prices or promotions changed: was {MoneyText.Amount(expected)}, now {MoneyText.Amount(server)}. The sale was NOT recorded — the receipt was recalculated, check the total and pay again.{list}",
            $"Fiyatlar veya kampanyalar değişti: {MoneyText.Amount(expected)} idi, {MoneyText.Amount(server)} oldu. Satış KAYDEDİLMEDİ — fiş yeniden hesaplandı, toplamı kontrol edip tekrar ödeyin.{list}",
            $"Narxlar yoki aksiyalar o'zgardi: {MoneyText.Amount(expected)} edi, {MoneyText.Amount(server)} bo'ldi. Sotuv O'TKAZILMADI — chek qayta hisoblandi, jami summani tekshirib, yana to'lang.{list}");
    }

    /// <summary>Расхождение quote и кассы — короткая строка над корзиной (итог сервера главный).</summary>
    public static string ServerTotalNote(decimal server, decimal local) => Tr.T(
        $"Итог по данным сервера: {MoneyText.Amount(server)} (касса считала {MoneyText.Amount(local)}) — акции или цены изменились.",
        $"Сервердин маалыматы боюнча жыйынтык: {MoneyText.Amount(server)} (касса {MoneyText.Amount(local)} эсептеген) — акциялар же баалар өзгөрдү.",
        $"Total according to the server: {MoneyText.Amount(server)} (the till had {MoneyText.Amount(local)}) — promotions or prices changed.",
        $"Sunucuya göre toplam: {MoneyText.Amount(server)} (kasa {MoneyText.Amount(local)} hesaplamıştı) — kampanyalar veya fiyatlar değişti.",
        $"Server ma'lumotiga ko'ra jami: {MoneyText.Amount(server)} (kassa {MoneyText.Amount(local)} hisoblagan) — aksiyalar yoki narxlar o'zgardi.");

    private static bool TryDec(JsonElement o, string name, out decimal value)
    {
        value = 0m;
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v))
            return false;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetDecimal(out value);
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
