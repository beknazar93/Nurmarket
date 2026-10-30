using System.Globalization;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

// 2026-10-30, просьба владельца «нумерация для накладных в закупках»: ВРЕМЕННАЯ клиентская нумерация документов закупок, пока сервер NurCRM не даёт
// своего номера (ТЗ Умару, пункт 13). Чистая логика без файлов и сети: формат номера, разбор, детерминированная раскладка порядковых номеров.
// Правило: номер = ТИП-ГОД-ПОРЯДОК, порядок считается по серверной истории (дата создания, затем id) отдельно по серии и году — поэтому любой ПК
// с той же историей получает те же номера. Номер, записанный в документ на сервере («№ ПР-2026-000123» в комментарии), закрепляется за документом.

/// <summary>Серия документов закупок.</summary>
public enum DocSeries
{
    /// <summary>Приход от поставщика (ПР).</summary>
    Receipt,
    /// <summary>Возврат поставщику (ВП).</summary>
    Return,
    /// <summary>Заказ поставщику (ЗП).</summary>
    Order,
}

/// <summary>Настройки нумерации (общие для ПК; префиксы и ширина должны совпадать на всех кассах компании, иначе номера выглядят по-разному).</summary>
public sealed class DocNumberingSettings
{
    public string PrefixReceipt { get; set; } = "ПР";
    public string PrefixReturn { get; set; } = "ВП";
    public string PrefixOrder { get; set; } = "ЗП";
    /// <summary>Сбрасывать порядковый номер каждый год (по умолчанию да). Нет — сквозная нумерация, год в номере — год создания.</summary>
    public bool ResetByYear { get; set; } = true;
    /// <summary>Сколько цифр в порядковом номере (4–9).</summary>
    public int Width { get; set; } = 6;
    /// <summary>2026-10-30: «Показывать наценку в печатной накладной» — для печати поставщику по умолчанию ВЫКЛ.</summary>
    public bool PrintMarkup { get; set; }
    /// <summary>Внутренняя копия (PDF «Скачать») — наценка по умолчанию ВКЛ (только у ролей с правом на закупочные цены).</summary>
    public bool PdfMarkup { get; set; } = true;

    public string Prefix(DocSeries s) => s switch { DocSeries.Return => PrefixReturn, DocSeries.Order => PrefixOrder, _ => PrefixReceipt };

    /// <summary>Привести значения к допустимым (пустой префикс → по умолчанию, ширина 4–9, префикс до 6 знаков без дефисов и пробелов).</summary>
    public DocNumberingSettings Normalized()
    {
        static string P(string? v, string def)
        {
            var s = new string((v ?? "").Trim().Where(c => char.IsLetterOrDigit(c)).ToArray());
            return s.Length == 0 ? def : (s.Length > 6 ? s[..6] : s).ToUpperInvariant();
        }
        return new DocNumberingSettings
        {
            PrefixReceipt = P(PrefixReceipt, "ПР"), PrefixReturn = P(PrefixReturn, "ВП"), PrefixOrder = P(PrefixOrder, "ЗП"),
            ResetByYear = ResetByYear, Width = Math.Clamp(Width, 4, 9), PrintMarkup = PrintMarkup, PdfMarkup = PdfMarkup,
        };
    }
}

/// <summary>Документ для раскладки номеров: id, дата создания на сервере и закреплённый за ним порядковый номер (из комментария на сервере или
/// из резерва этой кассы), если есть. ClaimYear — год, указанный в закреплённом номере (должен совпасть с годом документа).</summary>
public sealed record DocEntry(string Id, DateTime At, int ClaimYear = 0, int Claim = 0, string ClaimPrefix = "");

/// <summary>Номер документа: серия, год, порядок; Draft — временный («ч», офлайн-черновик). Prefix — префикс, с которым номер был присвоен
/// (2026-10-30: смена префикса в настройках не меняет номера старых документов); null — текущий префикс серии.</summary>
public readonly record struct DocNumber(DocSeries Series, int Year, int Seq, bool Draft = false, string? Prefix = null);

public static class DocumentNumberingCore
{
    /// <summary>«ПР-2026-000123»; черновик — «ПР-2026-ч123».</summary>
    public static string Format(DocNumberingSettings st, DocNumber n)
    {
        var prefix = string.IsNullOrEmpty(n.Prefix) ? st.Prefix(n.Series) : n.Prefix;
        var seq = n.Draft ? "ч" + n.Seq.ToString(CultureInfo.InvariantCulture) : n.Seq.ToString("D" + st.Width, CultureInfo.InvariantCulture);
        return $"{prefix}-{n.Year.ToString("D4", CultureInfo.InvariantCulture)}-{seq}";
    }

    private static readonly Regex Marker = new(@"№\s*([^\s\-№]{1,8})-(\d{4})-(\d{1,9})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Найти закреплённый номер серии в тексте (комментарий прихода на сервере: «№ ПР-2026-000123»). Чужая серия и черновик («ч») не считаются.</summary>
    public static (int Year, int Seq)? ClaimFromText(DocNumberingSettings st, DocSeries series, string? text) =>
        ClaimWithPrefixFromText(st, series, text) is { } c ? (c.Year, c.Seq) : null;

    /// <summary>2026-10-30 (приёмка 1.17.66): то же, но с префиксом из номера — он хранится с документом, поэтому смена префикса в настройках
    /// не перенумеровывает старые документы. Подходит текущий префикс серии либо прежний (не равный префиксу другой серии и не из одних цифр).</summary>
    public static (int Year, int Seq, string Prefix)? ClaimWithPrefixFromText(DocNumberingSettings st, DocSeries series, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var others = new List<string>();
        foreach (var o in new[] { DocSeries.Receipt, DocSeries.Return, DocSeries.Order })
            if (o != series)
            {
                others.Add(st.Prefix(o));
                others.Add(o == DocSeries.Receipt ? "ПР" : o == DocSeries.Return ? "ВП" : "ЗП");
            }
        foreach (Match m in Marker.Matches(text))
        {
            var prefix = m.Groups[1].Value;
            var current = string.Equals(prefix, st.Prefix(series), StringComparison.OrdinalIgnoreCase);
            if (!current && (others.Any(o => string.Equals(o, prefix, StringComparison.OrdinalIgnoreCase)) || !prefix.Any(char.IsLetter)))
                continue;
            if (int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                && int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0)
                return (y, s, prefix.ToUpperInvariant());
        }
        return null;
    }

    private static readonly Regex SupplierDoc = new(@"накладная поставщика:\s*(?<n>[^;]+?)(?:\s+от\s+(?<d>\d{2}\.\d{2}\.\d{4}))?\s*(?:;|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Номер и дата накладной поставщика из служебного комментария прихода («…; накладная поставщика: 45 от 29.10.2026»). Нет — пусто.</summary>
    public static (string Number, DateTime? Date) SupplierDocFromText(string? text)
    {
        if (string.IsNullOrEmpty(text) || SupplierDoc.Match(text) is not { Success: true } m)
            return ("", null);
        DateTime? date = DateTime.TryParseExact(m.Groups["d"].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return (m.Groups["n"].Value.Trim(), date);
    }

    /// <summary>Разобрать номер, набранный человеком: «ПР-2026-123», «пр 2026 000123», «2026-123», «123», «000123». year = 0 — год не указан,
    /// series = null — серия не указана. Ничего похожего — null.</summary>
    public static (DocSeries? Series, int Year, int Seq)? ParseQuery(DocNumberingSettings st, string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return null;
        var m = Regex.Match(q, @"^\s*(?:№\s*)?(?:(?<p>[^\W\d_]{1,6})[\s\-_]*)?(?:(?<y>\d{4})[\s\-_]+)?(?<s>\d{1,9})\s*$", RegexOptions.CultureInvariant);
        if (!m.Success)
            return null;
        DocSeries? series = null;
        if (m.Groups["p"].Success)
        {
            var p = m.Groups["p"].Value;
            foreach (var s in new[] { DocSeries.Receipt, DocSeries.Return, DocSeries.Order })
                if (string.Equals(st.Prefix(s), p, StringComparison.OrdinalIgnoreCase))
                    series = s;
            if (series is null)
                return null;
        }
        var year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : 0;
        var seq = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        return (series, year, seq);
    }

    /// <summary>Подходит ли номер документа под набранный запрос (серия/год — если указаны; порядок — равенство числа, а запрос из 3+ цифр — ещё и по части числа,
    /// чтобы «000123», «123» и «23» находили ПР-2026-000123).</summary>
    public static bool Matches(DocNumberingSettings st, DocNumber n, string? query)
    {
        if (ParseQuery(st, query) is not { } q)
            return false;
        if (q.Series is { } s && s != n.Series)
            return false;
        if (q.Year != 0 && q.Year != n.Year)
            return false;
        var digits = n.Seq.ToString(CultureInfo.InvariantCulture);
        var typed = q.Seq.ToString(CultureInfo.InvariantCulture);
        return n.Seq == q.Seq || digits.Contains(typed, StringComparison.Ordinal);
    }

    /// <summary>
    /// Раскладка порядковых номеров. Документы сортируются по (дата создания, id по символам) отдельно по годам (при сбросе по годам) — результат
    /// не зависит от порядка входного списка. Закреплённый за документом номер (Claim) сохраняется, если он ещё не занят более ранним документом
    /// и год совпадает; при коллизии (две кассы взяли один номер) закрепление сохраняет более ранний документ, поздний получает следующий свободный
    /// (id таких документов — в <paramref name="conflicts"/>). Документ без закрепления получает «предыдущий в порядке + 1», пропуская занятые;
    /// для истории без закреплённых номеров это просто порядковый номер 1, 2, 3…
    /// </summary>
    public static Dictionary<string, int> Assign(IEnumerable<DocEntry> entries, bool resetByYear, List<string>? conflicts = null)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = entries.Where(e => e.Id.Length > 0).GroupBy(e => resetByYear ? e.At.Year : 0);
        foreach (var g in groups)
        {
            var sorted = g.OrderBy(e => e.At).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            var used = new HashSet<int>();
            var accepted = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in sorted)
            {
                if (e.Claim <= 0)
                    continue;
                // год в закреплённом номере должен быть годом документа; при сквозной нумерации — любой (номер сквозной)
                var yearOk = !resetByYear || e.ClaimYear == 0 || e.ClaimYear == e.At.Year;
                if (yearOk && used.Add(e.Claim))
                    accepted[e.Id] = e.Claim;
                else
                    conflicts?.Add(e.Id);
            }
            var prev = 0;
            foreach (var e in sorted)
            {
                int seq;
                if (accepted.TryGetValue(e.Id, out var claimed))
                    seq = claimed;
                else
                {
                    seq = prev + 1;
                    while (used.Contains(seq))
                        seq++;
                    used.Add(seq);
                }
                result[e.Id] = seq;
                prev = seq;
            }
        }
        return result;
    }
}
