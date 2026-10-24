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
        "Новое окно оплаты: компактное, в две колонки; клиент выбирается карточкой, при долге видны сумма чека и «Получить сейчас»",
        "Маркетплейс как магазин приложений: витрина, страница «Подробнее» со слайдами, анимацией, «Чем поможет» и ценой; темы и виды с превью, купленное помечено «Куплено»",
        "Красивый чек-«бумага» в истории чеков; значок «Подробнее» и карточка товара",
        "Склад: массовое добавление товаров сканером; у поставщика «Погасить весь долг» (двойные записи не оплачиваются)",
        "Долг клиента считается как на сервере, двойные записи помечены; «Выручка (без долга)» везде одинаково",
        "Z-отчёт совпадает с X, в нём раскладка «в ящике должно быть»; остаток при открытии смены берётся из последней закрытой; предупреждение о смене старше 22 часов",
        "Воронка: клик по клиенту открывает чат (WhatsApp, Instagram, Telegram), стрелки «назад» и «вперёд» крупнее",
        "Программа владельца: Сводка обновляется сама раз в минуту, пустой счёт нельзя выставить, дубль телефона клиента виден сразу",
        "«Горячие клавиши» и «Импорт» в общем стиле, История чеков — по размеру окна кассы, «Пополнение» без услуг, окна не уходят под панель задач",
        "После входа виден кассир за кассой; исправлены утечки памяти (закрытые окна, миниатюры, темы)",
    ];

    public static readonly string[] LatestKy =
    [
        "Төлөмдүн жаңы терезеси: ыкчам, эки тилкелүү; кардар карточка менен тандалат, карызда чектин суммасы жана «Азыр алуу» көрүнөт",
        "Маркетплейс колдонмолор дүкөнүндөй: витрина, слайддар, анимация, «Эмне жардам берет» жана баасы бар «Кененирээк» барагы; алдын ала көрүүсү бар темалар жана көрүнүштөр, сатып алынганы «Сатып алынды» деп белгиленет",
        "Чектердин тарыхында кооз кагаз чек; «Кененирээк» белгиси жана товардын карточкасы",
        "Кампа: товарларды сканер менен жапырт кошуу; жеткирүүчүдө «Бүт карызды төлөө» (кош жазуулар төлөнбөйт)",
        "Кардардын карызы сервердегидей эсептелет, кош жазуулар белгиленет; «Түшүм (карызсыз)» бардык жерде бирдей",
        "Z-отчёт X менен дал келет, ичинде «ящикте болушу керек» бөлүштүрүү бар; смена ачылганда калдык акыркы жабылгандан алынат; 22 сааттан эски смена жөнүндө эскертүү",
        "Воронка: кардарды чыкылдатсаңыз чат ачылат (WhatsApp, Instagram, Telegram), «артка» жана «алдыга» жебелери чоңураак",
        "Ээсинин программасы: Жыйынтык мүнөтүнө бир жолу өзү жаңыланат, бош эсепти чыгарууга болбойт, кардардын телефону кайталанса дароо көрүнөт",
        "«Ысык баскычтар» жана «Импорт» жалпы стилде, Чектердин тарыхы кассанын терезесине жараша, «Толуктоо» кызматтарсыз, терезелер тапшырмалар панелинин астына кирбейт",
        "Киргенден кийин кассада ким отурганы көрүнөт; эс тутумдун агып кетүүлөрү оңдолду (жабылган терезелер, миниатюралар, темалар)",
    ];

    public static readonly string[] LatestEn =
    [
        "New payment window: compact, two columns; the client is picked with a card, and with a debt it shows the receipt total and “Receive now”",
        "Marketplace like an app store: a showcase, a “Details” page with slides, animation, “How it helps” and the price; themes and looks with previews, purchased items marked “Purchased”",
        "A nicer paper-style receipt in the receipt history; the “Details” badge and the product card",
        "Stock: bulk adding of products with a scanner; the supplier has “Pay off the whole debt” (duplicate entries are not paid)",
        "A client's debt is counted as on the server, duplicate entries are marked; “Revenue (without debt)” is the same everywhere",
        "The Z-report matches the X-report and has the “should be in the drawer” breakdown; the opening balance of a shift comes from the last closed one; a warning for a shift older than 22 hours",
        "Funnel: clicking a client opens a chat (WhatsApp, Instagram, Telegram), larger back and forward arrows",
        "Owner app: the Summary refreshes itself every minute, an empty invoice cannot be issued, a duplicate client phone is shown at once",
        "“Hotkeys” and “Import” in the common style, the receipt history follows the till window size, “Restocking” without services, windows no longer go under the taskbar",
        "The cashier at the till is shown after login; memory leaks fixed (closed windows, thumbnails, themes)",
    ];

    public static readonly string[] LatestTr =
    [
        "Yeni ödeme penceresi: kompakt, iki sütunlu; müşteri kartla seçilir, borçta fiş tutarı ve «Şimdi al» görünür",
        "Uygulama mağazası gibi Pazar yeri: vitrin, slaytlar, animasyon, «Nasıl yardımcı olur» ve fiyatlı «Ayrıntılı» sayfası; önizlemeli temalar ve görünümler, satın alınanlar «Satın alındı» diye işaretlenir",
        "Fiş geçmişinde güzel kâğıt fiş; «Ayrıntılı» simgesi ve ürün kartı",
        "Depo: ürünlerin tarayıcıyla toplu eklenmesi; tedarikçide «Tüm borcu öde» (çift kayıtlar ödenmez)",
        "Müşteri borcu sunucudaki gibi hesaplanır, çift kayıtlar işaretlenir; «Ciro (borçsuz)» her yerde aynı",
        "Z raporu X ile aynıdır, içinde «çekmecede olması gereken» dökümü var; vardiya açılırken bakiye son kapanan vardiyadan alınır; 22 saatten eski vardiya uyarısı",
        "Satış hunisi: müşteriye tıklamak sohbeti açar (WhatsApp, Instagram, Telegram), geri ve ileri okları daha büyük",
        "Sahip programı: Özet dakikada bir kendiliğinden yenilenir, boş fatura kesilemez, müşteri telefonu tekrarı hemen görünür",
        "«Kısayol tuşları» ve «İçe aktar» ortak tarzda, Fiş geçmişi kasa penceresinin boyutuna göre, «Stok yenileme» hizmetsiz, pencereler görev çubuğunun altına girmez",
        "Girişten sonra kasadaki kasiyer görünür; bellek sızıntıları giderildi (kapatılan pencereler, küçük resimler, temalar)",
    ];

    public static readonly string[] LatestUz =
    [
        "Yangi to'lov oynasi: ixcham, ikki ustunli; mijoz karta orqali tanlanadi, qarzda chek summasi va «Hozir olish» ko'rinadi",
        "Ilovalar do'koni kabi Marketplace: vitrina, slaydlar, animatsiya, «Qanday yordam beradi» va narxli «Batafsil» sahifasi; oldindan ko'rish bilan mavzular va ko'rinishlar, sotib olingani «Sotib olingan» deb belgilanadi",
        "Cheklar tarixida chiroyli qog'oz chek; «Batafsil» belgisi va mahsulot kartasi",
        "Ombor: mahsulotlarni skaner bilan ommaviy qo'shish; yetkazib beruvchida «Butun qarzni to'lash» (qo'sh yozuvlar to'lanmaydi)",
        "Mijoz qarzi serverdagidek hisoblanadi, qo'sh yozuvlar belgilanadi; «Tushum (qarzsiz)» hamma joyda bir xil",
        "Z-hisobot X bilan mos keladi, unda «tortmada bo'lishi kerak» taqsimoti bor; smena ochilganda qoldiq oxirgi yopilgan smenadan olinadi; 22 soatdan eski smena haqida ogohlantirish",
        "Voronka: mijozga bosish chatni ochadi (WhatsApp, Instagram, Telegram), orqaga va oldinga strelkalari kattaroq",
        "Egasining dasturi: Xulosa daqiqada bir marta o'zi yangilanadi, bo'sh hisobni chiqarib bo'lmaydi, mijoz telefoni takrori darhol ko'rinadi",
        "«Tezkor tugmalar» va «Import» umumiy uslubda, Cheklar tarixi kassa oynasi o'lchamiga qarab, «To'ldirish» xizmatlarsiz, oynalar vazifalar paneli ostiga kirmaydi",
        "Kirgandan keyin kassada kim ekani ko'rinadi; xotira oqishlari tuzatildi (yopilgan oynalar, kichik rasmlar, mavzular)",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-24: Android 1.17.60 (тестовая) — то же, что на Windows в 1.17.60, без пунктов только для компьютера (воронка, окна и панель задач, горячие клавиши, импорт) и без программы владельца.
    // 2026-10-21: Android 1.17.59 (тестовая) — то же, что на Windows в 1.17.59, без пунктов только для компьютера.
    // 2026-10-11: Android 1.17.58 (после 1.17.53 — всё, что вышло на Windows в 1.17.54–1.17.58).
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Новое окно оплаты: компактное, в две колонки; клиент выбирается карточкой, при долге видны сумма чека и «Получить сейчас»",
        "Маркетплейс как магазин приложений: витрина, страница «Подробнее» со слайдами, анимацией, «Чем поможет» и ценой; темы и виды с превью, купленное помечено «Куплено»",
        "Красивый чек-«бумага» в истории чеков; значок «Подробнее» и карточка товара",
        "Долг клиента считается как на сервере, двойные записи помечены; «Выручка (без долга)» везде одинаково",
        "Z-отчёт совпадает с X, в нём раскладка «в ящике должно быть»; остаток при открытии смены берётся из последней закрытой; предупреждение о смене старше 22 часов",
        "После входа виден кассир за кассой; исправлены утечки памяти (закрытые окна, миниатюры, темы)",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Төлөмдүн жаңы терезеси: ыкчам, эки тилкелүү; кардар карточка менен тандалат, карызда чектин суммасы жана «Азыр алуу» көрүнөт",
        "Маркетплейс колдонмолор дүкөнүндөй: витрина, слайддар, анимация, «Эмне жардам берет» жана баасы бар «Кененирээк» барагы; алдын ала көрүүсү бар темалар жана көрүнүштөр, сатып алынганы «Сатып алынды» деп белгиленет",
        "Чектердин тарыхында кооз кагаз чек; «Кененирээк» белгиси жана товардын карточкасы",
        "Кардардын карызы сервердегидей эсептелет, кош жазуулар белгиленет; «Түшүм (карызсыз)» бардык жерде бирдей",
        "Z-отчёт X менен дал келет, ичинде «ящикте болушу керек» бөлүштүрүү бар; смена ачылганда калдык акыркы жабылгандан алынат; 22 сааттан эски смена жөнүндө эскертүү",
        "Киргенден кийин кассада ким отурганы көрүнөт; эс тутумдун агып кетүүлөрү оңдолду (жабылган терезелер, миниатюралар, темалар)",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "New payment window: compact, two columns; the client is picked with a card, and with a debt it shows the receipt total and “Receive now”",
        "Marketplace like an app store: a showcase, a “Details” page with slides, animation, “How it helps” and the price; themes and looks with previews, purchased items marked “Purchased”",
        "A nicer paper-style receipt in the receipt history; the “Details” badge and the product card",
        "A client's debt is counted as on the server, duplicate entries are marked; “Revenue (without debt)” is the same everywhere",
        "The Z-report matches the X-report and has the “should be in the drawer” breakdown; the opening balance of a shift comes from the last closed one; a warning for a shift older than 22 hours",
        "The cashier at the till is shown after login; memory leaks fixed (closed windows, thumbnails, themes)",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Yeni ödeme penceresi: kompakt, iki sütunlu; müşteri kartla seçilir, borçta fiş tutarı ve «Şimdi al» görünür",
        "Uygulama mağazası gibi Pazar yeri: vitrin, slaytlar, animasyon, «Nasıl yardımcı olur» ve fiyatlı «Ayrıntılı» sayfası; önizlemeli temalar ve görünümler, satın alınanlar «Satın alındı» diye işaretlenir",
        "Fiş geçmişinde güzel kâğıt fiş; «Ayrıntılı» simgesi ve ürün kartı",
        "Müşteri borcu sunucudaki gibi hesaplanır, çift kayıtlar işaretlenir; «Ciro (borçsuz)» her yerde aynı",
        "Z raporu X ile aynıdır, içinde «çekmecede olması gereken» dökümü var; vardiya açılırken bakiye son kapanan vardiyadan alınır; 22 saatten eski vardiya uyarısı",
        "Girişten sonra kasadaki kasiyer görünür; bellek sızıntıları giderildi (kapatılan pencereler, küçük resimler, temalar)",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Yangi to'lov oynasi: ixcham, ikki ustunli; mijoz karta orqali tanlanadi, qarzda chek summasi va «Hozir olish» ko'rinadi",
        "Ilovalar do'koni kabi Marketplace: vitrina, slaydlar, animatsiya, «Qanday yordam beradi» va narxli «Batafsil» sahifasi; oldindan ko'rish bilan mavzular va ko'rinishlar, sotib olingani «Sotib olingan» deb belgilanadi",
        "Cheklar tarixida chiroyli qog'oz chek; «Batafsil» belgisi va mahsulot kartasi",
        "Mijoz qarzi serverdagidek hisoblanadi, qo'sh yozuvlar belgilanadi; «Tushum (qarzsiz)» hamma joyda bir xil",
        "Z-hisobot X bilan mos keladi, unda «tortmada bo'lishi kerak» taqsimoti bor; smena ochilganda qoldiq oxirgi yopilgan smenadan olinadi; 22 soatdan eski smena haqida ogohlantirish",
        "Kirgandan keyin kassada kim ekani ko'rinadi; xotira oqishlari tuzatildi (yopilgan oynalar, kichik rasmlar, mavzular)",
    ];

    public static string[] LatestForCurrentLanguage() =>
        OperatingSystem.IsAndroid()
            ? UserPreferences.Instance.Language switch
            {
                AppLanguage.Kyrgyz => AndroidLatestKy,
                AppLanguage.English => AndroidLatestEn,
                AppLanguage.Turkish => AndroidLatestTr,
                AppLanguage.Uzbek => AndroidLatestUz,
                _ => AndroidLatest,
            }
            : UserPreferences.Instance.Language switch
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
