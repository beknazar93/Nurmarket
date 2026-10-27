namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-26: одна строка на пяти языках интерфейса (ТЗ сфер, СФ1). Хранится как данные — не зависит от текущего языка,
/// поэтому стенд может проверить, что ни один из пяти текстов не пустой. Get() — текст на языке интерфейса.
/// </summary>
public readonly record struct L5(string Ru, string Ky, string En, string Tur, string Uz)
{
    public string Get() => Tr.T(Ru, Ky, En, Tur, Uz);

    /// <summary>Все пять текстов заполнены.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Ru) && !string.IsNullOrWhiteSpace(Ky) && !string.IsNullOrWhiteSpace(En)
        && !string.IsNullOrWhiteSpace(Tur) && !string.IsNullOrWhiteSpace(Uz);
}

/// <summary>2026-10-26 (ТЗ хозмаг, ХМ1): единица измерения профиля сферы. Code — машинный код (pcs, m, m2…),
/// Synonyms — как её пишут в поле «Ед.» (без регистра, точек и пробелов; сравнение через <see cref="SphereCatalog.NormalizeUnitText"/>).
/// Fractional — можно ли дробное количество без весов; Step — шаг «+/−»; ScaleStep — шаг при весах (кг);
/// Decimals — знаков после запятой; MinQty — минимум продажи (null — равен шагу).</summary>
public sealed record UnitDef(
    string Code,
    L5 Name,
    bool Fractional,
    decimal Step,
    int Decimals,
    string[] Synonyms,
    decimal? ScaleStep = null,
    decimal? MinQty = null);

/// <summary>
/// 2026-10-26 (СФ1, минимальная часть): сфера магазина как данные. BaseBehavior — одна из трёх сфер, от которых зависят
/// старые ветки IsClothing / IsServices и поле market_sphere на сервере (grocery / clothing / services). Точная сфера
/// (hardware и другие) на сервер НЕ уходит — сервер примет только базовую; точное значение хранится в
/// UserPreferences.MarketSphereDetail и синхронизируется общими настройками компании.
/// </summary>
public sealed record SphereProfile
{
    public required string Id { get; init; }
    public required string BaseBehavior { get; init; }
    public required L5 Title { get; init; }
    public required L5 ShortTitle { get; init; }
    public required L5 Description { get; init; }
    /// <summary>Эмодзи плитки выбора.</summary>
    public string Icon { get; init; } = "";
    /// <summary>Ключ значка для дизайна (hammer…).</summary>
    public string IconKey { get; init; } = "";
    /// <summary>Цвет акцента плитки (#RRGGBB) или пусто — по теме.</summary>
    public string AccentHex { get; init; } = "";
    /// <summary>Подсказки единиц у поля «Ед.» (пусто — подсказок нет, как сейчас).</summary>
    public IReadOnlyList<UnitDef> Units { get; init; } = Array.Empty<UnitDef>();
    /// <summary>Подсказки категорий (создаются только по кнопке владельца).</summary>
    public IReadOnlyList<L5> CategoryHints { get; init; } = Array.Empty<L5>();
    /// <summary>Размеры и цвета (варианты) включены по умолчанию.</summary>
    public bool ShowVariants { get; init; }
    /// <summary>Кнопка «Подробнее» у товара включена профилем (для hardware; у одежды и услуг — прежней веткой).</summary>
    public bool ShowProductDetails { get; init; }
}

/// <summary>
/// 2026-10-26 (СФ1, минимальная часть, ТЗ «Касса — сферы», 5.2): каталог сфер как данные. Пока в нём три базовые сферы
/// и hardware (ХМ1). Неизвестный id (новая версия → старая касса) ведёт себя как grocery и не теряется — его хранит
/// UserPreferences.MarketSphereDetail как есть.
/// </summary>
public static class SphereCatalog
{
    private static readonly Dictionary<string, SphereProfile> ById = Build();

    private static Dictionary<string, SphereProfile> Build()
    {
        var map = new Dictionary<string, SphereProfile>(StringComparer.Ordinal);
        void Add(SphereProfile p) => map[p.Id] = p;

        // Тексты базовых сфер — как в окне первого запуска (MarketSphereChoiceWindow); поведение этих сфер не меняется.
        Add(new SphereProfile
        {
            Id = MarketSpheres.Grocery,
            BaseBehavior = MarketSpheres.Grocery,
            Title = new L5("Продукты", "Азык-түлүк", "Grocery", "Market (gıda)", "Oziq-ovqat"),
            ShortTitle = new L5("Продукты", "Азык-түлүк", "Grocery", "Market (gıda)", "Oziq-ovqat"),
            Description = new L5("Продуктовый магазин, супермаркет: весы и весовые товары, быстрые продажи.",
                "Азык-түлүк дүкөнү, супермаркет: таразалар жана салмактуу товарлар, тез сатуу.",
                "Grocery store, supermarket: scales and weighed goods, fast sales.",
                "Market, süpermarket: teraziler ve tartılı ürünler, hızlı satış.",
                "Oziq-ovqat do'koni, supermarket: tarozilar va tortiladigan mahsulotlar, tez sotuv."),
            Icon = "🛒",
        });
        Add(new SphereProfile
        {
            Id = MarketSpheres.Clothing,
            BaseBehavior = MarketSpheres.Clothing,
            Title = new L5("Одежда, обувь и похожие", "Кийим, бут кийим жана ушул сыяктуулар", "Clothing, shoes and similar", "Giyim, ayakkabı ve benzeri", "Kiyim, poyabzal va shunga o'xshash"),
            ShortTitle = new L5("Одежда", "Кийим", "Clothing", "Giyim", "Kiyim"),
            Description = new L5("Размеры и цвета, этикетки размеров, обмен по закону, консультант, прокат.",
                "Өлчөмдөр жана түстөр, өлчөм этикеткалары, мыйзам боюнча алмаштыруу, консультант, прокат.",
                "Sizes and colours, size labels, legal exchange, consultant, rentals.",
                "Beden ve renkler, beden etiketleri, yasal değişim, danışman, kiralama.",
                "O'lcham va ranglar, o'lcham yorliqlari, qonun bo'yicha almashtirish, maslahatchi, prokat."),
            Icon = "👕",
            ShowVariants = true,
        });
        Add(new SphereProfile
        {
            Id = MarketSpheres.Services,
            BaseBehavior = MarketSpheres.Services,
            Title = new L5("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar"),
            ShortTitle = new L5("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar"),
            Description = new L5("Салон, ремонт, прокат: услуги без остатка на складе, прокат вещей.",
                "Салон, оңдоо, прокат: кампада калдыгы жок кызматтар, буюмдарды прокатка берүү.",
                "Salon, repair, rentals: services without stock, item rentals.",
                "Salon, tamir, kiralama: stoksuz hizmetler, eşya kiralama.",
                "Salon, ta'mirlash, prokat: omborda qoldiqsiz xizmatlar, buyumlarni prokatga berish."),
            Icon = "🛠",
        });

        // ХМ1: профиль «Хозтовары и стройматериалы» — одна строка регистрации.
        Add(HardwareProfile.Create());
        return map;
    }

    /// <summary>Все известные профили (порядок не гарантируется).</summary>
    public static IReadOnlyCollection<SphereProfile> All => ById.Values;

    /// <summary>Профиль по точному id (регистр важен) или null, если такого id в каталоге нет.</summary>
    public static SphereProfile? TryGet(string? id) =>
        id is not null && ById.TryGetValue(id, out var p) ? p : null;

    /// <summary>Профиль по id; неизвестный id → grocery (поведение «Продуктов», значение не теряется — его хранит вызывающий).</summary>
    public static SphereProfile Get(string? id) => TryGet(id) ?? ById[MarketSpheres.Grocery];

    /// <summary>Профиль магазина по базовой сфере и точной сфере. Точная применяется, только если она есть в каталоге
    /// и её базовое поведение совпадает с базовой сферой (иначе, например, при расхождении кассы и сервера — базовая).</summary>
    public static SphereProfile Resolve(string? baseSphere, string? detail)
    {
        var baseValue = MarketSpheres.Normalize(baseSphere);
        if (TryGet(detail) is { } exact && exact.BaseBehavior == baseValue)
            return exact;
        return Get(baseValue);
    }

    /// <summary>Значение MarketSphereDetail из файла или облака: обрезка, нижний регистр, только [a-z_] до 32 символов
    /// (как поле market_profile в ТЗ бэкенду). Неизвестный каталогу id сохраняется как есть; мусор → null.</summary>
    public static string? SanitizeDetail(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v) || v.Length > 32)
            return null;
        foreach (var ch in v)
            if (ch is not (>= 'a' and <= 'z') and not '_')
                return null;
        return v;
    }

    /// <summary>Единица как её пишут в поле «Ед.»: без регистра, точек, запятых, пробелов, скобок и апострофов; ² и ³ → 2 и 3.</summary>
    public static string NormalizeUnitText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            switch (ch)
            {
                case '²': sb.Append('2'); break;
                case '³': sb.Append('3'); break;
                case '.': case ',': case ' ': case ' ': case '(': case ')': case '\'': case '’': case 'ʻ': case '`':
                    break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
