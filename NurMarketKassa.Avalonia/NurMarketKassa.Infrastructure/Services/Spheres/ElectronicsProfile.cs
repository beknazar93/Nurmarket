using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Электроника», ЭЛ1): данные профиля «Электроника и техника» (код electronics) и признаки сферы.
/// Базовое поведение — grocery: на сервер в market_sphere уходит grocery, точное значение хранится в
/// UserPreferences.MarketSphereDetail (как у hardware). Всё, что касается серийных номеров, гарантии и срока обмена,
/// включается ТОЛЬКО при <see cref="IsActive"/>; у остальных сфер ничего не меняется.
/// </summary>
public static class ElectronicsProfile
{
    public const string Id = "electronics";

    /// <summary>Поле строки чека (JSON корзины) со списком серийных номеров / IMEI. Только в кассе: на сервер не уходит (тело продажи
    /// собирается по отдельным полям, см. QuickCheckoutBody), до пунктов Б-ЭЛ1…Б-ЭЛ4 сервера.</summary>
    public const string SerialsField = "serials";

    /// <summary>Поле строки чека: срок гарантии в месяцах на момент добавления в чек (0 — гарантии нет).</summary>
    public const string WarrantyMonthsField = "warranty_months";

    /// <summary>Признаки сферы (ТЗ раздел 1): серийники у отмеченных категорий, гарантия 12 мес. по умолчанию, срок обмена 14 дней.</summary>
    public const bool SerialNumbers = true;
    public const int DefaultWarrantyMonths = 12;
    public const int DefaultExchangeDays = 14;

    /// <summary>Текущая сфера — «Электроника». Для других сфер всегда false.</summary>
    public static bool IsActive => MarketSpheres.Profile.Id == Id;

    public static SphereProfile Create() => new()
    {
        Id = Id,
        BaseBehavior = MarketSpheres.Grocery,
        Title = new L5(
            "Электроника и техника",
            "Электроника жана техника",
            "Electronics and appliances",
            "Elektronik ve teknoloji",
            "Elektronika va texnika"),
        ShortTitle = new L5("Электроника", "Электроника", "Electronics", "Elektronik", "Elektronika"),
        Description = new L5(
            "Телефоны, ноутбуки, бытовая техника, аксессуары. Серийные номера и IMEI, гарантия, рассрочка",
            "Телефондор, ноутбуктар, тиричилик техникасы, аксессуарлар. Сериялык номерлер жана IMEI, кепилдик, бөлүп төлөө",
            "Phones, laptops, home appliances, accessories. Serial numbers and IMEI, warranty, instalments",
            "Telefonlar, dizüstü bilgisayarlar, ev aletleri, aksesuarlar. Seri numaraları ve IMEI, garanti, taksit",
            "Telefonlar, noutbuklar, maishiy texnika, aksessuarlar. Seriya raqamlari va IMEI, kafolat, bo'lib to'lash"),
        Icon = "📱",
        IconKey = "phone",
        AccentHex = "#6366F1",
        Units = UnitList,
        CategoryHints = CategoryList,
        ShowVariants = false,
        ShowProductDetails = true,
        ChoiceVisible = true, // 2026-11-13 (ЭЛ1, ХМ14): плитка «Электроника» в окне первого запуска и в Настройках
    };

    // 2026-11-12: порядок полей важен (см. HardwareProfile): списки заполняются до первого обращения из каталога сфер.
    private static readonly UnitDef[] UnitList =
    {
        new("pcs", new L5("шт", "даана", "pcs", "adet", "dona"), false, 1m, 0,
            new[] { "шт", "штука", "штуки", "штук", "pcs", "pc", "piece", "adet", "dona", "даана", "дана" }),
        new("set", new L5("комплект", "топтом", "set", "takım", "to'plam"), false, 1m, 0,
            new[] { "комплект", "компл", "кмп", "set", "takım", "takim", "toplam", "топтом" }),
        new("pair", new L5("пара", "жуп", "pair", "çift", "juft"), false, 1m, 0,
            new[] { "пара", "пар", "pair", "çift", "cift", "juft", "жуп" }),
        // Кабель на отрез — редко, без дробного количества по умолчанию (Fractional = false).
        new("m", new L5("м", "м", "m", "m", "m"), false, 1m, 0,
            new[] { "м", "метр", "метра", "метров", "метры", "m", "meter", "metre", "metr" }),
    };

    /// <summary>Категории-подсказки (ТЗ раздел 1). Первые пять — «с серийником» по умолчанию.</summary>
    private static readonly L5[] CategoryList =
    {
        new("Смартфоны", "Смартфондор", "Smartphones", "Akıllı telefonlar", "Smartfonlar"),
        new("Планшеты", "Планшеттер", "Tablets", "Tabletler", "Planshetlar"),
        new("Ноутбуки и ПК", "Ноутбуктар жана ПК", "Laptops and PCs", "Dizüstü bilgisayarlar ve PC", "Noutbuklar va PK"),
        new("Телевизоры", "Телевизорлор", "TVs", "Televizyonlar", "Televizorlar"),
        new("Бытовая техника крупная", "Ири тиричилик техникасы", "Large appliances", "Büyük ev aletleri", "Yirik maishiy texnika"),
        new("Аудио и наушники", "Аудио жана кулакчындар", "Audio and headphones", "Ses ve kulaklıklar", "Audio va quloqchinlar"),
        new("Бытовая техника мелкая", "Майда тиричилик техникасы", "Small appliances", "Küçük ev aletleri", "Mayda maishiy texnika"),
        new("Фото и игры", "Фото жана оюндар", "Photo and games", "Fotoğraf ve oyun", "Foto va o'yinlar"),
        new("Аксессуары (чехлы, стёкла, зарядки, кабели)", "Аксессуарлар (капкактар, айнектер, заряддагычтар, кабелдер)", "Accessories (cases, glass, chargers, cables)", "Aksesuarlar (kılıf, cam, şarj, kablo)", "Aksessuarlar (g'iloflar, oynalar, zaryadkalar, kabellar)"),
        new("Комплектующие и расходники", "Тетиктер жана сарпталуучу материалдар", "Components and consumables", "Bileşenler ve sarf malzemeleri", "Butlovchi qismlar va sarf materiallari"),
        new("Умный дом", "Акылдуу үй", "Smart home", "Akıllı ev", "Aqlli uy"),
        new("Услуги (настройка, перенос данных, наклейка стекла)", "Кызматтар (жөндөө, маалыматты которуу, айнек жабыштыруу)", "Services (setup, data transfer, glass fitting)", "Hizmetler (kurulum, veri aktarımı, cam takma)", "Xizmatlar (sozlash, ma'lumot ko'chirish, oyna yopishtirish)"),
    };

    /// <summary>Сколько первых <see cref="CategoryList"/> — категории с серийником по умолчанию.</summary>
    private const int SerialDefaultCount = 5;

    /// <summary>Услуги-заготовки онбординга (только предложение, ТЗ раздел 1).</summary>
    public static IReadOnlyList<L5> ServiceTemplates { get; } = new[]
    {
        new L5("Настройка телефона", "Телефонду жөндөө", "Phone setup", "Telefon kurulumu", "Telefonni sozlash"),
        new L5("Перенос данных", "Маалыматты которуу", "Data transfer", "Veri aktarımı", "Ma'lumot ko'chirish"),
        new L5("Наклейка защитного стекла", "Коргоочу айнекти жабыштыруу", "Screen protector fitting", "Ekran koruyucu takma", "Himoya oynasini yopishtirish"),
        new L5("Доставка и установка", "Жеткирүү жана орнотуу", "Delivery and installation", "Teslimat ve kurulum", "Yetkazib berish va o'rnatish"),
    };

    /// <summary>Корни слов категорий, у которых по умолчанию спрашивается серийник (в нижнем регистре, «ё» → «е»).</summary>
    private static readonly string[] SerialStems =
    {
        "смартфон", "телефон", "планшет", "ноутбук", "телевиз", "холодильн", "стиральн",
        "smartphone", "phone", "tablet", "laptop", "television", "telefon", "dizüstü", "televizyon", "planshet", "noutbuk", "televizor",
    };

    /// <summary>Слова-исключения: «Аксессуары для телефонов» — не телефон, серийник не нужен.</summary>
    private static readonly string[] AccessoryStems = { "аксессуар", "accessor", "aksesuar", "aksessuar", "чехл", "услуг", "xizmat", "кызмат", "hizmet", "service" };

    private static string Norm(string? text) => (text ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Нужен ли серийник у товара этой категории. Не электроника — всегда false. Список владельца
    /// (<see cref="ElectronicsSettings.SerialCategories"/>) главнее умолчания.</summary>
    public static bool NeedsSerial(string? category)
    {
        if (!IsActive)
            return false;
        return NeedsSerialIn(category, ElectronicsSettings.Current.SerialCategories);
    }

    /// <summary>То же без проверки сферы (для тестов и настроек): владельческий список (null — по умолчанию).</summary>
    public static bool NeedsSerialIn(string? category, IReadOnlyCollection<string>? ownerList)
    {
        var c = Norm(category);
        if (c.Length == 0)
            return false;
        if (ownerList is not null)
            return ownerList.Any(x => Norm(x) == c);
        for (var i = 0; i < SerialDefaultCount; i++)
        {
            var hint = CategoryList[i];
            if (c == Norm(hint.Ru) || c == Norm(hint.Ky) || c == Norm(hint.En) || c == Norm(hint.Tur) || c == Norm(hint.Uz))
                return true;
        }
        if (AccessoryStems.Any(c.Contains))
            return false;
        return SerialStems.Any(c.Contains);
    }

    /// <summary>2026-11-13 (сервер sn1311): включён ли у компании флаг electronics_2026_11 — только тогда касса шлёт серийники серверу и ждёт от него проверок и поиска.
    /// Для проверок подменяется (<see cref="ServerFlagEnabled"/>); у других сфер всегда false.</summary>
    public static bool ServerEnabled => IsActive && ServerFlagEnabled();

    /// <summary>Источник флага компании; по умолчанию — признаки сервера (capabilities, как hardware_2026_11). Подмена — только в проверках.</summary>
    public static Func<bool> ServerFlagEnabled { get; set; } = () => NurMarketKassa.Services.Api.ServerCapabilities.Has(NurMarketKassa.Services.Api.ServerCapabilities.Electronics202611);

    private static readonly string[] WarrantyReasonWords = { "гарантия", "кепилдик", "warranty", "garanti", "kafolat" };

    /// <summary>2026-11-13 (сервер sn1311): причина возврата «Гарантия» — обычный текст в reason (сервер отдельного поля не знает), на языке кассы.</summary>
    public static string WarrantyReason => Tr.T("Гарантия", "Кепилдик", "Warranty", "Garanti", "Kafolat");

    /// <summary>Причина возврата — гарантийный случай (на любом из 5 языков); тогда срок гарантии проверяется по warranty_active сервера.</summary>
    public static bool IsWarrantyReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && WarrantyReasonWords.Contains(reason.Trim().ToLowerInvariant());

    /// <summary>2026-11-13 (сервер sn1311): нужен ли серийник у товара. track_serials=true на сервере — да; иначе (false, нет поля, старый сервер) — по категории, как раньше
    /// (false не отменяет категорию: у существующих товаров поле по умолчанию false). Не электроника — false.</summary>
    public static bool NeedsSerialFor(string? productId, string? category)
    {
        if (!IsActive)
            return false;
        return ProductSerialRules.TrackSerials(productId) == true || NeedsSerial(category);
    }

    /// <summary>2026-11-13 (сервер sn1311): гарантия товара в месяцах — warranty_months карточки (если сервер сказал число &gt; 0), иначе по категории (<see cref="WarrantyMonthsFor(string?)"/>).</summary>
    public static int WarrantyMonthsFor(string? productId, string? category)
    {
        if (!IsActive)
            return 0;
        return ProductSerialRules.WarrantyMonths(productId) is > 0 and var m ? m : WarrantyMonthsFor(category);
    }

    /// <summary>Срок гарантии товара этой категории в месяцах. Не электроника — 0. По категории из настроек владельца; иначе 12 мес.
    /// у категорий «с серийником» и 0 у остальных (аксессуары без гарантии, пока владелец не задал срок).</summary>
    public static int WarrantyMonthsFor(string? category)
    {
        if (!IsActive)
            return 0;
        var s = ElectronicsSettings.Current;
        var key = Norm(category);
        foreach (var (name, months) in s.WarrantyByCategory)
            if (Norm(name) == key && key.Length > 0)
                return Math.Clamp(months, 0, 120);
        return NeedsSerialIn(category, s.SerialCategories) ? Math.Clamp(s.DefaultWarrantyMonths, 0, 120) : 0;
    }
}

/// <summary>2026-11-12 (ЭЛ6, минимальная часть): настройки сферы «Электроника» — список категорий «с серийником» (null — по умолчанию),
/// срок гарантии по умолчанию и по категориям. Лежат файлом на этой кассе (<c>electronics-settings.json</c>); общий ключ компании
/// SphereSettingsJson (ХМ15) ещё не сделан — тогда файл станет его локальной копией. Экран владельца — отдельный пакет.</summary>
public sealed class ElectronicsSettings
{
    public List<string>? SerialCategories { get; set; }
    public int DefaultWarrantyMonths { get; set; } = ElectronicsProfile.DefaultWarrantyMonths;
    public Dictionary<string, int> WarrantyByCategory { get; set; } = new();

    private static readonly object Gate = new();
    private static ElectronicsSettings? _cached;
    private static DateTime _cachedStamp;

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.KassaLocal, "electronics-settings.json");

    public static ElectronicsSettings Current
    {
        get
        {
            lock (Gate)
            {
                try
                {
                    var path = FilePath;
                    var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                    if (_cached is not null && stamp == _cachedStamp)
                        return _cached;
                    _cachedStamp = stamp;
                    _cached = stamp == DateTime.MinValue
                        ? new ElectronicsSettings()
                        : JsonSerializer.Deserialize<ElectronicsSettings>(AtomicFile.ReadWithRetry(path)) ?? new ElectronicsSettings();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    PosLogger.Log($"Настройки электроники не прочитаны ({ex.Message}) — по умолчанию.", "WARNING");
                    _cached = new ElectronicsSettings();
                }
                _cached.WarrantyByCategory ??= new();
                return _cached;
            }
        }
    }

    public static void Save(ElectronicsSettings settings)
    {
        lock (Gate)
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings));
            _cached = null;
        }
    }
}
