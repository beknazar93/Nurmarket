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
            double? Optional(int idx) => idx >= 0 ? ParseDouble(Field(idx)) : null;

            var name = Field(nameIdx);
            var barcode = Field(barcodeIdx);

            string? error = null;
            if (string.IsNullOrWhiteSpace(name))
                error = "нет названия";
            else if (string.IsNullOrWhiteSpace(barcode))
                error = "нет штрихкода";

            rows.Add(new ImportRow
            {
                RowNumber = i + 1,
                Name = name,
                Barcode = barcode,
                Article = NullIfEmpty(Field(ArticleIdx())),
                Category = NullIfEmpty(Field(CategoryIdx())),
                Brand = NullIfEmpty(Field(BrandIdx())),
                Unit = string.IsNullOrWhiteSpace(Field(UnitIdx())) ? "шт" : Field(UnitIdx()),
                Quantity = Optional(QuantityIdx()),
                PurchasePrice = Optional(PurchasePriceIdx()),
                MarkupPercent = Optional(MarkupIdx()),
                Price = Optional(PriceIdx()),
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

    private static double? ParseDouble(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        return double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
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
