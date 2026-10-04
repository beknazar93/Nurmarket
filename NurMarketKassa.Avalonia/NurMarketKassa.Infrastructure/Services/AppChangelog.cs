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
        "Маленькие экраны и сенсорные моноблоки (1024×768, 1280×800, 1366×768): касса больше не уменьшается целиком — кнопки крупные, под палец, «Оплатить» всегда видна, лишнее в шапке — в меню «⋯». Настройки → Экран → «Компактный вид»",
        "Оплата быстрее: 3 секунды → 1 секунда, чек печатается в фоне; окно «Платёж принят» закрывается следующим сканом",
        "Меньше интернета и нагрузки: каталог не скачивается заново, если не менялся; одна проверка связи вместо двух; «Сводка» владельца обновляется раз в минуту и только когда открыта",
        "Без интернета: возврат и оплата долга сразу говорят «недоступно» вместо ожидания 55 секунд; вход по паролю — не дольше 8 секунд, есть «Войти автономно»",
        "«Касса: N сом» в шапке учитывает продажи, сделанные без интернета; после возврата интернета касса становится «Онлайн» не позже чем через 20 секунд",
        "Окно смены: «Наличные» одинаковые во всех окнах, предоплата долга — отдельной строкой; в «Прокате за смену» виден возвращённый залог",
        "После перезапуска кассы не нужно снова вводить пароль, пока вход действителен",
    ];

    public static readonly string[] LatestKy =
    [
        "Кичине экрандар жана сенсордук моноблоктор: касса толугу менен кичирейбейт — баскычтар чоң, «Төлөө» дайыма көрүнөт, ашыкчасы шапкадагы «⋯» менюсунда. Жөндөөлөр → Экран → «Компакттуу көрүнүш»",
        "Төлөм тезирээк: 3 секунд → 1 секунд, чек фондо басылат",
        "Интернет жана жүк азыраак: каталог өзгөрбөсө кайра жүктөлбөйт; байланышты бир гана текшерүү; ээсинин «Жыйынтыгы» мүнөтүнө бир жолу жана ачык турганда гана жаңырат",
        "Интернетсиз: кайтаруу жана карыз төлөө 55 секунд күтпөй дароо «жеткиликсиз» дейт; сырсөз менен кирүү 8 секунддан ашпайт, «Автономдуу кирүү» бар",
        "Шапкадагы «Касса: N сом» интернетсиз сатууларды эсептейт; интернет келгенде касса 20 секунддун ичинде «Онлайн» болот",
        "Смена терезеси: «Накталай» бардык терезелерде бирдей, карыздын алдын ала төлөмү өзүнчө сапта; «Сменадагы прокатта» кайтарылган күрөө көрүнөт",
        "Кассаны кайра жүргүзгөндөн кийин кирүү жарактуу болсо сырсөздү кайра киргизүүнүн кереги жок",
    ];

    public static readonly string[] LatestEn =
    [
        "Small screens and touch monoblocks (1024×768, 1280×800, 1366×768): the till no longer shrinks as a whole — buttons are finger-sized, “Pay” is always visible, extras moved to the “⋯” menu. Settings → Screen → “Compact view”",
        "Faster payment: 3 seconds → 1 second, the receipt prints in the background",
        "Less internet and load: the catalog is not downloaded again if unchanged; one connection check instead of two; the owner's Summary refreshes once a minute and only while open",
        "Offline: returns and debt payments say “unavailable” at once instead of waiting 55 seconds; password login takes at most 8 seconds, with “Sign in offline”",
        "“Till: N som” in the header includes sales made offline; when the internet returns the till is Online within 20 seconds",
        "Shift window: “Cash” is the same in all windows, debt prepayment on its own line; returned rental deposits are shown",
        "After restarting the till there is no need to enter the password again while the login is valid",
    ];

    public static readonly string[] LatestTr =
    [
        "Küçük ekranlar ve dokunmatik monobloklar: kasa artık bütünüyle küçülmüyor — düğmeler parmak boyutunda, «Öde» her zaman görünür, fazlası «⋯» menüsünde. Ayarlar → Ekran → «Kompakt görünüm»",
        "Daha hızlı ödeme: 3 saniye → 1 saniye, fiş arka planda yazdırılır",
        "Daha az internet ve yük: katalog değişmediyse yeniden indirilmez; iki yerine tek bağlantı kontrolü; işletme sahibi Özeti dakikada bir ve yalnızca açıkken yenilenir",
        "İnternetsiz: iade ve borç ödemesi 55 saniye beklemeden hemen «kullanılamıyor» der; şifreyle giriş en fazla 8 saniye, «Çevrimdışı giriş» var",
        "Başlıktaki «Kasa: N som» çevrimdışı satışları da sayar; internet dönünce kasa 20 saniye içinde Çevrimiçi olur",
        "Vardiya penceresi: «Nakit» tüm pencerelerde aynı, borç ön ödemesi ayrı satırda; iade edilen kira depozitosu görünür",
        "Kasa yeniden başlatıldığında giriş geçerliyse şifreyi tekrar girmek gerekmez",
    ];

    public static readonly string[] LatestUz =
    [
        "Kichik ekranlar va sensorli monobloklar: kassa endi butunlay kichraymaydi — tugmalar barmoq uchun katta, «To'lash» doim ko'rinadi, ortiqchasi «⋯» menyusida. Sozlamalar → Ekran → «Ixcham ko'rinish»",
        "To'lov tezroq: 3 soniya → 1 soniya, chek fonda chop etiladi",
        "Kamroq internet va yuk: katalog o'zgarmasa qayta yuklanmaydi; ikki o'rniga bitta aloqa tekshiruvi; ega «Xulosasi» daqiqada bir marta va faqat ochiq bo'lganda yangilanadi",
        "Internetsiz: qaytarish va qarz to'lovi 55 soniya kutmasdan darhol «mavjud emas» deydi; parol bilan kirish 8 soniyadan oshmaydi, «Oflayn kirish» bor",
        "Sarlavhadagi «Kassa: N so'm» internetsiz sotuvlarni ham hisoblaydi; internet qaytganda kassa 20 soniya ichida Onlayn bo'ladi",
        "Smena oynasi: «Naqd» barcha oynalarda bir xil, qarzning oldindan to'lovi alohida qatorda; qaytarilgan prokat garovi ko'rinadi",
        "Kassa qayta ishga tushirilganda kirish amal qilsa parolni qayta kiritish shart emas",
    ];

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
