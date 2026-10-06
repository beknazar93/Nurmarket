using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

public static class CartReceiptTextBuilder
{
    private static int W => ReceiptLayout.CharWidth;

    /// <param name="receiptTime">Время продажи для строк «Дата/Время»; null — сейчас (чек при оплате).</param>
    /// <param name="isReprint">Копия из «Истории чеков» (2026-09-27): внизу отметка
    /// «(повторная печать)», как у копии из «Продаж» (SaleReceiptTextBuilder).</param>
    public static string BuildSimpleReceipt(
        string cartJson,
        string? offlineNote = null,
        string? paymentMethodKey = null,
        string? cashReceived = null,
        DateTime? receiptTime = null,
        bool isReprint = false)
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
            // 2026-09-21, по просьбе владельца: формат как у формального ККМ-чека — заголовок
            // "Контрольно-кассовый чек", явные подписи "Маркет"/"Кассир"/"Дата"/"Время" вместо
            // одной строки "Дата: ...". Кассир — из текущей сессии (PosApp.CurrentUserDisplayName,
            // тот же источник, что уже используют аудит/история списаний).
            var prefs = UserPreferences.Instance;
            var store = prefs.StoreName ?? "MARKET PLUS";

            Line(ReceiptLineLayout.Center("Контрольно-кассовый чек", W));
            Blank();

            if (prefs.ShowStoreName)
                Line($"Маркет - {store}");

            if (prefs.ShowInn && !string.IsNullOrWhiteSpace(prefs.StoreInn))
                Line($"ИНН: {prefs.StoreInn.Trim()}");

            if (prefs.ShowAddress && !string.IsNullOrWhiteSpace(prefs.StoreAddress))
            {
                foreach (var addrLine in ReceiptLineLayout.WrapCenter(prefs.StoreAddress.Trim(), W))
                    Line(addrLine);
            }

            var cashierName = PosApp.CurrentUserDisplayName;
            if (!string.IsNullOrWhiteSpace(cashierName))
                Line($"Кассир - {cashierName.Trim()}");

            // Консультант продажи (сфера «Одежда») — PosCheckoutService кладёт его имя в снимок.
            if (root.TryGetProperty("consultant_display", out var consultant)
                && consultant.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(consultant.GetString()))
                Line($"Консультант - {consultant.GetString()!.Trim()}");

            var receiptNo = TryReceiptNumber(root);
            if (prefs.ShowReceiptNumber && !string.IsNullOrEmpty(receiptNo))
                Line($"Чек №: {PrettyReceiptNumber(receiptNo)}");

            if (prefs.ShowDate)
            {
                var now = receiptTime ?? DateTime.Now;
                Line($"Дата - {now:dd.MM.yyyy}");
                Line($"Время - {now:HH:mm}");
            }

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

            // ─── 2026-10-06, владелец: прокат и долг — крупной надписью и с условиями ───────
            // «в чек при аренде/прокате — с надписью ПРОКАТ: сумма за день, итог, дата от и до, штраф за просрочку;
            // и в долге тоже». Условия проката — RentalReceiptInfoStore, срок долга — снимок (debt_due_date).
            var when = receiptTime ?? DateTime.Now;
            var rentals = CartDisplayHelper.EnumerateItems(root)
                .Select(it => (Info: RentalReceiptInfoStore.FromLineName(CartDisplayHelper.ItemName(it), when), Total: LineTotalDouble(it)))
                .Where(x => x.Info != null)
                // Условий на этой кассе нет (прокат оформлен на другой) — итог из строки чека, цена за сутки — итог / сутки.
                .Select(x => x.Info!.Total > 0.004 || x.Total <= 0.004
                    ? x.Info!
                    : x.Info! with { Total = x.Total, PricePerDay = x.Info!.Days > 0 ? Math.Round(x.Total / x.Info!.Days, 2) : 0 })
                .GroupBy(x => x.Number)
                .Select(g => g.First())
                .ToList();
            foreach (var rental in rentals)
                AppendRentalBlock(sb, rental);
            var isDebtSale = (paymentMethodKey ?? "").Trim().StartsWith("debt", StringComparison.OrdinalIgnoreCase);
            if (isDebtSale)
            {
                Line(ReceiptLineLayout.Center("*** ПРОДАЖА В ДОЛГ ***", W));
                Blank();
            }

            // ─── Items ────────────────────────────────────────────────
            if (prefs.ShowItems)
            {
                Line("Товарный чек:");
                Blank();
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

                    // 2026-10-02: вариант по акции — обычная цена и сумма скидки (цену считает сервер).
                    if (CartDisplayHelper.VariantBasePrice(it) is { } promoBase)
                        AppendStackedAmountLine(sb, $"АКЦИЯ, было {FormatMoney(promoBase)}:", "-" + FormatMoney((promoBase - unitPrice) * qty));

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
            }

            // ─── Итоги и оплата (внизу чека, перед «Спасибо») ─────────
            var totals = CartTotalsCalculator.Calculate(root);
            if (totals.LineDiscounts > 1e-6)
                AppendStackedAmountLine(sb, "СКИДКА ПОЗИЦИЙ:", "-" + FormatMoney(totals.LineDiscounts));
            if (totals.OrderDiscount > 1e-6)
                AppendStackedAmountLine(sb, "СКИДКА НА ЧЕК:", "-" + FormatMoney(totals.OrderDiscount));
            if (totals.LineDiscounts > 1e-6 || totals.OrderDiscount > 1e-6)
                AppendStackedAmountLine(sb, "ПРОМЕЖУТОЧНЫЙ ИТОГ:", FormatMoney(totals.Subtotal));

            var pm = (paymentMethodKey ?? "").Trim().ToLowerInvariant();
            // 2026-10-04, клиент: предоплата долга — как её принял кассир (наличными/безналом) — видна и в чеке.
            // PosCheckoutService передаёт «debt-noncash» / «debt-cash», для остального это обычный «debt».
            string? debtPrepaymentLabel = pm switch
            {
                "debt-noncash" => "ВНЕСЕНО БЕЗНАЛОМ:",
                "debt-cash" => "ВНЕСЕНО НАЛИЧНЫМИ:",
                _ => null,
            };
            if (pm.StartsWith("debt-", StringComparison.Ordinal))
                pm = "debt";
            var paymentMethodLine = FormatPaymentMethodLine(pm);
            if (paymentMethodLine != null)
                Line(paymentMethodLine);

            var cash = ParseMoneyOrNull(cashReceived);
            if (pm is "mixed" && cash.HasValue)
            {
                // 2026-09-25: у смешанной оплаты cashReceived — только наличная часть; раньше она
                // печаталась как «ВНЕСЕНО», и по чеку нельзя было понять, где безнал.
                AppendStackedAmountLine(sb, "НАЛИЧНЫМИ:", FormatMoney(cash.Value));
                AppendStackedAmountLine(sb, "БЕЗНАЛИЧНЫМИ:", FormatMoney(Math.Max(0, totals.TotalDue - cash.Value)));
            }
            else if (pm is "debt")
            {
                if (cash.HasValue)
                    AppendStackedAmountLine(sb, cash.Value > 0.005 && debtPrepaymentLabel != null ? debtPrepaymentLabel : "ВНЕСЕНО:", FormatMoney(cash.Value));
                AppendStackedAmountLine(sb, "В ДОЛГ:", FormatMoney(Math.Max(0, totals.TotalDue - (cash ?? 0))));
                // 2026-10-06: дата долга, срок возврата (названный клиентом) и штраф за просрочку.
                Line($"Дата долга: {when:dd.MM.yyyy}");
                var schedule = root.TryGetProperty("debt_schedule", out var ds) && ds.ValueKind == JsonValueKind.Object
                               && ds.TryGetProperty("payments", out var dp) && dp.ValueKind == JsonValueKind.Array ? dp : default;
                if (schedule.ValueKind == JsonValueKind.Array && schedule.GetArrayLength() > 1)
                {
                    // Рассрочка: каждый платёж — дата и сумма.
                    var months = ds.TryGetProperty("unit", out var unit) && unit.GetString() == "month";
                    var interval = ds.TryGetProperty("interval", out var iv) && iv.TryGetInt32(out var ivn) ? ivn : 1;
                    var every = months
                        ? interval == 1 ? "каждый месяц" : $"каждые {interval} мес."
                        : interval switch { 1 => "каждый день", 2 => "через день", 7 => "раз в неделю", _ => $"каждые {interval} дн." };
                    var count = schedule.GetArrayLength();
                    var word = count % 10 == 1 && count % 100 != 11 ? "платёж"
                        : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "платежа" : "платежей";
                    foreach (var part in WrapText($"Рассрочка: {count} {word}, {every}", W))
                        Line(part);
                    foreach (var pay in schedule.EnumerateArray())
                    {
                        var no = pay.TryGetProperty("number", out var nEl) ? nEl.ToString() : "";
                        var date = pay.TryGetProperty("due_date", out var dEl) && DateTime.TryParseExact(dEl.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd)
                            ? dd.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
                        var amount = pay.TryGetProperty("amount", out var aEl) && double.TryParse(aEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var av) ? av : 0;
                        AppendStackedAmountLine(sb, $"{no}) {date}", FormatMoney(amount));
                    }
                }
                else if (TryDebtDueDate(root) is { } dueDate)
                    Line($"Вернуть до: {dueDate:dd.MM.yyyy} ({Math.Max(0, (dueDate.Date - when.Date).Days)} дн.)");
                if (UserPreferences.Instance.DebtLatePenaltyText is { Length: > 0 } debtPenalty)
                    foreach (var part in WrapText("Штраф за просрочку: " + debtPenalty.Trim(), W))
                        Line(part);
            }
            else if (pm.Length > 0 && cash.HasValue)
            {
                AppendStackedAmountLine(sb, "ВНЕСЕНО:", FormatMoney(cash.Value));
                if (pm is "cash")
                {
                    var change = CartTotalsCalculator.CalculateChange(cash.Value, totals.TotalDue);
                    AppendStackedAmountLine(sb, "СДАЧА:", FormatMoney(change));
                }
            }

            if (prefs.ShowTotal)
            {
                Blank();
                Line(ReceiptLineLayout.FormatLabelAmount(
                    "ИТОГО:",
                    ReceiptLineLayout.WithSom(FormatMoney(totals.TotalDue)),
                    W));
            }

            Blank();
            Dash();
            Blank();

            if (isReprint)
            {
                // Копия отличается от оригинала на бумаге: пометка и когда её напечатали (2026-09-28).
                Line(ReceiptLineLayout.Center("ПОВТОРНАЯ ПЕЧАТЬ", W));
                Line(ReceiptLineLayout.Center(DateTime.Now.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture), W));
                Blank();
            }

            // ─── Footer ────────────────────────────────────────────────
            Line(ReceiptLineLayout.Center("Спасибо за покупку! :)", W));
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

    /// <summary>Строка «Способ оплаты» — общая для чека при продаже и чека из «Продаж».
    /// 2026-09-25: смешанная и «в долг» строки не получали вовсе.</summary>
    internal static string? FormatPaymentMethodLine(string paymentMethodKey) =>
        paymentMethodKey switch
        {
            "cash" => "Способ оплаты: Наличными",
            "transfer" or "card" or "mbank" or "bakai" => "Способ оплаты: Безналичными",
            "mixed" => "Способ оплаты: Смешанная",
            "debt" => "Способ оплаты: В долг",
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

    /// <summary>Блок «ПРОКАТ» в начале чека: номер, даты с/по, цена за сутки, итог, залог, штраф за просрочку.</summary>
    private static void AppendRentalBlock(StringBuilder sb, RentalReceiptInfo rental)
    {
        void Line(string s = "") { sb.Append(s); sb.Append('\n'); }
        Line(ReceiptLineLayout.Center("*** ПРОКАТ ***", W));
        Line($"Прокат №{rental.Number}");
        if (rental.To != default)
        {
            Line($"С:  {rental.From:dd.MM.yyyy}");
            Line($"По: {rental.To:dd.MM.yyyy}" + (rental.Days > 0 ? $" ({rental.Days} сут.)" : ""));
        }
        if (rental.PricePerDay > 0.004)
            AppendStackedAmountLine(sb, "Цена за сутки:", FormatMoney(rental.PricePerDay));
        if (rental.Total > 0.004)
            AppendStackedAmountLine(sb, "За прокат:", FormatMoney(rental.Total));
        if (rental.DepositAmount > 0.004)
            AppendStackedAmountLine(sb, "Залог:", FormatMoney(rental.DepositAmount));
        else if (!string.IsNullOrWhiteSpace(rental.DepositDocument))
            foreach (var part in WrapText("Залог: документ (" + rental.DepositDocument.Trim() + ")", W))
                Line(part);
        if (rental.LatePenaltyPerDay > 0.004)
            foreach (var part in WrapText($"Штраф за просрочку: {FormatMoney(rental.LatePenaltyPerDay)} сом за каждые сутки", W))
                Line(part);
        if (rental.To != default)
            Line($"Вернуть вещь до {rental.To:dd.MM.yyyy}");
        Line(new string('-', W));
        Line();
    }

    /// <summary>Срок возврата долга из снимка чека (PosCheckoutService кладёт debt_due_date).</summary>
    private static DateTime? TryDebtDueDate(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("debt_due_date", out var v) && v.ValueKind == JsonValueKind.String
        && DateTime.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

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
        // sale_id/order_id/pos_sale_id намеренно не входят в список — это GUID сервера,
        // а не читаемый номер чека, печатать их на чеке не нужно (см. такое же решение в SalesWindow).
        foreach (var key in new[]
                 {
                     "receipt_number", "receipt_no", "check_number", "check_no", "sale_number", "number", "seq",
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
        // 2026-09-28, продажа №1136: скидка строки на чеке — та же, что в итоге и в запросе на
        // сервер (скидка кассира в процентах или акция товара NurCRM раньше сюда не попадали).
        if (it.ValueKind == JsonValueKind.Object)
            return CartDisplayHelper.EffectiveLineDiscount(it);

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
