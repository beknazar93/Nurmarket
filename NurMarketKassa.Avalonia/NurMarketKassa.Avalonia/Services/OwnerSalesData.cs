using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-07, владелец (снимок: «по чекам за вчера в сводке только общая выручка… предлагаю зайти в NurMarket» — «баг!
/// сделай так, чтобы у нашего ИИ был полный доступ и он сам собирал информацию и выводил у себя в чате таблицей! и на звонок
/// добавь!»). Вопрос про чеки или продажи за период («чеки за вчера», «продажи 6.10», «чеки за неделю», «кечээки чектер») —
/// программа сама загружает чеки всех касс с сервера (ReceiptHistoryService.LoadRangeAsync) и собирает таблицу: чеки (№, время,
/// кассир, оплата, сумма, товар), итоги по оплате, кассирам и дням. Таблица уходит ИИ в чате и показывается в чате при звонке.</summary>
public static class OwnerSalesData
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Похоже на вопрос о чеках или списке продаж за период.</summary>
    public static bool LooksLikeQuestion(string question)
    {
        var t = (question ?? "").ToLowerInvariant();
        if (Regex.IsMatch(t, @"\bчек|чектер|чекти|receipt|fiş|chek"))
            return true;
        var sales = new[] { "продаж", "продал", "сатуу", "саттык", "сатылды", "sales", "satış", "sotuv" }.Any(t.Contains);
        var detail = new[] { "список", "подроб", "кажд", "все ", "таблиц", "покажи", "выведи", "по часам", "по дням", "по кассир", "тизме", "көрсөт", "list", "detail" }.Any(t.Contains);
        return sales && detail;
    }

    private static readonly string[] Months = { "январ", "феврал", "март", "апрел", "ма", "июн", "июл", "август", "сентябр", "октябр", "ноябр", "декабр" };

    /// <summary>Период из вопроса; не назван — сегодня.</summary>
    public static (DateTime From, DateTime ToExclusive, string Label) Period(string question)
    {
        var t = (question ?? "").ToLowerInvariant();
        var today = DateTime.Today;
        (DateTime, DateTime, string) Day(DateTime d) => (d, d.AddDays(1), d.ToString("dd.MM.yyyy", Ru));
        var m = Regex.Match(t, @"\b(\d{1,2})[./](\d{1,2})(?:[./](\d{2,4}))?\b");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var dd) && int.TryParse(m.Groups[2].Value, out var mm) && mm is >= 1 and <= 12 && dd is >= 1 and <= 31)
        {
            var yy = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var y) ? (y < 100 ? 2000 + y : y) : today.Year;
            if (dd <= DateTime.DaysInMonth(yy, mm))
                return Day(new DateTime(yy, mm, dd));
        }
        var named = Regex.Match(t, @"\b(\d{1,2})\s+(январ|феврал|март|апрел|ма[йя]|июн|июл|август|сентябр|октябр|ноябр|декабр)");
        if (named.Success && int.TryParse(named.Groups[1].Value, out var nd))
        {
            var month = Array.FindIndex(Months, x => named.Groups[2].Value.StartsWith(x)) + 1;
            if (month > 0 && nd <= DateTime.DaysInMonth(today.Year, month))
                return Day(new DateTime(today.Year, month, nd));
        }
        if (t.Contains("позавчера") || t.Contains("мурдагы күн"))
            return Day(today.AddDays(-2));
        if (t.Contains("вчера") || t.Contains("кечээ") || t.Contains("yesterday") || t.Contains("dün") || t.Contains("kecha"))
            return Day(today.AddDays(-1));
        if (t.Contains("недел") || t.Contains("жума") || t.Contains("7 дн") || t.Contains("week") || t.Contains("hafta"))
            return (today.AddDays(-6), today.AddDays(1), $"{today.AddDays(-6):dd.MM}–{today:dd.MM.yyyy}");
        if (t.Contains("месяц") || Regex.IsMatch(t, @"\bай\b|айдагы|бул ай") || t.Contains("month") || t.Contains("ay ") || t.Contains("oy "))
            return (new DateTime(today.Year, today.Month, 1), today.AddDays(1), $"{new DateTime(today.Year, today.Month, 1):dd.MM}–{today:dd.MM.yyyy}");
        return Day(today);
    }

    private static string Money(decimal v) => v.ToString("N0", Ru) + " сом";

    /// <summary>Таблица чеков и итоги за период вопроса (Markdown) и короткий итог для голоса. Ошибка сервера — текст ошибки.</summary>
    public static async Task<(string Table, string Summary)> BuildAsync(string question, CancellationToken ct)
    {
        var (from, to, label) = Period(question);
        var days = (to - from).TotalDays;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        List<ReceiptHistoryEntry> all;
        try
        {
            all = await ReceiptHistoryService.LoadRangeAsync(from, to, days > 8 ? 15 : 10, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"ИИ-советник: чеки за {label} не загружены ({ex.Message}).", "WARNING");
            return ("", $"Чеки за {label} с сервера не загрузились ({ex.Message}).");
        }
        var paid = all.Where(e => !ReceiptHistoryService.IsReturnedOrCanceled(e)).ToList();
        var back = all.Where(ReceiptHistoryService.IsReturnedOrCanceled).ToList();
        PosLogger.Log($"ИИ-советник: чеки за {label} — {all.Count} шт. за {watch.ElapsedMilliseconds} мс.", "INFO");
        if (all.Count == 0)
            return ($"ЧЕКИ ЗА {label}: чеков нет.", $"За {label} чеков нет.");

        var total = paid.Sum(e => e.Total);
        var cash = paid.Where(e => e.PaymentMethod.Equals("cash", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Total);
        var sb = new StringBuilder();
        sb.AppendLine($"ЧЕКИ ЗА {label} (загружены программой с сервера NurCRM только что, все кассы; выведи владельцу таблицей):");
        sb.AppendLine($"Итого: {paid.Count} чеков на {Money(total)}; наличными {Money(cash)}, безналом и другими {Money(total - cash)}; "
                      + $"средний чек {Money(paid.Count > 0 ? total / paid.Count : 0)}"
                      + (back.Count > 0 ? $"; возвраты и отмены: {back.Count} на {Money(back.Sum(e => e.Total))}" : "") + ".");
        sb.AppendLine();
        var multiDay = days > 1;
        if (multiDay)
        {
            sb.AppendLine("| Дата | Чеков | Сумма |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in paid.GroupBy(e => e.CreatedAt.Date).OrderBy(g => g.Key))
                sb.AppendLine($"| {g.Key:dd.MM} | {g.Count()} | {Money(g.Sum(e => e.Total))} |");
            sb.AppendLine($"| Итого | {paid.Count} | {Money(total)} |");
            sb.AppendLine();
        }
        var byCashier = paid.GroupBy(e => string.IsNullOrWhiteSpace(e.Cashier) ? "—" : e.Cashier!.Trim()).ToList();
        if (byCashier.Count > 1)
        {
            sb.AppendLine("| Кассир | Чеков | Сумма |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in byCashier.OrderByDescending(g => g.Sum(e => e.Total)))
                sb.AppendLine($"| {g.Key} | {g.Count()} | {Money(g.Sum(e => e.Total))} |");
            sb.AppendLine();
        }
        const int maxRows = 60;
        sb.AppendLine("| № | Время | Кассир | Оплата | Сумма | Товар |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var e in all.Take(maxRows))
        {
            var status = ReceiptHistoryService.IsReturnedOrCanceled(e) ? " (возврат/отмена)" : "";
            var item = (e.FirstItemName ?? "").Replace("|", "/");
            if (item.Length > 40)
                item = item[..40] + "…";
            sb.AppendLine($"| {(e.ReceiptNumber.Length > 0 ? e.ReceiptNumber : "—")} | {e.CreatedAt.ToString(multiDay ? "dd.MM HH:mm" : "HH:mm", Ru)} | "
                          + $"{(string.IsNullOrWhiteSpace(e.Cashier) ? "—" : e.Cashier!.Replace("|", "/"))} | {ReceiptHistoryService.PaymentLabel(e.PaymentMethod)}{status} | "
                          + $"{Money(e.Total)} | {item} |");
        }
        if (all.Count > maxRows)
            sb.AppendLine($"(показаны последние {maxRows} из {all.Count}; итоги выше — по всем)");
        var summary = $"За {label}: {paid.Count} чеков на {Money(total)}, наличными {Money(cash)}, безналом {Money(total - cash)}"
                      + (back.Count > 0 ? $", возвратов {back.Count}" : "") + ".";
        return (sb.ToString().TrimEnd(), summary);
    }
}
