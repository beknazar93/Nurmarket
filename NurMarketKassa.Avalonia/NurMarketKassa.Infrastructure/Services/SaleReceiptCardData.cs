using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-23, владелец: «чек красивым как в мобилке сделай» — данные для карточки чека в «Продажи и финансы → Чеки»
/// (вместо моноширинного текста). Берёт те же поля, что <see cref="SaleReceiptTextBuilder"/> (тот же ответ сервера, то же
/// правило «Сумма/Скидка», тот же журнал долга), новых запросов нет. Текст для принтера строит прежний построитель — здесь
/// только то, что видно на экране.
///
/// 2026-10-27, владелец (дважды): «вид чека в десктопе как в мобилке — дизайн, отображение чека». Модель стала ОБЩЕЙ для всех
/// мест, где чек виден на экране (история чеков кассы, «Продажи и финансы → Чеки», чек клиента, «Чек» после оплаты, предпросмотр):
/// порядок блоков как в ReceiptView мобилки (шапка магазина → плашка-вид документа → «Чек №», смена, дата, кассир, касса,
/// покупатель → позиции «1. Название / 2 шт × 85 — 170» → «Сумма без скидки / Скидка» → ИТОГО → оплата, внесено, сдача, долг и
/// «Вернуть до» → QR → «Спасибо за покупку!»). Печатная лента и деньги не затронуты: это только вид.</summary>
public sealed class SaleReceiptCardData
{
    public enum StatusKind { Paid, Debt, Return }

    public sealed record Line(string Name, string QtyPrice, string Total, string? Discount, string? Returned = null);

    /// <summary>Строка блока оплаты. Accent: 0 — обычная, 1 — предупреждающий цвет (долг), 2 — цвет скидки, 3 — жирная (сдача).</summary>
    public sealed record Row(string Label, string Value, int Accent = 0);

    public string Shop { get; set; } = "";
    public string Inn { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>Номер чека как показан в списке; <see cref="NumberText"/> — с нулями, как в мобилке (1601 → 001601).</summary>
    public string Number { get; set; } = "";
    public string Shift { get; set; } = "";
    public string DateTimeText { get; set; } = "";
    public string Cashier { get; set; } = "";
    public string Cashbox { get; set; } = "";
    public string Consultant { get; set; } = "";
    public string Client { get; set; } = "";
    public StatusKind Status { get; set; } = StatusKind.Paid;
    public string StatusText { get; set; } = "";
    /// <summary>Плашка-вид документа под шапкой: «ТОВАРНЫЙ ЧЕК» / «ТОВАРНЫЙ ЧЕК · ВОЗВРАТ» (чек кассы нефискальный).</summary>
    public string Title { get; set; } = "";
    public bool IsReturnDoc => Status == StatusKind.Return;
    public List<Line> Lines { get; } = new();
    /// <summary>Сумма и скидка (до итога); пусто, если цифры не сходятся — как в текстовом чеке.</summary>
    public List<Row> BeforeTotal { get; } = new();
    public string TotalLabel { get; set; } = "";
    /// <summary>Итог с валютой («430 сом»).</summary>
    public string TotalText { get; set; } = "";
    /// <summary>Способ оплаты, внесено, сдача, оплачено, в долг, вернуть до, остаток долга.</summary>
    public List<Row> Payment { get; } = new();
    /// <summary>2026-10-27: возвраты по чеку — отдельным блоком под оплатой (чек выше остаётся «как был продан»).</summary>
    public string ReturnsCaption { get; set; } = "";
    public List<Row> Returns { get; } = new();
    /// <summary>Чек сохранён без связи (офлайн-очередь) — строка под чеком.</summary>
    public string OfflineNote { get; set; } = "";
    /// <summary>Ссылка на электронный чек из ответа сервера (как eReceiptUrl мобилки); null — сервер её не выдал.</summary>
    public string? QrUrl { get; set; }
    /// <summary>Показывать ли плашку статуса («Оплачен/Долг/Возврат») внутри бумаги: у окна истории она уже в шапке окна.</summary>
    public bool ShowStatusBadge { get; set; } = true;
    /// <summary>Статус стоит показать плашкой (долг, возврат, «Долг погашен»); обычный «Оплачен» уже виден по блоку оплаты.</summary>
    public bool StatusNotable { get; set; }

    // Количество на экране — запятая («0,35 кг»), как у остальных цифр кассы (MoneyText).
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");


    /// <summary>Сумма как в мобилке: целая — без копеек («1 020»), иначе с копейками («1 020,50»).</summary>
    public static string Fmt(decimal v)
    {
        var s = MoneyText.Amount(v);
        return s.EndsWith(",00", StringComparison.Ordinal) ? s[..^3] : s;
    }

    public static string Som(decimal v) => Fmt(v) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    /// <summary>Номер с нулями, как в мобилке (ReceiptNumber.numberText): «№1601» → «001601»; не число — как есть; пусто — «—».</summary>
    public static string PadNumber(string? number)
    {
        var n = (number ?? "").Trim().TrimStart('№').Trim();
        if (n.Length == 0)
            return "—";
        var digits = true;
        foreach (var c in n)
            digits &= c is >= '0' and <= '9';
        return digits && n.Length < 6 ? n.PadLeft(6, '0') : n;
    }

    public string NumberText => PadNumber(Number);

    public static string TitleText(bool isReturn) => isReturn
        ? Tr.T("ТОВАРНЫЙ ЧЕК · ВОЗВРАТ", "ТОВАРДЫК ЧЕК · КАЙТАРУУ", "SALES RECEIPT · RETURN", "SATIŞ FİŞİ · İADE", "TOVAR CHEKI · QAYTARISH")
        : Tr.T("ТОВАРНЫЙ ЧЕК", "ТОВАРДЫК ЧЕК", "SALES RECEIPT", "SATIŞ FİŞİ", "TOVAR CHEKI");

    public static SaleReceiptCardData Build(JsonElement sale, string? receiptNumber, decimal discount, decimal? total,
        string? paymentMethodOverride = null, string? cashReceivedOverride = null, DateTime? soldAt = null, bool savedOffline = false)
    {
        var prefs = UserPreferences.Instance;
        var d = new SaleReceiptCardData
        {
            Shop = prefs.ShowStoreName && !string.IsNullOrWhiteSpace(prefs.StoreName) ? prefs.StoreName.Trim() : "NurMarket",
            Inn = prefs.ShowInn && !string.IsNullOrWhiteSpace(prefs.StoreInn) ? prefs.StoreInn.Trim() : "",
            Address = prefs.ShowAddress && !string.IsNullOrWhiteSpace(prefs.StoreAddress) ? prefs.StoreAddress.Trim() : "",
            Number = (receiptNumber ?? "").Trim(),
            Cashier = (CartDisplayHelper.TryCashierName(sale) ?? PosApp.CurrentUserDisplayName ?? "").Trim(),
            Cashbox = Str(sale, "cashbox_name") ?? Str(sale, "cashbox_display") ?? "",
            Shift = ShiftNo(sale),
            Consultant = Str(sale, "consultant_display") ?? "",
            Client = Str(sale, "client_name") ?? "",
            QrUrl = EReceiptUrl(sale),
        };
        if (prefs.ShowDate)
        {
            if (Str(sale, "created_at") is { } createdText
                && DateTime.TryParse(createdText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
                d.DateTimeText = created.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            else if (soldAt is { } at)
                d.DateTimeText = at.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }
        if (savedOffline)
            d.OfflineNote = Tr.T("Чек сохранён без связи — уйдёт на сервер автоматически", "Чек байланышсыз сакталды — серверге автоматтык түрдө жөнөтүлөт",
                "The receipt is saved offline — it will be sent to the server automatically", "Fiş çevrimdışı kaydedildi — sunucuya otomatik gönderilecek",
                "Chek aloqasiz saqlandi — serverga avtomatik yuboriladi");

        decimal linesTotal = 0;
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var qty = CartDisplayHelper.LineQuantity(line);
            var price = (decimal)CartDisplayHelper.UnitPrice(line);
            if (!decimal.TryParse(CartDisplayHelper.LineTotal(line), NumberStyles.Any, CultureInfo.InvariantCulture, out var sum))
                sum = (decimal)qty * price;
            linesTotal += sum;
            var unit = Str(line, "unit") ?? Str(line, "unit_name") ?? Str(line, "unit_display") ?? "";
            var lineDiscount = 0m;
            foreach (var key in new[] { "discount_total", "line_discount", "discount" })
                if (Money(line, key) is { } ld && ld > 0.005m) { lineDiscount = ld; break; }
            d.Lines.Add(new Line(CartDisplayHelper.ItemName(line),
                $"{qty.ToString("0.###", RuCulture)}{(unit.Length == 0 ? "" : " " + unit)} × {Fmt(price)}", Fmt(sum),
                lineDiscount > 0 ? Tr.T("скидка −", "арзандатуу −", "discount −", "indirim −", "chegirma −") + Fmt(lineDiscount) : null));
        }

        var due = total ?? linesTotal - discount;
        if (discount > 0.005m)
        {
            decimal? subtotal = null;
            if (Math.Abs(linesTotal - discount - due) < 0.01m) subtotal = linesTotal;
            else if (Math.Abs(linesTotal - due) < 0.01m) subtotal = due + discount;
            if (subtotal is { } sub)
            {
                d.BeforeTotal.Add(new Row(Tr.T("Сумма без скидки", "Арзандатуусуз сумма", "Subtotal", "İndirimsiz tutar", "Chegirmasiz summa"), Fmt(sub)));
                d.BeforeTotal.Add(new Row(Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"), "−" + Fmt(discount), 2));
            }
        }
        d.TotalLabel = Tr.T("ИТОГО", "ЖАЛПЫ", "TOTAL", "TOPLAM", "JAMI");
        d.TotalText = Som(due);

        var sale0 = (Str(sale, "status") ?? "").ToLowerInvariant();
        var method = (paymentMethodOverride ?? Str(sale, "payment_method") ?? "").ToLowerInvariant();
        var received = cashReceivedOverride is not null
            ? (decimal.TryParse(cashReceivedOverride, NumberStyles.Any, CultureInfo.InvariantCulture, out var cr) ? cr : null)
            : Money(sale, "cash_received");
        var debtLeft = 0m;
        decimal? debtNow = null;
        switch (method)
        {
            case "cash":
                d.Payment.Add(new Row(Tr.T("Наличными", "Накталай", "Cash", "Nakit", "Naqd"), Fmt(received is > 0 ? received.Value : due)));
                if (received is > 0)
                {
                    var change = Money(sale, "change") ?? Math.Max(0, received.Value - due);
                    if (change > 0.004m)
                        d.Payment.Add(new Row(Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim"), Fmt(change), 3));
                }
                break;
            case "transfer" or "card" or "mbank" or "bakai":
                d.Payment.Add(new Row(Tr.T("Безналичными", "Накталайсыз", "Non-cash", "Nakitsiz", "Naqdsiz"), Fmt(due)));
                break;
            case "mixed" or "split":
                if (received is > 0 && received < due)
                {
                    d.Payment.Add(new Row(Tr.T("Наличными", "Накталай", "Cash", "Nakit", "Naqd"), Fmt(received.Value)));
                    d.Payment.Add(new Row(Tr.T("Безналичными", "Накталайсыз", "Non-cash", "Nakitsiz", "Naqdsiz"), Fmt(due - received.Value)));
                }
                else
                    d.Payment.Add(new Row(Tr.T("Смешанная оплата", "Аралаш төлөм", "Mixed payment", "Karışık ödeme", "Aralash to'lov"), Fmt(due)));
                break;
            case "debt":
            {
                var ledger = SaleReturnLedger.Get(Str(sale, "id"));
                var paidNow = ledger is { Total: > 0m } ? ledger.PaidCash + ledger.PaidNonCash : received ?? 0m;
                debtLeft = ledger is { Total: > 0m } ? ledger.Debt : Math.Max(0m, due - paidNow);
                d.Payment.Add(new Row(Tr.T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), Tr.T("В долг", "Карызга", "On credit", "Borca", "Qarzga")));
                if (paidNow > 0.005m)
                    d.Payment.Add(new Row(Tr.T("Внесено", "Киргизилди", "Paid in", "Alınan", "Kiritildi"), Fmt(paidNow)));
                d.Payment.Add(new Row(Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz"), Fmt(debtLeft), 1));
                if (ledger is { DebtSchedule.Count: > 1 })
                {
                    d.Payment.Add(new Row(Tr.T("Рассрочка", "Бөлүп төлөө", "Instalments", "Taksit", "Bo'lib to'lash"),
                        ledger.DebtSchedule.Count + " " + Tr.T("плат.", "төл.", "pmts", "ödeme", "to'lov")));
                    var no = 0;
                    foreach (var p in ledger.DebtSchedule)
                        d.Payment.Add(new Row($"{++no}) {p.Date:dd.MM.yyyy}", Fmt(p.Amount)));
                }
                else if (ledger?.DebtDueDate is { } dueDate)
                    d.Payment.Add(new Row(Tr.T("Вернуть до", "Кайтаруу мөөнөтү", "Pay by", "Son ödeme", "Qaytarish muddati"), dueDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), 1));
                if (Money(sale, "remaining_debt") is { } remaining)
                {
                    debtNow = remaining;
                    d.Payment.Add(new Row(Tr.T("Остаток долга сейчас", "Азыркы карыз калдыгы", "Debt balance now", "Şu anki borç bakiyesi", "Hozirgi qarz qoldig'i"), Fmt(remaining), 1));
                }
                if (ledger?.ClientPhone is { Length: > 0 } phone && CartReceiptTextBuilder.MaskPhone(phone) is { Length: > 0 } masked)
                    d.Payment.Add(new Row(Tr.T("Телефон клиента", "Кардардын телефону", "Client phone", "Müşteri telefonu", "Mijoz telefoni"), masked));
                if (d.Client.Length == 0 && !string.IsNullOrWhiteSpace(ledger?.ClientName))
                    d.Client = ledger!.ClientName!.Trim();
                break;
            }
        }

        // Плашка: возврат — серая, долг (остался неоплаченный) — оранжевая, иначе «Оплачен» — зелёная.
        if (sale0.Contains("return") || sale0.Contains("refund"))
        {
            d.Status = StatusKind.Return;
            d.StatusNotable = true;
            d.StatusText = Tr.T("Возврат", "Кайтарым", "Return", "İade", "Qaytarish");
        }
        else if (method == "debt" && debtLeft > 0.005m && debtNow is { } dn && dn < 0.005m)
        {
            // 2026-10-23: долг был, но уже погашен — зелёная плашка вместо «Долг».
            d.Status = StatusKind.Paid;
            d.StatusNotable = true;
            d.StatusText = Tr.T("Долг погашен", "Карыз төлөндү", "Debt repaid", "Borç ödendi", "Qarz to'landi");
        }
        else if (sale0.Contains("debt") || (method == "debt" && debtLeft > 0.005m))
        {
            d.Status = StatusKind.Debt;
            d.StatusNotable = true;
            d.StatusText = Tr.T("Долг", "Карыз", "Debt", "Borç", "Qarz");
        }
        else
        {
            d.Status = StatusKind.Paid;
            d.StatusText = Tr.T("Оплачен", "Төлөндү", "Paid", "Ödendi", "To'landi");
        }
        d.Title = TitleText(d.IsReturnDoc);
        return d;
    }

    /// <summary>Текст для мессенджера и QR без ссылки — те же реквизиты строками (как Receipt.toText мобилки). Печатную ленту не заменяет.</summary>
    public string ToText()
    {
        const string dash = "--------------------------------";
        var b = new StringBuilder();
        b.AppendLine(Shop);
        if (Inn.Length > 0) b.AppendLine(Tr.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ") + Inn);
        if (Address.Length > 0) b.AppendLine(Address);
        b.AppendLine(Title);
        b.AppendLine(Tr.T("Чек № ", "Чек № ", "Receipt № ", "Fiş № ", "Chek № ") + NumberText
            + (Shift.Length == 0 ? "" : Tr.T(" · смена № ", " · смена № ", " · shift № ", " · vardiya № ", " · smena № ") + Shift));
        if (DateTimeText.Length > 0) b.AppendLine(DateTimeText);
        if (Cashier.Length > 0) b.AppendLine(Tr.T("Кассир: ", "Кассир: ", "Cashier: ", "Kasiyer: ", "Kassir: ") + Cashier);
        if (Client.Length > 0) b.AppendLine(Tr.T("Покупатель: ", "Сатып алуучу: ", "Customer: ", "Müşteri: ", "Xaridor: ") + Client);
        b.AppendLine(dash);
        var i = 0;
        foreach (var l in Lines)
        {
            b.AppendLine($"{++i}. {l.Name}");
            b.AppendLine($"  {l.QtyPrice} = {l.Total}");
            if (l.Discount is { Length: > 0 }) b.AppendLine("  " + l.Discount);
            if (l.Returned is { Length: > 0 }) b.AppendLine("  " + l.Returned);
        }
        b.AppendLine(dash);
        foreach (var r in BeforeTotal) b.AppendLine($"{r.Label}: {r.Value}");
        b.AppendLine($"{TotalLabel}: {TotalText}");
        foreach (var r in Payment) b.AppendLine($"{r.Label}: {r.Value}");
        if (Returns.Count > 0)
        {
            b.AppendLine(dash);
            b.AppendLine(ReturnsCaption);
            foreach (var r in Returns) b.AppendLine(r.Value.Length == 0 ? r.Label.Trim() : $"{r.Label.Trim()}: {r.Value}");
        }
        if (QrUrl != null) b.AppendLine(Tr.T("Электронный чек: ", "Электрондук чек: ", "E-receipt: ", "E-fiş: ", "Elektron chek: ") + QrUrl);
        b.AppendLine(Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışveriş için teşekkürler!", "Xaridingiz uchun rahmat!"));
        return b.ToString();
    }

    /// <summary>Что кодирует QR: ссылка на электронный чек; нет её — чек текстом, уложенный в ~1100 байт (как compactReceiptText мобилки).</summary>
    public string QrPayload()
    {
        return QrUrl ?? CompactText(1100);
    }

    /// <summary>Чек текстом, уложенный в <paramref name="maxBytes"/> (UTF-8): шапка, сколько влезет позиций, «…» и итоговые строки.</summary>
    public string CompactText(int maxBytes)
    {
        var full = ToText();
        if (Encoding.UTF8.GetByteCount(full) <= maxBytes)
            return full;
        var lines = full.Split('\n');
        var head = new List<string>(lines[..Math.Min(3, lines.Length)]);
        var tail = new List<string>();
        for (var k = lines.Length - 1; k >= 3 && tail.Count < 3; k--)
            if (lines[k].Contains(':')) tail.Insert(0, lines[k]);
        var middle = new List<string>();
        for (var k = 3; k < lines.Length; k++)
        {
            var trial = string.Join("\n", head) + "\n" + string.Join("\n", middle) + "\n" + lines[k] + "\n…\n" + string.Join("\n", tail);
            if (Encoding.UTF8.GetByteCount(trial) > maxBytes - 50)
                break;
            middle.Add(lines[k]);
        }
        return string.Join("\n", head) + "\n" + string.Join("\n", middle) + "\n…\n" + string.Join("\n", tail);
    }

    /// <summary>Ссылка на электронный чек из ответа сервера — как eReceiptUrl мобилки (e_receipt.pdf_url / url / token).</summary>
    public static string? EReceiptUrl(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return null;
        JsonElement nested = default;
        if (sale.TryGetProperty("e_receipt", out var e) && e.ValueKind == JsonValueKind.Object)
            nested = e;
        var pdf = Str(sale, "e_receipt_pdf_url") ?? Str(nested, "pdf_url");
        if (pdf != null && pdf.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return pdf;
        var url = Str(sale, "e_receipt_url") ?? Str(nested, "url") ?? Str(sale, "receipt_url");
        if (url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? url : url.TrimEnd('/') + ".pdf";
        var token = Str(sale, "e_receipt_token") ?? Str(nested, "token");
        return token == null ? null : "https://check.nurcrm.kg/" + token + ".pdf";
    }

    private static string ShiftNo(JsonElement sale)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return "";
        if (sale.TryGetProperty("shift", out var s) && s.ValueKind == JsonValueKind.Object)
            return Str(s, "number") ?? "";
        return Str(sale, "shift_number") ?? "";
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
