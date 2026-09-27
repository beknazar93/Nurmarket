namespace NurMarketKassa.Services;

/// <summary>Краткое описание изменений ТЕКУЩЕЙ версии — показывается кассиру один раз
/// в окне "Что нового" после того, как касса обновилась (см. UserPreferences.LastSeenAppVersion),
/// и используется как текст релиза при публикации на GitHub.
/// ВАЖНО: перед каждым релизом содержимое <see cref="Latest"/> (и переводов) полностью ЗАМЕНЯЕТСЯ
/// на изменения именно этой версии — не дописывается поверх старых пунктов. История прошлых версий
/// не нужна здесь: она уже есть в самих релизах на GitHub, а этот список — только "что нового прямо
/// сейчас". 2026-09-07: список показывается на языке интерфейса (ru/ky/en/tr/uz, см.
/// <see cref="LatestForCurrentLanguage"/>) — раньше при кыргызском интерфейсе заголовок был
/// кыргызским, а сами пункты русскими.</summary>
public static class AppChangelog
{
    public static readonly string[] Latest =
    [
        "Выгрузки в Excel и Word и ABC-анализ считаются по данным сервера — цифры совпадают с сайтом и экраном «Финансы»",
        "Смешанная оплата показывается отдельно («+ смешанная»), как на сайте, а не внутри безнала",
        "«Финансы → Смены» показывают все смены, а не только первые 100",
        "Номер чека в «Истории чеков» и в «Возврате» одинаковый и совпадает с сайтом",
        "«Последние продажи» в «Сводке» показывают «+ ещё N», если в чеке несколько товаров",
        "Ранее в 1.17.19–1.17.21: шесть видов кассы, редактор своих тем, оформленные выгрузки, ценники через драйвер, масштаб на квадратных экранах, история чеков",
    ];

    public static readonly string[] LatestKy =
    [
        "Excel жана Word'го жүктөө жана ABC-талдоо сервердин маалыматы боюнча эсептелет — сандар сайт жана «Каржы» экраны менен дал келет",
        "Аралаш төлөм сайттагыдай өзүнчө көрсөтүлөт («+ аралаш»), накталай эмес төлөмдүн ичинде эмес",
        "«Каржы → Сменалар» биринчи 100 эмес, бардык сменаларды көрсөтөт",
        "«Чектердин тарыхы» жана «Кайтаруу» терезелериндеги чектин номери бирдей жана сайт менен дал келет",
        "«Жыйынтыктагы» «Акыркы сатуулар» чекте бир нече товар болсо «+ дагы N» деп көрсөтөт",
        "Мурда 1.17.19–1.17.21де: кассанын алты көрүнүшү, темалардын редактору, жасалгаланган жүктөөлөр, драйвер аркылуу баа белгилери, чарчы экрандагы масштаб, чектердин тарыхы",
    ];

    public static readonly string[] LatestEn =
    [
        "Excel and Word exports and ABC analysis use server data — the numbers match the website and the “Finance” screen",
        "Mixed payment is shown separately (“+ mixed”), as on the website, not inside card payments",
        "“Finance → Shifts” shows all shifts, not only the first 100",
        "The receipt number in “Receipt history” and “Return” is the same and matches the website",
        "“Recent sales” in the “Overview” shows “+ N more” when a receipt has several products",
        "Earlier in 1.17.19–1.17.21: six till layouts, your own theme editor, formatted exports, price tags through the driver, scaling on square screens, receipt history",
    ];

    public static readonly string[] LatestTr =
    [
        "Excel ve Word dışa aktarımları ve ABC analizi sunucu verileriyle hesaplanır — rakamlar site ve «Finans» ekranıyla aynı",
        "Karışık ödeme sitedeki gibi ayrı gösterilir («+ karışık»), kartlı ödemelerin içinde değil",
        "«Finans → Vardiyalar» ilk 100'ü değil, tüm vardiyaları gösterir",
        "«Fiş geçmişi» ve «İade» pencerelerindeki fiş numarası aynı ve siteyle eşleşiyor",
        "«Özet»teki «Son satışlar» fişte birden çok ürün varsa «+ N daha» gösterir",
        "Önceki 1.17.19–1.17.21: altı kasa görünümü, tema düzenleyici, biçimli dışa aktarımlar, sürücüyle etiketler, kare ekranlarda ölçek, fiş geçmişi",
    ];

    public static readonly string[] LatestUz =
    [
        "Excel va Word'ga eksport va ABC tahlili server ma'lumotlari bo'yicha hisoblanadi — raqamlar sayt va «Moliya» ekrani bilan mos",
        "Aralash to'lov saytdagidek alohida ko'rsatiladi («+ aralash»), naqdsiz to'lov ichida emas",
        "«Moliya → Smenalar» birinchi 100 tasini emas, barcha smenalarni ko'rsatadi",
        "«Cheklar tarixi» va «Qaytarish» oynalaridagi chek raqami bir xil va sayt bilan mos",
        "«Umumiy»dagi «So'nggi sotuvlar» chekda bir nechta mahsulot bo'lsa «+ yana N» deb ko'rsatadi",
        "Avval 1.17.19–1.17.21 da: kassaning oltita ko'rinishi, mavzular muharriri, bezatilgan eksportlar, drayver orqali narx yorliqlari, kvadrat ekranlarda masshtab, cheklar tarixi",
    ];

    /// <summary>Список на языке интерфейса (2026-09-07).</summary>
    public static string[] LatestForCurrentLanguage() =>
        UserPreferences.Instance.Language switch
        {
            AppLanguage.Kyrgyz => LatestKy,
            AppLanguage.English => LatestEn,
            AppLanguage.Turkish => LatestTr,
            AppLanguage.Uzbek => LatestUz,
            _ => Latest,
        };

    public static string LatestAsBulletedText() =>
        string.Join("\n", System.Linq.Enumerable.Select(LatestForCurrentLanguage(), line => "• " + line));
}
