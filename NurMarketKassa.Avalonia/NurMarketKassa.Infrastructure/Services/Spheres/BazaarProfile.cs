namespace NurMarketKassa.Services;

/// <summary>2026-11-20: режим сферы «Базар» — розница или опт (одна сфера, два режима; решение владельца 20.11).</summary>
public enum BazaarMode
{
    Retail,
    Wholesale,
}

/// <summary>
/// 2026-11-20 (решение владельца: сфера «Базар» с розницей и оптом; ТЗ «Сфера Контейнер-базар», раздел «Общая основа»): ТОЛЬКО КАРКАС — профиль, плитка, режим «Розница / Опт».
/// Базовое поведение — grocery: на сервер в market_sphere уходит grocery, точное значение хранится в UserPreferences.MarketSphereDetail (как у hardware, autoparts).
/// Режим хранится в SphereSettings (раздел <c>bazaar</c>, ключ <c>mode</c>). Серверные значения market_profile из ТЗ — <see cref="RetailDetail"/> / <see cref="WholesaleDetail"/> —
/// читаются как этот же профиль. Функции базара (торг, плитки категорий, цена по количеству, курсы, накладные) — отдельной работой; «продуктовой» сферой «Базар» не считается
/// (см. <see cref="MarketSpheres.IsBazaar"/>).
/// </summary>
public static class BazaarProfile
{
    public const string Id = "bazaar";
    public const string RetailDetail = "bazaar_retail";
    public const string WholesaleDetail = "bazaar_wholesale";

    /// <summary>Текущая сфера — «Базар». Для других сфер всегда false.</summary>
    public static bool IsActive => MarketSpheres.Profile.Id == Id;

    /// <summary>Значение market_profile для сервера по режиму (ТЗ Контейнер-базар, Б-БЗ0).</summary>
    public static string ServerDetail(BazaarMode mode) => mode == BazaarMode.Wholesale ? WholesaleDetail : RetailDetail;

    public static string ModeName(BazaarMode mode) => mode == BazaarMode.Wholesale
        ? Tr.T("Опт", "Дүң", "Wholesale", "Toptan", "Ulgurji")
        : Tr.T("Розница", "Чекене", "Retail", "Perakende", "Chakana");

    public static string ModeHint(BazaarMode mode) => mode == BazaarMode.Wholesale
        ? Tr.T("Продаёте оптом: крупные партии, оптовые покупатели и договорные цены.",
            "Дүң сатасыз: чоң партиялар, дүң сатып алуучулар жана келишим баалары.",
            "You sell wholesale: large lots, wholesale buyers and contract prices.",
            "Toptan satıyorsunuz: büyük partiler, toptan alıcılar ve sözleşme fiyatları.",
            "Ulgurji sotasiz: katta partiyalar, ulgurji xaridorlar va shartnoma narxlari.")
        : Tr.T("Продаёте в розницу: быстрые продажи покупателям с рынка.",
            "Чекене сатасыз: базардагы сатып алуучуларга тез сатуу.",
            "You sell retail: quick sales to market shoppers.",
            "Perakende satıyorsunuz: pazar müşterilerine hızlı satış.",
            "Chakana sotasiz: bozor xaridorlariga tez sotuv.");

    public static SphereProfile Create() => new()
    {
        Id = Id,
        BaseBehavior = MarketSpheres.Grocery,
        Title = new L5("Базар", "Базар", "Bazaar", "Pazar", "Bozor"),
        ShortTitle = new L5("Базар", "Базар", "Bazaar", "Pazar", "Bozor"),
        Description = new L5(
            "Рынок, контейнер, павильон: розница и опт. Режим «Розница» или «Опт» выбирается в настройках",
            "Базар, контейнер, павильон: чекене жана дүң. «Чекене» же «Дүң» режими тууралоолордо тандалат",
            "Market stall, container, pavilion: retail and wholesale. The “Retail” or “Wholesale” mode is chosen in settings",
            "Pazar, konteyner, pavyon: perakende ve toptan. «Perakende» veya «Toptan» modu ayarlarda seçilir",
            "Bozor, konteyner, pavilon: chakana va ulgurji. «Chakana» yoki «Ulgurji» rejimi sozlamalarda tanlanadi"),
        Icon = "🏬",
        IconKey = "store",
        AccentHex = "#EA580C",
        ChoiceVisible = true,
    };
}
