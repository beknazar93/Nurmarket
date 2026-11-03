using System.Globalization;

namespace NurMarketKassa.Services.InvoiceImport;

// 2026-10-27, просьба владельца «добавь загрузку электронных накладных» (Склад → Приёмка): модель разобранной накладной.
// Без интерфейса и сети. Разбор — InvoiceTableParser (Excel/CSV) и InvoiceXmlParser (XML), сверка с каталогом — InvoiceMatcher,
// защита от дубля — InvoiceImportRegistry, вход — InvoiceImportService. Деньги и остатки здесь не считаются: проводит обычная приёмка.

/// <summary>Шапка накладной. Любое поле может быть пустым — файл не обязан её содержать.</summary>
public sealed class InvoiceHeader
{
    public string Number { get; set; } = "";
    public DateTime? Date { get; set; }
    public string SupplierName { get; set; } = "";
    public string SupplierInn { get; set; } = "";
    public string SupplierKpp { get; set; } = "";
    public string BuyerName { get; set; } = "";
    public string BuyerInn { get; set; } = "";

    /// <summary>Итог из файла (строка «Итого», поле «Всего», сумма документа) — для сверки с суммой строк.</summary>
    public double? TotalFromFile { get; set; }

    /// <summary>Итог без НДС из файла (колонка «Сумма без НДС» в строке «Итого»), если отдельно указан.</summary>
    public double? NetTotalFromFile { get; set; }

    /// <summary>Сумма НДС из файла, если указана.</summary>
    public double? VatTotalFromFile { get; set; }

    /// <summary>Валюта накладной «как в файле» («руб.», «RUB», «сом»); пусто — не указана.</summary>
    public string Currency { get; set; } = "";
}

/// <summary>Строка накладной в том виде, как её прочли (ничего не угадывается, кроме числа: пробелы, запятая).</summary>
public sealed class InvoiceItem
{
    /// <summary>Номер строки файла (Excel/CSV) или порядковый номер элемента (XML) — для показа человеку.</summary>
    public int SourceRow { get; init; }

    public string Barcode { get; set; } = "";
    public string Article { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Quantity { get; set; }

    /// <summary>Цена за единицу как в файле (в накладных с НДС — обычно без НДС). null — не указана.</summary>
    public double? Price { get; set; }

    /// <summary>Цена за единицу с НДС, если файл её даёт.</summary>
    public double? PriceWithVat { get; set; }

    /// <summary>Розничная цена из файла, если есть.</summary>
    public double? RetailPrice { get; set; }

    /// <summary>Сумма строки (без НДС, если в файле есть отдельная колонка «с НДС»).</summary>
    public double? Sum { get; set; }

    public double? SumWithVat { get; set; }
    public double? VatSum { get; set; }

    /// <summary>Ставка НДС в процентах, если в файле она числом; null — не указана или «без НДС».</summary>
    public double? VatPercent { get; set; }

    /// <summary>Ставка как текст из файла («20 %», «Без НДС»).</summary>
    public string VatText { get; set; } = "";

    public List<string> Warnings { get; } = [];

    /// <summary>Закупка за единицу: «Цена за ед.»; нет цены — сумма ÷ количество. 0 — не определить.</summary>
    public double UnitPrice(bool withVat)
    {
        if (withVat)
        {
            if (PriceWithVat is > 0)
                return PriceWithVat.Value;
            if (SumWithVat is > 0 && Quantity > 0)
                return Math.Round(SumWithVat.Value / Quantity, 4);
            if (Price is > 0 && VatPercent is > 0)
                return Math.Round(Price.Value * (1 + VatPercent.Value / 100), 4);
        }

        if (Price is > 0)
            return Price.Value;
        if (Sum is > 0 && Quantity > 0)
            return Math.Round(Sum.Value / Quantity, 4);
        if (SumWithVat is > 0 && Quantity > 0 && VatSum is > 0)
            return Math.Round((SumWithVat.Value - VatSum.Value) / Quantity, 4);
        // 2026-10-27 (ревью Ск-12): в файле только цены/суммы «с НДС» — раньше закупка выходила 0 («нет цены»), переключатель «с НДС» был скрыт. Берём то, что есть:
        // без НДС, если ставка известна, иначе как есть (в окне предупреждение «в файле только цены с НДС»).
        if (PriceWithVat is > 0)
            return VatPercent is > 0 ? Math.Round(PriceWithVat.Value / (1 + VatPercent.Value / 100), 4) : PriceWithVat.Value;
        if (SumWithVat is > 0 && Quantity > 0)
            return Math.Round(SumWithVat.Value / Quantity / (1 + (VatPercent ?? 0) / 100), 4);
        return 0;
    }

    /// <summary>2026-10-27 (Ск-12): цена закупки определилась только из цены/суммы «с НДС» (цены без НДС в файле нет).</summary>
    public bool OnlyGrossPrice => !(Price is > 0) && !(Sum is > 0) && (PriceWithVat is > 0 || SumWithVat is > 0) && !(SumWithVat is > 0 && VatSum is > 0 && Quantity > 0);

    public bool HasVat => (VatSum ?? 0) > 0.004 || (VatPercent ?? 0) > 0 || (SumWithVat is > 0 && Sum is > 0 && SumWithVat.Value - Sum.Value > 0.004);
}

/// <summary>Разобранная накладная.</summary>
public sealed class InvoiceDocument
{
    public InvoiceHeader Header { get; init; } = new();
    public List<InvoiceItem> Items { get; init; } = [];

    /// <summary>«Таблица Excel», «CSV», «XML (CommerceML)», «XML (счёт-фактура)» — для журнала и подписи в окне.</summary>
    public string Format { get; init; } = "";

    public string FileName { get; init; } = "";

    /// <summary>Замечания разбора (валюта, колонки не найдены, пропущенные строки).</summary>
    public List<string> Warnings { get; } = [];

    public bool AnyVat => Items.Any(i => i.HasVat);

    /// <summary>Сумма строк по закупке (количество × цена за единицу).</summary>
    public double LinesTotal(bool withVat) => Items.Sum(i => i.Quantity * i.UnitPrice(withVat));
}

/// <summary>Общие приёмы чтения текста накладных: числа, штрихкоды, даты, ИНН.</summary>
public static class InvoiceText
{
    private static readonly char[] NumberTrash = [' ', ' ', ' ', ' ', '​', ' ', '\t'];

    /// <summary>Число из ячейки/атрибута: «1 234,50», «1&#160;234.50», «45 000,00 руб.», «20 %». Текст без цифр — false.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        // 2026-10-27 (ревью Ск-18): «(500)» — отрицательное (бухгалтерская запись), как в мобилке; раньше касса читала +500.
        var negative = s.Length > 2 && s[0] == '(' && s.TrimEnd(' ', ' ', '.', ',', ';').EndsWith(')') && s.Count(c => c == '(') == 1 && s.Skip(1).Any(char.IsDigit) && !s.Skip(1).Any(char.IsLetter);
        // Хвост-единица: «руб.», «сом», «₽», «%», «шт», «kgs»…
        var end = s.Length;
        while (end > 0 && !(char.IsDigit(s[end - 1])))
            end--;
        if (end == 0)
            return false;
        var start = 0;
        while (start < end && !(char.IsDigit(s[start]) || ((s[start] is '-' or '+' or '−') && start + 1 < end && char.IsDigit(s[start + 1]))))
            start++;
        s = s[start..end].Replace('−', '-');
        foreach (var c in NumberTrash)
            s = s.Replace(c.ToString(), "");
        if (s.Length == 0)
            return false;
        // 1 234,5 → уже без пробелов. Если после чистки остались буквы между цифрами — это не число («12abc3»).
        if (s.Any(c => !(char.IsDigit(c) || c is '.' or ',' or '-' or '+' or '\'')))
            return false;
        // 2026-10-27 (ревью Ск-18): «1.234.567» / «1,234,567» — несколько одинаковых разделителей и нет другого: это разряды тысяч («1234567»); раньше не разбиралось.
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[+-]?\d{1,3}(\.\d{3}){2,}$"))
            s = s.Replace(".", "");
        else if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[+-]?\d{1,3}(,\d{3}){2,}$"))
            s = s.Replace(",", "");
        if (!ProductCsvImporter.TryParseNumber(s, out value) || double.IsNaN(value) || double.IsInfinity(value))
            return false;
        if (negative && value > 0)
            value = -value;
        return true;
    }

    public static double? ParseNumberOrNull(string? text) => TryParseNumber(text, out var v) ? v : null;

    /// <summary>2026-11-01 (контрольный аудит, С2-09): штрихкод хранится КАК СТРОКА, как пришёл из накладной (после trim): убираем только служебное — пробелы по краям,
    /// кавычки, «="…"» Excel, хвост «.0» у числа из ячейки и пробелы внутри цифрового кода. НЕ приводим к числу/EAN, НЕ дописываем нули и контрольную цифру, НЕ выбрасываем
    /// буквы и коды нестандартной длины (внутренние 5–7 и 9–11 цифр, «A-1023», «KZ123»). Научную запись Excel («4,6E+12») не раскрываем: хвост уже потерян, угадывать нельзя —
    /// <see cref="IsMangledBarcode"/> помечает такой код, парсеры его не используют и пишут замечание. Параметр <paramref name="restoreLeadingZero"/> оставлен для совместимости и ничего не делает.</summary>
    public static string CleanBarcode(string? raw, bool restoreLeadingZero = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var s = raw.Trim();
        if (s.StartsWith("=\"", StringComparison.Ordinal) && s.EndsWith('"'))
            s = s[2..^1];
        s = s.Trim('\'', '"', '`', '*', ' ');
        if (IsMangledBarcode(s) || IsPlaceholderBarcode(s))
            return "";
        // хвост «.0», «.00», «,00» у числа из ячейки Excel (4870200221261.00) — не часть кода
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^(\d{4,})[.,]0+$", "$1");
        // пробелы и дефисы внутри цифрового кода («4870 2002 21261», «4870-2002-21261») — группы цифр, склеиваем; код с буквами оставляем как есть
        var compact = new string(s.Where(c => !char.IsWhiteSpace(c) && c != ' ' && c != ' ' && c != '-' && c != '–').ToArray());
        return compact.Length > 0 && compact.All(char.IsAsciiDigit) ? compact : s;
    }

    private static readonly HashSet<string> PlaceholderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "нет", "нету", "н/д", "нд", "б/ш", "б\\ш", "бш", "без ш/к", "без штрихкода", "без штрих-кода", "без кода", "отсутствует", "n/a", "na", "no", "none", "null", "nan", "nil", "empty", "-",
        "жок", "yok", "yo'q", "yoq",
    };

    /// <summary>2026-11-01 (финальная приёмка, Н-3): «заглушка» вместо кода — «-», «—», «0», «000», «нет», «n/a», «б/ш», строка без букв и цифр. Это НЕ штрихкод: строка считается без штрихкода
    /// и не склеивается с другими строками по ключу «bc:-»; товар не заводится с кодом «-».</summary>
    public static bool IsPlaceholderBarcode(string? raw)
    {
        var s = (raw ?? "").Trim().Trim('\'', '"', '`', '*', ' ');
        if (s.Length == 0)
            return true;
        if (PlaceholderWords.Contains(s))
            return true;
        if (!s.Any(char.IsLetterOrDigit))
            return true; // только знаки: «-», «—», «.», «/», «???»
        return s.All(c => c == '0' || char.IsWhiteSpace(c) || c is '-' or '.' or ','); // «0», «00000000», «0.0»
    }

    /// <summary>Научная запись из Excel («4,87020022126E+12», «4.6E12»): настоящий код потерян. Такой «штрихкод» нельзя ни раскрывать, ни сравнивать — только просить исправить файл.</summary>
    public static bool IsMangledBarcode(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && System.Text.RegularExpressions.Regex.IsMatch(raw.Trim().Trim('\'', '"', '`', '*'), @"^\d+([.,]\d+)?[eE][+-]?\d{1,2}$");

    /// <summary>Замечание для строки с испорченным кодом (научная запись) — на 5 языках.</summary>
    public static string MangledBarcodeWarning(string raw) => Tr.T($"штрихкод «{raw}» испорчен Excel (научная запись) — не используется; исправьте столбец в файле (формат «Текст») и загрузите заново, либо проверьте код вручную",
        $"штрих-код «{raw}» Excel тарабынан бузулган (илимий жазуу) — колдонулбайт; мамычаны файлда оңдоңуз («Текст» форматы) жана кайра жүктөңүз же кодду кол менен текшериңиз",
        $"barcode “{raw}” was mangled by Excel (scientific notation) — not used; fix the column in the file (“Text” format) and load again, or check the code by hand",
        $"barkod «{raw}» Excel tarafından bozulmuş (bilimsel gösterim) — kullanılmadı; sütunu dosyada düzeltin («Metin» biçimi) ve yeniden yükleyin veya kodu elle kontrol edin",
        $"shtrix-kod «{raw}» Excel tomonidan buzilgan (ilmiy yozuv) — ishlatilmaydi; ustunni faylda tuzating («Matn» formati) va qayta yuklang yoki kodni qo'lda tekshiring");

    /// <summary>Контрольная цифра штрихкода (EAN-8/12/13/14) верна.</summary>
    public static bool HasValidCheckDigit(string code)
    {
        if (code.Length is not (8 or 12 or 13 or 14) || !code.All(char.IsAsciiDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < code.Length - 1; i++)
        {
            var d = code[code.Length - 2 - i] - '0';
            sum += d * (i % 2 == 0 ? 3 : 1);
        }

        return (10 - sum % 10) % 10 == code[^1] - '0';
    }

    /// <summary>Похоже ли значение на штрихкод (цифры нужной длины с верной контрольной цифрой) — чтобы отличить код-штрихкод от артикула.</summary>
    /// <summary>Похоже на штрихкод: только цифры стандартной длины с верной контрольной цифрой (для «артикула», который на самом деле код).
    public static bool LooksLikeBarcode(string? raw) => CleanBarcode(raw) is { Length: > 0 } c && c.All(char.IsAsciiDigit) && HasValidCheckDigit(c);

    /// <summary>ИНН/ПИН: только цифры, 9–14 штук (РФ — 10/12, КР — 14, KZ — 12, UZ — 9). Иначе пусто.</summary>
    public static string CleanInn(string? raw)
    {
        var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
        return digits.Length is >= 9 and <= 14 ? digits : "";
    }

    private static readonly Dictionary<string, int> Months = BuildMonths();

    private static Dictionary<string, int> BuildMonths()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        void Add(int m, params string[] stems)
        {
            foreach (var s in stems)
                map[s] = m;
        }
        Add(1, "янв", "jan", "yan", "oca", "ян");
        Add(2, "фев", "feb", "fev", "şub", "sub", "фе");
        Add(3, "мар", "mar");
        Add(4, "апр", "apr", "nis", "ап");
        Add(5, "мая", "май", "may", "mayıs", "mayis");
        Add(6, "июн", "jun", "iyun", "haz");
        Add(7, "июл", "jul", "iyul", "tem");
        Add(8, "авг", "aug", "avg", "ağu", "agu");
        Add(9, "сен", "sep", "sent", "eyl");
        Add(10, "окт", "oct", "okt", "eki");
        Add(11, "ноя", "nov", "noy", "kas");
        Add(12, "дек", "dec", "dek", "ara");
        return map;
    }

    private static int MonthOf(string word)
    {
        var w = word.ToLowerInvariant().Replace('ё', 'е').Trim('.', ',', '-');
        if (w.Length < 2)
            return 0;
        for (var len = Math.Min(w.Length, 5); len >= 2; len--)
        {
            if (Months.TryGetValue(w[..len], out var m))
                return m;
        }
        return 0;
    }

    private static readonly System.Text.RegularExpressions.Regex WordDate = new(
        @"(?<d>\d{1,2})[»""”']?\s*(?:-?\s*(?:го|ші|нчи|inchi|th|st|nd|rd))?\s*[-–.\s]?\s*(?<m>[\p{L}]{3,12})\.?\s*[,]?\s*(?<y>\d{4})",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // «2026-жылдын 27-октябры», «2026 г. 27 октября»: год впереди.
    private static readonly System.Text.RegularExpressions.Regex YearFirstDate = new(
        @"(?<y>\d{4})\W{0,3}(?:жыл\p{L}*|йил\p{L}*|yil\p{L}*|г\.?|ж\.?)\W{0,3}(?<d>\d{1,2})\s*[-–.\s]?\s*(?<m>\p{L}{3,12})",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex NumDate = new(
        @"(?<!\d)(?<d>\d{1,2})[./\-](?<m>\d{1,2})[./\-](?<y>\d{4}|\d{2})(?!\d)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex IsoDate = new(
        @"(?<!\d)(?<y>\d{4})[./\-](?<m>\d{1,2})[./\-](?<d>\d{1,2})(?!\d)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Дата из текста: 27.10.2026, 2026-10-27, «27 октября 2026 г.», «27 October 2026», «27-октябрь 2026», «27 oktabr 2026»,
    /// число Excel (46327). Не разобралась — null.</summary>
    public static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var s = text.Trim();
        if (IsoDate.Match(s) is { Success: true } iso && Make(iso, out var fromIso))
            return fromIso;
        if (NumDate.Match(s) is { Success: true } num && Make(num, out var fromNum))
            return fromNum;
        foreach (System.Text.RegularExpressions.Match m in WordDate.Matches(s))
        {
            var month = MonthOf(m.Groups["m"].Value);
            if (month == 0)
                continue;
            if (int.TryParse(m.Groups["d"].Value, out var d) && int.TryParse(m.Groups["y"].Value, out var y) && Valid(y, month, d))
                return new DateTime(y, month, d);
        }

        if (YearFirstDate.Match(s) is { Success: true } yf
            && MonthOf(yf.Groups["m"].Value) is var yfMonth and > 0
            && int.TryParse(yf.Groups["d"].Value, out var yfDay) && int.TryParse(yf.Groups["y"].Value, out var yfYear) && Valid(yfYear, yfMonth, yfDay))
            return new DateTime(yfYear, yfMonth, yfDay);

        // Серийный номер даты Excel (ячейка с форматом даты читалась как число).
        if (s.All(char.IsDigit) && s.Length == 5 && int.TryParse(s, out var serial) && serial is > 36000 and < 60000)
            return DateTime.FromOADate(serial).Date;
        return null;

        static bool Make(System.Text.RegularExpressions.Match m, out DateTime date)
        {
            date = default;
            if (!int.TryParse(m.Groups["d"].Value, out var d) || !int.TryParse(m.Groups["m"].Value, out var mo) || !int.TryParse(m.Groups["y"].Value, out var y))
                return false;
            if (y < 100)
                y += 2000;
            if (!Valid(y, mo, d))
                return false;
            date = new DateTime(y, mo, d);
            return true;
        }

        static bool Valid(int y, int m, int d) => y is >= 2000 and <= 2100 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);
    }

    /// <summary>Валюта по тексту заголовка или подписи («руб.», «рублей», «сом», «KGS», «тенге», «$»). Пусто — не узнали.</summary>
    public static string DetectCurrency(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var s = text.ToLowerInvariant().Replace('ё', 'е');
        if (s.Contains("руб") || s.Contains("rub") || s.Contains("₽") || s.Contains("rur"))
            return "руб.";
        if (s.Contains("kgs") || s.Contains("сом ") || s.EndsWith("сом") || s.Contains("сом,") || s.Contains("сом.") || s.Contains("сомов") || s.Contains("сомдор"))
            return "сом";
        if (s.Contains("тенге") || s.Contains("kzt") || s.Contains("₸"))
            return "тенге";
        if (s.Contains("uzs") || s.Contains("сум ") || s.Contains("so'm") || s.Contains("so‘m"))
            return "сум";
        if (s.Contains("usd") || s.Contains("$") || s.Contains("долл"))
            return "USD";
        if (s.Contains("eur") || s.Contains("€") || s.Contains("евро"))
            return "EUR";
        return "";
    }

    /// <summary>Код валюты ОКВ (643 — рубль, 417 — сом…) → подпись.</summary>
    public static string CurrencyFromCode(string? code) => (code ?? "").Trim() switch
    {
        "643" or "RUB" or "RUR" => "руб.",
        "417" or "KGS" => "сом",
        "398" or "KZT" => "тенге",
        "860" or "UZS" => "сум",
        "840" or "USD" => "USD",
        "978" or "EUR" => "EUR",
        var other => other,
    };

    /// <summary>Валюта накладной — не сом: касса работает в сомах, цены надо проверить (не конвертируется).</summary>
    public static bool IsForeignCurrency(string? currency) => !string.IsNullOrWhiteSpace(currency) && !currency.Contains("сом", StringComparison.OrdinalIgnoreCase) && !currency.Contains("kgs", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ сумма прописью («Триста одна тысяча пятьсот рублей 50 копеек»)

    private static readonly Dictionary<string, int> Units = new(StringComparer.Ordinal)
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["одно"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8,
        ["девять"] = 9, ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16,
        ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19, ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60,
        ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90, ["сто"] = 100, ["двести"] = 200, ["триста"] = 300, ["четыреста"] = 400, ["пятьсот"] = 500,
        ["шестьсот"] = 600, ["семьсот"] = 700, ["восемьсот"] = 800, ["девятьсот"] = 900,
    };

    /// <summary>Сумма прописью по-русски → число: «Триста одна тысяча пятьсот рублей 50 копеек» = 301500,5. Не нашли число словами — null.
    /// Разбирается только текст до слова валюты; копейки — цифрами после него.</summary>
    public static double? ParseAmountInWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var t = text.ToLowerInvariant().Replace('ё', 'е');
        var colon = t.LastIndexOf(':');
        if (colon >= 0)
            t = t[(colon + 1)..];
        var tokens = System.Text.RegularExpressions.Regex.Matches(t, @"[\p{L}]+|\d+").Select(m => m.Value).ToList();
        long total = 0, current = 0;
        var any = false;
        var i = 0;
        for (; i < tokens.Count; i++)
        {
            var w = tokens[i];
            if (Units.TryGetValue(w, out var unit))
            {
                current += unit;
                any = true;
            }
            else if (w.StartsWith("тысяч", StringComparison.Ordinal))
            {
                total += (current == 0 ? 1 : current) * 1000;
                current = 0;
                any = true;
            }
            else if (w.StartsWith("миллион", StringComparison.Ordinal))
            {
                total += (current == 0 ? 1 : current) * 1_000_000;
                current = 0;
                any = true;
            }
            else if (w.StartsWith("миллиард", StringComparison.Ordinal))
            {
                total += (current == 0 ? 1 : current) * 1_000_000_000;
                current = 0;
                any = true;
            }
            else if (any)
                break; // слово валюты («рублей», «сомов») или иное — число кончилось
            else if (w.Length > 0 && char.IsDigit(w[0]))
                return null; // в начале цифры, не слова — это не сумма прописью
        }
        if (!any)
            return null;
        total += current;
        var kopecks = 0;
        // «… рублей 50 копеек»: цифры после слова валюты
        for (var j = i; j < tokens.Count - 1; j++)
        {
            if (tokens[j].Length > 0 && char.IsDigit(tokens[j][0]) && int.TryParse(tokens[j], out var k) && k is >= 0 and < 100
                && (tokens[j + 1].StartsWith("коп", StringComparison.Ordinal) || tokens[j + 1].StartsWith("тыйын", StringComparison.Ordinal) || tokens[j + 1].StartsWith("тиин", StringComparison.Ordinal)))
            {
                kopecks = k;
                break;
            }
        }
        return total + kopecks / 100.0;
    }

    public static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
