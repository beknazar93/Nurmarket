using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-05 (функция 13 «Сверка банковской выписки», идея Invoice Ninja bank rules / ERPNext Bank Reconciliation Tool — лицензии чужие, код не заимствован):
// выписка банка (Excel/CSV MBank, Optima и др.) → разбор колонок (дата, сумма, описание, вход/исход) → сопоставление: безнал/QR по сменам (по дню и сумме),
// оплаты поставщикам (по сумме и названию), остальное — «расход?» / «поступление?» с правилом «запомнить» (BankRulesStore). Всё в этом файле — чистая логика без окон
// и без сети (данные смен и поставщиков приносит окно); тесты — BankReconcileTests.

/// <summary>Строка выписки: <see cref="Amount"/> со знаком — плюс поступление, минус списание.</summary>
public sealed record BankLine(int Row, DateTime Date, double Amount, string Description);

public sealed record BankStatement(IReadOnlyList<BankLine> Lines, IReadOnlyList<string> Warnings, string Columns);

/// <summary>Безналичная выручка смены (noncash_sales_total): день закрытия смены, сумма, число чеков.</summary>
public sealed record ShiftCashless(string Id, DateTime Day, double Amount, int Receipts, string Label);

/// <summary>Исходящая оплата поставщику, известная кассе (приход от поставщика / оплата сделки): дата, сумма, название.</summary>
public sealed record SupplierOutgoing(DateTime Date, double Amount, string Supplier);

public sealed record BankRule(string Pattern, string Category);

public enum BankLineKind
{
    /// <summary>Поступление совпало со сменой/днём точно.</summary>
    SalesMatched,
    /// <summary>Поступление чуть меньше выручки дня (комиссия банка/эквайринга до 3 %).</summary>
    SalesWithFee,
    /// <summary>Списание совпало с оплатой поставщику по сумме.</summary>
    SupplierPayment,
    /// <summary>Сумма не нашлась, но в описании название поставщика.</summary>
    SupplierByName,
    /// <summary>Подошло сохранённое правило («запомнить»).</summary>
    Rule,
    /// <summary>Списание непонятное — «расход?».</summary>
    UnknownExpense,
    /// <summary>Поступление, не связанное со сменами — «поступление?».</summary>
    UnknownIncome,
}

public sealed record BankLineResult(BankLine Line, BankLineKind Kind, IReadOnlyList<string> ShiftIds, string? Supplier, string? Category);

public sealed record BankReconcileResult(
    IReadOnlyList<BankLineResult> Lines,
    IReadOnlyList<ShiftCashless> ShiftsNotFound,
    double KassaTotal,
    double BankTotal,
    int ReceiptsNotFound)
{
    /// <summary>Касса минус банк (положительная — банк получил меньше, чем записала касса).</summary>
    public double Difference => Math.Round(KassaTotal - BankTotal, 2);
    public int Count(BankLineKind kind) => Lines.Count(l => l.Kind == kind);
}

/// <summary>Итог за месяц: «безнал по кассе X, по банку Y, разница Z — N чеков» (<see cref="BankReconciler.Summarize"/>).</summary>
public sealed record BankMonthSummary(double Kassa, double Bank, double Difference, int ShiftsNotFound, int ReceiptsNotFound);

public static class BankCategories
{
    public static readonly string[] Codes = ["rent", "salary", "tax", "utilities", "ads", "bank_fee", "other_expense", "not_sales"];

    public static string Title(string? code) => code switch
    {
        "rent" => Tr.T("Аренда", "Ижара", "Rent", "Kira", "Ijara"),
        "salary" => Tr.T("Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi"),
        "tax" => Tr.T("Налоги", "Салыктар", "Taxes", "Vergiler", "Soliqlar"),
        "utilities" => Tr.T("Коммунальные", "Коммуналдык", "Utilities", "Faturalar", "Kommunal"),
        "ads" => Tr.T("Реклама", "Жарнама", "Advertising", "Reklam", "Reklama"),
        "bank_fee" => Tr.T("Комиссия банка", "Банк комиссиясы", "Bank fee", "Banka komisyonu", "Bank komissiyasi"),
        "other_expense" => Tr.T("Прочий расход", "Башка чыгым", "Other expense", "Diğer gider", "Boshqa xarajat"),
        "not_sales" => Tr.T("Не от продаж (перевод, заём)", "Сатуудан эмес (которуу, насыя)", "Not from sales (transfer, loan)", "Satış değil (havale, kredi)", "Sotuvdan emas (o'tkazma, qarz)"),
        _ => Tr.T("не указано", "көрсөтүлгөн эмес", "not set", "belirtilmedi", "ko'rsatilmagan"),
    };
}

public static class BankStatementParser
{
    private static readonly string[] DateKeys = ["дата операции", "дата проводки", "дата платежа", "дата", "date", "күнү", "datum", "tarih"];
    private static readonly string[] DescKeys = ["назначение", "описание", "детали", "примечание", "комментарий", "наименование операции", "details", "description", "purpose", "narrative", "сообщение", "контрагент", "получатель", "плательщик", "мазмуну", "аныктама"];
    private static readonly string[] AmountKeys = ["сумма операции", "сумма", "amount", "сумма платежа", "summa"];
    private static readonly string[] DebitKeys = ["дебет", "debit", "расход", "списание", "исходящ", "withdrawal", "чыгым", "снятие"];
    private static readonly string[] CreditKeys = ["кредит", "credit", "приход", "зачисление", "поступление", "входящ", "deposit", "кирешe", "киреше", "пополнение"];
    private static readonly string[] DirectionKeys = ["тип операции", "тип", "направление", "вид операции", "вид", "dr/cr", "d/c", "type"];

    private static string Norm(string? s) => (s ?? "").Replace(' ', ' ').Trim().ToLowerInvariant().Replace('ё', 'е');

    private static int FindColumn(string[] headers, string[] keys, params int[] exclude)
    {
        // Ключи идут от более точного к общему: «дата операции» важнее просто «дата».
        foreach (var key in keys)
            for (var c = 0; c < headers.Length; c++)
                if (!exclude.Contains(c) && headers[c].Length > 0 && headers[c].Contains(key, StringComparison.Ordinal))
                    return c;
        return -1;
    }

    /// <summary>Разобрать таблицу выписки (все строки файла, включая шапку банка). Заголовок ищется среди первых 40 строк по словам «дата» + «сумма»/«дебет»/«кредит».
    /// Нераспознанные строки (итоги, пустые, без даты или суммы) пропускаются с предупреждением в общем счёте.</summary>
    public static BankStatement Parse(IReadOnlyList<string[]> rows)
    {
        var warnings = new List<string>();
        var headerAt = -1;
        int dateCol = -1, descCol = -1, amountCol = -1, debitCol = -1, creditCol = -1, dirCol = -1;
        for (var i = 0; i < Math.Min(rows.Count, 40); i++)
        {
            var h = rows[i].Select(Norm).ToArray();
            var d = FindColumn(h, DateKeys);
            if (d < 0)
                continue;
            var debit = FindColumn(h, DebitKeys, d);
            var credit = FindColumn(h, CreditKeys, d, debit);
            var amount = FindColumn(h, AmountKeys, d, debit, credit);
            if (amount < 0 && debit < 0 && credit < 0)
                continue; // нужна либо «Сумма», либо «Дебет»/«Кредит»
            headerAt = i;
            dateCol = d;
            debitCol = debit;
            creditCol = credit;
            amountCol = amount;
            descCol = FindColumn(h, DescKeys, d, debit, credit, amount);
            dirCol = FindColumn(h, DirectionKeys, d, debit, credit, amount, descCol);
            break;
        }
        if (headerAt < 0)
        {
            warnings.Add(Tr.T("Не нашла строку заголовков (нужны колонки «Дата» и «Сумма» или «Дебет/Кредит»).", "Баш сап табылган жок («Күнү» жана «Сумма» же «Дебет/Кредит» мамычалары керек).",
                "Could not find the header row (columns “Date” and “Amount” or “Debit/Credit” are required).", "Başlık satırı bulunamadı («Tarih» ve «Tutar» ya da «Borç/Alacak» sütunları gerekli).", "Sarlavha qatori topilmadi («Sana» va «Summa» yoki «Debet/Kredit» ustunlari kerak)."));
            return new BankStatement([], warnings, "");
        }

        var lines = new List<BankLine>();
        var skipped = 0;
        for (var i = headerAt + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Cell(int c) => c >= 0 && c < r.Length ? (r[c] ?? "").Trim() : "";
            if (!TryParseDate(Cell(dateCol), out var date))
            {
                if (r.Any(x => !string.IsNullOrWhiteSpace(x)))
                    skipped++;
                continue;
            }
            double? signed = null;
            if (debitCol >= 0 || creditCol >= 0)
            {
                var debit = TryParseAmount(Cell(debitCol), out var dv) ? Math.Abs(dv) : 0;
                var credit = TryParseAmount(Cell(creditCol), out var cv) ? Math.Abs(cv) : 0;
                if (debit > 0 || credit > 0)
                    signed = credit - debit;
            }
            else if (TryParseAmount(Cell(amountCol), out var a))
            {
                signed = a;
                // Одна колонка суммы без знака + колонка направления («Приход/Расход», «Dr/Cr», «+/−»).
                if (a >= 0 && dirCol >= 0 && IsOutgoing(Cell(dirCol)))
                    signed = -a;
            }
            if (signed is not { } amount || Math.Abs(amount) < 0.005)
            {
                skipped++;
                continue;
            }
            lines.Add(new BankLine(i + 1, date.Date, Math.Round(amount, 2), Cell(descCol)));
        }
        if (skipped > 0)
            warnings.Add(Tr.T($"Пропущено строк без даты или суммы: {skipped}.", $"Күнү же суммасы жок саптар өткөрүлдү: {skipped}.", $"Rows without a date or amount skipped: {skipped}.",
                $"Tarihi veya tutarı olmayan {skipped} satır atlandı.", $"Sanasi yoki summasi yo'q qatorlar o'tkazib yuborildi: {skipped}."));
        if (lines.Count == 0)
            warnings.Add(Tr.T("В файле нет операций.", "Файлда операциялар жок.", "There are no transactions in the file.", "Dosyada işlem yok.", "Faylda operatsiyalar yo'q."));
        var headers = rows[headerAt];
        string Name(int c) => c >= 0 && c < headers.Length ? headers[c].Trim() : "—";
        var columns = amountCol >= 0
            ? $"{Name(dateCol)} | {Name(amountCol)}{(dirCol >= 0 ? " + " + Name(dirCol) : "")} | {Name(descCol)}"
            : $"{Name(dateCol)} | {Name(debitCol)} / {Name(creditCol)} | {Name(descCol)}";
        return new BankStatement(lines, warnings, columns);
    }

    private static bool IsOutgoing(string value)
    {
        var v = Norm(value);
        if (v.Length == 0)
            return false;
        return v is "-" or "−" or "dr" or "d" or "debit" || v.Contains("расход", StringComparison.Ordinal) || v.Contains("списан", StringComparison.Ordinal) || v.Contains("исход", StringComparison.Ordinal)
            || v.Contains("debit", StringComparison.Ordinal) || v.Contains("дебет", StringComparison.Ordinal) || v.Contains("withdraw", StringComparison.Ordinal) || v.Contains("чыгым", StringComparison.Ordinal);
    }

    private static readonly string[] DateFormats =
    [
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy H:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm",
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "dd/MM/yyyy HH:mm:ss", "dd-MM-yyyy", "yyyy/MM/dd",
        "dd.MM.yyyy hh:mm:ss tt",
    ];

    public static bool TryParseDate(string? text, out DateTime date)
    {
        date = default;
        var t = (text ?? "").Replace(' ', ' ').Trim();
        if (t.Length == 0)
            return false;
        if (DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        // Дата Excel числом (серийный номер): 45000 ≈ 2023 год.
        if (double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 30000 and < 80000)
        {
            date = DateTime.FromOADate(Math.Floor(serial));
            return true;
        }
        // Дата в начале строки с лишним текстом («05.11.2026 Оплата…») — берём первые 10 символов.
        if (t.Length > 10 && DateTime.TryParseExact(t[..10], ["dd.MM.yyyy", "yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        return false;
    }

    /// <summary>«1 234,56», «1,234.56», «−1 234,56», «(500)», «500-», «12.5», «1234» → число. Пусто/текст → false.</summary>
    public static bool TryParseAmount(string? text, out double value)
    {
        value = 0;
        var t = (text ?? "").Replace(' ', ' ').Replace(" ", "").Replace(" ", "").Replace('−', '-').Trim();
        if (t.Length == 0)
            return false;
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')'))
        {
            negative = true;
            t = t[1..^1];
        }
        if (t.EndsWith('-'))
        {
            negative = true;
            t = t[..^1];
        }
        if (t.StartsWith('-'))
        {
            negative = !negative;
            t = t[1..];
        }
        if (t.StartsWith('+'))
            t = t[1..];
        t = t.Replace("сом", "", StringComparison.OrdinalIgnoreCase).Replace("kgs", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (t.Length == 0 || !t.All(ch => char.IsDigit(ch) || ch is ',' or '.'))
            return false;
        var lastDot = t.LastIndexOf('.');
        var lastComma = t.LastIndexOf(',');
        string normalized;
        if (lastDot >= 0 && lastComma >= 0)
        {
            // Оба разделителя: последний — десятичный, другой — разряды («1,234.56», «1.234,56»).
            var decimalSep = lastDot > lastComma ? '.' : ',';
            var groupSep = decimalSep == '.' ? ',' : '.';
            normalized = t.Replace(groupSep.ToString(), "").Replace(decimalSep, '.');
        }
        else if (lastDot < 0 && lastComma < 0)
            normalized = t;
        else
        {
            var sep = lastDot >= 0 ? '.' : ',';
            var at = Math.Max(lastDot, lastComma);
            var count = t.Count(ch => ch == sep);
            var digitsAfter = t.Length - at - 1;
            var before = t[..at];
            // «1.234.567» — только разряды; «1,234» (запятая, три цифры, слева 1–3 цифры не «0») — разряды; остальное — дробная часть.
            var grouping = count > 1 || (sep == ',' && digitsAfter == 3 && before.Length is >= 1 and <= 3 && before != "0");
            normalized = grouping ? t.Replace(sep.ToString(), "") : t.Replace(sep, '.');
        }
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        if (negative)
            value = -value;
        return true;
    }
}

public static class BankRules
{
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "оплата", "платеж", "платёж", "перевод", "согласно", "договор", "договору", "счет", "счету", "счёт", "услуги", "услуг", "товар", "товары", "для", "без", "ндс", "ооо", "осоо", "ип",
        "the", "for", "payment", "transfer", "invoice", "kgs", "сом", "номер", "дата", "от", "по", "за",
    };

    /// <summary>Нижний регистр, «ё»→«е», всё кроме букв — пробелы (цифры, даты и номера не мешают совпадению), слова через один пробел.</summary>
    public static string Normalize(string? text)
    {
        var sb = new StringBuilder();
        var space = true;
        foreach (var ch in (text ?? "").ToLowerInvariant())
        {
            var c = ch == 'ё' ? 'е' : ch;
            if (char.IsLetter(c))
            {
                sb.Append(c);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Шаблон правила «запомнить»: до трёх значимых слов описания (без служебных «оплата», «договор»…). Пусто — запоминать нечего.</summary>
    public static string MakePattern(string? description)
    {
        var words = Normalize(description).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !Noise.Contains(w)).Take(3);
        return string.Join(' ', words);
    }

    private static readonly HashSet<string> LegalForms = new(StringComparer.Ordinal) { "осоо", "ооо", "ип", "ао", "зао", "тоо", "llc", "ltd" };

    /// <summary>Ключ названия поставщика для поиска в описании платежа: без «ОсОО/ООО/ИП» и т. п. Короче 4 букв — пусто (слишком общее).</summary>
    public static string NameKey(string? name)
    {
        var key = string.Join(' ', Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !LegalForms.Contains(w)));
        return key.Length >= 4 ? key : "";
    }

    public static BankRule? Find(IEnumerable<BankRule> rules, string? description)
    {
        var n = Normalize(description);
        if (n.Length == 0)
            return null;
        BankRule? best = null;
        foreach (var r in rules)
            if (r.Pattern.Length > 0 && n.Contains(r.Pattern, StringComparison.Ordinal) && (best is null || r.Pattern.Length > best.Pattern.Length))
                best = r;
        return best;
    }
}

public static class BankReconciler
{
    /// <summary>Допуск по сумме (сом) при точном совпадении.</summary>
    public const double AmountTolerance = 0.5;

    /// <summary>Комиссия банка/эквайринга: поступление может быть меньше выручки дня не более чем на столько (доля).</summary>
    public const double MaxFeeShare = 0.03;

    /// <summary>Сколько дней после смены банк может зачислить деньги.</summary>
    public const int CreditDelayDays = 5;

    /// <summary>2026-11-05 (приёмка волн 0–3, С-4): месяц выписки по датам строк — тот, где больше всего операций (при равенстве — более поздний); первое число месяца. null — строк нет.
    /// Окно сверки открывается на этом месяце, а не на «прошлом/текущем», иначе выписка за ноябрь сверялась с октябрём и давала ложную разницу.</summary>
    public static DateTime? DominantMonth(IEnumerable<BankLine> lines)
    {
        var top = lines.GroupBy(l => new DateTime(l.Date.Year, l.Date.Month, 1)).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).FirstOrDefault();
        return top?.Key;
    }

    /// <summary>Сколько дней после прихода/оплаты в кассе банк может показать списание поставщику.</summary>
    public const int SupplierWindowDays = 45;

    /// <summary>Итог за период [from, to]. Касса — безнал смен периода. Банк — поступления, сопоставленные со сменами периода, плюс несопоставленные поступления
    /// с датой в периоде (кроме помеченных «не от продаж»). Зачисление за смены прошлого месяца, пришедшее в начале этого, в банк периода не входит.
    /// «Не найдено в банке» — смены периода без поступления; чеки — по числу чеков смен (нет данных — по одному на смену).</summary>
    public static BankMonthSummary Summarize(BankReconcileResult result, IReadOnlyList<ShiftCashless> shifts, DateTime from, DateTime to)
    {
        var inMonth = shifts.Where(s => s.Amount > 0.005 && s.Day.Date >= from.Date && s.Day.Date <= to.Date).ToList();
        var ids = new HashSet<string>(inMonth.Select(s => s.Id), StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var bank = 0.0;
        foreach (var r in result.Lines.Where(r => r.Line.Amount > 0 && r.Category != "not_sales"))
        {
            if (r.Kind is BankLineKind.SalesMatched or BankLineKind.SalesWithFee)
            {
                var own = r.ShiftIds.Where(ids.Contains).ToList();
                if (own.Count == 0)
                    continue;
                foreach (var id in own)
                    matched.Add(id);
                bank += r.Line.Amount;
            }
            else if (r.Line.Date.Date >= from.Date && r.Line.Date.Date <= to.Date)
                bank += r.Line.Amount;
        }
        var kassa = Math.Round(inMonth.Sum(s => s.Amount), 2);
        var missing = inMonth.Where(s => !matched.Contains(s.Id)).ToList();
        bank = Math.Round(bank, 2);
        return new BankMonthSummary(kassa, bank, Math.Round(kassa - bank, 2), missing.Count, missing.Sum(s => Math.Max(s.Receipts, 1)));
    }

    private sealed class DayGroup
    {
        public DateTime Day;
        public List<ShiftCashless> Shifts = new();
        public double Total;
        public bool Matched;
    }

    public static BankReconcileResult Reconcile(IReadOnlyList<BankLine> bank, IReadOnlyList<ShiftCashless> shifts, IReadOnlyList<SupplierOutgoing> supplierPayments,
        IReadOnlyList<string> supplierNames, IReadOnlyList<BankRule> rules)
    {
        var results = new Dictionary<BankLine, BankLineResult>();
        var ordered = bank.OrderBy(l => l.Date).ThenBy(l => l.Row).ToList();

        // 1. Что в банке уже понятно по сохранённым правилам «не от продаж» — такие поступления в сверку смен не идут.
        var incoming = new List<BankLine>();
        foreach (var l in ordered.Where(l => l.Amount > 0))
        {
            if (BankRules.Find(rules, l.Description) is { Category: "not_sales" } rule)
                results[l] = new BankLineResult(l, BankLineKind.Rule, [], null, rule.Category);
            else
                incoming.Add(l);
        }

        // 2. Смены → дни.
        var groups = shifts.Where(s => s.Amount > 0.005).GroupBy(s => s.Day.Date).OrderBy(g => g.Key)
            .Select(g => new DayGroup { Day = g.Key, Shifts = g.ToList(), Total = Math.Round(g.Sum(s => s.Amount), 2) }).ToList();
        var singles = groups.SelectMany(g => g.Shifts.Select(s => (Group: g, Shift: s))).ToList();
        var matchedShiftIds = new HashSet<string>(StringComparer.Ordinal);
        var free = new List<BankLine>(incoming);

        bool InWindow(BankLine l, DateTime day) { var d = (l.Date.Date - day.Date).Days; return d >= 0 && d <= CreditDelayDays; }
        void Take(BankLine l, BankLineKind kind, IEnumerable<ShiftCashless> by)
        {
            var ids = by.Select(s => s.Id).ToList();
            foreach (var id in ids)
                matchedShiftIds.Add(id);
            results[l] = new BankLineResult(l, kind, ids, null, null);
            free.Remove(l);
        }

        // 2а. Точно: сумма дня.
        foreach (var l in incoming.ToList())
        {
            var g = groups.Where(x => !x.Matched && Math.Abs(x.Total - l.Amount) <= AmountTolerance && InWindow(l, x.Day)).OrderBy(x => (l.Date.Date - x.Day).Days).FirstOrDefault();
            if (g is null)
                continue;
            g.Matched = true;
            Take(l, BankLineKind.SalesMatched, g.Shifts);
        }
        // 2б. Точно: одна смена (когда банк зачисляет по сменам, а не по дням).
        foreach (var l in free.ToList())
        {
            var hit = singles.Where(x => !x.Group.Matched && !matchedShiftIds.Contains(x.Shift.Id) && Math.Abs(x.Shift.Amount - l.Amount) <= AmountTolerance && InWindow(l, x.Group.Day))
                .OrderBy(x => (l.Date.Date - x.Group.Day).Days).Select(x => x.Shift).FirstOrDefault();
            if (hit is null)
                continue;
            Take(l, BankLineKind.SalesMatched, [hit]);
        }
        // Остаток дня: смены, которые ещё не сопоставлены (часть смен дня могла уйти по 2б).
        IEnumerable<ShiftCashless> RemShifts(DayGroup g) => g.Shifts.Where(x => !matchedShiftIds.Contains(x.Id));
        double Rem(DayGroup g) => Math.Round(RemShifts(g).Sum(x => x.Amount), 2);

        // 2в. Несколько дней одним зачислением (выходные, праздники): подряд идущие дни, сумма сходится.
        foreach (var l in free.ToList())
        {
            var open = groups.Where(g => Rem(g) > 0.005).ToList();
            for (var start = 0; start < open.Count; start++)
            {
                var sum = 0.0;
                var found = false;
                for (var len = 1; len <= 7 && start + len <= open.Count; len++)
                {
                    sum += Rem(open[start + len - 1]);
                    if (len >= 2 && Math.Abs(sum - l.Amount) <= AmountTolerance && InWindow(l, open[start + len - 1].Day))
                    {
                        var run = open.Skip(start).Take(len).ToList();
                        Take(l, BankLineKind.SalesMatched, run.SelectMany(RemShifts).ToList());
                        found = true;
                        break;
                    }
                }
                if (found)
                    break;
            }
        }
        // 2г. С комиссией: поступление чуть меньше выручки дня.
        foreach (var l in free.ToList())
        {
            var g = groups.Where(x => Rem(x) > 0.005 && l.Amount < Rem(x) - AmountTolerance && l.Amount >= Rem(x) * (1 - MaxFeeShare) && InWindow(l, x.Day))
                .OrderBy(x => (l.Date.Date - x.Day).Days).FirstOrDefault();
            if (g is null)
                continue;
            Take(l, BankLineKind.SalesWithFee, RemShifts(g).ToList());
        }

        // 3. Поступления без смены.
        foreach (var l in free.ToList())
        {
            if (BankRules.Find(rules, l.Description) is { } rule)
                results[l] = new BankLineResult(l, BankLineKind.Rule, [], null, rule.Category);
            else
                results[l] = new BankLineResult(l, BankLineKind.UnknownIncome, [], null, null);
        }

        // 4. Списания: оплаты поставщикам по сумме → правила → название поставщика → «расход?».
        var supplierPool = supplierPayments.ToList();
        var normalizedNames = supplierNames.Select(n => (Name: n, Key: BankRules.NameKey(n))).Where(x => x.Key.Length > 0).ToList();
        foreach (var l in ordered.Where(l => l.Amount < 0))
        {
            var amount = -l.Amount;
            var lineKey = BankRules.Normalize(l.Description);
            var candidates = supplierPool.Where(p => Math.Abs(p.Amount - amount) <= AmountTolerance && (l.Date.Date - p.Date.Date).Days is >= -2 and <= SupplierWindowDays).ToList();
            if (candidates.Count > 0)
            {
                // Из нескольких одинаковых по сумме — тот, чьё название встречается в описании, иначе ближайший по дате.
                var pick = candidates.Where(p => { var k = BankRules.NameKey(p.Supplier); return k.Length > 0 && lineKey.Contains(k, StringComparison.Ordinal); }).OrderByDescending(p => BankRules.NameKey(p.Supplier).Length).FirstOrDefault() // 2026-11-05 (Н-8): самое длинное название
                           ?? candidates.OrderBy(p => Math.Abs((l.Date.Date - p.Date.Date).Days)).First();
                supplierPool.Remove(pick);
                results[l] = new BankLineResult(l, BankLineKind.SupplierPayment, [], pick.Supplier, null);
                continue;
            }
            if (BankRules.Find(rules, l.Description) is { } rule)
            {
                results[l] = new BankLineResult(l, BankLineKind.Rule, [], null, rule.Category);
                continue;
            }
            // 2026-11-05 (приёмка волн 0–3, Н-8): из нескольких названий в описании берём самое длинное («ТЕСТ П26», а не короткое «Тест»).
            var named = normalizedNames.Where(n => lineKey.Contains(n.Key, StringComparison.Ordinal)).OrderByDescending(n => n.Key.Length).FirstOrDefault();
            if (named.Name is not null)
            {
                results[l] = new BankLineResult(l, BankLineKind.SupplierByName, [], named.Name, null);
                continue;
            }
            results[l] = new BankLineResult(l, BankLineKind.UnknownExpense, [], null, null);
        }

        var lines = ordered.Select(l => results[l]).ToList();
        var notFound = shifts.Where(s => s.Amount > 0.005 && !matchedShiftIds.Contains(s.Id)).OrderBy(s => s.Day).ToList();
        var kassa = Math.Round(shifts.Where(s => s.Amount > 0.005).Sum(s => s.Amount), 2);
        // «По банку» — все поступления, кроме помеченных правилом «не от продаж»: то, что банк получил от продаж (и неопознанное, которое может ими быть).
        var bankTotal = Math.Round(lines.Where(r => r.Line.Amount > 0 && r.Category != "not_sales").Sum(r => r.Line.Amount), 2);
        return new BankReconcileResult(lines, notFound, kassa, bankTotal, notFound.Sum(s => Math.Max(s.Receipts, 1)));
    }
}

/// <summary>Правила «запомнить» (шаблон описания → категория). Файл bank-rules.json в папке данных программы; хранится локально.</summary>
public static class BankRulesStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static List<BankRule>? _rules;

    /// <summary>Только для тестов и стенда: другая папка вместо %APPDATA%.</summary>
    public static string? DataDirOverride { get; set; }

    private static string FilePath => Path.Combine(DataDirOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName), "bank-rules.json");

    public static IReadOnlyList<BankRule> All
    {
        get
        {
            lock (Gate)
                return (_rules ??= Read()).ToList();
        }
    }

    /// <summary>Запомнить: описание → категория. false — из описания не вышло шаблона (одни служебные слова).</summary>
    public static bool Remember(string? description, string category)
    {
        var pattern = BankRules.MakePattern(description);
        if (pattern.Length == 0 || string.IsNullOrWhiteSpace(category))
            return false;
        lock (Gate)
        {
            var rules = _rules ??= Read();
            rules.RemoveAll(r => string.Equals(r.Pattern, pattern, StringComparison.Ordinal));
            rules.Add(new BankRule(pattern, category));
            if (rules.Count > 500)
                rules.RemoveRange(0, rules.Count - 500);
            Save(rules);
        }
        return true;
    }

    public static void Reset()
    {
        lock (Gate)
            _rules = null;
    }

    private static void Save(List<BankRule> rules)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(rules, Json));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка выписки: правила не записаны ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private static List<BankRule> Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<BankRule>>(File.ReadAllText(FilePath), Json) ?? new List<BankRule>();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сверка выписки: файл правил не прочитан ({ex.GetType().Name}) — правил нет.", "WARNING");
        }
        return new List<BankRule>();
    }
}
