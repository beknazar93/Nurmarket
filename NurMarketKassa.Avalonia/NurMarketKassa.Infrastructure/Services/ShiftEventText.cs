using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-26 (владелец, фото «Возвраты» и «Оплата долгов» смены): в списках смены вместо служебного «cash=119.75;noncash=0.00;debt=454.25» —
/// понятный текст: что вернули и как («Наличными 119,75 · В счёт долга 454,25»), а у оплаты долга — способ («Наличными», «Безналом» или «Смешанно»).
/// Чистые функции без окон, файлов и сети — проверяются юнит-тестами (ShiftEventTextTests).</summary>
public static class ShiftEventText
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string Amount(double value) => value.ToString("N2", Ru);

    /// <summary>Подпись «Наличными».</summary>
    public static string CashLabel => Tr.T("Наличными", "Накталай", "Cash", "Nakit", "Naqd");

    /// <summary>Подпись «Безналом» (оплата долга).</summary>
    public static string NonCashLabel => Tr.T("Безналом", "Накталай эмес", "Non-cash", "Nakitsiz", "Naqd emas");

    /// <summary>Подпись «Смешанно» (часть наличными, часть безналом за один раз).</summary>
    public static string MixedLabel => Tr.T("Смешанно", "Аралаш", "Mixed", "Karışık", "Aralash");

    /// <summary>Способ оплаты долга одной записи: «Наличными» / «Безналом».</summary>
    public static string DebtMethod(bool nonCash) => nonCash ? NonCashLabel : CashLabel;

    /// <summary>2026-11-26: делит общую сумму смешанной оплаты долга: безналичная часть строго больше нуля и меньше общей суммы (обе части — копейки), наличная — остаток.
    /// false — часть не годится (кассиру: «укажите сумму безналом»).</summary>
    public static bool TrySplitMixed(decimal total, decimal nonCash, out decimal cashPart, out decimal nonCashPart)
    {
        var all = Math.Round(total, 2);
        nonCashPart = Math.Round(nonCash, 2);
        cashPart = all - nonCashPart;
        return nonCashPart > 0m && cashPart > 0m;
    }

    /// <summary>«Смешанно: наличными 40,00 + безналом 60,00».</summary>
    public static string DebtMixed(double cash, double nonCash) =>
        MixedLabel + ": " + Tr.T("наличными ", "накталай ", "cash ", "nakit ", "naqd ") + Amount(cash)
        + " + " + Tr.T("безналом ", "накталай эмес ", "non-cash ", "nakitsiz ", "naqd emas ") + Amount(nonCash);

    /// <summary>Одна строка списка «Оплата долгов»: время (UTC), сумма, клиент (примечание события), способ.</summary>
    public sealed record DebtRow(DateTime At, double Amount, string Details, double Cash, double NonCash)
    {
        public bool IsMixed => Cash > 0.004 && NonCash > 0.004;
    }

    /// <summary>2026-11-26 (живая проверка): признак «обе части ушли одной операцией „Смешанно“ из окна оплаты долга» — общий идентификатор в конце source_id события смены
    /// («pay-debt:КЛЮЧ|mix=ГРУППА»). Схема базы та же (source_id — текст); отдельные оплаты (наличные 100 и безнал 100 за минуту) маркера не имеют и не сливаются.</summary>
    public const string MixMarker = "|mix=";

    /// <summary>source_id для события оплаты долга: с идентификатором группы смешанной оплаты (mixId) или без него.</summary>
    public static string DebtSourceId(string baseId, string? mixId) =>
        string.IsNullOrWhiteSpace(mixId) ? baseId : baseId + MixMarker + mixId.Trim();

    /// <summary>Идентификатор группы смешанной оплаты из source_id события; нет маркера — null.</summary>
    public static string? MixGroupOf(string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId))
            return null;
        var at = sourceId.LastIndexOf(MixMarker, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var id = sourceId[(at + MixMarker.Length)..].Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>Собирает строки списка «Оплата долгов». Наличная и безналичная части сливаются в одну строку «Смешанно» ТОЛЬКО если обе ушли одной операцией «Смешанно» окна
    /// (общий идентификатор в source_id, см. <see cref="MixMarker"/>) и это один клиент; две отдельные оплаты (в том числе подряд за минуту) — две строки.</summary>
    public static List<DebtRow> BuildDebtRows(IEnumerable<(DateTime At, double Amount, string? Note, bool NonCash, string? SourceId)> events)
    {
        var ordered = events.OrderBy(e => e.At).ToList();
        var used = new bool[ordered.Count];
        var rows = new List<DebtRow>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            var e = ordered[i];
            var client = (e.Note ?? "").Trim();
            var group = MixGroupOf(e.SourceId);
            // Пара: запись ТОГО ЖЕ клиента другим способом из той же смешанной оплаты.
            var pair = -1;
            if (group is not null)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    if (!used[j] && ordered[j].NonCash != e.NonCash && string.Equals(MixGroupOf(ordered[j].SourceId), group, StringComparison.Ordinal)
                        && string.Equals((ordered[j].Note ?? "").Trim(), client, StringComparison.OrdinalIgnoreCase))
                    {
                        pair = j;
                        break;
                    }
                }
            }

            if (pair >= 0)
            {
                used[pair] = true;
                var other = ordered[pair];
                var cash = e.NonCash ? other.Amount : e.Amount;
                var non = e.NonCash ? e.Amount : other.Amount;
                rows.Add(new DebtRow(e.At, cash + non, Join(client, DebtMixed(cash, non)), cash, non));
            }
            else
            {
                rows.Add(new DebtRow(e.At, e.Amount, Join(client, DebtMethod(e.NonCash)), e.NonCash ? 0 : e.Amount, e.NonCash ? e.Amount : 0));
            }
        }

        return rows;
    }

    /// <summary>2026-11-26: оплата долга того же клиента на ту же сумму (любым способом) не раньше чем <paramref name="window"/> назад — время (UTC) самой свежей такой оплаты; нет — null.
    /// Нужно для предупреждения «такая же оплата только что принята» (возможно, повторное нажатие).</summary>
    public static DateTime? FindRecentSamePayment(IEnumerable<(DateTime At, double Amount, string? Note, bool NonCash)> events, string? client, double amount, DateTime nowUtc, TimeSpan window)
    {
        var name = (client ?? "").Trim();
        DateTime? best = null;
        foreach (var e in events)
        {
            if (Math.Abs(e.Amount - amount) > 0.005 || !string.Equals((e.Note ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                continue;
            var at = e.At.Kind == DateTimeKind.Local ? e.At.ToUniversalTime() : e.At;
            if (nowUtc - at < TimeSpan.Zero || nowUtc - at > window)
                continue;
            if (best is null || at > best)
                best = at;
        }

        return best;
    }

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>id чека из source_id события возврата («id:yyyyMMddHHmmssfff», см. ShiftEventsStore.OperationKey); пусто — id нет.</summary>
    public static string? SaleIdFromSource(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return null;
        var s = sourceId.Trim();
        var colon = s.LastIndexOf(':');
        var id = colon > 0 ? s[..colon] : s;
        return id is "op" or "" ? null : id;
    }

    /// <summary>Возврат из журнала чека, который соответствует событию смены: по сумме и ближайший по времени (не дальше 15 минут). Нет такого — null.</summary>
    public static SaleLedgerRefund? FindRefund(SaleLedgerEntry? ledger, DateTime eventAtUtc, double amount)
    {
        if (ledger is null || ledger.Refunds.Count == 0)
            return null;
        return ledger.Refunds
            .Where(r => Math.Abs((double)r.Amount - Math.Abs(amount)) < 0.011 && Math.Abs((r.At.UtcDateTime - eventAtUtc.ToUniversalTime()).TotalMinutes) <= 15)
            .OrderBy(r => Math.Abs((r.At.UtcDateTime - eventAtUtc.ToUniversalTime()).TotalSeconds))
            .FirstOrDefault();
    }

    /// <summary>«Наличными 119,75 · В счёт долга 454,25 · На карту 0» — из примечания события возврата. По умолчанию показываются только ненулевые части; нет разбивки (запись
    /// до 20.10.2026) — вся сумма наличными. Отмена возврата («storno=1») и обмен («exchange=1») подписываются.</summary>
    public static string ReturnSplit(string? note, double amount)
    {
        var parts = new List<string>();
        var hasSplit = ShiftEventsStore.NoteNumber(note, "cash") is not null;
        var cash = hasSplit ? ShiftEventsStore.NoteNumber(note, "cash") ?? 0 : amount;
        var non = hasSplit ? ShiftEventsStore.NoteNumber(note, "noncash") ?? 0 : 0;
        var debt = hasSplit ? ShiftEventsStore.NoteNumber(note, "debt") ?? 0 : 0;
        if (Math.Abs(cash) > 0.004)
            parts.Add(CashLabel + " " + Amount(cash));
        if (Math.Abs(debt) > 0.004)
            parts.Add(Tr.T("В счёт долга ", "Карызга эсептелди ", "Against debt ", "Borca mahsup ", "Qarz hisobiga ") + Amount(debt));
        if (Math.Abs(non) > 0.004)
            parts.Add(Tr.T("На карту ", "Картага ", "To card ", "Karta ", "Kartaga ") + Amount(non));
        if (parts.Count == 0)
            parts.Add(CashLabel + " " + Amount(0));
        var prefix = new List<string>();
        if (ShiftEventsStore.NoteNumber(note, "storno") is { } st && st > 0.5)
            prefix.Add(ShiftEventsStore.IsPreviousShiftNote(note)
                ? Tr.T("Отмена возврата прошлой смены", "Өткөн сменадагы кайтарууну жокко чыгаруу", "Cancelled return of a previous shift", "Önceki vardiya iadesinin iptali", "Oldingi smena qaytarishini bekor qilish")
                : Tr.T("Отмена возврата", "Кайтарууну жокко чыгаруу", "Return cancelled", "İade iptali", "Qaytarishni bekor qilish"));
        if (ShiftEventsStore.NoteNumber(note, "exchange") is { } ex && ex > 0.5)
            prefix.Add(Tr.T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish"));
        return Join(string.Join(" · ", prefix), string.Join(" · ", parts));
    }

    /// <summary>«Чек № 000006» (номер чека; нет номера — пусто).</summary>
    public static string ReceiptLabel(string? number) =>
        string.IsNullOrWhiteSpace(number) ? "" : Tr.T("Чек № ", "Чек № ", "Receipt № ", "Fiş № ", "Chek № ") + number.Trim();

    /// <summary>Что возвращено: «Баранина × 0,575 · Бука лакым × 1» (не больше трёх позиций, дальше — «и ещё N»).</summary>
    public static string Goods(IReadOnlyList<SaleLedgerLine> lines, int max = 3)
    {
        if (lines.Count == 0)
            return "";
        var items = lines.Take(max).Select(l => $"{l.Name} × {l.Quantity.ToString("0.###", Ru)}").ToList();
        var text = string.Join(", ", items);
        if (lines.Count > max)
            text += " " + Tr.T($"и ещё {lines.Count - max}", $"жана дагы {lines.Count - max}", $"and {lines.Count - max} more", $"ve {lines.Count - max} tane daha", $"va yana {lines.Count - max} ta");
        return text;
    }

    /// <summary>Полная подпись строки возврата: «Чек № 000006 · Баранина × 0,575 · Наличными 119,75 · В счёт долга 454,25».</summary>
    public static string ReturnDetails(string? note, double amount, string? receiptNumber, IReadOnlyList<SaleLedgerLine>? goods) =>
        Join(ReceiptLabel(receiptNumber), goods is { Count: > 0 } ? Goods(goods) : "", ReturnSplit(note, amount));
}
