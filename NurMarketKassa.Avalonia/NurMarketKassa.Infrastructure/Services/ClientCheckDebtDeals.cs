using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-11-11 (сводка Умара, в бою): GET api/main/clients/&lt;id&gt;/deals/ добавляет в конец ВИРТУАЛЬНЫЕ строки старых долговых чеков —
/// id = "sale:&lt;uuid чека&gt;", is_check_debt: true; рассрочек и платежей у них нет. Это не сделка: открывать как сделку, слать в запросы сделок
/// (detail / pay / PATCH) нельзя — гасится такой долг оплатой долга ПО ЧЕКУ (pos/sales/&lt;uuid&gt;/pay-debt/), а в сумму долга чек входит один раз.</summary>
public static partial class ClientCheckDebtDeals
{
    public const string IdPrefix = "sale:";

    /// <summary>id вида «sale:…» — виртуальная строка, не сделка.</summary>
    public static bool IsVirtualId(string? id) => (id ?? "").Trim().StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Строка deals — виртуальный долг по чеку (is_check_debt: true или id «sale:…»).</summary>
    public static bool IsVirtual(JsonElement deal)
    {
        if (deal.ValueKind != JsonValueKind.Object)
            return false;
        if (deal.TryGetProperty("is_check_debt", out var f) && (f.ValueKind == JsonValueKind.True
                || (f.ValueKind == JsonValueKind.String && string.Equals(f.GetString(), "true", StringComparison.OrdinalIgnoreCase))))
            return true;
        return IsVirtualId(Str(deal, "id"));
    }

    /// <summary>uuid чека: из id «sale:&lt;uuid&gt;», иначе поле sale / sale_id (строка или объект с id). Пусто — не знаем.</summary>
    public static string SaleId(JsonElement deal)
    {
        var id = Str(deal, "id").Trim();
        if (IsVirtualId(id) && id.Length > IdPrefix.Length)
            return id[IdPrefix.Length..].Trim();
        foreach (var name in new[] { "sale_id", "sale" })
        {
            if (deal.ValueKind != JsonValueKind.Object || !deal.TryGetProperty(name, out var v))
                continue;
            if (v.ValueKind is JsonValueKind.String or JsonValueKind.Number && v.ToString().Trim() is { Length: > 0 } s)
                return s;
            if (v.ValueKind == JsonValueKind.Object && Str(v, "id") is { Length: > 0 } inner)
                return inner;
        }
        return "";
    }

    /// <summary>Номер чека для подписи: sale_number / number / receipt_number / doc_number, иначе «№…» из title; пусто — не знаем.</summary>
    public static string Number(JsonElement deal)
    {
        foreach (var name in new[] { "sale_number", "receipt_number", "number", "doc_number" })
            if (Str(deal, name).Trim() is { Length: > 0 } n)
                return n.TrimStart('№', '#').Trim();
        if (deal.ValueKind == JsonValueKind.Object && deal.TryGetProperty("sale", out var s) && s.ValueKind == JsonValueKind.Object && Str(s, "number") is { Length: > 0 } sn)
            return sn;
        var m = NumberInTitle().Match(Str(deal, "title"));
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Остаток долга по чеку: remaining_debt / debt_remaining / remaining, иначе debt_amount / amount. null — сервер не прислал суммы.</summary>
    public static double? Remaining(JsonElement deal)
    {
        foreach (var name in new[] { "remaining_debt", "debt_remaining", "remaining", "debt_amount", "amount" })
            if (Num(deal, name) is { } v)
                return Math.Max(0, v);
        return null;
    }

    /// <summary>Исходная сумма долга чека (debt_amount / amount / total); null — нет.</summary>
    public static double? Original(JsonElement deal)
    {
        foreach (var name in new[] { "debt_amount", "amount", "total" })
            if (Num(deal, name) is { } v && v > 0)
                return v;
        return null;
    }

    /// <summary>Подпись строки: «Долг по чеку №123» (номера нет — «Долг по чеку»), 5 языков.</summary>
    public static string Title(string number)
    {
        var n = string.IsNullOrWhiteSpace(number) ? "" : " №" + number.Trim();
        return Tr.T("Долг по чеку" + n, "Чек боюнча карыз" + n, "Debt on receipt" + n, "Fiş borcu" + n, "Chek bo'yicha qarz" + n);
    }

    /// <summary>Строки сделок без виртуальных строк чеков (для мест, где нужны ТОЛЬКО настоящие сделки: сроки, оплата взносов, поиск своей сделки).</summary>
    public static IEnumerable<JsonElement> RealDeals(IEnumerable<JsonElement> deals) => deals.Where(d => !IsVirtual(d));

    /// <summary>Запрет «sale:…» в адресах сделок (detail / pay / PATCH): такой id сервер как сделку не знает — понятный отказ (400) вместо запроса; null — id настоящей сделки.</summary>
    public static ApiException? RejectVirtualDealId(string? dealId)
    {
        if (!IsVirtualId(dealId))
            return null;
        PosLogger.Log($"Сделки: запрос по «{dealId}» не отправлен — это долг по чеку, а не сделка.", "WARNING");
        return new ApiException(Tr.T("Это долг по чеку, а не сделка — погасите его оплатой долга по чеку.", "Бул чек боюнча карыз, бүтүм эмес — аны чек боюнча карызды төлөө менен жабыңыз.",
                "This is a receipt debt, not a deal — settle it with a receipt debt payment.", "Bu bir fiş borcu, anlaşma değil — fiş borcu ödemesiyle kapatın.",
                "Bu chek bo'yicha qarz, bitim emas — uni chek bo'yicha qarzni to'lash bilan yoping."), 400);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

    private static double? Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    [GeneratedRegex(@"№\s*([0-9A-Za-zА-Яа-я\-]+)")]
    private static partial Regex NumberInTitle();
}
