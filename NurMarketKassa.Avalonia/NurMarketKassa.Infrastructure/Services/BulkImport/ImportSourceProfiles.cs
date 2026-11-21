namespace NurMarketKassa.Services;

// 2026-11-13 (Л-10, «Новичок: перенос данных»): готовые сопоставления столбцов для выгрузок чужих программ — 1С:Розница, МойСклад, Poster, Loyverse,
// Paloma365, Umag — и автоопределение источника по заголовкам. Профиль только подсказывает, какой столбец что значит (так же, как автоподбор по
// синонимам); денег и сети не касается, строки по-прежнему проверяет BulkImportPlan. Названия столбцов — типичные для выгрузок (справка Poster/
// Loyverse/МойСклад и образцы 1С); у Paloma365 и Umag точный шаблон закрыт справкой — их заголовки подобраны по типичным, определение строже (MinScore),
// а вручную источник можно выбрать в окне («Из какой программы файл?»). Ошибка в определении безвредна: профиль лишь ставит столбцы по их названиям.

/// <summary>Правило профиля: поле и допустимые ключи заголовка по приоритету. Ключ — как <see cref="ProductImportMapping.Key"/> (буквы и цифры, нижний регистр);
/// «слово*» — заголовок начинается с этого слова, «*слово*» — содержит.</summary>
public sealed record ImportColumnRule(ProductImportField Field, params string[] Keys);

/// <summary>Профиль выгрузки одной программы.</summary>
public sealed class ImportSourceProfile
{
    internal ImportSourceProfile(string id, string name, string[] signature, int minScore, ImportColumnRule[] rules, Func<string> howTo)
    {
        Id = id;
        Name = name;
        Signature = signature;
        MinScore = minScore;
        Rules = rules;
        _howTo = howTo;
    }

    private readonly Func<string> _howTo;

    /// <summary>2026-11-20 (CT MAX): строки-группы (нет штрихкода, цена и закупка пусты или 0 — это названия групп/брендов) в загрузку не берутся и не считаются ошибками.</summary>
    public bool SkipGroupRows { get; init; }

    /// <summary>2026-11-20 (CT MAX): в выгрузке бывают служебные коды («*10», без цифр, короче 4, длиннее 14): товар не теряется — получает внутренний штрихкод NurCRM
    /// (прежний код — в артикул), если это включено в «Дополнительно»; цена 0 при непустой закупке — предупреждение.</summary>
    public bool ServiceCodes { get; init; }

    /// <summary>Технический код («1c», «moysklad», «poster», «loyverse», «paloma365», «umag»).</summary>
    public string Id { get; }

    /// <summary>Название программы (одинаково на всех языках).</summary>
    public string Name { get; }

    /// <summary>Характерные заголовки (ключи): по ним программа узнаётся.</summary>
    public IReadOnlyList<string> Signature { get; }

    /// <summary>Сколько характерных заголовков должно совпасть.</summary>
    public int MinScore { get; }

    public IReadOnlyList<ImportColumnRule> Rules { get; }

    /// <summary>Как выгрузить товары из этой программы (одна строка подсказки на языке кассы).</summary>
    public string HowToExport => _howTo();

    internal static bool KeyMatches(string key, string pattern)
    {
        if (key.Length == 0)
            return false;
        if (pattern.Length > 2 && pattern[0] == '*' && pattern[^1] == '*')
            return key.Contains(pattern[1..^1], StringComparison.Ordinal);
        if (pattern.Length > 1 && pattern[^1] == '*')
            return key.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return key == pattern;
    }

    /// <summary>Сколько характерных заголовков из подписи есть в файле.</summary>
    public int Score(IReadOnlyList<string> keys) => Signature.Count(s => keys.Any(k => KeyMatches(k, s)));

    /// <summary>Поставить столбцы по правилам профиля. Столбец, уже взятый профилем под другое поле, повторно не берётся; поле профиля главнее автоподбора
    /// (уверенность «высокая»). Возвращает, сколько полей поставлено.</summary>
    public int Apply(ProductImportMapping mapping, IReadOnlyList<string> headers)
    {
        var keys = headers.Select(ProductImportMapping.Key).ToArray();
        var used = new HashSet<int>();
        var count = 0;
        foreach (var rule in Rules)
        {
            var found = -1;
            foreach (var pattern in rule.Keys)
            {
                for (var i = 0; i < keys.Length && found < 0; i++)
                {
                    if (!used.Contains(i) && KeyMatches(keys[i], pattern))
                        found = i;
                }
                if (found >= 0)
                    break;
            }
            if (found < 0)
                continue;
            used.Add(found);
            mapping.Set(rule.Field, found);
            count++;
        }
        return count;
    }
}

public static class ImportSourceProfiles
{
    private static ImportColumnRule R(ProductImportField f, params string[] keys) => new(f, keys);

    public static readonly IReadOnlyList<ImportSourceProfile> All =
    [
        new("1c", "1С:Розница / 1С:Торговля",
            signature: ["номенклатура", "видноменклатуры", "ставкандс", "полноенаименование", "единицахранения", "родитель", "основнойштрихкод", "базоваяединицаизмерения", "конечныйостаток"],
            minScore: 2,
            rules:
            [
                R(ProductImportField.Barcode, "штрихкод", "основнойштрихкод", "штрихкоды"),
                R(ProductImportField.Name, "наименование", "номенклатура", "полноенаименование"),
                R(ProductImportField.Article, "артикул", "код"),
                R(ProductImportField.Quantity, "конечныйостаток", "остаток", "количество", "остатокнаскладе"),
                R(ProductImportField.Purchase, "ценапоступления", "закупочнаяцена", "ценазакупки", "себестоимость", "учетнаяцена", "входящаяцена"),
                R(ProductImportField.Sale, "розничнаяцена", "ценапродажи", "цена", "цена*"),
                R(ProductImportField.Unit, "единицаизмерения", "единицахранения", "базоваяединицаизмерения", "едизм"),
                R(ProductImportField.Category, "родитель", "группа", "номенклатурнаягруппа", "видноменклатуры"),
                R(ProductImportField.Weight, "весовой", "весовойтовар"),
                R(ProductImportField.Brand, "производитель", "бренд", "марка"),
            ],
            howTo: () => Tr.T("1С: «Номенклатура» → «Вывести список…» → «Сохранить» в Excel (.xlsx) или CSV; при необходимости добавьте столбцы «Штрихкод» и «Цена».",
                "1С: «Номенклатура» → «Тизмени чыгаруу…» → Excel (.xlsx) же CSV катары сактаңыз; керек болсо «Штрих-код» жана «Баа» мамычаларын кошуңуз.",
                "1C: “Items” → “Output list…” → save as Excel (.xlsx) or CSV; add “Barcode” and “Price” columns if needed.",
                "1C: «Ürünler» → «Listeyi çıkar…» → Excel (.xlsx) veya CSV olarak kaydedin; gerekirse «Barkod» ve «Fiyat» sütunlarını ekleyin.",
                "1C: «Nomenklatura» → «Ro'yxatni chiqarish…» → Excel (.xlsx) yoki CSV sifatida saqlang; kerak bo'lsa «Shtrix-kod» va «Narx» ustunlarini qo'shing.")),

        new("moysklad", "МойСклад",
            signature: ["штрихкодean13", "штрихкодean8", "штрихкодcode128", "штрихкодupc", "внешнийкод", "группы", "закупочнаяцена", "неснижаемыйостаток", "ценаценапродажи", "*ценапродажи*"],
            minScore: 2,
            rules:
            [
                R(ProductImportField.Name, "наименование"),
                R(ProductImportField.Barcode, "штрихкодean13", "штрихкодean8", "штрихкодcode128", "штрихкодupc", "штрихкод"),
                R(ProductImportField.AltBarcodes, "штрихкодean8", "штрихкодcode128", "штрихкодupc"),
                R(ProductImportField.Article, "артикул", "код"),
                R(ProductImportField.Quantity, "остаток", "количество"),
                R(ProductImportField.Purchase, "закупочнаяцена", "ценаприхода"),
                R(ProductImportField.Sale, "ценаценапродажи", "ценапродажи", "*ценапродажи*", "ценарозничная", "*рознич*"),
                R(ProductImportField.Unit, "единицаизмерения", "едизм"),
                R(ProductImportField.Category, "группы", "группа"),
                R(ProductImportField.Weight, "весовойтовар", "весовой"),
                R(ProductImportField.Brand, "бренд", "производитель", "торговаямарка"),
            ],
            howTo: () => Tr.T("МойСклад: «Товары» → «Экспорт» → файл Excel; в списке столбцов отметьте «Штрихкод», «Закупочная цена», «Цена продажи», «Остаток».",
                "МойСклад: «Товарлар» → «Экспорт» → Excel файлы; мамычалардан «Штрих-код», «Сатып алуу баасы», «Сатуу баасы», «Калдык» белгилеңиз.",
                "MoySklad: “Products” → “Export” → an Excel file; tick “Barcode”, “Purchase price”, “Sale price”, “Stock” in the column list.",
                "MoySklad: «Ürünler» → «Dışa aktar» → Excel dosyası; sütun listesinde «Barkod», «Alış fiyatı», «Satış fiyatı», «Stok» işaretleyin.",
                "MoySklad: «Mahsulotlar» → «Eksport» → Excel fayli; ustunlar ro'yxatida «Shtrix-kod», «Xarid narxi», «Sotuv narxi», «Qoldiq»ni belgilang.")),

        new("poster", "Poster",
            signature: ["цех", "налог", "тип", "весовойпродукт", "sku", "скрытьвменю"],
            minScore: 3,
            rules:
            [
                R(ProductImportField.Name, "название", "названиетовара", "наименование"),
                R(ProductImportField.Category, "категория"),
                R(ProductImportField.Article, "sku", "артикул"),
                R(ProductImportField.Barcode, "штрихкод", "штрихкодтовара"),
                R(ProductImportField.Sale, "цена"),
                R(ProductImportField.Purchase, "себестоимость", "закупочнаяцена"),
                R(ProductImportField.Weight, "весовойпродукт", "весовойтовар", "весовой"),
                R(ProductImportField.Unit, "единицаизмерения", "едизм"),
                R(ProductImportField.Quantity, "остаток", "количество"),
            ],
            howTo: () => Tr.T("Poster: «Меню» → «Товары» → «⋯» → «Экспорт» (файл Excel или CSV).",
                "Poster: «Меню» → «Товарлар» → «⋯» → «Экспорт» (Excel же CSV файлы).",
                "Poster: “Menu” → “Products” → “⋯” → “Export” (an Excel or CSV file).",
                "Poster: «Menü» → «Ürünler» → «⋯» → «Dışa aktar» (Excel veya CSV dosyası).",
                "Poster: «Menyu» → «Mahsulotlar» → «⋯» → «Eksport» (Excel yoki CSV fayli).")),

        new("loyverse", "Loyverse",
            signature: ["handle", "ref", "soldbyweight", "option1name", "option1value", "trackstock", "skuofincludeditem", "quantityofincludeditem", "supplier"],
            minScore: 2,
            rules:
            [
                R(ProductImportField.Name, "name", "itemname"),
                R(ProductImportField.Category, "category"),
                R(ProductImportField.Article, "ref", "sku"),
                R(ProductImportField.Barcode, "barcode"),
                R(ProductImportField.Weight, "soldbyweight"),
                R(ProductImportField.Purchase, "cost", "cost*"),
                R(ProductImportField.Sale, "price", "price*"),
                R(ProductImportField.Quantity, "instock*", "instock", "quantity"),
            ],
            howTo: () => Tr.T("Loyverse: Back Office → «Товары» → «Экспорт товаров» (файл CSV).",
                "Loyverse: Back Office → «Товарлар» → «Товарларды экспорттоо» (CSV файлы).",
                "Loyverse: Back Office → “Items” → “Export items” (a CSV file).",
                "Loyverse: Back Office → «Ürünler» → «Ürünleri dışa aktar» (CSV dosyası).",
                "Loyverse: Back Office → «Mahsulotlar» → «Mahsulotlarni eksport qilish» (CSV fayli).")),

        new("paloma365", "Paloma365",
            signature: ["внешнийid", "externalid", "признакгруппы", "этогруппа", "родительскаягруппа", "idгруппы", "кодгруппы"],
            minScore: 2,
            rules:
            [
                R(ProductImportField.Name, "наименование", "название", "наименованиетовара"),
                R(ProductImportField.Barcode, "штрихкод", "штрихкоды"),
                R(ProductImportField.Category, "родительскаягруппа", "группа", "категория", "родитель"),
                R(ProductImportField.Sale, "ценапродажи", "цена", "розничнаяцена"),
                R(ProductImportField.Purchase, "ценазакупки", "закупочнаяцена", "себестоимость", "ценапоступления"),
                R(ProductImportField.Unit, "единицаизмерения", "едизм", "ед"),
                R(ProductImportField.Quantity, "остаток", "количество"),
                R(ProductImportField.Article, "артикул", "код"),
            ],
            howTo: () => Tr.T("Paloma365: раздел «Номенклатура» / «Товары» → «Экспорт в Excel». Строки-папки (группы) без штрихкода кассa пометит ошибкой — их можно не загружать.",
                "Paloma365: «Номенклатура» / «Товарлар» бөлүмү → «Excel'ге экспорт». Штрих-кодсуз папка саптарын (топтор) касса ката деп белгилейт — аларды жүктөбөсө болот.",
                "Paloma365: the “Items” section → “Export to Excel”. Folder (group) rows without a barcode are marked as errors — you can skip them.",
                "Paloma365: «Ürünler» bölümü → «Excel'e aktar». Barkodsuz klasör (grup) satırları hata olarak işaretlenir — yüklemeyebilirsiniz.",
                "Paloma365: «Nomenklatura» / «Mahsulotlar» bo'limi → «Excel'ga eksport». Shtrix-kodsiz papka (guruh) qatorlari xato deb belgilanadi — ularni yuklamasa bo'ladi.")),

        new("umag", "Umag",
            signature: ["наименованиетовара", "штрихкодтовара", "закупочнаяцена", "ценазакупки", "ценапродажи", "остатокнаскладе", "категориятовара", "единицаизмерения", "артикул"],
            minScore: 5,
            rules:
            [
                R(ProductImportField.Name, "наименованиетовара", "наименование", "название"),
                R(ProductImportField.Barcode, "штрихкодтовара", "штрихкод"),
                R(ProductImportField.Article, "артикул", "код"),
                R(ProductImportField.Category, "категориятовара", "категория", "группа"),
                R(ProductImportField.Purchase, "ценазакупки", "закупочнаяцена", "себестоимость"),
                R(ProductImportField.Sale, "ценапродажи", "розничнаяцена", "цена"),
                R(ProductImportField.Quantity, "остатокнаскладе", "остаток", "количество"),
                R(ProductImportField.Unit, "единицаизмерения", "едизм"),
            ],
            howTo: () => Tr.T("Umag: «Список товаров» → «Экспорт товаров» (файл Excel).",
                "Umag: «Товарлар тизмеси» → «Товарларды экспорттоо» (Excel файлы).",
                "Umag: “Product list” → “Export products” (an Excel file).",
                "Umag: «Ürün listesi» → «Ürünleri dışa aktar» (Excel dosyası).",
                "Umag: «Mahsulotlar ro'yxati» → «Mahsulotlarni eksport qilish» (Excel fayli).")),
        // 2026-11-19 (Л-10): Rosta (Роста, КР). Точный шаблон выгрузки закрыт справкой — заголовки подобраны по типовым («Наименование / Штрихкод / Цена закупки / Цена продажи / Остаток»);
        // не угаданный столбец кассир ставит вручную, профиль только подсказывает.
        new("rosta", "Rosta",
            signature: ["наименованиетовара", "штрихкод", "ценапоставщика", "ценазакупки", "розничнаяцена", "ценапродажи", "остаток", "единицаизмерения", "группатовара"],
            minScore: 5,
            rules:
            [
                R(ProductImportField.Name, "наименованиетовара", "наименование", "название", "товар"),
                R(ProductImportField.Barcode, "штрихкод", "штрихкодтовара"),
                R(ProductImportField.Article, "артикул", "кодтовара", "код"),
                R(ProductImportField.Category, "группатовара", "группа", "категория"),
                R(ProductImportField.Purchase, "ценапоставщика", "ценазакупки", "закупочнаяцена", "себестоимость"),
                R(ProductImportField.Sale, "розничнаяцена", "ценапродажи", "цена"),
                R(ProductImportField.Quantity, "остаток", "количество"),
                R(ProductImportField.Unit, "единицаизмерения", "едизм"),
            ],
            howTo: () => Tr.T("Rosta: раздел «Товары» → «Экспорт» (Excel). Если столбцы не узнаны — поставьте их вручную.",
                "Rosta: «Товарлар» бөлүмү → «Экспорт» (Excel). Тилкелер табылбаса — кол менен коюңуз.",
                "Rosta: “Products” → “Export” (Excel). If columns are not recognised, set them by hand.",
                "Rosta: «Ürünler» → «Dışa aktar» (Excel). Sütunlar tanınmazsa elle seçin.",
                "Rosta: «Tovarlar» → «Eksport» (Excel). Ustunlar topilmasa, qo'lda tanlang.")),
        // 2026-11-20: CT MAX (КР). Выгрузка товаров — 4 столбца («Штрих Код», «Наименование», «Цена», «Закупочная»), заголовок не в первой строке, остатка, единицы и категории нет:
        // грузятся только карточки и цены. Строки-группы без штрихкода пропускаются; служебные коды получают внутренний штрихкод (см. BulkImportPlan).
        new("ctmax", "CT MAX",
            signature: ["штрихкод", "наименование", "цена", "закупочная"],
            minScore: 4,
            rules:
            [
                R(ProductImportField.Barcode, "штрихкод"),
                R(ProductImportField.Name, "наименование"),
                R(ProductImportField.Sale, "цена"),
                R(ProductImportField.Purchase, "закупочная"),
            ],
            howTo: () => Tr.T("CT MAX: выгрузите список товаров в Excel (.xlsx) со столбцами «Штрих Код», «Наименование», «Цена», «Закупочная» → загрузите файл здесь. Остатков в файле нет — загрузятся только карточки и цены, остаток не изменится.",
                "CT MAX: товарлардын тизмесин Excel (.xlsx) катары «Штрих Код», «Наименование», «Цена», «Закупочная» мамычалары менен чыгарыңыз → файлды ушул жерге жүктөңүз. Файлда калдык жок — карточкалар жана баалар гана жүктөлөт, калдык өзгөрбөйт.",
                "CT MAX: export the product list to Excel (.xlsx) with the columns “Штрих Код”, “Наименование”, “Цена”, “Закупочная” → load the file here. The file has no stock — only cards and prices are loaded, the stock does not change.",
                "CT MAX: ürün listesini «Штрих Код», «Наименование», «Цена», «Закупочная» sütunlarıyla Excel (.xlsx) olarak dışa aktarın → dosyayı buraya yükleyin. Dosyada stok yok — yalnızca kart ve fiyatlar yüklenir, stok değişmez.",
                "CT MAX: mahsulotlar ro'yxatini «Штрих Код», «Наименование», «Цена», «Закупочная» ustunlari bilan Excel (.xlsx) sifatida chiqaring → faylni shu yerga yuklang. Faylda qoldiq yo'q — faqat kartochkalar va narxlar yuklanadi, qoldiq o'zgarmaydi."))
        {
            SkipGroupRows = true,
            ServiceCodes = true,
        },
    ];

    public static ImportSourceProfile? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>Узнать программу по заголовкам: побеждает профиль с наибольшим числом совпавших характерных заголовков (не меньше его порога).
    /// Ничья — первый в списке. Обычный файл (наш шаблон, чужой Excel) — null.</summary>
    public static ImportSourceProfile? Detect(IReadOnlyList<string> headers)
    {
        if (headers.Count == 0)
            return null;
        var keys = headers.Select(ProductImportMapping.Key).ToArray();
        ImportSourceProfile? best = null;
        var bestScore = 0;
        foreach (var p in All)
        {
            var score = p.Score(keys);
            if (score >= p.MinScore && score > bestScore)
            {
                best = p;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>Применить профиль к уже подобранному сопоставлению (например, выбранному вручную в окне).</summary>
    public static void Apply(ImportSourceProfile profile, ProductImportMapping mapping, IReadOnlyList<string> headers, ProductFileTable? table = null)
    {
        profile.Apply(mapping, headers);
        mapping.Source = profile;
        if (table is not null)
            mapping.Refine(table);
    }
}
