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
        "Чековый принтер: в «Ширина ленты» есть вариант «80 мм — 42 симв. (XP-80 и похожие)» — линии и суммы больше не переносятся на следующую строку",
        "Возврат чека работает и на тарифе «Старт»",
        "Тариф «Старт»: функции «Стандарта» (ИИ, Telegram-бот, клиенты и воронка, аналитика, пополнение, зарплата, долги, поддержка) подключаются по отдельности в Маркетплейс → Доп. функции; во вкладке «Аккаунт» — сравнение «Старт» и «Стандарт»",
        "ИИ ищет в интернете бесплатно — с ключом Groq; запасные модели OpenRouter отвечают, когда у Gemini кончился лимит. Ключи — в Настройки → Операции рядом с ключом Gemini или в ИИ-советнике",
        "ИИ-советник: кнопка «■ Стоп», «Новый разговор» сохраняет прежний, прошлые разговоры — в «Истории»; голосом отвечает быстрее; находит фото товара по названию и в интернете",
        "Касса и программа владельца на одном компьютере сами берут друг у друга ключи ИИ и Telegram-бота",
        "QR покупателя из приложения NurCRM: касса находит клиента через сервер, нового заводит сама",
        "«Заказы с сайта»: заказы витрины и Telegram-бота с сервера NurCRM, статусы «Принят → Готов → Выдан / Отменён»",
    ];

    public static readonly string[] LatestKy =
    [
        "Чек принтери: «Тасманын туурасы» тизмесинде «80 мм — 42 белги (XP-80 жана окшоштору)» варианты бар — сызыктар жана суммалар кийинки сапка өтпөйт",
        "Чекти кайтаруу «Старт» тарифинде да иштейт",
        "«Старт» тарифи: «Стандарттын» функциялары (ИИ, Telegram-бот, кардарлар жана воронка, аналитика, толуктоо, эмгек акы, карыздар, колдоо) Маркетплейс → Кошумча функциялар бөлүмүндө өзүнчө туташтырылат; «Аккаунт» өтмөгүндө «Старт» менен «Стандарттын» салыштыруусу",
        "ИИ интернеттен акысыз издейт — Groq ачкычы менен; Gemini'нин лимити бүткөндө OpenRouter'дин запастагы моделдери жооп берет. Ачкычтар — Жөндөөлөр → Операциялар бөлүмүндө Gemini ачкычынын жанында же ИИ-кеңешчиде",
        "ИИ-кеңешчи: «■ Токтотуу» баскычы, «Жаңы маек» мурункусун сактайт, мурунку маектер — «Тарыхта»; үн менен тезирээк жооп берет; товардын сүрөтүн аталышы боюнча жана интернеттен табат",
        "Бир компьютердеги касса жана ээсинин программасы ИИ жана Telegram-бот ачкычтарын бири-биринен өзү алат",
        "NurCRM тиркемесиндеги сатып алуучунун QR коду: касса кардарды сервер аркылуу табат, жаңысын өзү кошот",
        "«Сайттан заказдар»: витринанын жана Telegram-боттун заказдары NurCRM серверинен, статустар «Кабыл алынды → Даяр → Берилди / Жокко чыгарылды»",
    ];

    public static readonly string[] LatestEn =
    [
        "Receipt printer: “Tape width” has the option “80 mm — 42 chars (XP-80 and similar)” — lines and totals no longer wrap to the next line",
        "Receipt returns now work on the “Start” plan too",
        "“Start” plan: “Standard” features (AI, Telegram bot, customers and funnel, analytics, restock, salaries, debts, support) can be connected one by one in Marketplace → Extras; the “Account” tab compares “Start” and “Standard”",
        "AI searches the web for free with a Groq key; OpenRouter backup models answer when Gemini's limit runs out. Keys go in Settings → Operations next to the Gemini key or in the AI advisor",
        "AI advisor: a “■ Stop” button, “New chat” keeps the previous one, past chats are in “History”; voice answers come faster; finds product photos by name and on the web",
        "The till and the owner program on one computer pick up each other's AI and Telegram bot keys automatically",
        "Customer QR from the NurCRM app: the till finds the customer through the server and adds a new one by itself",
        "“Website orders”: showcase and Telegram bot orders from the NurCRM server, statuses “Accepted → Ready → Handed over / Canceled”",
    ];

    public static readonly string[] LatestTr =
    [
        "Fiş yazıcısı: «Rulo genişliği» listesinde «80 mm — 42 karakter (XP-80 ve benzerleri)» seçeneği var — çizgiler ve tutarlar artık alt satıra kaymıyor",
        "Fiş iadesi «Start» tarifesinde de çalışıyor",
        "«Start» tarifesi: «Standart» özellikleri (yapay zekâ, Telegram botu, müşteriler ve huni, analiz, stok yenileme, maaşlar, borçlar, destek) Marketplace → Ek özellikler bölümünden tek tek bağlanabilir; «Hesap» sekmesinde «Start» ve «Standart» karşılaştırması",
        "Yapay zekâ Groq anahtarıyla internette ücretsiz arar; Gemini'nin limiti bitince OpenRouter yedek modelleri yanıtlar. Anahtarlar Ayarlar → İşlemler'de Gemini anahtarının yanında veya yapay zekâ danışmanında",
        "Yapay zekâ danışmanı: «■ Durdur» düğmesi, «Yeni sohbet» öncekini saklar, geçmiş sohbetler «Geçmiş»te; sesli yanıtlar daha hızlı; ürün fotoğrafını adıyla ve internette bulur",
        "Aynı bilgisayardaki kasa ve sahip programı yapay zekâ ve Telegram botu anahtarlarını birbirinden kendiliğinden alır",
        "NurCRM uygulamasındaki müşteri QR'ı: kasa müşteriyi sunucu üzerinden bulur, yenisini kendisi ekler",
        "«Web sitesi siparişleri»: vitrin ve Telegram botu siparişleri NurCRM sunucusundan, durumlar «Kabul edildi → Hazır → Teslim edildi / İptal edildi»",
    ];

    public static readonly string[] LatestUz =
    [
        "Chek printeri: «Lenta kengligi» ro'yxatida «80 mm — 42 belgi (XP-80 va o'xshashlari)» varianti bor — chiziqlar va summalar endi keyingi qatorga o'tmaydi",
        "Chekni qaytarish «Start» tarifida ham ishlaydi",
        "«Start» tarifi: «Standart» funksiyalari (SI, Telegram-bot, mijozlar va voronka, analitika, to'ldirish, ish haqi, qarzlar, yordam) Marketpleys → Qo'shimcha funksiyalar bo'limida alohida ulanadi; «Akkaunt» yorlig'ida «Start» va «Standart» taqqoslanadi",
        "SI Groq kaliti bilan internetda bepul qidiradi; Gemini limiti tugaganda OpenRouter zaxira modellari javob beradi. Kalitlar — Sozlamalar → Operatsiyalar bo'limida Gemini kaliti yonida yoki SI maslahatchida",
        "SI maslahatchi: «■ To'xtatish» tugmasi, «Yangi suhbat» oldingisini saqlaydi, oldingi suhbatlar «Tarix»da; ovoz bilan tezroq javob beradi; mahsulot rasmini nomi bo'yicha va internetdan topadi",
        "Bir kompyuterdagi kassa va ega dasturi SI va Telegram-bot kalitlarini bir-biridan o'zi oladi",
        "NurCRM ilovasidagi xaridor QR kodi: kassa mijozni server orqali topadi, yangisini o'zi qo'shadi",
        "«Saytdan buyurtmalar»: vitrina va Telegram-bot buyurtmalari NurCRM serveridan, holatlar «Qabul qilindi → Tayyor → Berildi / Bekor qilindi»",
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
