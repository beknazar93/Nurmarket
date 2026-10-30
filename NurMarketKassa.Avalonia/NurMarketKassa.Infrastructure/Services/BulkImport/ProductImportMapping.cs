using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-10-26, ТЗ «Склад — массовое добавление» (М3): поля загрузки товаров из файла, автоподбор столбцов по заголовкам
// (синонимы старой «Массовой загрузки» + мобилки, 5 языков) и память сопоставлений «тот же поставщик — без ручного выбора».
// Без интерфейса и сети.

/// <summary>Поле, которое можно загрузить из столбца файла.</summary>
public enum ProductImportField
{
    Barcode,
    Name,
    Quantity,
    Purchase,
    Sale,
    Markup,
    Category,
    Unit,
    Weight,
    Article,
    Brand,
    /// <summary>2026-10-30: «Доп. штрихкоды» — несколько значений через ; , пробел | или перенос строки.</summary>
    AltBarcodes,
}

/// <summary>2026-10-30: насколько автоподбор уверен в столбце (для подсветки спорных в окне).</summary>
public enum MappingConfidence
{
    None = 0,
    /// <summary>Угадано по данным столбца, заголовок не узнан.</summary>
    Low = 1,
    /// <summary>Заголовок содержит слово поля или сопоставление из памяти.</summary>
    Medium = 2,
    /// <summary>Заголовок точно совпал или столбец выбран вручную.</summary>
    High = 3,
}

/// <summary>Какой столбец файла (с нуля) какому полю соответствует. Один столбец — одно поле.</summary>
public sealed class ProductImportMapping
{
    private readonly Dictionary<ProductImportField, int> _columns = new();
    private readonly Dictionary<ProductImportField, MappingConfidence> _confidence = new();

    /// <summary>Сопоставление взято из памяти (по «подписи» заголовков), а не подобрано заново.</summary>
    public bool FromMemory { get; private set; }

    public IReadOnlyDictionary<ProductImportField, int> Columns => _columns;

    /// <summary>Столбец поля; -1 — поле не загружается.</summary>
    public int ColumnOf(ProductImportField field) => _columns.TryGetValue(field, out var c) ? c : -1;

    /// <summary>2026-10-30: уверенность автоподбора в столбце поля (ручной выбор — High).</summary>
    public MappingConfidence ConfidenceOf(ProductImportField field) =>
        _columns.ContainsKey(field) ? (_confidence.TryGetValue(field, out var c) ? c : MappingConfidence.High) : MappingConfidence.None;

    /// <summary>Назначить поле столбцу (-1 — «не загружать»). Столбец, занятый другим полем, у него отнимается.</summary>
    public void Set(ProductImportField field, int column) => Set(field, column, MappingConfidence.High);

    private void Set(ProductImportField field, int column, MappingConfidence confidence)
    {
        if (column < 0)
        {
            _columns.Remove(field);
            _confidence.Remove(field);
            return;
        }

        foreach (var other in _columns.Where(kv => kv.Value == column && kv.Key != field).Select(kv => kv.Key).ToList())
        {
            _columns.Remove(other);
            _confidence.Remove(other);
        }
        _columns[field] = column;
        _confidence[field] = confidence;
    }

    /// <summary>Обязательные поля, которые не сопоставлены. 2026-10-30: только штрихкод — файл без количества это законная загрузка
    /// «только карточки и цены» (остаток не меняется). Название нужно только новым товарам — его отсутствие плану видно по строкам.</summary>
    public IReadOnlyList<ProductImportField> MissingRequired
    {
        get
        {
            var list = new List<ProductImportField>();
            if (!_columns.ContainsKey(ProductImportField.Barcode))
                list.Add(ProductImportField.Barcode);
            return list;
        }
    }

    /// <summary>2026-10-30: столбец количества выбран (иначе загрузка идёт режимом «только карточки и цены»).</summary>
    public bool HasQuantity => _columns.ContainsKey(ProductImportField.Quantity);

    public bool IsComplete => MissingRequired.Count == 0;

    public static string FieldLabel(ProductImportField field) => field switch
    {
        ProductImportField.Barcode => Tr.T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"),
        ProductImportField.Name => Tr.T("Название", "Аталышы", "Name", "Ürün adı", "Nomi"),
        ProductImportField.Quantity => Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor"),
        ProductImportField.Purchase => Tr.T("Закупка", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"),
        ProductImportField.Sale => Tr.T("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi"),
        ProductImportField.Markup => Tr.T("Наценка %", "Үстөк %", "Markup %", "Kâr marjı %", "Ustama %"),
        ProductImportField.Category => Tr.T("Категория", "Категория", "Category", "Kategori", "Kategoriya"),
        ProductImportField.Unit => Tr.T("Единица", "Өлчөм бирдиги", "Unit", "Birim", "Birlik"),
        ProductImportField.Weight => Tr.T("Весовой", "Весовой", "By weight", "Tartılı", "Vaznli"),
        ProductImportField.Article => Tr.T("Артикул", "Артикул", "Article", "Ürün kodu", "Artikul"),
        ProductImportField.AltBarcodes => Tr.T("Доп. штрихкоды", "Кошумча штрих-коддор", "Extra barcodes", "Ek barkodlar", "Qo'shimcha shtrix-kodlar"),
        _ => Tr.T("Бренд", "Бренд", "Brand", "Marka", "Brend"),
    };

    // ------------------------------------------------------------------ автоподбор

    // Заголовок приводится к «ключу»: только буквы и цифры, нижний регистр, ё = е. «Штрих-код», «штрих код» и «Штрихкод»
    // дают один ключ; «Наценка, %» → «наценка». Точное совпадение важнее частичного: «цена закупки» не становится «ценой».
    private static readonly Dictionary<ProductImportField, string[]> Exact = new()
    {
        [ProductImportField.Barcode] = ["штрихкод", "штрихкоды", "шк", "barcode", "barcodes", "ean", "ean13", "ean8", "gtin", "upc", "баркод", "штрихкодтовара",
            "штрихкодтоваров", "barkod", "shtrixkod", "shtrixkodi", "штрихкоду", "основнойштрихкод", "штрихкодосновной", "кодштрихкод"],
        [ProductImportField.AltBarcodes] = ["допштрихкоды", "допштрихкод", "допшк", "дополнительныештрихкоды", "дополнительныйштрихкод", "альтернативныештрихкоды",
            "альтернативныйштрихкод", "altbarcodes", "altbarcode", "alternatebarcodes", "alternatebarcode", "additionalbarcodes", "extrabarcodes", "ekbarkodlar",
            "кошумчаштрихкоддор", "qoshimchashtrixkodlar", "штрихкоддоп", "допкод", "допкоды"],
        [ProductImportField.Name] = ["название", "наименование", "товар", "имя", "name", "продукт", "product", "номенклатура", "названиетовара", "наименованиетовара",
            "аталышы", "аты", "товардунаталышы", "nomi", "nom", "mahsulotnomi", "mahsulot", "ürünadı", "ürünadi", "ürün", "urunadi", "ad", "title"],
        [ProductImportField.Quantity] = ["количество", "колво", "кол", "qty", "quantity", "остаток", "остатки", "приход", "штук", "саны", "сан", "miktar", "soni", "miqdor",
            "adet", "количествошт", "колвошт", "количествотовара"],
        [ProductImportField.Purchase] = ["закупка", "закупочная", "закупочнаяцена", "ценазакупки", "ценазакупка", "себестоимость", "входящаяцена", "входнаяцена", "purchase",
            "purchaseprice", "cost", "costprice", "сатыпалуубаасы", "сатыпалуу", "alışfiyatı", "alisfiyati", "xarid", "xaridnarxi", "ценаприхода", "ценапоставщика",
            "приходнаяцена", "закупочнаястоимость", "входная", "входящая", "закуп", "закупки", "ценазакуп"],
        [ProductImportField.Sale] = ["цена", "ценапродажи", "продажа", "розница", "розничнаяцена", "ценарозничная", "price", "saleprice", "retail", "retailprice",
            "сатуубаасы", "баасы", "баа", "satışfiyatı", "satisfiyati", "fiyat", "sotuvnarxi", "narx", "narxi", "ценазапродажу", "ценареализации",
            "прайс", "розничная", "рознцена", "ценапрайс", "продажная", "продажнаяцена"],
        [ProductImportField.Markup] = ["наценка", "наценкапроцент", "процентнаценки", "markup", "маржа", "үстөк", "үстөкакы", "ustama", "kârmarjı", "karmarji", "margin"],
        [ProductImportField.Category] = ["категория", "категории", "группа", "группатовара", "категориятовара", "раздел", "category", "group", "санат", "категориясы", "топ",
            "kategori", "kategoriya", "toifa", "guruh"],
        [ProductImportField.Unit] = ["единица", "ед", "едизм", "единицаизмерения", "единицы", "едизмерения", "unit", "uom", "өлчөмбирдиги", "өлчөм", "бирдик", "birim",
            "birlik", "олчовбирлиги", "бирдиги", "олчөмбирдиги", "olchovbirligi", "oʻlchovbirligi", "olchov", "unitofmeasure", "measure", "еденицаизмерения", "единицаизм", "изм"],
        [ProductImportField.Weight] = ["весовой", "весовойтовар", "весовые", "isweight", "weight", "weighted", "weighable", "byweight", "tartılı", "tartili", "vaznli"],
        [ProductImportField.Article] = ["артикул", "art", "код", "кодтовара", "article", "sku", "артикултовара", "kod", "ürünkodu", "urunkodu", "mahsulotkodi", "artikul"],
        [ProductImportField.Brand] = ["бренд", "марка", "производитель", "brand", "изготовитель", "marka", "brend", "üretici", "ishlabchiqaruvchi"],
    };

    // Частичное совпадение (ключ заголовка СОДЕРЖИТ слово). Порядок полей важен: сначала более «узкие» (закупка до продажи,
    // категория до названия), чтобы «Название категории» стало категорией, а «Цена закупки, сом» — закупкой.
    private static readonly (ProductImportField Field, string[] Words)[] Partial =
    [
        (ProductImportField.Purchase, ["закупк", "закупочн", "себестоим", "purchase", "costprice", "сатыпалуу", "alışfiyat", "alisfiyat", "xarid", "входящ", "входн", "ценаприход"]),
        (ProductImportField.Markup, ["наценк", "markup", "ustama", "үстөк"]),
        (ProductImportField.Sale, ["ценапродаж", "продажн", "розничн", "розниц", "saleprice", "retail", "сатуубаас", "satışfiyat", "satisfiyat", "sotuvnarx", "ценареализ"]),
        (ProductImportField.AltBarcodes, ["допштрихкод", "допшк", "дополнительныештрихкод", "дополнительныйштрихкод", "альтернативныештрихкод", "alternatebarcode", "altbarcode",
            "additionalbarcode", "extrabarcode", "ekbarkod", "кошумчаштрих", "qoshimchashtrix"]),
        (ProductImportField.Barcode, ["штрихкод", "barcode", "barkod", "shtrixkod"]),
        (ProductImportField.Quantity, ["количеств", "колво", "quantity", "остаток", "саны", "miktar", "miqdor"]),
        (ProductImportField.Category, ["категори", "групп", "category", "санат", "kategori", "toifa", "guruh"]),
        (ProductImportField.Unit, ["единиц", "едизм", "өлчөм", "birim", "birlik", "бирдик"]),
        (ProductImportField.Brand, ["бренд", "brand", "производител", "marka", "brend"]),
        (ProductImportField.Weight, ["весов"]),
        (ProductImportField.Article, ["артикул", "article", "кодтовара", "artikul"]),
        (ProductImportField.Name, ["название", "наименование", "номенклатура", "аталышы", "productname", "mahsulotnomi", "ürünadı", "nomi"]),
    ];

    private static readonly Dictionary<string, ProductImportField> ExactLookup = BuildExactLookup();

    private static Dictionary<string, ProductImportField> BuildExactLookup()
    {
        var map = new Dictionary<string, ProductImportField>(StringComparer.Ordinal);
        foreach (var (field, words) in Exact)
        {
            foreach (var word in words)
                map.TryAdd(word, field);
        }
        return map;
    }

    /// <summary>Только буквы и цифры, нижний регистр, «ё» = «е».</summary>
    public static string Key(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return "";
        var sb = new StringBuilder(header.Length);
        foreach (var ch in header)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;
            var lower = char.ToLowerInvariant(ch);
            sb.Append(lower == 'ё' ? 'е' : lower);
        }
        return sb.ToString();
    }

    /// <summary>Подобрать столбцы по заголовкам. 2026-10-30: с таблицей — ещё и проверка по данным (столбец с дробными суммами — не количество,
    /// столбец с числами — не единица; штрихкод/название ищутся по содержимому, если заголовок не узнан).</summary>
    public static ProductImportMapping AutoDetect(IReadOnlyList<string> headers, ProductFileTable? table = null)
    {
        var result = new ProductImportMapping();
        var keys = new string[headers.Count];
        for (var i = 0; i < keys.Length; i++)
            keys[i] = Key(headers[i]);

        // 1. Точное совпадение, слева направо; второй столбец того же поля не берём.
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i].Length > 0 && ExactLookup.TryGetValue(keys[i], out var field) && !result._columns.ContainsKey(field))
                result.Set(field, i, MappingConfidence.High);
        }

        // 2. Частичное: заголовок содержит слово поля («Цена закупки, сом», «Название товара (рус.)»).
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i].Length == 0 || result._columns.ContainsValue(i))
                continue;
            foreach (var (field, words) in Partial)
            {
                if (result._columns.ContainsKey(field) || !words.Any(w => keys[i].Contains(w, StringComparison.Ordinal)))
                    continue;
                result.Set(field, i, MappingConfidence.Medium);
                break;
            }
        }

        // 3. Заголовок, начинающийся с «цена»/«price» («Цена, сом»), — цена продажи, если закупка и продажа уже не найдены другими.
        for (var i = 0; i < keys.Length && !result._columns.ContainsKey(ProductImportField.Sale); i++)
        {
            if (!result._columns.ContainsValue(i) && (keys[i].StartsWith("цена", StringComparison.Ordinal) || keys[i].StartsWith("price", StringComparison.Ordinal)))
                result.Set(ProductImportField.Sale, i, MappingConfidence.Medium);
        }

        if (table is not null)
            result.Refine(table);
        return result;
    }

    // ------------------------------------------------------------------ проверка по данным (2026-10-30)

    private sealed class ColumnStats
    {
        public int NonEmpty, Numeric, Fractional, Digits4To14, Digits8To14, TextLength;
    }

    private const int SampleRows = 300;

    private static ColumnStats[] Stats(ProductFileTable table)
    {
        var stats = new ColumnStats[table.ColumnCount];
        for (var i = 0; i < stats.Length; i++)
            stats[i] = new ColumnStats();
        var seen = 0;
        foreach (var row in table.DataRows)
        {
            if (seen++ >= SampleRows)
                break;
            for (var c = 0; c < stats.Length; c++)
            {
                var cell = row[c].Trim();
                if (cell.Length == 0)
                    continue;
                var st = stats[c];
                st.NonEmpty++;
                st.TextLength += cell.Length;
                if (ProductCsvImporter.TryParseNumber(cell, out var number))
                {
                    st.Numeric++;
                    if (Math.Abs(number - Math.Round(number)) > 0.0001)
                        st.Fractional++;
                }
                if (cell.Length is >= 4 and <= 14 && cell.All(char.IsDigit))
                {
                    st.Digits4To14++;
                    if (cell.Length >= 8)
                        st.Digits8To14++;
                }
            }
        }
        return stats;
    }

    /// <summary>Поправить сопоставление по содержимому столбцов: количество не бывает дробной суммой с копейками, единица не число;
    /// штрихкод — столбец из 8–14 цифр, название — самый «текстовый» столбец; цена продажи — единственный свободный числовой при найденной закупке.</summary>
    public void Refine(ProductFileTable table)
    {
        if (table.DataRowCount == 0 || table.ColumnCount == 0)
            return;
        var stats = Stats(table);
        ColumnStats? Of(ProductImportField f) => _columns.TryGetValue(f, out var c) && c < stats.Length ? stats[c] : null;

        // Количество: если заголовок не «точный», а 40 %+ значений — дроби вроде 65,5 / 125,9, это сумма (цена), а не количество.
        if (Of(ProductImportField.Quantity) is { NonEmpty: > 0 } q && ConfidenceOf(ProductImportField.Quantity) != MappingConfidence.High
            && q.Fractional * 100 >= q.NonEmpty * 40)
        {
            var col = _columns[ProductImportField.Quantity];
            Set(ProductImportField.Quantity, -1);
            if (!_columns.ContainsKey(ProductImportField.Sale) && q.Numeric * 100 >= q.NonEmpty * 80)
                Set(ProductImportField.Sale, col, MappingConfidence.Low);
        }

        // Единица — текст (шт, кг): столбец, где почти всё числа, единицей быть не может.
        if (Of(ProductImportField.Unit) is { NonEmpty: > 0 } u && u.Numeric * 100 >= u.NonEmpty * 70)
        {
            var col = _columns[ProductImportField.Unit];
            Set(ProductImportField.Unit, -1);
            if (!_columns.ContainsKey(ProductImportField.Purchase))
                Set(ProductImportField.Purchase, col, MappingConfidence.Low);
            else if (!_columns.ContainsKey(ProductImportField.Sale))
                Set(ProductImportField.Sale, col, MappingConfidence.Low);
        }

        // Штрихкод не узнан по заголовку — берём столбец, где 70 %+ значений из 8–14 цифр.
        if (!_columns.ContainsKey(ProductImportField.Barcode))
        {
            for (var c = 0; c < stats.Length; c++)
            {
                if (_columns.ContainsValue(c) || stats[c].NonEmpty == 0 || stats[c].Digits8To14 * 100 < stats[c].NonEmpty * 70)
                    continue;
                Set(ProductImportField.Barcode, c, MappingConfidence.Low);
                break;
            }
        }
        // Штрихкод по заголовку, но в столбце почти нет цифровых кодов — пониженная уверенность (окно подсветит).
        else if (Of(ProductImportField.Barcode) is { NonEmpty: > 0 } b && b.Digits4To14 * 100 < b.NonEmpty * 50 && ConfidenceOf(ProductImportField.Barcode) == MappingConfidence.High)
            _confidence[ProductImportField.Barcode] = MappingConfidence.Medium;

        // Название не узнано — самый «длинный текстовый» свободный столбец.
        if (!_columns.ContainsKey(ProductImportField.Name))
        {
            var best = -1;
            double bestLen = 0;
            for (var c = 0; c < stats.Length; c++)
            {
                var st = stats[c];
                if (_columns.ContainsValue(c) || st.NonEmpty == 0 || st.Numeric * 100 > st.NonEmpty * 30)
                    continue;
                var avg = (double)st.TextLength / st.NonEmpty;
                if (avg > bestLen)
                {
                    bestLen = avg;
                    best = c;
                }
            }
            if (best >= 0 && bestLen >= 4)
                Set(ProductImportField.Name, best, MappingConfidence.Low);
        }

        // Закупка найдена, цены продажи нет, и свободный числовой столбец ровно один — это цена продажи (с пометкой «проверьте»).
        if (_columns.ContainsKey(ProductImportField.Purchase) && !_columns.ContainsKey(ProductImportField.Sale) && !_columns.ContainsKey(ProductImportField.Markup))
        {
            var free = Enumerable.Range(0, stats.Length).Where(c => !_columns.ContainsValue(c) && stats[c].NonEmpty > 0 && stats[c].Numeric * 100 >= stats[c].NonEmpty * 90).ToList();
            if (free.Count == 1)
                Set(ProductImportField.Sale, free[0], MappingConfidence.Low);
        }
    }

    /// <summary>Сколько разных полей узнано в строке (для поиска строки заголовков).</summary>
    public static int CountRecognized(IReadOnlyList<string> cells) => AutoDetect(cells).Columns.Count;

    // ------------------------------------------------------------------ память

    /// <summary>«Подпись» заголовков: одинаковые заголовки в том же порядке (тот же поставщик) дают одну подпись.</summary>
    public static string Signature(IReadOnlyList<string> headers)
    {
        var sb = new StringBuilder();
        foreach (var header in headers)
            sb.Append(Key(header)).Append('|');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>Сопоставление из памяти, если такие заголовки уже загружали; иначе подбор по заголовкам.</summary>
    public static ProductImportMapping Resolve(IReadOnlyList<string> headers, ProductFileTable? table = null)
    {
        var auto = AutoDetect(headers, table);
        var remembered = ProductImportMappingStore.TryLoad(Signature(headers), headers.Count);
        if (remembered is null)
            return auto;
        // 2026-10-30: память могла сохранить неверный выбор (у владельца «Количество = Цена» — столбец «Цена» ушёл из цены продажи). Столбцы,
        // которые заголовок называет точно, главнее памяти; остальное — из памяти.
        foreach (var (field, column) in auto.Columns.ToList())
        {
            if (auto.ConfidenceOf(field) == MappingConfidence.High)
                remembered.Set(field, column, MappingConfidence.High);
        }
        if (table is not null)
            remembered.Refine(table);
        return remembered;
    }

    /// <summary>Запомнить это сопоставление для таких же заголовков.</summary>
    public void Remember(IReadOnlyList<string> headers) =>
        ProductImportMappingStore.Save(Signature(headers), this);

    internal static ProductImportMapping FromStored(Dictionary<string, int> stored, int headerCount)
    {
        var result = new ProductImportMapping { FromMemory = true };
        foreach (var (name, column) in stored)
        {
            if (Enum.TryParse<ProductImportField>(name, out var field) && column >= 0 && column < headerCount)
            {
                result._columns[field] = column;
                result._confidence[field] = MappingConfidence.Medium;
            }
        }
        return result;
    }
}

/// <summary>Память сопоставлений столбцов: файл рядом с черновиками массового добавления.</summary>
public static class ProductImportMappingStore
{
    private const int MaxEntries = 50;

    /// <summary>Только для стенда проверки: другая папка (не %APPDATA%).</summary>
    public static string? DataDirOverride { get; set; }

    private static string DataDir => DataDirOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NurMarketKassa", "purchases");

    private static string FilePath => Path.Combine(DataDir, "import-mappings.json");
    private static readonly object Sync = new();

    private sealed class Entry
    {
        public DateTime SavedAt { get; set; }
        public Dictionary<string, int> Columns { get; set; } = new();
    }

    private static Dictionary<string, Entry> Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Импорт товаров: память сопоставлений не прочитана ({ex.Message}).", "WARNING");
        }
        return new();
    }

    internal static ProductImportMapping? TryLoad(string signature, int headerCount)
    {
        lock (Sync)
        {
            if (!Read().TryGetValue(signature, out var entry) || entry.Columns.Count == 0)
                return null;
            var mapping = ProductImportMapping.FromStored(entry.Columns, headerCount);
            return mapping.Columns.Count == 0 ? null : mapping;
        }
    }

    internal static void Save(string signature, ProductImportMapping mapping)
    {
        lock (Sync)
        {
            try
            {
                var all = Read();
                all[signature] = new Entry
                {
                    SavedAt = DateTime.Now,
                    Columns = mapping.Columns.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                };
                foreach (var old in all.OrderByDescending(kv => kv.Value.SavedAt).Skip(MaxEntries).Select(kv => kv.Key).ToList())
                    all.Remove(old);

                Directory.CreateDirectory(DataDir);
                // Атомарно: сначала во временный файл, потом подмена (как у черновика).
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Импорт товаров: сопоставление не сохранено ({ex.Message}).", "WARNING");
            }
        }
    }
}
