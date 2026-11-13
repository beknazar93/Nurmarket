namespace NurMarketKassa.Services;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ1): признаки профиля «Канцтовары и книги» (СФ4 для этой сферы). Только данные —
/// сами функции делают общие пакеты (скан коробки = N, опт организациям) и КТ2–КТ3. Магазины других сфер этих значений не видят.</summary>
public sealed record StationeryFeatures
{
    /// <summary>Дробное количество — нет: канцтовары продаются штуками, пачками и коробками.</summary>
    public bool FractionalUnits { get; init; }
    /// <summary>Скан коробки поставщика = N штук (пакет ХМ3), для новых магазинов.</summary>
    public bool PackScan { get; init; } = true;
    public bool QuantityPad { get; init; }
    public bool ConsumptionCalc { get; init; }
    /// <summary>Опт для организаций (школы, офисы) — пакет ХМ8.</summary>
    public bool OrgWholesale { get; init; } = true;
    public bool ShowVariants { get; init; }
    public bool ShowProductDetails { get; init; }
    /// <summary>Сроки годности не нужны.</summary>
    public bool ExpiryDates { get; init; }
    /// <summary>Весовой товар не нужен.</summary>
    public bool WeightGoods { get; init; }
    /// <summary>«Списки к школе» (КТ2) и «Счёт из этого чека» (КТ3) доступны только в этой сфере.</summary>
    public bool SchoolLists { get; init; } = true;
    public bool InvoiceFromReceipt { get; init; } = true;
}

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Канцтовары», КТ1): данные профиля «Канцтовары и книги» (код stationery). Базовое поведение — grocery:
/// на сервер в market_sphere уходит grocery, точное значение хранится в UserPreferences.MarketSphereDetail (как у hardware).
/// Содержит названия на 5 языках, 7 единиц без дробных, 11 категорий, 8 быстрых групп F1–F8 и 4 услуги копицентра (заготовки,
/// создаются только по кнопке владельца). Переводы ky, tr и uz проверяет владелец.
/// </summary>
public static class StationeryProfile
{
    public const string Id = "stationery";

    /// <summary>Разделы, скрытые при первом выборе этой сферы (тумблер в «Разделах меню» остаётся — владелец может включить).</summary>
    public static readonly string[] DefaultHiddenSections = { "scales" };

    public static SphereProfile Create() => new()
    {
        Id = Id,
        BaseBehavior = MarketSpheres.Grocery,
        Title = new L5(
            "Канцтовары и книги",
            "Кеңсе буюмдары жана китептер",
            "Stationery and books",
            "Kırtasiye ve kitap",
            "Kanselyariya mollari va kitoblar"),
        ShortTitle = new L5("Канцтовары", "Кеңсе буюмдары", "Stationery", "Kırtasiye", "Kanselyariya"),
        Description = new L5(
            "Канцтовары, школьные товары, книги, товары для офиса. Штуки, пачки и коробки, наборы к школе",
            "Кеңсе буюмдары, мектеп буюмдары, китептер, кеңсе үчүн товарлар. Даана, пачка жана куту, мектепке топтомдор",
            "Stationery, school supplies, books, office goods. Pieces, packs and boxes, back-to-school sets",
            "Kırtasiye, okul malzemeleri, kitaplar, ofis ürünleri. Adet, paket ve kutu, okula dönüş setleri",
            "Kanselyariya, maktab buyumlari, kitoblar, ofis mollari. Dona, pachka va quti, maktabga to'plamlar"),
        Icon = "✏️",
        IconKey = "pencil",
        AccentHex = "#2563EB",
        Units = UnitList,
        CategoryHints = CategoryList,
        ShowVariants = false,
        ShowProductDetails = false,
        PackScan = true, // 2026-11-13: скан коробки = N (ТЗ раздел 1); у других сфер выключен
        ChoiceVisible = true, // 2026-11-13: плитка «Канцтовары» в окне первого запуска и в Настройках
    };

    /// <summary>Признаки профиля (ТЗ раздел 1).</summary>
    public static StationeryFeatures Features { get; } = new();

    /// <summary>Быстрые группы F1–F8 (предложение в онбординге, поле hotkey_group): только подсказки, сами не создаются.</summary>
    public static IReadOnlyList<L5> QuickGroups => QuickGroupList;

    /// <summary>Услуги-заготовки копицентра (kind=service, цена 0 — вводится в чеке). Создаются только по кнопке.</summary>
    public static IReadOnlyList<L5> ServiceTemplates => ServiceList;

    /// <summary>Применить настройки по умолчанию при первом выборе сферы: раздел «Весы» скрыт (тумблер остаётся).
    /// Вызывается из <see cref="MarketSpheres.Set"/> только когда сфера сменилась НА «Канцтовары».</summary>
    public static void ApplyDefaultsOnSelect()
    {
        foreach (var key in DefaultHiddenSections)
            SectionVisibility.Set(key, true);
    }

    private static readonly UnitDef[] UnitList =
    {
        new("pcs", new L5("шт", "даана", "pcs", "adet", "dona"), false, 1m, 0,
            new[] { "шт", "штука", "штуки", "штук", "pcs", "pc", "piece", "adet", "dona", "даана", "дана" }),
        new("pack", new L5("уп", "таңгак", "pack", "paket", "qadoq"), false, 1m, 0,
            new[] { "уп", "упак", "упаковка", "пачка", "пач", "pack", "paket", "qadoq", "pachka", "таңгак" }),
        new("box", new L5("коробка", "куту", "box", "kutu", "quti"), false, 1m, 0,
            new[] { "коробка", "короб", "кор", "box", "kutu", "quti", "куту" }),
        new("set", new L5("набор", "топтом", "set", "takım", "to'plam"), false, 1m, 0,
            new[] { "набор", "комплект", "компл", "set", "takım", "takim", "toplam", "топтом" }),
        new("sheet", new L5("лист", "барак", "sheet", "levha", "list"), false, 1m, 0,
            new[] { "лист", "листов", "sheet", "levha", "list", "барак" }),
        new("roll", new L5("рулон", "түрмөк", "roll", "rulo", "rulon"), false, 1m, 0,
            new[] { "рулон", "рул", "roll", "rulo", "rulon", "түрмөк" }),
        new("pair", new L5("пара", "жуп", "pair", "çift", "juft"), false, 1m, 0,
            new[] { "пара", "пар", "pair", "çift", "cift", "juft", "жуп" }),
    };

    private static readonly L5[] CategoryList =
    {
        new("Тетради и блокноты", "Дептерлер жана блокноттор", "Notebooks and notepads", "Defterler ve not blokları", "Daftarlar va bloknotlar"),
        new("Ручки, карандаши, маркеры", "Каламдар, карандаштар, маркерлер", "Pens, pencils, markers", "Kalemler, kurşun kalemler, markörler", "Ruchkalar, qalamlar, markerlar"),
        new("Бумага и картон", "Кагаз жана картон", "Paper and cardboard", "Kağıt ve karton", "Qog'oz va karton"),
        new("Папки, файлы, архив", "Папкалар, файлдар, архив", "Folders, files, archive", "Klasörler, dosyalar, arşiv", "Papkalar, fayllar, arxiv"),
        new("Школьные принадлежности", "Мектеп буюмдары", "School supplies", "Okul malzemeleri", "Maktab buyumlari"),
        new("Рюкзаки и пеналы", "Рюкзактар жана пенал", "Backpacks and pencil cases", "Sırt çantaları ve kalemlikler", "Ryukzaklar va penallar"),
        new("Творчество (краски, пластилин)", "Чыгармачылык (боёктор, пластилин)", "Crafts (paints, clay)", "Yaratıcılık (boya, oyun hamuru)", "Ijodkorlik (bo'yoqlar, plastilin)"),
        new("Офисная техника и расходники", "Кеңсе техникасы жана чыгым материалдары", "Office equipment and consumables", "Ofis cihazları ve sarf malzemeleri", "Ofis texnikasi va sarf materiallari"),
        new("Книги и учебники", "Китептер жана окуу китептери", "Books and textbooks", "Kitaplar ve ders kitapları", "Kitoblar va darsliklar"),
        new("Подарки и упаковка", "Белектер жана таңгак", "Gifts and packaging", "Hediyeler ve ambalaj", "Sovg'alar va qadoqlash"),
        new("Копицентр (услуги)", "Копицентр (кызматтар)", "Copy centre (services)", "Fotokopi merkezi (hizmetler)", "Kopitsentr (xizmatlar)"),
    };

    private static readonly L5[] QuickGroupList =
    {
        new("Тетради", "Дептерлер", "Notebooks", "Defterler", "Daftarlar"),
        new("Ручки и карандаши", "Каламдар жана карандаштар", "Pens and pencils", "Kalemler", "Ruchkalar va qalamlar"),
        new("Бумага А4", "А4 кагаз", "A4 paper", "A4 kağıt", "A4 qog'oz"),
        new("Папки и файлы", "Папкалар жана файлдар", "Folders and files", "Klasörler ve dosyalar", "Papkalar va fayllar"),
        new("Наборы к школе", "Мектепке топтомдор", "Back-to-school sets", "Okula dönüş setleri", "Maktabga to'plamlar"),
        new("Рюкзаки и пеналы", "Рюкзактар жана пенал", "Backpacks and pencil cases", "Sırt çantaları ve kalemlikler", "Ryukzaklar va penallar"),
        new("Книги", "Китептер", "Books", "Kitaplar", "Kitoblar"),
        new("Копицентр", "Копицентр", "Copy centre", "Fotokopi", "Kopitsentr"),
    };

    private static readonly L5[] ServiceList =
    {
        new("Печать А4", "А4 басып чыгаруу", "A4 printing", "A4 baskı", "A4 chop etish"),
        new("Ксерокопия", "Ксерокопия", "Photocopy", "Fotokopi", "Ksero"),
        new("Ламинирование", "Ламинациялоо", "Lamination", "Laminasyon", "Laminatsiya"),
        new("Сканирование", "Сканерлөө", "Scanning", "Tarama", "Skanerlash"),
    };
}
