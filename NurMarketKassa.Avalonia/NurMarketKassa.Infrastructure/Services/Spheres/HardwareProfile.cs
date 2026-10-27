namespace NurMarketKassa.Services;

/// <summary>2026-10-26 (ТЗ хозмаг, ХМ1, ответ владельца В-Х1): один профиль hardware с переключателем размера.</summary>
public enum HardwareSize
{
    /// <summary>Хозмаг у дома (20–80 м², 1–2 кассы).</summary>
    Shop,
    /// <summary>Строительный маркет / база (склад, прорабы, доставка).</summary>
    Market,
}

/// <summary>2026-10-26 (ХМ1, ТЗ 3.2): что включено по умолчанию для размера магазина. Это ТОЛЬКО данные — сами функции
/// делают следующие пакеты (ХМ2 дробное количество, ХМ3 скан упаковки, ХМ5 калькулятор, ХМ6 окно количества, ХМ8 цена прораба…).
/// Магазины grocery / clothing / services этих значений не видят.</summary>
public sealed record HardwareFeatures
{
    /// <summary>Дробное количество по единице (м, м², м³, л, кг, т) — ХМ2.</summary>
    public bool FractionalByUnit { get; init; } = true;
    /// <summary>Скан упаковки = N базовых единиц — ХМ3. Только возможность: для перешедших магазинов владелец включает галочкой
    /// (В-Х4), по умолчанию для новых — <see cref="PackScanDefaultForNew"/>.</summary>
    public bool PackScan { get; init; } = true;
    public bool PackScanDefaultForNew { get; init; } = true;
    /// <summary>Окно «Количество / длина / площадь» — ХМ6.</summary>
    public bool QuantityPad { get; init; } = true;
    /// <summary>Калькулятор расхода в карточке товара — ХМ5.</summary>
    public bool ConsumptionCalc { get; init; } = true;
    /// <summary>Весовой товар (гвозди, крепёж на вес).</summary>
    public bool WeightGoods { get; init; } = true;
    /// <summary>Размеры и цвета по умолчанию (включаются в самом товаре).</summary>
    public bool ShowVariants { get; init; }
    /// <summary>Опт в чеке.</summary>
    public bool WholesaleInReceipt { get; init; } = true;
    /// <summary>Цена «Прораб» через оптовую цену для отмеченных клиентов (ответ владельца В-Х5) — ХМ8.</summary>
    public bool ForemanPrice { get; init; }
    /// <summary>Срок долга по умолчанию, дней (null — как сейчас).</summary>
    public int? DefaultDebtDays { get; init; }
    /// <summary>КП / счёт / «Смета» в меню на виду — ХМ9.</summary>
    public bool QuoteInMenu { get; init; }
    /// <summary>Предложить услуги «Доставка», «Подъём на этаж», «Резка» (только предложение, ХМ13).</summary>
    public bool OfferServiceTemplates { get; init; } = true;
    /// <summary>Прокат инструмента (как во всех сферах).</summary>
    public bool Rentals { get; init; } = true;
    /// <summary>«Подробнее» с описанием товара.</summary>
    public bool ProductDetails { get; init; } = true;
}

/// <summary>
/// 2026-10-26 (ТЗ «Вид магазина — Хозмаг и стройматериалы», пакет ХМ1): данные профиля «Хозтовары и стройматериалы»
/// (код hardware). Базовое поведение — grocery: на сервер в market_sphere уходит grocery (hardware сервер отклонит),
/// точное значение хранится в UserPreferences.MarketSphereDetail. Содержит названия на 5 языках, 14 единиц с синонимами
/// (ТЗ 3.3), 15 категорий (ТЗ 3.4) и включения по размеру магазина (ТЗ 3.2). Логики продажи здесь нет.
/// </summary>
public static class HardwareProfile
{
    public const string Id = "hardware";

    public static SphereProfile Create() => new()
    {
        Id = Id,
        BaseBehavior = MarketSpheres.Grocery,
        Title = new L5(
            "Хозтовары и стройматериалы",
            "Чарба буюмдары жана курулуш материалдары",
            "Hardware and building materials",
            "Hırdavat ve yapı malzemeleri",
            "Xo'jalik mollari va qurilish materiallari"),
        ShortTitle = new L5("Хозмаг и стройка", "Чарба дүкөнү жана курулуш", "Hardware store", "Hırdavat ve yapı", "Xo'jalik va qurilish"),
        Description = new L5(
            "Хозмаг, стройматериалы, сантехника, электрика, краски. Метры, м², мешки и коробки",
            "Чарба дүкөнү, курулуш материалдары, сантехника, электр, боёк. Метр, м², кап жана куту",
            "Hardware, building materials, plumbing, electrical, paint. Metres, m², bags and boxes",
            "Hırdavat, yapı malzemeleri, tesisat, elektrik, boya. Metre, m², torba ve kutu",
            "Xo'jalik do'koni, qurilish materiallari, santexnika, elektr, bo'yoq. Metr, m², qop va quti"),
        Icon = "🔨",
        IconKey = "hammer",
        AccentHex = "#F59E0B",
        Units = UnitList,
        CategoryHints = CategoryList,
        ShowVariants = false,
        ShowProductDetails = true,
    };

    /// <summary>Включения по размеру (ТЗ 3.2). Хозмаг у дома: без цены прораба, без срока долга 14 дней, КП не на виду.
    /// Строительный маркет: цена прораба (опт для отмеченных клиентов), долг 14 дней, КП / счёт / «Смета» на виду.</summary>
    public static HardwareFeatures FeaturesFor(HardwareSize size) => size == HardwareSize.Market
        ? new HardwareFeatures { ForemanPrice = true, DefaultDebtDays = 14, QuoteInMenu = true }
        : new HardwareFeatures();

    /// <summary>Единица по тексту поля «Ед.» (ТЗ 3.3): без регистра, точек и пробелов; «м», «м.», «метр», «пог.м», «п.м» → m;
    /// «м2», «м²», «кв.м» → m2 и т. д. Неизвестный текст — null (такая единица штучная, как сейчас).</summary>
    public static UnitDef? FindUnit(string? unitText)
    {
        var key = SphereCatalog.NormalizeUnitText(unitText);
        return key.Length > 0 && BySynonym.TryGetValue(key, out var unit) ? unit : null;
    }

    /// <summary>Код единицы (pcs, m, m2…) по тексту; неизвестная — null.</summary>
    public static string? UnitCode(string? unitText) => FindUnit(unitText)?.Code;

    /// <summary>Можно ли дробное количество у этой единицы без весов. Неизвестная единица — нет.</summary>
    public static bool IsFractionalUnit(string? unitText) => FindUnit(unitText)?.Fractional == true;

    private static readonly UnitDef[] UnitList =
    {
        new("pcs", new L5("шт", "даана", "pcs", "adet", "dona"), false, 1m, 0,
            new[] { "шт", "штука", "штуки", "штук", "pcs", "pc", "piece", "adet", "dona", "даана", "дана" }),
        new("m", new L5("м", "м", "m", "m", "m"), true, 0.1m, 2,
            new[] { "м", "метр", "метра", "метров", "метры", "погм", "пм", "мп", "m", "meter", "metre", "metr" }, MinQty: 1m),
        new("m2", new L5("м²", "м²", "m²", "m²", "m²"), true, 0.01m, 2,
            new[] { "м2", "квм", "квметр", "квметра", "m2", "sqm" }, MinQty: 0.5m),
        new("m3", new L5("м³", "м³", "m³", "m³", "m³"), true, 0.01m, 2,
            new[] { "м3", "кубм", "куб", "кубометр", "m3", "cbm" }),
        new("kg", new L5("кг", "кг", "kg", "kg", "kg"), true, 0.1m, 3,
            new[] { "кг", "килограмм", "кило", "kg", "kilo" }, ScaleStep: 0.001m),
        new("l", new L5("л", "л", "l", "l", "l"), true, 0.1m, 2,
            new[] { "л", "литр", "литра", "литров", "l", "lt", "ltr", "litre", "liter" }, MinQty: 0.5m),
        new("t", new L5("т", "т", "t", "t", "t"), true, 0.01m, 3,
            new[] { "т", "тонна", "тонн", "t", "ton" }),
        new("pack", new L5("уп", "таңгак", "pack", "paket", "qadoq"), false, 1m, 0,
            new[] { "уп", "упак", "упаковка", "pack", "paket", "qadoq", "таңгак" }),
        new("box", new L5("коробка", "куту", "box", "kutu", "quti"), false, 1m, 0,
            new[] { "коробка", "короб", "кор", "box", "kutu", "quti", "куту" }),
        new("bag", new L5("мешок", "кап", "bag", "torba", "qop"), false, 1m, 0,
            new[] { "мешок", "меш", "мешков", "bag", "torba", "qop", "кап" }),
        new("roll", new L5("рулон", "түрмөк", "roll", "rulo", "rulon"), false, 1m, 0,
            new[] { "рулон", "рул", "roll", "rulo", "rulon", "түрмөк" }),
        new("sheet", new L5("лист", "барак", "sheet", "levha", "list"), false, 1m, 0,
            new[] { "лист", "листов", "sheet", "levha", "list", "барак" }),
        new("set", new L5("комплект", "топтом", "set", "takım", "to'plam"), false, 1m, 0,
            new[] { "комплект", "компл", "кмп", "set", "takım", "takim", "toplam", "топтом" }),
        new("pair", new L5("пара", "жуп", "pair", "çift", "juft"), false, 1m, 0,
            new[] { "пара", "пар", "pair", "çift", "cift", "juft", "жуп" }),
    };

    /// <summary>Быстрые категории по умолчанию (ТЗ 3.4): только подсказки, сами не создаются.</summary>
    private static readonly L5[] CategoryList =
    {
        new("Крепёж", "Бекиткичтер", "Fasteners", "Bağlantı elemanları", "Mahkamlash buyumlari"),
        new("Инструмент", "Аспаптар", "Tools", "Aletler", "Asboblar"),
        new("Электрика", "Электр жабдуулары", "Electrical", "Elektrik malzemeleri", "Elektr jihozlari"),
        new("Сантехника", "Сантехника", "Plumbing", "Tesisat", "Santexnika"),
        new("Краски и лаки", "Боёктор жана лактар", "Paints and varnishes", "Boya ve vernikler", "Bo'yoq va laklar"),
        new("Сухие смеси", "Кургак аралашмалар", "Dry mixes", "Kuru karışımlar", "Quruq aralashmalar"),
        new("Плитка и напольные покрытия", "Плитка жана полго төшөмөлөр", "Tiles and flooring", "Fayans ve zemin kaplamaları", "Plitka va pol qoplamalari"),
        new("Обои и клей", "Обои жана желим", "Wallpaper and glue", "Duvar kağıdı ve yapıştırıcı", "Oboy va yelim"),
        new("Листовые материалы", "Барактуу материалдар", "Sheet materials", "Levha malzemeler", "Varaqli materiallar"),
        new("Пиломатериалы", "Араланган жыгач", "Lumber", "Kereste", "Yog'och materiallar"),
        new("Утеплитель и изоляция", "Жылуулагыч жана изоляция", "Insulation", "Yalıtım malzemeleri", "Issiqlik izolyatsiyasi"),
        new("Сад и огород", "Бак жана бакча", "Garden", "Bahçe ve bostan", "Bog' va tomorqa"),
        new("Бытовая химия и хозтовары", "Тиричилик химиясы жана чарба буюмдары", "Household chemicals and goods", "Ev kimyasalları ve ev eşyaları", "Maishiy kimyo va xo'jalik mollari"),
        new("Герметики и пена", "Герметиктер жана көбүк", "Sealants and foam", "Sızdırmazlık ve köpük", "Germetiklar va ko'pik"),
        new("Спецодежда и СИЗ", "Атайын кийим жана жеке коргонуу каражаттары", "Workwear and PPE", "İş kıyafeti ve koruyucu ekipman", "Maxsus kiyim va himoya vositalari"),
    };

    // 2026-10-26: порядок полей важен — SphereCatalog при создании зовёт Create(), а BuildSynonyms зовёт SphereCatalog:
    // CategoryList и UnitList должны быть заполнены ДО BySynonym, иначе при первом обращении через FindUnit категории окажутся null.
    private static readonly Dictionary<string, UnitDef> BySynonym = BuildSynonyms();

    private static Dictionary<string, UnitDef> BuildSynonyms()
    {
        var map = new Dictionary<string, UnitDef>(StringComparer.Ordinal);
        foreach (var unit in UnitList)
        {
            map[SphereCatalog.NormalizeUnitText(unit.Code)] = unit;
            foreach (var synonym in unit.Synonyms)
                map[SphereCatalog.NormalizeUnitText(synonym)] = unit;
        }
        return map;
    }
}
