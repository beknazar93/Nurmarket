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

    // 2026-11-12 (СФ4, ТЗ хозмаг): признаки возможностей профиля. По умолчанию ВСЕ выключены — grocery / clothing / services
    // ведут себя как раньше; включает только профиль (hardware…). Проверки «IsHardware» в новых местах заменяются этими признаками.
    /// <summary>Дробное количество по единице профиля (м, м², л…) — ХМ2. Фактически включается ещё флагом сервера.</summary>
    public bool FractionalUnits { get; init; }
    /// <summary>Скан коробки = N штук — ХМ3 (дополнительно нужна галочка владельца).</summary>
    public bool PackScan { get; init; }
    /// <summary>Окно «Количество / длина / Д×Ш» — ХМ6.</summary>
    public bool QuantityPad { get; init; }
    /// <summary>Калькулятор расхода «Сколько нужно?» — ХМ5.</summary>
    public bool ConsumptionCalc { get; init; }
    /// <summary>Цена прораба (опт для отмеченных клиентов) — ХМ8: возможность профиля (включение — по размеру магазина).</summary>
    public bool ForemanPriceAvailable { get; init; }
    /// <summary>Плитка сферы показывается в окне первого запуска и в Настройках. По умолчанию ВЫКЛ: профиль сам объявляет, что готов к выбору
    /// (три базовые и hardware — да); у скрытых выбор только через техподдержку (MarketSphereLock).</summary>
    public bool ChoiceVisible { get; init; }

    // 2026-11-20 (решение владельца: Хозмаг и Строймаг — две сферы на одной серверной базе grocery): признаки профиля вместо проверок «id == hardware».
    /// <summary>Семейство «Хозмаг / Строймаг» (общая основа: упаковки, фасовка, дробные единицы, калькулятор). Для остальных сфер false.</summary>
    public bool HardwareFamily { get; init; }
    /// <summary>«Смета» доступна без Маркетплейса — только Строймаг (в остальных сферах включается модулем).</summary>
    public bool HasEstimate { get; init; }
    /// <summary>Прокат включён по умолчанию — только Строймаг (в остальных сферах — модулем Маркетплейса или если прокат уже есть).</summary>
    public bool HasRentalsByDefault { get; init; }
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
            Title = new L5("Продуктовый", "Азык-түлүк", "Grocery", "Market (gıda)", "Oziq-ovqat"), // 2026-11-20: «Продуктовый» — как просил владелец
            ShortTitle = new L5("Продукты", "Азык-түлүк", "Grocery", "Market (gıda)", "Oziq-ovqat"),
            Description = new L5("Продуктовый магазин, супермаркет: весы и весовые товары, быстрые продажи.",
                "Азык-түлүк дүкөнү, супермаркет: таразалар жана салмактуу товарлар, тез сатуу.",
                "Grocery store, supermarket: scales and weighed goods, fast sales.",
                "Market, süpermarket: teraziler ve tartılı ürünler, hızlı satış.",
                "Oziq-ovqat do'koni, supermarket: tarozilar va tortiladigan mahsulotlar, tez sotuv."),
            Icon = "🛒",
            ChoiceVisible = true,
        });
        Add(new SphereProfile
        {
            Id = MarketSpheres.Clothing,
            BaseBehavior = MarketSpheres.Clothing,
            Title = new L5("Магазин одежды", "Кийим дүкөнү", "Clothing store", "Giyim mağazası", "Kiyim do'koni"), // 2026-11-20: название плитки по решению владельца (обувь и похожие — в описании)
            ShortTitle = new L5("Одежда", "Кийим", "Clothing", "Giyim", "Kiyim"),
            // 2026-11-20: прокат по умолчанию только в Строймаге (решение владельца) — в описании не обещаем, он ставится из Маркетплейса.
            Description = new L5("Одежда, обувь и похожие. Размеры и цвета, этикетки размеров, обмен по закону, консультант. Прокат — в Маркетплейсе",
                "Кийим, бут кийим жана ушул сыяктуулар. Өлчөмдөр жана түстөр, өлчөм этикеткалары, мыйзам боюнча алмаштыруу, консультант. Прокат — Маркетплейсте",
                "Clothing, shoes and similar. Sizes and colours, size labels, legal exchange, consultant. Rentals — in the Marketplace",
                "Giyim, ayakkabı ve benzeri. Beden ve renkler, beden etiketleri, yasal değişim, danışman. Kiralama — Pazaryeri'nde",
                "Kiyim, poyabzal va shunga o'xshash. O'lcham va ranglar, o'lcham yorliqlari, qonun bo'yicha almashtirish, maslahatchi. Prokat — Marketplace'da"),
            Icon = "👕",
            ChoiceVisible = true,
            ShowVariants = true,
        });
        Add(new SphereProfile
        {
            Id = MarketSpheres.Services,
            BaseBehavior = MarketSpheres.Services,
            Title = new L5("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar"),
            ShortTitle = new L5("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar"),
            // 2026-11-20: то же — прокат вещей теперь модуль Маркетплейса.
            Description = new L5("Салон, ремонт: услуги без остатка на складе. Прокат вещей — в Маркетплейсе",
                "Салон, оңдоо: кампада калдыгы жок кызматтар. Буюмдарды прокатка берүү — Маркетплейсте",
                "Salon, repair: services without stock. Item rentals — in the Marketplace",
                "Salon, tamir: stoksuz hizmetler. Eşya kiralama — Pazaryeri'nde",
                "Salon, ta'mirlash: omborda qoldiqsiz xizmatlar. Buyumlarni prokatga berish — Marketplace'da"),
            Icon = "🛠",
            ChoiceVisible = true,
        });

        // ХМ1: профиль «Хозтовары и стройматериалы» — одна строка регистрации.
        Add(HardwareProfile.CreateHozmag());   // 2026-11-20: «Хозмаг» (hozmag) — без Сметы и проката по умолчанию
        Add(HardwareProfile.CreateStroymag()); // 2026-11-20: «Строймаг» (stroymag) — всё как было у hardware
        Add(BazaarProfile.Create()); // 2026-11-20: «Базар» (bazaar) — каркас: розница / опт внутри сферы
        Add(StationeryProfile.Create()); // 2026-11-12: КТ1 — «Канцтовары и книги»
        Add(ElectronicsProfile.Create()); // 2026-11-12: ЭЛ1 — «Электроника и техника»
        Add(AutoPartsProfile.Create()); // 2026-11-20: АЗ1 — «Автозапчасти и автотовары»
        return map;
    }

    /// <summary>2026-11-20 (решение владельца): порядок плиток выбора — главные шесть (Продуктовый, Хозмаг, Строймаг, Магазин одежды, Магазин электроники, Базар),
    /// потом «Другие» (<see cref="OtherIds"/>: Автозапчасти, Канцтовары, Услуги).</summary>
    public static readonly string[] MainIds =
        { MarketSpheres.Grocery, HardwareProfile.HozmagId, HardwareProfile.StroymagId, MarketSpheres.Clothing, ElectronicsProfile.Id, BazaarProfile.Id };
    public static readonly string[] OtherIds = { AutoPartsProfile.Id, StationeryProfile.Id, MarketSpheres.Services };

    /// <summary>2026-11-12 (ХМ14): профили для плиток выбора (окно первого запуска и Настройки). 2026-11-20: порядок задан <see cref="MainIds"/> + <see cref="OtherIds"/>,
    /// потом (на будущее) любые другие профили с <see cref="SphereProfile.ChoiceVisible"/> по id.</summary>
    public static IReadOnlyList<SphereProfile> ChoiceList()
    {
        var fixedIds = MainIds.Concat(OtherIds).ToArray();
        var list = fixedIds.Select(Get).Where(p => p.ChoiceVisible).ToList();
        list.AddRange(ById.Values.Where(p => p.ChoiceVisible && !fixedIds.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal));
        return list;
    }

    /// <summary>2026-11-20: прежние / серверные значения точной сферы → id текущего профиля. Старое «hardware» (одна сфера «Хозтовары и стройматериалы») —
    /// это Строймаг: у таких компаний Смета, прокат и уровни цен остаются без потерь. bazaar_retail / bazaar_wholesale (значения market_profile из ТЗ) — профиль «Базар»
    /// (режим розница / опт хранится в SphereSettings). Сохранённая строка в настройках не переписывается — читается через <see cref="TryGet"/>.</summary>
    private static string CanonicalId(string id) => id switch
    {
        HardwareProfile.Id => HardwareProfile.StroymagId,
        BazaarProfile.RetailDetail or BazaarProfile.WholesaleDetail => BazaarProfile.Id,
        _ => id,
    };

    /// <summary>Все известные профили (порядок не гарантируется).</summary>
    public static IReadOnlyCollection<SphereProfile> All => ById.Values;

    /// <summary>Профиль по точному id (регистр важен) или null, если такого id в каталоге нет.</summary>
    public static SphereProfile? TryGet(string? id) =>
        id is not null && ById.TryGetValue(CanonicalId(id), out var p) ? p : null;

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
