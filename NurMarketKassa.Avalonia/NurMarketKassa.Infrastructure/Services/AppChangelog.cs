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
        "Программа владельца: новый раздел «Воронка» — сделки по этапам, карточку можно перетащить мышью, у сделки источник клиента (Telegram, WhatsApp, Instagram, звонок…), переписка в WhatsApp и Instagram прямо из карточки",
        "Раздел «WhatsApp» — WhatsApp Web прямо в программе владельца (вход по QR-коду один раз)",
        "ИИ-советник: можно спрашивать голосом (кнопка микрофона), сам находит фото товаров по штрихкоду и ставит их на склад, предлагает акции на проблемные товары, включает и выключает функции бота после вашего «Применить»",
        "«Сводка»: карточка «План продаж на месяц» — цель, сколько продано, сколько нужно продавать в день и успеваете ли",
        "Голосовое управление кассой: фразу, которую касса не поняла, разбирает ИИ; можно спросить цену и остаток («касса, сколько стоит пепси»)",
        "Подсказки «С этим часто берут» со всех касс собираются на сервере; ошибки программы автоматически уходят в поддержку (без паролей и токенов)",
    ];

    public static readonly string[] LatestKy =
    [
        "Ээсинин программасы: жаңы «Воронка» бөлүмү — этаптар боюнча келишимдер, карточканы чычкан менен сүйрөөгө болот, кардардын булагы (Telegram, WhatsApp, Instagram, чалуу…), карточкадан эле WhatsApp жана Instagram аркылуу кат алышуу",
        "«WhatsApp» бөлүмү — WhatsApp Web ээсинин программасынын ичинде (QR-код менен бир жолу кирүү)",
        "ИИ-кеңешчи: үн менен суроого болот (микрофон баскычы), товарлардын сүрөтүн штрихкод боюнча өзү таап, кампага коёт, көйгөйлүү товарларга акция сунуштайт, боттун функцияларын «Колдонуу» дегенден кийин күйгүзүп-өчүрөт",
        "«Жыйынтык»: «Айлык сатуу планы» карточкасы — максат, канча сатылды, күнүнө канча сатуу керек жана үлгүрүп жатасызбы",
        "Кассаны үн менен башкаруу: касса түшүнбөгөн сөздү ИИ талдайт; бааны жана калдыкты сурасаңыз болот («касса, пепси канча турат»)",
        "Бардык кассалардын «Муну менен көп алышат» кеңештери серверге чогулат; программанын каталары колдоого өзү кетет (сырсөз жана токенсиз)",
    ];

    public static readonly string[] LatestEn =
    [
        "Owner program: new “Funnel” section — deals by stage, drag a card with the mouse, client source on each deal (Telegram, WhatsApp, Instagram, phone call…), chat on WhatsApp and Instagram right from the card",
        "“WhatsApp” section — WhatsApp Web inside the owner program (sign in with a QR code once)",
        "AI advisor: ask by voice (microphone button), it finds product photos by barcode and puts them on the warehouse cards, suggests promotions for problem products, turns bot features on and off after you press “Apply”",
        "Overview: “Monthly sales plan” card — the target, how much is sold, how much to sell per day and whether you are on track",
        "Voice control of the till: a phrase the till did not understand is parsed by AI; you can ask for a price and stock (“till, how much is Pepsi”)",
        "“Often bought with” suggestions from all tills are collected on the server; program errors are sent to support automatically (without passwords or tokens)",
    ];

    public static readonly string[] LatestTr =
    [
        "Sahip programı: yeni «Huni» bölümü — aşamalara göre anlaşmalar, kart fareyle sürüklenebilir, her anlaşmada müşteri kaynağı (Telegram, WhatsApp, Instagram, telefon…), karttan doğrudan WhatsApp ve Instagram yazışması",
        "«WhatsApp» bölümü — WhatsApp Web sahip programının içinde (QR koduyla bir kez giriş)",
        "Yapay zekâ danışmanı: sesle sorulabilir (mikrofon düğmesi), ürün fotoğraflarını barkoddan kendisi bulup depoya koyar, sorunlu ürünler için kampanya önerir, «Uygula» dedikten sonra bot özelliklerini açıp kapatır",
        "Özet: «Aylık satış planı» kartı — hedef, ne kadar satıldı, günde ne kadar satmak gerektiği ve yetişip yetişmediğiniz",
        "Kasayı sesle yönetme: kasanın anlamadığı cümleyi yapay zekâ çözer; fiyat ve stok sorulabilir («kasa, Pepsi ne kadar»)",
        "Tüm kasaların «Bununla sık alınır» önerileri sunucuda toplanır; program hataları desteğe kendiliğinden gider (şifre ve token olmadan)",
    ];

    public static readonly string[] LatestUz =
    [
        "Ega dasturi: yangi «Voronka» bo'limi — bosqichlar bo'yicha bitimlar, kartani sichqoncha bilan sudrash mumkin, bitimda mijoz manbasi (Telegram, WhatsApp, Instagram, qo'ng'iroq…), kartadan to'g'ridan-to'g'ri WhatsApp va Instagram yozishmasi",
        "«WhatsApp» bo'limi — WhatsApp Web ega dasturining ichida (QR-kod bilan bir marta kirish)",
        "SI maslahatchi: ovoz bilan so'rash mumkin (mikrofon tugmasi), mahsulot rasmlarini shtrix-kod bo'yicha o'zi topib omborga qo'yadi, muammoli mahsulotlarga aksiya taklif qiladi, «Qo'llash»dan keyin bot funksiyalarini yoqib-o'chiradi",
        "«Umumiy ko'rinish»: «Oylik savdo rejasi» kartasi — maqsad, qancha sotildi, kuniga qancha sotish kerak va ulgurayapsizmi",
        "Kassani ovoz bilan boshqarish: kassa tushunmagan gapni SI tahlil qiladi; narx va qoldiqni so'rash mumkin («kassa, pepsi qancha turadi»)",
        "Barcha kassalarning «Bu bilan ko'p olishadi» tavsiyalari serverda yig'iladi; dastur xatolari yordamga o'zi ketadi (parol va tokensiz)",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    public static readonly string[] AndroidLatest =
    [
        "Кассовые терминалы со встроенным сканером и принтером (Sunmi, iMin, Urovo, Newland и похожие): коды со сканера принимаются сами, встроенный принтер чеков находится сам",
        "При сворачивании касса больше не закрывается — работает в фоне, в шторке «NurMarket работает»",
        "Кнопка 📷 в поиске товара — сканер штрихкодов камерой",
        "Окна весового товара, возврата, оплаты долга, проката, истории чеков и настроек подстроены под телефон; каталог — по 12 товаров на странице, листается быстрее",
        "Программа владельца: «Подробнее» в продажах больше не зависает; новые разделы «ИИ-советник», «Воронка» (с источником клиента) и «WhatsApp»",
        "Изъятие больше наличных в кассе запрещено; если кассу закрыли сразу после оплаты, товары больше не возвращаются в чек",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Ичинде сканер жана принтер бар касса терминалдары (Sunmi, iMin, Urovo, Newland ж.б.): сканердин коддору өзү кабыл алынат, чек принтери өзү табылат",
        "Кичирейткенде касса мындан ары жабылбайт — фондо иштейт, билдирмелерде «NurMarket иштеп жатат»",
        "Товар издөөдө 📷 баскычы — камера менен штрихкод сканери",
        "Салмак товары, кайтаруу, карыз төлөө, прокат, чектердин тарыхы жана жөндөөлөр терезелери телефонго ылайыкталды; каталогдо баракта 12 товар, тезирээк жылат",
        "Ээсинин программасы: сатуудагы «Толугураак» мындан ары катып калбайт; жаңы бөлүмдөр «ИИ-кеңешчи», «Воронка» (кардардын булагы менен) жана «WhatsApp»",
        "Кассадагы накталайдан көп алуу тыюу салынды; төлөмдөн кийин касса дароо жабылса, товарлар чекке кайтпайт",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Till terminals with a built-in scanner and printer (Sunmi, iMin, Urovo, Newland and similar): scanner codes are accepted and the built-in receipt printer is found automatically",
        "Minimising no longer closes the till — it keeps running in the background with “NurMarket is running” in the notification shade",
        "The 📷 button in product search — barcode scanning with the camera",
        "Weighed-product, return, debt payment, rentals, receipt history and settings windows fit a phone; the catalog shows 12 products per page and pages faster",
        "Owner app: “Details” in sales no longer freezes; new sections “AI advisor”, “Funnel” (with client source) and “WhatsApp”",
        "A cash-out larger than the cash in the till is not allowed; if the till is closed right after payment, the items no longer come back into the receipt",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Dahili tarayıcılı ve yazıcılı kasa terminalleri (Sunmi, iMin, Urovo, Newland ve benzerleri): tarayıcı kodları kendiliğinden alınır, dahili fiş yazıcısı kendiliğinden bulunur",
        "Küçültünce kasa artık kapanmıyor — arka planda çalışır, bildirimlerde «NurMarket çalışıyor»",
        "Ürün aramada 📷 düğmesi — kamerayla barkod tarama",
        "Tartılı ürün, iade, borç ödeme, kiralama, fiş geçmişi ve ayarlar pencereleri telefona uyarlandı; katalogda sayfada 12 ürün, daha hızlı",
        "Sahip programı: satışlarda «Ayrıntılar» artık donmuyor; yeni bölümler «YZ danışmanı», «Huni» (müşteri kaynağıyla) ve «WhatsApp»",
        "Kasadaki nakitten fazla çıkış yasak; kasa ödemeden hemen sonra kapanırsa ürünler fişe geri gelmez",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Ichki skaner va printerli kassa terminallari (Sunmi, iMin, Urovo, Newland va shunga o'xshashlar): skaner kodlari o'zi qabul qilinadi, ichki chek printeri o'zi topiladi",
        "Yig'ilganda kassa endi yopilmaydi — fonda ishlaydi, bildirishnomalarda «NurMarket ishlayapti»",
        "Mahsulot qidiruvida 📷 tugmasi — kamera bilan shtrix-kod skaneri",
        "Tortiladigan mahsulot, qaytarish, qarz to'lash, prokat, cheklar tarixi va sozlamalar oynalari telefonga moslashtirildi; katalogda sahifada 12 ta mahsulot, tezroq",
        "Egasi dasturi: savdodagi «Batafsil» endi qotib qolmaydi; yangi bo'limlar «SI-maslahatchi», «Voronka» (mijoz manbasi bilan) va «WhatsApp»",
        "Kassadagi naqddan ko'p chiqim taqiqlangan; to'lovdan so'ng kassa darhol yopilsa, mahsulotlar chekka qaytmaydi",
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
