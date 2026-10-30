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

    private static readonly string[] NameHeaders = ["название", "наименование", "наименование товара", "товар", "name"];
    private static readonly string[] BarcodeHeaders = ["штрихкод", "штрих-код", "штрих код", "barcode", "ean"];
    private static readonly string[] ArticleHeaders = ["артикул", "код", "article"];
    private static readonly string[] CategoryHeaders = ["категория", "category"];
    private static readonly string[] BrandHeaders = ["бренд", "brand"];
    private static readonly string[] UnitHeaders = ["единица", "ед.изм.", "ед. изм.", "ед.изм", "unit"];
    private static readonly string[] QuantityHeaders = ["количество", "кол-во", "остаток", "quantity"];
    private static readonly string[] PurchasePriceHeaders = ["закупка", "закупочная цена", "purchase"];
    private static readonly string[] MarkupHeaders = ["наценка", "markup"];
    private static readonly string[] PriceHeaders = ["цена", "цена продажи", "price"];
    private static readonly string[] WeightHeaders = ["весовой", "весовой товар", "isweight", "weight"];

    /// <summary>Результат разбора файла (2026-10-29: срочный баг клиента — окно «Массовая загрузка» роняло
    /// программу на «неудобных» файлах). <see cref="Error"/> — понятное сообщение на языке интерфейса, если
    /// файл не разобран целиком (Excel вместо CSV, нет заголовков, пустой, слишком большой); тогда
    /// <see cref="Rows"/> пуст. Ошибки отдельных строк лежат в самих строках (ParseError).</summary>
    public sealed class ParseResult
    {
        public List<ImportRow> Rows { get; } = [];
        public string? Error { get; set; }
        public string? EncodingName { get; set; }
    }

    /// <summary>2026-10-29: больше строк в одной загрузке не берём — сервер всё равно отвечает 429 и
    /// сотни строк идут минутами; огромный файл только вешал окно.</summary>
    public const int MaxRows = 20_000;

    private static readonly System.Text.RegularExpressions.Regex ScientificBarcode =
        new(@"^\d+([.,]\d+)?[eE][+\-]?\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex TrailingZeroFraction =
        new(@"^(\d+)[.,]0+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Разбирает CSV-текст в список строк. Не бросает исключений на плохих строках —
    /// каждая строка сама несёт ParseError, чтобы предпросмотр в окне показал ВСЕ проблемы
    /// сразу, а не остановился на первой же ошибке.</summary>
    public static List<ImportRow> Parse(string csvText)
    {
        var result = new ParseResult();
        try
        {
            ParseInto(csvText, result);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: разбор CSV прерван ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        return result.Rows;
    }

    /// <summary>2026-10-29: разбор файла целиком из байтов — сам определяет кодировку (UTF-8 с BOM и без,
    /// UTF-16, Windows-1251), отличает Excel (.xlsx/.xls) и двоичный мусор от CSV и никогда не бросает
    /// исключений: любая беда превращается в <see cref="ParseResult.Error"/>.</summary>
    public static ParseResult ParseBytes(byte[] bytes)
    {
        var result = new ParseResult();
        try
        {
            if (bytes.Length == 0)
            {
                result.Error = EmptyFileText;
                return result;
            }
            if (bytes.LongLength > ProductFileReader.MaxFileBytes)
            {
                result.Error = Tr.T("Файл больше 20 МБ. Разделите его на части.", "Файл 20 МБдан чоң. Бөлүктөргө бөлүңүз.",
                    "The file is larger than 20 MB. Split it into parts.", "Dosya 20 MB'tan büyük. Parçalara bölün.", "Fayl 20 MB dan katta. Uni qismlarga bo'ling.");
                return result;
            }
            if (ProductFileReader.IsZip(bytes) || ProductFileReader.IsOleCompound(bytes))
            {
                result.Error = Tr.T("Это файл Excel (.xlsx/.xls), а не CSV. Откройте его в Excel и сохраните как «CSV UTF-8» или загрузите через мастер «Массовая загрузка».",
                    "Бул Excel файлы (.xlsx/.xls), CSV эмес. Аны Excel'де ачып «CSV UTF-8» катары сактаңыз же «Массалык жүктөө» устасы аркылуу жүктөңүз.",
                    "This is an Excel file (.xlsx/.xls), not CSV. Open it in Excel and save as “CSV UTF-8”, or load it through the bulk import wizard.",
                    "Bu bir Excel dosyası (.xlsx/.xls), CSV değil. Excel'de açıp «CSV UTF-8» olarak kaydedin veya toplu yükleme sihirbazını kullanın.",
                    "Bu Excel fayli (.xlsx/.xls), CSV emas. Uni Excel'da ochib «CSV UTF-8» sifatida saqlang yoki ommaviy yuklash ustasidan foydalaning.");
                return result;
            }

            var text = ProductFileReader.Decode(bytes, ProductFileEncoding.Auto, out var encodingName);
            result.EncodingName = encodingName;
            if (LooksBinary(text))
            {
                result.Error = Tr.T("Файл не похож на текстовый CSV (двоичное содержимое). Сохраните таблицу из Excel как «CSV UTF-8».",
                    "Файл текст CSV'ге окшобойт (экилик мазмун). Таблицаны Excel'ден «CSV UTF-8» катары сактаңыз.",
                    "The file does not look like a text CSV (binary content). Save the table from Excel as “CSV UTF-8”.",
                    "Dosya metin CSV'ye benzemiyor (ikili içerik). Tabloyu Excel'den «CSV UTF-8» olarak kaydedin.",
                    "Fayl matnli CSV ga o'xshamaydi (ikkilik tarkib). Jadvalni Excel'dan «CSV UTF-8» sifatida saqlang.");
                return result;
            }

            ParseInto(text, result);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: файл не разобран ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            result.Rows.Clear();
            result.Error = Tr.T($"Не удалось разобрать файл: {ex.Message}", $"Файлды талдоо мүмкүн болгон жок: {ex.Message}",
                $"Could not parse the file: {ex.Message}", $"Dosya ayrıştırılamadı: {ex.Message}", $"Faylni tahlil qilib bo'lmadi: {ex.Message}");
        }
        return result;
    }

    private static string EmptyFileText => Tr.T("Файл пустой.", "Файл бош.", "The file is empty.", "Dosya boş.", "Fayl bo'sh.");

    /// <summary>Нулевой символ или много управляющих символов — это не текст (например, .xlsx/.xls/.pdf, переименованный в .csv).</summary>
    private static bool LooksBinary(string text)
    {
        var control = 0;
        foreach (var c in text)
        {
            if (c == '\0')
                return true;
            if (c < ' ' && c != '\t' && c != '\r' && c != '\n')
                control++;
        }
        return control > 0 && control * 50 > text.Length;
    }

    private static string NormalizeHeader(string h)
    {
        var s = Clean(h).Trim().ToLowerInvariant().TrimEnd(':', '*').Trim();
        while (s.Contains("  "))
            s = s.Replace("  ", " ");
        return s;
    }

    /// <summary>Управляющие и невидимые символы (NUL, BOM внутри текста, нулевая ширина) — в пробел/пусто:
    /// в таблице предпросмотра они ломали отрисовку и сохранялись в названиях.</summary>
    private static string Clean(string s)
    {
        if (s.Length == 0)
            return s;
        var needs = false;
        foreach (var c in s)
        {
            if (char.IsControl(c) || c is '​' or '‌' or '‍' or '﻿')
            {
                needs = true;
                break;
            }
        }
        if (!needs)
            return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '​' or '‌' or '‍' or '﻿')
                continue;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString();
    }

    /// <summary>Штрихкод из ячейки: без пробелов (в т.ч. неразрывных), без апострофа «'4600…» и хвоста «.0», «null»/«#Н/Д» —
    /// пусто. Научная запись Excel («4,6E+12») — цифры уже потеряны, угадывать код нельзя: возвращаем причину.</summary>
    private static string NormalizeBarcode(string raw, out string? problem)
    {
        problem = null;
        var code = new string(Clean(raw).Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimStart('\'');
        if (code.Length == 0)
            return "";
        var lower = code.ToLowerInvariant();
        if (lower is "null" or "nan" or "none" or "#n/a" or "#н/д" or "-" or "—")
            return "";
        if (ScientificBarcode.IsMatch(code))
        {
            problem = Tr.T($"штрихкод «{code}» записан в научном виде (Excel потерял цифры) — в Excel задайте для столбца формат «Текстовый» и сохраните файл заново",
                $"штрихкод «{code}» илимий түрдө жазылган (Excel сандарды жоготкон) — Excel'де мамычага «Текст» форматын коюп, файлды кайра сактаңыз",
                $"barcode “{code}” is in scientific notation (Excel lost the digits) — set the column format to Text in Excel and save the file again",
                $"barkod «{code}» bilimsel gösterimde (Excel rakamları kaybetti) — Excel'de sütun biçimini Metin yapıp dosyayı yeniden kaydedin",
                $"shtrix-kod «{code}» ilmiy ko'rinishda (Excel raqamlarni yo'qotgan) — Excel'da ustun formatini Matn qilib, faylni qayta saqlang");
            return code;
        }
        var m = TrailingZeroFraction.Match(code);
        return m.Success ? m.Groups[1].Value : code;
    }

    private static void ParseInto(string csvText, ParseResult result)
    {
        if (string.IsNullOrWhiteSpace(csvText))
        {
            result.Error = EmptyFileText;
            return;
        }
        if (csvText[0] == '﻿')
            csvText = csvText[1..];

        var delimiter = ProductFileReader.DetectDelimiter(csvText);

        var headerFound = false;
        int nameIdx = -1, barcodeIdx = -1, articleIdx = -1, categoryIdx = -1, brandIdx = -1, unitIdx = -1,
            quantityIdx = -1, purchasePriceIdx = -1, markupIdx = -1, priceIdx = -1, weightIdx = -1;
        var scanned = 0;
        var firstSeen = "";

        foreach (var (lineNo, cells) in ProductFileReader.Records(csvText, delimiter))
        {
            if (cells.All(c => c.Length == 0))
                continue;

            if (!headerFound)
            {
                // Первая строка «sep=;» — служебная строка Excel, не заголовок.
                if (cells.Length == 1 && cells[0].StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
                    continue;
                scanned++;
                var h = cells.Select(NormalizeHeader).ToList();
                if (scanned == 1)
                    firstSeen = string.Join(", ", cells.Where(c => c.Length > 0).Take(8).Select(c => Clean(c)));
                var n = IndexOfAny(h, NameHeaders);
                var b = IndexOfAny(h, BarcodeHeaders);
                if (n >= 0 && b >= 0)
                {
                    headerFound = true;
                    nameIdx = n;
                    barcodeIdx = b;
                    articleIdx = IndexOfAny(h, ArticleHeaders);
                    categoryIdx = IndexOfAny(h, CategoryHeaders);
                    brandIdx = IndexOfAny(h, BrandHeaders);
                    unitIdx = IndexOfAny(h, UnitHeaders);
                    quantityIdx = IndexOfAny(h, QuantityHeaders);
                    purchasePriceIdx = IndexOfAny(h, PurchasePriceHeaders);
                    markupIdx = IndexOfAny(h, MarkupHeaders);
                    priceIdx = IndexOfAny(h, PriceHeaders);
                    weightIdx = IndexOfAny(h, WeightHeaders);
                }
                else if (scanned >= 20)
                {
                    break;
                }
                continue;
            }

            if (result.Rows.Count >= MaxRows)
            {
                result.Rows.Clear();
                result.Error = Tr.T($"В файле больше {MaxRows:N0} строк. Разделите его на части.", $"Файлда {MaxRows:N0} саптан ашык. Бөлүктөргө бөлүңүз.",
                    $"The file has more than {MaxRows:N0} rows. Split it into parts.", $"Dosyada {MaxRows:N0} satırdan fazla var. Parçalara bölün.",
                    $"Faylda {MaxRows:N0} qatordan ko'p. Uni qismlarga bo'ling.");
                return;
            }

            try
            {
                result.Rows.Add(BuildRow(lineNo, cells, nameIdx, barcodeIdx, articleIdx, categoryIdx, brandIdx, unitIdx,
                    quantityIdx, purchasePriceIdx, markupIdx, priceIdx, weightIdx));
            }
            catch (Exception ex)
            {
                // Одна кривая строка не должна ронять весь файл.
                PosLogger.Log($"Массовая загрузка: строка {lineNo} не разобрана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                result.Rows.Add(new ImportRow
                {
                    RowNumber = lineNo,
                    ParseError = Tr.T($"не удалось разобрать строку: {ex.Message}", $"сапты талдоо мүмкүн болгон жок: {ex.Message}",
                        $"could not parse the row: {ex.Message}", $"satır ayrıştırılamadı: {ex.Message}", $"qatorni tahlil qilib bo'lmadi: {ex.Message}"),
                });
            }
        }

        if (!headerFound)
        {
            result.Error = Tr.T($"Не найдена строка заголовков со столбцами «Название» и «Штрихкод». В первой строке: {firstSeen}. Скачайте шаблон и сверьте заголовки.",
                $"«Аталышы» жана «Штрихкод» мамычалары бар аталыш сабы табылган жок. Биринчи сапта: {firstSeen}. Үлгүнү жүктөп алып, аталыштарды салыштырыңыз.",
                $"No header row with “Name” and “Barcode” columns found. First row: {firstSeen}. Download the template and compare the headers.",
                $"«Ad» ve «Barkod» sütunlarını içeren başlık satırı bulunamadı. İlk satır: {firstSeen}. Şablonu indirip başlıkları karşılaştırın.",
                $"«Nomi» va «Shtrix-kod» ustunlari bor sarlavha qatori topilmadi. Birinchi qator: {firstSeen}. Shablonni yuklab, sarlavhalarni solishtiring.");
            return;
        }
        if (result.Rows.Count == 0)
            result.Error = Tr.T("В файле есть заголовки, но нет ни одной строки с товарами.", "Файлда аталыштар бар, бирок товар саптары жок.",
                "The file has headers but no product rows.", "Dosyada başlıklar var ama ürün satırı yok.", "Faylda sarlavhalar bor, lekin mahsulot qatorlari yo'q.");
    }

    private static ImportRow BuildRow(int lineNo, string[] cells, int nameIdx, int barcodeIdx, int articleIdx, int categoryIdx,
        int brandIdx, int unitIdx, int quantityIdx, int purchasePriceIdx, int markupIdx, int priceIdx, int weightIdx)
    {
        string Field(int idx) => idx >= 0 && idx < cells.Length ? Clean(cells[idx]).Trim() : "";

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
            if (!TryParseNumber(cell, out var value) || double.IsNaN(value) || double.IsInfinity(value))
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
        var barcode = NormalizeBarcode(barcodeIdx >= 0 && barcodeIdx < cells.Length ? cells[barcodeIdx] : "", out var barcodeProblem);
        var quantity = Optional(quantityIdx, "количество", 1e9);
        var purchasePrice = Optional(purchasePriceIdx, "закупка", 1e8);
        var markup = Optional(markupIdx, "наценка", 1e6, allowNegative: true);
        var price = Optional(priceIdx, "цена", 1e8);

        // Пределы сервера (OPTIONS api/main/products/create-manual/): название до 255
        // символов, штрихкод до 64. Длиннее — сервер отвечал 500 без объяснений.
        string? error = null;
        if (string.IsNullOrWhiteSpace(name))
            error = "нет названия";
        else if (barcodeProblem != null)
            error = barcodeProblem;
        else if (string.IsNullOrWhiteSpace(barcode))
            error = "нет штрихкода";
        else if (name.Length > 255)
            error = $"название длиннее 255 символов ({name.Length})";
        else if (barcode.Length > 64)
            error = "штрихкод длиннее 64 символов";
        else if (problems.Count > 0)
            error = string.Join("; ", problems);

        var unit = Field(unitIdx);
        return new ImportRow
        {
            RowNumber = lineNo,
            Name = name,
            Barcode = barcode,
            Article = NullIfEmpty(Field(articleIdx)),
            Category = NullIfEmpty(Field(categoryIdx)),
            Brand = NullIfEmpty(Field(brandIdx)),
            Unit = string.IsNullOrWhiteSpace(unit) ? "шт" : unit,
            Quantity = quantity,
            PurchasePrice = purchasePrice,
            MarkupPercent = markup,
            Price = price,
            IsWeight = ParseBool(Field(weightIdx)),
            ParseError = error,
        };
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
}
