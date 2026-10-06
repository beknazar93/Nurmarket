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
        "Сенсорные моноблоки: касание поля ввода пальцем открывает экранную клавиатуру Windows; в окне «Новый товар» поля цены видны и на небольшом экране",
        "Фото товара из интернета: ИИ-советник находит варианты через Яндекс.Картинки (Ozon, Маркет и другие магазины) — раньше часто писал «вариантов 0»; варианты пронумерованы, «поставь фото 4» — ставит сам",
        "Разговор с ИИ-советником: «открой склад», «перейди в зарплату», «открой товар …» — программа сразу открывает любой раздел меню или товар на складе",
        "Звонок с ИИ-советником подключается за секунды, даже когда сервер отвечает медленно: данные магазина приходят в разговор, как только соберутся",
        "Если часть данных для ИИ не загрузилась, остальное всё равно доходит до советника (раньше пропадало всё)",
        "Исправлено: настройки иногда не сохранялись, если файл был на миг занят; лишние ошибки в журнале программы владельца при сбое связи",
    ];

    public static readonly string[] LatestKy =
    [
        "Сенсордук моноблоктор: киргизүү талаасын манжа менен басканда Windows экрандык клавиатурасы ачылат; «Жаңы товар» терезесинде баа талаалары кичине экранда да көрүнөт",
        "Интернеттен товардын сүрөтү: ИИ-кеңешчи варианттарды Яндекс.Сүрөттөр аркылуу табат (Ozon, Маркет жана башка дүкөндөр) — мурда көп учурда «варианттар 0» деп жазчу; варианттар номерленген, «4-сүрөттү кой» — өзү коёт",
        "ИИ-кеңешчи менен маек: «кампаны ач», «эмгек акыга өт», «… товарын ач» — программа ошол замат каалаган бөлүмдү же кампадагы товарды ачат",
        "ИИ-кеңешчи менен чалуу сервер жай жооп бергенде да бир нече секундда туташат: дүкөндүн маалыматы даяр болгондо маекке келет",
        "ИИ үчүн маалыматтын бир бөлүгү жүктөлбөсө, калганы баары бир кеңешчиге жетет (мурда баары жоголчу)",
        "Оңдолду: файл бир саамга бош эмес болсо жөндөөлөр кээде сакталчу эмес; байланыш үзүлгөндө ээсинин программасынын журналындагы ашыкча каталар",
    ];

    public static readonly string[] LatestEn =
    [
        "Touchscreen all-in-ones: tapping an input field opens the Windows on-screen keyboard; price fields in the “New product” window are visible on small screens too",
        "Product photos from the web: the AI advisor finds options via Yandex Images (Ozon, Market and other shops) — before it often said “0 options”; options are numbered, “set photo 4” sets it by itself",
        "Talking to the AI advisor: “open the warehouse”, “go to salary”, “open product …” — the program opens any menu section or the product in the warehouse right away",
        "A call with the AI advisor connects in seconds even when the server is slow: shop data joins the conversation as soon as it is ready",
        "If part of the data for the AI fails to load, the rest still reaches the advisor (before, everything was lost)",
        "Fixed: settings were sometimes not saved when the file was busy for a moment; extra errors in the owner program log when the connection failed",
    ];

    public static readonly string[] LatestTr =
    [
        "Dokunmatik ekranlı hepsi bir arada bilgisayarlar: giriş alanına parmakla dokunmak Windows ekran klavyesini açar; «Yeni ürün» penceresindeki fiyat alanları küçük ekranda da görünür",
        "İnternetten ürün fotoğrafı: yapay zekâ danışmanı seçenekleri Yandex Görseller üzerinden bulur (Ozon, Market ve diğer mağazalar) — önce sık sık «0 seçenek» diyordu; seçenekler numaralı, «4. fotoğrafı koy» — kendisi koyar",
        "Yapay zekâ danışmanıyla konuşma: «depoyu aç», «maaşa geç», «… ürününü aç» — program istenen menü bölümünü veya depodaki ürünü hemen açar",
        "Yapay zekâ danışmanıyla görüşme sunucu yavaş olsa da saniyeler içinde bağlanır: mağaza verileri hazır olunca görüşmeye gelir",
        "Yapay zekâ için verilerin bir kısmı yüklenmezse geri kalanı yine danışmana ulaşır (önce hepsi kayboluyordu)",
        "Düzeltildi: dosya bir anlığına meşgulse ayarlar bazen kaydedilmiyordu; bağlantı kesilince sahip programı günlüğünde gereksiz hatalar",
    ];

    public static readonly string[] LatestUz =
    [
        "Sensorli monobloklar: kiritish maydoniga barmoq bilan tegilsa Windows ekran klaviaturasi ochiladi; «Yangi mahsulot» oynasida narx maydonlari kichik ekranda ham ko'rinadi",
        "Internetdan mahsulot rasmi: SI maslahatchi variantlarni Yandex Rasmlar orqali topadi (Ozon, Market va boshqa do'konlar) — avval ko'pincha «0 variant» derdi; variantlar raqamlangan, «4-rasmni qo'y» — o'zi qo'yadi",
        "SI maslahatchi bilan suhbat: «omborni och», «ish haqiga o't», «… mahsulotini och» — dastur istalgan menyu bo'limini yoki ombordagi mahsulotni darhol ochadi",
        "SI maslahatchi bilan qo'ng'iroq server sekin bo'lsa ham bir necha soniyada ulanadi: do'kon ma'lumotlari tayyor bo'lishi bilan suhbatga keladi",
        "SI uchun ma'lumotlarning bir qismi yuklanmasa, qolgani baribir maslahatchiga yetadi (avval hammasi yo'qolardi)",
        "Tuzatildi: fayl bir lahzaga band bo'lsa sozlamalar ba'zan saqlanmasdi; aloqa uzilganda ega dasturi jurnalidagi ortiqcha xatolar",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Обновление прямо из программы: «Настройки → Обновления → Проверить обновления» находит новую версию, «Обновить» скачивает и открывает установку",
        "Долги клиентов подробно: раздел «Долги клиентов», карточка в «Сводке» и вкладка «Долги» в «Аналитике», напоминание в WhatsApp",
        "Возврат работает на тарифе «Старт»; функции «Стандарта» можно подключить на «Старте» по отдельности, в «Аккаунте» — сравнение тарифов",
        "«Настройки → Экран → Разделы меню»: ненужные разделы можно скрыть; при закупке дороже продажи — красное предупреждение",
        "Чековый принтер 80 мм на 42 символа (XP-80 и похожие); покупатель с QR из приложения NurCRM находится сам",
        "ИИ-советник: новый вид, история разговоров, «Стоп», поиск в интернете; меню программы владельца открывает раздел с первого нажатия",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Программадан эле жаңыртуу: «Жөндөөлөр → Жаңыртуулар → Жаңыртууларды текшерүү» жаңы версияны табат, «Жаңыртуу» жүктөп, орнотууну ачат",
        "Кардарлардын карыздары толук: «Кардарлардын карыздары» бөлүмү, «Жыйынтыктагы» карточка жана «Талдоодогу» «Карыздар» өтмөгү, WhatsApp'та эскертүү",
        "Кайтаруу «Старт» тарифинде иштейт; «Стандарттын» функцияларын «Стартта» өзүнчө кошууга болот, «Аккаунтта» — тарифтерди салыштыруу",
        "«Жөндөөлөр → Экран → Меню бөлүмдөрү»: керексиз бөлүмдөрдү жашырса болот; сатып алуу баасы сатуудан кымбат болсо — кызыл эскертүү",
        "80 мм, 42 белгилүү чек принтери (XP-80 ж.б.); NurCRM тиркемесиндеги QR менен кардар өзү табылат",
        "ИИ-кеңешчи: жаңы көрүнүш, маектердин тарыхы, «Токтотуу», интернеттен издөө; ээсинин программасынын менюсу бөлүмдү биринчи басуудан ачат",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Update right from the app: “Settings → Updates → Check for updates” finds a new version, “Update” downloads it and opens the installer",
        "Customer debts in detail: the “Customer debts” section, a card in “Overview” and a “Debts” tab in “Analytics”, WhatsApp reminders",
        "Returns work on the “Start” plan; “Standard” features can be added to “Start” one by one, “Account” compares the plans",
        "“Settings → Screen → Menu sections”: hide sections you don't need; a red warning when the purchase price is above the selling price",
        "80 mm receipt printers with 42 characters (XP-80 and similar); a customer with a QR from the NurCRM app is found automatically",
        "AI advisor: new look, chat history, “Stop”, web search; the owner app menu opens a section on the first tap",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Programdan doğrudan güncelleme: «Ayarlar → Güncellemeler → Güncellemeleri kontrol et» yeni sürümü bulur, «Güncelle» indirir ve yükleyiciyi açar",
        "Müşteri borçları ayrıntılı: «Müşteri borçları» bölümü, «Özet»te kart ve «Analiz»de «Borçlar» sekmesi, WhatsApp hatırlatması",
        "İade «Start» tarifesinde çalışır; «Standart» özellikleri «Start»a tek tek eklenebilir, «Hesap»ta tarife karşılaştırması",
        "«Ayarlar → Ekran → Menü bölümleri»: gereksiz bölümler gizlenebilir; alış fiyatı satıştan yüksekse kırmızı uyarı",
        "80 mm, 42 karakterlik fiş yazıcıları (XP-80 ve benzerleri); NurCRM uygulamasındaki QR ile müşteri kendiliğinden bulunur",
        "Yapay zekâ danışmanı: yeni görünüm, sohbet geçmişi, «Durdur», internet araması; sahip programının menüsü bölümü ilk dokunuşta açar",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Dasturning o'zidan yangilash: «Sozlamalar → Yangilanishlar → Yangilanishlarni tekshirish» yangi versiyani topadi, «Yangilash» yuklab olib, o'rnatishni ochadi",
        "Mijozlar qarzlari batafsil: «Mijozlar qarzlari» bo'limi, «Umumiy ko'rinish»da kartochka va «Analitika»da «Qarzlar» yorlig'i, WhatsApp'da eslatish",
        "Qaytarish «Start» tarifida ishlaydi; «Standart» funksiyalarini «Start»ga alohida ulash mumkin, «Akkaunt»da tariflarni solishtirish",
        "«Sozlamalar → Ekran → Menyu bo'limlari»: keraksiz bo'limlarni yashirish mumkin; xarid narxi sotuvdan qimmat bo'lsa — qizil ogohlantirish",
        "80 mm, 42 belgili chek printerlari (XP-80 va shunga o'xshash); NurCRM ilovasidagi QR bilan mijoz o'zi topiladi",
        "SI-maslahatchi: yangi ko'rinish, suhbatlar tarixi, «To'xtatish», internetda qidirish; egasi dasturi menyusi bo'limni birinchi bosishda ochadi",
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
