using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

public static class CartReceiptTextBuilder
{
    private static int W => ReceiptLayout.CharWidth;

    public static string BuildSimpleReceipt(
        string cartJson,
        string? offlineNote = null,
        string? paymentMethodKey = null,
        string? cashReceived = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            var root = doc.RootElement;
            var sb = new StringBuilder(900);

            void Line(string s = "") { sb.Append(s); sb.Append('\n'); }
            void Dash() => Line(new string('-', W));
            void Blank() => Line();

            // ─── Header ───────────────────────────────────────────────
            var prefs = UserPreferences.Instance;
            var store = prefs.StoreName ?? "MARKET PLUS";

            Line(ReceiptLineLayout.Center(store, W));

            if (prefs.ShowInn && !string.IsNullOrWhiteSpace(prefs.StoreInn))
                Line(ReceiptLineLayout.Center($"ИНН: {prefs.StoreInn.Trim()}", W));

            if (prefs.ShowAddress && !string.IsNullOrWhiteSpace(prefs.StoreAddress))
            {
                foreach (var addrLine in ReceiptLineLayout.WrapCenter(prefs.StoreAddress.Trim(), W))
                    Line(addrLine);
            }

            Blank();

            var receiptNo = TryReceiptNumber(root);
            if (!string.IsNullOrEmpty(receiptNo))
                Line($"Чек №: {PrettyReceiptNumber(receiptNo)}");

            var now = DateTime.Now;
            Line($"Дата: {now:dd.MM.yyyy HH:mm}");

            if (!string.IsNullOrWhiteSpace(offlineNote))
            {
                Blank();
                Dash();
                Line(ReceiptLineLayout.Center("!! " + offlineNote.Trim() + " !!", W));
                Dash();
            }
            else
            {
                Dash();
            }
            Blank();

            // ─── Items ────────────────────────────────────────────────
            int itemIndex = 0;
            foreach (var it in CartDisplayHelper.EnumerateItems(root))
            {
                itemIndex++;
                var name = CartDisplayHelper.ItemName(it).Trim();
                var qty = CartDisplayHelper.LineQuantity(it);
                var unitPrice = CartDisplayHelper.UnitPrice(it);
                var lineTotal = LineTotalDouble(it);
                var isWeight = CartDisplayHelper.LineMustWeigh(it);

                var qtyStr = isWeight ? FormatQty(qty) : qty.ToString("0");
                var unitStr = FormatMoney(unitPrice);
                var totalStr = FormatMoney(lineTotal);

                foreach (var line in WrapText(name, W))
                    Line(line);

                foreach (var itemLine in ReceiptLineLayout.FormatItemBlock($"{qtyStr} x {unitStr}", totalStr, W))
                    Line(itemLine);

                Blank();

                var disc = TryLineDiscount(it);
                if (disc > 1e-6)
                    AppendStackedAmountLine(sb, "СКИДКА:", "-" + FormatMoney(disc));
            }

            if (itemIndex == 0)
                Line(ReceiptLineLayout.Center("(НЕТ ПОЗИЦИЙ)", W));

            Blank();
            Dash();
            Blank();

            // ─── Итоги и оплата (внизу чека, перед «Спасибо») ─────────
            var totals = CartTotalsCalculator.Calculate(root);
            if (totals.LineDiscounts > 1e-6)
                AppendStackedAmountLine(sb, "СКИДКА ПОЗИЦИЙ:", "-" + FormatMoney(totals.LineDiscounts));
            if (totals.OrderDiscount > 1e-6)
                AppendStackedAmountLine(sb, "СКИДКА НА ЧЕК:", "-" + FormatMoney(totals.OrderDiscount));
            if (totals.LineDiscounts > 1e-6 || totals.OrderDiscount > 1e-6)
                AppendStackedAmountLine(sb, "ПРОМЕЖУТОЧНЫЙ ИТОГ:", FormatMoney(totals.Subtotal));

            var pm = (paymentMethodKey ?? "").Trim().ToLowerInvariant();
            var paymentMethodLine = FormatPaymentMethodLine(pm);
            if (paymentMethodLine != null)
                Line(paymentMethodLine);

            var cash = ParseMoneyOrNull(cashReceived);
            if (pm.Length > 0 && cash.HasValue)
            {
                AppendStackedAmountLine(sb, "ВНЕСЕНО:", FormatMoney(cash.Value));
                if (pm is "cash")
                {
                    var change = CartTotalsCalculator.CalculateChange(cash.Value, totals.TotalDue);
                    AppendStackedAmountLine(sb, "СДАЧА:", FormatMoney(change));
                }
            }

            Blank();
            Line(ReceiptLineLayout.FormatLabelAmount(
                "ИТОГО:",
                ReceiptLineLayout.WithSom(FormatMoney(totals.TotalDue)),
                W));

            Blank();
            Dash();
            Blank();

            // ─── Footer ────────────────────────────────────────────────
            Line(ReceiptLineLayout.Center("Спасибо за покупку!", W));
            Blank();
            Blank();
            Blank();

            return sb.ToString().ToUpperInvariant();
        }
        catch
        {
            return "NUR MARKET\n\n(НЕ УДАЛОСЬ РАЗОБРАТЬ КОРЗИНУ)\n\n".ToUpperInvariant();
        }
    }

    private static string? FormatPaymentMethodLine(string paymentMethodKey) =>
        paymentMethodKey switch
        {
            "cash" => "Способ оплаты: Наличными",
            "transfer" or "card" => "Способ оплаты: Безналичными",
            _ => null,
        };

    private static string PrettyReceiptNumber(string raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0)
            return t;
        if (t.All(char.IsAsciiDigit) && ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var u))
            return u.ToString("D6", CultureInfo.InvariantCulture);
        return t;
    }

    private static double LineTotalDouble(JsonElement it)
    {
        foreach (var key in new[]
                 {
                     "line_total", "line_total_amount", "line_amount", "amount",
                     "total", "sum", "total_price", "subtotal", "line_sum",
                 })
        {
            if (it.TryGetProperty(key, out var v))
            {
                var d = v.ValueKind switch
                {
                    JsonValueKind.Number => v.TryGetDouble(out var x) ? x : (double?)null,
                    JsonValueKind.String => double.TryParse(
                        v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : null,
                    _ => null,
                };
                if (d.HasValue) return d.Value;
            }
        }

        var qty = CartDisplayHelper.LineQuantity(it);
        var up = CartDisplayHelper.UnitPrice(it);
        return qty * up;
    }

    private static string FormatMoney(double v) =>
        v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatQty(double qty)
    {
        if (qty == Math.Floor(qty) && qty >= 0 && qty < 1_000_000)
            return ((long)qty).ToString(CultureInfo.InvariantCulture);
        return qty.ToString("0.000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    private static IEnumerable<string> WrapText(string text, int width)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        while (text.Length > width)
        {
            int wrap = text.LastIndexOf(' ', width);

            if (wrap <= 0)
                wrap = width;

            yield return text[..wrap].TrimEnd();
            text = text[wrap..].TrimStart();
        }

        if (text.Length > 0)
            yield return text;
    }

    private static void AppendStackedAmountLine(StringBuilder sb, string label, string amount)
    {
        var line = ReceiptLineLayout.FormatLabelAmount(label, ReceiptLineLayout.WithSom(amount), W);
        sb.Append(line);
        sb.Append('\n');
    }

    private static double? ParseMoneyOrNull(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0)
            return null;
        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : null;
    }

    private static string? TryReceiptNumber(JsonElement cart)
    {
        if (cart.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in new[]
                 {
                     "receipt_number", "receipt_no", "check_number", "check_no", "sale_number", "number", "seq",
                     "sale_id", "order_id", "pos_sale_id",
                 })
        {
            if (cart.TryGetProperty(key, out var v))
            {
                var s = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null,
                };
                s = (s ?? "").Trim();
                if (s.Length > 0)
                    return s;
            }
        }

        return null;
    }

    private static double TryLineDiscount(JsonElement it)
    {
        foreach (var key in new[] { "discount_total", "line_discount", "discount" })
        {
            if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d))
                    return d;
                if (v.ValueKind == JsonValueKind.String &&
                    double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x))
                    return x;
            }
        }
        return 0;
    }
}
