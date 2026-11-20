using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-20 (ТЗ «Сфера Автозапчасти», пакеты импорта; образцы docs/Образцы/Автозапчасти): профиль «Прайс поставщика (автозапчасти)» для
// «Массовой загрузки». Прайсы поставщиков не имеют единого вида: шапка из нескольких строк, строки-группы без цены, остаток диапазоном
// («20–50», «менее 8»), название из нескольких столбцов, номер детали вместо штрихкода. Здесь — чистая логика (без окон и сети):
// найти строку заголовков, сопоставить столбцы по синонимам, очистить строки и подготовить таблицу для обычного BulkImportPlan.
// Поля запчасти (oem/cross/fits/condition/part_type/ячейка/гарантия) пишутся тем же путём, что в карточке товара: AutoPartInfo →
// ProductEditRequest.Attributes / StorageLocation (PartMetaBody), PartMetaStore, AutoPartsProductMeta.

/// <summary>Роль столбца прайса автозапчастей.</summary>
public enum PartColumn
{
    Barcode, PartNo, Name, Brand, Oem, Cross, Fits, Location, Condition, Warranty, Stock, Sale, Purchase,
}

public enum AutoPartsRowStatus { New, Update, Skipped }

/// <summary>Параметры подготовки: какие столбцы склеить в название (null — по умолчанию) и обновлять ли остаток у существующих.</summary>
public sealed class AutoPartsPriceOptions
{
    public IReadOnlyCollection<int>? NameColumns { get; set; }
    public bool UpdateStock { get; set; } = true;
}

/// <summary>Данные запчасти одной строки прайса: уходят вместе со строкой плана (<see cref="BulkImportRow.Extra"/>) в загрузчик карточек.</summary>
public sealed class AutoPartsRowData
{
    public string Brand { get; init; } = "";
    public string Article { get; init; } = "";
    public AutoPartInfo Info { get; init; } = new();
    public int? WarrantyDays { get; init; }
    public double? Stock { get; init; }
    public bool IsNew { get; init; }
    /// <summary>Что у существующего товара надо записать (у нового пишется всё).</summary>
    public bool NeedsAttrs { get; init; }
    public bool NeedsLocation { get; init; }
    public bool NeedsBrand { get; init; }
    public bool NeedsArticle { get; init; }
    public bool NeedsStock { get; init; }
    public bool NeedsWarranty { get; init; }
    public string ProductName { get; init; } = "";

    public bool NeedsWrite => !IsNew && (NeedsAttrs || NeedsLocation || NeedsBrand || NeedsArticle || NeedsStock || NeedsWarranty);

    /// <summary>Новый товар: поля запчасти в тело создания (как карточка: Attributes, StorageLocation, BrandName).</summary>
    public void ApplyToCreate(ProductEditRequest r)
    {
        if (Brand.Length > 0)
            r.BrandName = Brand;
        if (Article.Length > 0)
            r.Article = Article;
        if (Info.BuildAttributes() is { Count: > 0 } attrs)
            r.Attributes = attrs;
        if (Info.Location.Length > 0)
        {
            r.SendStorageLocation = true;
            r.StorageLocation = Info.Location;
        }
        if (Stock is { } stock)
        {
            r.Quantity = stock;
            r.SendQuantity = true;
        }
    }

    /// <summary>Существующий товар: только изменившиеся поля — в PATCH (формат тех же ключей, что у PartMetaBody).</summary>
    public void AddPatchFields(IDictionary<string, object?> fields)
    {
        if (NeedsBrand)
            fields["brand_name"] = Brand;
        if (NeedsArticle)
            fields["article"] = Article;
        if (NeedsAttrs)
            fields["attributes"] = Info.BuildAttributes().Select(a => (object)new Dictionary<string, object?> { ["key"] = a.Key, ["value"] = a.Value }).ToArray();
        if (NeedsLocation)
            fields["storage_location"] = Info.Location;
        if (NeedsStock && Stock is { } stock)
            fields["quantity"] = stock;
    }

    /// <summary>После записи на сервер: запомнить данные запчасти и гарантию по id товара (как карточка товара). Файл PartMetaStore
    /// пишется один раз в конце загрузки (<see cref="PartMetaStore.SaveIfDirty"/>).</summary>
    public void AfterSaved(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        if (IsNew || NeedsAttrs || NeedsLocation)
            PartMetaStore.Set(productId, Info);
        if ((IsNew || NeedsWarranty) && WarrantyDays is > 0)
            AutoPartsProductMeta.Set(productId, WarrantyDays, AutoPartsProductMeta.Get(productId)?.Electric ?? AutoPartsRules.LooksElectric(ProductName),
                AutoPartsProductMeta.Get(productId)?.NoReturn ?? false);
    }

    /// <summary>id товара из ответа создания.</summary>
    public static string? IdOf(JsonElement created) =>
        created.ValueKind == JsonValueKind.Object && created.TryGetProperty("id", out var id)
            ? (id.ValueKind == JsonValueKind.String ? id.GetString() : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null)
            : null;
}

/// <summary>Одна строка прайса после разбора (для предпросмотра и таблицы плана).</summary>
public sealed class AutoPartsPreparedRow
{
    public int FileRow { get; init; }
    public AutoPartsRowStatus Status { get; init; }
    public string SkipReason { get; init; } = "";
    public string Name { get; init; } = "";
    public string PartNo { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Barcode { get; init; } = "";
    public double? Stock { get; init; }
    /// <summary>Пометка остатка («20–50 → 20», «есть → 1»).</summary>
    public string StockNote { get; init; } = "";
    public double Sale { get; init; }
    public double Purchase { get; init; }
    public AutoPartsRowData? Data { get; init; }
}

/// <summary>Результат подготовки прайса.</summary>
public sealed class AutoPartsPrepared
{
    public IReadOnlyList<AutoPartsPreparedRow> Rows { get; init; } = [];
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Skipped { get; init; }
    /// <summary>Сколько остатков записано приблизительно (диапазон, «менее», «есть/нет»).</summary>
    public int ApproxStock { get; init; }
    public IReadOnlyDictionary<PartColumn, int> Columns { get; init; } = new Dictionary<PartColumn, int>();
    public IReadOnlyList<int> NameColumns { get; init; } = [];
    public string FileName { get; init; } = "";

    public string SummaryText => Tr.T($"Будет создано {Created}, обновлено {Updated}, пропущено {Skipped}",
        $"Түзүлөт {Created}, жаңыртылат {Updated}, өткөрүлөт {Skipped}",
        $"Will create {Created}, update {Updated}, skip {Skipped}",
        $"Oluşturulacak {Created}, güncellenecek {Updated}, atlanacak {Skipped}",
        $"Yaratiladi {Created}, yangilanadi {Updated}, o'tkaziladi {Skipped}")
        + (ApproxStock > 0 ? Tr.T($" · остаток приблизительно (диапазон/«менее»/«есть»): {ApproxStock}", $" · калдык болжолдуу: {ApproxStock}",
            $" · approximate stock (range/“less than”/“in stock”): {ApproxStock}", $" · yaklaşık stok: {ApproxStock}", $" · taxminiy qoldiq: {ApproxStock}") : "");

    /// <summary>Таблица для обычного плана загрузки: Штрихкод, Название, Закупка, Цена продажи, Артикул, Бренд. Пропущенные строки не входят.
    /// Данные запчасти едут в <see cref="ProductFileRow.Tag"/>; остаток пишет загрузчик сам (план остаётся «только карточки и цены»).</summary>
    public ProductFileTable ToTable()
    {
        var rows = new List<ProductFileRow>
        {
            new(0, [PartLabel.Barcode, PartLabel.Name, PartLabel.Purchase, PartLabel.Sale, PartLabel.Article, PartLabel.Brand], 0),
        };
        foreach (var r in Rows.Where(x => x.Status != AutoPartsRowStatus.Skipped))
        {
            rows.Add(new ProductFileRow(r.FileRow, [r.Barcode, r.Name,
                r.Purchase > 0 ? r.Purchase.ToString("0.###", CultureInfo.InvariantCulture) : "",
                r.Sale.ToString("0.###", CultureInfo.InvariantCulture), r.PartNo, r.Brand], 0) { Tag = r.Data });
        }
        var table = new ProductFileTable { Kind = ProductFileKind.Csv, FileName = FileName, Rows = rows, ColumnCount = 6 };
        table.SetHeaderRow(0);
        return table;
    }

    /// <summary>Сопоставление столбцов prepared-таблицы (<see cref="ToTable"/>).</summary>
    public static ProductImportMapping TableMapping()
    {
        var m = new ProductImportMapping();
        m.Set(ProductImportField.Barcode, 0);
        m.Set(ProductImportField.Name, 1);
        m.Set(ProductImportField.Purchase, 2);
        m.Set(ProductImportField.Sale, 3);
        m.Set(ProductImportField.Article, 4);
        m.Set(ProductImportField.Brand, 5);
        return m;
    }

    private static class PartLabel
    {
        public static string Barcode => ProductImportMapping.FieldLabel(ProductImportField.Barcode);
        public static string Name => ProductImportMapping.FieldLabel(ProductImportField.Name);
        public static string Purchase => ProductImportMapping.FieldLabel(ProductImportField.Purchase);
        public static string Sale => ProductImportMapping.FieldLabel(ProductImportField.Sale);
        public static string Article => ProductImportMapping.FieldLabel(ProductImportField.Article);
        public static string Brand => ProductImportMapping.FieldLabel(ProductImportField.Brand);
    }
}

public static class AutoPartsPriceImport
{
    public const string ProfileId = "autoparts-price";
    public const int PreviewRows = 200;
    private const int HeaderScan = 30;

    public static string ProfileName => Tr.T("Прайс поставщика (автозапчасти)", "Жеткирүүчүнүн прайси (унаа тетиктери)", "Supplier price list (auto parts)",
        "Tedarikçi fiyat listesi (oto parça)", "Yetkazib beruvchi prays-listi (avto ehtiyot qismlar)");

    public static string HowTo => Tr.T("Выберите файл прайса (.xlsx, .xls или .csv). Шапка, строки-группы и строки без цены пропускаются сами.",
        "Прайс файлын (.xlsx, .xls же .csv) тандаңыз. Баш жазуулар, топ саптары жана баасыз саптар өзү өтүп кетет.",
        "Pick the price list file (.xlsx, .xls or .csv). The header, group rows and rows without a price are skipped automatically.",
        "Fiyat listesi dosyasını (.xlsx, .xls veya .csv) seçin. Üst bilgi, grup satırları ve fiyatsız satırlar otomatik atlanır.",
        "Prays-list faylini (.xlsx, .xls yoki .csv) tanlang. Sarlavha, guruh qatorlari va narxsiz qatorlar o'zi o'tkazib yuboriladi.");

    // ------------------------------------------------------------------ синонимы столбцов

    // Порядок полей важен: «узкие» раньше «широких» (OEM и кросс до артикула, закупка до цены), иначе «Цена опт» станет ценой продажи.
    // Внутри поля — приоритет ключей: точное совпадение ключа (по порядку списка), затем «заголовок содержит слово».
    private static readonly (PartColumn Field, string[] Exact, string[] Partial)[] Rules =
    [
        (PartColumn.Barcode, ["штрихкод", "шк", "barcode", "ean", "ean13", "баркод", "штрихкодтовара"], ["штрихкод", "barcode"]),
        (PartColumn.Oem, ["oem", "оем", "оригинальныйномер", "оригинальныйномероем", "originalnumber", "номероем"], ["oem", "оем", "оригинальныйном"]),
        (PartColumn.Cross, ["кросс", "кроссномера", "кроссномер", "аналог", "аналоги", "заменители", "замена", "cross", "crossref", "crossnumbers", "analog"],
            ["кросс", "аналог", "заменител", "cross"]),
        (PartColumn.Fits, ["подходитк", "применимость", "применяемость", "авто", "автомобиль", "маркаавто", "fits", "fitment", "vehicle"],
            ["подходит", "применим", "применяем", "fitment", "автомобил", "маркаавто"]),
        (PartColumn.Location, ["ячейка", "место", "местохранения", "полка", "стеллаж", "cell", "location", "bin"], ["ячейк", "местохран", "location"]),
        (PartColumn.Condition, ["состояние", "condition", "качество"], ["состоя", "condition"]),
        (PartColumn.Warranty, ["гарантия", "гарантиядней", "гарантиясрок", "warranty"], ["гаранти", "warranty"]),
        (PartColumn.Purchase, ["закупка", "закупочная", "закупочнаяцена", "ценазакупки", "опт", "ценаопт", "оптоваяцена", "оптовая", "оптцена", "себестоимость", "cost",
            "wholesale", "purchase"], ["закуп", "себестоим", "оптов", "ценаопт", "purchase", "wholesale"]),
        (PartColumn.Sale, ["цена", "розница", "розничнаяцена", "ценарозничная", "рознцена", "ценапродажи", "ценасом", "ценаруб", "ценарубли", "price", "прайс",
            "наличныйрасчет"], ["розничн", "розниц", "ценапродаж", "ценаруб", "price", "цена"]),
        (PartColumn.Stock, ["остаток", "остатки", "наличие", "колво", "количество", "кол", "qty", "stock", "quantity", "налич"], ["остат", "налич", "количеств", "колво", "stock", "qty"]),
        (PartColumn.Brand, ["бренд", "производитель", "производство", "марка", "brand", "изготовитель", "manufacturer", "maker"], ["бренд", "brand", "производител", "изготовител"]),
        (PartColumn.PartNo, ["каталожныйномер", "каталожныйномердетали", "каталожныйномеранар", "partnumber", "partno", "номердетали", "артикул", "артикултовара",
            "артикулпроизводителя", "article", "art", "pn", "sku", "номерпроизводителя", "номер", "кодтовара", "код"], ["каталож", "partnumber", "номердетал", "артикул", "article"]),
        (PartColumn.Name, ["наименование", "название", "наименованиетовара", "наименованиетоваров", "наименованиедетали", "товар", "name", "номенклатура", "деталь", "описание",
            "description", "title"], ["наименован", "назван", "номенклатур", "описани", "name"]),
    ];

    // 2026-11-20: «Безналичный расчёт» — вторая цена прайса (avtogarant), а не остаток: «налич» в слове «безналичный» принимал её за «наличие».
    private static bool Ignored(string key) => (key.StartsWith("цена", StringComparison.Ordinal) && key.EndsWith("сто", StringComparison.Ordinal)) || key.Contains("безнал", StringComparison.Ordinal);

    /// <summary>Столбцы по заголовкам (синонимы). Остаток по складам («…(Остаток)») — все столбцы с «остаток» в имени, кроме «ожидаем».</summary>
    public static Dictionary<PartColumn, int> Detect(IReadOnlyList<string> headers, out List<int> stockColumns)
    {
        var keys = headers.Select(h => ProductImportMapping.Key(h)).ToArray();
        var used = new HashSet<int>();
        var map = new Dictionary<PartColumn, int>();
        stockColumns = [];
        foreach (var (field, exact, partial) in Rules)
        {
            var found = -1;
            foreach (var word in exact)
            {
                for (var i = 0; i < keys.Length && found < 0; i++)
                    if (!used.Contains(i) && keys[i] == word && !Ignored(keys[i]))
                        found = i;
                if (found >= 0)
                    break;
            }
            if (found < 0)
            {
                foreach (var word in partial)
                {
                    for (var i = 0; i < keys.Length && found < 0; i++)
                        if (!used.Contains(i) && keys[i].Length > 0 && keys[i].Contains(word, StringComparison.Ordinal) && !Ignored(keys[i]))
                            found = i;
                    if (found >= 0)
                        break;
                }
            }
            if (found < 0)
                continue;
            used.Add(found);
            map[field] = found;
            if (field == PartColumn.Stock)
            {
                stockColumns.Add(found);
                if (keys[found].EndsWith("остаток", StringComparison.Ordinal))
                {
                    for (var i = 0; i < keys.Length; i++)
                        if (i != found && !used.Contains(i) && keys[i].EndsWith("остаток", StringComparison.Ordinal) && !keys[i].Contains("ожида", StringComparison.Ordinal))
                        {
                            stockColumns.Add(i);
                            used.Add(i);
                        }
                }
            }
        }
        return map;
    }

    /// <summary>Номер строки заголовков (индекс в <see cref="ProductFileTable.Rows"/>): первая из первых 30 строк с наибольшим числом узнанных
    /// столбцов, среди них номер детали или название. Шапка прайса (дата, реквизиты) и строка «Лист 1» заголовком не бывают.</summary>
    public static int FindHeaderRow(ProductFileTable table)
    {
        var best = -1;
        var bestScore = 1;
        for (var i = 0; i < Math.Min(table.Rows.Count, HeaderScan); i++)
        {
            var cells = table.Rows[i].Cells;
            // Строка данных с длинным текстом заголовком не бывает: ячейки заголовка короткие.
            if (cells.Any(c => c.Length > 60))
                continue;
            var map = Detect(cells, out _);
            if (!map.ContainsKey(PartColumn.PartNo) && !map.ContainsKey(PartColumn.Name))
                continue;
            if (map.Count > bestScore)
            {
                bestScore = map.Count;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Похож ли файл на прайс запчастей: нашлись номер детали (или название) и цена.</summary>
    public static bool LooksLikePrice(ProductFileTable table)
    {
        var header = FindHeaderRow(table);
        if (header < 0)
            return false;
        var map = Detect(table.Rows[header].Cells, out _);
        return map.ContainsKey(PartColumn.Sale) && (map.ContainsKey(PartColumn.PartNo) || map.ContainsKey(PartColumn.Name));
    }

    // ------------------------------------------------------------------ числа и остаток

    /// <summary>Число из ячейки: «1 234,50», «5240.0», «5 240 сом» → число; пусто или не число → null.</summary>
    public static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"-?\d[\d\s  .,]*");
        if (!m.Success)
            return null;
        var s = new string(m.Value.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd('.', ',');
        var dot = s.LastIndexOf('.');
        var comma = s.LastIndexOf(',');
        if (dot >= 0 && comma >= 0)
            s = dot > comma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.'); // последний знак — десятичный
        else if (comma >= 0)
            s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;
    }

    /// <summary>Остаток из ячейки: число; «20–50» → 20 (нижняя граница); «менее 8», «&lt;8», «до 8» → 8; «&gt;10», «более 10» → 10;
    /// «есть / в наличии» → 1, «нет / под заказ» → 0. approx — значение приблизительное (пометка для предпросмотра).</summary>
    public static double? ParseStock(string? text, out string note)
    {
        note = "";
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
            return null;
        var low = raw.ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^-?\d+([.,]\d+)?$") && ParseNumber(raw) is { } plain)
            return Math.Max(0, plain);
        if (low is "нет" or "нет в наличии" or "отсутствует" or "под заказ" or "ожидается" or "-" or "—" or "no" or "yoq" or "жок" or "yok")
        {
            note = Tr.T($"«{raw}» → 0", $"«{raw}» → 0", $"“{raw}” → 0", $"«{raw}» → 0", $"«{raw}» → 0");
            return 0;
        }
        if (low is "есть" or "в наличии" or "да" or "+" or "yes" or "bor" or "бар" or "var" or "много" or "достаточно")
        {
            note = Tr.T($"«{raw}» → 1", $"«{raw}» → 1", $"“{raw}” → 1", $"«{raw}» → 1", $"«{raw}» → 1");
            return 1;
        }
        // диапазон «20–50», «20-50», «20 — 50»: берём нижнюю границу
        var range = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*(\d+(?:[.,]\d+)?)\s*[-–—]\s*(\d+(?:[.,]\d+)?)\s*$");
        if (range.Success && ParseNumber(range.Groups[1].Value) is { } lower)
        {
            note = Tr.T($"«{raw}» → {lower:0.###} (нижняя граница)", $"«{raw}» → {lower:0.###} (төмөнкү чек)", $"“{raw}” → {lower:0.###} (lower bound)",
                $"«{raw}» → {lower:0.###} (alt sınır)", $"«{raw}» → {lower:0.###} (quyi chegara)");
            return lower;
        }
        // «менее 8», «<8», «до 8», «>10», «более 10», «от 10», «10+»
        var bound = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*(?:менее|меньше|до|более|больше|от|<=|>=|<|>|≥|≤|less\s+than|more\s+than)?\s*(\d+(?:[.,]\d+)?)\s*\+?\s*(?:шт\.?)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bound.Success && ParseNumber(bound.Groups[1].Value) is { } edge)
        {
            note = Tr.T($"«{raw}» → {edge:0.###} (граница)", $"«{raw}» → {edge:0.###} (чек)", $"“{raw}” → {edge:0.###} (boundary)", $"«{raw}» → {edge:0.###} (sınır)", $"«{raw}» → {edge:0.###} (chegara)");
            return edge;
        }
        note = Tr.T($"остаток «{raw}» не распознан — не загружается", $"калдык «{raw}» таанылган жок — жүктөлбөйт", $"stock “{raw}” not recognised — not loaded",
            $"stok «{raw}» tanınmadı — yüklenmez", $"qoldiq «{raw}» tanilmadi — yuklanmaydi");
        return null;
    }

    /// <summary>Состояние из текста прайса → ключ для <see cref="AutoPartInfo.FromState"/>: original / oes / aftermarket / used / refurb / "".</summary>
    public static string StateOf(string? text)
    {
        var k = ProductImportMapping.Key(text);
        if (k.Length == 0)
            return "";
        if (k is "бу" or "bu" || k.Contains("used", StringComparison.Ordinal) || k.Contains("разбор", StringComparison.Ordinal) || k.Contains("секондхенд", StringComparison.Ordinal))
            return "used";
        if (k.Contains("восстанов", StringComparison.Ordinal) || k.Contains("refurb", StringComparison.Ordinal) || k.Contains("реставр", StringComparison.Ordinal))
            return "refurb";
        if (k.Contains("oes", StringComparison.Ordinal) || k.Contains("оес", StringComparison.Ordinal))
            return "oes";
        if (k.Contains("аналог", StringComparison.Ordinal) || k.Contains("aftermarket", StringComparison.Ordinal) || k.Contains("analog", StringComparison.Ordinal) || k.Contains("неоригинал", StringComparison.Ordinal))
            return "aftermarket";
        if (k.Contains("оригинал", StringComparison.Ordinal) || k.Contains("original", StringComparison.Ordinal) || k.Contains("новый", StringComparison.Ordinal) || k == "new")
            return "original";
        return "";
    }

    // ------------------------------------------------------------------ подготовка

    /// <summary>Столбцы названия по умолчанию: есть «Наименование» — оно одно; иначе бренд + до четырёх незанятых текстовых столбцов.</summary>
    public static List<int> DefaultNameColumns(ProductFileTable table, IReadOnlyDictionary<PartColumn, int> map, IReadOnlyList<int> stockColumns)
    {
        if (map.TryGetValue(PartColumn.Name, out var name))
            return [name];
        var taken = new HashSet<int>(map.Where(kv => kv.Key != PartColumn.Brand).Select(kv => kv.Value));
        foreach (var s in stockColumns)
            taken.Add(s);
        var result = new List<int>();
        if (map.TryGetValue(PartColumn.Brand, out var brand))
            result.Add(brand);
        var rows = table.DataRows.Take(60).ToList();
        for (var c = 0; c < table.ColumnCount && result.Count < 5; c++)
        {
            if (taken.Contains(c) || result.Contains(c))
                continue;
            var filled = rows.Select(r => r[c].Trim()).Where(v => v.Length > 0).ToList();
            if (filled.Count == 0)
                continue;
            var numeric = filled.Count(v => ParseNumber(v) is not null && v.All(ch => char.IsDigit(ch) || ch is '.' or ',' or ' '));
            if (numeric * 2 >= filled.Count)
                continue;
            result.Add(c);
        }
        return result;
    }

    public static AutoPartsPrepared Prepare(ProductFileTable table, AutoPartsPriceOptions? options = null, IReadOnlyList<CatalogProductTileVm>? catalog = null)
    {
        options ??= new AutoPartsPriceOptions();
        var headers = table.Headers;
        var map = Detect(headers, out var stockCols);
        var nameCols = (options.NameColumns?.Where(c => c >= 0 && c < table.ColumnCount).ToList() is { Count: > 0 } chosen) ? chosen : DefaultNameColumns(table, map, stockCols);
        int Col(PartColumn f) => map.TryGetValue(f, out var c) ? c : -1;
        string Cell(ProductFileRow r, PartColumn f) => Col(f) is var c and >= 0 ? r[c].Trim() : "";
        var catalogList = catalog ?? SnapshotCatalog();
        var byNumber = IndexCatalog(catalogList);
        var catalogCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byBarcode = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in catalogList)
            if (!string.IsNullOrWhiteSpace(t.Barcode))
            {
                catalogCodes.Add(t.Barcode.Trim());
                byBarcode.TryAdd(t.Barcode.Trim(), t);
            }
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<AutoPartsPreparedRow>();
        int created = 0, updated = 0, skipped = 0, approx = 0;

        foreach (var src in table.DataRows)
        {
            var nonEmpty = src.Cells.Count(c => !string.IsNullOrWhiteSpace(c));
            var sale = ParseNumber(Cell(src, PartColumn.Sale));
            var purchase = ParseNumber(Cell(src, PartColumn.Purchase));
            var partRaw = Cell(src, PartColumn.PartNo);
            var brand = Cell(src, PartColumn.Brand);
            var name = JoinName(src, nameCols);
            AutoPartsPreparedRow Skip(string reason)
            {
                skipped++;
                return new AutoPartsPreparedRow { FileRow = src.FileRow, Status = AutoPartsRowStatus.Skipped, SkipReason = reason, Name = name, PartNo = partRaw, Brand = brand };
            }

            // Строка-группа («Амортизаторы»): заполнена одна-две ячейки, цены и номера нет.
            if (sale is null or <= 0 && purchase is null && (nonEmpty <= 1 || (partRaw.Length == 0 && nonEmpty <= 2)))
            {
                rows.Add(Skip(Tr.T("группа", "топ", "group", "grup", "guruh")));
                continue;
            }
            if (sale is null or <= 0)
            {
                rows.Add(Skip(Tr.T("нет цены", "баасы жок", "no price", "fiyat yok", "narx yo'q")));
                continue;
            }
            if (name.Length == 0 && partRaw.Length == 0)
            {
                rows.Add(Skip(Tr.T("нет названия", "аталышы жок", "no name", "ad yok", "nomi yo'q")));
                continue;
            }
            if (name.Length == 0)
                name = (brand + " " + partRaw).Trim();
            if (name.Length > 255)
                name = name[..255].TrimEnd();

            var partNorm = PartNumbers.Norm(partRaw);
            var brandKey = ProductImportMapping.Key(brand);
            var dupKey = partNorm.Length > 0 ? partNorm + "|" + brandKey : "n|" + BulkImportPlan.NameKey(name);
            if (seen.TryGetValue(dupKey, out var firstRow))
            {
                rows.Add(Skip(Tr.T($"повтор строки {firstRow}", $"{firstRow}-сапты кайталоо", $"repeat of row {firstRow}", $"{firstRow}. satırın tekrarı", $"{firstRow}-qator takrori")));
                continue;
            }
            seen[dupKey] = src.FileRow;

            // Остаток (по складам — сумма), диапазон → нижняя граница.
            double? stock = null;
            var stockNote = "";
            var approxRow = false;
            foreach (var sc in stockCols)
            {
                var v = ParseStock(src[sc], out var n);
                if (n.Length > 0 && stockNote.Length == 0)
                    stockNote = n;
                if (v is { } sv)
                {
                    stock = (stock ?? 0) + sv;
                    approxRow |= n.Length > 0;
                }
            }
            if (approxRow)
                approx++;

            // Существующий товар: по номеру детали + бренд (повторная загрузка того же прайса обновляет, а не плодит дубли).
            CatalogProductTileVm? match = null;
            if (partNorm.Length > 0 && byNumber.TryGetValue(partNorm, out var candidates))
                match = candidates.FirstOrDefault(t => Same(t.Brand, brandKey, strict: true)) ?? candidates.FirstOrDefault(t => Same(t.Brand, brandKey, strict: false));
            // 2026-11-20: товар заведён раньше без артикула (прежняя загрузка по штрихкоду) — берём его по штрихкоду из файла; иначе строка в окне считалась бы «без изменений»
            // (план находит товар по штрихкоду), а артикул, бренд, OEM и ячейка не записывались.
            if (match is null && Cell(src, PartColumn.Barcode) is { Length: > 0 } fileBarcode && byBarcode.TryGetValue(fileBarcode, out var byCode))
                match = byCode;
            var isNew = match is null || string.IsNullOrWhiteSpace(match.Barcode);

            // Штрихкод: у существующего — его; иначе из файла (цифры 4–14); иначе внутренний (как «Сгенерировать штрихкоды»).
            string barcode;
            var fileCode = Cell(src, PartColumn.Barcode);
            if (!isNew)
                barcode = match!.Barcode.Trim();
            else if (BulkImportPlan.IsCardBarcode(fileCode))
                barcode = fileCode;
            else
                barcode = BarcodeGenerator.Generate(null, code => usedCodes.Contains(code) || catalogCodes.Contains(code));
            usedCodes.Add(barcode);

            var info = BuildInfo(src, map, partNorm, isNew ? null : PartMetaStore.Get(match!.Id));
            var existingInfo = isNew ? null : PartMetaStore.Get(match!.Id);
            var warranty = AutoPartsProductMeta.ParseDays(Cell(src, PartColumn.Warranty));
            var data = new AutoPartsRowData
            {
                Brand = brand,
                Article = partRaw,
                Info = info,
                WarrantyDays = warranty,
                Stock = stock,
                IsNew = isNew,
                ProductName = name,
                NeedsAttrs = !isNew && !SameAttrs(existingInfo, info),
                NeedsLocation = !isNew && info.Location.Length > 0 && !string.Equals(info.Location, existingInfo?.Location ?? "", StringComparison.Ordinal),
                NeedsBrand = !isNew && brand.Length > 0 && !string.Equals(brand, match!.Brand ?? "", StringComparison.OrdinalIgnoreCase),
                NeedsArticle = !isNew && partRaw.Length > 0 && PartNumbers.Norm(match!.Article) != partNorm,
                NeedsStock = !isNew && options.UpdateStock && stock is { } st && Math.Abs(st - match!.Quantity) > 0.0001,
                NeedsWarranty = !isNew && warranty is > 0 && AutoPartsProductMeta.Get(match!.Id)?.WarrantyDays != warranty,
            };
            if (isNew)
                created++;
            else
                updated++;
            rows.Add(new AutoPartsPreparedRow
            {
                FileRow = src.FileRow, Status = isNew ? AutoPartsRowStatus.New : AutoPartsRowStatus.Update, Name = name, PartNo = partRaw, Brand = brand,
                Barcode = barcode, Stock = stock, StockNote = stockNote, Sale = sale.Value, Purchase = purchase ?? 0, Data = data,
            });
        }

        return new AutoPartsPrepared
        {
            Rows = rows, Created = created, Updated = updated, Skipped = skipped, ApproxStock = approx, Columns = map, NameColumns = nameCols, FileName = table.FileName,
        };
    }

    private static string JoinName(ProductFileRow row, IReadOnlyList<int> columns)
    {
        var sb = new StringBuilder();
        foreach (var c in columns)
        {
            var part = row[c].Trim();
            if (part.Length == 0 || sb.ToString().Contains(part, StringComparison.OrdinalIgnoreCase))
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(part);
        }
        return sb.ToString();
    }

    /// <summary>Данные запчасти строки: новые значения из файла поверх известных (пустое в файле не стирает известное).</summary>
    private static AutoPartInfo BuildInfo(ProductFileRow src, IReadOnlyDictionary<PartColumn, int> map, string partNorm, AutoPartInfo? existing)
    {
        string Cell(PartColumn f) => map.TryGetValue(f, out var c) ? src[c].Trim() : "";
        var info = existing?.Clone() ?? new AutoPartInfo();
        if (partNorm.Length > 0)
            info.PartNo = partNorm.Length <= AutoPartInfo.MaxValueLength ? partNorm : partNorm[..AutoPartInfo.MaxValueLength];
        var oem = PartNumbers.ParseList(Cell(PartColumn.Oem)).Take(AutoPartInfo.MaxOem).ToList();
        if (oem.Count > 0)
            info.Oem = oem;
        var cross = PartNumbers.ParseList(Cell(PartColumn.Cross)).Take(AutoPartInfo.MaxCross).ToList();
        if (cross.Count > 0)
            info.Cross = cross;
        var fits = Cell(PartColumn.Fits).Split([';', '\n', '\r', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Length > AutoPartInfo.MaxValueLength ? x[..AutoPartInfo.MaxValueLength] : x).Distinct(StringComparer.OrdinalIgnoreCase).Take(AutoPartInfo.MaxFits).ToList();
        if (fits.Count > 0)
            info.Fits = fits;
        if (StateOf(Cell(PartColumn.Condition)) is { Length: > 0 } state)
            (info.Condition, info.PartType) = AutoPartInfo.FromState(state);
        if (Cell(PartColumn.Location) is { Length: > 0 } loc)
            info.Location = loc;
        return info;
    }

    private static bool SameAttrs(AutoPartInfo? old, AutoPartInfo now) =>
        JsonSerializer.Serialize((old ?? new AutoPartInfo()).BuildAttributes()) == JsonSerializer.Serialize(now.BuildAttributes());

    /// <summary>Бренды совпадают: strict — оба названы и равны; нестрогое — хотя бы у одной стороны бренда нет.</summary>
    private static bool Same(string? tileBrand, string brandKey, bool strict)
    {
        var t = ProductImportMapping.Key(tileBrand);
        return strict ? t.Length > 0 && brandKey.Length > 0 && t == brandKey : t.Length == 0 || brandKey.Length == 0;
    }

    private static Dictionary<string, List<CatalogProductTileVm>> IndexCatalog(IReadOnlyList<CatalogProductTileVm> catalog)
    {
        var map = new Dictionary<string, List<CatalogProductTileVm>>(StringComparer.Ordinal);
        void Add(string? number, CatalogProductTileVm tile)
        {
            var norm = PartNumbers.Norm(number);
            if (norm.Length == 0)
                return;
            if (!map.TryGetValue(norm, out var list))
                map[norm] = list = [];
            if (!list.Contains(tile))
                list.Add(tile);
        }
        foreach (var tile in catalog)
        {
            Add(tile.Article, tile);
            if (!string.IsNullOrWhiteSpace(tile.Id) && PartMetaStore.Get(tile.Id) is { PartNo.Length: > 0 } meta)
                Add(meta.PartNo, tile);
        }
        return map;
    }

    private static IReadOnlyList<CatalogProductTileVm> SnapshotCatalog()
    {
        try
        {
            return CatalogCacheService.Products.ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
