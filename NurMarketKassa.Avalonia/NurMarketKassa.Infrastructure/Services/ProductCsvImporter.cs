using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Массовая загрузка товаров из CSV (2026-09-04) — заменяет собой недоступный на
/// реальном API "перенос между складами" (у аккаунта только один общий склад, см. память
/// project_multilanguage_2026-09-04 и связанные заметки этой сессии). Разбирает файл в список
/// строк, каждую из которых вызывающая сторона превращает в ProductEditRequest и сохраняет
/// через УЖЕ РАБОТАЮЩИЙ ICatalogApiService.CreateProductAsync/UpdateProductAsync — та же
/// цепочка, что использует обычная форма "Новый товар", просто в цикле.</summary>
public static class ProductCsvImporter
{
    public sealed record ImportRow
    {
        public int RowNumber { get; init; }
        public string Name { get; init; } = "";
        public string Barcode { get; init; } = "";
        public string? Article { get; init; }
        public string? Category { get; init; }
        public string? Brand { get; init; }
        public string Unit { get; init; } = "шт";
        /// <summary>null — такой колонки в файле НЕ БЫЛО (или ячейка пуста). Это не то же самое,
        /// что ноль: ноль означает «на складе пусто», а null — «файл про остаток ничего не
        /// говорит, не трогай его на сервере». Раньше здесь был не-nullable double, и отсутствие
        /// колонки давало 0, который уходил на сервер абсолютным значением и обнулял склад.</summary>
        public double? Quantity { get; init; }
        public double? PurchasePrice { get; init; }
        public double? MarkupPercent { get; init; }
        public double? Price { get; init; }
        public bool IsWeight { get; init; }
        public string? ParseError { get; init; }
        public bool IsValid => ParseError is null;
    }

    private static readonly string[] NameHeaders = ["название", "наименование", "name"];
    private static readonly string[] BarcodeHeaders = ["штрихкод", "штрих-код", "barcode"];
    private static readonly string[] ArticleHeaders = ["артикул", "код", "article"];
    private static readonly string[] CategoryHeaders = ["категория", "category"];
    private static readonly string[] BrandHeaders = ["бренд", "brand"];
    private static readonly string[] UnitHeaders = ["единица", "ед.изм.", "unit"];
    private static readonly string[] QuantityHeaders = ["количество", "остаток", "quantity"];
    private static readonly string[] PurchasePriceHeaders = ["закупка", "закупочная цена", "purchase"];
    private static readonly string[] MarkupHeaders = ["наценка", "markup"];
    private static readonly string[] PriceHeaders = ["цена", "цена продажи", "price"];
    private static readonly string[] WeightHeaders = ["весовой", "весовой товар", "isweight", "weight"];

    /// <summary>Разбирает CSV-текст в список строк. Не бросает исключений на плохих строках —
    /// каждая строка сама несёт ParseError, чтобы предпросмотр в окне показал ВСЕ проблемы
    /// сразу, а не остановился на первой же ошибке.</summary>
    public static List<ImportRow> Parse(string csvText)
    {
        var rows = new List<ImportRow>();
        if (string.IsNullOrWhiteSpace(csvText))
            return rows;

        var lines = csvText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        if (lines.Length == 0)
            return rows;

        var delimiter = DetectDelimiter(lines[0]);
        var header = ParseLine(lines[0], delimiter).Select(h => h.Trim().ToLowerInvariant()).ToList();

        int NameIdx() => IndexOfAny(header, NameHeaders);
        int BarcodeIdx() => IndexOfAny(header, BarcodeHeaders);
        int ArticleIdx() => IndexOfAny(header, ArticleHeaders);
        int CategoryIdx() => IndexOfAny(header, CategoryHeaders);
        int BrandIdx() => IndexOfAny(header, BrandHeaders);
        int UnitIdx() => IndexOfAny(header, UnitHeaders);
        int QuantityIdx() => IndexOfAny(header, QuantityHeaders);
        int PurchasePriceIdx() => IndexOfAny(header, PurchasePriceHeaders);
        int MarkupIdx() => IndexOfAny(header, MarkupHeaders);
        int PriceIdx() => IndexOfAny(header, PriceHeaders);
        int WeightIdx() => IndexOfAny(header, WeightHeaders);

        var nameIdx = NameIdx();
        var barcodeIdx = BarcodeIdx();

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseLine(line, delimiter);
            string Field(int idx) => idx >= 0 && idx < fields.Count ? fields[idx].Trim() : "";

            // Числовое поле, которого может не быть в файле. null означает «колонки нет либо
            // ячейка пуста» — такое поле дальше по цепочке вообще не отправляется серверу.
            // 2026-09-26, стресс-тест массовой загрузки: ячейка, которую не удалось разобрать
            // («1 234,50» из Excel, «abc»), тоже давала null — и товар молча создавался с ценой
            // 0.00. Теперь это ошибка строки, видная в предпросмотре до загрузки.
            var problems = new List<string>();
            double? Optional(int idx, string column, double limit, bool allowNegative = false)
            {
                if (idx < 0)
                    return null;
                var cell = Field(idx);
                if (string.IsNullOrWhiteSpace(cell))
                    return null;
                if (!TryParseNumber(cell, out var value))
                {
                    problems.Add($"{column}: не число «{cell}»");
                    return null;
                }
                if (value < 0 && !allowNegative)
                {
                    problems.Add($"{column}: отрицательное число");
                    return null;
                }
                if (Math.Abs(value) >= limit)
                {
                    problems.Add($"{column}: слишком большое число");
                    return null;
                }
                return value;
            }

            var name = Field(nameIdx);
            var barcode = Field(barcodeIdx);
            var quantity = Optional(QuantityIdx(), "количество", 1e9);
            var purchasePrice = Optional(PurchasePriceIdx(), "закупка", 1e8);
            var markup = Optional(MarkupIdx(), "наценка", 1e6, allowNegative: true);
            var price = Optional(PriceIdx(), "цена", 1e8);

            // Пределы сервера (OPTIONS api/main/products/create-manual/): название до 255
            // символов, штрихкод до 64. Длиннее — сервер отвечал 500 без объяснений.
            string? error = null;
            if (string.IsNullOrWhiteSpace(name))
                error = "нет названия";
            else if (string.IsNullOrWhiteSpace(barcode))
                error = "нет штрихкода";
            else if (name.Length > 255)
                error = $"название длиннее 255 символов ({name.Length})";
            else if (barcode.Length > 64)
                error = "штрихкод длиннее 64 символов";
            else if (problems.Count > 0)
                error = string.Join("; ", problems);

            rows.Add(new ImportRow
            {
                RowNumber = i + 1,
                Name = name,
                Barcode = barcode,
                Article = NullIfEmpty(Field(ArticleIdx())),
                Category = NullIfEmpty(Field(CategoryIdx())),
                Brand = NullIfEmpty(Field(BrandIdx())),
                Unit = string.IsNullOrWhiteSpace(Field(UnitIdx())) ? "шт" : Field(UnitIdx()),
                Quantity = quantity,
                PurchasePrice = purchasePrice,
                MarkupPercent = markup,
                Price = price,
                IsWeight = ParseBool(Field(WeightIdx())),
                ParseError = error,
            });
        }

        return rows;
    }

    public static string BuildTemplateCsv() =>
        "Название,Штрихкод,Артикул,Категория,Бренд,Единица,Количество,Закупка,Наценка,Цена,Весовой\n" +
        "Хлеб белый,4600000000001,ХЛБ-01,Хлеб,,шт,50,25,20,30,нет\n" +
        "Картошка,4600000000002,,Овощи,,кг,100,20,,35,да\n";

    private static int IndexOfAny(List<string> header, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var idx = header.IndexOf(candidate);
            if (idx >= 0)
                return idx;
        }
        return -1;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Число из ячейки в любом привычном виде: «1234,50», «1234.50», «1 234,50» (Excel с
    /// русской локалью ставит пробел или неразрывный пробел между разрядами), «1,234.50».
    /// Если есть и запятая, и точка, дробная часть — после последнего из них.</summary>
    internal static bool TryParseNumber(string s, out double value)
    {
        value = 0;
        var text = new string(s.Where(c => !char.IsWhiteSpace(c) && c != '\'').ToArray());
        if (text.Length == 0)
            return false;

        var lastComma = text.LastIndexOf(',');
        var lastDot = text.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
            text = lastComma > lastDot ? text.Replace(".", "").Replace(',', '.') : text.Replace(",", "");
        else
            text = text.Replace(',', '.');

        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }

    private static bool ParseBool(string s)
    {
        var normalized = s.Trim().ToLowerInvariant();
        return normalized is "да" || normalized is "true" || normalized is "1" || normalized is "yes";
    }

    /// <summary>Многие Excel-экспорты с русской локалью используют ; вместо , (т.к. , уже
    /// используется как десятичный разделитель в этой локали) — определяем по заголовку.</summary>
    private static char DetectDelimiter(string headerLine)
    {
        var commas = headerLine.Count(c => c == ',');
        var semicolons = headerLine.Count(c => c == ';');
        return semicolons > commas ? ';' : ',';
    }

    private static List<string> ParseLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}
