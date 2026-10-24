using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-23, владелец: «чек красивым как в мобилке сделай» — данные для карточки чека в «Продажи и финансы → Чеки»
/// (вместо моноширинного текста). Берёт те же поля, что <see cref="SaleReceiptTextBuilder"/> (тот же ответ сервера, то же
/// правило «Сумма/Скидка», тот же журнал долга), новых запросов нет. Текст для принтера строит прежний построитель — здесь
/// только то, что видно на экране.</summary>
public sealed class SaleReceiptCardData
{
    public enum StatusKind { Paid, Debt, Return }

    public sealed record Line(string Name, string QtyPrice, string Total, string? Discount);

    /// <summary>Строка блока оплаты. Accent: 0 — обычная, 1 — предупреждающий цвет (долг), 2 — цвет скидки.</summary>
    public sealed record Row(string Label, string Value, int Accent = 0);

    public string Shop { get; set; } = "";
    public string Inn { get; set; } = "";
    public string Address { get; set; } = "";
    public string Number { get; set; } = "";
    public string DateTimeText { get; set; } = "";
    public string Cashier { get; set; } = "";
    public string Consultant { get; set; } = "";
    public string Client { get; set; } = "";
    public StatusKind Status { get; set; } = StatusKind.Paid;
    public string StatusText { get; set; } = "";
    public List<Line> Lines { get; } = new();
    /// <summary>Сумма и скидка (до итога); пусто, если цифры не сходятся — как в текстовом чеке.</summary>
    public List<Row> BeforeTotal { get; } = new();
    public string TotalLabel { get; set; } = "";
    public string TotalText { get; set; } = "";
    /// <summary>Способ оплаты, внесено, сдача, оплачено, в долг, вернуть до, остаток долга.</summary>
    public List<Row> Payment { get; } = new();

    private static string N2(decimal v) => v.ToString("N2", CultureInfo.CurrentCulture);
    private static string Som(decimal v) => N2(v) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    public static SaleReceiptCardData Build(JsonElement sale, string? receiptNumber, decimal discount, decimal? total)
    {
        var prefs = UserPreferences.Instance;
        var d = new SaleReceiptCardData
        {
            Shop = prefs.ShowStoreName && !string.IsNullOrWhiteSpace(prefs.StoreName) ? prefs.StoreName.Trim() : "NurMarket",
            Inn = prefs.ShowInn && !string.IsNullOrWhiteSpace(prefs.StoreInn) ? prefs.StoreInn.Trim() : "",
            Address = prefs.ShowAddress && !string.IsNullOrWhiteSpace(prefs.StoreAddress) ? prefs.StoreAddress.Trim() : "",
            Number = (receiptNumber ?? "").Trim(),
            Cashier = (CartDisplayHelper.TryCashierName(sale) ?? PosApp.CurrentUserDisplayName ?? "").Trim(),
            Consultant = Str(sale, "consultant_display") ?? "",
            Client = Str(sale, "client_name") ?? "",
        };
        if (prefs.ShowDate && Str(sale, "created_at") is { } createdText
            && DateTime.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
            d.DateTimeText = created.ToString("dd.MM.yyyy  HH:mm", CultureInfo.InvariantCulture);

        decimal linesTotal = 0;
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var qty = CartDisplayHelper.LineQuantity(line);
            var price = (decimal)CartDisplayHelper.UnitPrice(line);
            if (!decimal.TryParse(CartDisplayHelper.LineTotal(line), NumberStyles.Any, CultureInfo.InvariantCulture, out var sum))
                sum = (decimal)qty * price;
            linesTotal += sum;
            d.Lines.Add(new Line(CartDisplayHelper.ItemName(line),
                $"{qty.ToString("0.###", CultureInfo.InvariantCulture)} × {N2(price)}", N2(sum), null));
        }

        var due = total ?? linesTotal - discount;
        if (discount > 0.005m)
        {
            decimal? subtotal = null;
            if (Math.Abs(linesTotal - discount - due) < 0.01m) subtotal = linesTotal;
            else if (Math.Abs(linesTotal - due) < 0.01m) subtotal = due + discount;
            if (subtotal is { } sub)
            {
                d.BeforeTotal.Add(new Row(Tr.T("Сумма", "Сумма", "Subtotal", "Tutar", "Summa"), N2(sub)));
                d.BeforeTotal.Add(new Row(Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"), "−" + N2(discount), 2));
            }
        }
        d.TotalLabel = Tr.T("ИТОГО", "ЖАЛПЫ", "TOTAL", "TOPLAM", "JAMI");
        d.TotalText = Som(due);

        var sale0 = (Str(sale, "status") ?? "").ToLowerInvariant();
        var method = (Str(sale, "payment_method") ?? "").ToLowerInvariant();
        var methodText = method switch
        {
            "cash" => Tr.T("Наличными", "Накталай", "Cash", "Nakit", "Naqd"),
            "transfer" or "card" or "mbank" or "bakai" => Tr.T("Безналичными", "Накталайсыз", "Non-cash", "Nakitsiz", "Naqdsiz"),
            "mixed" => Tr.T("Смешанная", "Аралаш", "Mixed", "Karışık", "Aralash"),
            "debt" => Tr.T("В долг", "Карызга", "On credit", "Borca", "Qarzga"),
            _ => "",
        };
        if (methodText.Length > 0)
            d.Payment.Add(new Row(Tr.T("Способ оплаты", "Төлөм ыкмасы", "Payment method", "Ödeme yöntemi", "To'lov usuli"), methodText));

        var received = Money(sale, "cash_received");
        var debtLeft = 0m;
        decimal? debtNow = null;
        if (method == "cash" && received is > 0)
        {
            d.Payment.Add(new Row(Tr.T("Внесено", "Киргизилди", "Received", "Alınan", "Kiritildi"), Som(received.Value)));
            var change = Money(sale, "change") ?? Math.Max(0, received.Value - due);
            d.Payment.Add(new Row(Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim"), Som(change)));
        }
        if (method == "debt")
        {
            var ledger = SaleReturnLedger.Get(Str(sale, "id"));
            var paidNow = ledger is { Total: > 0m } ? ledger.PaidCash + ledger.PaidNonCash : received ?? 0m;
            if (paidNow > 0.005m)
                d.Payment.Add(new Row(Tr.T("Оплачено", "Төлөндү", "Paid", "Ödendi", "To'landi"), Som(paidNow)));
            debtLeft = ledger is { Total: > 0m } ? ledger.Debt : Math.Max(0m, due - paidNow);
            d.Payment.Add(new Row(Tr.T("В долг", "Карызга", "On credit", "Borca", "Qarzga"), Som(debtLeft), 1));
            if (ledger is { DebtSchedule.Count: > 1 })
            {
                d.Payment.Add(new Row(Tr.T("Рассрочка", "Бөлүп төлөө", "Instalments", "Taksit", "Bo'lib to'lash"),
                    ledger.DebtSchedule.Count + " " + Tr.T("плат.", "төл.", "pmts", "ödeme", "to'lov")));
                var no = 0;
                foreach (var p in ledger.DebtSchedule)
                    d.Payment.Add(new Row($"{++no}) {p.Date:dd.MM.yyyy}", N2(p.Amount)));
            }
            else if (ledger?.DebtDueDate is { } dueDate)
                d.Payment.Add(new Row(Tr.T("Вернуть до", "Кайтаруу мөөнөтү", "Pay by", "Son ödeme", "Qaytarish muddati"), dueDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), 1));
            if (Money(sale, "remaining_debt") is { } remaining)
            {
                debtNow = remaining;
                d.Payment.Add(new Row(Tr.T("Остаток долга сейчас", "Азыркы карыз калдыгы", "Debt balance now", "Şu anki borç bakiyesi", "Hozirgi qarz qoldig'i"), Som(remaining), 1));
            }
            if (ledger?.ClientPhone is { Length: > 0 } phone && CartReceiptTextBuilder.MaskPhone(phone) is { Length: > 0 } masked)
                d.Payment.Add(new Row(Tr.T("Телефон клиента", "Кардардын телефону", "Client phone", "Müşteri telefonu", "Mijoz telefoni"), masked));
            if (d.Client.Length == 0 && !string.IsNullOrWhiteSpace(ledger?.ClientName))
                d.Client = ledger!.ClientName!.Trim();
        }

        // Плашка: возврат — серая, долг (остался неоплаченный) — оранжевая, иначе «Оплачен» — зелёная.
        if (sale0.Contains("return") || sale0.Contains("refund"))
        {
            d.Status = StatusKind.Return;
            d.StatusText = Tr.T("Возврат", "Кайтарым", "Return", "İade", "Qaytarish");
        }
        else if (method == "debt" && debtLeft > 0.005m && debtNow is { } dn && dn < 0.005m)
        {
            // 2026-10-23: долг был, но уже погашен — зелёная плашка вместо «Долг».
            d.Status = StatusKind.Paid;
            d.StatusText = Tr.T("Долг погашен", "Карыз төлөндү", "Debt repaid", "Borç ödendi", "Qarz to'landi");
        }
        else if (sale0.Contains("debt") || (method == "debt" && debtLeft > 0.005m))
        {
            d.Status = StatusKind.Debt;
            d.StatusText = Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz");
        }
        else
        {
            d.Status = StatusKind.Paid;
            d.StatusText = Tr.T("Оплачен", "Төлөндү", "Paid", "Ödendi", "To'landi");
        }
        return d;
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static decimal? Money(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        var text = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : null;
    }
}
