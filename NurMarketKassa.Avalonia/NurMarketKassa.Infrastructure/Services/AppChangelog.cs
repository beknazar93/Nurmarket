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
        "Шесть видов кассы на выбор (Настройки → Экран → «Вид кассы»): Классика, Табличная, Карточки, Минимал, Профи и 1С",
        "Редактор своих тем (Маркетплейс → Темы → «Редактор тем»): цвета, скругление, шрифт, живой предпросмотр",
        "Выгрузки в Excel и Word — оформленные отчёты с итогами, диаграммами и печатью на A4",
        "Ценники и этикетки через драйвер принтера печатаются в своём размере, штрих-код не обрезается",
        "Квадратные экраны: масштаб больше не прыгает до 160%; крестик закрывает кассу без выхода из учётной записи",
        "История чеков для кассира и «Печать копии» с пометкой «ПОВТОРНАЯ ПЕЧАТЬ» и датой",
        "После обновления касса больше не предлагает обновиться повторно и показывает это окно «Что нового»",
        "Голос «удалить последний» убирает последний добавленный товар; оплата долга показывает «Оплачено N сом»",
    ];

    public static readonly string[] LatestKy =
    [
        "Кассанын алты көрүнүшү (Жөндөөлөр → Экран → «Кассанын көрүнүшү»): Классика, Таблица, Карточкалар, Минимал, Профи жана 1С",
        "Өз темаларыңыздын редактору (Маркетплейс → Темалар → «Темалардын редактору»): түстөр, бурчтар, шрифт, түз алдын ала көрүү",
        "Excel жана Word'го жүктөө — жыйынтыктары, диаграммалары бар, A4'кө басылуучу жасалгаланган отчёттор",
        "Баа белгилери жана этикеткалар принтердин драйвери аркылуу өз өлчөмүндө басылат, штрих-код кесилбейт",
        "Чарчы экрандар: масштаб 160%га секирбейт; айкаш белги кассаны каттоо эсебинен чыкпай жабат",
        "Кассир үчүн чектердин тарыхы жана «ПОВТОРНАЯ ПЕЧАТЬ» белгиси, күнү менен «Көчүрмөсүн басып чыгаруу»",
        "Жаңыртуудан кийин касса кайра жаңыртууну сунуштабайт жана ушул «Эмне жаңы» терезесин көрсөтөт",
        "«Акыркысын өчүр» үн буйругу акыркы кошулган товарды алып салат; карызды төлөө «N сом төлөндү» деп көрсөтөт",
    ];

    public static readonly string[] LatestEn =
    [
        "Six till layouts (Settings → Screen → “Till layout”): Classic, Table, Cards, Minimal, Pro and 1C",
        "Your own theme editor (Marketplace → Themes → “Theme editor”): colors, corner radius, font, live preview",
        "Excel and Word exports are formatted reports with totals, charts and A4 printing",
        "Price tags and labels printed through a printer driver keep their real size, the barcode is no longer cut off",
        "Square screens: the scale no longer jumps to 160%; the close button closes the till without signing out",
        "Receipt history for cashiers and “Print copy” marked “REPRINT” with the date",
        "After an update the till no longer offers the same update again and shows this “What's new” window",
        "Voice “remove last” removes the most recently added item; debt payment shows “Paid N som”",
    ];

    public static readonly string[] LatestTr =
    [
        "Altı kasa görünümü (Ayarlar → Ekran → «Kasa görünümü»): Klasik, Tablo, Kartlar, Minimal, Pro ve 1C",
        "Kendi tema düzenleyiciniz (Pazaryeri → Temalar → «Tema düzenleyici»): renkler, köşe yuvarlaklığı, yazı tipi, canlı önizleme",
        "Excel ve Word dışa aktarımları toplamlar, grafikler ve A4 yazdırma ile biçimli raporlar",
        "Yazıcı sürücüsüyle yazdırılan etiketler gerçek boyutunda çıkar, barkod kesilmez",
        "Kare ekranlar: ölçek artık %160'a sıçramıyor; kapatma düğmesi oturumu kapatmadan kasayı kapatır",
        "Kasiyer için fiş geçmişi ve «ПОВТОРНАЯ ПЕЧАТЬ» işareti ve tarihle «Kopya yazdır»",
        "Güncellemeden sonra kasa aynı güncellemeyi tekrar önermiyor ve bu «Yenilikler» penceresini gösteriyor",
        "«Sonuncuyu sil» sesli komutu son eklenen ürünü kaldırır; borç ödemesi «N som ödendi» gösterir",
    ];

    public static readonly string[] LatestUz =
    [
        "Kassaning oltita ko'rinishi (Sozlamalar → Ekran → «Kassa ko'rinishi»): Klassika, Jadval, Kartochkalar, Minimal, Pro va 1C",
        "O'z mavzularingiz muharriri (Marketpleys → Mavzular → «Mavzular muharriri»): ranglar, burchaklar, shrift, jonli oldindan ko'rish",
        "Excel va Word'ga eksport — jamlar, diagrammalar va A4 chop etish bilan bezatilgan hisobotlar",
        "Printer drayveri orqali chop etilgan narx yorliqlari o'z o'lchamida chiqadi, shtrix-kod kesilmaydi",
        "Kvadrat ekranlar: masshtab endi 160% gacha sakramaydi; yopish tugmasi hisobdan chiqmasdan kassani yopadi",
        "Kassir uchun cheklar tarixi va «ПОВТОРНАЯ ПЕЧАТЬ» belgisi hamda sana bilan «Nusxani chop etish»",
        "Yangilangandan keyin kassa o'sha yangilanishni qayta taklif qilmaydi va ushbu «Yangiliklar» oynasini ko'rsatadi",
        "«Oxirgisini o'chir» ovozli buyrug'i oxirgi qo'shilgan mahsulotni olib tashlaydi; qarz to'lovi «N so'm to'landi» deb ko'rsatadi",
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
