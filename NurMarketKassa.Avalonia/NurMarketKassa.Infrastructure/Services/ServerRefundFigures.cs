using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-01 (ответ Умара по ТЗ 01.11, п. 3 «Возврат долгового чека»): цифры, которые сервер называет в ответе на возврат чека.
/// Выдача наличных — <c>refund.cash</c> (НЕ <c>debt_adjustment.reduced_by</c> — это на сколько уменьшился ДОЛГ); безналичная и «на баланс» части —
/// <c>refund.noncash</c> / <c>refund.balance</c>, если сервер их отдаёт; долг клиента — <c>debt_adjustment.reduced_by</c> и <c>remaining_debt</c>.
/// Есть <c>refund.cash</c> — касса доверяет серверу; нет (старый сервер / ответ без поля) — прежний расчёт кассы. Чистая разборка JSON без сети.</summary>
public sealed record ServerRefundFigures(decimal? Cash, decimal? NonCash, decimal? Balance, decimal? DebtReducedBy, decimal? RemainingDebt, string? ReturnStatus = null)
{
    /// <summary>Сервер назвал выдачу наличными — это источник суммы к выдаче.</summary>
    public bool HasCash => Cash is not null;

    /// <summary>null — в ответе нет ни refund, ни debt_adjustment (старый сервер, пустой ответ).</summary>
    public static ServerRefundFigures? TryRead(JsonElement response)
    {
        try
        {
            var root = response;
            // Ответ может прийти в конверте {"data": {...}}.
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("refund", out _) && !root.TryGetProperty("debt_adjustment", out _)
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                root = data;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            decimal? cash = null, nonCash = null, balance = null, reducedBy = null, remaining = null;
            var any = false;
            if (root.TryGetProperty("refund", out var refund) && refund.ValueKind == JsonValueKind.Object)
            {
                any = true;
                cash = Num(refund, "cash");
                nonCash = Num(refund, "noncash") ?? Num(refund, "non_cash");
                balance = Num(refund, "balance");
            }
            if (root.TryGetProperty("debt_adjustment", out var debt) && debt.ValueKind == JsonValueKind.Object)
            {
                any = true;
                reducedBy = Num(debt, "reduced_by");
                remaining = Num(debt, "remaining_debt");
            }
            // return_status чека в ответе (none / partial / full): у полностью возвращённого долгового чека сервер оставляет старый остаток долга — окно возврата его не показывает.
            var status = root.TryGetProperty("return_status", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
            return any ? new ServerRefundFigures(cash, nonCash, balance, reducedBy, remaining, status) : null;
        }
        catch
        {
            return null;
        }
    }

    private static decimal? Num(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDecimal(out var exact) ? exact : null;
        return el.ValueKind == JsonValueKind.String ? ParseMoney(el.GetString()) : null;
    }

    /// <summary>2026-11-01 (приёмка 1.17.72, С-1): число из строки, не зависящее от культуры. «2,00» → 2, а не 200. Есть и точка, и запятая — десятичная та, что правее
    /// (другая — тысячная, убирается); одна запятая и не больше двух цифр после неё — десятичная; запятая с тремя цифрами или несколько запятых — тысячные; пробелы убираются.
    /// Не число — null (цифре сервера тогда не доверяем).</summary>
    public static decimal? ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var s = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '\u00A0').ToArray());
        var dot = s.LastIndexOf('.');
        var comma = s.LastIndexOf(',');
        if (dot >= 0 && comma >= 0)
        {
            var decimalSep = dot > comma ? '.' : ',';
            var thousandSep = decimalSep == '.' ? ',' : '.';
            s = s.Replace(thousandSep.ToString(), "").Replace(decimalSep, '.');
        }
        else if (comma >= 0)
        {
            var commas = s.Count(c => c == ',');
            var afterComma = s.Length - comma - 1;
            s = commas == 1 && afterComma <= 2 ? s.Replace(',', '.') : s.Replace(",", "");
        }
        else if (dot >= 0 && s.Count(c => c == '.') > 1)
            s = s.Replace(".", "");
        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
