using System.Globalization;
using System.Linq;
using NurMarketKassa.Core.Application;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13 (Л-5): строка списка чеков для поиска в окне возврата. <see cref="Position"/> — порядковый номер в списке (чек без номера с сервера показывается как «№N»).</summary>
public sealed record ReturnSearchRow(
    string SaleId,
    string? ReceiptNumber,
    int Position,
    decimal Total,
    DateTime Date,
    string? ClientName,
    string? ClientPhone = null,
    string? CardLast4 = null);

/// <summary>2026-11-13 (Л-5, «быстро из одного поля»): ОДНО поле поиска чека для возврата. Из набранного текста понимает: номер чека («1115», «№1115», «#1115»), сумму («3 200», «1500,50»),
/// дату («12.11», «12.11.26», «12.11.2026», «2026-11-12»), клиента (часть имени), телефон или карту (последние 4 и больше цифр). Чек подходит, если подошло ХОТЬ ОДНО из этого;
/// товар (название, штрихкод, артикул) проверяет вызывающий через <see cref="SaleProductQuery"/> и тоже «или». Чистая логика без окон — проверяется тестами.</summary>
public sealed class ReturnSearchQuery
{
    private static readonly string[] FullDateFormats = { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
    private static readonly string[] ShortDateFormats = { "dd.MM", "d.M", "dd/MM", "d/M" };

    private readonly string _text;
    private readonly string _trimmed; // без «№» и «#»
    private readonly long? _number;
    private readonly decimal? _amount;
    private readonly DateTime? _day;
    private readonly string _digits; // только цифры, если в тексте нет букв и цифр ≥ 4 (телефон / карта); иначе пусто
    private readonly bool _digitsOnly;

    public ReturnSearchQuery(string? text, DateTime now)
    {
        _text = (text ?? "").Trim();
        _trimmed = _text.TrimStart('№', '#').Trim();
        if (_text.Length == 0)
            return;
        _number = ParseNumber(_text);
        _amount = SaleListFilter.ParseAmount(_trimmed);
        _day = ParseDay(_text, now);
        var letters = _text.Any(char.IsLetter);
        var digits = new string(_text.Where(char.IsAsciiDigit).ToArray());
        _digitsOnly = !letters && digits.Length > 0 && _text.All(c => char.IsAsciiDigit(c) || c is ' ' or ' ' or '+' or '-' or '(' or ')' or '№' or '#' or '.' or ',' or '/'); // точка, запятая и косая — суммы и даты («1 500,50», «12.11»)
        _digits = _digitsOnly && digits.Length >= 4 ? digits : "";
    }

    public bool IsEmpty => _text.Length == 0;

    /// <summary>Текст целиком — число / сумма / дата / телефон (без букв): поиск по составу чеков (товар) нужен, только если такой чек не нашёлся.</summary>
    public bool LooksLikeNumbers => _digitsOnly;

    public long? Number => _number;
    public decimal? Amount => _amount;
    public DateTime? Day => _day;
    public string Digits => _digits;

    /// <summary>Подходит ли строка списка (без товара).</summary>
    public bool Matches(ReturnSearchRow row)
    {
        if (IsEmpty)
            return true;
        if (row.SaleId.Contains(_text, StringComparison.OrdinalIgnoreCase))
            return true;
        // 2026-10-29: «№1115» и «#1115» находят чек; 2026-09-28: «1115» и «001115» — один и тот же постоянный номер.
        if (!string.IsNullOrWhiteSpace(row.ReceiptNumber))
        {
            if (row.ReceiptNumber.Contains(_trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
            if (_number is { } wanted && ParseNumber(row.ReceiptNumber) == wanted)
                return true;
        }
        else if (string.Equals(_trimmed, row.Position.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return true;

        if (_amount is { } amount && Math.Abs(row.Total - amount) <= 0.004m)
            return true;
        if (_day is { } day && row.Date != DateTime.MinValue && row.Date.Date == day.Date)
            return true;

        if (_digits.Length >= 4)
        {
            var phone = DigitsOf(row.ClientPhone);
            if (phone.Length >= 4 && (phone.EndsWith(_digits, StringComparison.Ordinal) || (_digits.Length >= 7 && phone.Contains(_digits, StringComparison.Ordinal))))
                return true;
            var card = DigitsOf(row.CardLast4);
            if (card.Length >= 4 && (card.EndsWith(_digits, StringComparison.Ordinal) || (_digits.Length > 4 && _digits.EndsWith(card, StringComparison.Ordinal))))
                return true;
        }

        // Клиент: слова имени (и телефон) — только для текста с буквами; цифры телефона разобраны выше «по последним цифрам».
        if (!_digitsOnly && (!string.IsNullOrWhiteSpace(row.ClientName) || !string.IsNullOrWhiteSpace(row.ClientPhone))
            && SearchMatch.Matches(_text, row.ClientName, row.ClientPhone))
            return true;
        return false;
    }

    private static long? ParseNumber(string? text)
    {
        var t = (text ?? "").Trim().TrimStart('№', '#').Trim();
        return t.Length is > 0 and <= 12 && t.All(char.IsAsciiDigit) && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static string DigitsOf(string? s) => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsAsciiDigit).ToArray());

    /// <summary>Дата из текста: «12.11.2026», «12.11.26», «12.11» (год — текущий; если дата в будущем — прошлого года), «2026-11-12». Не дата — null.</summary>
    public static DateTime? ParseDay(string? text, DateTime now)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 3 || t.Length > 10)
            return null;
        if (DateTime.TryParseExact(t, FullDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var full) && full.Year >= 2000)
            return full.Date;
        if (DateTime.TryParseExact(t, ShortDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var shortDate))
        {
            var d = new DateTime(now.Year, shortDate.Month, shortDate.Day);
            return d.Date > now.Date ? d.AddYears(-1).Date : d.Date;
        }
        return null;
    }
}

/// <summary>2026-11-13 (Л-5): строка отчёта «Возвраты по причинам и кассирам» — один возврат (причина, кассир, сумма).</summary>
public sealed record ReturnReasonRow(string? Reason, string? Cashier, decimal Amount);

/// <summary>Группа отчёта: причина или кассир, число возвратов и сумма.</summary>
public sealed record ReturnGroup(string Name, int Count, decimal Sum);

/// <summary>2026-11-13 (Л-5, программа владельца): возвраты периода по причинам и по кассирам. Причины сравниваются без регистра и лишних пробелов («Брак» = «брак »); пустая причина —
/// отдельная строка «без причины». Данные — возвраты сервера (GET pos/returns/): сумма, кассир, причина; аннулированные вызывающий не передаёт.</summary>
public static class ReturnReasonReport
{
    public static (IReadOnlyList<ReturnGroup> ByReason, IReadOnlyList<ReturnGroup> ByCashier) Build(IEnumerable<ReturnReasonRow> rows, string noReason, string noCashier)
    {
        var list = rows.ToList();
        return (Group(list, r => Clean(r.Reason, noReason), noReason), Group(list, r => Clean(r.Cashier, noCashier), noCashier));
    }

    private static IReadOnlyList<ReturnGroup> Group(List<ReturnReasonRow> rows, Func<ReturnReasonRow, string> key, string empty) =>
        rows.GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReturnGroup(g.Key, g.Count(), g.Sum(r => Math.Abs(r.Amount))))
            .OrderByDescending(g => g.Sum).ThenByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Подпись группы: пробелы схлопнуты, первая строка, не длиннее 60 знаков; пусто — <paramref name="empty"/>.</summary>
    public static string Clean(string? text, string empty)
    {
        var t = (text ?? "").Split('\n', '\r').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        t = string.Join(' ', t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length == 0)
            return empty;
        return t.Length > 60 ? t[..60].TrimEnd() + "…" : t;
    }
}
