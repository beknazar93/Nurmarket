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
        "Новое окно «Печать штрихкодов и ценников»: массовая печать, листы A4, этикетка подбирается автоматически",
        "Редактор этикеток: шаблон магазина больше не затирается шаблоном по умолчанию; платные функции проверяются единой проверкой покупки",
        "Сводка и аналитика: столбцы и доли диаграмм разных цветов",
        "Темы сайта для магазина одежды (10 тем); проверка читаемости своих тем; испорченный файл тем восстанавливается",
        "Чек как в мобилке: экранный вид чека и PDF «бумага»",
        "Ключи Gemini из нескольких аккаунтов: окно «Ключи Gemini», автоматическая смена ключа",
        "Поля ключей и паролей не отдают значение через UI Automation, пока оно скрыто точками",
        "Печать чеков с телефона на кассе (по умолчанию выключена)",
        "Второй монитор: «внесено» и «сдача» при включённой галочке, сторож экрана покупателя возвращает пропавшее окно",
        "Удалённая помощь: пока кассир работает мышью или клавиатурой, ввод оператора уступает кассиру; номер техподдержки +996 221 068 796",
        "Склад: убрана лишняя подсказка сканера, фильтр «Наличие» больше не дублирует плитки, приёмка в узком окне",
        "Маркетплейс: ряды карточек листаются мышью и пальцем; «Редактор сайта»: публикация только по команде и после подтверждения, нужно право «Настройки»",
        "Исправлено: утечка памяти голосового управления (буфер звука не больше 30 с); принтер этикеток не запоминает виртуальные (PDF/XPS)",
    ];

    public static readonly string[] LatestKy =
    [
        "Жаңы «Штрихкод жана баа белгилерин басуу» терезеси: жапырт басуу, A4 барактары, этикетка автоматтык тандалат",
        "Этикетка редактору: дүкөндүн шаблону демейки шаблон менен жазылып калбайт; акы төлөнүүчү функциялар сатып алууну бирдиктүү текшерүү менен текшерилет",
        "Жыйынтык жана аналитика: диаграммалардын мамычалары жана үлүштөрү ар түрдүү түстө",
        "Кийим дүкөнү үчүн сайт темалары (10 тема); өз темаларыңыздын окулуучулугун текшерүү; бузулган темалар файлы калыбына келет",
        "Чек мобилкадагыдай: экрандагы чек көрүнүшү жана PDF «кагаз»",
        "Бир нече аккаунттун Gemini ачкычтары: «Gemini ачкычтары» терезеси, ачкычты автоматтык алмаштыруу",
        "Ачкыч жана сырсөз талаалары мааниси чекиттер менен жашырылганда UI Automation аркылуу бербейт",
        "Кассада телефондон чектерди басуу (демейки боюнча өчүк)",
        "Экинчи монитор: белгиленген болсо «киргизилди» жана «кайтарым», сатып алуучунун экранынын кароолчусу жоголгон терезени кайтарат",
        "Алыстан жардам: кассир чычкан же баскычтоп менен иштегенде оператордун киргизүүсү кассирге жол берет; колдоонун номери +996 221 068 796",
        "Кампа: ашыкча сканер кеңеши алынды, «Бар болушу» чыпкасы плиткаларды кайталабайт, тар терезеде кабыл алуу",
        "Маркетплейс: карточка катарлары чычкан жана манжа менен сүрүлөт; «Сайт редактору»: жарыялоо буйрук менен жана ырастоодон кийин гана, «Жөндөөлөр» укугу керек",
        "Оңдолду: үн менен башкарууда эс тутум агып кетүүсү (үн буфери 30 сек.ден ашпайт); этикетка принтери виртуалдык (PDF/XPS) принтерлерди эстебейт",
    ];

    public static readonly string[] LatestEn =
    [
        "New “Print barcodes and price tags” window: bulk printing, A4 sheets, the label is fitted automatically",
        "Label editor: the store template is no longer overwritten by the default one; paid features are checked by one common purchase check",
        "Summary and analytics: chart bars and shares in different colors",
        "Site themes for a clothing store (10 themes); readability check of your own themes; a damaged themes file is restored",
        "Receipt like in the mobile app: an on-screen receipt view and a PDF “paper” receipt",
        "Gemini keys from several accounts: the “Gemini keys” window, automatic key rotation",
        "Key and password fields do not expose the value through UI Automation while it is hidden by dots",
        "Printing receipts from the phone at the register (off by default)",
        "Second monitor: “paid” and “change” when the checkbox is on, a customer-screen watchdog brings back a lost window",
        "Remote help: while the cashier works with the mouse or keyboard, the operator’s input yields to the cashier; support number +996 221 068 796",
        "Stock: the extra scanner hint is removed, the “Availability” filter no longer duplicates the tiles, receiving in a narrow window",
        "Marketplace: card rows scroll with the mouse and a finger; “Site editor”: publishing only on command and after confirmation, the “Settings” permission is required",
        "Fixed: a memory leak in voice control (the audio buffer is at most 30 s); the label printer does not remember virtual (PDF/XPS) printers",
    ];

    public static readonly string[] LatestTr =
    [
        "Yeni «Barkod ve fiyat etiketi yazdırma» penceresi: toplu yazdırma, A4 sayfaları, etiket otomatik seçilir",
        "Etiket düzenleyici: mağaza şablonu artık varsayılan şablonla ezilmez; ücretli işlevler tek ortak satın alma denetimiyle kontrol edilir",
        "Özet ve analiz: grafiklerin sütunları ve payları farklı renklerde",
        "Giyim mağazası için site temaları (10 tema); kendi temalarınızın okunabilirlik denetimi; bozulan tema dosyası geri yüklenir",
        "Mobil uygulamadaki gibi fiş: ekran fiş görünümü ve PDF «kâğıt»",
        "Birden çok hesabın Gemini anahtarları: «Gemini anahtarları» penceresi, otomatik anahtar değişimi",
        "Anahtar ve parola alanları, değer noktalarla gizliyken UI Automation üzerinden vermez",
        "Kasada telefondan fiş yazdırma (varsayılan olarak kapalı)",
        "İkinci monitör: kutu işaretliyse «ödenen» ve «para üstü», müşteri ekranı bekçisi kaybolan pencereyi geri getirir",
        "Uzaktan yardım: kasiyer fare veya klavye kullanırken operatörün girişi kasiyere yol verir; destek numarası +996 221 068 796",
        "Depo: gereksiz tarayıcı ipucu kaldırıldı, «Stok durumu» filtresi kutucukları tekrarlamaz, dar pencerede mal kabul",
        "Pazaryeri: kart sıraları fare ve parmakla kaydırılır; «Site düzenleyici»: yayınlama yalnızca komutla ve onaydan sonra, «Ayarlar» yetkisi gerekir",
        "Düzeltildi: sesli kontrolde bellek sızıntısı (ses tamponu en çok 30 sn); etiket yazıcısı sanal (PDF/XPS) yazıcıları hatırlamaz",
    ];

    public static readonly string[] LatestUz =
    [
        "Yangi «Shtrix-kod va narx yorliqlarini chop etish» oynasi: ommaviy chop etish, A4 varaqlar, yorliq avtomatik tanlanadi",
        "Yorliq muharriri: do'kon shabloni endi standart shablon bilan o'chirilmaydi; pullik funksiyalar yagona xarid tekshiruvi bilan tekshiriladi",
        "Xulosa va tahlil: diagramma ustunlari va ulushlari turli ranglarda",
        "Kiyim do'koni uchun sayt mavzulari (10 ta); o'z mavzularingiz o'qilishini tekshirish; buzilgan mavzular fayli tiklanadi",
        "Chek mobil ilovadagidek: ekrandagi chek ko'rinishi va PDF «qog'oz»",
        "Bir nechta akkauntning Gemini kalitlari: «Gemini kalitlari» oynasi, kalitni avtomatik almashtirish",
        "Kalit va parol maydonlari qiymat nuqtalar bilan yashirin bo'lsa, UI Automation orqali bermaydi",
        "Kassada telefondan cheklarni chop etish (odatda o'chiq)",
        "Ikkinchi monitor: belgi yoqilgan bo'lsa «kiritildi» va «qaytim», xaridor ekrani qorovuli yo'qolgan oynani qaytaradi",
        "Masofaviy yordam: kassir sichqoncha yoki klaviatura bilan ishlaganda operator kiritishi kassirga yo'l beradi; yordam raqami +996 221 068 796",
        "Ombor: ortiqcha skaner maslahati olib tashlandi, «Mavjudligi» filtri plitkalarni takrorlamaydi, tor oynada qabul",
        "Marketplace: kartalar qatorlari sichqoncha va barmoq bilan suriladi; «Sayt muharriri»: e'lon qilish faqat buyruq bilan va tasdiqlangandan keyin, «Sozlamalar» huquqi kerak",
        "Tuzatildi: ovozli boshqaruvda xotira oqishi (ovoz buferi 30 soniyadan oshmaydi); yorliq printeri virtual (PDF/XPS) printerlarni eslamaydi",
    ];

    // 2026-10-05, владелец: «описание андройд выводи на андройд … не смешивай описание». На Android окно
    // «Касса обновлена» показывало пункты десктопа («на вашем компьютере», «сведения о компьютере») —
    // у Android свой список, тот же, что в выпуске Nurmarket-Android.
    // 2026-10-27: Android 1.17.62 (тестовая) — то же, что на Windows в 1.17.62, без пунктов только для компьютера (печать, второй монитор, удалённая помощь, чеки с телефона, Склад).
    // 2026-10-24: Android 1.17.60 (тестовая) — то же, что на Windows в 1.17.60, без пунктов только для компьютера (воронка, окна и панель задач, горячие клавиши, импорт) и без программы владельца.
    // 2026-10-21: Android 1.17.59 (тестовая) — то же, что на Windows в 1.17.59, без пунктов только для компьютера.
    // 2026-10-11: Android 1.17.58 (после 1.17.53 — всё, что вышло на Windows в 1.17.54–1.17.58).
    // 2026-10-05: Android 1.17.52. Без эмодзи — на Android их шрифт не рисует (снимок владельца: «Кнопка  в поиске»).
    public static readonly string[] AndroidLatest =
    [
        "Сводка и аналитика: столбцы и доли диаграмм разных цветов",
        "Темы сайта для магазина одежды (10 тем); проверка читаемости своих тем",
        "Чек как в мобилке: экранный вид чека",
        "Ключи Gemini из нескольких аккаунтов: окно «Ключи Gemini», автоматическая смена ключа",
        "Маркетплейс: ряды карточек листаются пальцем; «Редактор сайта»: публикация только по команде и после подтверждения",
    ];

    public static readonly string[] AndroidLatestKy =
    [
        "Жыйынтык жана аналитика: диаграммалардын мамычалары жана үлүштөрү ар түрдүү түстө",
        "Кийим дүкөнү үчүн сайт темалары (10 тема); өз темаларыңыздын окулуучулугун текшерүү",
        "Чек мобилкадагыдай: экрандагы чек көрүнүшү",
        "Бир нече аккаунттун Gemini ачкычтары: «Gemini ачкычтары» терезеси, ачкычты автоматтык алмаштыруу",
        "Маркетплейс: карточка катарлары манжа менен сүрүлөт; «Сайт редактору»: жарыялоо буйрук менен жана ырастоодон кийин гана",
    ];

    public static readonly string[] AndroidLatestEn =
    [
        "Summary and analytics: chart bars and shares in different colors",
        "Site themes for a clothing store (10 themes); readability check of your own themes",
        "Receipt like in the mobile app: an on-screen receipt view",
        "Gemini keys from several accounts: the “Gemini keys” window, automatic key rotation",
        "Marketplace: card rows scroll with a finger; “Site editor”: publishing only on command and after confirmation",
    ];

    public static readonly string[] AndroidLatestTr =
    [
        "Özet ve analiz: grafiklerin sütunları ve payları farklı renklerde",
        "Giyim mağazası için site temaları (10 tema); kendi temalarınızın okunabilirlik denetimi",
        "Mobil uygulamadaki gibi fiş: ekran fiş görünümü",
        "Birden çok hesabın Gemini anahtarları: «Gemini anahtarları» penceresi, otomatik anahtar değişimi",
        "Pazaryeri: kart sıraları parmakla kaydırılır; «Site düzenleyici»: yayınlama yalnızca komutla ve onaydan sonra",
    ];

    public static readonly string[] AndroidLatestUz =
    [
        "Xulosa va tahlil: diagramma ustunlari va ulushlari turli ranglarda",
        "Kiyim do'koni uchun sayt mavzulari (10 ta); o'z mavzularingiz o'qilishini tekshirish",
        "Chek mobil ilovadagidek: ekrandagi chek ko'rinishi",
        "Bir nechta akkauntning Gemini kalitlari: «Gemini kalitlari» oynasi, kalitni avtomatik almashtirish",
        "Marketplace: kartalar qatorlari barmoq bilan suriladi; «Sayt muharriri»: e'lon qilish faqat buyruq bilan va tasdiqlangandan keyin",
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
