using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-03, владелец: «нужна кнопка для печати документов из чека». Документ по проведённой продаже
/// на листе A4: «Товарный чек» или «Накладная» — реквизиты магазина (название, ИНН, адрес), покупатель, таблица
/// товаров (№, наименование, кол-во, цена, сумма), скидка, итог, подписи. Собирается в HTML и открывается в
/// браузере сразу с окном печати: на кассе офисных программ обычно нет, а браузер есть всегда, и он же
/// сохраняет в PDF («Печать» → «Сохранить как PDF»).</summary>
public static class SaleDocumentHtmlBuilder
{
    public enum Kind { SalesReceipt, Waybill }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Build(JsonElement sale, string? number, decimal? total, Kind kind)
    {
        var prefs = UserPreferences.Instance;
        string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        string M(decimal v) => v.ToString("N2", Ru);

        var title = kind == Kind.Waybill
            ? Tr.T("Накладная", "Накладной", "Waybill", "İrsaliye", "Yuk xati")
            : Tr.T("Товарный чек", "Товардык чек", "Sales receipt", "Satış fişi", "Tovar cheki");
        var date = Str(sale, "created_at") is { } c && DateTime.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
            ? created
            : DateTime.Now;

        var sb = new StringBuilder(4000);
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(E(title)).Append(' ').Append(E(number)).Append("</title><style>")
          .Append("@page{size:A4;margin:15mm}body{font-family:Arial,sans-serif;font-size:12pt;color:#000}")
          .Append("h1{font-size:18pt;margin:0 0 4mm}table{width:100%;border-collapse:collapse;margin-top:4mm}")
          .Append("th,td{border:1px solid #000;padding:2mm 3mm;vertical-align:top}th{background:#eee}.r{text-align:right}.c{text-align:center}")
          .Append(".tot td{border:none;padding:1mm 3mm}.sign{display:flex;justify-content:space-between;margin-top:14mm}")
          .Append(".sign div{width:45%}.line{border-bottom:1px solid #000;height:7mm}.small{font-size:10pt;color:#333}")
          .Append("@media print{.noprint{display:none}}</style></head><body>");
        sb.Append("<div class=\"noprint\" style=\"margin-bottom:6mm\"><button onclick=\"window.print()\" style=\"font-size:14pt;padding:6px 18px\">")
          .Append(E(Tr.T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"))).Append("</button></div>");

        sb.Append("<h1>").Append(E(title)).Append(" № ").Append(E(number)).Append(" · ").Append(date.ToString("dd.MM.yyyy", Ru)).Append("</h1>");

        var seller = string.IsNullOrWhiteSpace(prefs.StoreName) ? "—" : prefs.StoreName!.Trim();
        sb.Append("<div>").Append(E(Tr.T("Продавец", "Сатуучу", "Seller", "Satıcı", "Sotuvchi"))).Append(": <b>").Append(E(seller)).Append("</b>");
        if (!string.IsNullOrWhiteSpace(prefs.StoreInn))
            sb.Append(", ").Append(E(Tr.T("ИНН", "ИНН", "TIN", "VKN", "STIR"))).Append(' ').Append(E(prefs.StoreInn!.Trim()));
        if (!string.IsNullOrWhiteSpace(prefs.StoreAddress))
            sb.Append(", ").Append(E(prefs.StoreAddress!.Trim()));
        sb.Append("</div>");
        var client = Str(sale, "client_name") ?? Str(sale, "client_display");
        sb.Append("<div>").Append(E(Tr.T("Покупатель", "Сатып алуучу", "Buyer", "Alıcı", "Xaridor"))).Append(": ")
          .Append(string.IsNullOrWhiteSpace(client) ? "____________________________" : "<b>" + E(client) + "</b>").Append("</div>");

        sb.Append("<table><tr><th class=\"c\">№</th><th>").Append(E(Tr.T("Наименование", "Аталышы", "Item", "Ürün", "Nomi")))
          .Append("</th><th class=\"r\">").Append(E(Tr.T("Кол-во", "Саны", "Qty", "Adet", "Soni")))
          .Append("</th><th class=\"r\">").Append(E(Tr.T("Цена", "Баасы", "Price", "Fiyat", "Narxi")))
          .Append("</th><th class=\"r\">").Append(E(Tr.T("Сумма", "Сумма", "Amount", "Tutar", "Summa"))).Append("</th></tr>");
        decimal linesTotal = 0;
        double qtyTotal = 0;
        var n = 0;
        foreach (var line in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var qty = CartDisplayHelper.LineQuantity(line);
            var price = (decimal)CartDisplayHelper.UnitPrice(line);
            if (!decimal.TryParse(CartDisplayHelper.LineTotal(line), NumberStyles.Any, CultureInfo.InvariantCulture, out var sum))
                sum = (decimal)qty * price;
            linesTotal += sum;
            qtyTotal += qty;
            sb.Append("<tr><td class=\"c\">").Append(++n).Append("</td><td>").Append(E(CartDisplayHelper.ItemName(line)))
              .Append("</td><td class=\"r\">").Append(qty.ToString("0.###", Ru)).Append("</td><td class=\"r\">").Append(M(price))
              .Append("</td><td class=\"r\">").Append(M(sum)).Append("</td></tr>");
        }
        sb.Append("</table>");

        var due = total ?? linesTotal;
        sb.Append("<table class=\"tot\">");
        if (linesTotal - due > 0.005m)
        {
            sb.Append("<tr><td class=\"r\">").Append(E(Tr.T("Сумма без скидки", "Арзандатуусуз сумма", "Subtotal", "Ara toplam", "Chegirmasiz summa"))).Append(":</td><td class=\"r\" style=\"width:35mm\">").Append(M(linesTotal)).Append("</td></tr>");
            sb.Append("<tr><td class=\"r\">").Append(E(Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma"))).Append(":</td><td class=\"r\">−").Append(M(linesTotal - due)).Append("</td></tr>");
        }
        sb.Append("<tr><td class=\"r\"><b>").Append(E(Tr.T("Итого", "Жыйынтык", "Total", "Toplam", "Jami"))).Append(":</b></td><td class=\"r\" style=\"width:35mm\"><b>")
          .Append(M(due)).Append(' ').Append(E(Tr.T("сом", "сом", "som", "som", "so'm"))).Append("</b></td></tr></table>");
        sb.Append("<div class=\"small\">").Append(E(Tr.T($"Всего наименований {n}, на сумму {M(due)} сом.", $"Бардыгы {n} аталыш, {M(due)} сомго.",
            $"{n} items in total, {M(due)} som.", $"Toplam {n} kalem, {M(due)} som.", $"Jami {n} nom, {M(due)} so'm."))).Append("</div>");

        var (left, right) = kind == Kind.Waybill
            ? (Tr.T("Отпустил", "Берди", "Released by", "Teslim eden", "Berdi"), Tr.T("Получил", "Алды", "Received by", "Teslim alan", "Oldi"))
            : (Tr.T("Продавец", "Сатуучу", "Seller", "Satıcı", "Sotuvchi"), Tr.T("Покупатель", "Сатып алуучу", "Buyer", "Alıcı", "Xaridor"));
        var cashier = CartDisplayHelper.TryCashierName(sale) ?? PosApp.CurrentUserDisplayName;
        sb.Append("<div class=\"sign\"><div>").Append(E(left)).Append(":<div class=\"line\"></div><div class=\"small\">")
          .Append(E(cashier)).Append("</div></div><div>").Append(E(right)).Append(":<div class=\"line\"></div></div></div>");
        sb.Append("<div class=\"small\" style=\"margin-top:8mm\">").Append(E(Tr.T("М.П.", "М.О.", "Stamp", "Kaşe", "M.O'."))).Append("</div>");
        sb.Append("<script>window.onload=function(){setTimeout(function(){window.print()},300)}</script></body></html>");
        return sb.ToString();
    }

    /// <summary>Сохраняет документ во временную папку и открывает в браузере (там же окно печати).</summary>
    public static string OpenForPrint(JsonElement sale, string? number, decimal? total, Kind kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NurMarketKassa", "documents");
        Directory.CreateDirectory(dir);
        var safe = string.Concat((number ?? "sale").Where(ch => char.IsLetterOrDigit(ch) || ch == '-'));
        var path = Path.Combine(dir, $"{(kind == Kind.Waybill ? "nakladnaya" : "tovarny-chek")}-{safe}-{DateTime.Now:HHmmss}.html");
        File.WriteAllText(path, Build(sale, number, total, kind), new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        PosLogger.Log($"Документ по чеку {number} открыт для печати ({kind}).", "PRINTER");
        return path;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
}
