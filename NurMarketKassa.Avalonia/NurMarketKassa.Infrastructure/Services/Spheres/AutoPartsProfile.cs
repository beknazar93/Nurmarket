namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ1): профиль «Автозапчасти и автотовары» (код autoparts). Базовое поведение — grocery: на сервер в market_sphere
/// уходит grocery, точное значение хранится в UserPreferences.MarketSphereDetail (как у hardware, electronics). Всё «автозапчастное» (поиск по номеру,
/// значок состояния, ячейка, аналоги, блок в карточке товара) включается ТОЛЬКО при <see cref="IsActive"/>; у остальных сфер ничего не меняется.
/// </summary>
public static class AutoPartsProfile
{
    public const string Id = "autoparts";

    /// <summary>Текущая сфера — «Автозапчасти». Для других сфер всегда false.</summary>
    public static bool IsActive => MarketSpheres.Profile.Id == Id;

    public static SphereProfile Create() => new()
    {
        Id = Id,
        BaseBehavior = MarketSpheres.Grocery,
        Title = new L5(
            "Автозапчасти и автотовары",
            "Автозапчасттар жана автотоварлар",
            "Auto parts and accessories",
            "Oto yedek parça ve aksesuar",
            "Avtoehtiyot qismlar va avtotovarlar"),
        ShortTitle = new L5("Автозапчасти", "Автозапчасттар", "Auto parts", "Oto yedek parça", "Avtoehtiyot"),
        Description = new L5(
            "Запчасти, масла, автохимия. Поиск по каталожному номеру, аналоги, Б/У, цена для СТО, заказ под клиента",
            "Запчасттар, майлар, автохимия. Каталог номери боюнча издөө, аналогдор, Б/У, СТО баасы, кардарга заказ",
            "Parts, oils, car chemicals. Search by part number, analogues, used parts, workshop price, orders for customers",
            "Yedek parça, yağ, oto kimya. Katalog numarasıyla arama, muadiller, ikinci el, servis fiyatı, müşteriye sipariş",
            "Ehtiyot qismlar, moylar, avtokimyo. Katalog raqami bo'yicha qidirish, analoglar, B/U, STO narxi, mijozga buyurtma"),
        Icon = "🚗",
        IconKey = "car",
        AccentHex = "#0EA5E9",
        Units = UnitList,
        CategoryHints = CategoryList,
        ShowVariants = false,
        ShowProductDetails = true, // «Подробнее» на плитке — там «Аналоги (N)» и каталожные номера
        ForemanPriceAvailable = true, // цена «СТО / мастер» — готовая механика хозмага (ТЗ АЗ8, делает исполнитель Б)
        ChoiceVisible = true,
    };

    // 2026-11-20: порядок полей важен (см. ElectronicsProfile): списки заполняются до первого обращения из каталога сфер.
    private static readonly UnitDef[] UnitList =
    {
        new("pcs", new L5("шт", "даана", "pcs", "adet", "dona"), false, 1m, 0,
            new[] { "шт", "штука", "штуки", "штук", "pcs", "pc", "piece", "adet", "dona", "даана", "дана" }),
        new("set", new L5("комплект", "топтом", "set", "takım", "to'plam"), false, 1m, 0,
            new[] { "комплект", "компл", "кмп", "set", "takım", "takim", "toplam", "топтом" }),
        new("pair", new L5("пара", "жуп", "pair", "çift", "juft"), false, 1m, 0,
            new[] { "пара", "пар", "pair", "çift", "cift", "juft", "жуп" }),
        new("m", new L5("м", "м", "m", "m", "m"), false, 1m, 0,
            new[] { "м", "метр", "метра", "метров", "метры", "m", "meter", "metre", "metr" }),
    };

    private static readonly L5[] CategoryList =
    {
        new("Фильтры", "Чыпкалар", "Filters", "Filtreler", "Filtrlar"),
        new("Масла и жидкости", "Майлар жана суюктуктар", "Oils and fluids", "Yağlar ve sıvılar", "Moylar va suyuqliklar"),
        new("Тормозная система", "Тормоз системасы", "Brake system", "Fren sistemi", "Tormoz tizimi"),
        new("Подвеска и рулевое", "Асма жана руль", "Suspension and steering", "Süspansiyon ve direksiyon", "Osma va rul"),
        new("Двигатель и ГРМ", "Кыймылдаткыч жана ГРМ", "Engine and timing", "Motor ve triger", "Dvigatel va GRM"),
        new("Электрика и свет", "Электр жана жарык", "Electrics and lighting", "Elektrik ve aydınlatma", "Elektr va yorug'lik"),
        new("Выхлопная система", "Чыгаруу системасы", "Exhaust system", "Egzoz sistemi", "Chiqindi tizimi"),
        new("Кузов и оптика", "Кузов жана оптика", "Body and optics", "Kaporta ve far", "Kuzov va optika"),
        new("Трансмиссия и сцепление", "Трансмиссия жана сцепление", "Transmission and clutch", "Şanzıman ve debriyaj", "Transmissiya va debriyaj"),
        new("Аккумуляторы", "Аккумуляторлор", "Batteries", "Aküler", "Akkumulyatorlar"),
        new("Шины и диски", "Дөңгөлөктөр жана дисктер", "Tyres and wheels", "Lastik ve jant", "Shinalar va disklar"),
        new("Автохимия и уход", "Автохимия жана кам көрүү", "Car chemicals and care", "Oto kimya ve bakım", "Avtokimyo va parvarish"),
        new("Аксессуары и салон", "Аксессуарлар жана салон", "Accessories and interior", "Aksesuar ve iç mekan", "Aksessuarlar va salon"),
        new("Инструмент", "Аспаптар", "Tools", "Aletler", "Asboblar"),
        new("Б/У и с разборки", "Б/У жана чечмелөөдөн", "Used and from dismantling", "İkinci el ve söküntü", "B/U va demontajdan"),
        new("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar"),
    };

    /// <summary>Услуги-заготовки онбординга (только предложение, ТЗ раздел 6.1).</summary>
    public static IReadOnlyList<L5> ServiceTemplates { get; } = new[]
    {
        new L5("Замена масла", "Майды алмаштыруу", "Oil change", "Yağ değişimi", "Moyni almashtirish"),
        new L5("Установка запчасти", "Запчастты орнотуу", "Part installation", "Parça montajı", "Ehtiyot qismni o'rnatish"),
        new L5("Доставка", "Жеткирүү", "Delivery", "Teslimat", "Yetkazib berish"),
        new L5("Подбор по номеру", "Номер боюнча тандоо", "Selection by number", "Numaraya göre seçim", "Raqam bo'yicha tanlash"),
    };

    /// <summary>Состояния детали в порядке выпадающего списка карточки (код = <c>AutoPartInfo.State</c>; пустой — «не указано»).</summary>
    public static readonly string[] States = { "", "original", "oes", "aftermarket", "used", "refurb" };

    /// <summary>Название состояния на языке кассы (для списка в карточке и строки «Характеристики»).</summary>
    public static string StateName(string state) => state switch
    {
        "original" => Tr.T("Оригинал", "Оригинал", "Original", "Orijinal", "Original"),
        "oes" => Tr.T("Оригинальный поставщик (OES)", "Оригиналдуу жеткирүүчү (OES)", "Original supplier (OES)", "Orijinal tedarikçi (OES)", "Original yetkazib beruvchi (OES)"),
        "aftermarket" => Tr.T("Аналог", "Аналог", "Aftermarket", "Muadil", "Analog"),
        "used" => Tr.T("Б/У", "Б/У", "Used", "İkinci el", "B/U"),
        "refurb" => Tr.T("Восстановленный", "Калыбына келтирилген", "Refurbished", "Yenilenmiş", "Qayta tiklangan"),
        _ => Tr.T("Не указано", "Көрсөтүлгөн эмес", "Not set", "Belirtilmedi", "Ko'rsatilmagan"),
    };

    /// <summary>Короткая подпись значка на плитке.</summary>
    public static string StateBadge(string state) => state switch
    {
        "original" => Tr.T("ОРИГ", "ОРИГ", "OEM", "ORJ", "ORIG"),
        "oes" => "OES",
        "aftermarket" => Tr.T("АНАЛОГ", "АНАЛОГ", "AFTER", "MUADİL", "ANALOG"),
        "used" => Tr.T("Б/У", "Б/У", "USED", "2.EL", "B/U"),
        "refurb" => Tr.T("ВОССТ", "КАЛЫБ", "REFURB", "YENİ", "TIKL"),
        _ => "",
    };

    public static string CellWord => Tr.T("яч.", "уяча", "cell", "göz", "yacheyka");

    /// <summary>Подсказка мягкого лимита тарифа «Старт» (больше 5 кросс-номеров на карточку): ничего не блокирует.</summary>
    public static string CrossLimitHint => Tr.T("Больше 5 кросс-номеров доступно на тарифе Стандарт", "5 кросс-номерден көп Стандарт тарифинде жеткиликтүү",
        "More than 5 cross numbers are available on the Standard plan", "5'ten fazla çapraz numara Standart tarifede mevcut", "5 tadan ko'p kross-raqam Standart tarifida mavjud");

    /// <summary>Связывает плитку каталога (Core) с данными запчастей: вызывается один раз при загрузке сборки.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void WireHub()
    {
        NurMarketKassa.Models.Pos.AutoPartHub.IsActive = () => IsActive;
        NurMarketKassa.Models.Pos.AutoPartHub.Lookup = PartMetaStore.Get;
        NurMarketKassa.Models.Pos.AutoPartHub.StateBadge = StateBadge;
        NurMarketKassa.Models.Pos.AutoPartHub.CellLabel = () => CellWord;
    }
}
