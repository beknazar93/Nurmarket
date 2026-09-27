using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>Шапка любого выгружаемого отчёта: что за отчёт, чей, за какой период, когда и кем
/// сформирован. Одна и та же для Excel и Word — чтобы бумажная и табличная версии одного
/// отчёта подписывались одинаково.
///
/// Кассир и касса берутся из текущей POS-сессии (<see cref="PosApp"/>), а не передаются из окна:
/// выгрузок несколько, и забыть передать имя в одной из них — значит получить отчёт без автора.</summary>
public sealed record ReportMeta(
    string Title,
    string? Company,
    string? Period,
    DateTime GeneratedAt,
    string? GeneratedBy,
    string? Cashbox)
{
    public static ReportMeta Create(string title, string? company, string? period) =>
        new(title, Clean(company), Clean(period), DateTime.Now,
            Clean(PosApp.CurrentUserDisplayName), Clean(PosApp.PosCashboxDisplayName));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Строка «Магазин · Период: …» под заголовком.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Company is not null)
                parts.Add(Company);
            if (Period is not null)
                parts.Add(ReportLabels.Period + ": " + Period);
            return string.Join("   ·   ", parts);
        }
    }

    /// <summary>Строка «Сформирован … · Кассир … · Касса …» — мелким шрифтом под заголовком.</summary>
    public string GeneratedLine
    {
        get
        {
            var parts = new List<string> { ReportLabels.Generated + ": " + GeneratedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) };
            if (GeneratedBy is not null)
                parts.Add(ReportLabels.Cashier + ": " + GeneratedBy);
            if (Cashbox is not null)
                parts.Add(ReportLabels.Cashbox + ": " + Cashbox);
            return string.Join("   ·   ", parts);
        }
    }
}

/// <summary>Имя файла выгрузки в виде «Отчёт период компания.xlsx» — так файлы в папке
/// «Загрузки» различаются с первого взгляда, а не превращаются в ряд analitika-2026-09-01(3).xlsx.</summary>
public static class ExportFileName
{
    public static string Build(string report, string? period, string? company, string extension)
    {
        var parts = new[] { report, period, company }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        var name = string.Join(" ", parts);

        // Двоеточие, слэш, кавычки и т. п. в имени файла Windows не принимает: название магазина
        // вроде «ИП "Нур"» или «Нур/Маркет» иначе сорвало бы сохранение.
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > 120)
            cleaned = cleaned[..120].TrimEnd();
        if (cleaned.Length == 0)
            cleaned = "report";

        return cleaned + "." + extension.TrimStart('.');
    }

    /// <summary>Период для имени файла: один день — одна дата, иначе «с-по».</summary>
    public static string Period(DateTime from, DateTime to) =>
        from.Date == to.Date
            ? from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
            : from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + "-" + to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Числа для Word-отчётов — в культуре языка интерфейса: «1 234,56» по-русски и
/// по-кыргызски, «1,234.56» по-английски. В Excel числа хранятся числами, и их вид там решает
/// формат ячейки, поэтому этот класс нужен только для текста документа.</summary>
public static class ReportFormat
{
    public static CultureInfo Culture => UserPreferences.Instance.Language switch
    {
        AppLanguage.English => CultureInfo.GetCultureInfo("en-US"),
        AppLanguage.Turkish => CultureInfo.GetCultureInfo("tr-TR"),
        _ => CultureInfo.GetCultureInfo("ru-RU"),
    };

    /// <summary>Язык документа для проверки орфографии в Word.</summary>
    public static string LanguageTag => UserPreferences.Instance.Language switch
    {
        AppLanguage.Kyrgyz => "ky-KG",
        AppLanguage.English => "en-US",
        AppLanguage.Turkish => "tr-TR",
        AppLanguage.Uzbek => "uz-Latn-UZ",
        _ => "ru-RU",
    };

    public static string Money(double value) => value.ToString("#,##0.00", Culture);

    public static string Quantity(double value) => value.ToString("#,##0.###", Culture);

    public static string Integer(double value) => value.ToString("#,##0", Culture);

    public static string Percent(double percentPoints) => percentPoints.ToString("0.0", Culture) + " %";

    public static string Days(double value) => value.ToString("#,##0.0", Culture);

    public static string Date(DateTime value) => value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public static string DateAndTime(DateTime value) => value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Цвета отчётов — одни и те же в Excel и Word. Группы ABC окрашены так же, как на
/// экране программы (зелёный / янтарный / синий), только светлее — по заливке таблицы должен
/// читаться текст, и она не должна съедать тонер при печати.</summary>
public static class ReportPalette
{
    public const string Accent = "1F4E78";      // тёмно-синий: шапки таблиц, заголовки
    public const string AccentSoft = "2E75B6";  // подзаголовки
    public const string HeaderText = "FFFFFF";
    public const string Zebra = "F3F6FA";        // чередование строк
    public const string TotalFill = "DDE5F0";    // строка «Итого»
    public const string LabelFill = "EEF2F7";    // подписи в блоках «поле — значение»
    public const string GridLine = "C9D3E0";     // тонкие рамки таблиц
    public const string Muted = "7F7F7F";        // служебные строки
    public const string SubtitleText = "404040";
    public const string Link = "0563C1";

    public const string ChartPrimary = "2F5597";
    public const string ChartLine = "ED7D31";

    // Группы ABC и состояния: заливка + цвет текста (как стили «Хороший/Нейтральный» Excel).
    public const string GoodFill = "C6EFCE", GoodText = "006100";   // A, доставлено
    public const string WarnFill = "FFEB9C", WarnText = "9C5700";   // B, в пути, мало на складе
    public const string InfoFill = "DDEBF7", InfoText = "1F4E78";   // C, создано
    public const string BadFill = "FFC7CE", BadText = "9C0006";     // кончается, отрицательный остаток
    public const string MutedFill = "EDEDED", MutedText = "595959"; // отменено

    /// <summary>Насыщенные цвета групп — для диаграмм (совпадают с экраном ABC).</summary>
    public static string GroupChartColor(string group) => group switch
    {
        "A" => "16A34A",
        "B" => "F59E0B",
        _ => "3B82F6",
    };
}

/// <summary>Подсветка ячейки в отчёте.</summary>
public enum ReportTone
{
    None,
    Good,
    Warn,
    Info,
    Bad,
    Muted,
}

/// <summary>Подписи отчётов на языке интерфейса. Собраны в одном месте, потому что одни и те же
/// слова («Итого», «Период», «Сформирован») стоят и в Excel, и в Word, и в каждом отчёте —
/// переводить их пять раз по разным файлам значит рано или поздно получить разнобой.</summary>
public static class ReportLabels
{
    public static string Period => Tr.T("Период", "Мезгил", "Period", "Dönem", "Davr");
    public static string Generated => Tr.T("Сформирован", "Түзүлгөн", "Generated", "Oluşturulma", "Yaratilgan");
    public static string Cashier => Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir");
    public static string Cashbox => Tr.T("Касса", "Касса", "Till", "Kasa", "Kassa");
    public static string Shop => Tr.T("Магазин", "Дүкөн", "Shop", "Mağaza", "Do'kon");
    public static string Total => Tr.T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami");
    public static string Number => "№";
    public static string Contents => Tr.T("Содержание", "Мазмуну", "Contents", "İçindekiler", "Mundarija");
    public static string NoData => Tr.T("Нет данных за период", "Мезгил ичинде маалымат жок", "No data for the period", "Bu dönem için veri yok", "Davr uchun ma'lumot yo'q");
    public static string Currency => Tr.T("сом", "сом", "som", "som", "so'm");
    public static string Kg => Tr.T("кг", "кг", "kg", "kg", "kg");

    /// <summary>Колонтитул Excel: &amp;P — номер страницы, &amp;N — всего страниц.</summary>
    public static string ExcelPageOf => Tr.T("Страница &P из &N", "&P-бет, бардыгы &N", "Page &P of &N", "Sayfa &P / &N", "&P-bet, jami &N");

    /// <summary>Нижний колонтитул Word: {P} и {N} заменяются полями PAGE и NUMPAGES.</summary>
    public static string WordPageOf => Tr.T("Страница {P} из {N}", "{P}-бет, бардыгы {N}", "Page {P} of {N}", "Sayfa {P} / {N}", "{P}-bet, jami {N}");

    /// <summary>«Выручка, сом» — единица в заголовке колонки, а не в каждой ячейке: так числа
    /// остаются числами и их можно складывать.</summary>
    public static string WithUnit(string title, string unit) => title + ", " + unit;
}
